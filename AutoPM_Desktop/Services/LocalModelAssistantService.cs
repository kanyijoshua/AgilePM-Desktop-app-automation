using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AutoPM_Desktop.Services;

/// <summary>
/// Assistant backed by any OpenAI-compatible chat endpoint serving open models: hosted free tiers (Groq, OpenRouter,
/// Hugging Face, GitHub Models, Cerebras, Mistral; see <see cref="OpenModelPresets"/>) or local servers such as
/// Ollama and LM Studio (no API key). Runs the same tool loop as the Claude backend.
/// </summary>
public sealed partial class LocalModelAssistantService : IAiAssistant
{
    public const string DefaultBaseUrl = "http://localhost:11434/v1/"; // Ollama
    public const string DefaultModel = "llama3.1:8b";

    /// <summary>Tool results are cut to this size so they fit the small context windows of local models.</summary>
    private const int MaxToolResultChars = 6000;

    /// <summary>Tool results from earlier turns are cut further when the history is replayed.</summary>
    private const int OldToolResultChars = 400;

    /// <summary>
    /// Used when the request budget is tight: the tools that cover most requests, so the definitions stay small.
    /// </summary>
    private static readonly HashSet<string> MinimalTools = new(StringComparer.Ordinal)
    {
        "get_my_work", "list_user_stories", "get_user_story", "list_project_tasks", "get_project_task", "create_project_task",
        "update_project_task", "move_task_status", "add_comment", "bulk_create_project_tasks", "get_project_progress",
        "find_overdue_tasks", "list_project_team_members",
    };

    /// <summary>
    /// The tools small local models get by default. Each tool schema costs context, and 8B-class models pick tools
    /// more reliably from a short list; "Use all tools" in the panel sends everything.
    /// </summary>
    private static readonly HashSet<string> CoreTools = new(StringComparer.Ordinal)
    {
        "list_my_projects", "list_project_team_members", "get_my_work", "list_epics", "list_user_stories",
        "get_user_story", "list_project_tasks", "get_project_task", "create_project_task", "update_project_task",
        "move_task_status", "reassign_project_task", "create_sub_task", "add_comment", "create_user_story",
        "bulk_create_project_tasks", "bulk_move_task_status", "get_standup_report", "find_overdue_tasks", "get_project_progress",
    };

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    /// <summary>Free hosted tiers rate-limit hard; wait and retry this many times on 429 before giving up.</summary>
    private const int MaxRateLimitRetries = 3;
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);

    private readonly List<JsonObject> _history = [];
    private string _baseUrl = DefaultBaseUrl;
    private string _model = DefaultModel;
    private string? _apiKey;
    private bool _useAllTools;
    private string _providerName = "Local";
    private bool _keyRequired;
    private int _requestBudget = OpenModelPreset.DefaultRequestTokens;

    /// <summary>
    /// The most tokens one request (prompt + answer) may hold. Free hosted tiers cap it (Groq: 8,000 per minute),
    /// so each request is fitted to it, and it shrinks when a service still says a request was too large.
    /// </summary>
    public int RequestBudgetTokens => _requestBudget;

    private int MaxAnswerTokens => Math.Clamp(_requestBudget / 5, 700, 2048);

    public string DisplayName => $"{_providerName} · {_model}";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_model)
        && Uri.IsWellFormedUriString(_baseUrl, UriKind.Absolute)
        && (!_keyRequired || _apiKey is not null);

    public string NotConfiguredHint => _keyRequired && _apiKey is null
        ? $"{_providerName} needs a (free) API key. Add it in the AI Assistant panel and press Save."
        : "Set the open model's server URL and model name in the AI Assistant panel.";

    public string BaseUrl => _baseUrl;

    /// <param name="providerName">Shown in the UI, e.g. "Groq" or "Ollama".</param>
    /// <param name="keyRequired">Hosted services need a key; local servers don't.</param>
    public void Configure(string? baseUrl, string? model, string? apiKey, bool useAllTools, string providerName = "Local", bool keyRequired = false,
        int requestBudgetTokens = OpenModelPreset.DefaultRequestTokens)
    {
        _requestBudget = Math.Max(2500, requestBudgetTokens);
        _baseUrl = NormalizeBaseUrl(baseUrl);
        _model = string.IsNullOrWhiteSpace(model) ? DefaultModel : model.Trim();
        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
        _useAllTools = useAllTools;
        _providerName = string.IsNullOrWhiteSpace(providerName) ? "Local" : providerName;
        _keyRequired = keyRequired;
    }

    public void ResetConversation() => _history.Clear();

    /// <summary>"http://localhost:11434" -> "http://localhost:11434/v1/"; keeps an explicit path such as "/v1" or "/api/v1".</summary>
    public static string NormalizeBaseUrl(string? raw)
    {
        var url = string.IsNullOrWhiteSpace(raw) ? DefaultBaseUrl : raw.Trim();
        if (!url.Contains("://", StringComparison.Ordinal)) url = "http://" + url;
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.AbsolutePath is "" or "/")
        {
            url = url.TrimEnd('/') + "/v1";
        }
        return url.TrimEnd('/') + "/";
    }

    /// <summary>
    /// Chat models the server offers (GET /models): what's been pulled into Ollama, or a hosted catalogue.
    /// Models a service marks as not supporting tools are left out, and free models (":free") come first.
    /// </summary>
    public static async Task<IReadOnlyList<string>> ListModelsAsync(string? baseUrl, string? apiKey, CancellationToken ct = default)
    {
        var url = NormalizeBaseUrl(baseUrl);
        var key = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
        var body = await SendRawAsync(() => NewRequest(HttpMethod.Get, url + "models", key), url, model: null, ct);

        using var doc = JsonDocument.Parse(body);
        var models = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement
            : doc.RootElement.TryGetProperty("data", out var data) ? data : default;
        if (models.ValueKind != JsonValueKind.Array) return [];

        return models.EnumerateArray()
            .Where(SupportsTools)
            .Select(m => m.TryGetProperty("id", out var id) ? id.GetString() : null)
            .OfType<string>()
            .Where(id => !id.Contains("embed", StringComparison.OrdinalIgnoreCase) && !id.Contains("whisper", StringComparison.OrdinalIgnoreCase))
            .OrderBy(id => id.EndsWith(":free", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // OpenRouter lists "supported_parameters"; other servers don't say, so keep their models.
        static bool SupportsTools(JsonElement m) =>
            !m.TryGetProperty("supported_parameters", out var p) || p.ValueKind != JsonValueKind.Array
            || p.EnumerateArray().Any(x => x.GetString() == "tools");
    }

    public async Task<string> SendAsync(
        string userText,
        string contextNote,
        AgilePmToolbox toolbox,
        Func<string, string, Task<bool>> approveWrite,
        Action<AssistantEvent> onEvent,
        CancellationToken ct)
    {
        var tools = new JsonArray(toolbox.Tools
            .Where(t => _useAllTools || CoreTools.Contains(t.Name))
            .Select(ToFunctionTool)
            .ToArray<JsonNode?>());

        _history.Add(new JsonObject { ["role"] = "user", ["content"] = AssistantPrompts.UserTurn(contextNote, userText) });

        var finalText = new List<string>();
        var turn = new ToolTurn();
        for (var round = 0; round < AssistantPrompts.MaxToolRounds; round++)
        {
            // Out of calls (or rounds): ask for the answer from what has been gathered instead of giving up.
            var answerNow = turn.MustAnswer || round == AssistantPrompts.MaxToolRounds - 1;
            using var response = JsonDocument.Parse(await PostFittedChatAsync(tools, onEvent, ct, answerNow));
            var choice = response.RootElement.GetProperty("choices")[0];
            var message = choice.GetProperty("message");
            var finishReason = choice.TryGetProperty("finish_reason", out var fr) ? fr.GetString() : null;

            var content = StripThinking(message.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null);
            if (!string.IsNullOrWhiteSpace(content))
            {
                finalText.Add(content);
                onEvent(new AssistantEvent(AssistantEventKind.Text, content));
            }

            var calls = ReadToolCalls(message, round);
            var assistant = new JsonObject { ["role"] = "assistant", ["content"] = content ?? "" };
            if (calls.Count > 0)
            {
                assistant["tool_calls"] = new JsonArray(calls.Select(call => (JsonNode?)new JsonObject
                {
                    ["id"] = call.Id,
                    ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = call.Name, ["arguments"] = call.RawArguments },
                }).ToArray());
            }
            _history.Add(assistant);

            if (calls.Count == 0)
            {
                if (finishReason == "length")
                {
                    onEvent(new AssistantEvent(AssistantEventKind.Notice, "The answer was cut off because it reached the length limit."));
                }
                return string.Join("\n\n", finalText);
            }

            foreach (var call in calls)
            {
                string resultText;
                if (call.Arguments is null)
                {
                    resultText = $"Your arguments for {call.Name} were not valid JSON. Call the tool again with a JSON object.";
                    onEvent(new AssistantEvent(AssistantEventKind.ToolFailed, resultText, call.Name));
                }
                else
                {
                    (resultText, _) = await turn.RunAsync(call.Name, call.Arguments, toolbox, approveWrite, onEvent, ct);
                }

                if (resultText.Length > MaxToolResultChars)
                {
                    resultText = resultText[..MaxToolResultChars] + "\n…(truncated; use the tool's filters for a narrower result)";
                }

                _history.Add(new JsonObject
                {
                    ["role"] = "tool",
                    ["tool_call_id"] = call.Id,
                    ["name"] = call.Name,
                    ["content"] = resultText,
                });
            }
        }

        const string limit = "Stopped after many tool calls without finishing. Ask a narrower question or continue from here.";
        onEvent(new AssistantEvent(AssistantEventKind.Notice, limit));
        return limit;
    }

    public async Task<IReadOnlyList<DraftTask>> DraftTasksForStoryAsync(
        string story, string? description, string? acceptanceCriteria, IEnumerable<string> existingTaskTitles, CancellationToken ct)
    {
        var prompt = AssistantPrompts.DraftTasks(story, description, acceptanceCriteria, existingTaskTitles) +
            "\n\nAnswer with JSON only, in this shape: {\"tasks\": [{\"title\": \"...\", \"description\": \"...\", \"hours\": 2, \"priority\": \"Medium\"}]}";

        var request = SingleTurn(prompt);
        request["response_format"] = new JsonObject
        {
            ["type"] = "json_schema",
            ["json_schema"] = new JsonObject
            {
                ["name"] = "draft_tasks",
                ["strict"] = true,
                ["schema"] = JsonNode.Parse(JsonSerializer.Serialize(AssistantPrompts.DraftTasksSchema())),
            },
        };

        string body;
        try
        {
            body = await PostChatAsync(request, ct);
        }
        catch (LocalModelException ex) when (ex.StatusCode == HttpStatusCode.BadRequest)
        {
            // Older servers don't support structured output; the prompt still asks for JSON.
            request.Remove("response_format");
            body = await PostChatAsync(request, ct);
        }

        return AssistantPrompts.ParseDraftTasks(ReadContent(body));
    }

    public async Task<string> PolishStandupAsync(string rawReport, CancellationToken ct) =>
        ReadContent(await PostChatAsync(SingleTurn(AssistantPrompts.PolishStandup(rawReport)), ct));

    public async Task<string> CompleteAsync(string prompt, Dictionary<string, JsonElement>? jsonSchema, int maxAnswerTokens, CancellationToken ct)
    {
        var request = SingleTurn(prompt);
        request["max_tokens"] = maxAnswerTokens;
        if (jsonSchema is null)
        {
            return ReadContent(await PostChatAsync(request, ct));
        }

        // Structured output where the service supports it, then plain JSON mode, then just the prompt's instructions.
        request["response_format"] = new JsonObject
        {
            ["type"] = "json_schema",
            ["json_schema"] = new JsonObject
            {
                ["name"] = "result",
                ["strict"] = true,
                ["schema"] = JsonNode.Parse(JsonSerializer.Serialize(jsonSchema)),
            },
        };
        try
        {
            return ReadContent(await PostChatAsync(request, ct));
        }
        catch (LocalModelException ex) when (ex.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity)
        {
            request["response_format"] = new JsonObject { ["type"] = "json_object" };
        }

        try
        {
            return ReadContent(await PostChatAsync(request, ct));
        }
        catch (LocalModelException ex) when (ex.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity)
        {
            request.Remove("response_format");
            return ReadContent(await PostChatAsync(request, ct));
        }
    }

    /// <summary>
    /// Sends the conversation fitted to the request budget. If the service still says it is too large (HTTP 413, or
    /// a "reduce your message size" error), the budget shrinks to what the service reported and the call is retried.
    /// </summary>
    private async Task<string> PostFittedChatAsync(JsonArray tools, Action<AssistantEvent> onEvent, CancellationToken ct, bool answerNow = false)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await PostChatAsync(BuildFittedRequest(tools, answerNow), ct);
            }
            catch (LocalModelException ex) when (ex.IsRequestTooLarge && attempt < 2)
            {
                var smaller = ex.ReportedTokenLimit is { } limit ? (int)(limit * 0.8) : (int)(_requestBudget * 0.65);
                _requestBudget = Math.Max(2500, Math.Min(smaller, (int)(_requestBudget * 0.85)));
                onEvent(new AssistantEvent(AssistantEventKind.Notice,
                    $"{_providerName} said the request was too large; retrying with less history (limit now about {_requestBudget:N0} tokens)."));
            }
        }
    }

    /// <summary>
    /// Builds the chat request within <see cref="RequestBudgetTokens"/>: tool definitions are shortened (then cut to a
    /// minimal set) when they take too much room, older turns are dropped whole (oldest first) and their tool results
    /// cut short, and the current turn's tool results are shortened until everything fits.
    /// </summary>
    private JsonObject BuildFittedRequest(JsonArray tools, bool answerNow = false)
    {
        var inputBudget = Math.Max(1500, _requestBudget - MaxAnswerTokens);

        JsonNode toolsNode = tools.DeepClone();
        if (Estimate(toolsNode) > inputBudget / 2) toolsNode = CompactTools(tools, minimalOnly: false);
        if (Estimate(toolsNode) > inputBudget / 2) toolsNode = CompactTools(tools, minimalOnly: true);

        var remaining = inputBudget - Estimate(toolsNode) - TextTools.EstimateTokens(AssistantPrompts.System) - 50;

        // The current turn (from the last user message) always goes; shrink its tool results until it fits. Older
        // results go first: cutting the one the model is reading now just makes it call the same tool again.
        var lastUser = Math.Max(0, _history.FindLastIndex(m => (string?)m["role"] == "user"));
        var current = _history.Skip(lastUser).Select(m => (JsonObject)m.DeepClone()).ToList();
        var results = current.Where(m => (string?)m["role"] == "tool").ToList();
        for (var cap = MaxToolResultChars / 2; Estimate(current) > remaining && cap >= 250; cap /= 2)
        {
            ShrinkToolResults(results.Take(Math.Max(0, results.Count - 1)), cap);
        }
        for (var cap = MaxToolResultChars / 2; Estimate(current) > remaining && cap >= 250; cap /= 2)
        {
            ShrinkToolResults(results, cap);
        }

        // Earlier turns, newest first, while they fit. Whole turns only, so tool calls keep their results.
        var used = Estimate(current);
        var earlier = new List<JsonObject>();
        var turnEnd = lastUser;
        while (turnEnd > 0)
        {
            var turnStart = _history.FindLastIndex(turnEnd - 1, m => (string?)m["role"] == "user");
            if (turnStart < 0) break;

            var turn = _history.Skip(turnStart).Take(turnEnd - turnStart).Select(m => (JsonObject)m.DeepClone()).ToList();
            ShrinkToolResults(turn, OldToolResultChars);
            var cost = Estimate(turn);
            if (used + cost > remaining) break;

            earlier.InsertRange(0, turn);
            used += cost;
            turnEnd = turnStart;
        }

        var messages = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = AssistantPrompts.System } };
        foreach (var m in earlier.Concat(current)) messages.Add(m);

        return new JsonObject
        {
            ["model"] = _model,
            ["messages"] = messages,
            ["tools"] = toolsNode,
            ["tool_choice"] = answerNow ? "none" : "auto",
            ["temperature"] = 0.2,
            ["max_tokens"] = MaxAnswerTokens,
            ["stream"] = false,
        };
    }

    private static int Estimate(JsonNode node) => TextTools.EstimateTokens(node.ToJsonString());

    private static int Estimate(IEnumerable<JsonObject> messages) => messages.Sum(m => Estimate(m));

    private static void ShrinkToolResults(IEnumerable<JsonObject> messages, int maxChars)
    {
        foreach (var m in messages.Where(m => (string?)m["role"] == "tool"))
        {
            if (m["content"]?.GetValue<string>() is { } content && content.Length > maxChars)
            {
                m["content"] = content[..maxChars] + "…(cut to save tokens)";
            }
        }
    }

    /// <summary>Tool definitions with one-sentence descriptions and no parameter descriptions (a fraction of the tokens).</summary>
    private static JsonArray CompactTools(JsonArray tools, bool minimalOnly)
    {
        var result = new JsonArray();
        foreach (var tool in tools.OfType<JsonObject>())
        {
            var fn = (JsonObject)tool["function"]!.DeepClone();
            var name = fn["name"]?.GetValue<string>() ?? "";
            if (minimalOnly && !MinimalTools.Contains(name)) continue;

            var description = fn["description"]?.GetValue<string>() ?? name;
            var stop = description.IndexOf(". ", StringComparison.Ordinal);
            fn["description"] = stop > 0 ? description[..(stop + 1)] : description;
            if (fn["parameters"]?["properties"] is JsonObject properties)
            {
                foreach (var property in properties.Select(p => p.Value).OfType<JsonObject>())
                {
                    property.Remove("description");
                    if (property["items"] is JsonObject items && items["properties"] is JsonObject inner)
                    {
                        foreach (var p in inner.Select(p => p.Value).OfType<JsonObject>()) p.Remove("description");
                    }
                }
            }

            result.Add(new JsonObject { ["type"] = "function", ["function"] = fn });
        }
        return result;
    }

    private JsonObject SingleTurn(string prompt) => new()
    {
        ["model"] = _model,
        ["messages"] = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = prompt } },
        ["temperature"] = 0.2,
        ["stream"] = false,
    };

    private async Task<string> PostChatAsync(JsonObject body, CancellationToken ct)
    {
        var json = body.ToJsonString();
        return await SendRawAsync(() =>
        {
            var request = NewRequest(HttpMethod.Post, _baseUrl + "chat/completions", _apiKey);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            return request;
        }, _baseUrl, _model, ct);
    }

    private static HttpRequestMessage NewRequest(HttpMethod method, string url, string? apiKey)
    {
        var request = new HttpRequestMessage(method, url);
        if (apiKey is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        // OpenRouter shows these on its dashboard; other services ignore them.
        request.Headers.TryAddWithoutValidation("X-Title", "AgilePM Desktop");
        return request;
    }

    /// <summary>Sends a request (rebuilt for each attempt) and retries on 429 rate limits, which free hosted tiers hit often.</summary>
    private static async Task<string> SendRawAsync(Func<HttpRequestMessage> createRequest, string baseUrl, string? model, CancellationToken ct)
    {
        var isLocal = Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) && uri.IsLoopback;
        for (var attempt = 0; ; attempt++)
        {
            HttpResponseMessage response;
            using var request = createRequest();
            try
            {
                response = await Http.SendAsync(request, ct);
            }
            catch (HttpRequestException ex)
            {
                throw new LocalModelException(isLocal
                    ? $"Could not reach the model server at {baseUrl}. Start Ollama (or LM Studio's local server) and check the URL. ({ex.Message})"
                    : $"Could not reach {baseUrl}. Check your internet connection and the URL. ({ex.Message})");
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new LocalModelException("The model took too long to answer. Try a smaller or faster model, or a shorter question.");
            }

            using (response)
            {
                var text = await response.Content.ReadAsStringAsync(ct);
                if (response.IsSuccessStatusCode) return text;

                if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < MaxRateLimitRetries)
                {
                    var wait = response.Headers.RetryAfter?.Delta
                        ?? (response.Headers.RetryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : (TimeSpan?)null)
                        ?? TimeSpan.FromSeconds(4 << attempt);
                    await Task.Delay(wait < TimeSpan.Zero ? TimeSpan.FromSeconds(1) : wait > MaxRetryDelay ? MaxRetryDelay : wait, ct);
                    continue;
                }

                var detail = text.Length > 400 ? text[..400] + "…" : text;
                var hint = response.StatusCode switch
                {
                    HttpStatusCode.NotFound when model is not null && isLocal =>
                        $" Model '{model}' may not be installed: run `ollama pull {model}` or load it in LM Studio.",
                    HttpStatusCode.NotFound or HttpStatusCode.BadRequest when model is not null =>
                        $" Check that '{model}' exists on this service and supports tool calling (press ⟳ to list models).",
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                        " The API key is missing, wrong or lacks permission for this model.",
                    HttpStatusCode.TooManyRequests =>
                        " The free tier's rate limit was reached; wait a minute or pick another model or service.",
                    HttpStatusCode.PaymentRequired =>
                        " This model isn't free on this service; pick a free one (e.g. ending in ':free' on OpenRouter).",
                    _ => "",
                };
                throw new LocalModelException($"The model service returned {(int)response.StatusCode}: {detail}{hint}", response.StatusCode);
            }
        }
    }

    private static string ReadContent(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var message = doc.RootElement.GetProperty("choices")[0].GetProperty("message");
        return StripThinking(message.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null) ?? "";
    }

    private sealed record ToolCall(string Id, string Name, string RawArguments, IReadOnlyDictionary<string, object?>? Arguments);

    private static List<ToolCall> ReadToolCalls(JsonElement message, int round)
    {
        var calls = new List<ToolCall>();
        if (!message.TryGetProperty("tool_calls", out var toolCalls) || toolCalls.ValueKind != JsonValueKind.Array)
        {
            return calls;
        }

        foreach (var tc in toolCalls.EnumerateArray())
        {
            if (!tc.TryGetProperty("function", out var fn) || !fn.TryGetProperty("name", out var nameEl)) continue;

            var id = tc.TryGetProperty("id", out var idEl) && idEl.GetString() is { Length: > 0 } s ? s : $"call_{round}_{calls.Count}";
            var raw = fn.TryGetProperty("arguments", out var args)
                ? args.ValueKind == JsonValueKind.String ? args.GetString() ?? "{}" : args.GetRawText()
                : "{}";

            calls.Add(new ToolCall(id, nameEl.GetString() ?? "", raw, ParseArguments(raw)));
        }

        return calls;
    }

    private static IReadOnlyDictionary<string, object?>? ParseArguments(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(raw) ? "{}" : raw);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                ? doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone())
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonNode ToFunctionTool(AssistantTool tool) => new JsonObject
    {
        ["type"] = "function",
        ["function"] = new JsonObject
        {
            ["name"] = tool.Name,
            ["description"] = tool.Description,
            ["parameters"] = Simplify(JsonNode.Parse(tool.InputSchema.GetRawText())),
        },
    };

    /// <summary>
    /// Some hosted providers reject parts of JSON Schema: nullable unions (["string", "null"]),
    /// "default": null and "format". Optional parameters are already outside "required", so these can go.
    /// </summary>
    private static JsonNode? Simplify(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj["type"] is JsonArray types)
                {
                    var nonNull = types.Select(t => t?.GetValue<string>()).Where(t => t is not null and not "null").ToList();
                    obj["type"] = nonNull.Count == 1 ? JsonValue.Create(nonNull[0]) : new JsonArray(nonNull.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray());
                }
                if (obj["default"] is null && obj.ContainsKey("default")) obj.Remove("default");
                // Only the keyword (a string); a parameter that happens to be called "format" is an object.
                if (obj["format"] is JsonValue) obj.Remove("format");
                foreach (var key in obj.Select(p => p.Key).ToList()) Simplify(obj[key]);
                break;
            case JsonArray array:
                foreach (var item in array) Simplify(item);
                break;
        }
        return node;
    }

    /// <summary>Reasoning models (Qwen3, DeepSeek-R1) put their reasoning in &lt;think&gt; tags; don't show it as the answer.</summary>
    private static string? StripThinking(string? text) =>
        text is null ? null : ThinkTags().Replace(text, "").Trim();

    [GeneratedRegex("<think>.*?</think>", RegexOptions.Singleline)]
    private static partial Regex ThinkTags();
}

public sealed partial class LocalModelException(string message, HttpStatusCode? statusCode = null) : Exception(message)
{
    public HttpStatusCode? StatusCode { get; } = statusCode;

    /// <summary>The service refused the request for its size (not a short-term rate limit that waiting fixes).</summary>
    public bool IsRequestTooLarge =>
        StatusCode == HttpStatusCode.RequestEntityTooLarge
        || Message.Contains("Request too large", StringComparison.OrdinalIgnoreCase)
        || Message.Contains("reduce your message size", StringComparison.OrdinalIgnoreCase)
        || Message.Contains("context length", StringComparison.OrdinalIgnoreCase)
        || Message.Contains("context_length_exceeded", StringComparison.OrdinalIgnoreCase);

    /// <summary>The limit the service quoted, e.g. 8000 from Groq's "Limit 8000, Requested 9430".</summary>
    public int? ReportedTokenLimit => LimitPattern().Match(Message) is { Success: true } m && int.TryParse(m.Groups[1].Value, out var limit)
        ? limit
        : null;

    [GeneratedRegex(@"Limit\s+(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex LimitPattern();
}
