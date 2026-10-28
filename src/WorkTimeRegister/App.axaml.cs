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

                sleepService?.Dispose();
                dockIconCoordinator?.Dispose();
                dockMenuCoordinator?.Dispose();
                (desktop.MainWindow?.DataContext as MainWindowViewModel)?.Dispose();
            };
            if (Program.Instance is { } instance)
            {
                instance.ActivateRequested = async () => await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    ShowMainWindow();
                    return true;
                });
                instance.ExitRequested = async () => await Dispatcher.UIThread.InvokeAsync(() => RequestExitAsync(offerForce: false));
            }
            _ = InitializeDesktopAsync(desktop);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async Task InitializeDesktopAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        MainWindowViewModel? viewModel = null;
        try
        {
            var repository = new SqliteTimeEntryRepository(Program.DatabasePath);
            await repository.InitializeAsync();
            viewModel = new MainWindowViewModel(repository, new TimeSummaryCalculator(), TimeProvider.System);
            await viewModel.Initialization;
            if (!viewModel.HasLoaded)
                throw new IOException(viewModel.ErrorMessage ?? "Не удалось загрузить рабочий день.");
            var oldWindow = desktop.MainWindow;
            var window = new MainWindow { DataContext = viewModel };
            dockIconCoordinator = new DockIconCoordinator(viewModel, DockIconServiceFactory.Create());
            dockMenuCoordinator = new DockMenuCoordinator(viewModel);
            NativeDock.SetMenu(this, dockMenuCoordinator.Menu);
            desktop.MainWindow = window;
            var captured = viewModel;
            sleepService = new ComputerSleepService(window, sleeping =>
            {
                var at = DateTimeOffset.UtcNow;
                Dispatcher.UIThread.Post(async () => await captured.HandlePowerEventAsync(sleeping, at), DispatcherPriority.Send);
            });
            if (oldWindow is not null)
            {
                window.Show();
                if (oldWindow is StartupErrorWindow failed)
                    failed.CloseAfterRecovery();
                else
                    oldWindow.Close();
            }
            if (viewModel.PendingRecovery is not null)
            {
                window.Show();
                await new RecoveryDialog(viewModel, () => { exitApproved = true; desktop.Shutdown(); }).ShowDialog(window);
            }
        }
        catch (Exception exception)
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            viewModel?.Dispose();
            sleepService?.Dispose();
            dockIconCoordinator?.Dispose();
            dockMenuCoordinator?.Dispose();
            var old = desktop.MainWindow;
            var error = new StartupErrorWindow(Program.DatabasePath, exception,
                () => InitializeDesktopAsync(desktop), () => { exitApproved = true; desktop.Shutdown(); });
            desktop.MainWindow = error;
            if (old?.IsVisible == true)
                error.Show();
            if (old is StartupErrorWindow failed)
                failed.CloseAfterRecovery();
            else
                old?.Close();
        }
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
