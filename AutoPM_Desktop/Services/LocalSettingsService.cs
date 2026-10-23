using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutoPM_Desktop.Services;

public sealed class AppUserSettings
{
    public string ServerUrl { get; set; } = LocalSettingsService.DefaultAgilePmUrl;
    public string Email { get; set; } = "";
    public string? SavedAccessToken { get; set; }
    public string? SavedRefreshToken { get; set; }
    public bool RememberSession { get; set; } = true;

    /// <summary>Claude API key, encrypted with DPAPI for the current Windows user. Use <see cref="AnthropicApiKey"/>.</summary>
    public string? ProtectedAnthropicApiKey { get; set; }

    /// <summary>When false, the AI assistant asks before every tool call that changes Agile PM data.</summary>
    public bool AiAutoApproveWrites { get; set; }

    /// <summary>Local code folder chosen for each project (project ID → path), read by Quick update.</summary>
    public Dictionary<string, string> ProjectFolders { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Background sync interval in minutes; 0 turns it off.</summary>
    public int AutoRefreshMinutes { get; set; }

    /// <summary>Which model backend the AI features use.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AiProviderKind AiProvider { get; set; } = AiProviderKind.Claude;

    /// <summary>OpenAI-compatible endpoint of the local/free model (Ollama by default).</summary>
    public string LocalAiBaseUrl { get; set; } = LocalModelAssistantService.DefaultBaseUrl;

    public string LocalAiModel { get; set; } = LocalModelAssistantService.DefaultModel;

    /// <summary>Send every Agile PM tool to the open model instead of the core set (needs a large context window).</summary>
    public bool LocalAiAllTools { get; set; }

    /// <summary>Optional key for OpenAI-compatible servers that require one; encrypted like the Claude key.</summary>
    /// <remarks>Superseded by <see cref="ProtectedOpenModelKeys"/>; still read as a fallback.</remarks>
    public string? ProtectedLocalAiApiKey { get; set; }

    /// <summary>Which <see cref="OpenModelPresets"/> entry the open-model settings came from.</summary>
    public string? OpenModelPresetId { get; set; }

    /// <summary>Encrypted API keys for open-model services, by host (e.g. "api.groq.com"), so switching services keeps each key.</summary>
    public Dictionary<string, string> ProtectedOpenModelKeys { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string? GetOpenModelKey(string? baseUrl) =>
        ProtectedOpenModelKeys.TryGetValue(KeyHost(baseUrl), out var protectedKey)
            ? LocalSettingsService.Unprotect(protectedKey)
            : LocalAiApiKey;

    public void SetOpenModelKey(string? baseUrl, string? key)
    {
        var host = KeyHost(baseUrl);
        if (LocalSettingsService.Protect(key) is { } protectedKey) ProtectedOpenModelKeys[host] = protectedKey;
        else ProtectedOpenModelKeys.Remove(host);
    }

    private static string KeyHost(string? baseUrl) =>
        Uri.TryCreate(LocalModelAssistantService.NormalizeBaseUrl(baseUrl), UriKind.Absolute, out var uri) ? uri.Authority.ToLowerInvariant() : "";

    [JsonIgnore]
    public string? AnthropicApiKey
    {
        get => LocalSettingsService.Unprotect(ProtectedAnthropicApiKey);
        set => ProtectedAnthropicApiKey = LocalSettingsService.Protect(value);
    }

    [JsonIgnore]
    public string? LocalAiApiKey
    {
        get => LocalSettingsService.Unprotect(ProtectedLocalAiApiKey);
        set => ProtectedLocalAiApiKey = LocalSettingsService.Protect(value);
    }
}

public static class LocalSettingsService
{
    /// <summary>Placeholder until the user enters their own Agile PM address at sign-in.</summary>
    public const string DefaultAgilePmUrl = "https://agilepm.example.com/";

    private static readonly string SettingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AutoPM_Desktop");

    private static readonly string SettingsFile = Path.Combine(SettingsDir, "settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static AppUserSettings Load()
    {
        try
        {
            if (File.Exists(SettingsFile))
            {
                var json = File.ReadAllText(SettingsFile);
                var loaded = JsonSerializer.Deserialize<AppUserSettings>(json, JsonOpts);
                if (loaded is not null)
                {
                    if (string.IsNullOrWhiteSpace(loaded.ServerUrl))
                    {
                        loaded.ServerUrl = DefaultAgilePmUrl;
                    }
                    return loaded;
                }
            }
        }
        catch
        {
            // Ignore corrupt settings file and return defaults.
        }

        return new AppUserSettings();
    }

    public static void Save(AppUserSettings settings)
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            var json = JsonSerializer.Serialize(settings, JsonOpts);
            File.WriteAllText(SettingsFile, json);
        }
        catch
        {
            // Ignore write errors
        }
    }

    /// <summary>
    /// Normalizes a user-supplied URL (e.g. "https://agilepm.example.com/" or "https://agilepm.example.com/api")
    /// so it always ends with "/api/".
    /// </summary>
    public static string NormalizeApiBaseUrl(string? rawUrl)
    {
        var url = string.IsNullOrWhiteSpace(rawUrl) ? DefaultAgilePmUrl : rawUrl.Trim();
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            url = "https://" + url;
        }

        url = url.TrimEnd('/');
        if (!url.EndsWith("/api", StringComparison.OrdinalIgnoreCase))
        {
            url += "/api";
        }

        return url + "/";
    }

    internal static string? Protect(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(value.Trim()), null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    internal static string? Unprotect(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            // Written by another Windows user or corrupted: treat as not set.
            return null;
        }
    }
}
