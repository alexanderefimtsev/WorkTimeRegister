using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace WorkTimeRegister.Views;

public sealed class StartupErrorWindow : Window
{
    private bool recovered;
    public void CloseAfterRecovery() { recovered = true; Close(); }
    public StartupErrorWindow(string databasePath, Exception error, Func<Task> retry, Action exit, bool databaseError = true)
    {
        Title = databaseError ? "Не удалось открыть данные" : "Не удалось открыть главное окно";
        Width = 640;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        var detail = new TextBlock { Text = error.Message, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var retryButton = new Button { Content = "Повторить" };
        retryButton.Click += async (_, _) =>
        {
            retryButton.IsEnabled = false;
            try { await retry(); }
            finally { retryButton.IsEnabled = true; }
        };
        var folderButton = new Button { Content = "Открыть папку" };
        folderButton.Click += (_, _) =>
        {
            try
            {
                var directory = Path.GetDirectoryName(databasePath)!;
                if (!Directory.Exists(directory))
                    directory = Path.GetDirectoryName(directory)!;
                Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
            }
            catch (Exception exception) { detail.Text = exception.Message; }
        };
        var exitButton = new Button { Content = "Выйти" };
        exitButton.Click += (_, _) => exit();
        Content = new StackPanel
        {
            Margin = new Thickness(24), Spacing = 16,
            Children =
            {
                new TextBlock { Text = databaseError ? "Не удалось открыть базу данных" : "Не удалось открыть главное окно", FontSize = 24 },
                new TextBlock { Text = databaseError ? "Приложение не заменяло базу пустой. Проверьте доступ к файлу и свободное место на диске." : "Данные загружены, но интерфейс не удалось создать. Повторите открытие окна. Это не означает повреждение базы.", TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new SelectableTextBlock { Text = databasePath, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                detail,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { retryButton, folderButton, exitButton } }
            }
        };
        Closing += (_, e) =>
        {
            if (!recovered && e.CloseReason is not (WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown))
            {
                e.Cancel = true;
                exit();
            }
        };
    }
}
