using AutoPM_Desktop.Models;
using System.Windows;

namespace AutoPM_Desktop.Dialogs;

public partial class ReassignDialog : Window
{
    public Guid TargetUserId { get; private set; }

    public ReassignDialog(string itemTitle, IReadOnlyList<TeamMemberItem> members)
    {
        InitializeComponent();
        TitleText.Text = $"Reassign: {itemTitle}";
        MemberCombo.ItemsSource = members;
        if (members.Count > 0) MemberCombo.SelectedIndex = 0;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var raw = (MemberCombo.SelectedValue as string) ?? MemberCombo.Text.Trim();
        if (!Guid.TryParse(raw, out var guid))
        {
            MessageBox.Show("Please select a team member or enter a valid User ID GUID.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        TargetUserId = guid;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
