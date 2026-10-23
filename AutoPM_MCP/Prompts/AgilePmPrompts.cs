using ModelContextProtocol.Server;
using System.ComponentModel;

namespace AutoPM_MCP.Prompts;

/// <summary>
/// Reusable prompt templates. MCP clients list them as slash commands or prompt pickers, so a user can start
/// a common Agile PM workflow without writing the instructions themselves.
/// </summary>
[McpServerPromptType]
internal class AgilePmPrompts
{
    [McpServerPrompt]
    [Description("Writes today's standup update from your Agile PM work.")]
    public static string DailyStandup(
        [Description("Tone of the update, e.g. 'brief', 'detailed' or 'for a manager'")] string tone = "brief") =>
        $"""
        Write my daily standup update for today. Call get_standup_report, then write a {tone} update with three parts:
        what I finished, what I'm working on now, and blockers or risks (returned and overdue tasks count as risks).
        Use the task titles as Agile PM shows them, and don't invent work that isn't in the report.
        """;

    [McpServerPrompt]
    [Description("Breaks a user story down into implementation tasks and, after you confirm, creates them.")]
    public static string BreakDownUserStory(
        [Description("User story ID")] string userStoryId,
        [Description("Rough total effort to aim for, in hours (optional)")] string? targetHours = null) =>
        $"""
        Read user story {userStoryId} with get_user_story, including its acceptance criteria and existing tasks.
        Propose 3 to 8 implementation tasks that together satisfy every acceptance criterion and don't duplicate existing tasks.
        For each, give a title, a one-line description, planned hours and a priority (Low, Medium or High).
        {(string.IsNullOrWhiteSpace(targetHours) ? "" : $"Aim for about {targetHours} hours in total. ")}Show the plan as a table and wait for my confirmation.
        Once I confirm, create them in one call with bulk_create_project_tasks under this story, then report what was created.
        """;

    [McpServerPrompt]
    [Description("Checks a project's sprint health: progress, overdue work and overloaded team members.")]
    public static string SprintHealthCheck(
        [Description("Project ID")] string projectId) =>
        $"""
        Assess the health of project {projectId}. Call get_project_progress and find_overdue_tasks for the project,
        then report: completion percentage, work by stage, the overdue tasks with how late they are, and anyone whose
        open load is far above the team average. Finish with up to three concrete actions, most important first.
        """;

    [McpServerPrompt]
    [Description("Plans your day: picks what to work on next from your assigned tasks.")]
    public static string PlanMyDay(
        [Description("Hours available today")] string hoursAvailable = "8") =>
        $"""
        Help me plan today. Call get_my_work and find_overdue_tasks. Pick the tasks I should work on in the
        {hoursAvailable} hours I have, prioritising overdue, returned and high-priority work, and finishing in-progress
        tasks before starting new ones. List them in order with the hours for each and one line on why.
        Don't change anything in Agile PM.
        """;
}
