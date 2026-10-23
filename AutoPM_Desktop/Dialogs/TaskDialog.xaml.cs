using AutoPM_Desktop.Models;
using System.Globalization;
using System.Windows;

namespace AutoPM_Desktop.Dialogs;

public partial class TaskDialog : Window
{
    public string TaskTitle => TitleBox.Text.Trim();
    public string? TaskDescription => string.IsNullOrWhiteSpace(DescriptionBox.Text) ? null : DescriptionBox.Text.Trim();
    public string? SelectedEpicId => EpicCombo.SelectedValue as string;
    public string? SelectedStoryId => StoryCombo.SelectedValue as string;
    public string? SelectedAssigneeId { get; private set; }
    public double PlannedHours { get; private set; }
    public PriorityEnum SelectedPriority => PriorityCombo.SelectedItem is PriorityEnum p ? p : PriorityEnum.Medium;
    public PriorityEnum SelectedComplexity => ComplexityCombo.SelectedItem is PriorityEnum c ? c : PriorityEnum.Medium;
    public GeneralStagesEnum SelectedStatus => StatusCombo.SelectedItem is GeneralStagesEnum s ? s : GeneralStagesEnum.Planned;
    public DateTime? SelectedEndDate => EndDatePicker.SelectedDate;

    public TaskDialog(
        IReadOnlyList<EpicItem> epics,
        IReadOnlyList<UserStoryItem> stories,
        IReadOnlyList<TeamMemberItem> members,
        TaskItem? existing = null,
        string? defaultEpicId = null,
        string? defaultStoryId = null,
        string? currentUserId = null)
    {
        InitializeComponent();

        var epicList = new List<EpicItem> { new() { Id = "", Title = "(None)" } };
        epicList.AddRange(epics);
        EpicCombo.ItemsSource = epicList;

        var storyList = new List<UserStoryItem> { new() { Id = "", Story = "(None)" } };
        storyList.AddRange(stories);
        StoryCombo.ItemsSource = storyList;

        var memberList = new List<TeamMemberItem> { new() { UserId = "", Name = "(Unassigned)" } };
        memberList.AddRange(members);
        AssigneeCombo.ItemsSource = memberList;

        PriorityCombo.ItemsSource = Enum.GetValues<PriorityEnum>();
        ComplexityCombo.ItemsSource = Enum.GetValues<PriorityEnum>();
        StatusCombo.ItemsSource = Enum.GetValues<GeneralStagesEnum>();

        if (existing is not null)
        {
            Title = "Edit Project Task";
            DialogHeader.Text = "Update Project Task";
            SaveBtn.Content = "Update Task";
            TitleBox.Text = existing.Title;
            DescriptionBox.Text = existing.Description;
            EpicCombo.SelectedValue = existing.EpicId ?? "";
            StoryCombo.SelectedValue = existing.UserStoryId ?? "";
            AssigneeCombo.SelectedValue = existing.AssignTo ?? "";
            AssigneeCombo.IsEnabled = false; // Reassign has its own dedicated endpoint in Agile PM
            PlannedHoursBox.Text = existing.PlannedHours.ToString("0.##", CultureInfo.InvariantCulture);
            PriorityCombo.SelectedItem = existing.Priority;
            ComplexityCombo.SelectedItem = existing.Complexity;
            StatusCombo.SelectedItem = existing.Status;
            StatusPanel.Visibility = Visibility.Collapsed;
            EndDatePicker.SelectedDate = existing.EndDateRaw;
        }
        else
        {
            EpicCombo.SelectedValue = defaultEpicId ?? "";
            StoryCombo.SelectedValue = defaultStoryId ?? "";
            AssigneeCombo.SelectedValue = currentUserId ?? "";
            PriorityCombo.SelectedItem = PriorityEnum.Medium;
            ComplexityCombo.SelectedItem = PriorityEnum.Medium;
            StatusCombo.SelectedItem = GeneralStagesEnum.Planned;
            EndDatePicker.SelectedDate = DateTime.Today.AddDays(7);
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TitleBox.Text))
        {
            MessageBox.Show("Task title is required.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!double.TryParse(PlannedHoursBox.Text.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var hrs) || hrs < 0)
        {
            MessageBox.Show("Please enter a valid number for Planned Hours.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        PlannedHours = hrs;

        var assignVal = AssigneeCombo.SelectedValue as string;
        if (string.IsNullOrWhiteSpace(assignVal) && Guid.TryParse(AssigneeCombo.Text.Trim(), out var manualGuid))
        {
            assignVal = manualGuid.ToString();
        }
        SelectedAssigneeId = string.IsNullOrWhiteSpace(assignVal) ? null : assignVal;

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
