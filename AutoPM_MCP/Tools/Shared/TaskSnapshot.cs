using AutoPM_MCP.Helpers;
using System.Globalization;
using System.Text.Json;

namespace AutoPM_MCP.Tools.Shared;

/// <summary>
/// A lenient, flattened view of a task or sub-task, used by tools that summarize work rather than echo it.
/// Agile PM's task payloads differ between endpoints (GetMyWork groups by project, names vary), so every
/// field is read from the first property name that is present.
/// </summary>
internal sealed record TaskSnapshot(
    string Id,
    string Title,
    string? ProjectName,
    string? UserStory,
    GeneralStagesEnum? Status,
    PriorityEnum? Priority,
    string? AssigneeId,
    string? AssigneeName,
    double PlannedHours,
    double ActualHours,
    DateTime? DueDate)
{
    public bool IsOpen => Status is not (GeneralStagesEnum.Completed or GeneralStagesEnum.Closed or GeneralStagesEnum.Cancelled);

    public bool IsOverdue(DateTime today) => IsOpen && DueDate is { } due && due.Date < today.Date;

    public static TaskSnapshot From(JsonElement task) => new(
        FirstString(task, "id", "projectTaskId", "taskId", "subTaskId") ?? "",
        FirstString(task, "title", "taskTitle", "name") ?? "(untitled)",
        FirstString(task, "projectName", "projectTitle"),
        FirstString(task, "userStory", "userStoryTitle"),
        ReadEnum<GeneralStagesEnum>(task, "status"),
        ReadEnum<PriorityEnum>(task, "priority"),
        FirstString(task, "assignTo", "assignedTo", "assignedToId", "applicationUserId"),
        FirstString(task, "assignedToName", "assignToName", "assigneeName", "userName"),
        FirstNumber(task, "plannedHours", "estimatedHours"),
        FirstNumber(task, "manHours", "actualHours", "hoursWorked"),
        FirstString(task, "endDate", "dueDate") is { } date
            && DateTime.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var parsed)
                ? parsed
                : null);

    /// <summary>
    /// Flattens a task list payload. GetMyWork may group tasks under a project ("tasks" / "projectTasks"),
    /// so a group's nested tasks are returned in place of the group itself.
    /// </summary>
    public static IEnumerable<TaskSnapshot> FromItems(IEnumerable<JsonElement> items)
    {
        foreach (var item in items.Where(i => i.ValueKind == JsonValueKind.Object))
        {
            var nested = item.GetPropertyOrNull("tasks") ?? (item.GetPropertyOrNull("title") is null ? item.GetPropertyOrNull("projectTasks") : null);
            if (nested is { ValueKind: JsonValueKind.Array } group)
            {
                var projectName = FirstString(item, "projectName", "projectTitle");
                foreach (var task in group.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.Object))
                {
                    var snapshot = From(task);
                    yield return snapshot.ProjectName is null ? snapshot with { ProjectName = projectName } : snapshot;
                }
            }
            else
            {
                yield return From(item);
            }
        }
    }

    /// <summary>The compact shape tools return to the LLM.</summary>
    public object ToSummary() => new
    {
        id = Id,
        title = Title,
        project = ProjectName,
        userStory = UserStory,
        status = Status?.ToString(),
        priority = Priority?.ToString(),
        assignee = AssigneeName ?? AssigneeId,
        plannedHours = PlannedHours,
        actualHours = ActualHours > 0 ? ActualHours : (double?)null,
        dueDate = DueDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
    };

    private static string? FirstString(JsonElement element, params string[] names) =>
        names.Select(name => element.GetStringOrNull(name)).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private static double FirstNumber(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.GetPropertyOrNull(name) is { ValueKind: JsonValueKind.Number } value && value.TryGetDouble(out var number))
            {
                return number;
            }
        }

        return 0;
    }

    /// <summary>Agile PM usually sends enums as numbers, but some views send names ("In Progress").</summary>
    private static T? ReadEnum<T>(JsonElement element, string name) where T : struct, Enum
    {
        switch (element.GetPropertyOrNull(name))
        {
            case { ValueKind: JsonValueKind.Number } number when number.TryGetInt32(out var value) && Enum.IsDefined(typeof(T), value):
                return (T)Enum.ToObject(typeof(T), value);
            case { ValueKind: JsonValueKind.String } text when Enum.TryParse<T>(text.GetString()?.Replace(" ", ""), ignoreCase: true, out var parsed):
                return parsed;
            default:
                return null;
        }
    }
}
