using AutoPM_Desktop.Models;
using System.Windows;

namespace AutoPM_Desktop.Dialogs;

public partial class UserStoryDialog : Window
{
    public string SelectedEpicId => (EpicCombo.SelectedValue as string) ?? "";
    public string StoryText => StoryBox.Text.Trim();
    public string? StoryDescription => string.IsNullOrWhiteSpace(DescriptionBox.Text) ? null : DescriptionBox.Text.Trim();
    public string? AcceptanceCriteria => string.IsNullOrWhiteSpace(AcceptanceBox.Text) ? null : AcceptanceBox.Text.Trim();
    public PriorityEnum SelectedPriority => PriorityCombo.SelectedItem is PriorityEnum p ? p : PriorityEnum.Medium;
    public UserStoryStagesEnum SelectedStatus => StatusCombo.SelectedItem is UserStoryStagesEnum s ? s : UserStoryStagesEnum.Planned;
    public List<string> SelectedAssigneeIds => AssigneesList.SelectedItems
        .OfType<TeamMemberItem>()
        .Select(m => m.UserId)
        .Where(id => !string.IsNullOrWhiteSpace(id))
        .ToList();

    public UserStoryDialog(
        IReadOnlyList<EpicItem> epics,
        IReadOnlyList<TeamMemberItem> members,
        UserStoryItem? existing = null,
        string? defaultEpicId = null)
    {
        InitializeComponent();

        EpicCombo.ItemsSource = epics;
        PriorityCombo.ItemsSource = Enum.GetValues<PriorityEnum>();
        StatusCombo.ItemsSource = Enum.GetValues<UserStoryStagesEnum>();
        AssigneesList.ItemsSource = members;

        if (existing is not null)
        {
            Title = "Update User Story";
            HeaderTitle.Text = "Update User Story";
            EpicCombo.SelectedValue = existing.EpicId;
            StoryBox.Text = existing.Story;
            DescriptionBox.Text = existing.Description;
            AcceptanceBox.Text = existing.AcceptanceCriteria;
            PriorityCombo.SelectedItem = existing.Priority;
            StatusCombo.SelectedItem = existing.Status;
            StatusPanel.Visibility = Visibility.Collapsed;

            foreach (var member in members)
            {
                if (existing.AssignedUserIds.Contains(member.UserId, StringComparer.OrdinalIgnoreCase))
                {
                    AssigneesList.SelectedItems.Add(member);
                }
            }
        }
        else
        {
            EpicCombo.SelectedValue = defaultEpicId ?? epics.FirstOrDefault()?.Id;
            PriorityCombo.SelectedItem = PriorityEnum.Medium;
            StatusCombo.SelectedItem = UserStoryStagesEnum.Planned;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SelectedEpicId))
        {
            MessageBox.Show("Please select an Epic for this User Story.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(StoryBox.Text))
        {
            MessageBox.Show("Story text is required.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
