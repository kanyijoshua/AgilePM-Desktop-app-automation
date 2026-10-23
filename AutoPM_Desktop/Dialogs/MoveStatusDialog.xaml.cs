using AutoPM_Desktop.Models;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace AutoPM_Desktop.Dialogs;

public partial class MoveStatusDialog : Window
{
    private readonly GeneralStagesEnum _currentStage;

    public GeneralStagesEnum SelectedStage { get; private set; }
    public double LoggedHours { get; private set; }

    public MoveStatusDialog(string itemTitle, GeneralStagesEnum currentStage, GeneralStagesEnum? suggestedStage = null, double defaultHours = 0)
    {
        InitializeComponent();
        _currentStage = currentStage;
        HeaderText.Text = $"Move: {itemTitle}";
        StageCombo.ItemsSource = Enum.GetValues<GeneralStagesEnum>();
        StageCombo.SelectedItem = suggestedStage ?? (currentStage == GeneralStagesEnum.Planned ? GeneralStagesEnum.InProgress : GeneralStagesEnum.Completed);
        HoursBox.Text = defaultHours.ToString("0.##", CultureInfo.InvariantCulture);
        UpdateNotice();
    }

    private void StageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateNotice();
    }

    private void UpdateNotice()
    {
        if (AutoStepNotice is null) return;
        var target = StageCombo.SelectedItem is GeneralStagesEnum g ? g : _currentStage;
        AutoStepNotice.Visibility = (_currentStage == GeneralStagesEnum.Planned && target == GeneralStagesEnum.Completed)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (StageCombo.SelectedItem is not GeneralStagesEnum target)
        {
            MessageBox.Show("Please select a target stage.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!double.TryParse(HoursBox.Text.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var hrs) || hrs < 0)
        {
            MessageBox.Show("Please enter a valid non-negative number for hours worked.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SelectedStage = target;
        LoggedHours = hrs;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
