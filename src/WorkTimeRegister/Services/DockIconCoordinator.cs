using System.ComponentModel;
using WorkTimeRegister.ViewModels;

namespace WorkTimeRegister.Services;

public sealed class DockIconCoordinator : IDisposable
{
    private readonly MainWindowViewModel viewModel;
    private readonly IDockIconService dockIconService;
    private DockIconPresentation? currentPresentation;
    private bool hasPresentation;
    private bool disposed;

    public DockIconCoordinator(MainWindowViewModel viewModel, IDockIconService dockIconService)
    {
        this.viewModel = viewModel;
        this.dockIconService = dockIconService;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Update();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.CurrentPeriod)
            or nameof(MainWindowViewModel.ActiveKind)
            or nameof(MainWindowViewModel.IsPaused)
            or nameof(MainWindowViewModel.TotalWork)
            or nameof(MainWindowViewModel.TotalBreak))
            Update();
    }

    private void Update()
    {
        var presentation = DockIconPresentation.Create(
            viewModel.ActiveKind,
            viewModel.IsPaused,
            viewModel.CurrentPeriodValue,
            viewModel.TotalWork,
            viewModel.TotalBreak);
        if (hasPresentation && Equals(currentPresentation, presentation))
            return;

        currentPresentation = presentation;
        hasPresentation = true;
        dockIconService.Update(presentation);
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        try
        {
            dockIconService.Update(null);
        }
        finally
        {
            dockIconService.Dispose();
        }
    }
}
