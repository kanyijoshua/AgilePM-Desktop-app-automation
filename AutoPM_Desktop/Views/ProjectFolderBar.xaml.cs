using AutoPM_Desktop.Models;
using AutoPM_Desktop.Services;
using Microsoft.Win32;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace AutoPM_Desktop.Views;

/// <summary>
/// Shows and sets a project's local code folder. The folder is remembered per project and shared: choosing it here
/// updates every other bar, Quick update and the sidebar. AI task generation reads the folder's layout and git history.
/// </summary>
public partial class ProjectFolderBar : UserControl
{
    private AiWorkspace? _ws;
    private ProjectItem? _project;

    public ProjectFolderBar()
    {
        InitializeComponent();
        Show();
    }

    public void Initialize(AiWorkspace workspace)
    {
        _ws = workspace;
        _ws.ProjectFolderChanged += id =>
        {
            if (string.Equals(id, _project?.Id, StringComparison.OrdinalIgnoreCase)) Show();
        };
        Show();
    }

    /// <summary>The project whose folder this bar shows (the active project, or the selected story's project).</summary>
    public ProjectItem? Project
    {
        get => _project;
        set
        {
            _project = value;
            Show();
        }
    }

    private void Show()
    {
        var folder = _project is null ? null : _ws?.GetProjectFolder(_project.Id);
        FolderText.Text = _project is null ? "Select a project first."
            : folder is null ? "None chosen. Pick the project's code folder so the AI can use the code and git history when generating tasks."
            : Directory.Exists(folder) ? folder
            : $"{folder} (not found; choose it again)";
        FolderText.ToolTip = folder;
        ChooseBtn.IsEnabled = _project is not null;
        ClearBtn.Visibility = folder is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Choose_Click(object sender, RoutedEventArgs e)
    {
        if (_ws is not null && _project is not null) Choose(_ws, _project, Window.GetWindow(this));
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (_ws is null || _project is null) return;
        _ws.RememberProjectFolder(_project.Id, null);
        _ws.Notify($"Forgot the folder for {_project.ProjectName.Trim()}.", false);
    }

    /// <summary>Asks for a project's code folder and remembers it; true when one was chosen.</summary>
    public static bool Choose(AiWorkspace ws, ProjectItem project, Window? owner)
    {
        var current = ws.GetProjectFolder(project.Id);
        var dialog = new OpenFolderDialog
        {
            Title = $"Code folder for {project.ProjectName.Trim()}",
            InitialDirectory = current is not null && Directory.Exists(current) ? current : "",
        };
        if (dialog.ShowDialog(owner) != true) return false;

        ws.RememberProjectFolder(project.Id, dialog.FolderName);
        ws.Notify($"Remembered {Path.GetFileName(dialog.FolderName)} as the folder for {project.ProjectName.Trim()}.", false);
        return true;
    }
}
