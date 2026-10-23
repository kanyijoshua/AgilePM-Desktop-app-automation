using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutoPM_Desktop.Models;

public enum GeneralStagesEnum
{
    Planned = 0,
    InProgress = 1,
    Returned = 2,
    Completed = 3,
    Closed = 4,
    Cancelled = 5,
}

public enum UserStoryStagesEnum
{
    Planned = 0,
    In_Progress = 1,
    Completed = 2,
    Closed = 3,
}

public enum PriorityEnum
{
    Low = 0,
    Medium = 1,
    High = 2,
}

public sealed record PageInfo(int Page, int PageSize, int TotalCount, int TotalPages, bool HasNextPage)
{
    public static PageInfo? From(JsonElement envelope)
    {
        if (!envelope.TryGetProperty("totalCount", out var totalCount) || totalCount.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        return new PageInfo(
            envelope.GetIntOrDefault("currentPage"),
            envelope.GetIntOrDefault("pageSize"),
            totalCount.GetInt32(),
            envelope.GetIntOrDefault("totalPages"),
            envelope.TryGetProperty("hasNextPage", out var next) && next.ValueKind == JsonValueKind.True);
    }
}

public sealed class UserIdentityInfo
{
    public IReadOnlyList<string> Ids { get; init; } = [];
    public IReadOnlyList<string> Emails { get; init; } = [];
    public string? DisplayName { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }

    public Guid? UserId => Ids
        .Select(id => Guid.TryParse(id, out var guid) ? guid : (Guid?)null)
        .FirstOrDefault(g => g is not null);

    public string PrimaryEmail => Emails.FirstOrDefault() ?? DisplayName ?? "Signed-in User";

    public bool IsUser(string? idOrEmail) =>
        !string.IsNullOrWhiteSpace(idOrEmail)
        && (Ids.Contains(idOrEmail, StringComparer.OrdinalIgnoreCase)
            || Emails.Contains(idOrEmail, StringComparer.OrdinalIgnoreCase));
}

public sealed class ProjectItem
{
    public string Id { get; init; } = "";
    public Guid? GuidId => Guid.TryParse(Id, out var g) ? g : null;
    public string ProjectName { get; init; } = "(Untitled Project)";
    public string MyRole { get; init; } = "Viewer";
    public string StatusText { get; init; } = "";
    public string PriorityText { get; init; } = "";
    public string ProjectManagerId { get; init; } = "";
    public string ProjectManagerName { get; init; } = "";
    public string ScrumMasterId { get; init; } = "";
    public string ScrumMasterName { get; init; } = "";
    public string StartDate { get; init; } = "";
    public string EndDate { get; init; } = "";
    public int TotalNumberOfTasks { get; init; }
    public List<TeamMemberItem> TeamMembers { get; init; } = [];
    public JsonElement RawJson { get; init; }

    public string DisplayLabel => string.IsNullOrWhiteSpace(MyRole) ? ProjectName : $"{ProjectName} ({MyRole})";

    /// <summary>What screen readers and UI automation read for the project in lists.</summary>
    public override string ToString() => DisplayLabel;

    public static ProjectItem FromJson(JsonElement el, UserIdentityInfo? identity)
    {
        var pmId = el.GetStringOrDefault("projectManagerId");
        var smId = el.GetStringOrDefault("scrumMasterId");
        var members = el.EnumerateArrayOrEmpty("projectTeamMembers")
            .Select(TeamMemberItem.FromJson)
            .ToList();

        string role = "All Projects";
        if (identity is not null)
        {
            if (identity.IsUser(pmId))
                role = "Project Manager";
            else if (identity.IsUser(smId))
                role = "Scrum Master";
            else
            {
                var m = members.FirstOrDefault(x => identity.IsUser(x.UserId) || identity.IsUser(x.Email));
                role = m is not null ? (string.IsNullOrWhiteSpace(m.Role) ? "Team Member" : m.Role) : "";
            }
        }

        return new ProjectItem
        {
            Id = el.GetStringOrDefault("id"),
            ProjectName = el.GetStringOrDefault("projectName", "(Untitled Project)"),
            MyRole = role,
            StatusText = FormatStageOrNumber(el, "status"),
            PriorityText = FormatPriorityOrNumber(el, "priority"),
            ProjectManagerId = pmId,
            ProjectManagerName = el.GetStringOrDefault("projectManagerName", "-"),
            ScrumMasterId = smId,
            ScrumMasterName = el.GetStringOrDefault("scrumMasterName", "-"),
            StartDate = FormatDate(el.GetStringOrDefault("startDate")),
            EndDate = FormatDate(el.GetStringOrDefault("endDate")),
            TotalNumberOfTasks = el.GetIntOrDefault("totalNumberOfTasks"),
            TeamMembers = members,
            RawJson = el.Clone()
        };
    }

    private static string FormatStageOrNumber(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var v)) return "-";
        if (v.ValueKind == JsonValueKind.String) return v.GetString() ?? "-";
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n))
        {
            return Enum.IsDefined(typeof(GeneralStagesEnum), n) ? ((GeneralStagesEnum)n).ToString() : n.ToString();
        }
        return "-";
    }

    private static string FormatPriorityOrNumber(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var v)) return "-";
        if (v.ValueKind == JsonValueKind.String) return v.GetString() ?? "-";
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n))
        {
            return Enum.IsDefined(typeof(PriorityEnum), n) ? ((PriorityEnum)n).ToString() : n.ToString();
        }
        return "-";
    }

    public static string FormatDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "-";
        return DateTime.TryParse(raw, out var dt) ? dt.ToString("yyyy-MM-dd") : raw;
    }
}

public sealed class TeamMemberItem
{
    public string UserId { get; init; } = "";
    public Guid? GuidUserId => Guid.TryParse(UserId, out var g) ? g : null;
    public string Name { get; init; } = "";
    public string Email { get; init; } = "";
    public string Role { get; init; } = "";
    public int PendingTasks { get; init; }

    public string DisplayName => !string.IsNullOrWhiteSpace(Name)
        ? (!string.IsNullOrWhiteSpace(Role) ? $"{Name} ({Role})" : Name)
        : Email;

    public static TeamMemberItem FromJson(JsonElement el) => new()
    {
        UserId = el.GetStringOrDefault("applicationUserId", el.GetStringOrDefault("userId", el.GetStringOrDefault("id"))),
        Name = el.GetStringOrDefault("userName", el.GetStringOrDefault("name", el.GetStringOrDefault("fullName"))),
        Email = el.GetStringOrDefault("email"),
        Role = el.GetStringOrDefault("userRole", el.GetStringOrDefault("role", "Team Member")),
        PendingTasks = el.GetIntOrDefault("numberOfPendingTasks")
    };
}

public sealed class EpicItem
{
    public string Id { get; init; } = "";
    public Guid? GuidId => Guid.TryParse(Id, out var g) ? g : null;
    public string ProjectId { get; init; } = "";
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public double EstimatedEffort { get; init; }
    public int Status { get; init; }
    public int Priority { get; init; }
    public string StartDate { get; init; } = "";
    public string EndDate { get; init; } = "";
    public string? MilestoneId { get; init; }
    public string? MilestoneGroupId { get; init; }
    public string? ProjectContractId { get; init; }
    public string? PhaseId { get; init; }
    public JsonElement RawJson { get; init; }

    public string StatusDisplay => Status switch
    {
        0 => "0 - Planned",
        1 => "1 - In Progress",
        2 => "2 - Completed",
        3 => "3 - Closed",
        _ => Status.ToString()
    };

    public string PriorityDisplay => Priority switch
    {
        0 => "0 - Low",
        1 => "1 - Medium",
        2 => "2 - High",
        3 => "3 - Critical",
        _ => Priority.ToString()
    };

    public static EpicItem FromJson(JsonElement el, string fallbackProjectId = "") => new()
    {
        Id = el.GetStringOrDefault("id"),
        ProjectId = el.GetStringOrDefault("projectId", fallbackProjectId),
        Title = el.GetStringOrDefault("title", "(Untitled Epic)"),
        Description = el.GetStringOrDefault("description"),
        EstimatedEffort = el.GetDoubleOrDefault("estimatedEffort"),
        Status = el.GetIntOrDefault("status"),
        Priority = el.GetIntOrDefault("priority"),
        StartDate = ProjectItem.FormatDate(el.GetStringOrDefault("startDate")),
        EndDate = ProjectItem.FormatDate(el.GetStringOrDefault("endDate")),
        MilestoneId = el.GetStringOrNull("milestoneId"),
        MilestoneGroupId = el.GetStringOrNull("milestoneGroupId"),
        ProjectContractId = el.GetStringOrNull("projectContractId"),
        PhaseId = el.GetStringOrNull("phaseId"),
        RawJson = el.Clone()
    };
}

public sealed class UserStoryItem
{
    public string Id { get; init; } = "";
    public Guid? GuidId => Guid.TryParse(Id, out var g) ? g : null;
    public string EpicId { get; init; } = "";
    public string EpicTitle { get; init; } = "";
    public string Story { get; init; } = "";
    public string Description { get; init; } = "";
    public string AcceptanceCriteria { get; init; } = "";
    public PriorityEnum Priority { get; init; }
    public UserStoryStagesEnum Status { get; init; }
    public string? WorkItemCategoryId { get; init; }
    public List<string> AssignedUserIds { get; init; } = [];
    public string AssigneesDisplay { get; init; } = "";
    public int TaskCount { get; init; }
    public JsonElement RawJson { get; init; }

    public static UserStoryItem FromJson(JsonElement el)
    {
        var assignees = new List<string>();
        var names = new List<string>();
        foreach (var a in el.EnumerateArrayOrEmpty("assignments"))
        {
            var uid = a.GetStringOrNull("applicationUserId") ?? a.GetStringOrNull("userId");
            if (!string.IsNullOrWhiteSpace(uid)) assignees.Add(uid);
            var uName = a.GetStringOrNull("applicationUserName") ?? a.GetStringOrNull("userName")
                ?? a.GetStringOrNull("fullName") ?? a.GetStringOrNull("email") ?? uid;
            if (!string.IsNullOrWhiteSpace(uName)) names.Add(string.Join(' ', uName.Split(' ', StringSplitOptions.RemoveEmptyEntries)));
        }

        var prioInt = el.GetIntOrDefault("priority", 1);
        var statusInt = el.GetIntOrDefault("status", 0);

        return new UserStoryItem
        {
            Id = el.GetStringOrDefault("id"),
            EpicId = el.GetStringOrDefault("epicId"),
            EpicTitle = el.GetStringOrDefault("epicTitle", el.GetStringOrDefault("epicName")),
            Story = el.GetStringOrDefault("story", el.GetStringOrDefault("title", "(Untitled Story)")),
            Description = el.GetStringOrDefault("description"),
            AcceptanceCriteria = el.GetStringOrDefault("acceptanceCriteria"),
            Priority = Enum.IsDefined(typeof(PriorityEnum), prioInt) ? (PriorityEnum)prioInt : PriorityEnum.Medium,
            Status = Enum.IsDefined(typeof(UserStoryStagesEnum), statusInt) ? (UserStoryStagesEnum)statusInt : UserStoryStagesEnum.Planned,
            WorkItemCategoryId = el.GetStringOrNull("workItemCategoryId"),
            AssignedUserIds = assignees,
            AssigneesDisplay = names.Count > 0 ? string.Join(", ", names) : (assignees.Count > 0 ? $"{assignees.Count} assigned" : "Unassigned"),
            TaskCount = el.EnumerateArrayOrEmpty("projectTasks").Count() + el.GetIntOrDefault("numberOfTasks"),
            RawJson = el.Clone()
        };
    }
}

public sealed class TaskItem
{
    public string Id { get; init; } = "";
    public Guid? GuidId => Guid.TryParse(Id, out var g) ? g : null;
    public string ProjectId { get; init; } = "";
    public string ProjectName { get; init; } = "";
    public string EpicId { get; init; } = "";
    public string UserStoryId { get; init; } = "";
    public string UserStoryTitle { get; init; } = "";
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public GeneralStagesEnum Status { get; init; }
    public PriorityEnum Priority { get; init; }
    public PriorityEnum Complexity { get; init; }
    public string? AssignTo { get; init; }
    public string AssigneeName { get; init; } = "";
    public double PlannedHours { get; init; }
    public double ActualHours { get; init; }
    public string EndDate { get; init; } = "";
    public DateTime? EndDateRaw { get; init; }
    public string? SkillId { get; init; }
    public bool IsPaused { get; init; }
    public JsonElement RawJson { get; init; }

    public string StatusDisplay => Status.ToString();
    public string HoursDisplay => ActualHours > 0 ? $"{ActualHours:0.#} / {PlannedHours:0.#} hrs" : $"{PlannedHours:0.#} hrs";

    public bool IsOpen => Status is not (GeneralStagesEnum.Completed or GeneralStagesEnum.Closed or GeneralStagesEnum.Cancelled);
    public bool IsOverdue => IsOpen && EndDateRaw is { } due && due.Date < DateTime.Today;

    public static TaskItem FromJson(JsonElement el, string fallbackProjectId = "", string fallbackProjectName = "")
    {
        var id = el.GetStringOrDefault("id", el.GetStringOrDefault("projectTaskId", el.GetStringOrDefault("taskId")));
        var title = el.GetStringOrDefault("title", el.GetStringOrDefault("taskTitle", el.GetStringOrDefault("name", "(Untitled Task)")));
        var projectId = el.GetStringOrDefault("projectId");
        var projectName = el.GetStringOrDefault("projectName", el.GetStringOrDefault("projectTitle"));
        var statusInt = el.GetEnumOrInt("status", (int)GeneralStagesEnum.Planned);
        var prioInt = el.GetEnumOrInt("priority", (int)PriorityEnum.Medium);
        var compInt = el.GetEnumOrInt("complexity", (int)PriorityEnum.Medium);
        var endDateStr = el.GetStringOrNull("endDate") ?? el.GetStringOrNull("dueDate");
        DateTime? endDateParsed = DateTime.TryParse(endDateStr, out var dt) ? dt : null;

        var assignTo = el.GetStringOrNull("assignTo")
            ?? el.GetStringOrNull("assignedTo")
            ?? el.GetStringOrNull("assignedToId")
            ?? el.GetStringOrNull("applicationUserId");

        var assigneeName = el.GetStringOrNull("assignedToName")
            ?? el.GetStringOrNull("assignToName")
            ?? el.GetStringOrNull("userName")
            ?? el.GetStringOrNull("assigneeName")
            ?? assignTo
            ?? "Unassigned";

        return new TaskItem
        {
            Id = id,
            ProjectId = string.IsNullOrWhiteSpace(projectId) ? fallbackProjectId : projectId,
            ProjectName = string.IsNullOrWhiteSpace(projectName) ? fallbackProjectName : projectName,
            EpicId = el.GetStringOrDefault("epicId"),
            UserStoryId = el.GetStringOrDefault("userStoryId"),
            UserStoryTitle = el.GetStringOrDefault("userStory", el.GetStringOrDefault("userStoryTitle", "")),
            Title = title,
            Description = el.GetStringOrDefault("description"),
            Status = Enum.IsDefined(typeof(GeneralStagesEnum), statusInt) ? (GeneralStagesEnum)statusInt : GeneralStagesEnum.Planned,
            Priority = Enum.IsDefined(typeof(PriorityEnum), prioInt) ? (PriorityEnum)prioInt : PriorityEnum.Medium,
            Complexity = Enum.IsDefined(typeof(PriorityEnum), compInt) ? (PriorityEnum)compInt : PriorityEnum.Medium,
            AssignTo = assignTo,
            AssigneeName = assigneeName,
            PlannedHours = el.GetDoubleOrDefault("plannedHours", el.GetDoubleOrDefault("estimatedHours")),
            ActualHours = el.GetDoubleOrDefault("manHours", el.GetDoubleOrDefault("actualHours", el.GetDoubleOrDefault("hoursWorked"))),
            EndDate = ProjectItem.FormatDate(endDateStr),
            EndDateRaw = endDateParsed,
            SkillId = el.GetStringOrNull("skillId"),
            IsPaused = el.TryGetProperty("isPaused", out var p) && p.ValueKind == JsonValueKind.True,
            RawJson = el.Clone()
        };
    }
}

public sealed class SubTaskItem
{
    public string Id { get; init; } = "";
    public Guid? GuidId => Guid.TryParse(Id, out var g) ? g : null;
    public string ParentTaskId { get; init; } = "";
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public GeneralStagesEnum Status { get; init; }
    public PriorityEnum Priority { get; init; }
    public double PlannedHours { get; init; }
    public string EndDate { get; init; } = "";
    public string AssigneeName { get; init; } = "";
    public bool IsBlocker { get; init; }

    public static SubTaskItem FromJson(JsonElement el, string parentTaskId = "")
    {
        var statusInt = el.GetEnumOrInt("status", 0);
        var prioInt = el.GetEnumOrInt("priority", 1);

        return new SubTaskItem
        {
            Id = el.GetStringOrDefault("id", el.GetStringOrDefault("subTaskId")),
            ParentTaskId = el.GetStringOrDefault("taskId", el.GetStringOrDefault("parentTaskId", parentTaskId)),
            Title = el.GetStringOrDefault("title", "(Untitled Sub-task)"),
            Description = el.GetStringOrDefault("description"),
            Status = Enum.IsDefined(typeof(GeneralStagesEnum), statusInt) ? (GeneralStagesEnum)statusInt : GeneralStagesEnum.Planned,
            Priority = Enum.IsDefined(typeof(PriorityEnum), prioInt) ? (PriorityEnum)prioInt : PriorityEnum.Medium,
            PlannedHours = el.GetDoubleOrDefault("plannedHours"),
            EndDate = ProjectItem.FormatDate(el.GetStringOrNull("endDate")),
            AssigneeName = el.GetStringOrDefault("assignedToName", el.GetStringOrDefault("assignTo", "Unassigned")),
            IsBlocker = el.TryGetProperty("isBlocker", out var b) && b.ValueKind == JsonValueKind.True
        };
    }
}

public sealed class CommentItem
{
    public string Id { get; init; } = "";
    public string Remark { get; init; } = "";
    public string Author { get; init; } = "";
    public string CreatedAt { get; init; } = "";
    public string Initial => string.IsNullOrWhiteSpace(Author) ? "?" : Author.Trim()[..1].ToUpperInvariant();

    public static CommentItem FromJson(JsonElement el) => new()
    {
        Id = el.GetStringOrDefault("id"),
        Remark = el.GetStringOrDefault("remark", el.GetStringOrDefault("comment", el.GetStringOrDefault("text"))),
        Author = el.GetStringOrDefault("createdByName", el.GetStringOrDefault("userName", el.GetStringOrDefault("author", "Team Member"))),
        CreatedAt = ProjectItem.FormatDate(el.GetStringOrDefault("createdAt", el.GetStringOrDefault("createdDate", el.GetStringOrDefault("dateCreated"))))
    };
}

// ---- API Request Payloads (matching AutoPM_MCP) ----

public sealed record CreateEpicRequest(
    string ProjectId,
    string Title,
    string? Description,
    double EstimatedEffort,
    int Status,
    int Priority,
    DateTime? StartDate,
    DateTime? EndDate,
    string? PhaseId = null,
    string? MilestoneId = null);

public sealed record UpdateEpicRequest(
    string Id,
    string? MilestoneId,
    string? MilestoneGroupId,
    string ProjectId,
    string? ProjectContractId,
    string? PhaseId,
    string? Title,
    string? Description,
    double EstimatedEffort,
    int Status,
    int Priority,
    DateTime? StartDate,
    DateTime? EndDate);

public sealed record CreateUserStoryRequest(
    string EpicId,
    string Story,
    string? Description,
    string? AcceptanceCriteria,
    PriorityEnum Priority,
    UserStoryStagesEnum Status,
    IReadOnlyList<string>? AssignedTo,
    string? WorkItemCategoryId = null);

public sealed record UpdateUserStoryRequest(
    string Id,
    string? Story,
    string? Description,
    string? AcceptanceCriteria,
    IReadOnlyList<string>? AssignedTo,
    string? EpicId,
    PriorityEnum Priority,
    string? WorkItemCategoryId);

public sealed record ReAssignUserStoryRequest(string UserStoryId, string AssignedToId, string AssignedFromId);

public sealed record MoveUserStoryStatusRequest(
    // The API's property name is misspelled; keep it as-is on the wire.
    [property: JsonPropertyName("userStorId")] string UserStoryId,
    UserStoryStagesEnum Status);

public sealed record CreateProjectTaskRequest(
    string ProjectId,
    string Title,
    string? Description,
    GeneralStagesEnum Status,
    PriorityEnum Priority,
    PriorityEnum Complexity,
    string? AssignTo,
    double PlannedHours,
    DateTime? EndDate,
    string? EpicId,
    string? UserStoryId,
    string? SkillId = null,
    string? WorkBucketId = null);

public sealed record UpdateProjectTaskRequest(
    string Id,
    string? Title,
    string? Description,
    GeneralStagesEnum Status,
    PriorityEnum Priority,
    PriorityEnum Complexity,
    string? SkillId,
    string? AssignTo,
    double PlannedHours,
    DateTime? EndDate,
    string? EpicId,
    string? UserStoryId);

public sealed record MoveTaskStatusRequest(
    string ProjectTaskId,
    string? SubTaskId,
    GeneralStagesEnum Status,
    double ManHours);

public sealed record ReAssignProjectTaskRequest(string ProjectTaskId, string AssignedToId);

public sealed record TaskWorkActionRequest(string ProjectTaskId);

public sealed record CreateSubTaskRequest(
    string TaskId,
    string Title,
    string? Description,
    string? AssignTo,
    GeneralStagesEnum Status,
    PriorityEnum Priority,
    PriorityEnum Complexity,
    double PlannedHours,
    DateTime? EndDate,
    bool IsBlocker,
    string? SkillId = null);

public sealed record CreateCommentRequest(
    string Remark,
    string? ProjectTaskId,
    string? SubTaskId,
    string? UserStoryId,
    IReadOnlyList<string>? TaggedMembers);

public static class JsonHelpers
{
    public static string? GetStringOrNull(this JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String)
            return value.GetString();

        // Case-insensitive fallback
        foreach (var prop in element.EnumerateObject())
        {
            if (string.Equals(prop.Name, property, StringComparison.OrdinalIgnoreCase) && prop.Value.ValueKind == JsonValueKind.String)
                return prop.Value.GetString();
        }
        return null;
    }

    public static string GetStringOrDefault(this JsonElement element, string property, string defaultValue = "") =>
        element.GetStringOrNull(property) ?? defaultValue;

    public static int GetIntOrDefault(this JsonElement element, string property, int defaultValue = 0)
    {
        if (element.ValueKind != JsonValueKind.Object) return defaultValue;
        if (element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n))
            return n;

        foreach (var prop in element.EnumerateObject())
        {
            if (string.Equals(prop.Name, property, StringComparison.OrdinalIgnoreCase) && prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetInt32(out var ci))
                return ci;
        }
        return defaultValue;
    }

    public static double GetDoubleOrDefault(this JsonElement element, string property, double defaultValue = 0)
    {
        if (element.ValueKind != JsonValueKind.Object) return defaultValue;
        if (element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var d))
            return d;

        foreach (var prop in element.EnumerateObject())
        {
            if (string.Equals(prop.Name, property, StringComparison.OrdinalIgnoreCase) && prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetDouble(out var cd))
                return cd;
        }
        return defaultValue;
    }

    public static int GetEnumOrInt(this JsonElement element, string property, int defaultValue = 0)
    {
        if (element.ValueKind != JsonValueKind.Object) return defaultValue;
        if (!element.TryGetProperty(property, out var value))
        {
            foreach (var prop in element.EnumerateObject())
            {
                if (string.Equals(prop.Name, property, StringComparison.OrdinalIgnoreCase))
                {
                    value = prop.Value;
                    break;
                }
            }
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n))
            return n;

        if (value.ValueKind == JsonValueKind.String)
        {
            var s = value.GetString();
            if (int.TryParse(s, out var parsedInt)) return parsedInt;
            if (Enum.TryParse<GeneralStagesEnum>(s?.Replace(" ", ""), true, out var gs)) return (int)gs;
            if (Enum.TryParse<PriorityEnum>(s, true, out var pr)) return (int)pr;
        }
        return defaultValue;
    }

    public static IEnumerable<JsonElement> EnumerateArrayOrEmpty(this JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object) return [];
        if (element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array)
            return value.EnumerateArray();

        foreach (var prop in element.EnumerateObject())
        {
            if (string.Equals(prop.Name, property, StringComparison.OrdinalIgnoreCase) && prop.Value.ValueKind == JsonValueKind.Array)
                return prop.Value.EnumerateArray();
        }
        return [];
    }
}
