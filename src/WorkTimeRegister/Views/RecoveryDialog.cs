using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using WorkTimeRegister.Models;
using WorkTimeRegister.Services;
using WorkTimeRegister.ViewModels;

namespace WorkTimeRegister.Views;

public sealed class RecoveryDialog : Window
{
    private bool confirmed;

    public RecoveryDialog(MainWindowViewModel viewModel, Action? exit = null)
    {
        var recovery = viewModel.PendingRecovery ?? throw new InvalidOperationException("Нет интервала для восстановления.");
        Title = RecoveryPresentation.Title(recovery.Origin);
        Width = 660;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var error = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var duration = new TextBox { Text = "00:00:00", Watermark = "ЧЧ:ММ:СС", IsEnabled = !recovery.WasPaused };
        var exclude = new Button { Content = $"Не учитывать {RecoveryPresentation.Duration(recovery.Duration)}", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
        var limitedByDay = recovery.MaximumCountableDuration < recovery.Duration;
        var include = new Button { Content = limitedByDay ? "Учесть до конца рабочего дня" : "Учесть весь интервал", IsEnabled = !recovery.WasPaused, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
        var custom = new Button { Content = "Учесть указанное время", IsEnabled = !recovery.WasPaused, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
        var exitButton = new Button { Content = "Выйти без восстановления", IsVisible = exit is not null };
        exitButton.Click += (_, _) => exit?.Invoke();
        async Task Resolve(RecoveryChoice choice)
        {
            var amount = TimeSpan.Zero;
            if (choice == RecoveryChoice.Custom &&
                (!TimeSpan.TryParse(duration.Text, CultureInfo.InvariantCulture, out amount) || amount < TimeSpan.Zero || amount > recovery.MaximumCountableDuration))
            {
                error.Text = $"Укажите время от нуля до {RecoveryPresentation.Duration(recovery.MaximumCountableDuration)}. Для нескольких суток используйте Д.ЧЧ:ММ:СС.";
                return;
            }
            exclude.IsEnabled = include.IsEnabled = custom.IsEnabled = false;
            if (await viewModel.ResolveRecoveryAsync(choice, amount))
            {
                confirmed = true;
                Close();
            }
            else
            {
                error.Text = viewModel.ErrorMessage;
                exclude.IsEnabled = true;
                include.IsEnabled = custom.IsEnabled = !recovery.WasPaused;
            }
        }
        exclude.Click += async (_, _) => await Resolve(RecoveryChoice.Exclude);
        include.Click += async (_, _) => await Resolve(RecoveryChoice.Include);
        custom.Click += async (_, _) => await Resolve(RecoveryChoice.Custom);
        Content = new StackPanel
        {
            Margin = new Thickness(24), Spacing = 14,
            Children =
            {
                new TextBlock { Text = Title, FontSize = 24, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new TextBlock { Text = RecoveryPresentation.Explanation(recovery.Origin), TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new TextBlock { Text = $"Последняя сохранённая отметка: {RecoveryPresentation.Moment(recovery.ConfirmedAt, viewModel.LocalTimeZone)}\nИнтервал обнаружен: {RecoveryPresentation.Moment(recovery.DetectedAt, viewModel.LocalTimeZone)}\nНеизвестный интервал: {RecoveryPresentation.Duration(recovery.Duration)}", TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new TextBlock { Text = recovery.WasPaused ? "Учёт уже был на паузе. Она сохранится; неизвестный интервал не учитывается." : $"Прежний режим: {(recovery.Mode == TimeEntryKind.Work ? "Работа" : "Перерыв")}. До выбора неизвестное время не добавляется к отображаемым итогам.", TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new TextBlock { Text = $"Продолжение через полночь выключено. Можно учесть не более {RecoveryPresentation.Duration(recovery.MaximumCountableDuration)} — до конца прежнего дня.", IsVisible = limitedByDay, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                exclude, include,
                new TextBlock { Text = "Или укажите длительность с начала неизвестного интервала (ЧЧ:ММ:СС или Д.ЧЧ:ММ:СС):", TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                duration, custom,
                new TextBlock { Text = "После восстановления открытый день остаётся на паузе. Для продолжения нажмите «Продолжить». Сохранённые настройки перехода через полночь действуют и при восстановлении.", TextWrapping = Avalonia.Media.TextWrapping.Wrap, Opacity = 0.7 },
                error, exitButton
            }
        };
        Closing += (_, e) =>
        {
            if (!confirmed && e.CloseReason is not (WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown))
                e.Cancel = true;
        };
        void RecoveryChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainWindowViewModel.PendingRecovery) && viewModel.PendingRecovery != recovery)
            {
                confirmed = true;
                Close();
            }
        }
        viewModel.PropertyChanged += RecoveryChanged;
        Closed += (_, _) => viewModel.PropertyChanged -= RecoveryChanged;
    }
}
