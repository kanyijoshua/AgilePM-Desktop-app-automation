using Anthropic;
using Anthropic.Models.Beta.Messages;
using System.Text.Json;

namespace AutoPM_Desktop.Services;

/// <summary>
/// Claude-powered assistant. Chat turns run an agentic loop over the Agile PM tools in
/// <see cref="AgilePmToolbox"/>; quick actions are single calls without tools.
/// </summary>
public sealed class AiAssistantService : IAiAssistant
{
    public const string Model = "claude-opus-5-5";

    // If Claude declines a request for policy reasons, the API retries it on a fallback model in the same call.
    // "default" lets the API pick the fallback model by refusal category.
    private const string FallbackBeta = "server-side-fallback-2026-07-01";

    private readonly List<BetaMessageParam> _history = [];
    private AnthropicClient? _client;
    private string? _clientKey;

    public string DisplayName => $"Claude · {Model}";

    public bool IsConfigured => _client is not null;

    public string NotConfiguredHint =>
        "Add your Claude API key in the AI Assistant panel (or set ANTHROPIC_API_KEY), or switch to a free open model.";

    /// <summary>Uses the given key, or ANTHROPIC_API_KEY when it is empty.</summary>
    public void Configure(string? apiKey)
    {
        var key = ResolveApiKey(apiKey);
        if (key is null)
        {
            _client = null;
            _clientKey = null;
            return;
        }

        if (key != _clientKey)
        {
            _client = new AnthropicClient { ApiKey = key };
            _clientKey = key;
        }
    }

    public void ResetConversation() => _history.Clear();

    public async Task<string> SendAsync(
        string userText,
        string contextNote,
        AgilePmToolbox toolbox,
        Func<string, string, Task<bool>> approveWrite,
        Action<AssistantEvent> onEvent,
        CancellationToken ct)
    {
        var client = RequireClient();
        var tools = toolbox.Tools.Select(ToBetaTool).ToList();

        // The context note travels with the user's message, not in the system prompt, so the cached prefix
        // (tools + system) stays identical between turns.
        _history.Add(new BetaUserMessageParam
        {
            Content = new List<BetaContentBlockParam>
            {
                new BetaTextBlockParam { Text = AssistantPrompts.UserTurn(contextNote, userText) },
            },
        });

        var finalText = new List<string>();
        var turn = new ToolTurn();
        for (var round = 0; round < AssistantPrompts.MaxToolRounds; round++)
        {
            var response = await client.Beta.Messages.Create(new MessageCreateParams
            {
                Model = Model,
                MaxTokens = 16000,
                System = AssistantPrompts.System,
                Tools = tools,
                // Out of tool calls: the model answers from what it has gathered.
                ToolChoice = turn.MustAnswer ? new BetaToolChoiceNone() : null,
                Messages = _history.ToList(),
                OutputConfig = new BetaOutputConfig { Effort = Effort.Medium },
                CacheControl = new BetaCacheControlEphemeral(),
                Betas = [FallbackBeta],
                Fallbacks = new Default(),
            }, ct);

            var assistantContent = new List<BetaContentBlockParam>();
            var toolCalls = new List<BetaToolUseBlock>();
            foreach (var block in response.Content)
            {
                if (block.TryPickText(out var text))
                {
                    assistantContent.Add(new BetaTextBlockParam { Text = text.Text });
                    if (!string.IsNullOrWhiteSpace(text.Text))
                    {
                        finalText.Add(text.Text);
                        onEvent(new AssistantEvent(AssistantEventKind.Text, text.Text));
                    }
                }
                else if (block.TryPickThinking(out var thinking))
                {
                    // Thinking blocks go back unchanged (signature included) to keep the conversation valid.
                    assistantContent.Add(new BetaThinkingBlockParam { Thinking = thinking.Thinking, Signature = thinking.Signature });
                }
                else if (block.TryPickRedactedThinking(out var redacted))
                {
                    assistantContent.Add(new BetaRedactedThinkingBlockParam { Data = redacted.Data });
                }
                else if (block.TryPickToolUse(out var toolUse))
                {
                    assistantContent.Add(new BetaToolUseBlockParam { ID = toolUse.ID, Name = toolUse.Name, Input = toolUse.Input });
                    toolCalls.Add(toolUse);
                }
            }

            _history.Add(new BetaAssistantMessageParam { Content = assistantContent });

            var stopReason = response.StopReason?.ToString();
            if (stopReason == "refusal")
            {
                const string refusal = "Claude declined this request. Try rephrasing it.";
                onEvent(new AssistantEvent(AssistantEventKind.Notice, refusal));
                return refusal;
            }

            if (toolCalls.Count == 0)
            {
                if (stopReason == "max_tokens")
                {
                    onEvent(new AssistantEvent(AssistantEventKind.Notice, "The answer was cut off because it reached the length limit."));
                }
                return string.Join("\n\n", finalText);
            }

            // Every tool_use needs a tool_result, all in one user message.
            var results = new List<BetaContentBlockParam>();
            foreach (var call in toolCalls)
            {
                var arguments = call.Input.ToDictionary(kv => kv.Key, kv => (object?)kv.Value);
                var (resultText, isError) = await turn.RunAsync(call.Name, arguments, toolbox, approveWrite, onEvent, ct);
                results.Add(new BetaToolResultBlockParam { ToolUseID = call.ID, Content = resultText, IsError = isError });
            }
            _history.Add(new BetaUserMessageParam { Content = results });
        }

        const string limit = "Stopped after many tool calls without finishing. Ask a narrower question or continue from here.";
        onEvent(new AssistantEvent(AssistantEventKind.Notice, limit));
        return limit;
    }

    public async Task<IReadOnlyList<DraftTask>> DraftTasksForStoryAsync(
        string story, string? description, string? acceptanceCriteria, IEnumerable<string> existingTaskTitles, string? codeContext, CancellationToken ct)
    {
        var client = RequireClient();
        var response = await client.Beta.Messages.Create(new MessageCreateParams
        {
            Model = Model,
            MaxTokens = 8000,
            Messages = [new BetaUserMessageParam { Content = AssistantPrompts.DraftTasks(story, description, acceptanceCriteria, existingTaskTitles, codeContext) }],
            OutputConfig = new BetaOutputConfig
            {
                Effort = Effort.Low,
                Format = new BetaJsonOutputFormat { Schema = AssistantPrompts.DraftTasksSchema() },
            },
            Betas = [FallbackBeta],
            Fallbacks = new Default(),
        }, ct);

        return AssistantPrompts.ParseDraftTasks(ResponseText(response));
    }

    public async Task<string> PolishStandupAsync(string rawReport, CancellationToken ct)
    {
        var client = RequireClient();
        var response = await client.Beta.Messages.Create(new MessageCreateParams
        {
            Model = Model,
            MaxTokens = 4000,
            Messages = [new BetaUserMessageParam { Content = AssistantPrompts.PolishStandup(rawReport) }],
            OutputConfig = new BetaOutputConfig { Effort = Effort.Low },
            Betas = [FallbackBeta],
            Fallbacks = new Default(),
        }, ct);

        return ResponseText(response);
    }

    public int RequestBudgetTokens => 150_000;

    public async Task<string> CompleteAsync(string prompt, Dictionary<string, JsonElement>? jsonSchema, int maxAnswerTokens, CancellationToken ct)
    {
        var client = RequireClient();
        var response = await client.Beta.Messages.Create(new MessageCreateParams
        {
            Model = Model,
            MaxTokens = Math.Max(4000, maxAnswerTokens * 2),
            Messages = [new BetaUserMessageParam { Content = prompt }],
            OutputConfig = jsonSchema is null
                ? new BetaOutputConfig { Effort = Effort.Medium }
                : new BetaOutputConfig { Effort = Effort.Medium, Format = new BetaJsonOutputFormat { Schema = jsonSchema } },
            Betas = [FallbackBeta],
            Fallbacks = new Default(),
        }, ct);

        return ResponseText(response);
    }

    /// <summary>Maps a tool's JSON schema to a Claude tool definition.</summary>
    private static BetaToolUnion ToBetaTool(AssistantTool tool)
    {
        var schema = tool.InputSchema;
        var properties = new Dictionary<string, JsonElement>();
        if (schema.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in props.EnumerateObject()) properties[p.Name] = p.Value.Clone();
        }

        var required = schema.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.Array
            ? req.EnumerateArray().Select(r => r.GetString()).OfType<string>().ToList()
            : [];

        return new BetaTool
        {
            Name = tool.Name,
            Description = tool.Description,
            InputSchema = new InputSchema { Properties = properties, Required = required },
        };
    }

    private static string ResponseText(BetaMessage response)
    {
        if (response.StopReason?.ToString() == "refusal")
        {
            throw new InvalidOperationException("Claude declined this request.");
        }

        return string.Concat(response.Content.Select(b => b.TryPickText(out var t) ? t.Text : ""));
    }

    private AnthropicClient RequireClient() => _client ?? throw new InvalidOperationException(NotConfiguredHint);

    private static string? ResolveApiKey(string? apiKey)
    {
        var key = string.IsNullOrWhiteSpace(apiKey) ? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") : apiKey;
        return string.IsNullOrWhiteSpace(key) ? null : key.Trim();
    }
}
