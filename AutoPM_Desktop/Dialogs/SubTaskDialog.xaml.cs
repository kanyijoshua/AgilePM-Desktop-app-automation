using AutoPM_Desktop.Models;
using System.Globalization;
using System.Windows;

namespace AutoPM_Desktop.Dialogs;

public partial class SubTaskDialog : Window
{
    public string SubTaskTitle => TitleBox.Text.Trim();
    public string? SubTaskDescription => string.IsNullOrWhiteSpace(DescriptionBox.Text) ? null : DescriptionBox.Text.Trim();
    public string? SelectedAssigneeId => string.IsNullOrWhiteSpace(AssigneeCombo.SelectedValue as string) ? null : AssigneeCombo.SelectedValue as string;
    public double PlannedHours { get; private set; }
    public PriorityEnum SelectedPriority => PriorityCombo.SelectedItem is PriorityEnum p ? p : PriorityEnum.Medium;
    public DateTime? SelectedEndDate => EndDatePicker.SelectedDate;
    public bool IsBlocker => BlockerCheck.IsChecked == true;

    public SubTaskDialog(IReadOnlyList<TeamMemberItem> members, string? currentUserId = null)
    {
        InitializeComponent();

        var list = new List<TeamMemberItem> { new() { UserId = "", Name = "(Unassigned)" } };
        list.AddRange(members);
        AssigneeCombo.ItemsSource = list;
        AssigneeCombo.SelectedValue = currentUserId ?? "";

        PriorityCombo.ItemsSource = Enum.GetValues<PriorityEnum>();
        PriorityCombo.SelectedItem = PriorityEnum.Medium;
        EndDatePicker.SelectedDate = DateTime.Today.AddDays(3);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TitleBox.Text))
        {
            MessageBox.Show("Sub-task title is required.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!double.TryParse(PlannedHoursBox.Text.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var hrs) || hrs < 0)
        {
            MessageBox.Show("Please enter a valid number for Planned Hours.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        PlannedHours = hrs;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
