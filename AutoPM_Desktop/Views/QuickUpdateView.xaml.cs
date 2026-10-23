using AutoPM_Desktop.Models;
using AutoPM_Desktop.Services;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace AutoPM_Desktop.Views;

/// <summary>
/// Natural-language updates: "finished X, log 3h; started Y" becomes a reviewed list of stage moves, hours and
/// comments on tasks in the active project. The update can also come from the project's code folder: recent commits
/// and changed files. One model call with a compact task list; nothing changes until Apply.
/// </summary>
public partial class QuickUpdateView : UserControl
{
    private readonly ObservableCollection<ProposedChange> _changes = [];
    private AiWorkspace? _ws;
    private bool _busy;

    /// <summary>True while the update text came from the project folder (a commit log rather than the user's words).</summary>
    private bool _fromFolder;

    public QuickUpdateView()
    {
        InitializeComponent();
        ActionsGrid.ItemsSource = _changes;
        UpdateBox.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                e.Handled = true;
                WorkOut_Click(this, e);
            }
        };
    }

    public void Initialize(AiWorkspace workspace)
    {
        _ws = workspace;
        // The folder can also be chosen from the sidebar, the story board or Describe → tasks.
        _ws.ProjectFolderChanged += _ => ShowFolder(_ws.ActiveProject());
    }

    public void OnProjectChanged(ProjectItem? project)
    {
        _changes.Clear();
        UpdateGrid();
        StatusText.Text = "";
        ScopeText.Text = project is null
            ? "Select an active project first."
            : $"Say what you did or want changed in {project.ProjectName.Trim()}, or read it from the project folder. The AI proposes the moves, hours and comments; you tick what to apply.";
        ShowFolder(project);
    }

    // ==================== Project folder ====================

    private string? CurrentFolder() => _ws?.ActiveProject() is { } p ? _ws.GetProjectFolder(p.Id) : null;

    private void ShowFolder(ProjectItem? project)
    {
        var folder = project is null ? null : _ws?.GetProjectFolder(project.Id);
        var exists = folder is not null && Directory.Exists(folder);
        FolderText.Text = folder is null
            ? "None chosen. Pick this project's code folder to turn your recent commits into task updates."
            : exists ? folder : $"{folder} (not found; choose it again)";
        FolderText.ToolTip = folder;
        ReadFolderBtn.IsEnabled = exists && !_busy;
    }

    private void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_ws?.ActiveProject() is not { } project)
        {
            _ws?.Notify("Select an active project first.", true);
            return;
        }

        ProjectFolderBar.Choose(_ws, project, Window.GetWindow(this)); // ProjectFolderChanged refreshes the text
    }

    private async void ReadFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_ws is null || _busy || CurrentFolder() is not { } folder) return;
        if (!_ws.EnsureAiReady()) return;

        var days = SinceCombo.SelectedItem is ComboBoxItem { Tag: string tag } && int.TryParse(tag, out var d) ? d : 1;
        var since = DateTime.Today.AddDays(-days);

        FolderActivity? activity = null;
        await RunBusyAsync("Reading the project folder…", async () => activity = await ProjectFolderScanner.ReadAsync(folder, since));
        if (activity is null) return;

        if (activity.Items == 0)
        {
            StatusText.Text = activity.FromGit
                ? "No commits or uncommitted changes of yours in that period. Pick a longer period, or type the update yourself."
                : "No files changed in that period. Pick a longer period, or type the update yourself.";
            return;
        }

        UpdateBox.Text = activity.Summary;
        _fromFolder = true;
        WorkOut_Click(sender, e);
    }

    private async void WorkOut_Click(object sender, RoutedEventArgs e)
    {
        if (_ws is null || _busy) return;
        var text = UpdateBox.Text.Trim();
        if (text.Length < 5)
        {
            _ws.Notify("Write what you did or want changed first.", true);
            UpdateBox.Focus();
            return;
        }
        if (!_ws.EnsureAiReady()) return;

        await RunBusyAsync("Loading the project's tasks…", async () =>
        {
            // Fresh data: stages must be current to move tasks correctly.
            var data = await _ws.LoadProjectAsync(refresh: true);
            var ai = _ws.Ai();

            var candidates = data.Tasks
                .Where(t => t.Status is not (GeneralStagesEnum.Closed or GeneralStagesEnum.Cancelled))
                .Where(t => OnlyMineCheck.IsChecked != true || _ws.IsMe(t.AssignTo))
                .OrderBy(t => t.Status switch
                {
                    GeneralStagesEnum.InProgress => 0,
                    GeneralStagesEnum.Returned => 1,
                    GeneralStagesEnum.Planned => 2,
                    _ => 3,
                })
                .ThenBy(t => t.EndDateRaw ?? DateTime.MaxValue)
                .ToList();
            if (candidates.Count == 0)
            {
                StatusText.Text = OnlyMineCheck.IsChecked == true
                    ? "You have no open tasks in this project. Untick \"Only my tasks\" to match any task."
                    : "This project has no open tasks.";
                return;
            }

            var (prompt, codes) = BuildPrompt(data, candidates, text, ai.RequestBudgetTokens, IsCodeLog(text));
            StatusText.Text = $"Asking {ai.DisplayName} to match your update to tasks…";
            var answer = await ai.CompleteAsync(prompt, Schema(), Math.Clamp(ai.RequestBudgetTokens / 5, 700, 2000), CancellationToken.None);

            using var doc = AssistantPrompts.ParseJsonObject(answer);
            _changes.Clear();
            if (doc.RootElement.TryGetProperty("actions", out var actions) && actions.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in actions.EnumerateArray())
                {
                    if (!codes.TryGetValue(Read(a, "task").Trim(), out var task)) continue;
                    var change = ProposedChange.From(task, Read(a, "action"), Read(a, "stage"),
                        a.TryGetProperty("hours", out var h) && h.ValueKind == JsonValueKind.Number ? h.GetDouble() : 0,
                        Read(a, "comment"), Read(a, "reason"));
                    if (change is not null) _changes.Add(change);
                }
            }

            var notes = doc.RootElement.TryGetProperty("unmatched", out var u) ? u.GetString() : null;
            UpdateGrid();
            StatusText.Text = _changes.Count == 0
                ? $"No matching changes found.{(string.IsNullOrWhiteSpace(notes) ? "" : $" {notes}")}"
                : $"{_changes.Count} change(s) proposed. Check them, adjust hours if needed, then Apply.{(string.IsNullOrWhiteSpace(notes) ? "" : $" Not matched: {notes}")}";
        });
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_ws is null || _busy) return;
        ActionsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        var selected = _changes.Where(c => c.Apply).ToList();
        if (selected.Count == 0) return;

        var done = 0;
        var failures = new List<string>();
        await RunBusyAsync($"Applying {selected.Count} change(s)…", async () =>
        {
            foreach (var change in selected)
            {
                try
                {
                    await change.ApplyAsync(_ws.Api);
                    done++;
                    _changes.Remove(change);
                }
                catch (Exception ex)
                {
                    change.Reason = $"Failed: {ex.Message}";
                    failures.Add(change.TaskTitle);
                }
            }
        });

        ActionsGrid.Items.Refresh();
        UpdateGrid();
        _ws.Invalidate();
        _ws.AddHistory("AI quick update", $"{done} of {selected.Count} change(s) applied", failures.Count == 0);
        StatusText.Text = failures.Count == 0 ? $"Applied {done} change(s)." : $"Applied {done}; {failures.Count} failed (see Why).";
        _ws.Notify(StatusText.Text, failures.Count > 0);
        if (done > 0)
        {
            if (failures.Count == 0)
            {
                UpdateBox.Clear();
                _fromFolder = false;
            }
            await _ws.DataChanged();
        }
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _fromFolder = false;
        _changes.Clear();
        UpdateGrid();
        StatusText.Text = "";
    }

    private void RowCheck_Click(object sender, RoutedEventArgs e) => UpdateGrid();

    private void UpdateGrid()
    {
        ActionsGrid.Visibility = _changes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Visibility = _changes.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        var selected = _changes.Count(c => c.Apply);
        ApplyBtn.IsEnabled = selected > 0 && !_busy;
        ApplyBtn.Content = selected > 0 ? $"Apply {selected} change{(selected == 1 ? "" : "s")}" : "Apply selected";
        SummaryText.Text = _changes.Count == 0 ? "" : $"{selected} of {_changes.Count} selected";
    }

    private static (string Prompt, Dictionary<string, TaskItem> Codes) BuildPrompt(ProjectData data, List<TaskItem> tasks, string update, int budgetTokens, bool isCodeLog = false)
    {
        var contextChars = Math.Max(800, (int)((budgetTokens - Math.Clamp(budgetTokens / 5, 700, 2000) - TextTools.EstimateTokens(update) - 450) * 3.3));
        var codes = new Dictionary<string, TaskItem>(StringComparer.OrdinalIgnoreCase);
        var lines = new StringBuilder();
        foreach (var t in tasks)
        {
            var code = $"T{codes.Count + 1}";
            var story = data.Story(t.UserStoryId)?.Story;
            var line = $"{code} | {TextTools.Cut(t.Title, 80)} | {t.Status} | {t.PlannedHours:0.#}h planned" +
                       (string.IsNullOrWhiteSpace(story) ? "" : $" | story: {TextTools.Cut(story, 50)}");
            if (lines.Length + line.Length + 1 > contextChars) break;
            lines.AppendLine(line);
            codes[code] = t;
        }

        var prompt = $$"""
            Match the user's update to tasks in the list and say which changes to make. Today is {{DateTime.Today:yyyy-MM-dd}}.
            Actions:
            - "move": change the stage. stage is one of Planned, InProgress, Returned, Completed. hours: hours worked to log with the move (0 if not said). Finished/done means Completed; started/working on means InProgress.
            - "comment": add a note to the task. comment is the text.
            - "pause" / "resume": pause or resume the work timer.
            Only use task codes from the list (e.g. T3). Skip anything you can't match and mention it in "unmatched". Don't invent changes the user didn't ask for.{{(isCodeLog ? CodeLogRules : "")}}
            Answer with JSON only: {"actions":[{"task":"T1","action":"move","stage":"Completed","hours":3,"comment":"","reason":"said they finished it"}],"unmatched":""}

            <tasks>
            {{lines.ToString().TrimEnd()}}
            </tasks>
            <update>
            {{update}}
            </update>
            """;
        return (prompt, codes);
    }

    /// <summary>The text is still the folder summary (the user may have replaced it with their own words).</summary>
    private bool IsCodeLog(string text) =>
        _fromFolder && (text.StartsWith("Code changes", StringComparison.Ordinal) || text.StartsWith("Files changed", StringComparison.Ordinal));

    /// <summary>Extra rules when the update is a commit log / file list read from the project folder.</summary>
    private const string CodeLogRules = """

        The update is a log of the user's own code changes (commit messages, changed files, uncommitted work), not a request.
        - Match a commit to a task only when its message or files clearly relate to the task title or story.
        - Work happened on a matched task: move a Planned or Returned task to InProgress. Move to Completed only when a commit says the work is finished, fixed, completed, closes or resolves it.
        - Add one "comment" per matched task listing its commit messages briefly (e.g. "Commits: add token refresh; fix expiry check").
        - hours: 0 (the user fills them in). Put commits that match no task in "unmatched" (short).
        """;

    private static Dictionary<string, JsonElement> Schema() => AssistantPrompts.ObjectSchema(new
    {
        actions = new
        {
            type = "array",
            items = new
            {
                type = "object",
                properties = new
                {
                    task = new { type = "string" },
                    action = new { type = "string", @enum = new[] { "move", "comment", "pause", "resume" } },
                    stage = new { type = "string", @enum = new[] { "Planned", "InProgress", "Returned", "Completed", "" } },
                    hours = new { type = "number" },
                    comment = new { type = "string" },
                    reason = new { type = "string" },
                },
                required = new[] { "task", "action", "stage", "hours", "comment", "reason" },
                additionalProperties = false,
            },
        },
        unmatched = new { type = "string" },
    }, "actions", "unmatched");

    private static string Read(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString()) : "";

    private async Task RunBusyAsync(string status, Func<Task> action)
    {
        _busy = true;
        BusyBar.Visibility = Visibility.Visible;
        WorkOutBtn.IsEnabled = false;
        ApplyBtn.IsEnabled = false;
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
            WorkOutBtn.IsEnabled = true;
            ReadFolderBtn.IsEnabled = CurrentFolder() is { } folder && Directory.Exists(folder);
            UpdateGrid();
        }
    }
}

/// <summary>One change the AI proposed for a task, editable (hours) and tickable before it is applied.</summary>
public sealed class ProposedChange
{
    private ProposedChange(TaskItem task) => Target = task;

    public TaskItem Target { get; }
    public bool Apply { get; set; } = true;
    public string Action { get; private init; } = "";
    public GeneralStagesEnum? Stage { get; private init; }
    public double Hours { get; set; }
    public string Comment { get; private init; } = "";
    public string Reason { get; set; } = "";

    public string TaskTitle => Target.Title;
    public string CurrentStage => StoryBoardView.Humanize(Target.Status);

    public string ChangeText => Action switch
    {
        "move" when Target.Status == GeneralStagesEnum.Planned && Stage == GeneralStagesEnum.Completed =>
            "Move to Completed (via In Progress)",
        "move" => $"Move to {StoryBoardView.Humanize(Stage!.Value)}",
        "comment" => $"Comment: {TextTools.Cut(Comment, 160)}",
        "pause" => "Pause the work timer",
        "resume" => "Resume the work timer",
        _ => Action,
    };

    /// <summary>Validates what the model proposed; returns null for changes that can't be applied.</summary>
    public static ProposedChange? From(TaskItem task, string action, string stage, double hours, string comment, string reason)
    {
        action = action.Trim().ToLowerInvariant();
        GeneralStagesEnum? target = Enum.TryParse<GeneralStagesEnum>(stage.Replace(" ", ""), true, out var s) ? s : null;
        if (task.GuidId is null) return null;
        if (action == "move" && (target is null || target == task.Status)) return null;
        if (action == "comment" && string.IsNullOrWhiteSpace(comment)) return null;
        if (action is not ("move" or "comment" or "pause" or "resume")) return null;

        return new ProposedChange(task)
        {
            Action = action,
            Stage = target,
            Hours = action == "move" ? Math.Max(0, Math.Round(hours, 2)) : 0,
            Comment = comment.Trim(),
            Reason = reason.Trim(),
        };
    }

    public async Task ApplyAsync(AgilePmApiService api)
    {
        var taskId = Target.GuidId!.Value;
        switch (Action)
        {
            case "move":
                await api.MoveTaskStatusSmartAsync(taskId, Stage!.Value, Hours, currentStatus: Target.Status);
                break;
            case "comment":
                await api.AddCommentAsync(new CreateCommentRequest(Comment, Target.Id, null, null, null));
                break;
            case "pause":
                await api.PauseTaskWorkAsync(taskId);
                break;
            case "resume":
                await api.ResumeTaskWorkAsync(taskId);
                break;
        }
    }
}
