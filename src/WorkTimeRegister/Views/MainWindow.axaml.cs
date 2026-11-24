using Avalonia.Controls;
using Avalonia.Interactivity;
using System.Reflection;
using WorkTimeRegister.Models;
using WorkTimeRegister.ViewModels;

namespace WorkTimeRegister.Views;

public sealed partial class MainWindow : Window
{
    public event Action? RetryIntegrationsRequested;
    internal void CloseForReplacement()
    {
        Closing -= MainWindowClosing;
        Close();
    }

    private void RetryIntegrations(object? sender, RoutedEventArgs e) => RetryIntegrationsRequested?.Invoke();

    private async void RetryClock(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
            await viewModel.RetryClockAsync();
    }

    private async void RetryPowerEvents(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
            await viewModel.RetryPowerEventsAsync();
    }
    public MainWindow()
    {
        InitializeComponent();
        var assembly = typeof(MainWindow).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion.Split('+')[0] ?? assembly.GetName().Version?.ToString(3) ?? "неизвестна";
        Title = $"Учёт рабочего времени — версия {version}";
        Closing += MainWindowClosing;
    }

    private void MainWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (e.CloseReason is WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown)
            return;

        e.Cancel = true;
        Hide();
    }

    private async void OpenTransferDialog(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
            return;

        var dialog = new TransferDialog(viewModel.TotalWork, viewModel.TotalBreak);
        var result = await dialog.ShowDialog<TransferDialogResult?>(this);
        if (result is not null)
            await viewModel.AdjustTimeAsync(result.Operation, result.Source, result.Amount);
    }

    private async void OpenActionLog(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
            return;

        var logViewModel = viewModel.CreateLogViewModel();
        var window = new ActionLogWindow { DataContext = logViewModel };
        void RefreshImport(object? _, System.ComponentModel.PropertyChangedEventArgs change)
        {
            if (change.PropertyName == nameof(MainWindowViewModel.HasClockConflict))
                logViewModel.RefreshImportAvailability();
        }
        viewModel.PropertyChanged += RefreshImport;
        try { await window.ShowDialog(this); }
        finally { viewModel.PropertyChanged -= RefreshImport; }
    }

    private async void OpenSettings(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
            return;
        var dialog = new SettingsDialog(viewModel.Settings);
        var settings = await dialog.ShowDialog<WorkDaySettings?>(this);
        if (settings is not null)
            await viewModel.SaveSettingsAsync(settings);
    }

    private async void AdjustWorkTime(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel && TryGetOperation(sender, out var operation))
            await ShowCategoryAdjustmentAsync(viewModel, operation, TimeEntryKind.Work, "Работа сегодня");
    }

    private async void AdjustBreakTime(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel && TryGetOperation(sender, out var operation))
            await ShowCategoryAdjustmentAsync(viewModel, operation, TimeEntryKind.Break, "Перерывы сегодня");
    }

    private async void AdjustCurrentTime(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel ||
            !viewModel.ActiveKind.HasValue ||
            !TryGetOperation(sender, out var operation))
            return;

        var kind = viewModel.ActiveKind.Value;
        var dialog = new TransferDialog(
            viewModel.TotalWork,
            viewModel.TotalBreak,
            operation,
            kind,
            viewModel.CurrentPeriodValue,
            $"Текущий период: {ModeName(kind)}");
        var result = await dialog.ShowDialog<TransferDialogResult?>(this);
        if (result is not null)
            await viewModel.AdjustCurrentPeriodAsync(result.Operation, result.Amount);
    }

    private async Task ShowCategoryAdjustmentAsync(
        MainWindowViewModel viewModel,
        TimeAdjustmentOperation operation,
        TimeEntryKind kind,
        string targetLabel)
    {
        var available = kind == TimeEntryKind.Work ? viewModel.TotalWork : viewModel.TotalBreak;
        var dialog = new TransferDialog(
            viewModel.TotalWork,
            viewModel.TotalBreak,
            operation,
            kind,
            available,
            targetLabel);
        var result = await dialog.ShowDialog<TransferDialogResult?>(this);
        if (result is not null)
            await viewModel.AdjustTimeAsync(result.Operation, result.Source, result.Amount);
    }

    private static bool TryGetOperation(object? sender, out TimeAdjustmentOperation operation) =>
        Enum.TryParse((sender as Button)?.Tag?.ToString(), out operation) &&
        operation is TimeAdjustmentOperation.Add or TimeAdjustmentOperation.Remove or TimeAdjustmentOperation.Clear;

    private static string ModeName(TimeEntryKind kind) => kind == TimeEntryKind.Work ? "Работа" : "Перерыв";
}
