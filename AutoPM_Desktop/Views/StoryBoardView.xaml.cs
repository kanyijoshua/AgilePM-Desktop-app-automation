using AutoPM_Desktop.Dialogs;
using AutoPM_Desktop.Models;
using AutoPM_Desktop.Services;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace AutoPM_Desktop.Views;

/// <summary>
/// The user stories assigned to the signed-in user in the active project (or all their projects), with the selected
/// story's tasks as a board: add a task in one line, let the AI suggest tasks for a story that has none, and move
/// tasks between stages by dragging a card or pressing its next-stage button.
/// </summary>
public partial class StoryBoardView : UserControl
{
    private static readonly Brush DropHighlight = new SolidColorBrush(Color.FromRgb(37, 99, 235));

    private readonly List<BoardColumn> _columns =
    [
        new(GeneralStagesEnum.Planned, "Planned", "#F59E0B"),
        new(GeneralStagesEnum.InProgress, "In Progress", "#2563EB"),
        new(GeneralStagesEnum.Returned, "Returned", "#EF4444"),
        new(GeneralStagesEnum.Completed, "Completed", "#10B981"),
    ];

    private AgilePmApiService? _api;
    private AiWorkspace? _ws;
    private readonly ObservableCollection<SuggestedTask> _suggestions = [];

    /// <summary>Name of the project folder the current suggestions were grounded in, or null.</summary>
    private string? _codeSource;
    private List<BoardStory> _allStories = [];
    private BoardStory? _selected;
    private Point _dragStart;
    private bool _busy;
    private int _loadVersion;

    public StoryBoardView()
    {
        InitializeComponent();
        ColumnsHost.ItemsSource = _columns;
        SuggestGrid.ItemsSource = _suggestions;
    }

    /// <summary>A message for the user (shown as a toast); true when it is a problem.</summary>
    public event Action<string, bool>? Notify;

    /// <summary>Raised after tasks were created or moved, so other views can refresh.</summary>
    public event Func<Task>? TasksChanged;

    /// <summary>When the stories were last loaded; null until the first load.</summary>
    public DateTime? LoadedAt { get; private set; }

    /// <summary>True when "All my projects" is ticked; otherwise the board follows the active project.</summary>
    public bool ShowsAllProjects => AllProjectsCheck.IsChecked == true;

    public void Initialize(AgilePmApiService api, AiWorkspace workspace)
    {
        _api = api;
        _ws = workspace;
        FolderBar.Initialize(workspace);
    }

    public void Clear()
    {
        _allStories = [];
        _selected = null;
        LoadedAt = null;
        StoryList.ItemsSource = null;
        foreach (var column in _columns) column.Cards.Clear();
        _suggestions.Clear();
        ShowBoard(false);
    }

    /// <summary>
    /// Loads the stories assigned to the user, with how many tasks each has, in the active project (or in each of
    /// <paramref name="myProjects"/> when "All my projects" is ticked), keeping the current selection.
    /// </summary>
    public async Task LoadAsync(IEnumerable<ProjectItem> myProjects)
    {
        if (_api is null) return;
        var loadVersion = ++_loadVersion;
        var active = _ws?.ActiveProject();
        var projects = (ShowsAllProjects || active is null ? myProjects : [active]).Where(p => p.GuidId is not null).ToList();
        StoriesSummaryText.Text = "Loading your user stories…";
        StoryLoadingOverlay.Visibility = Visibility.Visible;

        try
        {
            var perProject = await Task.WhenAll(projects.Select(async project =>
            {
                try
                {
                    // The project's tasks give each story's task count (one cached read, not one call per story).
                    var storiesTask = _api.ListAllUserStoriesAsync(project.GuidId!.Value, assignedToMe: true);
                    var tasksTask = _api.ListAllProjectTasksAsync(project.GuidId!.Value);
                    var stories = await storiesTask;
                    var counts = (await tasksTask)
                        .Where(t => t.Status != GeneralStagesEnum.Cancelled && !string.IsNullOrWhiteSpace(t.UserStoryId))
                        .GroupBy(t => t.UserStoryId, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
                    return stories.Select(s => new BoardStory(s, project) { TaskCount = counts.GetValueOrDefault(s.Id) }).ToList();
                }
                catch (AgilePmApiException)
                {
                    return []; // One project failing shouldn't hide the others; the API log shows the error.
                }
            }));
            if (loadVersion != _loadVersion) return;

            var previousId = _selected?.Story.Id;
            _allStories = perProject.SelectMany(s => s)
                .OrderBy(s => s.Story.Status is UserStoryStagesEnum.Completed or UserStoryStagesEnum.Closed)
                .ThenBy(s => s.Project.ProjectName)
                .ThenBy(s => s.Title)
                .ToList();
            LoadedAt = DateTime.Now;

            var open = _allStories.Count(s => s.Story.Status is not (UserStoryStagesEnum.Completed or UserStoryStagesEnum.Closed));
            var empty = _allStories.Count(s => s.TaskCount == 0);
            var where = projects.Count == 1 ? $"in {projects[0].ProjectName.Trim()}" : $"across {projects.Count} projects";
            StoriesSummaryText.Text = $"{_allStories.Count} assigned to you {where} ({open} open, {empty} without tasks)";
            ApplyStoryFilter();

            if (previousId is not null && _allStories.FirstOrDefault(s => s.Story.Id == previousId) is { } same)
            {
                StoryList.SelectedItem = same;
            }
        }
        finally
        {
            if (loadVersion == _loadVersion)
            {
                StoryLoadingOverlay.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void ApplyStoryFilter()
    {
        var search = StorySearchBox.Text.Trim();
        var noTasksOnly = NoTasksOnlyCheck.IsChecked == true;
        var visible = _allStories
            .Where(s => !noTasksOnly || s.TaskCount == 0)
            .Where(s => string.IsNullOrEmpty(search)
                        || s.Title.Contains(search, StringComparison.OrdinalIgnoreCase)
                        || s.Subtitle.Contains(search, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var selected = _selected;
        StoryList.ItemsSource = visible;
        if (selected is not null && visible.Contains(selected)) StoryList.SelectedItem = selected;

        NoStoriesText.Text = _allStories.Count == 0 ? "No user stories are assigned to you here yet."
            : noTasksOnly && string.IsNullOrEmpty(search) ? "Every story assigned to you has tasks."
            : "No stories match your search.";
        NoStoriesText.Visibility = visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void StorySearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyStoryFilter();

    private void StoryFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) ApplyStoryFilter();
    }

    private async void AllProjects_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded && Window.GetWindow(this) is MainWindow main) await LoadAsync(main.MyProjects());
    }

    private async void RefreshStories_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main) await LoadAsync(main.MyProjects());
    }

    private async void StoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (StoryList.SelectedItem is not BoardStory story || ReferenceEquals(story, _selected)) return;
        _selected = story;
        _suggestions.Clear();
        SuggestPanel.Visibility = Visibility.Collapsed;
        NextEmptyBtn.Visibility = Visibility.Collapsed;
        StoryTitleText.Text = story.Title;
        StorySubtitleText.Text = story.Subtitle;
        FolderBar.Project = story.Project;
        // Acceptance criteria arrive as HTML; show them as plain text in their own scrollable box.
        var criteria = TextTools.Plain(story.Story.AcceptanceCriteria, 4000);
        StoryCriteriaText.Text = criteria.Length == 0 ? "" : $"Acceptance criteria:\n{criteria}";
        StoryCriteriaScroll.Visibility = criteria.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        StoryCriteriaScroll.ScrollToTop();
        BoardTopScroll.ScrollToTop();
        ShowBoard(true);
        await LoadTasksAsync();
        NewTaskTitleBox.Focus();
    }

    /// <summary>Caps the area above the board so the board columns always keep at least some height.</summary>
    private void BoardPanel_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        const double minBoardHeight = 220;
        BoardTopScroll.MaxHeight = Math.Max(160, e.NewSize.Height - minBoardHeight);
    }

    /// <summary>
    /// Scrolls the suggestions grid with the wheel, and hands the wheel to the outer scroll area once the grid
    /// is at its top or bottom, so the page doesn't get stuck while the pointer is over the grid.
    /// </summary>
    private void SuggestGrid_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (FindChild<ScrollViewer>(SuggestGrid) is not { } inner) return;
        var atTop = inner.VerticalOffset <= 0;
        var atBottom = inner.VerticalOffset >= inner.ScrollableHeight;
        if ((e.Delta > 0 && !atTop) || (e.Delta < 0 && !atBottom)) return;

        e.Handled = true;
        BoardTopScroll.ScrollToVerticalOffset(BoardTopScroll.VerticalOffset - e.Delta);
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            if (FindChild<T>(child) is { } nested) return nested;
        }
        return null;
    }

    private async Task LoadTasksAsync()
    {
        if (_api is null || _selected is not { } story) return;
        await RunBusyAsync(async () =>
        {
            var tasks = await _api.ListAllProjectTasksAsync(story.Project.GuidId!.Value, story.Story.GuidId);
            if (!ReferenceEquals(story, _selected)) return; // the user picked another story meanwhile

            foreach (var column in _columns) column.Cards.Clear();
            foreach (var task in tasks.OrderBy(t => t.EndDateRaw ?? DateTime.MaxValue))
            {
                // Closed tasks sit with Completed; cancelled ones are left off the board.
                var stage = task.Status == GeneralStagesEnum.Closed ? GeneralStagesEnum.Completed : task.Status;
                _columns.FirstOrDefault(c => c.Stage == stage)?.Cards.Add(new BoardCard(task));
            }

            story.TaskCount = tasks.Count(t => t.Status != GeneralStagesEnum.Cancelled);
            NoTasksBanner.Visibility = story.TaskCount == 0 && _suggestions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        });
    }

    // ==================== AI-suggested tasks ====================

    private async void SuggestTasks_Click(object sender, RoutedEventArgs e)
    {
        if (_ws is null || _selected is not { } story || _busy) return;
        if (!_ws.EnsureAiReady()) return;

        var ai = _ws.Ai();
        var existing = _columns.SelectMany(c => c.Cards).Select(c => c.Task.Title).ToList();
        var folder = _ws.GetProjectFolder(story.Project.Id) is { } f && Directory.Exists(f) ? f : null;
        var contextChars = AssistantPrompts.CodeContextChars(ai.RequestBudgetTokens);
        SuggestTitleText.Text = folder is not null && contextChars > 0
            ? $"Reading {Path.GetFileName(folder.TrimEnd('\\', '/'))} and asking {ai.DisplayName} for tasks…"
            : $"Asking {ai.DisplayName} for tasks…";
        SuggestPanel.Visibility = Visibility.Visible;
        NoTasksBanner.Visibility = Visibility.Collapsed;
        _suggestions.Clear();
        _codeSource = null;

        IReadOnlyList<DraftTask> drafts = [];
        var ok = await RunBusyAsync(async () =>
        {
            // Descriptions are HTML (sometimes with pasted images); the model gets plain text.
            var description = TextTools.Plain(story.Story.Description, 1500);
            var criteria = TextTools.Plain(story.Story.AcceptanceCriteria, 1500);

            // The project's code folder (chosen in Quick update) grounds the tasks in the real code and git history.
            string? codeContext = null;
            if (folder is not null && contextChars > 0)
            {
                try
                {
                    codeContext = await ProjectFolderScanner.ReadCodeContextAsync(
                        folder, $"{story.Title}\n{story.Title}\n{description}\n{criteria}", contextChars, CancellationToken.None);
                    _codeSource = Path.GetFileName(folder.TrimEnd('\\', '/'));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException)
                {
                    Notify?.Invoke($"Couldn't read the project folder ({ex.Message}); suggesting from the story only.", true);
                }
            }

            drafts = await ai.DraftTasksForStoryAsync(story.Title, description, criteria, existing, codeContext, CancellationToken.None);
        });
        if (!ReferenceEquals(story, _selected)) return; // another story was picked meanwhile

        if (!ok || drafts.Count == 0)
        {
            SuggestPanel.Visibility = Visibility.Collapsed;
            NoTasksBanner.Visibility = story.TaskCount == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (ok) Notify?.Invoke("The AI didn't suggest any tasks. Add more detail to the story, or add tasks by hand.", true);
            return;
        }

        foreach (var d in drafts)
        {
            _suggestions.Add(new SuggestedTask
            {
                Title = d.Title.Trim(),
                Hours = Math.Clamp(Math.Round(d.Hours, 1), 0.5, 40),
                Priority = Enum.TryParse<PriorityEnum>(d.Priority, true, out var p) ? p : story.Story.Priority,
                Description = d.Description.Trim(),
            });
        }
        UpdateSuggestions();
    }

    private async void CreateSuggested_Click(object sender, RoutedEventArgs e)
    {
        if (_api is null || _selected is not { } story || _busy) return;
        SuggestGrid.CommitEdit(DataGridEditingUnit.Row, true);
        var picked = _suggestions.Where(t => t.Create && !string.IsNullOrWhiteSpace(t.Title)).ToList();
        if (picked.Count == 0) return;

        var due = NewTaskDuePicker.SelectedDate ?? DateTime.Today.AddDays(7);
        var created = 0;
        string? firstError = null;
        await RunBusyAsync(async () =>
        {
            foreach (var t in picked)
            {
                try
                {
                    await _api.CreateProjectTaskAsync(new CreateProjectTaskRequest(
                        story.Project.Id, t.Title.Trim(), string.IsNullOrWhiteSpace(t.Description) ? null : t.Description,
                        GeneralStagesEnum.Planned, t.Priority, PriorityEnum.Medium, _api.CurrentUser?.UserId?.ToString(),
                        Math.Max(0, t.Hours), due, string.IsNullOrWhiteSpace(story.Story.EpicId) ? null : story.Story.EpicId, story.Story.Id));
                    created++;
                    _suggestions.Remove(t);
                }
                catch (Exception ex)
                {
                    firstError ??= ex.Message;
                }
            }
        });

        Notify?.Invoke(firstError is null
            ? $"Created {created} task{(created == 1 ? "" : "s")} under '{TextTools.Cut(story.Title, 60)}'."
            : $"Created {created} of {picked.Count}. {firstError}", firstError is not null);
        _ws?.AddHistory("AI tasks for a story", $"{created} of {picked.Count} task(s) created", firstError is null);
        if (_suggestions.Count == 0) SuggestPanel.Visibility = Visibility.Collapsed;
        UpdateSuggestions();

        if (created > 0)
        {
            await LoadTasksAsync();
            ApplyStoryFilter();
            NextEmptyBtn.Visibility = NextStoryWithoutTasks() is null ? Visibility.Collapsed : Visibility.Visible;
            await RaiseTasksChangedAsync();
        }
    }

    private void DiscardSuggested_Click(object sender, RoutedEventArgs e)
    {
        _suggestions.Clear();
        SuggestPanel.Visibility = Visibility.Collapsed;
        NoTasksBanner.Visibility = _selected?.TaskCount == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SuggestCheck_Click(object sender, RoutedEventArgs e) => UpdateSuggestions();

    private void NextEmpty_Click(object sender, RoutedEventArgs e)
    {
        if (NextStoryWithoutTasks() is not { } next) return;
        if (!((IEnumerable<BoardStory>)StoryList.ItemsSource).Contains(next))
        {
            StorySearchBox.Clear(); // the search could hide it
            ApplyStoryFilter();
        }
        StoryList.SelectedItem = next;
        StoryList.ScrollIntoView(next);
    }

    /// <summary>The next open story (after the selected one, wrapping round) that has no tasks.</summary>
    private BoardStory? NextStoryWithoutTasks()
    {
        var candidates = _allStories.Where(s => s.TaskCount == 0 && !ReferenceEquals(s, _selected)
                                                && s.Story.Status is not (UserStoryStagesEnum.Completed or UserStoryStagesEnum.Closed)).ToList();
        var start = _selected is null ? -1 : _allStories.IndexOf(_selected);
        return candidates.FirstOrDefault(s => _allStories.IndexOf(s) > start) ?? candidates.FirstOrDefault();
    }

    private void UpdateSuggestions()
    {
        var picked = _suggestions.Count(t => t.Create);
        SuggestTitleText.Text = $"{_suggestions.Count} suggested task{(_suggestions.Count == 1 ? "" : "s")} • " +
                                $"{_suggestions.Where(t => t.Create).Sum(t => t.Hours):0.#} h selected" +
                                (_codeSource is null ? "" : $" • using code and git history from {_codeSource}");
        CreateSuggestedBtn.Content = picked > 0 ? $"Create {picked} task{(picked == 1 ? "" : "s")}" : "Create selected";
        CreateSuggestedBtn.IsEnabled = picked > 0 && !_busy;
    }

    // ==================== Quick add ====================

    private void NewTaskTitleBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        AddTask_Click(sender, e);
    }

    private async void AddTask_Click(object sender, RoutedEventArgs e)
    {
        if (_api is null || _selected is not { } story || _busy) return;

        var title = NewTaskTitleBox.Text.Trim();
        if (title.Length == 0)
        {
            Notify?.Invoke("Type a task title first.", true);
            NewTaskTitleBox.Focus();
            return;
        }

        var hoursText = NewTaskHoursBox.Text.Trim().Replace(',', '.');
        if (!double.TryParse(hoursText.Length == 0 ? "0" : hoursText, NumberStyles.Float, CultureInfo.InvariantCulture, out var hours) || hours < 0)
        {
            Notify?.Invoke("Hours must be a number, e.g. 2 or 1.5.", true);
            NewTaskHoursBox.Focus();
            return;
        }

        var request = new CreateProjectTaskRequest(
            story.Project.Id, title, null, GeneralStagesEnum.Planned, story.Story.Priority, PriorityEnum.Medium,
            _api.CurrentUser?.UserId?.ToString(), hours, NewTaskDuePicker.SelectedDate ?? DateTime.Today.AddDays(7),
            string.IsNullOrWhiteSpace(story.Story.EpicId) ? null : story.Story.EpicId, story.Story.Id);

        var created = await RunBusyAsync(() => _api.CreateProjectTaskAsync(request));
        if (!created) return;

        NewTaskTitleBox.Clear();
        NewTaskHoursBox.Clear();
        NewTaskDuePicker.SelectedDate = null;
        NewTaskTitleBox.Focus();
        Notify?.Invoke($"Task '{title}' added to the story.", false);
        await LoadTasksAsync();
        await RaiseTasksChangedAsync();
    }

    // ==================== Moving stages ====================

    private async void CardNext_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: BoardCard { Next: { } next } card }) await MoveAsync(card, next);
    }

    private void Card_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => _dragStart = e.GetPosition(this);

    private void Card_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _busy || sender is not FrameworkElement { Tag: BoardCard card } element) return;
        var delta = e.GetPosition(this) - _dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        element.Opacity = 0.5;
        try { DragDrop.DoDragDrop(element, new DataObject(typeof(BoardCard), card), DragDropEffects.Move); }
        finally { element.Opacity = 1; }
    }

    private void Column_DragEnter(object sender, DragEventArgs e) => UpdateDropTarget(sender, e, highlight: true);

    private void Column_DragOver(object sender, DragEventArgs e) => UpdateDropTarget(sender, e, highlight: true);

    private void Column_DragLeave(object sender, DragEventArgs e) => UpdateDropTarget(sender, e, highlight: false);

    private void UpdateDropTarget(object sender, DragEventArgs e, bool highlight)
    {
        var accepts = sender is Border { Tag: BoardColumn column } && e.Data.GetData(typeof(BoardCard)) is BoardCard card
                      && column.Stage != card.Column;
        e.Effects = accepts ? DragDropEffects.Move : DragDropEffects.None;
        if (sender is Border border) border.BorderBrush = highlight && accepts ? DropHighlight : Brushes.Transparent;
        e.Handled = true;
    }

    private async void Column_Drop(object sender, DragEventArgs e)
    {
        if (sender is not Border { Tag: BoardColumn column } border) return;
        border.BorderBrush = Brushes.Transparent;
        if (e.Data.GetData(typeof(BoardCard)) is BoardCard card && column.Stage != card.Column) await MoveAsync(card, column.Stage);
    }

    private async Task MoveAsync(BoardCard card, GeneralStagesEnum target)
    {
        if (_api is null || _busy || card.Task.GuidId is not { } taskId) return;

        var hours = 0.0;
        if (target == GeneralStagesEnum.Completed)
        {
            // Completing is when work gets logged, so ask for the hours (planned hours suggested).
            var dlg = new MoveStatusDialog(card.Task.Title, card.Task.Status, GeneralStagesEnum.Completed,
                card.Task.PlannedHours > 0 ? card.Task.PlannedHours : 1) { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() != true) return;
            target = dlg.SelectedStage;
            hours = dlg.LoggedHours;
        }

        // Show the card in its new column straight away; the reload below puts it right if the move failed.
        var from = _columns.FirstOrDefault(c => c.Cards.Contains(card));
        var to = _columns.FirstOrDefault(c => c.Stage == (target == GeneralStagesEnum.Closed ? GeneralStagesEnum.Completed : target));
        if (from is not null && to is not null && from != to)
        {
            from.Cards.Remove(card);
            to.Cards.Insert(0, card);
        }

        var moved = await RunBusyAsync(() => _api.MoveTaskStatusSmartAsync(taskId, target, hours, currentStatus: card.Task.Status));
        if (moved) Notify?.Invoke($"'{card.Task.Title}' moved to {Humanize(target)}.", false);
        await LoadTasksAsync();
        if (moved) await RaiseTasksChangedAsync();
    }

    // ==================== Helpers ====================

    private void ShowBoard(bool show)
    {
        BoardPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        BoardEmptyState.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Runs an Agile PM call with the progress bar showing; reports a failure and returns false.</summary>
    private async Task<bool> RunBusyAsync(Func<Task> action)
    {
        _busy = true;
        BusyBar.Visibility = Visibility.Visible;
        AddTaskBtn.IsEnabled = false;
        SuggestBtn.IsEnabled = false;
        CreateSuggestedBtn.IsEnabled = false;
        try
        {
            await action();
            return true;
        }
        catch (Exception ex)
        {
            Notify?.Invoke(ex.Message, true);
            return false;
        }
        finally
        {
            _busy = false;
            BusyBar.Visibility = Visibility.Hidden;
            AddTaskBtn.IsEnabled = true;
            SuggestBtn.IsEnabled = true;
            CreateSuggestedBtn.IsEnabled = _suggestions.Any(t => t.Create);
        }
    }

    private async Task RaiseTasksChangedAsync()
    {
        if (TasksChanged is { } handler) await handler();
    }

    internal static string Humanize(GeneralStagesEnum stage) => stage == GeneralStagesEnum.InProgress ? "In Progress" : stage.ToString();
}

/// <summary>A user story in the left-hand list, with the project it belongs to and how many tasks it has.</summary>
public sealed class BoardStory(UserStoryItem story, ProjectItem project) : INotifyPropertyChanged
{
    private static readonly Brush NoTasksBrush = new SolidColorBrush(Color.FromRgb(180, 83, 9));
    private static readonly Brush HasTasksBrush = new SolidColorBrush(Color.FromRgb(100, 116, 139));
    private int _taskCount;

    public event PropertyChangedEventHandler? PropertyChanged;

    public UserStoryItem Story { get; } = story;
    public ProjectItem Project { get; } = project;
    public string Title => Story.Story;

    public int TaskCount
    {
        get => _taskCount;
        set
        {
            if (_taskCount == value) return;
            _taskCount = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TaskCount)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TaskCountText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TaskCountBrush)));
        }
    }

    public string TaskCountText => TaskCount == 0 ? "No tasks yet" : $"{TaskCount} task{(TaskCount == 1 ? "" : "s")}";

    public Brush TaskCountBrush => TaskCount == 0 ? NoTasksBrush : HasTasksBrush;

    public string Subtitle => string.IsNullOrWhiteSpace(Story.EpicTitle)
        ? Project.ProjectName
        : $"{Project.ProjectName} • {Story.EpicTitle}";
}

/// <summary>One stage column of the board.</summary>
public sealed class BoardColumn(GeneralStagesEnum stage, string header, string accent)
{
    public GeneralStagesEnum Stage { get; } = stage;
    public string Header { get; } = header;
    public Brush Accent { get; } = (Brush)new BrushConverter().ConvertFromString(accent)!;
    public ObservableCollection<BoardCard> Cards { get; } = [];
}

/// <summary>A task card, with the one-click move that usually comes next.</summary>
public sealed class BoardCard(TaskItem task)
{
    public TaskItem Task { get; } = task;

    /// <summary>The column the card is in (closed tasks show under Completed).</summary>
    public GeneralStagesEnum Column => Task.Status == GeneralStagesEnum.Closed ? GeneralStagesEnum.Completed : Task.Status;

    public GeneralStagesEnum? Next => Task.Status switch
    {
        GeneralStagesEnum.Planned or GeneralStagesEnum.Returned => GeneralStagesEnum.InProgress,
        GeneralStagesEnum.InProgress => GeneralStagesEnum.Completed,
        _ => null,
    };

    public bool HasNext => Next is not null;

    public string NextLabel => Task.Status switch
    {
        GeneralStagesEnum.Planned => "▶ Start",
        GeneralStagesEnum.Returned => "↻ Rework",
        GeneralStagesEnum.InProgress => "✔ Complete",
        _ => "",
    };

    public string Meta => string.Join(" • ", new[]
    {
        Task.ActualHours > 0 ? $"{Task.ActualHours:0.#}/{Task.PlannedHours:0.#} h" : $"{Task.PlannedHours:0.#} h",
        Task.EndDateRaw is { } due ? $"due {due:MMM d}" : null,
        string.IsNullOrWhiteSpace(Task.AssigneeName) || Task.AssigneeName == "Unassigned" ? null : Task.AssigneeName,
        Task.IsPaused ? "⏸ paused" : null,
    }.Where(p => p is not null));
}

/// <summary>A task the AI suggested for a story; editable and tickable before it is created.</summary>
public sealed class SuggestedTask
{
    public bool Create { get; set; } = true;
    public string Title { get; set; } = "";
    public double Hours { get; set; }
    public PriorityEnum Priority { get; set; } = PriorityEnum.Medium;
    public string Description { get; set; } = "";
}
