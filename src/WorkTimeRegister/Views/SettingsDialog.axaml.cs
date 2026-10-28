using Avalonia.Controls;
using Avalonia.Interactivity;
using WorkTimeRegister.Models;

namespace WorkTimeRegister.Views;

public sealed partial class SettingsDialog : Window
{
    public SettingsDialog() => InitializeComponent();

    public SettingsDialog(WorkDaySettings settings) : this()
    {
        AutoStartCheckBox.IsChecked = settings.AutoStartOnLaunch;
        RestoreCheckBox.IsChecked = settings.RestoreOnLaunch;
        AutoContinueCheckBox.IsChecked = settings.AutoContinueAtMidnight;
    }

    private void SaveClicked(object? sender, RoutedEventArgs e) => Close(new WorkDaySettings(
        AutoStartCheckBox.IsChecked == true,
        RestoreCheckBox.IsChecked == true,
        AutoContinueCheckBox.IsChecked == true));

    private void CancelClicked(object? sender, RoutedEventArgs e) => Close((WorkDaySettings?)null);
}
