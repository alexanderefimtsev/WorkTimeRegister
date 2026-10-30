using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using WorkTimeRegister.Data;
using WorkTimeRegister.Services;
using WorkTimeRegister.ViewModels;
using WorkTimeRegister.Views;

namespace WorkTimeRegister;

public sealed partial class App : Application
{
    private IActivatableLifetime? _activatableLifetime;
    private bool exitPending;
    private bool exitApproved;
    private DockIconCoordinator? dockIconCoordinator;
    private DockMenuCoordinator? dockMenuCoordinator;
    private ComputerSleepService? sleepService;
    private MainWindowViewModel? activeViewModel;
    private System.ComponentModel.PropertyChangedEventHandler? recoveryChanged;
    private bool recoveryDialogOpen;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            desktop.MainWindow = new Window
            {
                Title = "Учёт рабочего времени", Width = 420, Height = 130,
                Content = new TextBlock { Text = "Загрузка данных…", Margin = new Thickness(24) }
            };
            desktop.ShutdownRequested += async (_, e) =>
            {
                if (exitApproved)
                    return;
                e.Cancel = true;
                await RequestExitAsync();
            };

            _activatableLifetime = TryGetFeature(typeof(IActivatableLifetime)) as IActivatableLifetime;
            if (_activatableLifetime is not null)
                _activatableLifetime.Activated += ApplicationActivated;

            desktop.Exit += (_, _) =>
            {
                if (_activatableLifetime is not null)
                    _activatableLifetime.Activated -= ApplicationActivated;

                DisposeDesktopServices();
                Program.ReleaseRetriedInstance();
            };
            if (Program.StartupFailure is { } startupFailure)
            {
                desktop.MainWindow = new StartupErrorWindow(Program.DatabasePath, startupFailure,
                    async () =>
                    {
                        if (!Program.RetryInstance())
                            throw Program.StartupFailure!;
                        WireInstance();
                        await InitializeDesktopAsync(desktop);
                    },
                    () => { exitApproved = true; desktop.Shutdown(); },
                    databaseError: false, communicationError: true);
            }
            else
            {
                WireInstance();
                _ = InitializeDesktopAsync(desktop);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void WireInstance()
    {
        if (Program.Instance is { } instance)
        {
            instance.ActivateRequested = async () => await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    ShowMainWindow();
                    return true;
                });
            instance.ExitRequested = async () => await Dispatcher.UIThread.InvokeAsync(() => RequestExitAsync(offerForce: false));
        }
    }

    private async Task InitializeDesktopAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        MainWindowViewModel? viewModel = null;
        var databaseError = true;
        try
        {
            DisposeDesktopServices();
            var repository = new SqliteTimeEntryRepository(Program.DatabasePath);
            await repository.InitializeAsync();
            viewModel = new MainWindowViewModel(repository, new TimeSummaryCalculator(), TimeProvider.System);
            await viewModel.Initialization;
            if (!viewModel.HasLoaded)
                throw new IOException(viewModel.ErrorMessage ?? "Не удалось загрузить рабочий день.");
            databaseError = false;
            var oldWindow = desktop.MainWindow;
            var window = new MainWindow { DataContext = viewModel };
            desktop.MainWindow = window;
            activeViewModel = viewModel;
            window.RetryIntegrationsRequested += () => ConnectOptionalServices(window, viewModel);
            recoveryChanged = (_, e) =>
            {
                if (e.PropertyName == nameof(MainWindowViewModel.PendingRecovery) && viewModel.PendingRecovery is not null)
                    Dispatcher.UIThread.Post(async () => await ShowRecoveryAsync(window, viewModel, desktop));
            };
            viewModel.PropertyChanged += recoveryChanged;
            if (oldWindow is not null)
            {
                window.Show();
                if (oldWindow is StartupErrorWindow failed)
                    failed.CloseAfterRecovery();
                else if (oldWindow is MainWindow previous)
                    previous.CloseForReplacement();
                else
                    oldWindow.Close();
            }
            ConnectOptionalServices(window, viewModel);
            await ShowRecoveryAsync(window, viewModel, desktop);
        }
        catch (Exception exception)
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            viewModel?.Dispose();
            DisposeDesktopServices();
            var old = desktop.MainWindow;
            var error = new StartupErrorWindow(Program.DatabasePath, exception,
                () => InitializeDesktopAsync(desktop), () => { exitApproved = true; desktop.Shutdown(); }, databaseError);
            desktop.MainWindow = error;
            if (old?.IsVisible == true)
                error.Show();
            if (old is StartupErrorWindow failed)
                failed.CloseAfterRecovery();
            else if (old is MainWindow previous)
                previous.CloseForReplacement();
            else
                old?.Close();
        }
    }

    private void ConnectOptionalServices(MainWindow window, MainWindowViewModel viewModel)
    {
        var warnings = new List<string>();
        viewModel.IntegrationWarning = null;
        try
        {
            dockIconCoordinator?.Dispose();
            dockIconCoordinator = null;
            dockIconCoordinator = new DockIconCoordinator(viewModel, DockIconServiceFactory.Create(exception =>
            {
                var warning = $"Динамическая иконка Dock недоступна: {exception.Message}. Учёт времени продолжает работать.";
                viewModel.IntegrationWarning = viewModel.HasIntegrationWarning ? viewModel.IntegrationWarning + "\n" + warning : warning;
            }));
        }
        catch (Exception exception) { warnings.Add($"Иконка Dock недоступна: {exception.Message}."); }
        try
        {
            if (dockMenuCoordinator is null)
            {
                dockMenuCoordinator = new DockMenuCoordinator(viewModel);
                NativeDock.SetMenu(this, dockMenuCoordinator.Menu);
            }
        }
        catch (Exception exception)
        {
            dockMenuCoordinator?.Dispose();
            dockMenuCoordinator = null;
            warnings.Add($"Меню Dock недоступно: {exception.Message}.");
        }
        try
        {
            sleepService?.Dispose();
            sleepService = null;
            sleepService = new ComputerSleepService(window, sleeping =>
            {
                var at = DateTimeOffset.UtcNow;
                Dispatcher.UIThread.Post(async () => await viewModel.HandlePowerEventAsync(sleeping, at), DispatcherPriority.Send);
            });
        }
        catch (Exception exception)
        {
            warnings.Add($"Уведомления сна недоступны: {exception.Message}. Пропущенный интервал потребует вашего решения.");
        }
        if (viewModel.IntegrationWarning is { } iconWarning)
            warnings.Insert(0, iconWarning);
        viewModel.IntegrationWarning = warnings.Count == 0 ? null : string.Join("\n", warnings);
    }

    private async Task ShowRecoveryAsync(MainWindow window, MainWindowViewModel viewModel,
        IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (recoveryDialogOpen || viewModel != activeViewModel || viewModel.PendingRecovery is null || exitApproved)
            return;
        recoveryDialogOpen = true;
        try
        {
            window.Show();
            while (!exitApproved && viewModel == activeViewModel && viewModel.PendingRecovery is not null)
                await new RecoveryDialog(viewModel, () => { exitApproved = true; desktop.Shutdown(); }).ShowDialog(window);
        }
        finally { recoveryDialogOpen = false; }
    }

    private void DisposeDesktopServices()
    {
        if (activeViewModel is not null && recoveryChanged is not null)
            activeViewModel.PropertyChanged -= recoveryChanged;
        recoveryChanged = null;
        foreach (var service in new IDisposable?[] { sleepService, dockIconCoordinator, dockMenuCoordinator, activeViewModel })
        {
            try { service?.Dispose(); }
            catch (Exception exception) { System.Diagnostics.Trace.TraceError($"Desktop cleanup failed: {exception}"); }
        }
        sleepService = null;
        dockIconCoordinator = null;
        dockMenuCoordinator = null;
        activeViewModel = null;
    }

    private void TrayIconClicked(object? sender, EventArgs e) => ShowMainWindow();

    private void OpenMainWindowClicked(object? sender, EventArgs e) => ShowMainWindow();

    private async void ExitApplicationClicked(object? sender, EventArgs e) => await RequestExitAsync();

    private async Task<bool> RequestExitAsync(bool offerForce = true)
    {
        if (exitPending || ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return false;
        exitPending = true;
        try
        {
            if (desktop.MainWindow?.DataContext is MainWindowViewModel viewModel)
            {
                while (!await viewModel.PrepareForExitAsync())
                {
                    ShowMainWindow();
                    if (viewModel.PendingRecovery is not null && desktop.MainWindow is MainWindow window)
                    {
                        await ShowRecoveryAsync(window, viewModel, desktop);
                        return false;
                    }
                    if (!offerForce)
                        return false;
                    var choice = await new ExitErrorDialog(viewModel.ErrorMessage).ShowDialog<ExitErrorChoice>(desktop.MainWindow);
                    if (choice == ExitErrorChoice.Cancel)
                        return false;
                    if (choice == ExitErrorChoice.Force)
                        break;
                }
            }
            exitApproved = true;
            Dispatcher.UIThread.Post(() => desktop.Shutdown(), DispatcherPriority.Background);
            return true;
        }
        finally { exitPending = false; }
    }

    private void ApplicationActivated(object? sender, ActivatedEventArgs e)
    {
        if (e.Kind == ActivationKind.Reopen)
            ShowMainWindow();
    }

    private void ShowMainWindow()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } window })
            return;

        _activatableLifetime?.TryLeaveBackground();
        if (!window.IsVisible)
            window.Show();
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
        window.Activate();
    }
}
