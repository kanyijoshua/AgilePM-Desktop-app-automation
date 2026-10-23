using AutoPM_Desktop.Models;
using System.Globalization;
using System.Windows;

namespace AutoPM_Desktop.Dialogs;

public partial class EpicDialog : Window
{
    public string EpicTitle => TitleBox.Text.Trim();
    public string? EpicDescription => string.IsNullOrWhiteSpace(DescriptionBox.Text) ? null : DescriptionBox.Text.Trim();
    public double EstimatedEffort { get; private set; }
    public int EpicStatus => Math.Clamp(StatusCombo.SelectedIndex, 0, 3);
    public int EpicPriority => Math.Clamp(PriorityCombo.SelectedIndex, 0, 3);
    public DateTime? StartDate => StartDatePicker.SelectedDate;
    public DateTime? EndDate => EndDatePicker.SelectedDate;

    public EpicDialog(EpicItem? existing = null)
    {
        InitializeComponent();
        if (existing is not null)
        {
            Title = "Update Epic";
            HeaderTitle.Text = "Update Epic";
            TitleBox.Text = existing.Title;
            DescriptionBox.Text = existing.Description;
            EffortBox.Text = existing.EstimatedEffort.ToString("0.##", CultureInfo.InvariantCulture);
            StatusCombo.SelectedIndex = Math.Clamp(existing.Status, 0, 3);
            PriorityCombo.SelectedIndex = Math.Clamp(existing.Priority, 0, 3);
            if (DateTime.TryParse(existing.StartDate, out var s)) StartDatePicker.SelectedDate = s;
            if (DateTime.TryParse(existing.EndDate, out var e)) EndDatePicker.SelectedDate = e;
        }
        else
        {
            StartDatePicker.SelectedDate = DateTime.Today;
            EndDatePicker.SelectedDate = DateTime.Today.AddDays(14);
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TitleBox.Text))
        {
            MessageBox.Show("Epic title is required.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!double.TryParse(EffortBox.Text.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var effort) || effort < 0)
        {
            MessageBox.Show("Please enter a valid number for Estimated Effort.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        EstimatedEffort = effort;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
