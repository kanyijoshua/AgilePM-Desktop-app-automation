namespace AutoPM_Desktop.Services;

/// <summary>
/// A ready-made OpenAI-compatible endpoint for open models. Suggested model names are starting points: providers
/// rename and retire models, so the panel's ⟳ button lists what the service offers today.
/// </summary>
public sealed record OpenModelPreset(
    string Id,
    string Name,
    string BaseUrl,
    string SuggestedModel,
    bool IsLocal,
    bool NeedsKey,
    string? KeyUrl,
    string Notes,
    int MaxRequestTokens = OpenModelPreset.DefaultRequestTokens)
{
    /// <summary>Request budget for services without a known limit (prompt + answer, in tokens).</summary>
    public const int DefaultRequestTokens = 12_000;

    public string DisplayName =>
        Id == OpenModelPresets.CustomId ? $"{Name} (any OpenAI-compatible URL)"
        : IsLocal ? $"{Name} (local, no key)"
        : $"{Name} (hosted, free key)";

    /// <summary>A hosted service lives at one fixed address; local servers and custom endpoints use the URL the user typed.</summary>
    public bool HasFixedUrl => !IsLocal && Id != OpenModelPresets.CustomId;

    /// <summary>
    /// The address requests go to. For hosted services this is always the preset's own URL, so a stale or empty URL box
    /// (for example the Ollama default left over from before the service was picked) can't send requests to localhost.
    /// </summary>
    public string ResolveBaseUrl(string? typedUrl) =>
        HasFixedUrl ? BaseUrl
        : string.IsNullOrWhiteSpace(typedUrl) && BaseUrl.Length > 0 ? BaseUrl
        : LocalModelAssistantService.NormalizeBaseUrl(typedUrl);
}

public static class OpenModelPresets
{
    public const string CustomId = "custom";

    public static readonly IReadOnlyList<OpenModelPreset> All =
    [
        new("groq", "Groq", "https://api.groq.com/openai/v1/", "llama-3.3-70b-versatile", false, true,
            "https://console.groq.com/keys",
            "Free tier with rate limits (8,000 tokens per minute on many models); very fast Llama, Qwen and GPT-OSS models with tool calling.", 6000),
        new("openrouter", "OpenRouter", "https://openrouter.ai/api/v1/", "meta-llama/llama-3.3-70b-instruct:free", false, true,
            "https://openrouter.ai/keys",
            "Many open models; the ones ending in ':free' cost nothing (daily limits apply). ⟳ lists only models that support tools."),
        new("huggingface", "Hugging Face", "https://router.huggingface.co/v1/", "Qwen/Qwen2.5-72B-Instruct", false, true,
            "https://huggingface.co/settings/tokens",
            "Inference Providers with free monthly credits. Use a token with 'Make calls to Inference Providers' permission."),
        new("github", "GitHub Models", "https://models.github.ai/inference/", "meta/Llama-3.3-70B-Instruct", false, true,
            "https://github.com/settings/personal-access-tokens",
            "Free for GitHub users with rate limits. Use a personal access token with the 'models: read' permission. Type the model name (no list).", 7000),
        new("cerebras", "Cerebras", "https://api.cerebras.ai/v1/", "llama-3.3-70b", false, true,
            "https://cloud.cerebras.ai",
            "Free tier with daily token limits; very fast Llama and Qwen models.", 7000),
        new("mistral", "Mistral", "https://api.mistral.ai/v1/", "mistral-small-latest", false, true,
            "https://console.mistral.ai/api-keys",
            "Free 'Experiment' plan with rate limits; Mistral's open-weight models support tool calling.", 24000),
        new("ollama", "Ollama", "http://localhost:11434/v1/", "llama3.1:8b", true, false, "https://ollama.com/download",
            "Runs on this computer: free and offline, but needs RAM/GPU. Run 'ollama pull llama3.1:8b' first."),
        new("lmstudio", "LM Studio", "http://localhost:1234/v1/", "", true, false, "https://lmstudio.ai",
            "Runs on this computer. Load a model and start the local server in LM Studio, then press ⟳."),
        new(CustomId, "Custom server", "", "", false, false, null,
            "Any OpenAI-compatible endpoint (vLLM, llama.cpp, Together, DeepInfra...). Add a key if it needs one."),
    ];

    public static OpenModelPreset Get(string? id) =>
        All.FirstOrDefault(p => p.Id == id) ?? All.First(p => p.Id == CustomId);

    /// <summary>The preset whose URL matches, for settings saved before presets existed.</summary>
    public static OpenModelPreset FromUrl(string? baseUrl)
    {
        var normalized = LocalModelAssistantService.NormalizeBaseUrl(baseUrl);
        return All.FirstOrDefault(p => p.BaseUrl.Length > 0 && string.Equals(p.BaseUrl, normalized, StringComparison.OrdinalIgnoreCase))
            ?? Get(CustomId);
    }
}
