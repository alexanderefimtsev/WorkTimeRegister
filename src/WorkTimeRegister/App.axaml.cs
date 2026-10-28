using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
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

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var databasePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WorkTimeRegister",
                "worktime.db");
            var repository = new SqliteTimeEntryRepository(databasePath);
            repository.InitializeAsync().GetAwaiter().GetResult();
            var viewModel = new MainWindowViewModel(repository, new TimeSummaryCalculator(), TimeProvider.System);
            var dockIconCoordinator = new DockIconCoordinator(viewModel, DockIconServiceFactory.Create());
            var dockMenuCoordinator = new DockMenuCoordinator(viewModel);
            NativeDock.SetMenu(this, dockMenuCoordinator.Menu);
            desktop.MainWindow = new MainWindow { DataContext = viewModel };
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

                dockIconCoordinator.Dispose();
                dockMenuCoordinator.Dispose();
                viewModel.Dispose();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void TrayIconClicked(object? sender, EventArgs e) => ShowMainWindow();

    private void OpenMainWindowClicked(object? sender, EventArgs e) => ShowMainWindow();

    private async void ExitApplicationClicked(object? sender, EventArgs e) => await RequestExitAsync();

    private async Task RequestExitAsync()
    {
        if (exitPending || ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime
            { MainWindow.DataContext: MainWindowViewModel viewModel } desktop)
            return;
        exitPending = true;
        try
        {
            if (await viewModel.PrepareForExitAsync())
            {
                exitApproved = true;
                desktop.Shutdown();
            }
            else
                ShowMainWindow();
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
