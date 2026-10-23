using AutoPM_Desktop.Models;
using AutoPM_Desktop.Services;
using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace AutoPM_Desktop.Views;

/// <summary>
/// "Describe → tasks": the user describes work in plain words, the model proposes tasks matched to the active
/// project's user stories and people, and the user reviews and edits them before anything is created.
/// One model call with a compact list of stories, sized to the model's request budget.
/// </summary>
public partial class TaskPlannerView : UserControl
{
    private const string AssignMe = "me";
    private const string AssignAi = "ai";

    private readonly ObservableCollection<PlannedTask> _drafts = [];
    private AiWorkspace? _ws;
    private ProjectData? _data;
    private bool _busy;

    public TaskPlannerView()
    {
        InitializeComponent();
        DataContext = this;
        DraftGrid.ItemsSource = _drafts;
        DescriptionBox.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                e.Handled = true;
                Draft_Click(this, e);
            }
        };
    }

    public IReadOnlyList<PriorityEnum> Priorities { get; } = Enum.GetValues<PriorityEnum>();
    public List<Option> StoryOptions { get; private set; } = [];
    public List<Option> MemberOptions { get; private set; } = [];

    public void Initialize(AiWorkspace workspace) => _ws = workspace;

    /// <summary>Called when the active project changes: drafts for the old project no longer apply.</summary>
    public void OnProjectChanged(ProjectItem? project)
    {
        _data = null;
        _drafts.Clear();
        EpicCombo.ItemsSource = null;
        AssignModeCombo.ItemsSource = null;
        UpdateGrid();
        ScopeText.Text = project is null
            ? "Select an active project first."
            : $"Tasks are matched to the user stories of {project.ProjectName.Trim()}. Nothing is created until you press Create.";
        StatusText.Text = "";
    }

    /// <summary>Loads the project's stories and people for the pickers (once per project).</summary>
    private async Task<ProjectData> EnsureDataAsync()
    {
        var data = await _ws!.LoadProjectAsync();
        if (ReferenceEquals(data, _data) && EpicCombo.ItemsSource is not null) return data;
        _data = data;

        var epicChoices = new List<Option> { new("", "All open user stories") };
        epicChoices.AddRange(data.Epics.OrderBy(e => e.Title).Select(e => new Option(e.Id, e.Title)));
        var previousEpic = (EpicCombo.SelectedItem as Option)?.Id;
        EpicCombo.ItemsSource = epicChoices;
        EpicCombo.SelectedItem = epicChoices.FirstOrDefault(o => o.Id == previousEpic) ?? epicChoices[0];

        var modes = new List<Option> { new(AssignMe, "Me"), new(AssignAi, "Let AI suggest"), new("", "Unassigned") };
        modes.AddRange(data.Members.Where(m => !string.IsNullOrWhiteSpace(m.UserId)).Select(m => new Option(m.UserId, data.PersonName(m.UserId))));
        var previousMode = (AssignModeCombo.SelectedItem as Option)?.Id ?? AssignMe;
        AssignModeCombo.ItemsSource = modes;
        AssignModeCombo.SelectedItem = modes.FirstOrDefault(o => o.Id == previousMode) ?? modes[0];

        StoryOptions = [new("", "(no story)"), .. data.Stories.Select(s => new Option(s.Id, TextTools.Cut(s.Story, 70)))];
        MemberOptions = [new("", "Unassigned"), .. data.Names.Select(n => new Option(n.Key, n.Value)).OrderBy(o => o.Label)];
        return data;
    }

    private async void Draft_Click(object sender, RoutedEventArgs e)
    {
        if (_ws is null || _busy) return;
        var description = DescriptionBox.Text.Trim();
        if (description.Length < 8)
        {
            _ws.Notify("Describe the work first (a sentence or more).", true);
            DescriptionBox.Focus();
            return;
        }
        if (!_ws.EnsureAiReady()) return;
        if (!int.TryParse(DueDaysBox.Text.Trim(), out var dueDays) || dueDays < 0) dueDays = 7;

        await RunBusyAsync("Reading the project's user stories…", async () =>
        {
            var data = await EnsureDataAsync();
            var ai = _ws.Ai();
            var epicId = (EpicCombo.SelectedItem as Option)?.Id ?? "";
            var assignMode = (AssignModeCombo.SelectedItem as Option)?.Id ?? AssignMe;

            var (prompt, storyCodes, memberCodes) = BuildPrompt(data, description, epicId, assignMode == AssignAi, ai.RequestBudgetTokens);
            StatusText.Text = $"Asking {ai.DisplayName} to draft tasks…";
            var answer = await ai.CompleteAsync(prompt, Schema(), AnswerTokens(ai), CancellationToken.None);

            using var doc = AssistantPrompts.ParseJsonObject(answer);
            var existing = data.Tasks.Select(t => t.Title.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var defaultAssignee = assignMode switch
            {
                AssignMe => _ws.CurrentUserId?.ToString() ?? "",
                AssignAi => "",
                _ => assignMode,
            };

            _drafts.Clear();
            foreach (var t in doc.RootElement.TryGetProperty("tasks", out var tasks) && tasks.ValueKind == JsonValueKind.Array
                         ? tasks.EnumerateArray() : Enumerable.Empty<JsonElement>())
            {
                var title = Read(t, "title").Trim();
                if (title.Length == 0) continue;

                var storyId = storyCodes.GetValueOrDefault(Read(t, "story").Trim()) ?? "";
                var assignee = assignMode == AssignAi ? memberCodes.GetValueOrDefault(Read(t, "assignee").Trim()) ?? "" : defaultAssignee;
                var duplicate = existing.Contains(title);
                _drafts.Add(new PlannedTask
                {
                    Create = !duplicate,
                    Title = title,
                    Description = Read(t, "description").Trim(),
                    Hours = t.TryGetProperty("hours", out var h) && h.ValueKind == JsonValueKind.Number ? Math.Round(h.GetDouble(), 1) : 2,
                    Priority = Enum.TryParse<PriorityEnum>(Read(t, "priority"), true, out var p) ? p : PriorityEnum.Medium,
                    StoryId = storyId,
                    AssigneeId = assignee,
                    Due = DateTime.Today.AddDays(dueDays),
                    Note = duplicate ? "A task with this title already exists" : storyId.Length == 0 ? "No matching story" : "",
                });
            }

            DraftGrid.Items.Refresh();
            UpdateGrid();
            StatusText.Text = _drafts.Count == 0
                ? "The model returned no tasks. Add more detail and try again."
                : $"{ai.DisplayName} drafted {_drafts.Count} task(s). Review them, then press Create.";
        });
    }

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        if (_ws is null || _busy || _data is not { } data) return;
        DraftGrid.CommitEdit(DataGridEditingUnit.Row, true);
        var selected = _drafts.Where(d => d.Create && !string.IsNullOrWhiteSpace(d.Title)).ToList();
        if (selected.Count == 0)
        {
            _ws.Notify("Tick at least one task to create.", true);
            return;
        }

        var created = 0;
        var failures = new List<string>();
        await RunBusyAsync($"Creating {selected.Count} task(s) in {data.Project.ProjectName.Trim()}…", async () =>
        {
            foreach (var draft in selected)
            {
                var story = data.Story(draft.StoryId);
                var request = new CreateProjectTaskRequest(
                    data.Project.Id, draft.Title.Trim(), string.IsNullOrWhiteSpace(draft.Description) ? null : draft.Description,
                    GeneralStagesEnum.Planned, draft.Priority, draft.Priority,
                    string.IsNullOrWhiteSpace(draft.AssigneeId) ? null : draft.AssigneeId,
                    Math.Max(0, draft.Hours), draft.Due ?? DateTime.Today.AddDays(7),
                    string.IsNullOrWhiteSpace(story?.EpicId) ? null : story!.EpicId, story?.Id);
                try
                {
                    await _ws.Api.CreateProjectTaskAsync(request);
                    created++;
                    _drafts.Remove(draft);
                }
                catch (Exception ex)
                {
                    draft.Note = $"Failed: {ex.Message}";
                    failures.Add(draft.Title);
                }
            }
        });

        DraftGrid.Items.Refresh();
        UpdateGrid();
        _ws.Invalidate();
        _ws.AddHistory("AI task planner", $"{created} of {selected.Count} task(s) created in {data.Project.ProjectName.Trim()}", failures.Count == 0);
        StatusText.Text = failures.Count == 0
            ? $"Created {created} task(s) in {data.Project.ProjectName.Trim()}."
            : $"Created {created}; {failures.Count} failed (see the Note column).";
        _ws.Notify(StatusText.Text, failures.Count > 0);
        if (created > 0) await _ws.DataChanged();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _drafts.Clear();
        UpdateGrid();
        StatusText.Text = "";
    }

    private void RowCheck_Click(object sender, RoutedEventArgs e) => UpdateGrid();

    private void DraftGrid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e) =>
        Dispatcher.BeginInvoke(UpdateGrid);

    private void UpdateGrid()
    {
        DraftGrid.Visibility = _drafts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Visibility = _drafts.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        var selected = _drafts.Where(d => d.Create).ToList();
        CreateBtn.IsEnabled = selected.Count > 0 && !_busy;
        CreateBtn.Content = selected.Count > 0 ? $"Create {selected.Count} task{(selected.Count == 1 ? "" : "s")}" : "Create selected tasks";
        SummaryText.Text = _drafts.Count == 0 ? "" : $"{selected.Count} of {_drafts.Count} selected • {selected.Sum(d => d.Hours):0.#} h planned";
    }

    /// <summary>
    /// The prompt, plus the short codes (S1, M1) used for stories and people so the model doesn't copy GUIDs.
    /// The story list is cut to fit the model's request budget: open stories first, the user's own first.
    /// </summary>
    private (string Prompt, Dictionary<string, string> Stories, Dictionary<string, string> Members) BuildPrompt(
        ProjectData data, string description, string epicId, bool suggestAssignees, int budgetTokens)
    {
        var contextChars = (int)((budgetTokens - AnswerTokens(budgetTokens) - TextTools.EstimateTokens(description) - 450) * 3.3);
        contextChars = Math.Max(800, contextChars);

        var stories = data.Stories
            .Where(s => epicId.Length == 0 || string.Equals(s.EpicId, epicId, StringComparison.OrdinalIgnoreCase))
            .Where(s => epicId.Length > 0 || s.Status is UserStoryStagesEnum.Planned or UserStoryStagesEnum.In_Progress)
            .OrderByDescending(s => s.AssignedUserIds.Any(_ws!.IsMe))
            .ThenBy(s => s.Status)
            .ToList();

        var storyCodes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var storyLines = new StringBuilder();
        var storyBudget = suggestAssignees ? contextChars * 3 / 4 : contextChars;
        foreach (var s in stories)
        {
            var code = $"S{storyCodes.Count + 1}";
            var epic = data.Epic(s.EpicId)?.Title ?? s.EpicTitle;
            var line = $"{code} | {TextTools.Cut(s.Story, 90)}{(string.IsNullOrWhiteSpace(epic) ? "" : $" | epic: {TextTools.Cut(epic, 40)}")}";
            if (storyLines.Length + line.Length + 1 > storyBudget) break;
            storyLines.AppendLine(line);
            storyCodes[code] = s.Id;
        }

        var memberCodes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var memberLines = new StringBuilder();
        if (suggestAssignees)
        {
            foreach (var m in data.Members.Where(m => !string.IsNullOrWhiteSpace(m.UserId)))
            {
                var code = $"M{memberCodes.Count + 1}";
                var open = data.Tasks.Count(t => t.IsOpen && string.Equals(t.AssignTo, m.UserId, StringComparison.OrdinalIgnoreCase));
                var line = $"{code} | {data.PersonName(m.UserId)} | {m.Role} | {open} open tasks";
                if (memberLines.Length + line.Length + 1 > contextChars / 4) break;
                memberLines.AppendLine(line);
                memberCodes[code] = m.UserId;
            }
        }

        var prompt = $$"""
            Turn the work description into implementation tasks for the Agile PM project "{{data.Project.ProjectName.Trim()}}".
            Rules:
            - 1 to 15 tasks, each concrete work one person can finish in 0.5 to 16 hours. Title: short and action-first.
            - description: one or two sentences.
            - story: the code (e.g. S3) of the user story the task belongs to, from the list below; "" if none fits. Only use listed codes.
            - assignee: {{(suggestAssignees ? "the code (e.g. M2) of the best-suited person from the team list (prefer lighter workloads); \"\" if unsure." : "always \"\".")}}
            - priority: Low, Medium or High. hours: a realistic estimate.
            Answer with JSON only: {"tasks":[{"title":"...","description":"...","hours":2,"priority":"Medium","story":"S1","assignee":""}]}

            <user_stories>
            {{(storyLines.Length == 0 ? "(none)" : storyLines.ToString().TrimEnd())}}
            </user_stories>
            {{(suggestAssignees ? $"<team>\n{memberLines.ToString().TrimEnd()}\n</team>\n" : "")}}<work_description>
            {{description}}
            </work_description>
            """;
        return (prompt, storyCodes, memberCodes);
    }

    private static Dictionary<string, JsonElement> Schema() => AssistantPrompts.ObjectSchema(new
    {
        tasks = new
        {
            type = "array",
            items = new
            {
                type = "object",
                properties = new
                {
                    title = new { type = "string" },
                    description = new { type = "string" },
                    hours = new { type = "number" },
                    priority = new { type = "string", @enum = new[] { "Low", "Medium", "High" } },
                    story = new { type = "string" },
                    assignee = new { type = "string" },
                },
                required = new[] { "title", "description", "hours", "priority", "story", "assignee" },
                additionalProperties = false,
            },
        },
    }, "tasks");

    private static int AnswerTokens(IAiAssistant ai) => AnswerTokens(ai.RequestBudgetTokens);

    private static int AnswerTokens(int budget) => Math.Clamp(budget / 4, 900, 3000);

    private static string Read(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString()) : "";

    private async Task RunBusyAsync(string status, Func<Task> action)
    {
        _busy = true;
        BusyBar.Visibility = Visibility.Visible;
        DraftBtn.IsEnabled = false;
        CreateBtn.IsEnabled = false;
        StatusText.Text = status;
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
            _ws?.Notify(ex.Message, true);
        }
        finally
        {
            _busy = false;
            BusyBar.Visibility = Visibility.Hidden;
            DraftBtn.IsEnabled = true;
            UpdateGrid();
        }
    }
}

/// <summary>A choice in a combo box: an ID and what the user sees.</summary>
public sealed record Option(string Id, string Label);

/// <summary>A task drafted by the AI, editable in the review grid.</summary>
public sealed class PlannedTask
{
    public bool Create { get; set; } = true;
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public double Hours { get; set; }
    public PriorityEnum Priority { get; set; } = PriorityEnum.Medium;
    public string StoryId { get; set; } = "";
    public string AssigneeId { get; set; } = "";
    public DateTime? Due { get; set; }
    public string Note { get; set; } = "";

    public override string ToString() => Title;
}
