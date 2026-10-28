using System.ComponentModel;
using Avalonia.Controls;
using WorkTimeRegister.ViewModels;

namespace WorkTimeRegister.Services;

public sealed class DockMenuCoordinator : IDisposable
{
    private readonly MainWindowViewModel viewModel;
    private readonly NativeMenuItem modeItem;
    private readonly NativeMenuItem pauseItem;
    private readonly NativeMenuItem workDayItem;
    private bool disposed;

    public DockMenuCoordinator(MainWindowViewModel viewModel)
    {
        this.viewModel = viewModel;
        modeItem = new NativeMenuItem { Command = viewModel.ModeSwitchCommand };
        pauseItem = new NativeMenuItem { Command = viewModel.TogglePauseCommand };
        workDayItem = new NativeMenuItem { Command = viewModel.ToggleWorkDayCommand };
        Menu = new NativeMenu
        {
            modeItem,
            pauseItem,
            new NativeMenuItemSeparator(),
            workDayItem
        };

        viewModel.PropertyChanged += ViewModelPropertyChanged;
        viewModel.ModeSwitchCommand.CanExecuteChanged += CommandCanExecuteChanged;
        viewModel.TogglePauseCommand.CanExecuteChanged += CommandCanExecuteChanged;
        viewModel.ToggleWorkDayCommand.CanExecuteChanged += CommandCanExecuteChanged;
        Menu.NeedsUpdate += MenuNeedsUpdate;
        Update();
    }

    public NativeMenu Menu { get; }

    private void ViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.ModeSwitchActionText)
            or nameof(MainWindowViewModel.PauseActionText)
            or nameof(MainWindowViewModel.WorkDayActionText)
            or nameof(MainWindowViewModel.IsReady))
            Update();
    }

    private void CommandCanExecuteChanged(object? sender, EventArgs e) => Update();

    private void MenuNeedsUpdate(object? sender, EventArgs e) => Update();

    private void Update()
    {
        if (disposed)
            return;

        modeItem.Header = viewModel.ModeSwitchActionText;
        pauseItem.Header = viewModel.PauseActionText;
        workDayItem.Header = viewModel.WorkDayActionText;
        modeItem.IsEnabled = viewModel.ModeSwitchCommand.CanExecute(null);
        pauseItem.IsEnabled = viewModel.TogglePauseCommand.CanExecute(null);
        workDayItem.IsEnabled = viewModel.ToggleWorkDayCommand.CanExecute(null);
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        viewModel.PropertyChanged -= ViewModelPropertyChanged;
        viewModel.ModeSwitchCommand.CanExecuteChanged -= CommandCanExecuteChanged;
        viewModel.TogglePauseCommand.CanExecuteChanged -= CommandCanExecuteChanged;
        viewModel.ToggleWorkDayCommand.CanExecuteChanged -= CommandCanExecuteChanged;
        Menu.NeedsUpdate -= MenuNeedsUpdate;
        foreach (var item in new[] { modeItem, pauseItem, workDayItem })
        {
            item.Command = null;
            item.IsEnabled = false;
        }
    }
}
