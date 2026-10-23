using AutoPM_Desktop.Models;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutoPM_Desktop.Services;

public sealed class AgilePmApiException(string message, HttpStatusCode? statusCode = null) : Exception(message)
{
    public HttpStatusCode? StatusCode { get; } = statusCode;
}

public sealed class AgilePmEnvelope
{
    public JsonElement? Data { get; init; }
    public IReadOnlyList<string> Messages { get; init; } = [];
    public PageInfo? Page { get; init; }

    public IEnumerable<JsonElement> Items => Data switch
    {
        { ValueKind: JsonValueKind.Array } array => array.EnumerateArray(),
        { ValueKind: JsonValueKind.Object } obj when obj.TryGetProperty("items", out var inner) && inner.ValueKind == JsonValueKind.Array => inner.EnumerateArray(),
        { ValueKind: JsonValueKind.Object } obj when obj.TryGetProperty("data", out var innerData) && innerData.ValueKind == JsonValueKind.Array => innerData.EnumerateArray(),
        { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } or null => [],
        { } single => [single]
    };
}

public sealed class AgilePmApiService
{
    private HttpClient _httpClient;
    private string _apiBaseUrl;
    private string? _accessToken;
    private string? _refreshToken;
    private string? _lastEmail;
    private string? _lastPassword;

    /// <summary>
    /// Recent GET responses by path. Views and the AI features often ask for the same list within seconds of each
    /// other; they share one request (also while it is still running). Any write clears it.
    /// </summary>
    private readonly ConcurrentDictionary<string, CachedResponse> _getCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan GetCacheLifetime = TimeSpan.FromSeconds(30);

    /// <summary>Parallel requests that all hit an expired token refresh it once.</summary>
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    /// <summary>Pages fetched at the same time when reading a long list.</summary>
    private const int MaxParallelPages = 6;

    private sealed record CachedResponse(Task<AgilePmEnvelope> Response, DateTime ExpiresAt);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly string[] TokenPropertyNames = ["jwtToken", "token", "accessToken", "access_token"];
    private static readonly string[] RefreshTokenPropertyNames = ["refreshToken", "refresh_token"];

    public AgilePmApiService(string? serverUrl = null)
    {
        _apiBaseUrl = LocalSettingsService.NormalizeApiBaseUrl(serverUrl);
        _httpClient = CreateHttpClient(_apiBaseUrl);
    }

    public string ApiBaseUrl => _apiBaseUrl;
    public string? AccessToken => _accessToken;
    public string? RefreshTokenValue => _refreshToken;
    public UserIdentityInfo? CurrentUser { get; private set; }
    public bool IsAuthenticated => !string.IsNullOrWhiteSpace(_accessToken);

    public event Action<string>? OnApiLog;

    public void UpdateServerUrl(string rawUrl)
    {
        var normalized = LocalSettingsService.NormalizeApiBaseUrl(rawUrl);
        if (!string.Equals(_apiBaseUrl, normalized, StringComparison.OrdinalIgnoreCase))
        {
            _apiBaseUrl = normalized;
            _httpClient = CreateHttpClient(_apiBaseUrl);
            ClearCache();
            Log($"API Base URL set to {_apiBaseUrl}");
        }
    }

    public void SetSessionToken(string accessToken, string? refreshToken = null)
    {
        _accessToken = accessToken.Trim();
        if (_accessToken.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            _accessToken = _accessToken["Bearer ".Length..].Trim();
        }
        _refreshToken = refreshToken;
        CurrentUser = DecodeTokenIdentity(_accessToken);
    }

    public void SignOut()
    {
        _accessToken = null;
        _refreshToken = null;
        _lastPassword = null;
        CurrentUser = null;
        ClearCache();
        Log("Signed out.");
    }

    /// <summary>Forget cached responses so the next reads come from Agile PM (Refresh, sign-out, after writes).</summary>
    public void ClearCache() => _getCache.Clear();

    // ---- Authentication ----

    public async Task<UserIdentityInfo> LoginAsync(string email, string password, CancellationToken ct = default)
    {
        _lastEmail = email.Trim();
        _lastPassword = password;

        Log($"Signing in as {_lastEmail} to {_apiBaseUrl}Auth/Login ...");
        var envelope = await SendAsync(HttpMethod.Post, "Auth/Login", new { email = _lastEmail, password }, anonymous: true, ct).ConfigureAwait(false);

        var token = (envelope.Data is { } body ? FindPropertyRecursive(body, TokenPropertyNames, requireJwtShape: true) : null)
            ?? throw new AgilePmApiException("Agile PM accepted the login request, but no JWT access token was found in the response.");

        var refresh = envelope.Data is { } b ? FindPropertyRecursive(b, RefreshTokenPropertyNames, requireJwtShape: false) : null;
        SetSessionToken(token, refresh);
        Log($"Signed in as {CurrentUser?.PrimaryEmail} (User ID: {CurrentUser?.UserId?.ToString() ?? "N/A"})");
        return CurrentUser!;
    }

    public async Task<bool> TryRefreshTokenOrReLoginAsync(CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(_accessToken) && !string.IsNullOrWhiteSpace(_refreshToken))
        {
            try
            {
                Log("Refreshing access token...");
                var envelope = await SendAsync(HttpMethod.Post, "Auth/RefreshToken", new
                {
                    jwtToken = _accessToken,
                    refreshToken = _refreshToken
                }, anonymous: true, ct).ConfigureAwait(false);

                if (envelope.Data is { } body && FindPropertyRecursive(body, TokenPropertyNames, requireJwtShape: true) is { } newToken)
                {
                    var newRefresh = FindPropertyRecursive(body, RefreshTokenPropertyNames, requireJwtShape: false) ?? _refreshToken;
                    SetSessionToken(newToken, newRefresh);
                    Log("Access token refreshed.");
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log($"Token refresh failed: {ex.Message}");
            }
        }

        if (!string.IsNullOrWhiteSpace(_lastEmail) && !string.IsNullOrEmpty(_lastPassword))
        {
            try
            {
                await LoginAsync(_lastEmail, _lastPassword, ct).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                Log($"Automatic re-login failed: {ex.Message}");
            }
        }

        return false;
    }

    // ---- Dashboard & My Work ----

    public async Task<JsonElement?> GetDashboardSummaryAsync(CancellationToken ct = default)
    {
        var resp = await GetAsync("TaskManagement/getDashboardSummary", ct: ct).ConfigureAwait(false);
        return resp.Data;
    }

    public async Task<List<TaskItem>> GetMyWorkAsync(bool includeCompleted = false, CancellationToken ct = default)
    {
        var path = $"WorkManagement/GetMyWork?IncludeCompleted={(includeCompleted ? "true" : "false")}";
        var resp = await GetAsync(path, ct: ct).ConfigureAwait(false);
        var tasks = ExtractTaskItems(resp);
        // Agile PM ignores IncludeCompleted=false and returns finished tasks too (71 of 74 in one account).
        return includeCompleted ? tasks : tasks.Where(t => t.IsOpen).ToList();
    }

    private static List<TaskItem> ExtractTaskItems(AgilePmEnvelope resp, string fallbackProjectId = "")
    {
        var list = new List<TaskItem>();
        foreach (var item in resp.Items)
        {
            if (item.ValueKind == JsonValueKind.Object)
            {
                // Some endpoints group tasks or wrap them in arrays inside an object
                if (item.TryGetProperty("tasks", out var nestedTasks) && nestedTasks.ValueKind == JsonValueKind.Array)
                {
                    foreach (var t in nestedTasks.EnumerateArray())
                        list.Add(TaskItem.FromJson(t, fallbackProjectId));
                }
                else if (item.TryGetProperty("projectTasks", out var pt) && pt.ValueKind == JsonValueKind.Array && !item.TryGetProperty("title", out _))
                {
                    foreach (var t in pt.EnumerateArray())
                        list.Add(TaskItem.FromJson(t, fallbackProjectId));
                }
                else
                {
                    list.Add(TaskItem.FromJson(item, fallbackProjectId));
                }
            }
        }
        return list;
    }

    // ---- Projects ----

    public async Task<List<ProjectItem>> GetProjectsAsync(bool onlyMyProjects = true, string? search = null, CancellationToken ct = default)
    {
        var allElements = await GetAllPagesAsync("ProjectSetup/getProjects", [], ct).ConfigureAwait(false);
        var identity = CurrentUser;

        var projects = allElements
            .Select(el => ProjectItem.FromJson(el, identity))
            .Where(p => !onlyMyProjects || !string.IsNullOrWhiteSpace(p.MyRole))
            .Where(p => string.IsNullOrWhiteSpace(search) || p.ProjectName.Contains(search, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.ProjectName)
            .ToList();

        return projects;
    }

    public async Task<ProjectItem> GetProjectByIdAsync(Guid projectId, CancellationToken ct = default)
    {
        var resp = await GetAsync($"ProjectSetup/getProjects?id={projectId:D}", ct: ct).ConfigureAwait(false);
        var first = resp.Items.FirstOrDefault();
        if (first.ValueKind != JsonValueKind.Object)
            throw new AgilePmApiException($"Project '{projectId}' was not found.");

        return ProjectItem.FromJson(first, CurrentUser);
    }

    // ---- Epics ----

    public async Task<List<EpicItem>> ListEpicsAsync(Guid projectId, int page = 1, int pageSize = 50, CancellationToken ct = default)
    {
        var path = $"TaskManagement/getEpics?ProjectId={projectId:D}&paging.pageNumber={page}&paging.pageSize={pageSize}";
        var resp = await GetAsync(path, ct: ct).ConfigureAwait(false);
        return resp.Items
            .Where(x => x.ValueKind == JsonValueKind.Object)
            .Select(x => EpicItem.FromJson(x, projectId.ToString()))
            .ToList();
    }

    public Task<AgilePmEnvelope> CreateEpicAsync(CreateEpicRequest request, CancellationToken ct = default) =>
        PostAsync("TaskManagement/AddEpic", request, ct);

    public async Task<AgilePmEnvelope> UpdateEpicAsync(
        Guid epicId,
        Guid projectId,
        string? title,
        string? description,
        double? estimatedEffort,
        int? status,
        int? priority,
        DateTime? startDate,
        DateTime? endDate,
        CancellationToken ct = default)
    {
        var resp = await GetAsync($"TaskManagement/getEpics?id={epicId:D}", ct: ct).ConfigureAwait(false);
        var first = resp.Items.FirstOrDefault();
        if (first.ValueKind != JsonValueKind.Object)
            throw new AgilePmApiException($"Epic '{epicId}' not found.");

        var current = EpicItem.FromJson(first, projectId.ToString());
        DateTime? curStart = DateTime.TryParse(first.GetStringOrNull("startDate"), out var s) ? s : null;
        DateTime? curEnd = DateTime.TryParse(first.GetStringOrNull("endDate"), out var e) ? e : null;

        var cmd = new UpdateEpicRequest(
            current.Id,
            current.MilestoneId,
            current.MilestoneGroupId,
            projectId.ToString(),
            current.ProjectContractId,
            current.PhaseId,
            title ?? current.Title,
            description ?? current.Description,
            estimatedEffort ?? current.EstimatedEffort,
            status ?? current.Status,
            priority ?? current.Priority,
            startDate ?? curStart,
            endDate ?? curEnd);

        return await PostAsync("TaskManagement/UpdateEpic", cmd, ct).ConfigureAwait(false);
    }

    // ---- User Stories ----

    public async Task<List<UserStoryItem>> ListUserStoriesAsync(
        Guid? projectId = null,
        Guid? epicId = null,
        bool assignedToMe = false,
        Guid? assignedToUserId = null,
        int page = 1,
        int pageSize = 50,
        CancellationToken ct = default)
    {
        var userId = assignedToMe ? CurrentUser?.UserId : assignedToUserId;
        var query = new List<string>
        {
            $"paging.pageNumber={page}",
            $"paging.pageSize={pageSize}"
        };
        if (projectId is not null) query.Add($"ProjectId={projectId:D}");
        if (epicId is not null) query.Add($"EpicId={epicId:D}");
        if (userId is not null) query.Add($"UserId={userId:D}");

        var path = "TaskManagement/getUserStorys?" + string.Join("&", query);
        var resp = await GetAsync(path, ct: ct).ConfigureAwait(false);
        return resp.Items
            .Where(x => x.ValueKind == JsonValueKind.Object)
            .Select(UserStoryItem.FromJson)
            .ToList();
    }

    /// <summary>Every user story matching the filters, reading all pages (a project can have hundreds).</summary>
    public async Task<List<UserStoryItem>> ListAllUserStoriesAsync(
        Guid projectId,
        Guid? epicId = null,
        bool assignedToMe = false,
        CancellationToken ct = default)
    {
        var query = new List<string> { $"ProjectId={projectId:D}" };
        if (epicId is not null) query.Add($"EpicId={epicId:D}");
        if (assignedToMe && CurrentUser?.UserId is { } userId) query.Add($"UserId={userId:D}");

        var items = await GetAllPagesAsync("TaskManagement/getUserStorys", query, ct).ConfigureAwait(false);
        return items
            .Where(x => x.ValueKind == JsonValueKind.Object)
            .Select(UserStoryItem.FromJson)
            .ToList();
    }

    public Task<AgilePmEnvelope> CreateUserStoryAsync(CreateUserStoryRequest request, CancellationToken ct = default) =>
        PostAsync("TaskManagement/AddUserStory", request, ct);

    public async Task<AgilePmEnvelope> UpdateUserStoryAsync(
        Guid userStoryId,
        string? story,
        string? description,
        string? acceptanceCriteria,
        PriorityEnum? priority,
        Guid? epicId,
        IReadOnlyList<string>? assignedTo,
        CancellationToken ct = default)
    {
        var resp = await GetAsync($"TaskManagement/getUserStorys?id={userStoryId:D}", ct: ct).ConfigureAwait(false);
        var first = resp.Items.FirstOrDefault();
        if (first.ValueKind != JsonValueKind.Object)
            throw new AgilePmApiException($"User story '{userStoryId}' not found.");

        var current = UserStoryItem.FromJson(first);
        var cmd = new UpdateUserStoryRequest(
            current.Id,
            story ?? current.Story,
            description ?? current.Description,
            acceptanceCriteria ?? current.AcceptanceCriteria,
            assignedTo ?? current.AssignedUserIds,
            epicId?.ToString() ?? current.EpicId,
            priority ?? current.Priority,
            current.WorkItemCategoryId);

        return await PostAsync("TaskManagement/UpdateUserStory", cmd, ct).ConfigureAwait(false);
    }

    public Task<AgilePmEnvelope> ReassignUserStoryAsync(Guid userStoryId, Guid fromUserId, Guid toUserId, CancellationToken ct = default)
    {
        var cmd = new ReAssignUserStoryRequest(userStoryId.ToString(), toUserId.ToString(), fromUserId.ToString());
        return PostAsync("TaskManagement/ReAssignUserStory", cmd, ct);
    }

    /// <summary>Deprecated in Agile PM: a story's stage follows its tasks, so the API may reject or ignore this.</summary>
    public Task<AgilePmEnvelope> MoveUserStoryStatusAsync(Guid userStoryId, UserStoryStagesEnum status, CancellationToken ct = default) =>
        PostAsync("TaskManagement/MoveUserStoryStatus", new MoveUserStoryStatusRequest(userStoryId.ToString(), status), ct);

    // ---- Project Tasks ----

    public async Task<List<TaskItem>> ListProjectTasksAsync(
        Guid? projectId = null,
        Guid? userStoryId = null,
        int page = 1,
        int pageSize = 50,
        CancellationToken ct = default)
    {
        var query = new List<string>
        {
            $"paging.pageNumber={page}",
            $"paging.pageSize={pageSize}"
        };
        if (projectId is not null) query.Add($"ProjectId={projectId:D}");
        if (userStoryId is not null) query.Add($"UserStoryId={userStoryId:D}");

        var path = "TaskManagement/getProjectTasks?" + string.Join("&", query);
        var resp = await GetAsync(path, ct: ct).ConfigureAwait(false);
        return resp.Items
            .Where(x => x.ValueKind == JsonValueKind.Object)
            .Select(x => TaskItem.FromJson(x, projectId?.ToString() ?? ""))
            .ToList();
    }

    /// <summary>Every task in a project (or under a story), reading all pages.</summary>
    public async Task<List<TaskItem>> ListAllProjectTasksAsync(Guid projectId, Guid? userStoryId = null, CancellationToken ct = default)
    {
        var query = new List<string> { $"ProjectId={projectId:D}" };
        if (userStoryId is not null) query.Add($"UserStoryId={userStoryId:D}");

        var items = await GetAllPagesAsync("TaskManagement/getProjectTasks", query, ct).ConfigureAwait(false);
        return items
            .Where(x => x.ValueKind == JsonValueKind.Object)
            .Select(x => TaskItem.FromJson(x, projectId.ToString()))
            .ToList();
    }

    public async Task<TaskItem> GetProjectTaskAsync(Guid taskId, CancellationToken ct = default)
    {
        var resp = await GetAsync($"TaskManagement/getProjectTasks?id={taskId:D}", ct: ct).ConfigureAwait(false);
        var first = resp.Items.FirstOrDefault();
        if (first.ValueKind != JsonValueKind.Object)
            throw new AgilePmApiException($"Task '{taskId}' not found.");

        return TaskItem.FromJson(first);
    }

    public Task<AgilePmEnvelope> CreateProjectTaskAsync(CreateProjectTaskRequest request, CancellationToken ct = default) =>
        PostAsync("TaskManagement/AddProjectTask", request, ct);

    public async Task<AgilePmEnvelope> UpdateProjectTaskAsync(
        Guid taskId,
        string? title,
        string? description,
        double? plannedHours,
        DateTime? endDate,
        PriorityEnum? priority,
        PriorityEnum? complexity,
        Guid? userStoryId,
        Guid? epicId,
        CancellationToken ct = default)
    {
        var current = await GetProjectTaskAsync(taskId, ct).ConfigureAwait(false);
        var cmd = new UpdateProjectTaskRequest(
            current.Id,
            title ?? current.Title,
            description ?? current.Description,
            current.Status,
            priority ?? current.Priority,
            complexity ?? current.Complexity,
            current.SkillId,
            current.AssignTo,
            plannedHours ?? current.PlannedHours,
            endDate ?? current.EndDateRaw,
            epicId?.ToString() ?? (string.IsNullOrWhiteSpace(current.EpicId) ? null : current.EpicId),
            userStoryId?.ToString() ?? (string.IsNullOrWhiteSpace(current.UserStoryId) ? null : current.UserStoryId));

        return await PostAsync("TaskManagement/UpdateProjectTask", cmd, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Moves a task or sub-task to another stage.
    /// Automatically handles Agile PM's rule that a Planned task cannot jump straight to Completed
    /// by stepping through InProgress first when needed.
    /// </summary>
    public async Task MoveTaskStatusSmartAsync(
        Guid taskId,
        GeneralStagesEnum targetStatus,
        double manHours = 0,
        Guid? subTaskId = null,
        GeneralStagesEnum? currentStatus = null,
        CancellationToken ct = default)
    {
        if (currentStatus == GeneralStagesEnum.Planned && targetStatus == GeneralStagesEnum.Completed)
        {
            Log($"Auto-stepping task {taskId} from Planned -> InProgress before marking Completed...");
            var step1 = new MoveTaskStatusRequest(taskId.ToString(), subTaskId?.ToString(), GeneralStagesEnum.InProgress, 0);
            await PostAsync("TaskManagement/MoveTaskStatus", step1, ct).ConfigureAwait(false);
        }

        var cmd = new MoveTaskStatusRequest(taskId.ToString(), subTaskId?.ToString(), targetStatus, manHours);
        await PostAsync("TaskManagement/MoveTaskStatus", cmd, ct).ConfigureAwait(false);
    }

    public Task<AgilePmEnvelope> ReassignProjectTaskAsync(Guid taskId, Guid assignToUserId, CancellationToken ct = default)
    {
        var cmd = new ReAssignProjectTaskRequest(taskId.ToString(), assignToUserId.ToString());
        return PostAsync("TaskManagement/ReAssignProjectTask", cmd, ct);
    }

    public Task<AgilePmEnvelope> PauseTaskWorkAsync(Guid taskId, CancellationToken ct = default) =>
        PostAsync("TaskManagement/PauseTaskWork", new TaskWorkActionRequest(taskId.ToString()), ct);

    public Task<AgilePmEnvelope> ResumeTaskWorkAsync(Guid taskId, CancellationToken ct = default) =>
        PostAsync("TaskManagement/ResumeTaskWork", new TaskWorkActionRequest(taskId.ToString()), ct);

    // ---- Sub-tasks ----

    public async Task<List<SubTaskItem>> ListSubTasksAsync(Guid taskId, int page = 1, int pageSize = 50, CancellationToken ct = default)
    {
        var path = $"TaskManagement/getSubTasks?ParentTaskId={taskId:D}&paging.pageNumber={page}&paging.pageSize={pageSize}";
        var resp = await GetAsync(path, ct: ct).ConfigureAwait(false);
        return resp.Items
            .Where(x => x.ValueKind == JsonValueKind.Object)
            .Select(x => SubTaskItem.FromJson(x, taskId.ToString()))
            .ToList();
    }

    public Task<AgilePmEnvelope> CreateSubTaskAsync(CreateSubTaskRequest request, CancellationToken ct = default) =>
        PostAsync("TaskManagement/AddSubTask", request, ct);

    // ---- Comments ----

    public async Task<List<CommentItem>> ListCommentsAsync(
        Guid? taskId = null,
        Guid? subTaskId = null,
        Guid? userStoryId = null,
        int page = 1,
        int pageSize = 50,
        CancellationToken ct = default)
    {
        var query = new List<string>
        {
            $"paging.pageNumber={page}",
            $"paging.pageSize={pageSize}"
        };
        if (taskId is not null) query.Add($"ProjectTaskId={taskId:D}");
        if (subTaskId is not null) query.Add($"SubTaskId={subTaskId:D}");
        if (userStoryId is not null) query.Add($"UserStoryId={userStoryId:D}");

        var path = "TaskManagement/getComments?" + string.Join("&", query);
        var resp = await GetAsync(path, ct: ct).ConfigureAwait(false);
        return resp.Items
            .Where(x => x.ValueKind == JsonValueKind.Object)
            .Select(CommentItem.FromJson)
            .ToList();
    }

    public Task<AgilePmEnvelope> AddCommentAsync(CreateCommentRequest request, CancellationToken ct = default) =>
        PostAsync("TaskManagement/AddComment", request, ct);

    // ---- Low-level HTTP transport ----

    /// <summary>
    /// A GET, answered from the short-lived cache when the same path was asked for recently or is still loading.
    /// The shared request ignores <paramref name="ct"/> so one caller giving up doesn't cancel it for the others.
    /// </summary>
    public async Task<AgilePmEnvelope> GetAsync(string relativePath, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        if (!_getCache.TryGetValue(relativePath, out var entry) || entry.ExpiresAt < now || entry.Response.IsFaulted || entry.Response.IsCanceled)
        {
            var fresh = new CachedResponse(SendWithRetryAsync(HttpMethod.Get, relativePath, null, anonymous: false, CancellationToken.None), now + GetCacheLifetime);
            entry = _getCache.AddOrUpdate(relativePath, fresh, (_, old) => old.ExpiresAt >= now && !old.Response.IsFaulted && !old.Response.IsCanceled ? old : fresh);
        }

        try
        {
            return await entry.Response.WaitAsync(ct).ConfigureAwait(false);
        }
        catch when (!ct.IsCancellationRequested)
        {
            // Don't keep a failure around; the next caller tries again.
            _getCache.TryRemove(new KeyValuePair<string, CachedResponse>(relativePath, entry));
            throw;
        }
    }

    public async Task<AgilePmEnvelope> PostAsync(string relativePath, object? body, CancellationToken ct = default)
    {
        try
        {
            return await SendWithRetryAsync(HttpMethod.Post, relativePath, body, anonymous: false, ct).ConfigureAwait(false);
        }
        finally
        {
            // A write changes what the lists return.
            ClearCache();
        }
    }

    /// <summary>
    /// Every item of a paged list. Reads the first page to learn how many there are, then fetches the rest
    /// in parallel (a few at a time) and returns them in page order.
    /// </summary>
    public async Task<IReadOnlyList<JsonElement>> GetAllPagesAsync(string path, List<string> baseQuery, CancellationToken ct)
    {
        const int pageSize = 100, maxPages = 30;
        string PagePath(int page) => $"{path}?{string.Join("&", baseQuery.Append($"paging.pageNumber={page}").Append($"paging.pageSize={pageSize}"))}";

        var first = await GetAsync(PagePath(1), ct).ConfigureAwait(false);
        var items = first.Items.ToList();
        if (first.Page is not { HasNextPage: true } info || items.Count == 0) return items;

        var lastPage = Math.Min(maxPages, info.TotalPages > 1 ? info.TotalPages : (int)Math.Ceiling(info.TotalCount / (double)pageSize));
        if (lastPage < 2)
        {
            // The API says there is more but not how much: read on one page at a time.
            for (var page = 2; page <= maxPages; page++)
            {
                var resp = await GetAsync(PagePath(page), ct).ConfigureAwait(false);
                var before = items.Count;
                items.AddRange(resp.Items);
                if (resp.Page is not { HasNextPage: true } || items.Count == before) break;
            }
            return items;
        }

        using var gate = new SemaphoreSlim(MaxParallelPages);
        var pages = Enumerable.Range(2, lastPage - 1).Select(async page =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try { return await GetAsync(PagePath(page), ct).ConfigureAwait(false); }
            finally { gate.Release(); }
        }).ToList();

        foreach (var page in await Task.WhenAll(pages).ConfigureAwait(false))
        {
            items.AddRange(page.Items);
        }
        return items;
    }

    private async Task<AgilePmEnvelope> SendWithRetryAsync(HttpMethod method, string path, object? body, bool anonymous, CancellationToken ct)
    {
        var tokenUsed = _accessToken;
        try
        {
            try
            {
                return await SendAsync(method, path, body, anonymous, ct).ConfigureAwait(false);
            }
            catch (AgilePmApiException ex) when (ex.StatusCode is null && method == HttpMethod.Get && !ct.IsCancellationRequested)
            {
                // A dropped connection or timeout on a read: try once more (reads are safe to repeat).
                Log($"Retrying {path} after: {ex.Message}");
                await Task.Delay(400, ct).ConfigureAwait(false);
                return await SendAsync(method, path, body, anonymous, ct).ConfigureAwait(false);
            }
        }
        catch (AgilePmApiException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized && !anonymous)
        {
            bool renewed;
            await _refreshLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // Another request may have renewed the token while this one waited.
                renewed = _accessToken != tokenUsed && !string.IsNullOrWhiteSpace(_accessToken)
                          || await TryRefreshTokenOrReLoginAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                _refreshLock.Release();
            }

            if (renewed)
            {
                return await SendAsync(method, path, body, anonymous, ct).ConfigureAwait(false);
            }
            throw;
        }
    }

    private async Task<AgilePmEnvelope> SendAsync(HttpMethod method, string path, object? body, bool anonymous, CancellationToken ct)
    {
        var cleanPath = path.TrimStart('/');
        using var request = new HttpRequestMessage(method, cleanPath);

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType(), options: JsonOptions);
        }

        if (!anonymous)
        {
            if (string.IsNullOrWhiteSpace(_accessToken))
            {
                throw new AgilePmApiException("You are not signed in to Agile PM. Please sign in first.");
            }
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        }

        HttpResponseMessage response;
        try
        {
            // Headers first, so the body is parsed straight from the network stream below.
            response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new AgilePmApiException($"Could not reach Agile PM at {_apiBaseUrl}. Check your network connection. ({ex.Message}{(ex.InnerException is { } inner ? " " + inner.Message : "")})");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new AgilePmApiException("Agile PM did not respond in time (request timed out).");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                Log($"HTTP {(int)response.StatusCode} from {method} {cleanPath}");
                throw CreateError(response.StatusCode, content);
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            Log($"HTTP {(int)response.StatusCode} OK <- {method} {cleanPath} ({bytes.Length / 1024.0:0.#} KB)");
            return ParseEnvelope(bytes);
        }
    }

    /// <summary>Parses the UTF-8 body directly (no intermediate string, no extra copy of the JSON tree).</summary>
    private static AgilePmEnvelope ParseEnvelope(byte[] content)
    {
        var span = content.AsSpan();
        if (span.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) span = span[3..]; // UTF-8 BOM
        if (span.Trim(" \t\r\n"u8).IsEmpty)
        {
            return new AgilePmEnvelope();
        }

        JsonElement root;
        try
        {
            var reader = new Utf8JsonReader(span);
            root = JsonElement.ParseValue(ref reader);
        }
        catch (JsonException)
        {
            return new AgilePmEnvelope { Data = JsonSerializer.SerializeToElement(Encoding.UTF8.GetString(span)) };
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return new AgilePmEnvelope { Data = root };
        }

        var isAuthEnvelope = root.TryGetProperty("isSuccess", out var isSuccess);
        if (!isAuthEnvelope && !root.TryGetProperty("succeeded", out isSuccess))
        {
            return new AgilePmEnvelope { Data = root };
        }

        var messages = ExtractErrorMessages(root);
        if (isSuccess.ValueKind == JsonValueKind.False)
        {
            throw new AgilePmApiException(messages.Count > 0
                ? $"Agile PM rejected the request: {string.Join(" ", messages)}"
                : "Agile PM rejected the request without giving a reason.");
        }

        if (isAuthEnvelope)
        {
            return new AgilePmEnvelope { Data = root, Messages = messages };
        }

        root.TryGetProperty("data", out var data);
        return new AgilePmEnvelope
        {
            Data = data.ValueKind == JsonValueKind.Undefined ? null : data,
            Messages = messages,
            Page = PageInfo.From(root)
        };
    }

    private static AgilePmApiException CreateError(HttpStatusCode statusCode, string content)
    {
        if (statusCode == HttpStatusCode.Unauthorized)
            return new AgilePmApiException("Agile PM rejected the access token (401 Unauthorized). Please sign in again.", statusCode);
        if (statusCode == HttpStatusCode.Forbidden)
            return new AgilePmApiException("You do not have permission for this action in Agile PM (403 Forbidden).", statusCode);
        if (statusCode == HttpStatusCode.NotFound)
            return new AgilePmApiException("Agile PM could not find the requested endpoint or resource (404 Not Found).", statusCode);

        string? detail = null;
        if (!string.IsNullOrWhiteSpace(content))
        {
            try
            {
                using var doc = JsonDocument.Parse(content);
                var msgs = ExtractErrorMessages(doc.RootElement);
                if (msgs.Count > 0) detail = string.Join(" ", msgs);
            }
            catch
            {
                detail = content.Length <= 400 ? content : content[..400] + "...";
            }
        }

        return new AgilePmApiException(
            $"Agile PM returned {(int)statusCode} ({statusCode}){(string.IsNullOrWhiteSpace(detail) ? "." : $": {detail}")}",
            statusCode);
    }

    private static List<string> ExtractErrorMessages(JsonElement body)
    {
        var messages = new List<string>();
        if (body.ValueKind != JsonValueKind.Object) return messages;

        CollectPropStrings(body, "messages", messages);
        CollectPropStrings(body, "message", messages);
        CollectPropStrings(body, "validationErrors", messages);

        if (body.GetStringOrNull("detail") is { } d) messages.Add(d);
        else if (body.GetStringOrNull("title") is { } t) messages.Add(t);

        if (body.TryGetProperty("errors", out var errs) && errs.ValueKind == JsonValueKind.Object)
        {
            foreach (var field in errs.EnumerateObject())
            {
                var fieldMsgs = new List<string>();
                CollectElementStrings(field.Value, fieldMsgs);
                messages.Add($"{field.Name}: {string.Join(" ", fieldMsgs)}");
            }
        }

        return messages;
    }

    private static void CollectPropStrings(JsonElement body, string prop, List<string> target)
    {
        if (body.TryGetProperty(prop, out var val)) CollectElementStrings(val, target);
    }

    private static void CollectElementStrings(JsonElement val, List<string> target)
    {
        switch (val.ValueKind)
        {
            case JsonValueKind.String when !string.IsNullOrWhiteSpace(val.GetString()):
                target.Add(val.GetString()!);
                break;
            case JsonValueKind.Array:
                foreach (var item in val.EnumerateArray()) CollectElementStrings(item, target);
                break;
        }
    }

    private static string? FindPropertyRecursive(JsonElement element, string[] propNames, bool requireJwtShape)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                var str = element.GetString();
                return (!requireJwtShape || LooksLikeJwt(str)) ? str : null;

            case JsonValueKind.Object:
                foreach (var name in propNames)
                {
                    if (element.TryGetProperty(name, out var val) && val.ValueKind == JsonValueKind.String)
                    {
                        var s = val.GetString();
                        if (!string.IsNullOrWhiteSpace(s) && (!requireJwtShape || LooksLikeJwt(s)))
                            return s;
                    }
                }
                foreach (var prop in element.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.Object && FindPropertyRecursive(prop.Value, propNames, requireJwtShape) is { } nested)
                        return nested;
                }
                if (element.TryGetProperty("data", out var d))
                    return FindPropertyRecursive(d, propNames, requireJwtShape);
                return null;

            default:
                return null;
        }
    }

    private static bool LooksLikeJwt(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Count(c => c == '.') == 2;

    private static UserIdentityInfo DecodeTokenIdentity(string token)
    {
        string[] userIdClaims =
        [
            "nameid",
            "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier",
            "uid",
            "userId",
            "UserId",
            "id",
            "sub"
        ];

        string[] emailClaims =
        [
            "email",
            "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress",
            "unique_name",
            "preferred_username"
        ];

        string[] nameClaims =
        [
            "unique_name",
            "name",
            "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name",
            "given_name"
        ];

        try
        {
            var parts = token.Split('.');
            if (parts.Length >= 2)
            {
                var payload = parts[1].Replace('-', '+').Replace('_', '/');
                payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
                using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
                var claims = doc.RootElement;

                var ids = ReadClaimList(claims, userIdClaims);
                var emails = ReadClaimList(claims, emailClaims).Where(e => e.Contains('@')).ToList();
                var names = ReadClaimList(claims, nameClaims);
                DateTimeOffset? expDate = null;
                if (claims.TryGetProperty("exp", out var exp) && exp.TryGetInt64(out var secs))
                {
                    expDate = DateTimeOffset.FromUnixTimeSeconds(secs);
                }

                return new UserIdentityInfo
                {
                    Ids = ids,
                    Emails = emails,
                    DisplayName = names.FirstOrDefault() ?? emails.FirstOrDefault(),
                    ExpiresAt = expDate
                };
            }
        }
        catch
        {
            // Ignore malformed JWT claims
        }

        return new UserIdentityInfo { DisplayName = "Authenticated User" };
    }

    private static List<string> ReadClaimList(JsonElement claims, string[] claimNames)
    {
        var list = new List<string>();
        foreach (var name in claimNames)
        {
            if (!claims.TryGetProperty(name, out var c)) continue;
            if (c.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(c.GetString()))
                list.Add(c.GetString()!);
            else if (c.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in c.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                        list.Add(item.GetString()!);
                }
            }
        }
        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static HttpClient CreateHttpClient(string baseUrl)
    {
        var handler = new SocketsHttpHandler
        {
            // Agile PM's JSON lists compress around 10x; gzip/brotli cuts download time on big projects.
            AutomaticDecompression = DecompressionMethods.All,
            // Keep connections open between requests and allow parallel page reads, but pick up DNS changes.
            // Agile PM closes idle connections after a few seconds; reusing one of those fails the request, so
            // idle connections are dropped first.
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(8),
            MaxConnectionsPerServer = MaxParallelPages + 2,
        };
        // HTTP/1.1 on purpose: Agile PM's HTTP/2 endpoint drops requests when several are sent at once.
        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri(baseUrl),
            Timeout = TimeSpan.FromSeconds(35),
        };
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    private void Log(string message)
    {
        OnApiLog?.Invoke($"[{DateTime.Now:HH:mm:ss}] {message}");
    }
}
