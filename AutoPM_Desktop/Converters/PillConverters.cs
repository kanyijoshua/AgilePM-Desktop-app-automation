using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace AutoPM_Desktop.Converters;

/// <summary>
/// Colours for the stage / priority pills in the grids. Bind the value and pass "bg" or "fg" as the parameter.
/// Matches loosely on the text so it works for task stages, story stages ("In_Progress") and epic labels ("1 - In Progress").
/// </summary>
public sealed class PillBrushConverter : IValueConverter
{
    private static readonly Dictionary<string, (string Bg, string Fg)> Palette = new()
    {
        ["planned"] = ("#FEF3C7", "#92400E"),
        ["inprogress"] = ("#DBEAFE", "#1E40AF"),
        ["returned"] = ("#FEE2E2", "#B91C1C"),
        ["completed"] = ("#D1FAE5", "#065F46"),
        ["closed"] = ("#E2E8F0", "#334155"),
        ["cancelled"] = ("#F1F5F9", "#64748B"),
        ["high"] = ("#FEE2E2", "#B91C1C"),
        ["critical"] = ("#FCE7F3", "#9D174D"),
        ["medium"] = ("#FEF3C7", "#92400E"),
        ["low"] = ("#E0F2FE", "#075985"),
        ["true"] = ("#FEE2E2", "#B91C1C"),
        ["false"] = ("#F1F5F9", "#64748B"),
    };

    private static readonly Dictionary<string, SolidColorBrush> Cache = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = Normalize(value?.ToString());
        var (bg, fg) = Palette.FirstOrDefault(p => key.Contains(p.Key)).Value;
        var color = (parameter as string) == "fg" ? fg ?? "#334155" : bg ?? "#F1F5F9";
        return GetBrush(color);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => DependencyProperty.UnsetValue;

    private static string Normalize(string? text) =>
        (text ?? "").Replace(" ", "").Replace("_", "").Replace("-", "").ToLowerInvariant();

    private static SolidColorBrush GetBrush(string hex)
    {
        if (!Cache.TryGetValue(hex, out var brush))
        {
            brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            Cache[hex] = brush;
        }
        return brush;
    }
}

/// <summary>Strips the numeric prefix from epic labels ("1 - In Progress" -> "In Progress") and humanises "In_Progress".</summary>
public sealed class PillTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var text = value?.ToString() ?? "";
        var dash = text.IndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0 && int.TryParse(text[..dash], out _)) text = text[(dash + 3)..];
        return text switch
        {
            "InProgress" or "In_Progress" => "In Progress",
            "True" => "Blocker",
            "False" => "—",
            _ => text,
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => DependencyProperty.UnsetValue;
}
