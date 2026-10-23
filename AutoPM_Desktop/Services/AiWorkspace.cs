using AutoPM_Desktop.Models;

namespace AutoPM_Desktop.Services;

/// <summary>
/// What the AI workspace tabs (plan tasks, quick update, analysis) need from the app: the signed-in API, the chosen
/// model, the active project and a way to report back. The active project's data is loaded once and shared.
/// </summary>
public sealed class AiWorkspace
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(2);
    private ProjectData? _cache;

    public required AgilePmApiService Api { get; init; }

    /// <summary>The model backend picked in the AI settings.</summary>
    public required Func<IAiAssistant> Ai { get; init; }

    /// <summary>True when the model is configured; otherwise shows the user what to set up and returns false.</summary>
    public required Func<bool> EnsureAiReady { get; init; }

    public required Func<ProjectItem?> ActiveProject { get; init; }

    /// <summary>A toast for the user; true when it is a problem.</summary>
    public required Action<string, bool> Notify { get; init; }

    /// <summary>Called after changes to Agile PM so the other views refresh.</summary>
    public required Func<Task> DataChanged { get; init; }

    /// <summary>Adds a line to the automation history (name, outcome, succeeded).</summary>
    public required Action<string, string, bool> AddHistory { get; init; }

    /// <summary>The local code folder remembered for a project (by project ID), or null.</summary>
    public required Func<string, string?> GetProjectFolder { get; init; }

    /// <summary>Remembers (and saves) the local code folder for a project.</summary>
    public required Action<string, string> SetProjectFolder { get; init; }

    public Guid? CurrentUserId => Api.CurrentUser?.UserId;

    public bool IsMe(string? userId) => Api.CurrentUser?.IsUser(userId) == true;

    /// <summary>The active project's people, epics, stories and tasks (cached for a couple of minutes).</summary>
    public async Task<ProjectData> LoadProjectAsync(bool refresh = false, CancellationToken ct = default)
    {
        var project = ActiveProject() ?? throw new InvalidOperationException("Select an active project first (top of the sidebar).");
        if (project.GuidId is not { } projectId) throw new InvalidOperationException("The active project has no valid ID.");

        if (!refresh && _cache is { } cached && cached.Project.Id == project.Id && DateTime.Now - cached.LoadedAt < CacheLifetime)
        {
            return cached;
        }

        if (refresh) Api.ClearCache();

        var full = Api.GetProjectByIdAsync(projectId, ct);
        var epics = Api.ListEpicsAsync(projectId, 1, 100, ct);
        var stories = Api.ListAllUserStoriesAsync(projectId, ct: ct);
        var tasks = Api.ListAllProjectTasksAsync(projectId, ct: ct);
        await Task.WhenAll(full, epics, stories, tasks).ConfigureAwait(false);

        // Building the lookups runs off the UI thread; callers resume on their own thread as usual.
        var data = new ProjectData(full.Result, epics.Result, stories.Result, tasks.Result, DateTime.Now);
        _cache = data;
        return data;
    }

    /// <summary>Forget the cached project data (after changes, or when the project switches).</summary>
    public void Invalidate() => _cache = null;
}

/// <summary>A snapshot of one project, with lookups the AI features use to show names instead of IDs.</summary>
public sealed class ProjectData
{
    private readonly Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, EpicItem> _epics = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, UserStoryItem> _stories = new(StringComparer.OrdinalIgnoreCase);

    public ProjectData(ProjectItem project, List<EpicItem> epics, List<UserStoryItem> stories, List<TaskItem> tasks, DateTime loadedAt)
    {
        Project = project;
        Epics = epics;
        Stories = stories;
        Tasks = tasks;
        LoadedAt = loadedAt;

        foreach (var m in project.TeamMembers.Where(m => !string.IsNullOrWhiteSpace(m.UserId)))
        {
            _names[m.UserId] = Clean(string.IsNullOrWhiteSpace(m.Name) ? m.Email : m.Name);
        }
        AddName(project.ProjectManagerId, project.ProjectManagerName);
        AddName(project.ScrumMasterId, project.ScrumMasterName);
        foreach (var e in epics.Where(e => !string.IsNullOrWhiteSpace(e.Id))) _epics[e.Id] = e;
        foreach (var s in stories.Where(s => !string.IsNullOrWhiteSpace(s.Id))) _stories[s.Id] = s;
    }

    public ProjectItem Project { get; }
    public List<TeamMemberItem> Members => Project.TeamMembers;
    public List<EpicItem> Epics { get; }
    public List<UserStoryItem> Stories { get; }
    public List<TaskItem> Tasks { get; }
    public DateTime LoadedAt { get; }

    public IReadOnlyDictionary<string, string> Names => _names;

    public string PersonName(string? userId, string? fallback = null) =>
        !string.IsNullOrWhiteSpace(userId) && _names.TryGetValue(userId, out var name) ? name
        : !string.IsNullOrWhiteSpace(fallback) && fallback != "Unassigned" && !Guid.TryParse(fallback, out _) ? fallback
        : string.IsNullOrWhiteSpace(userId) ? "Unassigned" : "Someone outside the team list";

    public EpicItem? Epic(string? id) => id is not null && _epics.TryGetValue(id, out var e) ? e : null;

    public UserStoryItem? Story(string? id) => id is not null && _stories.TryGetValue(id, out var s) ? s : null;

    private void AddName(string? id, string? name)
    {
        if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name) && name.Trim() != "-") _names.TryAdd(id, Clean(name));
    }

    private static string Clean(string name) => string.Join(' ', name.Split(' ', StringSplitOptions.RemoveEmptyEntries));
}
