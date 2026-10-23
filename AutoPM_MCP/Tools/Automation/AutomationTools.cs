using AutoPM_MCP.Helpers;
using AutoPM_MCP.Tools.Shared;
using AutoPM_MCP.Tools.TaskManagement;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Globalization;
using System.Text;

namespace AutoPM_MCP.Tools.Automation;

/// <summary>
/// Multi-step tools that save an AI assistant (or the desktop app) from chaining many single calls:
/// bulk creation and moves, standup reports, overdue checks and project progress.
/// </summary>
[McpServerToolType]
internal class AutomationTools(AgilePmClient client)
{
    private const int MaxBulkItems = 50;
    private const string ProjectTasksPath = "TaskManagement/getProjectTasks";

    [McpServerTool(Destructive = false)]
    [Description("Creates up to 50 tasks in one project in a single call, optionally all under one user story or epic. " +
                 "Each task is created independently: the result lists which ones succeeded, failed or were skipped as duplicates.")]
    public async Task<string> BulkCreateProjectTasks(
        [Description("Project ID")] Guid projectId,
        [Description("The tasks to create")] BulkTaskInput[] tasks,
        [Description("User story every task belongs to")] Guid? userStoryId = null,
        [Description("Epic every task belongs to")] Guid? epicId = null,
        [Description("Assignee for tasks that don't name their own")] Guid? defaultAssignTo = null,
        [Description("Due date for tasks that don't set one, in days from today")] int defaultDueInDays = 7,
        [Description("Skip tasks whose title already exists in the project (case-insensitive)")] bool skipExistingTitles = true,
        CancellationToken cancellationToken = default)
    {
        if (tasks.Length == 0 || tasks.Length > MaxBulkItems)
        {
            throw new McpException($"Pass between 1 and {MaxBulkItems} tasks.");
        }

        var existingTitles = skipExistingTitles
            ? (await client.GetAllPagesAsync(ProjectTasksPath, AgilePmQuery.Create().Add("ProjectId", projectId), cancellationToken))
                .Select(t => TaskSnapshot.From(t).Title)
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];

        var results = new List<BulkOutcome>(tasks.Length);
        foreach (var task in tasks)
        {
            if (string.IsNullOrWhiteSpace(task.Title))
            {
                results.Add(new BulkOutcome(task.Title, BulkOutcome.Failed, "Title is empty."));
                continue;
            }

            if (!existingTitles.Add(task.Title.Trim()))
            {
                results.Add(new BulkOutcome(task.Title, BulkOutcome.Skipped, "A task with this title already exists."));
                continue;
            }

            var command = new CreateProjectTaskCommand(
                projectId.ToString(), task.Title.Trim(), task.Description, GeneralStagesEnum.Planned, task.Priority, task.Priority,
                (task.AssignTo ?? defaultAssignTo)?.ToString(), task.PlannedHours,
                task.EndDate ?? DateTime.Today.AddDays(Math.Max(0, defaultDueInDays)),
                epicId?.ToString(), userStoryId?.ToString(), SkillId: null, WorkBucketId: null);

            try
            {
                await client.PostAsync("TaskManagement/AddProjectTask", command, cancellationToken);
                results.Add(new BulkOutcome(task.Title, BulkOutcome.Created));
            }
            catch (McpException ex)
            {
                results.Add(new BulkOutcome(task.Title, BulkOutcome.Failed, ex.Message));
            }
        }

        return ToolResponse.Json(new
        {
            succeeded = results.All(r => r.Outcome != BulkOutcome.Failed),
            summary = Summarize(results),
            results,
        });
    }

    [McpServerTool(Destructive = false)]
    [Description("Moves up to 50 tasks to the same stage in one call, logging the same hours on each. " +
                 "Planned tasks moved to Completed are stepped through InProgress automatically.")]
    public async Task<string> BulkMoveTaskStatus(
        [Description("Task IDs to move")] Guid[] taskIds,
        [Description("Target stage")] GeneralStagesEnum status,
        [Description("Hours worked to log on each task")] double manHoursEach = 0,
        CancellationToken cancellationToken = default)
    {
        if (taskIds.Length == 0 || taskIds.Length > MaxBulkItems)
        {
            throw new McpException($"Pass between 1 and {MaxBulkItems} task IDs.");
        }

        var results = new List<BulkOutcome>(taskIds.Length);
        foreach (var taskId in taskIds.Distinct())
        {
            try
            {
                var steps = await TaskStageMover.MoveAsync(client, taskId, subTaskId: null, status, manHoursEach, cancellationToken);
                results.Add(new BulkOutcome(taskId.ToString(), BulkOutcome.Moved, Stages: steps.Select(s => s.ToString()).ToList()));
            }
            catch (McpException ex)
            {
                results.Add(new BulkOutcome(taskId.ToString(), BulkOutcome.Failed, ex.Message));
            }
        }

        return ToolResponse.Json(new { succeeded = results.All(r => r.Outcome == BulkOutcome.Moved), summary = Summarize(results), results });
    }

    [McpServerTool(ReadOnly = true)]
    [Description("Builds the current user's daily standup report from their assigned work: in progress, done, next up, " +
                 "returned and overdue tasks, with hour totals and a ready-to-paste Markdown version.")]
    public async Task<string> GetStandupReport(
        [Description("How many planned tasks to list under 'next up'")] int maxNextUp = 8,
        CancellationToken cancellationToken = default)
    {
        var response = await client.GetAsync("WorkManagement/GetMyWork", AgilePmQuery.Create().Add("IncludeCompleted", true), cancellationToken);
        var tasks = TaskSnapshot.FromItems(response.Items).ToList();
        var today = DateTime.Today;

        var inProgress = tasks.Where(t => t.Status == GeneralStagesEnum.InProgress).ToList();
        var completed = tasks.Where(t => t.Status == GeneralStagesEnum.Completed).ToList();
        var returned = tasks.Where(t => t.Status == GeneralStagesEnum.Returned).ToList();
        var nextUp = tasks.Where(t => t.Status == GeneralStagesEnum.Planned)
            .OrderBy(t => t.DueDate ?? DateTime.MaxValue)
            .Take(Math.Clamp(maxNextUp, 1, 50))
            .ToList();
        var overdue = tasks.Where(t => t.IsOverdue(today)).OrderBy(t => t.DueDate).ToList();

        var markdown = new StringBuilder()
            .AppendLine($"**Standup: {today.ToString("dddd d MMMM yyyy", CultureInfo.InvariantCulture)}**")
            .AppendLine();
        AppendSection(markdown, "In progress", inProgress);
        AppendSection(markdown, "Done", completed);
        AppendSection(markdown, "Next up", nextUp);
        AppendSection(markdown, "Returned / needs attention", returned);
        AppendSection(markdown, "Overdue", overdue);

        return ToolResponse.Json(new
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
            inProgress = inProgress.Select(t => t.ToSummary()),
            completed = completed.Select(t => t.ToSummary()),
            nextUp = nextUp.Select(t => t.ToSummary()),
            returned = returned.Select(t => t.ToSummary()),
            overdue = overdue.Select(t => t.ToSummary()),
            markdown = markdown.ToString().TrimEnd(),
        });
    }

    [McpServerTool(ReadOnly = true)]
    [Description("Finds open tasks that are past their due date or due soon, either across the current user's work " +
                 "or across a whole project.")]
    public async Task<string> FindOverdueTasks(
        [Description("Check this project's tasks instead of the current user's work")] Guid? projectId = null,
        [Description("Also list open tasks due within this many days")] int dueWithinDays = 3,
        CancellationToken cancellationToken = default)
    {
        var tasks = await LoadTasksAsync(projectId, cancellationToken);
        var today = DateTime.Today;
        var horizon = today.AddDays(Math.Clamp(dueWithinDays, 0, 60));

        var overdue = tasks.Where(t => t.IsOverdue(today)).OrderBy(t => t.DueDate).ToList();
        var dueSoon = tasks
            .Where(t => t.IsOpen && t.DueDate is { } due && due.Date >= today && due.Date <= horizon)
            .OrderBy(t => t.DueDate)
            .ToList();

        return ToolResponse.Json(new
        {
            scope = projectId is null ? "my work" : $"project {projectId}",
            today = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            overdueCount = overdue.Count,
            dueSoonCount = dueSoon.Count,
            overdue = overdue.Select(t => new { task = t.ToSummary(), daysLate = (today - t.DueDate!.Value.Date).Days }),
            dueSoon = dueSoon.Select(t => t.ToSummary()),
        });
    }

    [McpServerTool(ReadOnly = true)]
    [Description("Summarizes a project's delivery progress: tasks by stage, completion percentage, planned vs logged hours, " +
                 "overdue work and each assignee's open load. Use it for sprint health checks and status updates.")]
    public async Task<string> GetProjectProgress(
        [Description("Project ID")] Guid projectId,
        CancellationToken cancellationToken = default)
    {
        var tasks = await LoadTasksAsync(projectId, cancellationToken);
        var today = DateTime.Today;
        var done = tasks.Count(t => t.Status is GeneralStagesEnum.Completed or GeneralStagesEnum.Closed);
        var counted = tasks.Count(t => t.Status != GeneralStagesEnum.Cancelled);

        var byAssignee = tasks
            .GroupBy(t => t.AssigneeName ?? t.AssigneeId ?? "Unassigned")
            .Select(g => new
            {
                assignee = g.Key,
                open = g.Count(t => t.IsOpen),
                completed = g.Count(t => t.Status is GeneralStagesEnum.Completed or GeneralStagesEnum.Closed),
                overdue = g.Count(t => t.IsOverdue(today)),
                openPlannedHours = g.Where(t => t.IsOpen).Sum(t => t.PlannedHours),
            })
            .OrderByDescending(a => a.open)
            .ToList();

        return ToolResponse.Json(new
        {
            projectId,
            totalTasks = tasks.Count,
            completionPercent = counted == 0 ? 0 : Math.Round(100.0 * done / counted, 1),
            byStage = Enum.GetValues<GeneralStagesEnum>().ToDictionary(s => s.ToString(), s => tasks.Count(t => t.Status == s)),
            plannedHours = tasks.Sum(t => t.PlannedHours),
            loggedHours = tasks.Sum(t => t.ActualHours),
            overdueCount = tasks.Count(t => t.IsOverdue(today)),
            unassignedOpen = tasks.Count(t => t.IsOpen && t.AssigneeId is null && t.AssigneeName is null),
            byAssignee,
            overdue = tasks.Where(t => t.IsOverdue(today)).OrderBy(t => t.DueDate).Take(20).Select(t => t.ToSummary()),
        });
    }

    private async Task<List<TaskSnapshot>> LoadTasksAsync(Guid? projectId, CancellationToken cancellationToken)
    {
        if (projectId is { } id)
        {
            var items = await client.GetAllPagesAsync(ProjectTasksPath, AgilePmQuery.Create().Add("ProjectId", id), cancellationToken);
            return TaskSnapshot.FromItems(items).ToList();
        }

        var response = await client.GetAsync("WorkManagement/GetMyWork", AgilePmQuery.Create().Add("IncludeCompleted", false), cancellationToken);
        return TaskSnapshot.FromItems(response.Items).ToList();
    }

    private static void AppendSection(StringBuilder markdown, string heading, IReadOnlyList<TaskSnapshot> tasks)
    {
        markdown.AppendLine($"*{heading} ({tasks.Count})*");
        if (tasks.Count == 0)
        {
            markdown.AppendLine("- Nothing");
        }

        foreach (var task in tasks)
        {
            var project = task.ProjectName is null ? "" : $"[{task.ProjectName}] ";
            var due = task.DueDate is { } d ? $", due {d:yyyy-MM-dd}" : "";
            markdown.AppendLine($"- {project}{task.Title} ({task.PlannedHours:0.#} h{due})");
        }

        markdown.AppendLine();
    }

    private static string Summarize(IEnumerable<BulkOutcome> results) =>
        string.Join(", ", results.GroupBy(r => r.Outcome).Select(g => $"{g.Count()} {g.Key}")) + ".";
}

/// <summary>One task for <see cref="AutomationTools.BulkCreateProjectTasks"/>.</summary>
internal sealed record BulkTaskInput(
    [property: Description("Task title")] string Title,
    [property: Description("Task description")] string? Description = null,
    [property: Description("Planned effort in hours")] double PlannedHours = 0,
    [property: Description("Priority (also used as complexity)")] PriorityEnum Priority = PriorityEnum.Medium,
    [property: Description("Due date (ISO 8601); defaults to defaultDueInDays from today")] DateTime? EndDate = null,
    [property: Description("User ID to assign this task to; defaults to defaultAssignTo")] Guid? AssignTo = null);

/// <summary>What happened to one item of a bulk tool call.</summary>
internal sealed record BulkOutcome(string Target, string Outcome, string? Error = null, IReadOnlyList<string>? Stages = null)
{
    public const string Created = "created";
    public const string Moved = "moved";
    public const string Skipped = "skipped";
    public const string Failed = "failed";
}
