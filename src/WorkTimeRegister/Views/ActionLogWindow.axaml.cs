using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using WorkTimeRegister.Services;
using WorkTimeRegister.ViewModels;

namespace WorkTimeRegister.Views;

public sealed partial class ActionLogWindow : Window
{
    private bool fileOperationInProgress;
    private static readonly FilePickerFileType JsonFileType = new("JSON")
    {
        Patterns = ["*.json"],
        MimeTypes = ["application/json"]
    };

    public ActionLogWindow() => InitializeComponent();

    private async void ExportJsonClicked(object? sender, RoutedEventArgs e)
    {
        if (fileOperationInProgress || DataContext is not LogWindowViewModel viewModel)
            return;
        SetFileOperation(true);
        try
        {
            using var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Экспорт журнала в JSON",
                SuggestedFileName = viewModel.SuggestedExportFileName,
                DefaultExtension = "json",
                FileTypeChoices = [JsonFileType],
                ShowOverwritePrompt = true
            });
            if (file is null)
                return;
            var path = file.TryGetLocalPath()
                ?? throw new IOException("Выберите файл на локальном диске для безопасного сохранения.");
            await new AtomicFileService().WriteAsync(path, viewModel.ExportAsync);
        }
        catch (Exception exception) { viewModel.ReportFileError("экспортировать журнал", exception); }
        finally { SetFileOperation(false); }
    }

    private async void ImportJsonClicked(object? sender, RoutedEventArgs e)
    {
        if (fileOperationInProgress || DataContext is not LogWindowViewModel viewModel)
            return;
        SetFileOperation(true);
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Импорт журнала из JSON",
                AllowMultiple = false,
                FileTypeFilter = [JsonFileType]
            });
            using var file = files.FirstOrDefault();
            if (file is null)
                return;
            await using var stream = await file.OpenReadAsync();
            await viewModel.ImportAsync(stream);
        }
        catch (Exception exception) { viewModel.ReportFileError("импортировать журнал", exception); }
        finally { SetFileOperation(false); }
    }

    private void SetFileOperation(bool value)
    {
        fileOperationInProgress = value;
        ExportJsonButton.IsEnabled = !value;
        ImportJsonButton.IsEnabled = !value;
    }
}
