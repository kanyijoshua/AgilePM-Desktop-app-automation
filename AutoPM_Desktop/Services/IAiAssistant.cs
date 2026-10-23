using System.Text.Json;

namespace AutoPM_Desktop.Services;

/// <summary>What the assistant is doing, reported to the UI while a turn runs.</summary>
public sealed record AssistantEvent(AssistantEventKind Kind, string Text, string? ToolName = null);

public enum AssistantEventKind { Text, ToolStarted, ToolFinished, ToolFailed, ToolDeclined, Notice }

/// <summary>A task drafted by the model for the batch creator.</summary>
public sealed record DraftTask(string Title, string Description, double Hours, string Priority);

/// <summary>Which model backend the AI features use.</summary>
public enum AiProviderKind
{
    /// <summary>Claude through the Anthropic API (needs an API key).</summary>
    Claude,

    /// <summary>Any OpenAI-compatible endpoint, e.g. a free local model in Ollama or LM Studio (no key needed).</summary>
    Local,
}

/// <summary>A model backend for the AI assistant and the quick AI actions.</summary>
public interface IAiAssistant
{
    /// <summary>Short label for the UI, e.g. "Claude · claude-opus-5-5".</summary>
    string DisplayName { get; }

    bool IsConfigured { get; }

    /// <summary>What the user must do when <see cref="IsConfigured"/> is false.</summary>
    string NotConfiguredHint { get; }

    void ResetConversation();

    /// <summary>
    /// Runs one user turn: sends the message, executes the Agile PM tools the model asks for, and loops until it answers.
    /// Tools that change data go through <paramref name="approveWrite"/> first.
    /// </summary>
    Task<string> SendAsync(
        string userText,
        string contextNote,
        AgilePmToolbox tools,
        Func<string, string, Task<bool>> approveWrite,
        Action<AssistantEvent> onEvent,
        CancellationToken ct);

    /// <summary>
    /// Drafts tasks for a user story. <c>codeContext</c> holds notes on the project's code folder and git history
    /// (from the project folder), or null.
    /// </summary>
    Task<IReadOnlyList<DraftTask>> DraftTasksForStoryAsync(
        string story, string? description, string? acceptanceCriteria, IEnumerable<string> existingTaskTitles, string? codeContext, CancellationToken ct);

    Task<string> PolishStandupAsync(string rawReport, CancellationToken ct);

    /// <summary>
    /// About how many tokens one request may hold, prompt and answer together. Features that send project data
    /// size it to fit (free hosted tiers allow far less than Claude).
    /// </summary>
    int RequestBudgetTokens { get; }

    /// <summary>
    /// One prompt, one answer, no tools. With <paramref name="jsonSchema"/> the answer is JSON in that shape
    /// (structured output where the backend supports it).
    /// </summary>
    Task<string> CompleteAsync(string prompt, Dictionary<string, JsonElement>? jsonSchema, int maxAnswerTokens, CancellationToken ct);
}

/// <summary>Instructions shared by every model backend, so they behave the same way.</summary>
internal static class AssistantPrompts
{
    public const int MaxToolRounds = 15;

    // Kept short on purpose: it is sent with every request, and free hosted tiers allow only a few thousand tokens.
    public const string System = """
        You are the AI assistant in AgilePM Desktop. You plan, track and update the signed-in user's Agile PM work with your tools.
        - Work in the active project from the context note unless the user names another project. Tools use the active project when you leave projectId out.
        - Look up IDs, names and stages with the tools; never guess them. Prefer bulk and report tools (bulk_create_project_tasks, bulk_move_task_status, get_project_progress, find_overdue_tasks, get_standup_report) over many single calls.
        - One well-filtered call usually answers: get_my_work for the user's own tasks; list_project_tasks with status, assignedToMe, assigneeId or search for others. Results are already sorted by urgency and include totals, so don't page through lists or repeat a call.
        - Before changing several items, state the plan in one line, then make the calls: the app asks the user to approve each change.
        - If a tool fails, fix the call or explain what blocked you.
        - Answer briefly with bullets or small tables. Never invent tasks, people or numbers.
        """;

    /// <summary>Reads the first JSON object in a model's answer; tolerates prose or code fences around it.</summary>
    public static JsonDocument ParseJsonObject(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            throw new InvalidOperationException("The model did not answer in the expected format. Try again, or pick a larger model.");
        }

        try
        {
            return JsonDocument.Parse(text[start..(end + 1)]);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("The model's answer was not valid JSON. Try again, or pick a larger model.");
        }
    }

    /// <summary>A JSON schema object: {"type":"object","properties":...,"required":[all],"additionalProperties":false}.</summary>
    public static Dictionary<string, JsonElement> ObjectSchema(object properties, params string[] required) => new()
    {
        ["type"] = JsonSerializer.SerializeToElement("object"),
        ["properties"] = JsonSerializer.SerializeToElement(properties),
        ["required"] = JsonSerializer.SerializeToElement(required),
        ["additionalProperties"] = JsonSerializer.SerializeToElement(false),
    };

    public const string DeclinedToolResult = "The user declined this change. Don't retry it; ask what they would like instead.";

    public static string UserTurn(string contextNote, string userText) => $"<context>\n{contextNote}\n</context>\n\n{userText}";

    /// <summary>How much codebase text a task draft can carry: about half of what the request has left (0 = none).</summary>
    public static int CodeContextChars(int requestBudgetTokens)
    {
        var chars = (int)Math.Min(12_000, (requestBudgetTokens - 2500) * 3.3 / 2);
        return chars < 600 ? 0 : chars;
    }

    public static string DraftTasks(string story, string? description, string? acceptanceCriteria, IEnumerable<string> existingTaskTitles, string? codeContext = null)
    {
        var existing = string.Join("\n", existingTaskTitles.Select(t => $"- {t}"));
        return $"""
            Break this user story into 3 to 8 implementation tasks for a software team.
            Together the tasks must cover every acceptance criterion. Don't repeat the existing tasks listed below.
            Titles: short, action-first (e.g. "Add validation to the sign-up form"). Description: one sentence.
            Hours: a realistic estimate between 0.5 and 16. Priority: Low, Medium or High.

            <story>{story}</story>
            <description>{description}</description>
            <acceptance_criteria>{acceptanceCriteria}</acceptance_criteria>
            <existing_tasks>
            {(existing.Length == 0 ? "(none)" : existing)}
            </existing_tasks>
            """ + CodebaseSection(codeContext);
    }

    /// <summary>The project folder notes for a task-drafting prompt, with how to use them; "" when there are none.</summary>
    public static string CodebaseSection(string? codeContext) => string.IsNullOrWhiteSpace(codeContext) ? "" : $"""


        The project's code folder and git history are below. Ground the tasks in them: name the real files, folders
        or components to change where that is clear, and leave out work the commits or uncommitted changes show is
        already done. Never invent files that aren't listed.
        <codebase>
        {codeContext}
        </codebase>
        """;

    /// <summary>JSON schema of the draft-tasks answer: {"tasks": [{title, description, hours, priority}]}.</summary>
    public static Dictionary<string, JsonElement> DraftTasksSchema() => new()
    {
        ["type"] = JsonSerializer.SerializeToElement("object"),
        ["properties"] = JsonSerializer.SerializeToElement(new
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
                    },
                    required = new[] { "title", "description", "hours", "priority" },
                    additionalProperties = false,
                },
            },
        }),
        ["required"] = JsonSerializer.SerializeToElement(new[] { "tasks" }),
        ["additionalProperties"] = JsonSerializer.SerializeToElement(false),
    };

    /// <summary>Reads the draft-tasks JSON; tolerates prose or code fences around it (smaller models add them).</summary>
    public static IReadOnlyList<DraftTask> ParseDraftTasks(string json)
    {
        var start = json.IndexOf('{');
        var end = json.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            throw new InvalidOperationException("The model did not return a task list.");
        }

        using var doc = JsonDocument.Parse(json[start..(end + 1)]);
        if (!doc.RootElement.TryGetProperty("tasks", out var tasks) || tasks.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("The model did not return a task list.");
        }

        return tasks.EnumerateArray()
            .Where(t => t.ValueKind == JsonValueKind.Object)
            .Select(t => new DraftTask(
                Read(t, "title"),
                Read(t, "description"),
                t.TryGetProperty("hours", out var h) && h.ValueKind == JsonValueKind.Number ? h.GetDouble()
                    : double.TryParse(Read(t, "hours"), out var parsed) ? parsed : 2,
                Read(t, "priority") is { Length: > 0 } p ? p : "Medium"))
            .Where(t => !string.IsNullOrWhiteSpace(t.Title))
            .ToList();

        static string Read(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString()) : "";
    }

    public static string PolishStandup(string rawReport) => $"""
        Rewrite this task report as my standup update for the team chat. Three short sections:
        Yesterday / done, Today, Blockers & risks (returned and overdue items count as risks; say "None" if there are none).
        Plain text with simple bullets, first person, no preamble. Use only the tasks in the report.

        <report>
        {rawReport}
        </report>
        """;
}

/// <summary>
/// One user turn's tool calls. Small models tend to repeat the same read or page through long lists; a repeated read
/// is answered from the earlier result without calling Agile PM again, and after a few calls the results remind the
/// model to answer. Backends stop offering tools once <see cref="MustAnswer"/> is true.
/// </summary>
internal sealed class ToolTurn
{
    /// <summary>From this many calls on, each result tells the model to answer with what it has.</summary>
    public const int WrapUpAfterCalls = 4;

    /// <summary>After this many calls the next request offers no tools, so the model has to answer.</summary>
    public const int AnswerAfterCalls = 8;

    private readonly HashSet<string> _readsDone = new(StringComparer.Ordinal);

    public int Calls { get; private set; }

    public bool MustAnswer => Calls >= AnswerAfterCalls;

    public async Task<(string Text, bool IsError)> RunAsync(
        string toolName,
        IReadOnlyDictionary<string, object?> arguments,
        AgilePmToolbox tools,
        Func<string, string, Task<bool>> approveWrite,
        Action<AssistantEvent> onEvent,
        CancellationToken ct)
    {
        var key = toolName + " " + JsonSerializer.Serialize(arguments.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToDictionary());
        if (tools.IsReadOnly(toolName) && !_readsDone.Add(key))
        {
            return ($"You already called {toolName} with these arguments in this turn; its result is above. " +
                    "Don't call it again: answer from that result, or use different filters.", false);
        }

        Calls++;
        var (text, isError) = await AssistantToolRunner.RunAsync(toolName, arguments, tools, approveWrite, onEvent, ct);
        if (isError) _readsDone.Remove(key);
        if (Calls >= WrapUpAfterCalls)
        {
            text += $"\n\n[{Calls} tool calls so far. Answer the user now from these results unless one more call is essential.]";
        }
        return (text, isError);
    }
}

/// <summary>Runs one tool call for any backend: approval for writes, UI events, and the result text.</summary>
internal static class AssistantToolRunner
{
    public static async Task<(string Text, bool IsError)> RunAsync(
        string toolName,
        IReadOnlyDictionary<string, object?> arguments,
        AgilePmToolbox tools,
        Func<string, string, Task<bool>> approveWrite,
        Action<AssistantEvent> onEvent,
        CancellationToken ct)
    {
        if (!tools.HasTool(toolName))
        {
            return ($"There is no tool named '{toolName}'. Use one of the tools you were given.", true);
        }

        var argsJson = JsonSerializer.Serialize(arguments, new JsonSerializerOptions { WriteIndented = true });
        if (!tools.IsReadOnly(toolName) && !await approveWrite(toolName, argsJson))
        {
            onEvent(new AssistantEvent(AssistantEventKind.ToolDeclined, "Declined by the user", toolName));
            return (AssistantPrompts.DeclinedToolResult, true);
        }

        onEvent(new AssistantEvent(AssistantEventKind.ToolStarted, argsJson, toolName));
        var (text, isError) = await tools.CallToolAsync(toolName, arguments, ct);
        onEvent(new AssistantEvent(isError ? AssistantEventKind.ToolFailed : AssistantEventKind.ToolFinished, text, toolName));
        return (text, isError);
    }
}
