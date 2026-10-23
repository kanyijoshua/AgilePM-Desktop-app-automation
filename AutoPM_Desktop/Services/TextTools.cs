using System.Net;
using System.Text.RegularExpressions;

namespace AutoPM_Desktop.Services;

/// <summary>
/// Turns Agile PM's rich text into short plain text for the AI. Descriptions and acceptance criteria are HTML,
/// sometimes with pasted screenshots inlined as base64 (one task description was 188,000 characters), which would
/// blow any model's token budget.
/// </summary>
public static partial class TextTools
{
    /// <summary>Strips images, tags and entities, collapses whitespace and cuts the text to <paramref name="maxChars"/>.</summary>
    public static string Plain(string? html, int maxChars = 300)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";

        var text = DataUris().Replace(html, " ");
        text = BlockTags().Replace(text, "\n");
        text = Tags().Replace(text, " ");
        text = WebUtility.HtmlDecode(text);
        text = Spaces().Replace(text, " ");
        text = BlankLines().Replace(text, "\n").Trim();
        return Cut(text, maxChars);
    }

    public static string Cut(string? text, int maxChars)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxChars) return text ?? "";
        return text[..Math.Max(0, maxChars - 1)].TrimEnd() + "…";
    }

    /// <summary>Rough token count for budgeting requests (about 3.5 characters per token for English and JSON).</summary>
    public static int EstimateTokens(string text) => (int)Math.Ceiling(text.Length / 3.5);

    [GeneratedRegex(@"data:[a-z0-9.+/-]+;base64,[A-Za-z0-9+/=\s]+", RegexOptions.IgnoreCase)]
    private static partial Regex DataUris();

    [GeneratedRegex(@"<\s*(br|/p|/li|/h[1-6]|/div|/tr)\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockTags();

    [GeneratedRegex(@"<[^>]*>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"[ \t ]+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"\s*\n\s*")]
    private static partial Regex BlankLines();
}
