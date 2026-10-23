using AutoPM_Desktop.Dialogs;
using AutoPM_Desktop.Models;
using AutoPM_Desktop.Services;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace AutoPM_Desktop;

public partial class MainWindow : Window
{
    private readonly AgilePmApiService _apiService;
    private readonly AgilePmToolbox _tools;
    private readonly AiWorkspace _aiWorkspace;
    private bool _syncingProjectPickers;
    private readonly AiAssistantService _claude = new();
    private readonly LocalModelAssistantService _local = new();
    private readonly DispatcherTimer _autoRefreshTimer = new();
    private AppUserSettings _userSettings;

    // Cache collections for current session
    private readonly BulkObservableCollection<ProjectItem> _projects = [];
    private readonly BulkObservableCollection<TeamMemberItem> _teamMembers = [];
    private readonly BulkObservableCollection<EpicItem> _epics = [];
    private readonly BulkObservableCollection<UserStoryItem> _stories = [];
    private readonly BulkObservableCollection<TaskItem> _projectTasks = [];
    private readonly BulkObservableCollection<TaskItem> _myWorkTasks = [];
    private readonly BulkObservableCollection<SubTaskItem> _subTasks = [];
    private readonly BulkObservableCollection<CommentItem> _comments = [];
    private readonly ObservableCollection<ChatMessage> _chat = [];
    private readonly ObservableCollection<AutomationRunEntry> _automationHistory = [];

    // Filtered lists
    private List<TaskItem> _allRawMyWork = [];
    private List<ProjectItem> _allRawProjects = [];
    private List<TaskItem> _allRawProjectTasks = [];

    private ProjectItem? _activeProject;
    private TaskItem? _selectedTask;
    private UserStoryItem? _selectedStory;
    private EpicItem? _selectedEpic;
    private bool _isInitialized;
    private int? _lastOverdueCount;
    private CancellationTokenSource? _aiCts;
    private bool _isSidebarMinimized;

    /// <summary>The model backend picked in the AI Assistant panel.</summary>
    private IAiAssistant _ai => _userSettings.AiProvider == AiProviderKind.Local ? _local : _claude;

    public MainWindow()
    {
        InitializeComponent();

        _userSettings = LocalSettingsService.Load();
        _apiService = new AgilePmApiService(_userSettings.ServerUrl);
        _apiService.OnApiLog += QueueApiLog;
        _tools = new AgilePmToolbox(_apiService);
        _tools.OnLog += QueueApiLog;
        MyStoryBoard.Notify += (message, isProblem) => ShowToast(message, isProblem ? ToastKind.Warning : ToastKind.Success);
        MyStoryBoard.TasksChanged += AfterTaskChangesAsync;

        // AI workspace: everything works on the active project.
        _tools.ActiveProject = () => _activeProject;
        _tools.UserNames = ActiveProjectNames;
        _aiWorkspace = new AiWorkspace
        {
            Api = _apiService,
            Ai = () => _ai,
            EnsureAiReady = EnsureAiKey,
            ActiveProject = () => _activeProject,
            Notify = (message, isProblem) => ShowToast(message, isProblem ? ToastKind.Warning : ToastKind.Success),
            DataChanged = AfterTaskChangesAsync,
            AddHistory = AddAutomationHistory,
            GetProjectFolder = id => _userSettings.ProjectFolders.TryGetValue(id, out var path) && path.Length > 0 ? path : null,
            SetProjectFolder = (id, path) =>
            {
                if (path.Length == 0) _userSettings.ProjectFolders.Remove(id);
                else _userSettings.ProjectFolders[id] = path;
                LocalSettingsService.Save(_userSettings);
            },
        };
        MyStoryBoard.Initialize(_apiService, _aiWorkspace);
        TaskPlanner.Initialize(_aiWorkspace);
        QuickUpdate.Initialize(_aiWorkspace);
        ProjectAnalysis.Initialize(_aiWorkspace);
        _aiWorkspace.ProjectFolderChanged += _ => ShowSidebarFolder();

        LoginUrlBox.Text = _userSettings.ServerUrl;
        LoginEmailBox.Text = _userSettings.Email;

        // Bind DataGrids
        MyWorkGrid.ItemsSource = _myWorkTasks;
        ProjectsGrid.ItemsSource = _projects;
        TeamMembersGrid.ItemsSource = _teamMembers;
        EpicsGrid.ItemsSource = _epics;
        StoriesGrid.ItemsSource = _stories;
        ProjectTasksGrid.ItemsSource = _projectTasks;
        SubTasksGrid.ItemsSource = _subTasks;
        CommentsList.ItemsSource = _comments;
        AiChatList.ItemsSource = _chat;
        AutomationHistoryList.ItemsSource = _automationHistory;

        // AI assistant settings
        _claude.Configure(_userSettings.AnthropicApiKey);
        AiAutoApproveCheck.IsChecked = _userSettings.AiAutoApproveWrites;
        RepairOpenModelUrl();
        LocalAiUrlBox.Text = _userSettings.LocalAiBaseUrl;
        LocalAiModelCombo.Text = _userSettings.LocalAiModel;
        LocalAiAllToolsCheck.IsChecked = _userSettings.LocalAiAllTools;
        OpenModelPresetCombo.ItemsSource = OpenModelPresets.All;
        OpenModelPresetCombo.SelectedItem = CurrentOpenModelPreset();
        UpdateOpenModelPresetUi();
        ConfigureOpenModel();
        AiProviderCombo.SelectedIndex = _userSettings.AiProvider == AiProviderKind.Local ? 1 : 0;
        UpdateAiProviderUi();
        UpdateAiKeyStatus();
        UpdateToolsStatus();

        _autoRefreshTimer.Tick += async (_, _) => await RefreshAllData(background: true);
        SelectAutoRefreshItem(_userSettings.AutoRefreshMinutes);

        _isInitialized = true;
        AttemptAutoSignIn();
    }

    // ==================== AUTHENTICATION & STARTUP ====================

    private async void AttemptAutoSignIn()
    {
        if (_userSettings.RememberSession && !string.IsNullOrWhiteSpace(_userSettings.SavedAccessToken))
        {
            SetBusy(true, "Restoring session...");
            try
            {
                _apiService.SetSessionToken(_userSettings.SavedAccessToken, _userSettings.SavedRefreshToken);
                // Test token with quick call
                await _apiService.GetDashboardSummaryAsync();
                OnSignInSuccess();
            }
            catch
            {
                // Token expired or invalid; show sign in
                _apiService.SignOut();
                ShowLoginOverlay(true);
            }
            finally
            {
                SetBusy(false);
            }
        }
        else
        {
            ShowLoginOverlay(true);
        }
    }

    private async void LoginSubmit_Click(object sender, RoutedEventArgs e)
    {
        var url = LoginUrlBox.Text.Trim();
        var email = LoginEmailBox.Text.Trim();
        var password = LoginPasswordBox.Password;

        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            ShowLoginError("Please enter your Agile PM account email and password.");
            return;
        }

        HideLoginError();
        SetBusy(true, "Signing in to Agile PM...");
        LoginSubmitBtn.IsEnabled = false;

        try
        {
            _apiService.UpdateServerUrl(url);
            await _apiService.LoginAsync(email, password);

            // Save settings
            _userSettings.ServerUrl = url;
            _userSettings.Email = email;
            _userSettings.RememberSession = RememberSessionCheck.IsChecked == true;
            if (_userSettings.RememberSession)
            {
                _userSettings.SavedAccessToken = _apiService.AccessToken;
                _userSettings.SavedRefreshToken = _apiService.RefreshTokenValue;
            }
            else
            {
                _userSettings.SavedAccessToken = null;
                _userSettings.SavedRefreshToken = null;
            }
            LocalSettingsService.Save(_userSettings);

            OnSignInSuccess();
        }
        catch (Exception ex)
        {
            ShowLoginError(ex.Message);
        }
        finally
        {
            SetBusy(false);
            LoginSubmitBtn.IsEnabled = true;
        }
    }

    private async void UseBearerToken_Click(object sender, RoutedEventArgs e)
    {
        var token = DirectTokenBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            ShowLoginError("Please paste a valid JWT access token.");
            return;
        }

        HideLoginError();
        SetBusy(true, "Verifying token...");

        try
        {
            _apiService.UpdateServerUrl(LoginUrlBox.Text.Trim());
            _apiService.SetSessionToken(token);
            await _apiService.GetDashboardSummaryAsync();

            _userSettings.ServerUrl = LoginUrlBox.Text.Trim();
            _userSettings.SavedAccessToken = token;
            _userSettings.RememberSession = RememberSessionCheck.IsChecked == true;
            LocalSettingsService.Save(_userSettings);

            OnSignInSuccess();
        }
        catch (Exception ex)
        {
            ShowLoginError($"Token rejected: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void LoginPasswordBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) LoginSubmit_Click(sender, e);
    }

    private async void OnSignInSuccess()
    {
        ShowLoginOverlay(false);
        var user = _apiService.CurrentUser;
        SidebarUserText.Text = user?.PrimaryEmail ?? "Signed In";
        SidebarUrlText.Text = _apiService.ApiBaseUrl;
        StatusBarText.Text = $"Signed in as {user?.PrimaryEmail} • Connected to {_apiService.ApiBaseUrl}";

        if (user?.ExpiresAt is { } exp)
        {
            TokenExpiryText.Text = $"Token expires: {exp.ToLocalTime():yyyy-MM-dd HH:mm}";
        }
        else
        {
            TokenExpiryText.Text = "Session: Active";
        }

        await RefreshAllData();
    }

    private void SignOut_Click(object sender, RoutedEventArgs e)
    {
        _aiCts?.Cancel();
        _apiService.SignOut();
        _userSettings.SavedAccessToken = null;
        _userSettings.SavedRefreshToken = null;
        LocalSettingsService.Save(_userSettings);

        _projects.Clear();
        _epics.Clear();
        _stories.Clear();
        _projectTasks.Clear();
        _myWorkTasks.Clear();
        _subTasks.Clear();
        _comments.Clear();
        _chat.Clear();
        MyStoryBoard.Clear();
        _activeProject = null;
        OnAiProjectChanged(null);
        _claude.ResetConversation();
        _local.ResetConversation();
        _lastOverdueCount = null;
        UpdateAiEmptyState();

        ShowLoginOverlay(true);
    }

    private void ShowLoginOverlay(bool show)
    {
        LoginOverlay.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        WorkspaceGrid.IsEnabled = !show;
    }

    private void ShowLoginError(string msg)
    {
        LoginErrorText.Text = msg;
        LoginErrorBorder.Visibility = Visibility.Visible;
    }

    private void HideLoginError()
    {
        LoginErrorBorder.Visibility = Visibility.Collapsed;
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _aiCts?.Cancel();
        _autoRefreshTimer.Stop();
    }

    // ==================== GLOBAL REFRESH & DATA LOADING ====================

    private async void RefreshAll_Click(object sender, RoutedEventArgs e) => await RefreshAllData();

    private async void RefreshShortcut_Executed(object sender, ExecutedRoutedEventArgs e) => await RefreshAllData();

    private async Task RefreshAllData(bool background = false)
    {
        if (!_apiService.IsAuthenticated) return;
        SetBusy(true, background ? "Syncing Agile PM data..." : "Refreshing Agile PM data...", showWaitCursor: !background);

        // A refresh always reads from Agile PM, not from the short-lived response cache.
        _apiService.ClearCache();
        _aiWorkspace.Invalidate();

        try
        {
            // My work and the dashboard load while the projects do.
            var dashboardTask = LoadDashboardAndMyWorkAsync();

            // 1. Load Projects
            var onlyMy = OnlyMyProjectsCheck.IsChecked == true;
            _allRawProjects = await _apiService.GetProjectsAsync(onlyMyProjects: false); // Load all to let user toggle freely

            _projects.ReplaceAll(_allRawProjects.Where(p => !onlyMy || !string.IsNullOrWhiteSpace(p.MyRole)));

            // Populate Global Project ComboBox
            var prevSelectedId = _activeProject?.Id;
            GlobalProjectCombo.ItemsSource = null;
            GlobalProjectCombo.ItemsSource = _projects;
            _syncingProjectPickers = true;
            AiProjectCombo.ItemsSource = _projects;
            _syncingProjectPickers = false;

            if (!string.IsNullOrWhiteSpace(prevSelectedId) && _projects.FirstOrDefault(x => x.Id == prevSelectedId) is { } matched)
            {
                GlobalProjectCombo.SelectedItem = matched;
            }
            else if (_projects.Count > 0)
            {
                GlobalProjectCombo.SelectedIndex = 0;
            }

            // 2. My Work & Dashboard (already loading)
            await dashboardTask;
            LastSyncText.Text = $"Last sync {DateTime.Now:HH:mm}";
        }
        catch (Exception ex)
        {
            if (background)
                AppendApiLog($"Background sync failed: {ex.Message}");
            else
                MessageBox.Show($"Refresh failed: {ex.Message}", "Agile PM Sync Error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task LoadDashboardAndMyWorkAsync()
    {
        try
        {
            var dashTask = _apiService.GetDashboardSummaryAsync();
            ObserveInBackground(dashTask);
            _allRawMyWork = await _apiService.GetMyWorkAsync(includeCompleted: true);

            ShowMyWork();

            // Alert when new tasks have gone overdue since the last sync (in any project: late work matters wherever it is).
            var overdueAnywhere = _allRawMyWork.Count(t => t.IsOverdue);
            if (_lastOverdueCount is { } previous && overdueAnywhere > previous)
            {
                ShowToast($"{overdueAnywhere - previous} more task(s) are now overdue.", ToastKind.Warning);
            }
            _lastOverdueCount = overdueAnywhere;

            // Try dashboard summary endpoint
            var dash = await dashTask;
            if (dash is { } d && d.ValueKind == JsonValueKind.Object)
            {
                StatDashboardRaw.Text = "Open work • live Agile PM data";
            }
        }
        catch (Exception ex)
        {
            AppendApiLog($"Dashboard load note: {ex.Message}");
        }
    }

    /// <summary>
    /// The user's tasks the dashboard, standup and plan-my-day work on: the active project's, unless "All projects"
    /// is ticked (or no project is selected).
    /// </summary>
    private List<TaskItem> ScopedMyWork() =>
        MyWorkAllProjectsCheck.IsChecked == true || _activeProject is null
            ? _allRawMyWork
            : _allRawMyWork.Where(t => string.Equals(t.ProjectId, _activeProject.Id, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>Fills the My Work grid and the KPI tiles for the current scope.</summary>
    private void ShowMyWork()
    {
        ApplyMyWorkFilters();

        var work = ScopedMyWork();
        StatTotalTasks.Text = work.Count.ToString();
        StatInProgress.Text = work.Count(t => t.Status == GeneralStagesEnum.InProgress).ToString();
        StatPlanned.Text = work.Count(t => t.Status == GeneralStagesEnum.Planned).ToString();
        StatCompleted.Text = work.Count(t => t.Status is GeneralStagesEnum.Completed or GeneralStagesEnum.Closed).ToString();
        StatPlannedHours.Text = $"{work.Where(t => t.IsOpen).Sum(t => t.PlannedHours):0.#} hrs";

        var overdue = work.Where(t => t.IsOverdue).ToList();
        StatOverdue.Text = overdue.Count.ToString();
        StatOverdueHint.Text = overdue.Count == 0
            ? "Nothing late"
            : $"Oldest due {overdue.Min(t => t.EndDateRaw)!.Value:MMM d}";

        if (MyWorkAllProjectsCheck.IsChecked == true || _activeProject is null)
        {
            var projectCount = work.Select(t => t.ProjectName).Where(p => !string.IsNullOrWhiteSpace(p)).Distinct().Count();
            StatProjectsCount.Text = $"Across {projectCount} project{(projectCount == 1 ? "" : "s")}";
        }
        else
        {
            var elsewhere = _allRawMyWork.Count - work.Count;
            StatProjectsCount.Text = $"In {_activeProject.ProjectName.Trim()}{(elsewhere > 0 ? $" • {elsewhere} more in other projects" : "")}";
        }
    }

    private void MyWorkScope_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitialized) ShowMyWork();
    }

    /// <summary>
    /// Bumped on every project switch. Loads check it after each await, so a slow load for the previous project
    /// can't overwrite the lists of the one now selected.
    /// </summary>
    private int _projectLoadVersion;

    /// <summary>True while the code itself changes filter controls, so their change handlers don't reload again.</summary>
    private bool _updatingFilters;

    private bool IsCurrentLoad(int version, string projectId) => version == _projectLoadVersion && _activeProject?.Id == projectId;

    private async Task LoadActiveProjectDetailsAsync()
    {
        if (_activeProject is null || _activeProject.GuidId is null) return;
        var project = _activeProject;
        var pId = project.GuidId.Value;
        var version = ++_projectLoadVersion;

        SelectedProjectTitle.Text = project.ProjectName;
        SelectedProjectMeta.Text = $"Role: {project.MyRole} • PM: {project.ProjectManagerName} • SM: {project.ScrumMasterName} • Tasks: {project.TotalNumberOfTasks} • End Date: {project.EndDate}";

        SetBusy(true, $"Loading {project.ProjectName}...");
        try
        {
            // Team, epics, stories and tasks are fetched at the same time; the lists are swapped in as they arrive.
            var fullProjectTask = _apiService.GetProjectByIdAsync(pId);
            var epicsTask = _apiService.ListEpicsAsync(pId, pageSize: 100);
            var storiesTask = FetchUserStoriesAsync();
            var tasksTask = _apiService.ListAllProjectTasksAsync(pId);
            ObserveInBackground(epicsTask, storiesTask, tasksTask);
            var epicFilterBefore = StoriesFilterByEpicCheck.IsChecked == true ? _selectedEpic?.Id : null;

            var fullProject = await fullProjectTask;
            var epics = await epicsTask;
            if (!IsCurrentLoad(version, project.Id)) return;

            Replace(_teamMembers, fullProject.TeamMembers);
            _updatingFilters = true;
            try
            {
                Replace(_epics, epics);
                // Keep the selected epic when it still exists in this project (e.g. after editing it).
                _selectedEpic = _selectedEpic is { } epic ? _epics.FirstOrDefault(x => x.Id == epic.Id) : null;
                EpicsGrid.SelectedItem = _selectedEpic;
            }
            finally
            {
                _updatingFilters = false;
            }
            BatchEpicCombo.ItemsSource = _epics;
            BatchAssigneeCombo.ItemsSource = _teamMembers;

            // The stories were requested for an epic that is gone now: ask again without it.
            if (epicFilterBefore is not null && _selectedEpic is null) storiesTask = FetchUserStoriesAsync();

            await LoadUserStoriesAsync(version, storiesTask);
            await LoadProjectTasksAsync(version, tasksTask);
        }
        catch (Exception ex)
        {
            AppendApiLog($"Error loading project details: {ex.Message}");
        }
        finally
        {
            if (version == _projectLoadVersion) SetBusy(false);
            UpdateAiContextPreview();
        }
    }

    /// <summary>
    /// Loads the active project's user stories, narrowed by the selected epic and "Assigned to Me" when those
    /// filters are on. Reads every page; a project can have hundreds of stories.
    /// </summary>
    private async Task LoadUserStoriesAsync(int? version = null, Task<List<UserStoryItem>>? prefetched = null)
    {
        if (_activeProject?.GuidId is null) return;
        var projectId = _activeProject.Id;
        var loadVersion = version ?? _projectLoadVersion;

        var stories = await (prefetched ?? FetchUserStoriesAsync());
        if (!IsCurrentLoad(loadVersion, projectId)) return;

        _updatingFilters = true;
        try
        {
            Replace(_stories, stories);
            BatchStoryCombo.ItemsSource = _stories;

            // Keep the selected story when it is still in the list; otherwise clear it.
            _selectedStory = _selectedStory is { } story ? _stories.FirstOrDefault(s => s.Id == story.Id) : null;
            StoriesGrid.SelectedItem = _selectedStory;
            ShowSelectedStory();

            RebuildTaskStoryFilter();
        }
        finally
        {
            _updatingFilters = false;
        }

        ApplyProjectTaskFilters();
    }

    /// <summary>The active project's stories with the current "Filter by Selected Epic" and "Assigned to Me" settings.</summary>
    private Task<List<UserStoryItem>> FetchUserStoriesAsync()
    {
        if (_activeProject?.GuidId is not { } projectId) return Task.FromResult(new List<UserStoryItem>());
        var assignedToMe = StoriesAssignedToMeCheck.IsChecked == true;
        Guid? epicFilter = StoriesFilterByEpicCheck.IsChecked == true ? _selectedEpic?.GuidId : null;
        return _apiService.ListAllUserStoriesAsync(projectId, epicFilter, assignedToMe);
    }

    /// <summary>Marks started requests as observed, so one that fails after an earlier await threw isn't left unobserved.</summary>
    private static void ObserveInBackground(params Task[] tasks)
    {
        foreach (var t in tasks) _ = t.ContinueWith(static x => _ = x.Exception, TaskContinuationOptions.OnlyOnFaulted);
    }

    /// <summary>
    /// The Project Tasks story filter: "All" (scoped to the selected epic when the epic filter is on) plus the stories
    /// in the list. It follows the story selected in Epics &amp; User Stories.
    /// </summary>
    private void RebuildTaskStoryFilter()
    {
        var epicScope = StoriesFilterByEpicCheck.IsChecked == true ? _selectedEpic : null;
        var filterStories = new List<UserStoryItem>
        {
            new() { Id = "", Story = epicScope is null ? "All Stories" : $"All stories in {epicScope.Title}" },
        };
        filterStories.AddRange(_stories);

        var wasUpdating = _updatingFilters;
        _updatingFilters = true;
        try
        {
            TaskStoryFilterCombo.ItemsSource = filterStories;
            TaskStoryFilterCombo.SelectedItem = _selectedStory is { } story
                ? filterStories.FirstOrDefault(s => s.Id == story.Id) ?? filterStories[0]
                : filterStories[0];
        }
        finally
        {
            _updatingFilters = wasUpdating;
        }
    }

    /// <summary>Loads every task in the active project; the story / epic / search filters are applied locally.</summary>
    private async Task LoadProjectTasksAsync(int? version = null, Task<List<TaskItem>>? prefetched = null)
    {
        if (_activeProject?.GuidId is null) return;
        var projectId = _activeProject.Id;
        var loadVersion = version ?? _projectLoadVersion;

        var tasks = await (prefetched ?? _apiService.ListAllProjectTasksAsync(_activeProject.GuidId.Value));
        if (!IsCurrentLoad(loadVersion, projectId)) return;

        _allRawProjectTasks = tasks;
        ApplyProjectTaskFilters();
    }

    private async void GlobalProjectCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isInitialized) return;
        if (GlobalProjectCombo.SelectedItem is not ProjectItem proj) return;

        var switched = proj.Id != _activeProject?.Id;
        _activeProject = proj; // when the same project is re-selected after a refresh, the selections stay
        if (switched)
        {
            ClearProjectScopedState();
            OnAiProjectChanged(proj);
        }
        SyncAiProjectPicker(proj);
        if (switched)
        {
            ShowMyWork();
            if (MyStoryBoard.LoadedAt is not null && !MyStoryBoard.ShowsAllProjects) _ = MyStoryBoard.LoadAsync(MyProjects());
        }

        await LoadActiveProjectDetailsAsync();
    }

    // ==================== AI WORKSPACE (ACTIVE PROJECT) ====================

    private void AiProjectCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingProjectPickers || !_isInitialized) return;
        if (AiProjectCombo.SelectedItem is ProjectItem project && !ReferenceEquals(project, GlobalProjectCombo.SelectedItem))
        {
            GlobalProjectCombo.SelectedItem = project; // loads it through GlobalProjectCombo_SelectionChanged
        }
    }

    private void SyncAiProjectPicker(ProjectItem project)
    {
        _syncingProjectPickers = true;
        try { AiProjectCombo.SelectedItem = project; }
        finally { _syncingProjectPickers = false; }
        AiProjectStatsText.Text = $"{project.MyRole} • {project.TotalNumberOfTasks} tasks";
    }

    /// <summary>
    /// The AI workspace follows the active project: the chat starts over (its history is about the old project),
    /// and the planner, quick update and analysis tabs drop what they had for the previous one.
    /// </summary>
    private void OnAiProjectChanged(ProjectItem? project)
    {
        _aiCts?.Cancel();
        _aiWorkspace.Invalidate();
        if (_chat.Count > 0)
        {
            _claude.ResetConversation();
            _local.ResetConversation();
            _chat.Clear();
            if (project is not null) AddChat(ChatRole.Notice, $"Now working on {project.ProjectName.Trim()}. Started a new conversation.");
        }
        UpdateAiEmptyState();
        ShowSidebarFolder();
        TaskPlanner.OnProjectChanged(project);
        QuickUpdate.OnProjectChanged(project);
        ProjectAnalysis.OnProjectChanged(project);
        if (project is null) AiProjectStatsText.Text = "";
        else if (ReferenceEquals(AiTabs.SelectedItem, AnalysisTab())) _ = ProjectAnalysis.ShowNumbersAsync();
    }

    /// <summary>The active project's code folder under the project picker (chosen here or in any view).</summary>
    private void ShowSidebarFolder()
    {
        var folder = _activeProject is null ? null : _aiWorkspace.GetProjectFolder(_activeProject.Id);
        SidebarFolderText.Text = folder is null ? "No project folder"
            : $"📂 {Path.GetFileName(folder.TrimEnd('\\', '/'))}{(Directory.Exists(folder) ? "" : " (not found)")}";
        SidebarFolderText.ToolTip = folder ?? "Choose the project's code folder so the AI can use its files and git history when generating tasks";
    }

    private void SidebarChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_activeProject is null)
        {
            ShowToast("Select an active project first.", ToastKind.Info);
            return;
        }
        Views.ProjectFolderBar.Choose(_aiWorkspace, _activeProject, this);
    }

    private async void AiTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Combo boxes and grids inside the tabs raise SelectionChanged too; only react to the tabs themselves.
        if (!ReferenceEquals(e.OriginalSource, AiTabs) || !_isInitialized) return;
        if (ReferenceEquals(AiTabs.SelectedItem, AnalysisTab()) && _activeProject is not null) await ProjectAnalysis.ShowNumbersAsync();
    }

    private TabItem? AnalysisTab() => AiTabs.Items.OfType<TabItem>().FirstOrDefault(t => ReferenceEquals(t.Content, ProjectAnalysis));

    /// <summary>User ID → name for the active project's people, so AI tool results show names instead of IDs.</summary>
    private IReadOnlyDictionary<string, string> ActiveProjectNames()
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in _teamMembers.Where(m => !string.IsNullOrWhiteSpace(m.UserId) && !string.IsNullOrWhiteSpace(m.Name)))
        {
            names[m.UserId] = string.Join(' ', m.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }
        if (_activeProject is { } p)
        {
            if (!string.IsNullOrWhiteSpace(p.ProjectManagerId)) names.TryAdd(p.ProjectManagerId, p.ProjectManagerName.Trim());
            if (!string.IsNullOrWhiteSpace(p.ScrumMasterId)) names.TryAdd(p.ScrumMasterId, p.ScrumMasterName.Trim());
        }
        return names;
    }

    /// <summary>
    /// Forgets everything that belongs to the previous project, so its epic doesn't filter the new project's stories
    /// and its lists, task details and comments don't linger while the new project loads.
    /// </summary>
    private void ClearProjectScopedState()
    {
        _projectLoadVersion++;
        _updatingFilters = true;
        try
        {
            _selectedEpic = null;
            _selectedStory = null;
            _selectedTask = null;
            _teamMembers.Clear();
            _epics.Clear();
            _stories.Clear();
            _allRawProjectTasks = [];
            _projectTasks.Clear();
            _subTasks.Clear();
            _comments.Clear();
            TaskStoryFilterCombo.ItemsSource = null;
            ProjectTaskCountText.Text = "";
            ShowSelectedStory();
            InspectorTaskTitle.Text = "Select a task to view sub-tasks & comments";
            InspectorTaskDesc.Text = "All sub-tasks and team comments for the selected task load automatically.";
        }
        finally
        {
            _updatingFilters = false;
        }
        UpdateAiContextPreview();
    }

    private void ShowSelectedStory()
    {
        if (_selectedStory is { } s)
        {
            SelectedStoryTitleText.Text = s.Story;
            SelectedStoryCriteriaText.Text = !string.IsNullOrWhiteSpace(s.AcceptanceCriteria)
                ? $"Acceptance Criteria: {s.AcceptanceCriteria}"
                : "No acceptance criteria specified.";
        }
        else
        {
            SelectedStoryTitleText.Text = "Select a user story to view acceptance criteria or add tasks";
            SelectedStoryCriteriaText.Text = "";
        }
    }

    private static void Replace<T>(BulkObservableCollection<T> target, IEnumerable<T> items) => target.ReplaceAll(items);

    // ==================== NAVIGATION TABS ====================

    private void NavDashboard_Click(object sender, RoutedEventArgs e) => SwitchView("Dashboard & My Work", DashboardView, NavDashboardBtn);
    private void NavProjects_Click(object sender, RoutedEventArgs e) => SwitchView("Projects & Team", ProjectsView, NavProjectsBtn);
    private void NavEpicsStories_Click(object sender, RoutedEventArgs e) => SwitchView("Epics & User Stories", EpicsStoriesView, NavEpicsStoriesBtn);
    private void NavTasks_Click(object sender, RoutedEventArgs e) => SwitchView("Project Tasks & Details", TasksView, NavTasksBtn);
    private void NavAutomation_Click(object sender, RoutedEventArgs e) => SwitchView("Automation & Standup", AutomationView, NavAutomationBtn);
    private void NavActivity_Click(object sender, RoutedEventArgs e) => SwitchView("Activity Log", ActivityView, NavActivityBtn);

    private async void NavStoryBoard_Click(object sender, RoutedEventArgs e)
    {
        SwitchView("My Story Board", StoryBoardHost, NavStoryBoardBtn);
        // Stories change less often than tasks; reload when the list is missing or a few minutes old.
        if (_apiService.IsAuthenticated && (MyStoryBoard.LoadedAt is not { } loaded || DateTime.Now - loaded > TimeSpan.FromMinutes(3)))
        {
            await MyStoryBoard.LoadAsync(MyProjects());
        }
    }

    /// <summary>Projects the signed-in user belongs to, whatever the "Only My Projects" filter shows.</summary>
    internal IEnumerable<ProjectItem> MyProjects() => _allRawProjects.Where(p => !string.IsNullOrWhiteSpace(p.MyRole));

    private void NavAi_Click(object sender, RoutedEventArgs e)
    {
        SwitchView("AI Workspace", AiView, NavAiBtn);
        AiInputBox.Focus();
    }

    private void AskAiShortcut_Executed(object sender, ExecutedRoutedEventArgs e) => NavAi_Click(sender, e);

    private void SidebarToggle_Click(object sender, RoutedEventArgs e)
    {
        _isSidebarMinimized = !_isSidebarMinimized;
        var targetWidth = _isSidebarMinimized ? 76d : 250d;
        SidebarBorder.BeginAnimation(WidthProperty, new DoubleAnimation(targetWidth, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
        SidebarBorder.Padding = _isSidebarMinimized ? new Thickness(8, 20, 8, 20) : new Thickness(16, 20, 16, 20);
        SidebarToggleBtn.Content = _isSidebarMinimized ? "▶" : "◀";
        SidebarToggleBtn.ToolTip = _isSidebarMinimized ? "Expand navigation" : "Minimize navigation";
        SidebarBrandText.Visibility = _isSidebarMinimized ? Visibility.Collapsed : Visibility.Visible;
        SidebarProjectCard.Visibility = _isSidebarMinimized ? Visibility.Collapsed : Visibility.Visible;
        SidebarAccountCard.Visibility = _isSidebarMinimized ? Visibility.Collapsed : Visibility.Visible;
        SidebarAiHeading.Visibility = _isSidebarMinimized ? Visibility.Collapsed : Visibility.Visible;
        SidebarManualHeading.Visibility = _isSidebarMinimized ? Visibility.Collapsed : Visibility.Visible;

        foreach (var (button, icon, label, fullToolTip) in new[]
        {
            (NavAiBtn, "✨", "AI Workspace", "Create tasks, update work and analyse the active project with AI (Ctrl+K)"),
            (NavDashboardBtn, "📊", "Dashboard & My Work", "Dashboard & My Work"),
            (NavStoryBoardBtn, "📌", "My Story Board", "Your user stories: add tasks in one line and drag them between stages"),
            (NavProjectsBtn, "📁", "Projects & Team", "Projects & Team"),
            (NavEpicsStoriesBtn, "🧭", "Epics & User Stories", "Epics & User Stories"),
            (NavTasksBtn, "✅", "Project Tasks & Details", "Project Tasks & Details"),
            (NavAutomationBtn, "⚡", "Automation & Standup", "Automation & Standup"),
            (NavActivityBtn, "📡", "Activity Log", "Activity Log"),
        })
        {
            button.Content = _isSidebarMinimized ? icon : $"{icon}  {label}";
            button.ToolTip = _isSidebarMinimized ? label : fullToolTip;
            button.HorizontalContentAlignment = _isSidebarMinimized ? HorizontalAlignment.Center : HorizontalAlignment.Left;
            button.Padding = _isSidebarMinimized ? new Thickness(6, 10, 6, 10) : new Thickness(14, 10, 14, 10);
        }
    }

    private void SwitchView(string title, Grid targetView, Button activeNavBtn)
    {
        PageTitleText.Text = title;
        PageSubtitleText.Text = title switch
        {
            "Dashboard & My Work" => "View your assigned tasks across all projects, log hours, and manage timers.",
            "Projects & Team" => "Browse projects you belong to, inspect roles, and view team members.",
            "Epics & User Stories" => $"Break down features into epics and user stories for {_activeProject?.ProjectName ?? "the active project"}.",
            "Project Tasks & Details" => $"Track tasks, sub-tasks, and team comments for {_activeProject?.ProjectName ?? "the active project"}.",
            "Automation & Standup" => "Bulk-create tasks or sub-tasks, draft them with AI, and generate standup summaries.",
            "My Story Board" => "Your assigned user stories: add tasks in one line and drag them between stages.",
            "AI Workspace" => "Create tasks, update your work and analyse the active project with AI, instead of clicking through Agile PM.",
            _ => "Inspect live Agile PM API traffic and the AI assistant's tool calls."
        };

        foreach (var view in new[] { DashboardView, ProjectsView, EpicsStoriesView, TasksView, AiView, AutomationView, ActivityView, StoryBoardHost })
        {
            view.Visibility = Visibility.Collapsed;
        }
        targetView.Visibility = Visibility.Visible;

        foreach (var btn in new[] { NavDashboardBtn, NavProjectsBtn, NavEpicsStoriesBtn, NavTasksBtn, NavAiBtn, NavAutomationBtn, NavActivityBtn, NavStoryBoardBtn })
        {
            btn.Tag = null;
        }
        activeNavBtn.Tag = "Active";

        if (targetView == AiView) UpdateAiContextPreview();
        if (targetView == AutomationView) UpdateBatchTargetHint();
    }

    // ==================== DASHBOARD & MY WORK ACTIONS ====================

    private void MyWorkFilter_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_isInitialized) return;
        Debounce(nameof(ApplyMyWorkFilters), ApplyMyWorkFilters);
    }

    private readonly Dictionary<string, DispatcherTimer> _debouncers = [];

    /// <summary>Runs <paramref name="action"/> once typing pauses, instead of re-filtering the lists on every key.</summary>
    private void Debounce(string key, Action action, int milliseconds = 180)
    {
        if (!_debouncers.TryGetValue(key, out var timer))
        {
            timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
            _debouncers[key] = timer;
        }
        timer.Stop();
        timer.Tag = action;
        timer.Tick -= DebounceTick;
        timer.Tick += DebounceTick;
        timer.Start();
    }

    private static void DebounceTick(object? sender, EventArgs e)
    {
        if (sender is not DispatcherTimer { Tag: Action action } timer) return;
        timer.Stop();
        action();
    }

    private void MyWorkStageFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_isInitialized) return;
        ApplyMyWorkFilters();
    }

    private void IncludeCompleted_Changed(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized) return;
        ApplyMyWorkFilters();
    }

    private void OverdueCard_Click(object sender, MouseButtonEventArgs e)
    {
        MyWorkStageFilter.SelectedIndex = 1; // "Overdue"
    }

    private void ApplyMyWorkFilters()
    {
        if (!_isInitialized) return;
        var search = MyWorkSearchBox?.Text?.Trim() ?? "";
        var stageItem = (MyWorkStageFilter?.SelectedItem as ComboBoxItem)?.Content?.ToString();

        var filtered = ScopedMyWork()
            .Where(t => IncludeCompletedCheck.IsChecked == true || t.IsOpen)
            .Where(t => string.IsNullOrWhiteSpace(search)
                        || t.Title.Contains(search, StringComparison.OrdinalIgnoreCase)
                        || t.ProjectName.Contains(search, StringComparison.OrdinalIgnoreCase)
                        || t.UserStoryTitle.Contains(search, StringComparison.OrdinalIgnoreCase))
            .Where(t => stageItem switch
            {
                null or "" or "All Stages" => true,
                "Overdue" => t.IsOverdue,
                _ => t.Status.ToString() == stageItem
            })
            .OrderByDescending(t => t.IsOverdue)
            .ThenBy(t => t.EndDateRaw ?? DateTime.MaxValue)
            .ToList();

        _myWorkTasks.ReplaceAll(filtered);
    }

    private List<TaskItem> SelectedMyWork() =>
        MyWorkGrid.SelectedItems.OfType<TaskItem>().Where(t => t.GuidId is not null).ToList();

    private void MyWorkGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = SelectedMyWork();
        MyWorkSelectionText.Text = selected.Count switch
        {
            0 => "Tip: Ctrl/Shift-click to select several tasks for bulk actions.",
            1 => $"1 task selected • {selected[0].PlannedHours:0.#} h planned",
            _ => $"{selected.Count} tasks selected • {selected.Sum(t => t.PlannedHours):0.#} h planned • buttons apply to all"
        };
    }

    private async void MyWorkStart_Click(object sender, RoutedEventArgs e)
    {
        var tasks = SelectedMyWork();
        if (tasks.Count == 0)
        {
            ShowToast("Select one or more tasks first.", ToastKind.Info);
            return;
        }

        SetBusy(true, tasks.Count == 1 ? $"Starting '{tasks[0].Title}'..." : $"Starting {tasks.Count} tasks...");
        var failures = new List<string>();
        foreach (var task in tasks)
        {
            try
            {
                await _apiService.MoveTaskStatusSmartAsync(task.GuidId!.Value, GeneralStagesEnum.InProgress, 0, currentStatus: task.Status);
            }
            catch (Exception ex)
            {
                failures.Add($"{task.Title}: {ex.Message}");
            }
        }

        await AfterTaskChangesAsync();
        SetBusy(false);
        ReportBulkResult("Start tasks", tasks.Count, failures);
    }

    private async void MyWorkComplete_Click(object sender, RoutedEventArgs e)
    {
        var tasks = SelectedMyWork();
        if (tasks.Count == 0)
        {
            ShowToast("Select one or more tasks first.", ToastKind.Info);
            return;
        }

        if (tasks.Count == 1)
        {
            var task = tasks[0];
            var dlg = new MoveStatusDialog(task.Title, task.Status, suggestedStage: GeneralStagesEnum.Completed, defaultHours: task.PlannedHours > 0 ? task.PlannedHours : 1);
            dlg.Owner = this;
            if (dlg.ShowDialog() != true) return;

            SetBusy(true, $"Completing task '{task.Title}'...");
            try
            {
                await _apiService.MoveTaskStatusSmartAsync(task.GuidId!.Value, dlg.SelectedStage, dlg.LoggedHours, currentStatus: task.Status);
                await AfterTaskChangesAsync();
                ShowToast($"'{task.Title}' moved to {dlg.SelectedStage}.");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                SetBusy(false);
            }
            return;
        }

        var hours = tasks.Sum(t => t.PlannedHours);
        var confirm = MessageBox.Show(
            $"Complete {tasks.Count} tasks and log each task's planned hours ({hours:0.#} h in total)?\n\nPlanned tasks are moved through InProgress automatically.",
            "Bulk Complete", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK) return;

        SetBusy(true, $"Completing {tasks.Count} tasks...");
        var failures = new List<string>();
        foreach (var task in tasks)
        {
            try
            {
                await _apiService.MoveTaskStatusSmartAsync(task.GuidId!.Value, GeneralStagesEnum.Completed, task.PlannedHours, currentStatus: task.Status);
            }
            catch (Exception ex)
            {
                failures.Add($"{task.Title}: {ex.Message}");
            }
        }

        await AfterTaskChangesAsync();
        SetBusy(false);
        ReportBulkResult("Complete tasks", tasks.Count, failures);
    }

    private async void MyWorkMove_Click(object sender, RoutedEventArgs e)
    {
        if (MyWorkGrid.SelectedItem is not TaskItem task || task.GuidId is null)
        {
            ShowToast("Select a task first.", ToastKind.Info);
            return;
        }

        var dlg = new MoveStatusDialog(task.Title, task.Status);
        dlg.Owner = this;
        if (dlg.ShowDialog() == true)
        {
            SetBusy(true, "Moving task status...");
            try
            {
                await _apiService.MoveTaskStatusSmartAsync(task.GuidId.Value, dlg.SelectedStage, dlg.LoggedHours, currentStatus: task.Status);
                await AfterTaskChangesAsync();
                ShowToast($"'{task.Title}' moved to {dlg.SelectedStage}.");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                SetBusy(false);
            }
        }
    }

    private async void MyWorkPause_Click(object sender, RoutedEventArgs e)
    {
        if (MyWorkGrid.SelectedItem is not TaskItem task || task.GuidId is null) return;
        SetBusy(true, "Pausing work timer...");
        try
        {
            await _apiService.PauseTaskWorkAsync(task.GuidId.Value);
            ShowToast($"Timer paused on '{task.Title}'.");
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Timer Error", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { SetBusy(false); }
    }

    private async void MyWorkResume_Click(object sender, RoutedEventArgs e)
    {
        if (MyWorkGrid.SelectedItem is not TaskItem task || task.GuidId is null) return;
        SetBusy(true, "Resuming work timer...");
        try
        {
            await _apiService.ResumeTaskWorkAsync(task.GuidId.Value);
            ShowToast($"Timer resumed on '{task.Title}'.");
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Timer Error", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { SetBusy(false); }
    }

    private void MyWorkInspect_Click(object sender, RoutedEventArgs e)
    {
        if (MyWorkGrid.SelectedItem is TaskItem task)
        {
            InspectTask(task);
            NavTasks_Click(sender, e);
        }
    }

    private void MyWorkGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (MyWorkGrid.SelectedItem is TaskItem task)
        {
            InspectTask(task);
            NavTasks_Click(sender, e);
        }
    }

    private void ExportMyWork_Click(object sender, RoutedEventArgs e) =>
        ExportTasksToCsv(_myWorkTasks, $"my-work-{DateTime.Today:yyyy-MM-dd}.csv");

    private async Task AfterTaskChangesAsync()
    {
        await LoadDashboardAndMyWorkAsync();
        if (_activeProject?.GuidId is not null) await LoadProjectTasksAsync();
    }

    private void ReportBulkResult(string name, int total, List<string> failures)
    {
        var ok = total - failures.Count;
        AddAutomationHistory(name, failures.Count == 0 ? $"{ok} of {total} succeeded" : $"{ok} of {total} succeeded; {failures.Count} failed", failures.Count == 0);
        if (failures.Count == 0)
        {
            ShowToast(total == 1 ? "Done." : $"{name}: all {total} succeeded.");
            return;
        }

        foreach (var f in failures) AppendApiLog($"{name} failed: {f}");
        MessageBox.Show($"{ok} of {total} succeeded.\n\nFailed:\n• {string.Join("\n• ", failures.Take(10))}", name, MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    // ==================== PROJECTS VIEW ACTIONS ====================

    private void ProjectsFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized || OnlyMyProjectsCheck is null || ProjectSearchBox is null) return;
        var onlyMy = OnlyMyProjectsCheck.IsChecked == true;
        var search = ProjectSearchBox?.Text?.Trim() ?? "";

        _projects.ReplaceAll(_allRawProjects
            .Where(p => !onlyMy || !string.IsNullOrWhiteSpace(p.MyRole))
            .Where(p => string.IsNullOrWhiteSpace(search) || p.ProjectName.Contains(search, StringComparison.OrdinalIgnoreCase)));
    }

    private void ProjectSearchBox_TextChanged(object sender, TextChangedEventArgs e) =>
        Debounce(nameof(ProjectsFilter_Changed), () => ProjectsFilter_Changed(sender, e));

    private void ProjectsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProjectsGrid.SelectedItem is ProjectItem p)
        {
            GlobalProjectCombo.SelectedItem = p;
        }
    }

    private void OpenSelectedProjectTasks_Click(object sender, RoutedEventArgs e)
    {
        NavTasks_Click(sender, e);
    }

    private void CopyTeamMemberId_Click(object sender, RoutedEventArgs e)
    {
        if (TeamMembersGrid.SelectedItem is TeamMemberItem m && !string.IsNullOrWhiteSpace(m.UserId))
        {
            Clipboard.SetText(m.UserId);
            ShowToast($"Copied {m.Name}'s user ID.");
        }
    }

    // ==================== EPICS & STORIES ACTIONS ====================

    private async void NewEpic_Click(object sender, RoutedEventArgs e)
    {
        if (_activeProject?.GuidId is null)
        {
            MessageBox.Show("Please select an active project first.", "Notice", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new EpicDialog();
        dlg.Owner = this;
        if (dlg.ShowDialog() == true)
        {
            SetBusy(true, "Creating Epic...");
            try
            {
                var req = new CreateEpicRequest(
                    _activeProject.Id,
                    dlg.EpicTitle,
                    dlg.EpicDescription,
                    dlg.EstimatedEffort,
                    dlg.EpicStatus,
                    dlg.EpicPriority,
                    dlg.StartDate,
                    dlg.EndDate);

                await _apiService.CreateEpicAsync(req);
                await LoadActiveProjectDetailsAsync();
                ShowToast($"Epic '{dlg.EpicTitle}' created.");
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Warning); }
            finally { SetBusy(false); }
        }
    }

    private async void EditEpic_Click(object sender, RoutedEventArgs e)
    {
        if (EpicsGrid.SelectedItem is not EpicItem epic || epic.GuidId is null || _activeProject?.GuidId is null)
        {
            MessageBox.Show("Please select an Epic to edit.", "Notice", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new EpicDialog(epic);
        dlg.Owner = this;
        if (dlg.ShowDialog() == true)
        {
            SetBusy(true, "Updating Epic...");
            try
            {
                await _apiService.UpdateEpicAsync(
                    epic.GuidId.Value,
                    _activeProject.GuidId.Value,
                    dlg.EpicTitle,
                    dlg.EpicDescription,
                    dlg.EstimatedEffort,
                    dlg.EpicStatus,
                    dlg.EpicPriority,
                    dlg.StartDate,
                    dlg.EndDate);

                await LoadActiveProjectDetailsAsync();
                ShowToast("Epic updated.");
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Warning); }
            finally { SetBusy(false); }
        }
    }

    private async void EpicsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingFilters) return;
        var epic = EpicsGrid.SelectedItem as EpicItem;
        if (epic?.Id == _selectedEpic?.Id) return;

        // A cleared selection clears the filter too, instead of leaving the old epic applied.
        _selectedEpic = epic;
        UpdateAiContextPreview();
        if (StoriesFilterByEpicCheck.IsChecked == true)
        {
            await LoadUserStoriesAsync();
        }
    }

    private async void StoriesFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized || _updatingFilters) return;
        await LoadUserStoriesAsync();
    }

    private async void NewStory_Click(object sender, RoutedEventArgs e)
    {
        if (_activeProject?.GuidId is null)
        {
            MessageBox.Show("Please select an active project first.", "Notice", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_epics.Count == 0)
        {
            MessageBox.Show("This project has no Epics yet. Create an Epic first before creating User Stories.", "Notice", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new UserStoryDialog(_epics, _teamMembers, defaultEpicId: _selectedEpic?.Id);
        dlg.Owner = this;
        if (dlg.ShowDialog() == true)
        {
            SetBusy(true, "Creating User Story...");
            try
            {
                var req = new CreateUserStoryRequest(
                    dlg.SelectedEpicId,
                    dlg.StoryText,
                    dlg.StoryDescription,
                    dlg.AcceptanceCriteria,
                    dlg.SelectedPriority,
                    dlg.SelectedStatus,
                    dlg.SelectedAssigneeIds);

                await _apiService.CreateUserStoryAsync(req);
                await LoadUserStoriesAsync();
                ShowToast("User story created.");
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Warning); }
            finally { SetBusy(false); }
        }
    }

    private async void EditStory_Click(object sender, RoutedEventArgs e)
    {
        if (StoriesGrid.SelectedItem is not UserStoryItem story || story.GuidId is null)
        {
            MessageBox.Show("Please select a User Story to edit.", "Notice", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new UserStoryDialog(_epics, _teamMembers, existing: story);
        dlg.Owner = this;
        if (dlg.ShowDialog() == true)
        {
            SetBusy(true, "Updating User Story...");
            try
            {
                Guid? epicGuid = Guid.TryParse(dlg.SelectedEpicId, out var eg) ? eg : null;
                await _apiService.UpdateUserStoryAsync(
                    story.GuidId.Value,
                    dlg.StoryText,
                    dlg.StoryDescription,
                    dlg.AcceptanceCriteria,
                    dlg.SelectedPriority,
                    epicGuid,
                    dlg.SelectedAssigneeIds);

                await LoadUserStoriesAsync();
                ShowToast("User story updated.");
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Warning); }
            finally { SetBusy(false); }
        }
    }

    private async void ReassignStory_Click(object sender, RoutedEventArgs e)
    {
        if (StoriesGrid.SelectedItem is not UserStoryItem story || story.GuidId is null)
        {
            MessageBox.Show("Please select a User Story to reassign.", "Notice", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var currentAssignee = story.AssignedUserIds.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(currentAssignee) || !Guid.TryParse(currentAssignee, out var fromGuid))
        {
            MessageBox.Show("This story has no current assignee ID to reassign from. Use 'Edit' to assign members.", "Notice", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new ReassignDialog(story.Story, _teamMembers);
        dlg.Owner = this;
        if (dlg.ShowDialog() == true)
        {
            SetBusy(true, "Reassigning User Story...");
            try
            {
                await _apiService.ReassignUserStoryAsync(story.GuidId.Value, fromGuid, dlg.TargetUserId);
                await LoadUserStoriesAsync();
                ShowToast("User story reassigned.");
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Warning); }
            finally { SetBusy(false); }
        }
    }

    private void StoriesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingFilters) return;
        _selectedStory = StoriesGrid.SelectedItem as UserStoryItem;
        ShowSelectedStory();
        UpdateAiContextPreview();

        // Project Tasks follows the selected story.
        RebuildTaskStoryFilter();
        ApplyProjectTaskFilters();
    }

    private void CreateTaskUnderStory_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedStory is null)
        {
            MessageBox.Show("Please select a User Story first.", "Notice", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        NewTask_Click(sender, e);
    }

    // ==================== PROJECT TASKS ACTIONS ====================

    private async void NewTask_Click(object sender, RoutedEventArgs e)
    {
        if (_activeProject?.GuidId is null)
        {
            MessageBox.Show("Please select an active project first.", "Notice", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new TaskDialog(
            _epics,
            _stories,
            _teamMembers,
            defaultEpicId: _selectedEpic?.Id,
            defaultStoryId: _selectedStory?.Id,
            currentUserId: _apiService.CurrentUser?.UserId?.ToString());

        dlg.Owner = this;
        if (dlg.ShowDialog() == true)
        {
            SetBusy(true, $"Creating task '{dlg.TaskTitle}'...");
            try
            {
                var req = new CreateProjectTaskRequest(
                    _activeProject.Id,
                    dlg.TaskTitle,
                    dlg.TaskDescription,
                    dlg.SelectedStatus,
                    dlg.SelectedPriority,
                    dlg.SelectedComplexity,
                    dlg.SelectedAssigneeId,
                    dlg.PlannedHours,
                    dlg.SelectedEndDate,
                    dlg.SelectedEpicId,
                    dlg.SelectedStoryId);

                await _apiService.CreateProjectTaskAsync(req);
                await LoadProjectTasksAsync();
                await LoadDashboardAndMyWorkAsync();
                ShowToast($"Task '{dlg.TaskTitle}' created.");
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Warning); }
            finally { SetBusy(false); }
        }
    }

    private async void EditProjectTask_Click(object sender, RoutedEventArgs e)
    {
        if (ProjectTasksGrid.SelectedItem is not TaskItem task || task.GuidId is null)
        {
            MessageBox.Show("Please select a task to edit.", "Notice", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new TaskDialog(_epics, _stories, _teamMembers, existing: task);
        dlg.Owner = this;
        if (dlg.ShowDialog() == true)
        {
            SetBusy(true, "Updating task...");
            try
            {
                Guid? epicG = Guid.TryParse(dlg.SelectedEpicId, out var eg) ? eg : null;
                Guid? storyG = Guid.TryParse(dlg.SelectedStoryId, out var sg) ? sg : null;

                await _apiService.UpdateProjectTaskAsync(
                    task.GuidId.Value,
                    dlg.TaskTitle,
                    dlg.TaskDescription,
                    dlg.PlannedHours,
                    dlg.SelectedEndDate,
                    dlg.SelectedPriority,
                    dlg.SelectedComplexity,
                    storyG,
                    epicG);

                await AfterTaskChangesAsync();
                ShowToast("Task updated.");
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Warning); }
            finally { SetBusy(false); }
        }
    }

    private async void MoveProjectTask_Click(object sender, RoutedEventArgs e)
    {
        if (ProjectTasksGrid.SelectedItem is not TaskItem task || task.GuidId is null)
        {
            MessageBox.Show("Please select a task to move.", "Notice", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new MoveStatusDialog(task.Title, task.Status);
        dlg.Owner = this;
        if (dlg.ShowDialog() == true)
        {
            SetBusy(true, "Moving status...");
            try
            {
                await _apiService.MoveTaskStatusSmartAsync(task.GuidId.Value, dlg.SelectedStage, dlg.LoggedHours, currentStatus: task.Status);
                await AfterTaskChangesAsync();
                ShowToast($"'{task.Title}' moved to {dlg.SelectedStage}.");
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Warning); }
            finally { SetBusy(false); }
        }
    }

    private async void ReassignProjectTask_Click(object sender, RoutedEventArgs e)
    {
        if (ProjectTasksGrid.SelectedItem is not TaskItem task || task.GuidId is null)
        {
            MessageBox.Show("Please select a task to reassign.", "Notice", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new ReassignDialog(task.Title, _teamMembers);
        dlg.Owner = this;
        if (dlg.ShowDialog() == true)
        {
            SetBusy(true, "Reassigning task...");
            try
            {
                await _apiService.ReassignProjectTaskAsync(task.GuidId.Value, dlg.TargetUserId);
                await AfterTaskChangesAsync();
                ShowToast("Task reassigned.");
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Warning); }
            finally { SetBusy(false); }
        }
    }

    private async void PauseProjectTask_Click(object sender, RoutedEventArgs e)
    {
        if (ProjectTasksGrid.SelectedItem is not TaskItem task || task.GuidId is null) return;
        SetBusy(true, "Pausing timer...");
        try
        {
            await _apiService.PauseTaskWorkAsync(task.GuidId.Value);
            ShowToast($"Timer paused on '{task.Title}'.");
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { SetBusy(false); }
    }

    private async void ResumeProjectTask_Click(object sender, RoutedEventArgs e)
    {
        if (ProjectTasksGrid.SelectedItem is not TaskItem task || task.GuidId is null) return;
        SetBusy(true, "Resuming timer...");
        try
        {
            await _apiService.ResumeTaskWorkAsync(task.GuidId.Value);
            ShowToast($"Timer resumed on '{task.Title}'.");
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { SetBusy(false); }
    }

    private void TaskStoryFilterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isInitialized || _updatingFilters) return;
        ApplyProjectTaskFilters();
    }

    private void ProjectTaskSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_isInitialized) return;
        Debounce(nameof(ApplyProjectTaskFilters), ApplyProjectTaskFilters);
    }

    private void ApplyProjectTaskFilters()
    {
        if (!_isInitialized) return;
        var search = ProjectTaskSearchBox?.Text?.Trim() ?? "";

        // Story picked in the filter, else the selected epic (when "Filter by Selected Epic" is on), else everything.
        var storyId = TaskStoryFilterCombo?.SelectedValue as string;
        var epicId = string.IsNullOrEmpty(storyId) && StoriesFilterByEpicCheck.IsChecked == true ? _selectedEpic?.Id : null;

        var list = _allRawProjectTasks
            .Where(t => string.IsNullOrEmpty(storyId) || string.Equals(t.UserStoryId, storyId, StringComparison.OrdinalIgnoreCase))
            .Where(t => string.IsNullOrEmpty(epicId) || string.Equals(t.EpicId, epicId, StringComparison.OrdinalIgnoreCase))
            .Where(t => string.IsNullOrWhiteSpace(search)
                        || t.Title.Contains(search, StringComparison.OrdinalIgnoreCase)
                        || t.AssigneeName.Contains(search, StringComparison.OrdinalIgnoreCase)
                        || t.Status.ToString().Contains(search, StringComparison.OrdinalIgnoreCase))
            .ToList();

        _projectTasks.ReplaceAll(list);

        var overdue = list.Count(t => t.IsOverdue);
        var scope = list.Count == _allRawProjectTasks.Count ? "" : $" of {_allRawProjectTasks.Count}";
        ProjectTaskCountText.Text = $"{list.Count}{scope} task{(list.Count == 1 ? "" : "s")}{(overdue > 0 ? $" • {overdue} overdue" : "")}";
    }

    private void ProjectTasksGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProjectTasksGrid.SelectedItem is TaskItem t)
        {
            InspectTask(t);
        }
    }

    private void ExportProjectTasks_Click(object sender, RoutedEventArgs e)
    {
        var name = string.Concat((_activeProject?.ProjectName ?? "project").Split(Path.GetInvalidFileNameChars()));
        ExportTasksToCsv(_projectTasks, $"{name}-tasks-{DateTime.Today:yyyy-MM-dd}.csv");
    }

    private async void InspectTask(TaskItem task)
    {
        _selectedTask = task;
        InspectorTaskTitle.Text = task.Title;
        InspectorTaskDesc.Text = $"Stage: {task.Status} • Assignee: {task.AssigneeName} • Effort: {task.HoursDisplay} • Due: {task.EndDate}{(task.IsOverdue ? " (overdue)" : "")}\n{(string.IsNullOrWhiteSpace(task.Description) ? "No description." : TextTools.Plain(task.Description, 4000))}";
        UpdateAiContextPreview();
        UpdateBatchTargetHint();

        if (task.GuidId is null) return;
        var tId = task.GuidId.Value;

        // Sub-tasks and comments load together; results for a task no longer selected are dropped.
        var subsTask = _apiService.ListSubTasksAsync(tId);
        var commentsTask = _apiService.ListCommentsAsync(taskId: tId);

        try
        {
            var subs = await subsTask;
            if (!ReferenceEquals(_selectedTask, task)) return;
            _subTasks.ReplaceAll(subs);
        }
        catch { if (ReferenceEquals(_selectedTask, task)) _subTasks.Clear(); }

        try
        {
            var comms = await commentsTask;
            if (!ReferenceEquals(_selectedTask, task)) return;
            _comments.ReplaceAll(comms);
        }
        catch { if (ReferenceEquals(_selectedTask, task)) _comments.Clear(); }
    }

    private async void AddSubTask_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedTask?.GuidId is null)
        {
            MessageBox.Show("Please select a parent task first.", "Notice", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new SubTaskDialog(_teamMembers, currentUserId: _apiService.CurrentUser?.UserId?.ToString());
        dlg.Owner = this;
        if (dlg.ShowDialog() == true)
        {
            SetBusy(true, "Adding sub-task...");
            try
            {
                var req = new CreateSubTaskRequest(
                    _selectedTask.Id,
                    dlg.SubTaskTitle,
                    dlg.SubTaskDescription,
                    dlg.SelectedAssigneeId,
                    GeneralStagesEnum.Planned,
                    dlg.SelectedPriority,
                    dlg.SelectedPriority,
                    dlg.PlannedHours,
                    dlg.SelectedEndDate,
                    dlg.IsBlocker);

                await _apiService.CreateSubTaskAsync(req);
                InspectTask(_selectedTask);
                ShowToast("Sub-task added.");
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Warning); }
            finally { SetBusy(false); }
        }
    }

    private async void MoveSubTask_Click(object sender, RoutedEventArgs e)
    {
        if (SubTasksGrid.SelectedItem is not SubTaskItem sub || sub.GuidId is null || _selectedTask?.GuidId is null)
        {
            MessageBox.Show("Please select a sub-task to move.", "Notice", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new MoveStatusDialog(sub.Title, sub.Status);
        dlg.Owner = this;
        if (dlg.ShowDialog() == true)
        {
            SetBusy(true, "Moving sub-task stage...");
            try
            {
                await _apiService.MoveTaskStatusSmartAsync(_selectedTask.GuidId.Value, dlg.SelectedStage, dlg.LoggedHours, subTaskId: sub.GuidId.Value, currentStatus: sub.Status);
                InspectTask(_selectedTask);
                ShowToast($"Sub-task moved to {dlg.SelectedStage}.");
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Warning); }
            finally { SetBusy(false); }
        }
    }

    private void NewCommentBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) AddComment_Click(sender, e);
    }

    private async void AddComment_Click(object sender, RoutedEventArgs e)
    {
        var text = NewCommentBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(text)) return;
        if (_selectedTask?.GuidId is null)
        {
            MessageBox.Show("Please select a task to comment on.", "Notice", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SetBusy(true, "Posting comment...");
        try
        {
            var req = new CreateCommentRequest(text, _selectedTask.Id, null, null, null);
            await _apiService.AddCommentAsync(req);
            NewCommentBox.Clear();
            InspectTask(_selectedTask);
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { SetBusy(false); }
    }

    // ==================== BATCH AUTOMATION ====================

    private bool BatchCreatesSubTasks => BatchModeCombo.SelectedIndex == 1;

    private void BatchModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isInitialized) return;
        UpdateBatchTargetHint();
    }

    private void UpdateBatchTargetHint()
    {
        if (!_isInitialized) return;
        BatchEpicCombo.IsEnabled = !BatchCreatesSubTasks;
        BatchStoryCombo.IsEnabled = !BatchCreatesSubTasks;
        BatchTargetHint.Text = BatchCreatesSubTasks
            ? (_selectedTask is null
                ? "Select a parent task in Project Tasks first."
                : $"Sub-tasks will be added under: {_selectedTask.Title}")
            : (_activeProject is null ? "Select an active project first." : $"Tasks will be created in: {_activeProject.ProjectName}");
    }

    private void LoadBatchTemplate_Click(object sender, RoutedEventArgs e)
    {
        BatchTasksInputBox.Text =
            "# Title | Hours | Priority | Description | Due\n" +
            "Setup repository and CI/CD pipelines | 3 | High | Configure workflows and build checks | +2\n" +
            "Implement Agile PM authentication module | 4 | High | Add login, token decoding, and refresh | +4\n" +
            "Build task management views and forms | 6 | Medium | Create task grids, move stage, and log hours | +6\n" +
            "Write comprehensive automated tests | 4 | Medium | Cover edge cases and API response envelopes | +8\n" +
            "Prepare release packaging and docs | 2 | Low | Create single-file desktop build | +10";
        BatchPreviewGrid.Visibility = Visibility.Collapsed;
    }

    private void ClearBatch_Click(object sender, RoutedEventArgs e)
    {
        BatchTasksInputBox.Clear();
        BatchPreviewGrid.Visibility = Visibility.Collapsed;
        BatchProgressText.Text = "Ready.";
    }

    private void ImportBatchCsv_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*", Title = "Import tasks from CSV" };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            var lines = File.ReadAllLines(dlg.FileName)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(ParseCsvLine)
                .ToList();

            // Skip a header row such as "Title,Hours,Priority,Description,Due".
            if (lines.Count > 0 && lines[0].FirstOrDefault()?.Trim().Equals("title", StringComparison.OrdinalIgnoreCase) == true)
            {
                lines.RemoveAt(0);
            }

            BatchTasksInputBox.Text = string.Join("\n", lines.Select(cells => string.Join(" | ", cells.Take(5).Select(c => c.Replace("|", "/").Trim()))));
            PreviewBatch_Click(sender, e);
            ShowToast($"Imported {lines.Count} line(s) from {Path.GetFileName(dlg.FileName)}.");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not read the CSV file: {ex.Message}", "Import", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void PreviewBatch_Click(object sender, RoutedEventArgs e)
    {
        var preview = ParseBatchLines(ExistingTitlesForBatch());
        BatchPreviewGrid.ItemsSource = preview;
        BatchPreviewGrid.Visibility = preview.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var ready = preview.Count(p => p.IsValid);
        BatchProgressText.Text = $"Preview: {ready} ready, {preview.Count - ready} will be skipped.";
    }

    private HashSet<string> ExistingTitlesForBatch()
    {
        if (BatchSkipDuplicatesCheck.IsChecked != true) return [];
        var titles = BatchCreatesSubTasks
            ? _subTasks.Select(s => s.Title)
            : _allRawProjectTasks.Select(t => t.Title);
        return titles.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private List<BatchLinePreview> ParseBatchLines(HashSet<string> existingTitles)
    {
        var defaultDays = int.TryParse(BatchDefaultDueBox.Text.Trim(), out var d) ? Math.Max(0, d) : 7;
        var seen = new HashSet<string>(existingTitles, StringComparer.OrdinalIgnoreCase);
        var result = new List<BatchLinePreview>();

        var rawLines = BatchTasksInputBox.Text.Split('\n');
        for (var i = 0; i < rawLines.Length; i++)
        {
            var line = rawLines[i].Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("//") || line.StartsWith('#')) continue;

            var parts = line.Split('|').Select(p => p.Trim()).ToArray();
            var problems = new List<string>();

            var title = parts[0];
            double hours = 4;
            if (parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1]) && !double.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out hours))
            {
                hours = 4;
                problems.Add($"hours '{parts[1]}' unreadable, using 4");
            }

            var prio = PriorityEnum.Medium;
            if (parts.Length > 2 && !string.IsNullOrWhiteSpace(parts[2]) && !Enum.TryParse(parts[2], true, out prio))
            {
                prio = PriorityEnum.Medium;
                problems.Add($"priority '{parts[2]}' unknown, using Medium");
            }

            var desc = parts.Length > 3 && !string.IsNullOrWhiteSpace(parts[3]) ? parts[3] : null;
            var due = DateTime.Today.AddDays(defaultDays);
            if (parts.Length > 4 && !string.IsNullOrWhiteSpace(parts[4]) && !TryParseDue(parts[4], out due))
            {
                due = DateTime.Today.AddDays(defaultDays);
                problems.Add($"due '{parts[4]}' unreadable");
            }

            var check = string.IsNullOrWhiteSpace(title) ? "Skip: empty title"
                : !seen.Add(title) ? "Skip: duplicate title"
                : problems.Count == 0 ? "Ready"
                : $"Ready ({string.Join("; ", problems)})";

            result.Add(new BatchLinePreview
            {
                LineNumber = i + 1,
                Title = title,
                Hours = hours,
                Priority = prio,
                Description = desc,
                DueDate = due,
                Check = check,
            });
        }

        return result;
    }

    private static bool TryParseDue(string text, out DateTime due)
    {
        if (text.StartsWith('+') && int.TryParse(text[1..], out var days))
        {
            due = DateTime.Today.AddDays(days);
            return true;
        }
        return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out due)
               || DateTime.TryParse(text, out due);
    }

    private async void RunBatchCreate_Click(object sender, RoutedEventArgs e)
    {
        if (_activeProject?.GuidId is null)
        {
            MessageBox.Show("Please select an active project first.", "Notice", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (BatchCreatesSubTasks && _selectedTask?.GuidId is null)
        {
            MessageBox.Show("Select the parent task in Project Tasks first.", "Notice", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var parsed = ParseBatchLines(ExistingTitlesForBatch());
        BatchPreviewGrid.ItemsSource = parsed;
        BatchPreviewGrid.Visibility = parsed.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var lines = parsed.Where(p => p.IsValid).ToList();

        if (lines.Count == 0)
        {
            MessageBox.Show(parsed.Count == 0 ? "Please enter at least one line." : "Every line was skipped (empty or duplicate titles).", "Notice", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var target = BatchCreatesSubTasks ? $"sub-tasks under '{_selectedTask!.Title}'" : $"task(s) in project '{_activeProject.ProjectName}'";
        var skipped = parsed.Count - lines.Count;
        var confirm = MessageBox.Show(
            $"Create {lines.Count} {target}?{(skipped > 0 ? $"\n\n{skipped} line(s) will be skipped." : "")}",
            "Confirm Batch Automation",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes) return;

        SetBusy(true, $"Processing {lines.Count} items...");
        RunBatchCreateBtn.IsEnabled = false;
        BatchProgressBar.Visibility = Visibility.Visible;
        BatchProgressBar.Maximum = lines.Count;
        BatchProgressBar.Value = 0;

        var epicId = BatchEpicCombo.SelectedValue as string;
        var storyId = BatchStoryCombo.SelectedValue as string;
        var assigneeId = BatchAssigneeCombo.SelectedValue as string;

        var success = 0;
        var failed = 0;

        foreach (var line in lines)
        {
            BatchProgressText.Text = $"Creating {success + failed + 1}/{lines.Count}: {line.Title}...";
            try
            {
                if (BatchCreatesSubTasks)
                {
                    await _apiService.CreateSubTaskAsync(new CreateSubTaskRequest(
                        _selectedTask!.Id, line.Title, line.Description, assigneeId, GeneralStagesEnum.Planned,
                        line.Priority, line.Priority, line.Hours, line.DueDate, IsBlocker: false));
                }
                else
                {
                    await _apiService.CreateProjectTaskAsync(new CreateProjectTaskRequest(
                        _activeProject.Id, line.Title, line.Description, GeneralStagesEnum.Planned, line.Priority, line.Priority,
                        assigneeId, line.Hours, line.DueDate, epicId, storyId));
                }

                line.Check = "Created ✔";
                success++;
            }
            catch (Exception ex)
            {
                line.Check = $"Failed: {ex.Message}";
                failed++;
                AppendApiLog($"Batch item failed ('{line.Title}'): {ex.Message}");
            }

            BatchProgressBar.Value = success + failed;
        }

        BatchPreviewGrid.Items.Refresh();
        BatchProgressText.Text = $"Batch complete: {success} created, {failed} failed{(skipped > 0 ? $", {skipped} skipped" : "")}.";
        SetBusy(false);
        RunBatchCreateBtn.IsEnabled = true;
        BatchProgressBar.Visibility = Visibility.Collapsed;

        if (BatchCreatesSubTasks) InspectTask(_selectedTask!);
        await AfterTaskChangesAsync();

        AddAutomationHistory(BatchCreatesSubTasks ? "Batch sub-tasks" : "Batch tasks", BatchProgressText.Text, failed == 0);
        ShowToast(BatchProgressText.Text, failed == 0 ? ToastKind.Success : ToastKind.Warning);
    }

    // ==================== STANDUP ====================

    private void GenerateStandupReport_Click(object sender, RoutedEventArgs e)
    {
        var sb = new StringBuilder();
        var dateStr = DateTime.Now.ToString("dddd, MMMM d, yyyy");
        sb.AppendLine("📊 AGILE PM DAILY STANDUP & WORK REPORT");
        sb.AppendLine($"Date: {dateStr}");
        sb.AppendLine($"User: {_apiService.CurrentUser?.PrimaryEmail}");
        sb.AppendLine(new string('=', 55));
        sb.AppendLine();

        var work = ScopedMyWork();
        var inProgress = work.Where(t => t.Status == GeneralStagesEnum.InProgress).ToList();
        var planned = work.Where(t => t.Status == GeneralStagesEnum.Planned).OrderBy(t => t.EndDateRaw ?? DateTime.MaxValue).ToList();
        var completed = work.Where(t => t.Status == GeneralStagesEnum.Completed).ToList();
        var returned = work.Where(t => t.Status == GeneralStagesEnum.Returned).ToList();
        var overdue = work.Where(t => t.IsOverdue).OrderBy(t => t.EndDateRaw).ToList();

        sb.AppendLine($"▶ CURRENTLY IN PROGRESS ({inProgress.Count})");
        if (inProgress.Count == 0) sb.AppendLine("  • No tasks currently in progress");
        foreach (var t in inProgress)
        {
            sb.AppendLine($"  • [{t.ProjectName}] {t.Title} ({t.HoursDisplay})");
        }
        sb.AppendLine();

        sb.AppendLine($"✔ RECENTLY COMPLETED ({completed.Count})");
        if (completed.Count == 0) sb.AppendLine("  • No completed tasks in current view (tick 'Include Completed' on the dashboard)");
        foreach (var t in completed)
        {
            sb.AppendLine($"  • [{t.ProjectName}] {t.Title} ({t.HoursDisplay})");
        }
        sb.AppendLine();

        sb.AppendLine($"⏳ PLANNED / NEXT UP ({planned.Count})");
        if (planned.Count == 0) sb.AppendLine("  • No planned tasks pending");
        foreach (var t in planned.Take(8))
        {
            sb.AppendLine($"  • [{t.ProjectName}] {t.Title} ({t.HoursDisplay}) - Due: {t.EndDate}");
        }
        if (planned.Count > 8) sb.AppendLine($"  ... and {planned.Count - 8} more planned tasks");
        sb.AppendLine();

        if (overdue.Count > 0)
        {
            sb.AppendLine($"⏰ OVERDUE ({overdue.Count})");
            foreach (var t in overdue) sb.AppendLine($"  • [{t.ProjectName}] {t.Title} - was due {t.EndDate}");
            sb.AppendLine();
        }

        if (returned.Count > 0)
        {
            sb.AppendLine($"⚠ RETURNED / NEEDS ATTENTION ({returned.Count})");
            foreach (var t in returned) sb.AppendLine($"  • [{t.ProjectName}] {t.Title}");
            sb.AppendLine();
        }

        var totalHours = work.Where(t => t.IsOpen).Sum(t => t.PlannedHours);
        sb.AppendLine($"Summary: {inProgress.Count} in progress, {completed.Count} completed, {planned.Count} planned, {overdue.Count} overdue ({totalHours:0.#} open hrs planned).");

        StandupReportBox.Text = sb.ToString();
    }

    private void CopyStandupReport_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(StandupReportBox.Text)) GenerateStandupReport_Click(sender, e);
        Clipboard.SetText(StandupReportBox.Text);
        ShowToast("Standup copied to clipboard.");
    }

    private async void AiPolishStandup_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureAiKey()) return;
        if (string.IsNullOrWhiteSpace(StandupReportBox.Text)) GenerateStandupReport_Click(sender, e);

        AiPolishStandupBtn.IsEnabled = false;
        SetBusy(true, "The AI is writing your standup...");
        try
        {
            StandupReportBox.Text = await _ai.PolishStandupAsync(StandupReportBox.Text, CancellationToken.None);
            AddAutomationHistory("AI standup", $"Standup rewritten by {_ai.DisplayName}", true);
            ShowToast("Standup polished. Review it, then copy.");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"The AI could not write the standup: {ex.Message}", "AI", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            SetBusy(false);
            AiPolishStandupBtn.IsEnabled = true;
        }
    }

    private void AddAutomationHistory(string name, string outcome, bool succeeded)
    {
        _automationHistory.Insert(0, new AutomationRunEntry(DateTime.Now, name, outcome, succeeded));
        while (_automationHistory.Count > 50) _automationHistory.RemoveAt(_automationHistory.Count - 1);
    }

    private void ClearAutomationHistory_Click(object sender, RoutedEventArgs e) => _automationHistory.Clear();

    // ==================== AI: QUICK ACTIONS ====================

    private async void AiDraftTasksFromStory_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedStory is null)
        {
            ShowToast("Select a user story first.", ToastKind.Info);
            return;
        }
        await DraftTasksForStoryAsync(_selectedStory);
    }

    private async void AiDraftTasksFromBatchStory_Click(object sender, RoutedEventArgs e)
    {
        var story = BatchStoryCombo.SelectedItem as UserStoryItem ?? _selectedStory;
        if (story is null)
        {
            ShowToast("Pick a Target Story (or select one in Epics & User Stories) first.", ToastKind.Info);
            return;
        }
        await DraftTasksForStoryAsync(story);
    }

    /// <summary>
    /// Codebase notes from the project's folder (layout, matching files, git history) for an AI task draft,
    /// or null when no folder is chosen, the model has no room for it, or the folder can't be read.
    /// </summary>
    private async Task<string?> ReadCodeContextAsync(ProjectItem? project, string storyText, IAiAssistant ai)
    {
        var chars = AssistantPrompts.CodeContextChars(ai.RequestBudgetTokens);
        if (project is null || chars == 0 || _aiWorkspace.GetProjectFolder(project.Id) is not { } folder || !Directory.Exists(folder)) return null;
        try
        {
            return await ProjectFolderScanner.ReadCodeContextAsync(folder, storyText, chars, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException)
        {
            ShowToast($"Couldn't read the project folder ({ex.Message}); drafting from the story only.", ToastKind.Warning);
            return null;
        }
    }

    /// <summary>Claude drafts tasks for a story; they land in the batch creator for review, nothing is created yet.</summary>
    private async Task DraftTasksForStoryAsync(UserStoryItem story)
    {
        if (!EnsureAiKey()) return;

        SetBusy(true, "The AI is drafting tasks...");
        try
        {
            var existing = new List<string>();
            if (_activeProject?.GuidId is { } pid && story.GuidId is { } sid)
            {
                existing = (await _apiService.ListAllProjectTasksAsync(pid, sid)).Select(t => t.Title).ToList();
            }

            var description = TextTools.Plain(story.Description, 1500);
            var criteria = TextTools.Plain(story.AcceptanceCriteria, 1500);
            var codeContext = await ReadCodeContextAsync(_activeProject, $"{story.Story}\n{story.Story}\n{description}\n{criteria}", _ai);
            var drafts = await _ai.DraftTasksForStoryAsync(story.Story, description, criteria, existing, codeContext, CancellationToken.None);
            if (drafts.Count == 0)
            {
                ShowToast("The AI didn't propose any new tasks for this story.", ToastKind.Info);
                return;
            }

            BatchModeCombo.SelectedIndex = 0;
            BatchStoryCombo.SelectedItem = _stories.FirstOrDefault(s => s.Id == story.Id);
            if (_epics.FirstOrDefault(ep => ep.Id == story.EpicId) is { } epic) BatchEpicCombo.SelectedItem = epic;

            BatchTasksInputBox.Text =
                $"# Drafted by {_ai.DisplayName} for: {story.Story}\n" +
                string.Join("\n", drafts.Select(t =>
                    $"{t.Title.Replace("|", "/")} | {t.Hours.ToString("0.#", CultureInfo.InvariantCulture)} | {t.Priority} | {t.Description.Replace("|", "/")}"));

            NavAutomation_Click(this, new RoutedEventArgs());
            PreviewBatch_Click(this, new RoutedEventArgs());
            AddAutomationHistory("AI task draft", $"{drafts.Count} tasks drafted for '{story.Story}'", true);
            ShowToast($"{drafts.Count} tasks drafted. Review and edit them, then press Create.", ToastKind.Info);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"The AI could not draft tasks: {ex.Message}", "AI", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void AiPlanMyDay_Click(object sender, RoutedEventArgs e)
    {
        NavAi_Click(sender, e);
        await RunSuggestionAsync("☀ Plan my day", "prompt:plan_my_day");
    }

    private async void AiHealthCheck_Click(object sender, RoutedEventArgs e)
    {
        NavAi_Click(sender, e);
        await RunSuggestionAsync("🩺 Project health check", "prompt:sprint_health_check");
    }

    // ==================== AI ASSISTANT (CHAT WITH AGILE PM TOOLS) ====================

    private async void AiSend_Click(object sender, RoutedEventArgs e)
    {
        var text = AiInputBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(text)) return;
        AiInputBox.Clear();
        await SendAiMessageAsync(text, text);
    }

    private void AiInputBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            e.Handled = true;
            AiSend_Click(sender, e);
        }
    }

    private async void AiSuggestion_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } button)
        {
            await RunSuggestionAsync(button.Content?.ToString() ?? tag, tag);
        }
    }

    /// <summary>Runs a chip: "prompt:name" sends one of the built-in prompt templates, anything else is sent as typed.</summary>
    private async Task RunSuggestionAsync(string label, string tag)
    {
        if (!tag.StartsWith("prompt:", StringComparison.Ordinal))
        {
            await SendAiMessageAsync(tag, tag);
            return;
        }

        string promptText;
        switch (tag["prompt:".Length..])
        {
            case "plan_my_day":
                promptText = AgilePmPromptTemplates.PlanMyDay();
                break;
            case "daily_standup":
                promptText = AgilePmPromptTemplates.DailyStandup();
                break;
            case "sprint_health_check" when _activeProject is null:
                ShowToast("Select an active project first.", ToastKind.Info);
                return;
            case "sprint_health_check":
                promptText = AgilePmPromptTemplates.SprintHealthCheck(_activeProject.Id);
                label = $"{label}: {_activeProject.ProjectName}";
                break;
            case "break_down_user_story" when _selectedStory is null:
                ShowToast("Select a user story in Epics & User Stories first.", ToastKind.Info);
                return;
            case "break_down_user_story":
                promptText = AgilePmPromptTemplates.BreakDownUserStory(_selectedStory.Id);
                label = $"{label}: {_selectedStory.Story}";
                break;
            case var unknown:
                AddChat(ChatRole.Notice, $"There is no built-in prompt called '{unknown}'.");
                return;
        }

        await SendAiMessageAsync(label, promptText);
    }

    private async Task SendAiMessageAsync(string displayText, string promptText)
    {
        if (_aiCts is not null) return; // a turn is already running
        if (!EnsureAiKey()) return;
        if (!_apiService.IsAuthenticated || string.IsNullOrWhiteSpace(_apiService.AccessToken))
        {
            ShowToast("Sign in to Agile PM first.", ToastKind.Warning);
            return;
        }

        AddChat(ChatRole.User, displayText);

        _aiCts = new CancellationTokenSource();
        SetAiRunning(true);
        var changedData = false;
        var toolBubbles = new Stack<ChatMessage>();

        try
        {
            await _ai.SendAsync(
                promptText,
                BuildAiContextNote(),
                _tools,
                ApproveAiWriteAsync,
                ev =>
                {
                    switch (ev.Kind)
                    {
                        case AssistantEventKind.Text:
                            AddChat(ChatRole.Assistant, ev.Text);
                            break;
                        case AssistantEventKind.ToolStarted:
                            var bubble = new ChatMessage { Role = ChatRole.Tool, ToolName = ev.ToolName, Text = "running…", Status = "running", Details = ev.Text };
                            toolBubbles.Push(bubble);
                            _chat.Add(bubble);
                            ScrollChatToEnd();
                            break;
                        case AssistantEventKind.ToolFinished or AssistantEventKind.ToolFailed:
                            if (toolBubbles.FirstOrDefault(b => b.ToolName == ev.ToolName && b.Status == "running") is { } running)
                            {
                                var failed = ev.Kind == AssistantEventKind.ToolFailed;
                                running.Status = failed ? "failed" : "done";
                                running.Text = failed ? Shorten(ev.Text, 140) : "done";
                                running.Details = $"{running.Details}\n\n→ {Shorten(ev.Text, 1500)}";
                                if (!failed && !_tools.IsReadOnly(ev.ToolName!)) changedData = true;
                            }
                            break;
                        case AssistantEventKind.ToolDeclined:
                            _chat.Add(new ChatMessage { Role = ChatRole.Tool, ToolName = ev.ToolName, Text = "declined", Status = "declined" });
                            break;
                        case AssistantEventKind.Notice:
                            AddChat(ChatRole.Notice, ev.Text);
                            break;
                    }
                },
                _aiCts.Token);
        }
        catch (OperationCanceledException)
        {
            AddChat(ChatRole.Notice, "Stopped.");
        }
        catch (Exception ex)
        {
            AddChat(ChatRole.Notice, $"Something went wrong: {ex.Message}");
            AppendApiLog($"AI assistant error: {ex}");
        }
        finally
        {
            _aiCts.Dispose();
            _aiCts = null;
            SetAiRunning(false);
        }

        if (changedData)
        {
            AddAutomationHistory("AI assistant", $"Updated Agile PM: {Shorten(displayText, 80)}", true);
            await RefreshAllData(background: true);
        }
    }

    /// <summary>Asks before the assistant runs any tool that changes Agile PM data (unless auto-approve is on).</summary>
    private Task<bool> ApproveAiWriteAsync(string toolName, string argumentsJson)
    {
        if (_userSettings.AiAutoApproveWrites) return Task.FromResult(true);

        var answer = MessageBox.Show(
            $"The AI assistant wants to run:\n\n{toolName}\n\n{Shorten(argumentsJson, 1200)}\n\nAllow this change in Agile PM?",
            "Approve AI action",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);
        return Task.FromResult(answer == MessageBoxResult.Yes);
    }

    private void UpdateToolsStatus() =>
        AiToolsText.Text = $"{_tools.Tools.Count} tools ({_tools.Tools.Count(t => t.ReadOnly)} read-only) built into this app. " +
            "They call Agile PM directly with your session; no separate server is needed.";

    private string BuildAiContextNote()
    {
        var user = _apiService.CurrentUser;
        var sb = new StringBuilder();
        sb.AppendLine($"Today: {DateTime.Now:dddd yyyy-MM-dd HH:mm}");
        sb.AppendLine($"Signed-in user: {user?.PrimaryEmail} (user ID {user?.UserId?.ToString() ?? "unknown"})");
        if (_activeProject is not null) sb.AppendLine($"Active project: {_activeProject.ProjectName} (ID {_activeProject.Id}), my role: {_activeProject.MyRole}");
        if (_selectedEpic is not null) sb.AppendLine($"Selected epic: {_selectedEpic.Title} (ID {_selectedEpic.Id})");
        if (_selectedStory is not null) sb.AppendLine($"Selected user story: {_selectedStory.Story} (ID {_selectedStory.Id})");
        if (_selectedTask is not null) sb.AppendLine($"Selected task: {_selectedTask.Title} (ID {_selectedTask.Id}, stage {_selectedTask.Status})");
        return sb.ToString().TrimEnd();
    }

    private void UpdateAiContextPreview()
    {
        if (!_isInitialized) return;
        AiContextPreview.Text = _apiService.IsAuthenticated ? BuildAiContextNote() : "Sign in to Agile PM to use the assistant.";
        AiContextText.Text = _activeProject is null
            ? "Uses your Agile PM tools as you"
            : $"Uses your Agile PM tools as you • {_activeProject.ProjectName}";
    }

    private void AddChat(ChatRole role, string text)
    {
        _chat.Add(new ChatMessage { Role = role, Text = text });
        UpdateAiEmptyState();
        ScrollChatToEnd();
    }

    private void ScrollChatToEnd() => Dispatcher.BeginInvoke(AiChatScroll.ScrollToEnd, DispatcherPriority.Background);

    private void UpdateAiEmptyState() =>
        AiEmptyState.Visibility = _chat.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void SetAiRunning(bool running)
    {
        AiSendBtn.IsEnabled = !running;
        AiSuggestionsPanel.IsEnabled = !running;
        AiStopBtn.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        BusyBadge.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        BusyText.Text = running ? $"{_ai.DisplayName} is working..." : "";
    }

    private void AiStop_Click(object sender, RoutedEventArgs e) => _aiCts?.Cancel();

    private void AiNewChat_Click(object sender, RoutedEventArgs e)
    {
        _aiCts?.Cancel();
        _ai.ResetConversation();
        _chat.Clear();
        UpdateAiEmptyState();
        AiInputBox.Focus();
    }

    private void AiCopyLast_Click(object sender, RoutedEventArgs e)
    {
        var last = _chat.LastOrDefault(m => m.Role == ChatRole.Assistant);
        if (last is null) return;
        Clipboard.SetText(last.Text);
        ShowToast("Answer copied.");
    }

    private bool EnsureAiKey()
    {
        if (_ai.IsConfigured) return true;
        NavAi_Click(this, new RoutedEventArgs());
        if (_userSettings.AiProvider == AiProviderKind.Claude) AiApiKeyBox.Focus(); else LocalAiModelCombo.Focus();
        ShowToast(_ai.NotConfiguredHint, ToastKind.Warning);
        return false;
    }

    private void AiProviderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isInitialized) return;
        var provider = AiProviderCombo.SelectedIndex == 1 ? AiProviderKind.Local : AiProviderKind.Claude;
        if (provider == _userSettings.AiProvider) return;

        _aiCts?.Cancel();
        _userSettings.AiProvider = provider;
        LocalSettingsService.Save(_userSettings);
        UpdateAiProviderUi();

        // Each backend keeps its own history format, so start a fresh conversation.
        _claude.ResetConversation();
        _local.ResetConversation();
        if (_chat.Count > 0) AddChat(ChatRole.Notice, $"Switched to {_ai.DisplayName}. Starting a new conversation.");
        ShowToast($"AI features now use {_ai.DisplayName}.", ToastKind.Info);
    }

    private void UpdateAiProviderUi()
    {
        var local = _userSettings.AiProvider == AiProviderKind.Local;
        ClaudeSettingsPanel.Visibility = local ? Visibility.Collapsed : Visibility.Visible;
        LocalSettingsPanel.Visibility = local ? Visibility.Visible : Visibility.Collapsed;
        var preset = CurrentOpenModelPreset();
        AiModelText.Text = local
            ? $"Model: {_userSettings.LocalAiModel} via {preset.Name} ({(IsHosted(_userSettings.LocalAiBaseUrl) ? "hosted" : "on this computer")})"
            : $"Model: {AiAssistantService.Model} (Anthropic API)";
    }

    private async void LoadLocalModels_Click(object sender, RoutedEventArgs e)
    {
        LocalAiStatusText.Text = "Looking for models…";
        try
        {
            var url = SelectedOpenModelPreset().ResolveBaseUrl(LocalAiUrlBox.Text);
            var key = LocalAiKeyBox.Password.Length > 0 ? LocalAiKeyBox.Password : _userSettings.GetOpenModelKey(url);
            var models = await LocalModelAssistantService.ListModelsAsync(url, key);
            var typed = LocalAiModelCombo.Text;
            LocalAiModelCombo.ItemsSource = models;
            LocalAiModelCombo.Text = models.Contains(typed) ? typed : models.FirstOrDefault() ?? typed;
            LocalAiStatusText.Text = models.Count == 0
                ? "Connected, but no tool-capable models were listed. Type a model name instead."
                : $"Connected ✔ {models.Count} model(s) found. Press Save to use the selected one.";
        }
        catch (Exception ex)
        {
            LocalAiStatusText.Text = ex.Message;
        }
    }

    private void SaveLocalAi_Click(object sender, RoutedEventArgs e)
    {
        var model = LocalAiModelCombo.Text.Trim();
        if (string.IsNullOrEmpty(model))
        {
            LocalAiStatusText.Text = "Enter or pick a model name.";
            return;
        }

        var preset = SelectedOpenModelPreset();
        _userSettings.OpenModelPresetId = preset.Id;
        _userSettings.LocalAiBaseUrl = preset.ResolveBaseUrl(LocalAiUrlBox.Text);
        _userSettings.LocalAiModel = model;
        _userSettings.LocalAiAllTools = LocalAiAllToolsCheck.IsChecked == true;
        if (LocalAiKeyBox.Password.Length > 0) _userSettings.SetOpenModelKey(_userSettings.LocalAiBaseUrl, LocalAiKeyBox.Password);
        LocalSettingsService.Save(_userSettings);

        ConfigureOpenModel();
        _local.ResetConversation();
        LocalAiUrlBox.Text = _userSettings.LocalAiBaseUrl;
        LocalAiKeyBox.Clear();
        UpdateAiProviderUi();
        UpdateOpenModelPresetUi();

        if (!_local.IsConfigured)
        {
            LocalAiStatusText.Text = $"Saved, but {preset.Name} needs an API key before it can answer.";
            return;
        }
        LocalAiStatusText.Text = "Saved ✔";
        ShowToast($"AI features now use {model} via {preset.Name}.");
    }

    private OpenModelPreset CurrentOpenModelPreset() =>
        _userSettings.OpenModelPresetId is { } id ? OpenModelPresets.Get(id) : OpenModelPresets.FromUrl(_userSettings.LocalAiBaseUrl);

    /// <summary>The service picked in the panel (which may not be saved yet).</summary>
    private OpenModelPreset SelectedOpenModelPreset() => OpenModelPresetCombo.SelectedItem as OpenModelPreset ?? CurrentOpenModelPreset();

    /// <summary>
    /// Settings saved while the URL box still showed the Ollama default could pair a hosted service (e.g. Groq) with
    /// http://localhost:11434, so every request went to a local server that isn't there. Hosted services have a fixed
    /// address: put it back, and move a key that was saved under the wrong address along with it.
    /// </summary>
    private void RepairOpenModelUrl()
    {
        var url = CurrentOpenModelPreset().ResolveBaseUrl(_userSettings.LocalAiBaseUrl);
        if (string.Equals(url, _userSettings.LocalAiBaseUrl, StringComparison.OrdinalIgnoreCase)) return;

        if (_userSettings.GetOpenModelKey(url) is null && _userSettings.GetOpenModelKey(_userSettings.LocalAiBaseUrl) is { } misplacedKey)
        {
            _userSettings.SetOpenModelKey(url, misplacedKey);
        }
        _userSettings.LocalAiBaseUrl = url;
        LocalSettingsService.Save(_userSettings);
    }

    private void ConfigureOpenModel()
    {
        var preset = CurrentOpenModelPreset();
        var baseUrl = preset.ResolveBaseUrl(_userSettings.LocalAiBaseUrl);
        var name = preset.Id == OpenModelPresets.CustomId && Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            ? uri.Host
            : preset.Name;
        _local.Configure(
            baseUrl,
            _userSettings.LocalAiModel,
            _userSettings.GetOpenModelKey(baseUrl),
            _userSettings.LocalAiAllTools,
            name,
            keyRequired: preset.NeedsKey,
            requestBudgetTokens: preset.MaxRequestTokens);
    }

    private static bool IsHosted(string? baseUrl) =>
        Uri.TryCreate(LocalModelAssistantService.NormalizeBaseUrl(baseUrl), UriKind.Absolute, out var uri) && !uri.IsLoopback;

    private void OpenModelPresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isInitialized || OpenModelPresetCombo.SelectedItem is not OpenModelPreset preset) return;

        // Fill in the service's address and a suggested model, and switch to the service straight away, so the chat
        // never keeps calling the previous one (e.g. local Ollama) while the panel shows a hosted service.
        if (preset.Id != OpenModelPresets.CustomId)
        {
            LocalAiUrlBox.Text = preset.BaseUrl;
            LocalAiModelCombo.ItemsSource = null;
            LocalAiModelCombo.Text = preset.SuggestedModel;

            _userSettings.OpenModelPresetId = preset.Id;
            _userSettings.LocalAiBaseUrl = preset.BaseUrl;
            if (preset.SuggestedModel.Length > 0) _userSettings.LocalAiModel = preset.SuggestedModel;
            LocalSettingsService.Save(_userSettings);
            ConfigureOpenModel();
            _local.ResetConversation();
            UpdateAiProviderUi();
        }
        LocalAiKeyBox.Clear();
        LocalAiStatusText.Text = preset.NeedsKey && _userSettings.GetOpenModelKey(preset.BaseUrl) is null
            ? "Paste your key, press ⟳ to see the models, then Save."
            : "Press ⟳ to see the models, then Save.";
        UpdateOpenModelPresetUi();
    }

    private void UpdateOpenModelPresetUi()
    {
        var preset = SelectedOpenModelPreset();
        var url = preset.ResolveBaseUrl(LocalAiUrlBox.Text);
        var hasKey = _userSettings.GetOpenModelKey(url) is not null;

        // A hosted service's address is fixed; only local servers and custom endpoints take a typed URL.
        LocalAiUrlBox.IsReadOnly = preset.HasFixedUrl;
        if (preset.HasFixedUrl) LocalAiUrlBox.Text = preset.BaseUrl;
        OpenModelPresetNotes.Text = preset.Notes;
        OpenModelPrivacyNote.Visibility = IsHosted(url) ? Visibility.Visible : Visibility.Collapsed;
        LocalAiKeyLabel.Text = preset.NeedsKey ? "API key (required, free)" : "API key (optional)";
        LocalAiKeyStatus.Text = hasKey ? "Key saved for this service ✔ " : preset.NeedsKey ? "No key saved yet. " : "Not needed. ";
        OpenModelKeyLinkText.Text = preset.KeyUrl is null ? "" : preset.IsLocal ? $"Download {preset.Name}" : "Get a free key";
    }

    private void OpenModelKeyLink_Click(object sender, RoutedEventArgs e)
    {
        var preset = OpenModelPresetCombo.SelectedItem as OpenModelPreset;
        if (preset?.KeyUrl is null) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(preset.KeyUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowToast($"Could not open the browser: {ex.Message}", ToastKind.Warning);
        }
    }

    private void SaveAiKey_Click(object sender, RoutedEventArgs e)
    {
        var key = AiApiKeyBox.Password.Trim();
        _userSettings.AnthropicApiKey = string.IsNullOrEmpty(key) ? null : key;
        LocalSettingsService.Save(_userSettings);
        _claude.Configure(_userSettings.AnthropicApiKey);
        AiApiKeyBox.Clear();
        UpdateAiKeyStatus();
        ShowToast(string.IsNullOrEmpty(key) ? "Stored key removed." : "Claude API key saved (encrypted for your Windows account).");
    }

    private void UpdateAiKeyStatus()
    {
        AiKeyStatusText.Text = !string.IsNullOrEmpty(_userSettings.AnthropicApiKey)
            ? "Key saved ✔"
            : !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"))
                ? "Using ANTHROPIC_API_KEY"
                : "No key set";
    }

    private void AiAutoApprove_Changed(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized) return;
        _userSettings.AiAutoApproveWrites = AiAutoApproveCheck.IsChecked == true;
        LocalSettingsService.Save(_userSettings);
    }

    // ==================== ACTIVITY LOG ====================

    private void CopyBearerToken_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_apiService.AccessToken))
        {
            MessageBox.Show("No active access token. Please sign in first.", "Notice", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Clipboard.SetText(_apiService.AccessToken);
        ShowToast("Bearer token copied. Treat it like a password.");
    }

    private void ClearApiLog_Click(object sender, RoutedEventArgs e) => ApiLogBox.Clear();

    private const int MaxApiLogChars = 200_000;
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _pendingLog = new();
    private int _logFlushQueued;

    /// <summary>
    /// Called from any thread. Lines are collected and written to the log box in one go when the UI is idle,
    /// so background requests never wait on the UI thread (a project load logs dozens of lines).
    /// </summary>
    private void QueueApiLog(string line)
    {
        _pendingLog.Enqueue(line);
        if (Interlocked.Exchange(ref _logFlushQueued, 1) == 0)
        {
            Dispatcher.BeginInvoke(FlushApiLog, DispatcherPriority.Background);
        }
    }

    private void FlushApiLog()
    {
        Interlocked.Exchange(ref _logFlushQueued, 0);
        var batch = new StringBuilder();
        while (_pendingLog.TryDequeue(out var line)) batch.Append(line).Append(Environment.NewLine);
        if (batch.Length > 0) AppendApiLogText(batch.ToString());
    }

    private void AppendApiLog(string line) => AppendApiLogText(line + Environment.NewLine);

    private void AppendApiLogText(string text)
    {
        ApiLogBox.AppendText(text);
        // Keep the box small; a TextBox slows down as its text grows.
        if (ApiLogBox.Text.Length > MaxApiLogChars)
        {
            var all = ApiLogBox.Text;
            var cut = all.IndexOf('\n', all.Length - MaxApiLogChars / 2);
            ApiLogBox.Text = cut > 0 ? all[(cut + 1)..] : all[^(MaxApiLogChars / 2)..];
        }
        ApiLogBox.ScrollToEnd();
    }

    // ==================== AUTO-SYNC ====================

    private void SelectAutoRefreshItem(int minutes)
    {
        foreach (ComboBoxItem item in AutoRefreshCombo.Items)
        {
            if (item.Tag?.ToString() == minutes.ToString())
            {
                AutoRefreshCombo.SelectedItem = item;
                break;
            }
        }
        ApplyAutoRefresh(minutes);
    }

    private void AutoRefreshCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isInitialized) return;
        var minutes = int.TryParse((AutoRefreshCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out var m) ? m : 0;
        _userSettings.AutoRefreshMinutes = minutes;
        LocalSettingsService.Save(_userSettings);
        ApplyAutoRefresh(minutes);
        ShowToast(minutes == 0 ? "Auto-sync off." : $"Auto-sync every {minutes} minutes.", ToastKind.Info);
    }

    private void ApplyAutoRefresh(int minutes)
    {
        _autoRefreshTimer.Stop();
        if (minutes <= 0) return;
        _autoRefreshTimer.Interval = TimeSpan.FromMinutes(minutes);
        _autoRefreshTimer.Start();
    }

    // ==================== HELPERS ====================

    private void ExportTasksToCsv(IEnumerable<TaskItem> tasks, string fileName)
    {
        var list = tasks.ToList();
        if (list.Count == 0)
        {
            ShowToast("Nothing to export.", ToastKind.Info);
            return;
        }

        var dlg = new SaveFileDialog { Filter = "CSV files (*.csv)|*.csv", FileName = fileName, Title = "Export tasks" };
        if (dlg.ShowDialog(this) != true) return;

        var sb = new StringBuilder();
        sb.AppendLine("Title,Project,Story,Stage,Priority,Assignee,Planned Hours,Logged Hours,Due Date,Overdue,Task ID");
        foreach (var t in list)
        {
            sb.AppendLine(string.Join(",", new[]
            {
                t.Title, t.ProjectName, t.UserStoryTitle, t.Status.ToString(), t.Priority.ToString(), t.AssigneeName,
                t.PlannedHours.ToString(CultureInfo.InvariantCulture), t.ActualHours.ToString(CultureInfo.InvariantCulture),
                t.EndDate, t.IsOverdue ? "Yes" : "No", t.Id
            }.Select(CsvEscape)));
        }

        File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        AddAutomationHistory("CSV export", $"{list.Count} tasks → {Path.GetFileName(dlg.FileName)}", true);
        ShowToast($"Exported {list.Count} tasks.");
    }

    private static string CsvEscape(string? value)
    {
        value ??= "";
        return value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
    }

    private static List<string> ParseCsvLine(string line)
    {
        var cells = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuotes)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                else if (c == '"') inQuotes = false;
                else current.Append(c);
            }
            else if (c == '"') inQuotes = true;
            else if (c == ',') { cells.Add(current.ToString()); current.Clear(); }
            else current.Append(c);
        }
        cells.Add(current.ToString());
        return cells;
    }

    private static string Shorten(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";

    private enum ToastKind { Success, Info, Warning }

    /// <summary>Non-blocking notification in the bottom-right corner; fades out after a few seconds.</summary>
    private void ShowToast(string message, ToastKind kind = ToastKind.Success)
    {
        var (bg, border, icon) = kind switch
        {
            ToastKind.Warning => ("#FFFBEB", "#FCD34D", "⚠"),
            ToastKind.Info => ("#EFF6FF", "#93C5FD", "ℹ"),
            _ => ("#ECFDF5", "#6EE7B7", "✔"),
        };

        var toast = new Border
        {
            Background = (Brush)new BrushConverter().ConvertFromString(bg)!,
            BorderBrush = (Brush)new BrushConverter().ConvertFromString(border)!,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 9, 12, 9),
            Margin = new Thickness(0, 6, 0, 0),
            Opacity = 0,
            Child = new DockPanel
            {
                Children =
                {
                    new TextBlock { Text = icon, FontSize = 13, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Top },
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Foreground = Brushes.Black },
                }
            },
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Opacity = 0.12 },
        };

        ToastHost.Children.Add(toast);
        while (ToastHost.Children.Count > 4) ToastHost.Children.RemoveAt(0);

        toast.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)));
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(kind == ToastKind.Warning ? 6 : 3.5) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(250));
            fade.Completed += (_, _) => ToastHost.Children.Remove(toast);
            toast.BeginAnimation(OpacityProperty, fade);
        };
        timer.Start();
    }

    private void SetBusy(bool isBusy, string text = "Working...", bool showWaitCursor = true)
    {
        BusyBadge.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
        GlobalBusyBar.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
        BusyText.Text = text;
        if (isBusy)
        {
            BusyBadge.Opacity = 0;
            BusyBadge.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)));
        }
        Mouse.OverrideCursor = isBusy && showWaitCursor ? Cursors.Wait : null;
    }
}
