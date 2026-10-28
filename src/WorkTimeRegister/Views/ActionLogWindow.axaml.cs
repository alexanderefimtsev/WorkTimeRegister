using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using WorkTimeRegister.ViewModels;

namespace WorkTimeRegister.Views;

public sealed partial class ActionLogWindow : Window
{
    private static readonly FilePickerFileType JsonFileType = new("JSON")
    {
        Patterns = ["*.json"],
        MimeTypes = ["application/json"]
    };

    public ActionLogWindow() => InitializeComponent();

    private async void ExportJsonClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LogWindowViewModel viewModel)
            return;

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Экспорт журнала в JSON",
            SuggestedFileName = viewModel.SuggestedExportFileName,
            DefaultExtension = "json",
            FileTypeChoices = [JsonFileType],
            ShowOverwritePrompt = true
        });

        if (file is null)
            return;

        await using var stream = await file.OpenWriteAsync();
        if (stream.CanSeek)
            stream.SetLength(0);
        await viewModel.ExportAsync(stream);
    }

    private async void ImportJsonClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LogWindowViewModel viewModel)
            return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Импорт журнала из JSON",
            AllowMultiple = false,
            FileTypeFilter = [JsonFileType]
        });
        var file = files.FirstOrDefault();
        if (file is null)
            return;

        await using var stream = await file.OpenReadAsync();
        await viewModel.ImportAsync(stream);
    }
}
