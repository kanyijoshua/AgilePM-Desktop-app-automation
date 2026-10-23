using AutoPM_MCP.Helpers;
using AutoPM_MCP.Tools.Shared;

namespace AutoPM_MCP.Tools.TaskManagement;

/// <summary>
/// Moves tasks and sub-tasks between stages. Agile PM refuses Planned -> Completed, so when the target is
/// Completed and the item is still Planned, it is stepped through InProgress first.
/// </summary>
internal static class TaskStageMover
{
    private const string MovePath = "TaskManagement/MoveTaskStatus";

    /// <summary>Moves the item and returns the stages it passed through (e.g. ["InProgress", "Completed"]).</summary>
    public static async Task<IReadOnlyList<GeneralStagesEnum>> MoveAsync(
        AgilePmClient client,
        Guid taskId,
        Guid? subTaskId,
        GeneralStagesEnum status,
        double manHours,
        CancellationToken cancellationToken)
    {
        var steps = new List<GeneralStagesEnum>(2);
        if (status == GeneralStagesEnum.Completed
            && await GetCurrentStageAsync(client, taskId, subTaskId, cancellationToken) == GeneralStagesEnum.Planned)
        {
            var step = new MoveTaskStatusCommand(taskId.ToString(), subTaskId?.ToString(), GeneralStagesEnum.InProgress, 0);
            await client.PostAsync(MovePath, step, cancellationToken);
            steps.Add(GeneralStagesEnum.InProgress);
        }

        var command = new MoveTaskStatusCommand(taskId.ToString(), subTaskId?.ToString(), status, manHours);
        await client.PostAsync(MovePath, command, cancellationToken);
        steps.Add(status);
        return steps;
    }

    private static async Task<GeneralStagesEnum?> GetCurrentStageAsync(
        AgilePmClient client, Guid taskId, Guid? subTaskId, CancellationToken cancellationToken)
    {
        if (subTaskId is null)
        {
            var task = await client.GetByIdAsync<ProjectTaskRecord>("TaskManagement/getProjectTasks", taskId, "Task", cancellationToken);
            return task.Status;
        }

        // Sub-tasks have no get-by-id; find it among its parent's sub-tasks.
        var response = await client.GetAsync(
            "TaskManagement/getSubTasks",
            AgilePmQuery.Paged(1, AgilePmQuery.MaxPageSize).Add("ParentTaskId", taskId),
            cancellationToken);

        return response.Items
            .Select(TaskSnapshot.From)
            .FirstOrDefault(s => string.Equals(s.Id, subTaskId.Value.ToString(), StringComparison.OrdinalIgnoreCase))
            ?.Status;
    }
}
