using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AutoPM_Desktop.Models;

public enum ChatRole { User, Assistant, Tool, Notice }

/// <summary>One bubble in the AI Assistant conversation. Tool bubbles update in place as the call finishes.</summary>
public sealed class ChatMessage : INotifyPropertyChanged
{
    private string _text = "";
    private string _status = "";

    public ChatRole Role { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public string TimeDisplay => Timestamp.ToString("HH:mm");

    public string Text
    {
        get => _text;
        set { _text = value; OnPropertyChanged(); }
    }

    /// <summary>Tool bubbles only: "running", "done", "failed" or "declined".</summary>
    public string Status
    {
        get => _status;
        set { _status = value; OnPropertyChanged(); }
    }

    public string? ToolName { get; init; }
    public string? Details { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>One finished automation run, shown in the automation history list.</summary>
public sealed record AutomationRunEntry(DateTime When, string Name, string Outcome, bool Succeeded)
{
    public string WhenDisplay => When.ToString("HH:mm:ss");
    public string Icon => Succeeded ? "✔" : "⚠";
}

/// <summary>A parsed line of the batch creator, shown in the preview before anything is created.</summary>
public sealed class BatchLinePreview
{
    public int LineNumber { get; init; }
    public string Title { get; init; } = "";
    public double Hours { get; init; }
    public PriorityEnum Priority { get; init; }
    public string? Description { get; init; }
    public DateTime DueDate { get; init; }
    public string DueDisplay => DueDate.ToString("yyyy-MM-dd");
    public string Check { get; set; } = "Ready";
    public bool IsValid => Check.StartsWith("Ready", StringComparison.Ordinal);
}
