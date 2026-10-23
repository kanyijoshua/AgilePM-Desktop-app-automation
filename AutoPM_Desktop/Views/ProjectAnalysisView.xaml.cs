using AutoPM_Desktop.Models;
using AutoPM_Desktop.Services;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace AutoPM_Desktop.Views;

/// <summary>
/// One-click analysis of the active project. The numbers (stages, overdue work, load per person, progress per epic)
/// are computed here from every task and story; the model only writes the analysis from those facts, so it can't
/// invent figures and the request stays small enough for free model tiers.
/// </summary>
public partial class ProjectAnalysisView : UserControl
{
    private static readonly Dictionary<string, (string Title, string Instructions)> Reports = new()
    {
        ["health"] = ("Health & risks",
            "Assess the project's delivery health. Start with a one-line verdict (On track / At risk / Off track) and why. " +
            "Then the key numbers, the top risks (overdue, returned, unassigned or stuck work, overloaded people) and up to " +
            "three concrete next actions, most important first."),
        ["workload"] = ("Workload balance",
            "Analyse how open work is spread across the team: who is overloaded or under-used compared with the team average " +
            "(open tasks and open planned hours), where overdue work concentrates, and specific reassignments (task → person) " +
            "that would even it out. Suggest only; don't claim anything was changed."),
        ["progress"] = ("Epic & story progress",
            "Summarise progress by epic: what is done, in progress and not started, which epics look at risk (low completion " +
            "near their end date, overdue tasks) and which user stories have no tasks yet."),
        ["deadlines"] = ("Overdue & upcoming",
            "List the overdue work and what is due in the next 7 days, grouped by person, with how late each item is. " +
            "Say what to chase first and why."),
        ["status"] = ("Stakeholder update",
            "Write a short status update for stakeholders (email or Teams), under 200 words, with these headings: Summary, " +
            "Done recently, Next, Risks & blockers, Help needed. Plain, confident language; no internal IDs."),
        ["mine"] = ("My work in this project",
            "Focus on the signed-in user's own tasks in this project: what to do next in priority order (overdue and returned " +
            "first, finish in-progress before starting new), what is at risk, and anything waiting on them."),
    };

    private AiWorkspace? _ws;
    private bool _busy;

    public ProjectAnalysisView() => InitializeComponent();

    public void Initialize(AiWorkspace workspace) => _ws = workspace;

    public void OnProjectChanged(ProjectItem? project)
    {
        KpiPanel.Visibility = Visibility.Collapsed;
        TitleText.Text = project is null ? "Project analysis" : $"Analysis: {project.ProjectName.Trim()}";
        DataText.Text = project is null
            ? "Select an active project first."
            : "Numbers are computed from every task and story in the project; the AI writes the analysis from them.";
        ReportTitle.Text = "Report";
        ReportBox.Text = "Pick a report above. It uses the live data of the active project.";
    }

    /// <summary>Loads the project's data and shows the key numbers (no AI needed).</summary>
    public async Task ShowNumbersAsync(bool refresh = false)
    {
        if (_ws?.ActiveProject() is null || _busy) return;
        await RunBusyAsync(async () => ShowKpis(await _ws.LoadProjectAsync(refresh)));
    }

    private async void Reload_Click(object sender, RoutedEventArgs e) => await ShowNumbersAsync(refresh: true);

    private void QuestionBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        Report_Click(AskBtn, e);
    }

    private async void Report_Click(object sender, RoutedEventArgs e)
    {
        if (_ws is null || _busy || sender is not FrameworkElement { Tag: string kind }) return;

        var question = QuestionBox.Text.Trim();
        if (kind == "custom" && question.Length < 4)
        {
            _ws.Notify("Type a question about the project first.", true);
            QuestionBox.Focus();
            return;
        }
        if (!_ws.EnsureAiReady()) return;

        var (title, instructions) = kind == "custom" ? ("Answer", $"Answer this question about the project: {question}") : Reports[kind];
        await RunBusyAsync(async () =>
        {
            var data = await _ws.LoadProjectAsync();
            ShowKpis(data);

            var ai = _ws.Ai();
            var answerTokens = Math.Clamp(ai.RequestBudgetTokens / 4, 900, 2500);
            var factChars = Math.Max(1200, (int)((ai.RequestBudgetTokens - answerTokens - 400) * 3.3));
            var facts = BuildFacts(data, kind, factChars);

            ReportTitle.Text = $"{title} — writing with {ai.DisplayName}…";
            ReportBox.Text = "";
            var prompt = $"""
                You are a delivery analyst for the Agile PM project "{data.Project.ProjectName.Trim()}". Today is {DateTime.Today:dddd yyyy-MM-dd}.
                {instructions}
                Use only the facts below; if something isn't in them, say so instead of guessing. Use people's names and task titles as given.
                Format: plain text with short headings and "-" bullets (no Markdown tables), easy to paste into email or chat.

                <facts>
                {facts}
                </facts>
                """;

            var answer = await ai.CompleteAsync(prompt, null, answerTokens, CancellationToken.None);
            ReportTitle.Text = $"{title} · {data.Project.ProjectName.Trim()} · {DateTime.Now:HH:mm} · {ai.DisplayName}";
            ReportBox.Text = answer.Trim();
            _ws.AddHistory("AI analysis", $"{title} for {data.Project.ProjectName.Trim()}", true);
        });
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ReportBox.Text)) return;
        Clipboard.SetText(ReportBox.Text);
        _ws?.Notify("Report copied.", false);
    }

    private void ShowKpis(ProjectData data)
    {
        var tasks = data.Tasks.Where(t => t.Status != GeneralStagesEnum.Cancelled).ToList();
        var done = tasks.Count(t => t.Status is GeneralStagesEnum.Completed or GeneralStagesEnum.Closed);
        var open = tasks.Where(t => t.IsOpen).ToList();
        var today = DateTime.Today;

        KpiDone.Text = tasks.Count == 0 ? "—" : $"{100.0 * done / tasks.Count:0}%";
        KpiOpen.Text = open.Count.ToString();
        KpiOverdue.Text = open.Count(t => t.IsOverdue).ToString();
        KpiDueSoon.Text = open.Count(t => t.EndDateRaw is { } d && d.Date >= today && d.Date <= today.AddDays(7)).ToString();
        KpiReturned.Text = tasks.Count(t => t.Status == GeneralStagesEnum.Returned).ToString();
        KpiUnassigned.Text = open.Count(t => string.IsNullOrWhiteSpace(t.AssignTo)).ToString();
        KpiHours.Text = $"{tasks.Sum(t => t.ActualHours):0} / {tasks.Sum(t => t.PlannedHours):0}";
        KpiPanel.Visibility = Visibility.Visible;
        DataText.Text = $"{data.Tasks.Count} tasks, {data.Stories.Count} user stories, {data.Epics.Count} epics, {data.Members.Count} people · data from {data.LoadedAt:HH:mm}";
    }

    /// <summary>
    /// The facts the model writes from: always the totals, then the sections the report needs, each list cut so the
    /// whole thing fits in <paramref name="maxChars"/>.
    /// </summary>
    private string BuildFacts(ProjectData data, string kind, int maxChars)
    {
        var today = DateTime.Today;
        var tasks = data.Tasks.Where(t => t.Status != GeneralStagesEnum.Cancelled).ToList();
        var open = tasks.Where(t => t.IsOpen).ToList();
        var done = tasks.Count(t => t.Status is GeneralStagesEnum.Completed or GeneralStagesEnum.Closed);
        string Who(TaskItem t) => data.PersonName(t.AssignTo, t.AssigneeName);
        string Due(TaskItem t) => t.EndDateRaw is { } d ? d.ToString("MMM d") : "no due date";

        var sb = new StringBuilder();
        var p = data.Project;
        sb.AppendLine($"Project: {p.ProjectName.Trim()} | PM: {p.ProjectManagerName.Trim()} | Scrum master: {p.ScrumMasterName.Trim()} | {p.StartDate} to {p.EndDate}");
        sb.AppendLine($"Tasks: {tasks.Count} ({done} done, {(tasks.Count == 0 ? 0 : 100.0 * done / tasks.Count):0}% complete). " +
                      $"By stage: {string.Join(", ", Enum.GetValues<GeneralStagesEnum>().Select(s => $"{StoryBoardView.Humanize(s)} {tasks.Count(t => t.Status == s)}"))}.");
        sb.AppendLine($"Hours: {tasks.Sum(t => t.ActualHours):0.#} logged of {tasks.Sum(t => t.PlannedHours):0.#} planned; open work {open.Sum(t => t.PlannedHours):0.#} h planned.");
        sb.AppendLine($"Overdue open tasks: {open.Count(t => t.IsOverdue)}. Due in next 7 days: {open.Count(t => t.EndDateRaw is { } d && d.Date >= today && d.Date <= today.AddDays(7))}. " +
                      $"Returned: {tasks.Count(t => t.Status == GeneralStagesEnum.Returned)}. Unassigned open: {open.Count(t => string.IsNullOrWhiteSpace(t.AssignTo))}. Paused: {open.Count(t => t.IsPaused)}.");
        sb.AppendLine($"User stories: {data.Stories.Count} ({string.Join(", ", data.Stories.GroupBy(s => s.Status).Select(g => $"{g.Key.ToString().Replace('_', ' ')} {g.Count()}"))}).");

        void Section(string heading, IEnumerable<string> lines, int maxLines)
        {
            var header = $"\n{heading}:";
            if (sb.Length + header.Length > maxChars) return;
            sb.AppendLine(header);
            foreach (var line in lines.Take(maxLines))
            {
                if (sb.Length + line.Length + 3 > maxChars) { sb.AppendLine("- …(more not shown)"); return; }
                sb.AppendLine($"- {line}");
            }
        }

        var people = tasks.GroupBy(Who).Select(g => new
        {
            Name = g.Key,
            Open = g.Count(t => t.IsOpen),
            InProgress = g.Count(t => t.Status == GeneralStagesEnum.InProgress),
            Overdue = g.Count(t => t.IsOverdue),
            OpenHours = g.Where(t => t.IsOpen).Sum(t => t.PlannedHours),
            Done = g.Count(t => t.Status is GeneralStagesEnum.Completed or GeneralStagesEnum.Closed),
        }).OrderByDescending(x => x.Open).ToList();
        var peopleLines = people.Select(x => $"{x.Name}: {x.Open} open ({x.InProgress} in progress, {x.Overdue} overdue), {x.OpenHours:0.#} h open, {x.Done} done");

        var epicLines = tasks.GroupBy(t => t.EpicId ?? "").Select(g =>
        {
            var epic = data.Epic(g.Key);
            var total = g.Count();
            var finished = g.Count(t => t.Status is GeneralStagesEnum.Completed or GeneralStagesEnum.Closed);
            return (Total: total, Line: $"{epic?.Title.Trim() ?? "(no epic)"}: {finished}/{total} tasks done, {g.Count(t => t.IsOverdue)} overdue, " +
                                        $"{g.Where(t => t.IsOpen).Sum(t => t.PlannedHours):0.#} h open{(epic is { EndDate: not ("-" or "") } ? $", ends {epic.EndDate}" : "")}");
        }).OrderByDescending(x => x.Total).Select(x => x.Line);

        var overdueLines = open.Where(t => t.IsOverdue).OrderBy(t => t.EndDateRaw)
            .Select(t => $"{TextTools.Cut(t.Title, 70)} | {Who(t)} | {StoryBoardView.Humanize(t.Status)} | {(today - t.EndDateRaw!.Value.Date).Days} days late");
        var soonLines = open.Where(t => t.EndDateRaw is { } d && d.Date >= today && d.Date <= today.AddDays(7)).OrderBy(t => t.EndDateRaw)
            .Select(t => $"{TextTools.Cut(t.Title, 70)} | {Who(t)} | {StoryBoardView.Humanize(t.Status)} | due {Due(t)}");
        var returnedLines = tasks.Where(t => t.Status == GeneralStagesEnum.Returned)
            .Select(t => $"{TextTools.Cut(t.Title, 70)} | {Who(t)} | due {Due(t)}");
        var storiesWithoutTasks = data.Stories
            .Where(s => s.Status is UserStoryStagesEnum.Planned or UserStoryStagesEnum.In_Progress)
            .Where(s => !tasks.Any(t => string.Equals(t.UserStoryId, s.Id, StringComparison.OrdinalIgnoreCase)))
            .Select(s => TextTools.Cut(s.Story, 80));
        var recentlyDone = tasks.Where(t => t.Status is GeneralStagesEnum.Completed or GeneralStagesEnum.Closed)
            .OrderByDescending(t => t.EndDateRaw).Select(t => $"{TextTools.Cut(t.Title, 70)} | {Who(t)}");
        var inProgress = open.Where(t => t.Status == GeneralStagesEnum.InProgress).OrderBy(t => t.EndDateRaw)
            .Select(t => $"{TextTools.Cut(t.Title, 70)} | {Who(t)} | due {Due(t)}");

        switch (kind)
        {
            case "health":
                Section("Overdue tasks", overdueLines, 15);
                Section("Workload by person", peopleLines, 15);
                Section("Progress by epic", epicLines, 12);
                Section("Returned tasks", returnedLines, 8);
                break;
            case "workload":
                Section("Workload by person", peopleLines, 30);
                Section("Overdue tasks", overdueLines, 15);
                Section("Unassigned open tasks", open.Where(t => string.IsNullOrWhiteSpace(t.AssignTo))
                    .Select(t => $"{TextTools.Cut(t.Title, 70)} | {t.PlannedHours:0.#} h | due {Due(t)}"), 12);
                break;
            case "progress":
                Section("Progress by epic", epicLines, 30);
                Section("Open user stories with no tasks", storiesWithoutTasks, 20);
                Section("In progress", inProgress, 12);
                break;
            case "deadlines":
                Section("Overdue tasks", overdueLines, 30);
                Section("Due in the next 7 days", soonLines, 25);
                break;
            case "status":
                Section("Progress by epic", epicLines, 10);
                Section("Recently completed", recentlyDone, 12);
                Section("In progress", inProgress, 10);
                Section("Overdue tasks", overdueLines, 8);
                Section("Returned tasks", returnedLines, 5);
                break;
            case "mine":
                var mine = tasks.Where(t => _ws!.IsMe(t.AssignTo)).ToList();
                sb.AppendLine($"\nThe signed-in user is {_ws!.Api.CurrentUser?.PrimaryEmail}; they have {mine.Count(t => t.IsOpen)} open tasks here.");
                Section("My open tasks", mine.Where(t => t.IsOpen).OrderBy(t => t.EndDateRaw ?? DateTime.MaxValue)
                    .Select(t => $"{TextTools.Cut(t.Title, 70)} | {StoryBoardView.Humanize(t.Status)} | {t.Priority} | {t.PlannedHours:0.#} h | due {Due(t)}{(t.IsOverdue ? " | OVERDUE" : "")}"), 30);
                Section("My recently completed", mine.Where(t => !t.IsOpen).OrderByDescending(t => t.EndDateRaw)
                    .Select(t => TextTools.Cut(t.Title, 70)), 8);
                break;
            default:
                Section("Progress by epic", epicLines, 15);
                Section("Workload by person", peopleLines, 15);
                Section("Overdue tasks", overdueLines, 12);
                Section("Due in the next 7 days", soonLines, 10);
                Section("Returned tasks", returnedLines, 6);
                Section("Open user stories with no tasks", storiesWithoutTasks, 10);
                break;
        }

        return sb.ToString().TrimEnd();
    }

    private async Task RunBusyAsync(Func<Task> action)
    {
        _busy = true;
        BusyBar.Visibility = Visibility.Visible;
        ReportButtons.IsEnabled = false;
        AskBtn.IsEnabled = false;
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            ReportTitle.Text = "Report";
            ReportBox.Text = ex.Message;
            _ws?.Notify(ex.Message, true);
        }
        finally
        {
            _busy = false;
            BusyBar.Visibility = Visibility.Hidden;
            ReportButtons.IsEnabled = true;
            AskBtn.IsEnabled = true;
        }
    }
}
