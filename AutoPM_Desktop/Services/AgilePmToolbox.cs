using AutoPM_Desktop.Models;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace AutoPM_Desktop.Services;

/// <summary>A tool the AI assistant can call: its name, JSON schema and the code that runs it.</summary>
public sealed record AssistantTool(
    string Name,
    string Description,
    JsonElement InputSchema,
    bool ReadOnly,
    Func<ToolArguments, CancellationToken, Task<string>> RunAsync);

/// <summary>A bad tool argument; the message goes back to the model as-is so it can fix the call.</summary>
public sealed class ToolInputException(string message) : Exception(message);

/// <summary>
/// The Agile PM tools the AI assistant works with, run in-process on the signed-in <see cref="AgilePmApiService"/>.
/// Names, parameters and output shapes follow the AutoPM MCP server's tools, so the same instructions work for both,
/// but the desktop app doesn't need the server. get_my_work and list_project_tasks go further: they filter, sort by
/// urgency and return totals plus compact rows, so a small model answers task questions in one call.
/// </summary>
public sealed class AgilePmToolbox
{
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 50;
    private const int MaxBulkItems = 50;
    private const int DefaultTaskRows = 25;
    private const int MaxTaskRows = 60;

    private const string StatusFilterDescription =
        "open (default: Planned, InProgress, Returned), overdue, all, or one stage: Planned, InProgress, Returned, Completed, Closed, Cancelled";

    private const string EpicStatusDescription = "Epic status code (0-3, as defined by Agile PM's EpicStatus)";
    private const string EpicPriorityDescription = "Epic priority code (0-3, as defined by Agile PM's EpicPriority)";

    private static readonly JsonSerializerOptions OutputOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly AgilePmApiService _api;
    private readonly Dictionary<string, AssistantTool> _toolsByName;

    public AgilePmToolbox(AgilePmApiService api)
    {
        _api = api;
        Tools = CreateTools();
        _toolsByName = Tools.ToDictionary(t => t.Name, StringComparer.Ordinal);
    }

    public event Action<string>? OnLog;

    /// <summary>
    /// The project selected in the app. Tools that work on one project use it when the model leaves projectId out,
    /// so the assistant stays in that project and small models don't have to copy GUIDs around.
    /// </summary>
    public Func<ProjectItem?>? ActiveProject { get; set; }

    /// <summary>User ID → display name for the active project's people; Agile PM's task lists only carry assignee IDs.</summary>
    public Func<IReadOnlyDictionary<string, string>>? UserNames { get; set; }

    public IReadOnlyList<AssistantTool> Tools { get; }

    /// <summary>True when the tool only reads data, so the assistant may run it without asking.</summary>
    public bool IsReadOnly(string toolName) => _toolsByName.TryGetValue(toolName, out var tool) && tool.ReadOnly;

    public bool HasTool(string toolName) => _toolsByName.ContainsKey(toolName);

    public async Task<(string Text, bool IsError)> CallToolAsync(string name, IReadOnlyDictionary<string, object?> arguments, CancellationToken ct = default)
    {
        if (!_toolsByName.TryGetValue(name, out var tool))
        {
            return ($"There is no tool named '{name}'.", true);
        }

        Log($"AI tool -> {name}");
        try
        {
            var args = new ToolArguments(arguments) { DefaultProjectId = ActiveProject?.Invoke()?.GuidId };
            var text = await tool.RunAsync(args, ct);
            Log($"AI tool <- {name}");
            return (string.IsNullOrWhiteSpace(text) ? "(no output)" : text, false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log($"AI tool {name} failed: {ex.Message}");
            return (ex.Message, true);
        }
    }

    private List<AssistantTool> CreateTools() =>
    [
        // ---- Projects & my work ----
        Read("list_my_projects",
            "Lists the Agile PM projects the current user belongs to (as project manager, scrum master or team member). " +
            "Start here to find project IDs for the other tools.",
            new ToolSchema()
                .String("nameContains", "Optional text to look for in the project name (case-insensitive)")
                .Paging("Projects"),
            ListMyProjectsAsync),
        Read("get_project", "Gets the full details of one project, including its team members.",
            new ToolSchema().ProjectId(),
            async (a, ct) => Json(ProjectDetails(await _api.GetProjectByIdAsync(a.ProjectId(), ct)))),
        Read("list_project_team_members",
            "Lists the members of a project with their user IDs and roles. Use it to find user IDs for assigning work.",
            new ToolSchema().ProjectId(),
            ListProjectTeamMembersAsync),
        Read("get_my_work",
            "The current user's own tasks, most urgent first (overdue, then by due date and priority), with stage, hours " +
            "and due date. Covers the active project unless allProjects is true. One call answers 'what am I working on?'.",
            new ToolSchema()
                .Bool("allProjects", "Include every project, not just the active one")
                .String("status", StatusFilterDescription)
                .Integer("limit", $"Most tasks to return (default {DefaultTaskRows}, max {MaxTaskRows})"),
            GetMyWorkAsync),
        Read("get_dashboard_summary", "Gets the current user's Agile PM dashboard summary (task counts and progress).",
            new ToolSchema(),
            async (_, ct) => Json(new { data = await _api.GetDashboardSummaryAsync(ct) })),

        // ---- Epics ----
        Read("list_epics", "Lists the epics in a project.",
            new ToolSchema().ProjectId().Paging("Epics"),
            async (a, ct) => List(await _api.GetAsync(
                PagedPath("TaskManagement/getEpics", a, ("ProjectId", a.ProjectId())), ct), e => EpicCompact(EpicItem.FromJson(e)))),
        Read("get_epic", "Gets one epic by ID.",
            new ToolSchema().Guid("epicId", "Epic ID", required: true),
            async (a, ct) => Single(await _api.GetAsync(Path("TaskManagement/getEpics", ("id", a.RequiredGuid("epicId"))), ct),
                e => EpicCompact(EpicItem.FromJson(e), 1500), "Epic")),
        Write("create_epic", "Creates an epic in a project.",
            new ToolSchema()
                .ProjectId()
                .String("title", "Epic title", required: true)
                .String("description", "Epic description")
                .Number("estimatedEffort", "Estimated effort (hours)")
                .Integer("status", EpicStatusDescription)
                .Integer("priority", EpicPriorityDescription)
                .Date("startDate", "Start date (ISO 8601)")
                .Date("endDate", "End date (ISO 8601)")
                .Guid("phaseId", "Optional phase ID")
                .Guid("milestoneId", "Optional milestone ID"),
            CreateEpicAsync),
        Write("update_epic", "Updates an epic. Only the fields you pass are changed; everything else is kept as it is.",
            new ToolSchema()
                .Guid("epicId", "Epic ID", required: true)
                .ProjectId()
                .String("title", "New title")
                .String("description", "New description")
                .Number("estimatedEffort", "New estimated effort (hours)")
                .Integer("status", EpicStatusDescription)
                .Integer("priority", EpicPriorityDescription)
                .Date("startDate", "New start date (ISO 8601)")
                .Date("endDate", "New end date (ISO 8601)"),
            UpdateEpicAsync),

        // ---- User stories ----
        Read("list_user_stories", "Lists user stories, filtered by project, epic and/or assignee. Pass at least one filter.",
            new ToolSchema()
                .Guid("projectId", "Only stories in this project (defaults to the active project unless epicId is given)")
                .Guid("epicId", "Only stories in this epic")
                .Bool("assignedToMe", "Only stories assigned to the current user")
                .Guid("assignedToUserId", "Only stories assigned to this user ID (ignored when assignedToMe is true)")
                .Paging("Stories"),
            ListUserStoriesAsync),
        Read("get_user_story", "Gets one user story by ID, including its assignments and tasks.",
            new ToolSchema().Guid("userStoryId", "User story ID", required: true),
            async (a, ct) => Single(await _api.GetAsync(Path("TaskManagement/getUserStorys", ("id", a.RequiredGuid("userStoryId"))), ct),
                e => StoryCompact(UserStoryItem.FromJson(e), 2000), "User story")),
        Write("create_user_story", "Creates a user story under an epic.",
            new ToolSchema()
                .Guid("epicId", "Epic ID the story belongs to", required: true)
                .String("story", "The story, e.g. 'As a <role> I want <goal> so that <benefit>'", required: true)
                .String("description", "Story description")
                .String("acceptanceCriteria", "Acceptance criteria")
                .Enum<PriorityEnum>("priority", "Priority (default Medium)")
                .Enum<UserStoryStagesEnum>("status", "Initial stage (default Planned)")
                .GuidArray("assignedTo", "User IDs to assign the story to")
                .Guid("workItemCategoryId", "Optional work item category ID"),
            CreateUserStoryAsync),
        Write("update_user_story",
            "Updates a user story's content. Only the fields you pass are changed. " +
            "Use move_user_story_status to change its stage and reassign_user_story to hand it to someone else.",
            new ToolSchema()
                .Guid("userStoryId", "User story ID", required: true)
                .String("story", "New story text")
                .String("description", "New description")
                .String("acceptanceCriteria", "New acceptance criteria")
                .Enum<PriorityEnum>("priority", "New priority")
                .Guid("epicId", "Move the story to this epic")
                .GuidArray("assignedTo", "Replace the assignees with these user IDs"),
            UpdateUserStoryAsync),
        Write("move_user_story_status",
            "Deprecated: a user story's stage follows its tasks, so move the tasks with move_task_status instead. " +
            "Kept for compatibility; Agile PM may reject or ignore the move.",
            new ToolSchema()
                .Guid("userStoryId", "User story ID", required: true)
                .Enum<UserStoryStagesEnum>("status", "Target stage", required: true),
            async (a, ct) =>
            {
                var status = a.RequiredEnum<UserStoryStagesEnum>("status");
                return Success($"User story moved to {status}.", await _api.MoveUserStoryStatusAsync(a.RequiredGuid("userStoryId"), status, ct));
            }),
        Write("reassign_user_story", "Reassigns a user story from one user to another.",
            new ToolSchema()
                .Guid("userStoryId", "User story ID", required: true)
                .Guid("fromUserId", "User ID of the current assignee", required: true)
                .Guid("toUserId", "User ID of the new assignee", required: true),
            async (a, ct) => Success("User story reassigned.", await _api.ReassignUserStoryAsync(
                a.RequiredGuid("userStoryId"), a.RequiredGuid("fromUserId"), a.RequiredGuid("toUserId"), ct))),

        // ---- Project tasks ----
        Read("list_project_tasks",
            "Searches a project's tasks (the active project by default) with filters, most urgent first, and returns the " +
            "total that match plus counts by stage. Filter instead of paging through the project.",
            new ToolSchema()
                .Guid("projectId", "Project to search (defaults to the active project)")
                .Guid("userStoryId", "Only tasks under this user story")
                .Bool("assignedToMe", "Only the current user's tasks")
                .Guid("assigneeId", "Only tasks assigned to this user")
                .String("status", StatusFilterDescription)
                .String("search", "Text to look for in the title")
                .Integer("limit", $"Most tasks to return (default {DefaultTaskRows}, max {MaxTaskRows})")
                .Integer("page", "Page of results (default 1)"),
            ListProjectTasksAsync),
        Read("get_project_task", "Gets one task by ID, including its sub-tasks and comments.",
            new ToolSchema().Guid("taskId", "Task ID", required: true),
            async (a, ct) => Single(await _api.GetAsync(Path("TaskManagement/getProjectTasks", ("id", a.RequiredGuid("taskId"))), ct),
                e => TaskDetails(TaskItem.FromJson(e)), "Task")),
        Write("create_project_task", "Creates a task in a project, optionally under a user story or epic.",
            new ToolSchema()
                .ProjectId()
                .String("title", "Task title", required: true)
                .String("description", "Task description")
                .Guid("userStoryId", "User story the task belongs to")
                .Guid("epicId", "Epic the task belongs to")
                .Guid("assignTo", "User ID to assign the task to")
                .Number("plannedHours", "Planned effort in hours")
                .Date("endDate", "Due date (ISO 8601)")
                .Enum<PriorityEnum>("priority", "Priority (default Medium)")
                .Enum<PriorityEnum>("complexity", "Complexity (default Medium)")
                .Enum<GeneralStagesEnum>("status", "Initial stage (default Planned)")
                .Guid("skillId", "Optional skill ID")
                .Guid("workBucketId", "Optional work bucket ID"),
            CreateProjectTaskAsync),
        Write("update_project_task",
            "Updates a task. Only the fields you pass are changed. " +
            "Use move_task_status to change its stage (it also records hours worked) and reassign_project_task to hand it over.",
            new ToolSchema()
                .Guid("taskId", "Task ID", required: true)
                .String("title", "New title")
                .String("description", "New description")
                .Number("plannedHours", "New planned effort in hours")
                .Date("endDate", "New due date (ISO 8601)")
                .Enum<PriorityEnum>("priority", "New priority")
                .Enum<PriorityEnum>("complexity", "New complexity")
                .Guid("userStoryId", "Move the task under this user story")
                .Guid("epicId", "Move the task under this epic"),
            async (a, ct) => Success("Task updated.", await _api.UpdateProjectTaskAsync(
                a.RequiredGuid("taskId"), a.String("title"), a.String("description"), a.Number("plannedHours"), a.Date("endDate"),
                a.Enum<PriorityEnum>("priority"), a.Enum<PriorityEnum>("complexity"), a.Guid("userStoryId"), a.Guid("epicId"), ct))),
        Write("move_task_status",
            "Moves a task (or one of its sub-tasks) to another stage and records the hours worked. " +
            "Agile PM does not allow Planned -> Completed directly, so a Planned item moved to Completed is stepped through InProgress automatically.",
            new ToolSchema()
                .Guid("taskId", "Task ID", required: true)
                .Enum<GeneralStagesEnum>("status", "Target stage", required: true)
                .Number("manHours", "Hours worked to log with this move")
                .Guid("subTaskId", "Move this sub-task of the task instead of the task itself"),
            async (a, ct) =>
            {
                var status = a.RequiredEnum<GeneralStagesEnum>("status");
                var subTaskId = a.Guid("subTaskId");
                var steps = await MoveAsync(a.RequiredGuid("taskId"), subTaskId, status, a.Number("manHours") ?? 0, ct);
                return Json(new
                {
                    succeeded = true,
                    summary = $"{(subTaskId is null ? "Task" : "Sub-task")} moved to {status}.",
                    stages = steps.Select(s => s.ToString()),
                });
            }),
        Write("reassign_project_task", "Reassigns a task to another user.",
            new ToolSchema()
                .Guid("taskId", "Task ID", required: true)
                .Guid("assignToUserId", "User ID of the new assignee", required: true),
            async (a, ct) => Success("Task reassigned.", await _api.ReassignProjectTaskAsync(a.RequiredGuid("taskId"), a.RequiredGuid("assignToUserId"), ct))),
        Write("pause_task_work", "Pauses the work timer on a task the current user is working on.",
            new ToolSchema().Guid("taskId", "Task ID", required: true),
            async (a, ct) => Success("Work on the task paused.", await _api.PauseTaskWorkAsync(a.RequiredGuid("taskId"), ct))),
        Write("resume_task_work", "Resumes the work timer on a paused task.",
            new ToolSchema().Guid("taskId", "Task ID", required: true),
            async (a, ct) => Success("Work on the task resumed.", await _api.ResumeTaskWorkAsync(a.RequiredGuid("taskId"), ct))),

        // ---- Sub-tasks & comments ----
        Read("list_sub_tasks", "Lists the sub-tasks of a task.",
            new ToolSchema().Guid("taskId", "Parent task ID", required: true).Paging("Sub-tasks"),
            async (a, ct) => List(await _api.GetAsync(
                PagedPath("TaskManagement/getSubTasks", a, ("ParentTaskId", a.RequiredGuid("taskId"))), ct), e => SubTaskCompact(SubTaskItem.FromJson(e)))),
        Write("create_sub_task", "Creates a sub-task under a task.",
            new ToolSchema()
                .Guid("taskId", "Parent task ID", required: true)
                .String("title", "Sub-task title", required: true)
                .String("description", "Sub-task description")
                .Guid("assignTo", "User ID to assign the sub-task to")
                .Number("plannedHours", "Planned effort in hours")
                .Date("endDate", "Due date (ISO 8601)")
                .Enum<PriorityEnum>("priority", "Priority (default Medium)")
                .Enum<PriorityEnum>("complexity", "Complexity (default Medium)")
                .Enum<GeneralStagesEnum>("status", "Initial stage (default Planned)")
                .Bool("isBlocker", "Whether this sub-task blocks its parent task")
                .Guid("skillId", "Optional skill ID"),
            CreateSubTaskAsync),
        Read("list_comments", "Lists the comments on a task, sub-task or user story. Pass exactly one of the IDs.",
            new ToolSchema()
                .Guid("taskId", "Task ID")
                .Guid("subTaskId", "Sub-task ID")
                .Guid("userStoryId", "User story ID")
                .Paging("Comments"),
            async (a, ct) =>
            {
                var (taskId, subTaskId, userStoryId) = SingleCommentTarget(a);
                return List(await _api.GetAsync(PagedPath("TaskManagement/getComments", a,
                    ("ProjectTaskId", taskId), ("SubTaskId", subTaskId), ("UserStoryId", userStoryId)), ct), e => CommentCompact(CommentItem.FromJson(e)));
            }),
        Write("add_comment", "Adds a comment to a task, sub-task or user story. Pass exactly one of the IDs.",
            new ToolSchema()
                .String("remark", "Comment text", required: true)
                .Guid("taskId", "Task ID")
                .Guid("subTaskId", "Sub-task ID")
                .Guid("userStoryId", "User story ID")
                .GuidArray("taggedUserIds", "User IDs to tag (notify) in the comment"),
            async (a, ct) =>
            {
                var (taskId, subTaskId, userStoryId) = SingleCommentTarget(a);
                var request = new CreateCommentRequest(
                    a.RequiredString("remark"), taskId?.ToString(), subTaskId?.ToString(), userStoryId?.ToString(),
                    a.GuidArray("taggedUserIds")?.Select(id => id.ToString()).ToList());
                return Success("Comment added.", await _api.AddCommentAsync(request, ct));
            }),

        // ---- Automation: bulk changes and reports ----
        Write("bulk_create_project_tasks",
            "Creates up to 50 tasks in one project in a single call, optionally all under one user story or epic. " +
            "Each task is created independently: the result lists which ones succeeded, failed or were skipped as duplicates.",
            new ToolSchema()
                .ProjectId()
                .Array("tasks", "The tasks to create", new ToolSchema()
                    .String("title", "Task title", required: true)
                    .String("description", "Task description")
                    .Number("plannedHours", "Planned effort in hours")
                    .Enum<PriorityEnum>("priority", "Priority, also used as complexity (default Medium)")
                    .Date("endDate", "Due date (ISO 8601); defaults to defaultDueInDays from today")
                    .Guid("assignTo", "User ID to assign this task to; defaults to defaultAssignTo"), required: true)
                .Guid("userStoryId", "User story every task belongs to")
                .Guid("epicId", "Epic every task belongs to")
                .Guid("defaultAssignTo", "Assignee for tasks that don't name their own")
                .Integer("defaultDueInDays", "Due date for tasks that don't set one, in days from today (default 7)")
                .Bool("skipExistingTitles", "Skip tasks whose title already exists in the project, case-insensitive (default true)"),
            BulkCreateProjectTasksAsync),
        Write("bulk_move_task_status",
            "Moves up to 50 tasks to the same stage in one call, logging the same hours on each. " +
            "Planned tasks moved to Completed are stepped through InProgress automatically.",
            new ToolSchema()
                .GuidArray("taskIds", "Task IDs to move", required: true)
                .Enum<GeneralStagesEnum>("status", "Target stage", required: true)
                .Number("manHoursEach", "Hours worked to log on each task"),
            BulkMoveTaskStatusAsync),
        Read("get_standup_report",
            "Builds the current user's daily standup report from their assigned work: in progress, done, next up, " +
            "returned and overdue tasks, with hour totals and a ready-to-paste Markdown version.",
            new ToolSchema().Integer("maxNextUp", "How many planned tasks to list under 'next up' (default 8)"),
            GetStandupReportAsync),
        Read("find_overdue_tasks",
            "Finds open tasks that are past their due date or due soon, either across the current user's work " +
            "or across a whole project.",
            new ToolSchema()
                .Guid("projectId", "Check this project's tasks instead of the current user's work")
                .Integer("dueWithinDays", "Also list open tasks due within this many days (default 3)"),
            FindOverdueTasksAsync),
        Read("get_project_progress",
            "Summarizes a project's delivery progress: tasks by stage, completion percentage, planned vs logged hours, " +
            "overdue work and each assignee's open load. Use it for sprint health checks and status updates.",
            new ToolSchema().ProjectId(),
            GetProjectProgressAsync),
    ];

    // ==================== Tool implementations ====================

    private async Task<string> ListMyProjectsAsync(ToolArguments a, CancellationToken ct)
    {
        var projects = (await _api.GetProjectsAsync(onlyMyProjects: true, a.String("nameContains"), ct))
            .Select(p => new
            {
                id = p.Id,
                projectName = p.ProjectName,
                myRole = p.MyRole,
                status = p.StatusText,
                priority = p.PriorityText,
                projectManagerName = p.ProjectManagerName,
                scrumMasterName = p.ScrumMasterName,
                startDate = p.StartDate,
                endDate = p.EndDate,
                totalNumberOfTasks = p.TotalNumberOfTasks,
            })
            .ToList();

        var (page, pageSize) = Paging(a);
        var totalPages = (int)Math.Ceiling(projects.Count / (double)pageSize);
        return Json(new
        {
            items = projects.Skip((page - 1) * pageSize).Take(pageSize),
            paging = new PageInfo(page, pageSize, projects.Count, totalPages, page < totalPages),
        });
    }

    private async Task<string> ListProjectTeamMembersAsync(ToolArguments a, CancellationToken ct)
    {
        var project = await _api.GetProjectByIdAsync(a.ProjectId(), ct);
        return Json(new
        {
            projectId = project.Id,
            projectName = project.ProjectName,
            projectManager = new { userId = project.ProjectManagerId, name = project.ProjectManagerName },
            scrumMaster = new { userId = project.ScrumMasterId, name = project.ScrumMasterName },
            members = project.TeamMembers.Select(m => new
            {
                userId = m.UserId,
                name = m.Name,
                email = m.Email,
                role = m.Role,
                pendingTasks = m.PendingTasks,
            }),
        });
    }

    private async Task<string> CreateEpicAsync(ToolArguments a, CancellationToken ct)
    {
        var title = a.RequiredString("title");
        var request = new CreateEpicRequest(
            a.ProjectId().ToString(), title, a.String("description"), a.Number("estimatedEffort") ?? 0,
            EpicCode(a, "status") ?? 0, EpicCode(a, "priority") ?? 0, a.Date("startDate"), a.Date("endDate"),
            a.Guid("phaseId")?.ToString(), a.Guid("milestoneId")?.ToString());
        return Success($"Epic '{title}' created.", await _api.CreateEpicAsync(request, ct));
    }

    private async Task<string> UpdateEpicAsync(ToolArguments a, CancellationToken ct)
    {
        var response = await _api.UpdateEpicAsync(
            a.RequiredGuid("epicId"), a.ProjectId(), a.String("title"), a.String("description"),
            a.Number("estimatedEffort"), EpicCode(a, "status"), EpicCode(a, "priority"), a.Date("startDate"), a.Date("endDate"), ct);
        return Success("Epic updated.", response);
    }

    private async Task<string> ListUserStoriesAsync(ToolArguments a, CancellationToken ct)
    {
        var userId = a.Bool("assignedToMe") == true
            ? _api.CurrentUser?.UserId ?? throw new ToolInputException("Could not find a user ID in your access token.")
            : a.Guid("assignedToUserId");
        var epicId = a.Guid("epicId");
        var projectId = a.Guid("projectId") ?? (epicId is null ? a.DefaultProjectId : null);
        if (projectId is null && epicId is null && userId is null)
        {
            throw new ToolInputException("Pass projectId, epicId, assignedToMe or assignedToUserId to narrow the list.");
        }

        return List(await _api.GetAsync(PagedPath("TaskManagement/getUserStorys", a,
            ("ProjectId", projectId), ("EpicId", epicId), ("UserId", userId)), ct), e => StoryCompact(UserStoryItem.FromJson(e)));
    }

    private async Task<string> CreateUserStoryAsync(ToolArguments a, CancellationToken ct)
    {
        var request = new CreateUserStoryRequest(
            a.RequiredGuid("epicId").ToString(), a.RequiredString("story"), a.String("description"), a.String("acceptanceCriteria"),
            a.Enum<PriorityEnum>("priority") ?? PriorityEnum.Medium, a.Enum<UserStoryStagesEnum>("status") ?? UserStoryStagesEnum.Planned,
            a.GuidArray("assignedTo")?.Select(id => id.ToString()).ToList(), a.Guid("workItemCategoryId")?.ToString());
        return Success("User story created.", await _api.CreateUserStoryAsync(request, ct));
    }

    private async Task<string> UpdateUserStoryAsync(ToolArguments a, CancellationToken ct)
    {
        var response = await _api.UpdateUserStoryAsync(
            a.RequiredGuid("userStoryId"), a.String("story"), a.String("description"), a.String("acceptanceCriteria"),
            a.Enum<PriorityEnum>("priority"), a.Guid("epicId"), a.GuidArray("assignedTo")?.Select(id => id.ToString()).ToList(), ct);
        return Success("User story updated.", response);
    }

    private async Task<string> GetMyWorkAsync(ToolArguments a, CancellationToken ct)
    {
        var allProjects = a.Bool("allProjects") == true || a.DefaultProjectId is null;
        var status = a.String("status");
        var tasks = await _api.GetMyWorkAsync(includeCompleted: StatusIncludesClosed(status), ct);
        if (!allProjects)
        {
            var projectId = a.DefaultProjectId!.Value.ToString();
            tasks = tasks.Where(t => string.Equals(t.ProjectId, projectId, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        return TaskResults(allProjects ? "my tasks in all projects" : "my tasks in the active project",
            FilterByStatus(tasks, status), a, withProject: allProjects, withAssignee: false);
    }

    /// <summary>
    /// Loads the whole project (the API client caches it for a few seconds) and filters here, so one call answers
    /// "my open tasks", "what's overdue" or "tasks about login" without the model paging through hundreds.
    /// </summary>
    private async Task<string> ListProjectTasksAsync(ToolArguments a, CancellationToken ct)
    {
        var userStoryId = a.Guid("userStoryId");
        var projectId = a.Guid("projectId") ?? a.DefaultProjectId;

        List<TaskItem> tasks;
        if (projectId is { } pid)
        {
            tasks = await _api.ListAllProjectTasksAsync(pid, ct: ct);
            if (userStoryId is { } sid)
            {
                tasks = tasks.Where(t => string.Equals(t.UserStoryId, sid.ToString(), StringComparison.OrdinalIgnoreCase)).ToList();
            }
        }
        else if (userStoryId is { } sid)
        {
            var response = await _api.GetAsync(Path("TaskManagement/getProjectTasks", ("UserStoryId", sid), ("paging.pageNumber", 1), ("paging.pageSize", 100)), ct);
            tasks = response.Items.Where(i => i.ValueKind == JsonValueKind.Object).Select(e => TaskItem.FromJson(e)).ToList();
        }
        else
        {
            throw new ToolInputException("Select an active project in the app, or pass projectId or userStoryId.");
        }

        var assignedToMe = a.Bool("assignedToMe") == true;
        var assigneeId = a.Guid("assigneeId")?.ToString();
        var search = a.String("search")?.Trim();
        var filtered = FilterByStatus(tasks, a.String("status"))
            .Where(t => !assignedToMe || _api.CurrentUser?.IsUser(t.AssignTo) == true)
            .Where(t => assigneeId is null || string.Equals(t.AssignTo, assigneeId, StringComparison.OrdinalIgnoreCase))
            .Where(t => string.IsNullOrEmpty(search) || t.Title.Contains(search, StringComparison.OrdinalIgnoreCase));

        return TaskResults(assignedToMe ? "my tasks" : "project tasks", filtered, a, withProject: false, withAssignee: !assignedToMe,
            projectTotal: tasks.Count);
    }

    private static bool StatusIncludesClosed(string? status) =>
        status?.Trim().ToLowerInvariant() is "all" or "completed" or "closed" or "cancelled" or "done";

    private static IEnumerable<TaskItem> FilterByStatus(IEnumerable<TaskItem> tasks, string? status)
    {
        var key = string.IsNullOrWhiteSpace(status) ? "open" : status.Trim().Replace(" ", "").Replace("_", "").ToLowerInvariant();
        return key switch
        {
            "open" or "active" or "notdone" => tasks.Where(t => t.IsOpen),
            "overdue" or "late" => tasks.Where(t => t.IsOverdue),
            "all" or "any" => tasks,
            "done" => tasks.Where(t => t.Status is GeneralStagesEnum.Completed or GeneralStagesEnum.Closed),
            _ when Enum.TryParse<GeneralStagesEnum>(key, ignoreCase: true, out var stage) => tasks.Where(t => t.Status == stage),
            _ => throw new ToolInputException($"Unknown status '{status}'. Use: {StatusFilterDescription}."),
        };
    }

    /// <summary>Overdue first (longest late first), then the soonest due, then started work, then higher priority.</summary>
    private static IOrderedEnumerable<TaskItem> ByUrgency(IEnumerable<TaskItem> tasks) => tasks
        .OrderByDescending(t => t.IsOverdue)
        .ThenBy(t => t.IsOpen ? t.EndDateRaw ?? DateTime.MaxValue : DateTime.MaxValue)
        .ThenBy(t => t.Status switch { GeneralStagesEnum.Returned => 0, GeneralStagesEnum.InProgress => 1, GeneralStagesEnum.Planned => 2, _ => 3 })
        .ThenByDescending(t => t.Priority);

    /// <summary>
    /// A filtered task list for the model: totals and stage counts (so it needn't fetch more to count) and the most
    /// urgent rows as a table, which costs about half the tokens of one JSON object per task.
    /// </summary>
    private string TaskResults(string scope, IEnumerable<TaskItem> tasks, ToolArguments a, bool withProject, bool withAssignee, int? projectTotal = null)
    {
        var matching = ByUrgency(tasks).ToList();
        var limit = Math.Clamp(a.Integer("limit") ?? DefaultTaskRows, 1, MaxTaskRows);
        var page = Math.Max(1, a.Integer("page") ?? 1);
        var shown = matching.Skip((page - 1) * limit).Take(limit).ToList();
        var more = matching.Count - (page - 1) * limit - shown.Count;

        return Json(new
        {
            scope,
            today = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            projectTotal,
            matching = matching.Count,
            byStage = matching.GroupBy(t => t.Status.ToString()).ToDictionary(g => g.Key, g => g.Count()),
            overdue = matching.Count(t => t.IsOverdue),
            openPlannedHours = Math.Round(matching.Where(t => t.IsOpen).Sum(t => t.PlannedHours), 1),
            sortedBy = "urgency (overdue first, then due date)",
            tasks = TaskTable(shown, withProject, withAssignee),
            more = more > 0 ? $"{more} more match; narrow with status/search/assignee filters, or ask for page {page + 1}" : null,
        });
    }

    private object TaskTable(IReadOnlyList<TaskItem> tasks, bool withProject, bool withAssignee)
    {
        var names = withAssignee ? UserNames?.Invoke() : null;
        var columns = new List<string> { "id", "title", "stage", "priority" };
        if (withAssignee) columns.Add("assignee");
        columns.AddRange(["plannedH", "loggedH", "due", "story"]);
        if (withProject) columns.Add("project");

        var today = DateTime.Today;
        var rows = tasks.Select(t =>
        {
            var row = new List<object?> { t.Id, t.Title, t.Status.ToString(), t.Priority.ToString() };
            if (withAssignee)
            {
                row.Add(!string.IsNullOrWhiteSpace(t.AssignTo) && names is not null && names.TryGetValue(t.AssignTo, out var name)
                    ? name
                    : PersonName(t.AssignTo, t.AssigneeName));
            }
            row.Add(t.PlannedHours);
            row.Add(t.ActualHours > 0 ? Math.Round(t.ActualHours, 1) : null);
            row.Add(t.EndDateRaw is { } due
                ? due.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + (t.IsOverdue ? $" ({(today - due.Date).Days}d late)" : "")
                : null);
            row.Add(string.IsNullOrWhiteSpace(t.UserStoryTitle) ? null : TextTools.Cut(t.UserStoryTitle, 60));
            if (withProject) row.Add(t.ProjectName);
            return row;
        });
        return new { columns, rows };
    }

    private async Task<string> CreateProjectTaskAsync(ToolArguments a, CancellationToken ct)
    {
        var title = a.RequiredString("title");
        var request = new CreateProjectTaskRequest(
            a.ProjectId().ToString(), title, a.String("description"),
            a.Enum<GeneralStagesEnum>("status") ?? GeneralStagesEnum.Planned,
            a.Enum<PriorityEnum>("priority") ?? PriorityEnum.Medium, a.Enum<PriorityEnum>("complexity") ?? PriorityEnum.Medium,
            a.Guid("assignTo")?.ToString(), a.Number("plannedHours") ?? 0, a.Date("endDate"),
            a.Guid("epicId")?.ToString(), a.Guid("userStoryId")?.ToString(), a.Guid("skillId")?.ToString(), a.Guid("workBucketId")?.ToString());
        return Success($"Task '{title}' created.", await _api.CreateProjectTaskAsync(request, ct));
    }

    private async Task<string> CreateSubTaskAsync(ToolArguments a, CancellationToken ct)
    {
        var title = a.RequiredString("title");
        var request = new CreateSubTaskRequest(
            a.RequiredGuid("taskId").ToString(), title, a.String("description"), a.Guid("assignTo")?.ToString(),
            a.Enum<GeneralStagesEnum>("status") ?? GeneralStagesEnum.Planned,
            a.Enum<PriorityEnum>("priority") ?? PriorityEnum.Medium, a.Enum<PriorityEnum>("complexity") ?? PriorityEnum.Medium,
            a.Number("plannedHours") ?? 0, a.Date("endDate"), a.Bool("isBlocker") ?? false, a.Guid("skillId")?.ToString());
        return Success($"Sub-task '{title}' created.", await _api.CreateSubTaskAsync(request, ct));
    }

    private async Task<string> BulkCreateProjectTasksAsync(ToolArguments a, CancellationToken ct)
    {
        var projectId = a.ProjectId();
        var tasks = a.ObjectArray("tasks") ?? throw new ToolInputException("tasks is required.");
        if (tasks.Count is 0 or > MaxBulkItems)
        {
            throw new ToolInputException($"Pass between 1 and {MaxBulkItems} tasks.");
        }

        var userStoryId = a.Guid("userStoryId");
        var epicId = a.Guid("epicId");
        var defaultAssignTo = a.Guid("defaultAssignTo");
        var defaultDue = DateTime.Today.AddDays(Math.Max(0, a.Integer("defaultDueInDays") ?? 7));
        var existingTitles = a.Bool("skipExistingTitles") ?? true
            ? (await _api.ListAllProjectTasksAsync(projectId, ct: ct)).Select(t => t.Title).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var results = new List<BulkOutcome>(tasks.Count);
        foreach (var task in tasks)
        {
            var title = task.String("title")?.Trim() ?? "";
            if (title.Length == 0)
            {
                results.Add(new BulkOutcome(title, BulkOutcome.Failed, "Title is empty."));
                continue;
            }

            if (!existingTitles.Add(title))
            {
                results.Add(new BulkOutcome(title, BulkOutcome.Skipped, "A task with this title already exists."));
                continue;
            }

            try
            {
                var priority = task.Enum<PriorityEnum>("priority") ?? PriorityEnum.Medium;
                var request = new CreateProjectTaskRequest(
                    projectId.ToString(), title, task.String("description"), GeneralStagesEnum.Planned, priority, priority,
                    (task.Guid("assignTo") ?? defaultAssignTo)?.ToString(), task.Number("plannedHours") ?? 0,
                    task.Date("endDate") ?? defaultDue, epicId?.ToString(), userStoryId?.ToString());
                await _api.CreateProjectTaskAsync(request, ct);
                results.Add(new BulkOutcome(title, BulkOutcome.Created));
            }
            catch (Exception ex) when (ex is AgilePmApiException or ToolInputException)
            {
                results.Add(new BulkOutcome(title, BulkOutcome.Failed, ex.Message));
            }
        }

        return Json(new { succeeded = results.All(r => r.Outcome != BulkOutcome.Failed), summary = Summarize(results), results });
    }

    private async Task<string> BulkMoveTaskStatusAsync(ToolArguments a, CancellationToken ct)
    {
        var taskIds = a.GuidArray("taskIds") ?? throw new ToolInputException("taskIds is required.");
        if (taskIds.Count is 0 or > MaxBulkItems)
        {
            throw new ToolInputException($"Pass between 1 and {MaxBulkItems} task IDs.");
        }

        var status = a.RequiredEnum<GeneralStagesEnum>("status");
        var hours = a.Number("manHoursEach") ?? 0;
        var results = new List<BulkOutcome>(taskIds.Count);
        foreach (var taskId in taskIds.Distinct())
        {
            try
            {
                var steps = await MoveAsync(taskId, subTaskId: null, status, hours, ct);
                results.Add(new BulkOutcome(taskId.ToString(), BulkOutcome.Moved, Stages: steps.Select(s => s.ToString()).ToList()));
            }
            catch (AgilePmApiException ex)
            {
                results.Add(new BulkOutcome(taskId.ToString(), BulkOutcome.Failed, ex.Message));
            }
        }

        return Json(new { succeeded = results.All(r => r.Outcome == BulkOutcome.Moved), summary = Summarize(results), results });
    }

    private async Task<string> GetStandupReportAsync(ToolArguments a, CancellationToken ct)
    {
        var tasks = await _api.GetMyWorkAsync(includeCompleted: true, ct);
        var today = DateTime.Today;

        var inProgress = tasks.Where(t => t.Status == GeneralStagesEnum.InProgress).ToList();
        var completed = tasks.Where(t => t.Status == GeneralStagesEnum.Completed).ToList();
        var returned = tasks.Where(t => t.Status == GeneralStagesEnum.Returned).ToList();
        var nextUp = tasks.Where(t => t.Status == GeneralStagesEnum.Planned)
            .OrderBy(t => t.EndDateRaw ?? DateTime.MaxValue)
            .Take(Math.Clamp(a.Integer("maxNextUp") ?? 8, 1, 50))
            .ToList();
        var overdue = tasks.Where(t => t.IsOverdue).OrderBy(t => t.EndDateRaw).ToList();

        var markdown = new StringBuilder()
            .AppendLine($"**Standup: {today.ToString("dddd d MMMM yyyy", CultureInfo.InvariantCulture)}**")
            .AppendLine();
        AppendSection(markdown, "In progress", inProgress);
        AppendSection(markdown, "Done", completed);
        AppendSection(markdown, "Next up", nextUp);
        AppendSection(markdown, "Returned / needs attention", returned);
        AppendSection(markdown, "Overdue", overdue);

        return Json(new
        {
            date = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            totals = new
            {
                assigned = tasks.Count,
                inProgress = inProgress.Count,
                completed = completed.Count,
                planned = tasks.Count(t => t.Status == GeneralStagesEnum.Planned),
                returned = returned.Count,
                overdue = overdue.Count,
                openPlannedHours = tasks.Where(t => t.IsOpen).Sum(t => t.PlannedHours),
                loggedHours = tasks.Sum(t => t.ActualHours),
            },
            inProgress = inProgress.Select(Summary),
            completed = completed.Select(Summary),
            nextUp = nextUp.Select(Summary),
            returned = returned.Select(Summary),
            overdue = overdue.Select(Summary),
            markdown = markdown.ToString().TrimEnd(),
        });
    }

    private async Task<string> FindOverdueTasksAsync(ToolArguments a, CancellationToken ct)
    {
        var projectId = a.Guid("projectId");
        var tasks = projectId is { } id
            ? await _api.ListAllProjectTasksAsync(id, ct: ct)
            : await _api.GetMyWorkAsync(includeCompleted: false, ct);
        var today = DateTime.Today;
        var horizon = today.AddDays(Math.Clamp(a.Integer("dueWithinDays") ?? 3, 0, 60));

        var overdue = tasks.Where(t => t.IsOverdue).OrderBy(t => t.EndDateRaw).ToList();
        var dueSoon = tasks
            .Where(t => t.IsOpen && t.EndDateRaw is { } due && due.Date >= today && due.Date <= horizon)
            .OrderBy(t => t.EndDateRaw)
            .ToList();

        return Json(new
        {
            scope = projectId is null ? "my work" : $"project {projectId}",
            today = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            overdueCount = overdue.Count,
            dueSoonCount = dueSoon.Count,
            overdue = overdue.Select(t => new { task = Summary(t), daysLate = (today - t.EndDateRaw!.Value.Date).Days }),
            dueSoon = dueSoon.Select(Summary),
        });
    }

    private async Task<string> GetProjectProgressAsync(ToolArguments a, CancellationToken ct)
    {
        var projectId = a.ProjectId();
        var tasks = await _api.ListAllProjectTasksAsync(projectId, ct: ct);
        var done = tasks.Count(t => t.Status is GeneralStagesEnum.Completed or GeneralStagesEnum.Closed);
        var counted = tasks.Count(t => t.Status != GeneralStagesEnum.Cancelled);

        var byAssignee = tasks
            .GroupBy(t => t.AssigneeName)
            .Select(g => new
            {
                assignee = g.Key,
                open = g.Count(t => t.IsOpen),
                completed = g.Count(t => t.Status is GeneralStagesEnum.Completed or GeneralStagesEnum.Closed),
                overdue = g.Count(t => t.IsOverdue),
                openPlannedHours = g.Where(t => t.IsOpen).Sum(t => t.PlannedHours),
            })
            .OrderByDescending(x => x.open)
            .ToList();

        return Json(new
        {
            projectId,
            totalTasks = tasks.Count,
            completionPercent = counted == 0 ? 0 : Math.Round(100.0 * done / counted, 1),
            byStage = Enum.GetValues<GeneralStagesEnum>().ToDictionary(s => s.ToString(), s => tasks.Count(t => t.Status == s)),
            plannedHours = tasks.Sum(t => t.PlannedHours),
            loggedHours = tasks.Sum(t => t.ActualHours),
            overdueCount = tasks.Count(t => t.IsOverdue),
            unassignedOpen = tasks.Count(t => t.IsOpen && string.IsNullOrWhiteSpace(t.AssignTo)),
            byAssignee,
            overdue = tasks.Where(t => t.IsOverdue).OrderBy(t => t.EndDateRaw).Take(20).Select(Summary),
        });
    }

    /// <summary>
    /// Moves a task or sub-task and returns the stages it passed through. Agile PM refuses Planned -> Completed,
    /// so the current stage is looked up first and the item is stepped through InProgress when needed.
    /// </summary>
    private async Task<IReadOnlyList<GeneralStagesEnum>> MoveAsync(Guid taskId, Guid? subTaskId, GeneralStagesEnum status, double manHours, CancellationToken ct)
    {
        GeneralStagesEnum? current = null;
        if (status == GeneralStagesEnum.Completed)
        {
            current = subTaskId is null
                ? (await _api.GetProjectTaskAsync(taskId, ct)).Status
                // Sub-tasks have no get-by-id; find it among its parent's sub-tasks.
                : (await _api.ListSubTasksAsync(taskId, 1, MaxPageSize, ct))
                    .FirstOrDefault(s => string.Equals(s.Id, subTaskId.Value.ToString(), StringComparison.OrdinalIgnoreCase))?.Status;
        }

        await _api.MoveTaskStatusSmartAsync(taskId, status, manHours, subTaskId, current, ct);
        return current == GeneralStagesEnum.Planned ? [GeneralStagesEnum.InProgress, status] : [status];
    }

    // ==================== Helpers ====================

    private static AssistantTool Read(string name, string description, ToolSchema schema, Func<ToolArguments, CancellationToken, Task<string>> run) =>
        new(name, description, schema.Build(), ReadOnly: true, run);

    private static AssistantTool Write(string name, string description, ToolSchema schema, Func<ToolArguments, CancellationToken, Task<string>> run) =>
        new(name, description, schema.Build(), ReadOnly: false, run);

    private static (int Page, int PageSize) Paging(ToolArguments a) =>
        (Math.Max(1, a.Integer("page") ?? 1), Math.Clamp(a.Integer("pageSize") ?? DefaultPageSize, 1, MaxPageSize));

    private static string PagedPath(string path, ToolArguments a, params (string Key, object? Value)[] query)
    {
        var (page, pageSize) = Paging(a);
        return Path(path, [("paging.pageNumber", page), ("paging.pageSize", pageSize), .. query]);
    }

    /// <summary>Builds "path?Key=value&amp;..." leaving out null values.</summary>
    private static string Path(string path, params (string Key, object? Value)[] query)
    {
        var parts = query
            .Where(q => q.Value is not null)
            .Select(q => $"{q.Key}={Uri.EscapeDataString(q.Value switch
            {
                Guid g => g.ToString("D"),
                bool b => b ? "true" : "false",
                IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
                var v => v!.ToString() ?? "",
            })}")
            .ToList();
        return parts.Count == 0 ? path : $"{path}?{string.Join("&", parts)}";
    }

    private static (Guid? TaskId, Guid? SubTaskId, Guid? UserStoryId) SingleCommentTarget(ToolArguments a)
    {
        var target = (a.Guid("taskId"), a.Guid("subTaskId"), a.Guid("userStoryId"));
        if (new[] { target.Item1, target.Item2, target.Item3 }.Count(id => id is not null) != 1)
        {
            throw new ToolInputException("Pass exactly one of taskId, subTaskId or userStoryId.");
        }
        return target;
    }

    private static int? EpicCode(ToolArguments a, string name) =>
        a.Integer(name) is { } value && value is < 0 or > 3
            ? throw new ToolInputException($"{name} must be between 0 and 3.")
            : a.Integer(name);

    /// <summary>The compact task shape the tools return to the model (no HTML, names instead of IDs).</summary>
    private object Summary(TaskItem t) => new
    {
        id = t.Id,
        title = t.Title,
        project = string.IsNullOrWhiteSpace(t.ProjectName) ? null : t.ProjectName,
        userStory = string.IsNullOrWhiteSpace(t.UserStoryTitle) ? null : t.UserStoryTitle,
        userStoryId = string.IsNullOrWhiteSpace(t.UserStoryId) ? null : t.UserStoryId,
        status = t.Status.ToString(),
        priority = t.Priority.ToString(),
        assignee = PersonName(t.AssignTo, t.AssigneeName),
        plannedHours = t.PlannedHours,
        actualHours = t.ActualHours > 0 ? t.ActualHours : (double?)null,
        dueDate = t.EndDateRaw?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
    };

    private object TaskDetails(TaskItem t) => new
    {
        task = Summary(t),
        epicId = string.IsNullOrWhiteSpace(t.EpicId) ? null : t.EpicId,
        assigneeId = t.AssignTo,
        complexity = t.Complexity.ToString(),
        paused = t.IsPaused ? true : (bool?)null,
        description = TextTools.Plain(t.Description, 1500),
    };

    private object StoryCompact(UserStoryItem s, int textChars = 160) => new
    {
        id = s.Id,
        story = s.Story,
        epic = string.IsNullOrWhiteSpace(s.EpicTitle) ? null : s.EpicTitle,
        epicId = s.EpicId,
        status = s.Status.ToString(),
        priority = s.Priority.ToString(),
        assignees = s.AssigneesDisplay,
        description = NullIfEmpty(TextTools.Plain(s.Description, textChars)),
        acceptanceCriteria = NullIfEmpty(TextTools.Plain(s.AcceptanceCriteria, textChars)),
    };

    private static object EpicCompact(EpicItem e, int textChars = 160) => new
    {
        id = e.Id,
        title = e.Title,
        status = e.StatusDisplay,
        priority = e.PriorityDisplay,
        endDate = e.EndDate is "-" or "" ? null : e.EndDate,
        description = NullIfEmpty(TextTools.Plain(e.Description, textChars)),
    };

    private static object SubTaskCompact(SubTaskItem s) => new
    {
        id = s.Id,
        title = s.Title,
        status = s.Status.ToString(),
        priority = s.Priority.ToString(),
        plannedHours = s.PlannedHours,
        dueDate = s.EndDate is "-" or "" ? null : s.EndDate,
        assignee = s.AssigneeName,
        blocker = s.IsBlocker ? true : (bool?)null,
    };

    private static object CommentCompact(CommentItem c) => new
    {
        author = c.Author,
        date = c.CreatedAt,
        remark = TextTools.Plain(c.Remark, 500),
    };

    private object ProjectDetails(ProjectItem p) => new
    {
        id = p.Id,
        projectName = p.ProjectName,
        myRole = p.MyRole,
        status = p.StatusText,
        priority = p.PriorityText,
        projectManager = p.ProjectManagerName,
        scrumMaster = p.ScrumMasterName,
        startDate = p.StartDate,
        endDate = p.EndDate,
        totalTasks = p.TotalNumberOfTasks,
        members = p.TeamMembers.Select(m => new { userId = m.UserId, name = m.Name, role = m.Role, pendingTasks = m.PendingTasks }),
    };

    /// <summary>A person's name: from the project's people when known, else the name Agile PM sent, else the user ID.</summary>
    private string? PersonName(string? userId, string? fallback)
    {
        if (!string.IsNullOrWhiteSpace(userId) && UserNames?.Invoke() is { } names && names.TryGetValue(userId, out var name))
        {
            return name;
        }

        return string.IsNullOrWhiteSpace(fallback) || fallback == "Unassigned" || System.Guid.TryParse(fallback, out _)
            ? (string.IsNullOrWhiteSpace(userId) ? null : userId)
            : fallback;
    }

    private static string? NullIfEmpty(string text) => text.Length == 0 ? null : text;

    private static void AppendSection(StringBuilder markdown, string heading, IReadOnlyList<TaskItem> tasks)
    {
        markdown.AppendLine($"*{heading} ({tasks.Count})*");
        if (tasks.Count == 0)
        {
            markdown.AppendLine("- Nothing");
        }

        foreach (var task in tasks)
        {
            var project = string.IsNullOrWhiteSpace(task.ProjectName) ? "" : $"[{task.ProjectName}] ";
            var due = task.EndDateRaw is { } d ? $", due {d:yyyy-MM-dd}" : "";
            markdown.AppendLine($"- {project}{task.Title} ({task.PlannedHours:0.#} h{due})");
        }

        markdown.AppendLine();
    }

    private static string Summarize(IEnumerable<BulkOutcome> results) =>
        string.Join(", ", results.GroupBy(r => r.Outcome).Select(g => $"{g.Count()} {g.Key}")) + ".";

    private static string Json(object? value) => JsonSerializer.Serialize(value, OutputOptions);

    /// <summary>A page of results from a paginated endpoint, each item in its compact shape.</summary>
    private static string List(AgilePmEnvelope response, Func<JsonElement, object> compact) => Json(new
    {
        items = response.Items.Where(i => i.ValueKind == JsonValueKind.Object).Select(compact),
        paging = response.Page,
    });

    /// <summary>One record by ID, in its compact shape.</summary>
    private static string Single(AgilePmEnvelope response, Func<JsonElement, object> compact, string kind) =>
        response.Items.FirstOrDefault(i => i.ValueKind == JsonValueKind.Object) is { ValueKind: JsonValueKind.Object } item
            ? Json(compact(item))
            : throw new ToolInputException($"{kind} not found. Check the ID with a list tool.");

    /// <summary>Confirmation of a write, with what Agile PM said and a small returned value such as the new ID.</summary>
    private static string Success(string summary, AgilePmEnvelope response)
    {
        object? data = response.Data;
        if (response.Data is { } d && d.GetRawText().Length > 600)
        {
            // Large echoes (whole records) cost tokens; keep only the ID when there is one.
            data = d.ValueKind == JsonValueKind.Object && d.GetStringOrNull("id") is { } id ? new { id } : null;
        }

        return Json(new
        {
            succeeded = true,
            summary,
            messages = response.Messages.Count > 0 ? response.Messages : null,
            data,
        });
    }

    private void Log(string message) => OnLog?.Invoke($"[{DateTime.Now:HH:mm:ss}] {message}");

    private sealed record BulkOutcome(string Target, string Outcome, string? Error = null, IReadOnlyList<string>? Stages = null)
    {
        public const string Created = "created";
        public const string Moved = "moved";
        public const string Skipped = "skipped";
        public const string Failed = "failed";
    }
}

/// <summary>
/// Ready-made instructions for common workflows (the suggestion chips), written for the tools in <see cref="AgilePmToolbox"/>.
/// </summary>
public static class AgilePmPromptTemplates
{
    public static string DailyStandup(string tone = "brief") => $"""
        Write my daily standup update for today. Call get_standup_report, then write a {tone} update with three parts:
        what I finished, what I'm working on now, and blockers or risks (returned and overdue tasks count as risks).
        Use the task titles as Agile PM shows them, and don't invent work that isn't in the report.
        """;

    public static string BreakDownUserStory(string userStoryId, string? targetHours = null) => $"""
        Read user story {userStoryId} with get_user_story, including its acceptance criteria and existing tasks.
        Propose 3 to 8 implementation tasks that together satisfy every acceptance criterion and don't duplicate existing tasks.
        For each, give a title, a one-line description, planned hours and a priority (Low, Medium or High).
        {(string.IsNullOrWhiteSpace(targetHours) ? "" : $"Aim for about {targetHours} hours in total. ")}Show the plan as a table and wait for my confirmation.
        Once I confirm, create them in one call with bulk_create_project_tasks under this story, then report what was created.
        """;

    public static string SprintHealthCheck(string projectId) => $"""
        Assess the health of project {projectId}. Call get_project_progress and find_overdue_tasks for the project,
        then report: completion percentage, work by stage, the overdue tasks with how late they are, and anyone whose
        open load is far above the team average. Finish with up to three concrete actions, most important first.
        """;

    public static string PlanMyDay(string hoursAvailable = "8") => $"""
        Help me plan today. Call get_my_work and find_overdue_tasks. Pick the tasks I should work on in the
        {hoursAvailable} hours I have, prioritising overdue, returned and high-priority work, and finishing in-progress
        tasks before starting new ones. List them in order with the hours for each and one line on why.
        Don't change anything in Agile PM.
        """;
}

/// <summary>
/// Typed, forgiving access to the arguments a model passed. Values arrive as JSON; open models often send numbers
/// and booleans as strings, or enum numbers instead of names, so those are accepted too.
/// </summary>
public sealed class ToolArguments
{
    private readonly Dictionary<string, JsonElement> _values;

    public ToolArguments(IReadOnlyDictionary<string, object?> values)
    {
        _values = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in values)
        {
            var element = value as JsonElement? ?? JsonSerializer.SerializeToElement(value);
            if (element.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)) _values[key] = element;
        }
    }

    private ToolArguments(JsonElement obj)
    {
        _values = obj.EnumerateObject()
            .Where(p => p.Value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Value, StringComparer.OrdinalIgnoreCase);
    }

    public string? String(string name) => Get(name) switch
    {
        { ValueKind: JsonValueKind.String } s => string.IsNullOrWhiteSpace(s.GetString()) ? null : s.GetString(),
        { } other => other.ToString(),
        null => null,
    };

    public string RequiredString(string name) => String(name) ?? throw Missing(name);

    public Guid? Guid(string name) => String(name) is not { } text ? null
        : System.Guid.TryParse(text, out var id) ? id
        : throw new ToolInputException($"{name} must be an ID (GUID), not '{text}'. Look the ID up with a list tool first.");

    public Guid RequiredGuid(string name) => Guid(name) ?? throw Missing(name);

    public double? Number(string name) => Get(name) switch
    {
        { ValueKind: JsonValueKind.Number } n => n.GetDouble(),
        { ValueKind: JsonValueKind.String } s when double.TryParse(s.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
        null => null,
        _ => throw new ToolInputException($"{name} must be a number."),
    };

    public int? Integer(string name) => Number(name) is { } d ? (int)Math.Round(d) : null;

    public bool? Bool(string name) => Get(name) switch
    {
        { ValueKind: JsonValueKind.True } => true,
        { ValueKind: JsonValueKind.False } => false,
        { ValueKind: JsonValueKind.String } s when bool.TryParse(s.GetString(), out var b) => b,
        null => null,
        _ => throw new ToolInputException($"{name} must be true or false."),
    };

    public DateTime? Date(string name) => String(name) is not { } text ? null
        : DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var date) ? date
        : throw new ToolInputException($"{name} must be a date such as 2026-11-30, not '{text}'.");

    public T? Enum<T>(string name) where T : struct, Enum
    {
        if (Get(name) is not { } value) return null;
        var text = value.ValueKind == JsonValueKind.Number ? value.GetRawText() : value.GetString()?.Trim() ?? "";

        // "InProgress", "In Progress", "in_progress" and the number all name the same stage.
        foreach (var candidate in new[] { text, text.Replace(" ", ""), text.Replace(" ", "_"), text.Replace(" ", "").Replace("_", "") })
        {
            if (System.Enum.TryParse<T>(candidate, ignoreCase: true, out var parsed) && System.Enum.IsDefined(parsed)) return parsed;
        }

        throw new ToolInputException($"{name} must be one of: {string.Join(", ", System.Enum.GetNames<T>())}.");
    }

    public T RequiredEnum<T>(string name) where T : struct, Enum => Enum<T>(name) ?? throw Missing(name);

    public IReadOnlyList<Guid>? GuidArray(string name) => Array(name)?
        .Select(e => e.ValueKind == JsonValueKind.String && System.Guid.TryParse(e.GetString(), out var id) ? id
            : throw new ToolInputException($"{name} must be a list of IDs (GUIDs)."))
        .ToList();

    public IReadOnlyList<ToolArguments>? ObjectArray(string name) => Array(name)?
        .Select(e => e.ValueKind == JsonValueKind.Object ? new ToolArguments(e)
            : throw new ToolInputException($"Each item of {name} must be an object."))
        .ToList();

    private IReadOnlyList<JsonElement>? Array(string name)
    {
        var value = Get(name);
        // Some open models send an array as a JSON string.
        if (value is { ValueKind: JsonValueKind.String } s && s.GetString()?.TrimStart().StartsWith('[') == true)
        {
            try { value = JsonDocument.Parse(s.GetString()!).RootElement.Clone(); }
            catch (JsonException) { /* reported below */ }
        }

        return value switch
        {
            { ValueKind: JsonValueKind.Array } array => array.EnumerateArray().ToList(),
            { ValueKind: JsonValueKind.String } single when name.EndsWith("Ids", StringComparison.Ordinal) || name == "assignedTo" => [single],
            null => null,
            _ => throw new ToolInputException($"{name} must be a list."),
        };
    }

    /// <summary>The app's active project, used when a tool needs a project and the model didn't name one.</summary>
    public Guid? DefaultProjectId { get; init; }

    public Guid ProjectId() => Guid("projectId") ?? DefaultProjectId
        ?? throw new ToolInputException("projectId is required: no active project is selected in the app. Call list_my_projects to find it.");

    private JsonElement? Get(string name) => _values.TryGetValue(name, out var value) ? value : null;

    private static ToolInputException Missing(string name) => new($"{name} is required.");
}

/// <summary>Builds a tool's JSON input schema.</summary>
public sealed class ToolSchema
{
    private readonly JsonObject _properties = [];
    private readonly List<string> _required = [];

    public ToolSchema String(string name, string description, bool required = false) => Add(name, "string", description, required);

    public ToolSchema Guid(string name, string description, bool required = false) => Add(name, "string", $"{description} (GUID)", required);

    public ToolSchema Date(string name, string description, bool required = false) => Add(name, "string", description, required);

    public ToolSchema Number(string name, string description, bool required = false) => Add(name, "number", description, required);

    public ToolSchema Integer(string name, string description, bool required = false) => Add(name, "integer", description, required);

    public ToolSchema Bool(string name, string description, bool required = false) => Add(name, "boolean", description, required);

    public ToolSchema Enum<T>(string name, string description, bool required = false) where T : struct, Enum =>
        Add(name, new JsonObject
        {
            ["type"] = "string",
            ["description"] = description,
            ["enum"] = new JsonArray(System.Enum.GetNames<T>().Select(n => (JsonNode?)n).ToArray()),
        }, required);

    public ToolSchema GuidArray(string name, string description, bool required = false) =>
        Add(name, new JsonObject
        {
            ["type"] = "array",
            ["description"] = $"{description} (GUIDs)",
            ["items"] = new JsonObject { ["type"] = "string" },
        }, required);

    public ToolSchema Array(string name, string description, ToolSchema items, bool required = false) =>
        Add(name, new JsonObject
        {
            ["type"] = "array",
            ["description"] = description,
            ["items"] = items.BuildNode(),
        }, required);

    /// <summary>projectId, optional because it defaults to the project selected in the app.</summary>
    public ToolSchema ProjectId() => Add("projectId", "string", "Project ID (GUID). Leave out to use the active project.", required: false);

    public ToolSchema Paging(string itemsName) => this
        .Integer("page", "Page number, starting at 1")
        .Integer("pageSize", $"{itemsName} per page (max 50, default 20)");

    public JsonElement Build() => JsonSerializer.SerializeToElement(BuildNode());

    private JsonObject BuildNode() => new()
    {
        ["type"] = "object",
        ["properties"] = _properties.DeepClone(),
        ["required"] = new JsonArray(_required.Select(r => (JsonNode?)r).ToArray()),
    };

    private ToolSchema Add(string name, string type, string description, bool required) =>
        Add(name, new JsonObject { ["type"] = type, ["description"] = description }, required);

    private ToolSchema Add(string name, JsonObject schema, bool required)
    {
        _properties[name] = schema;
        if (required) _required.Add(name);
        return this;
    }
}
