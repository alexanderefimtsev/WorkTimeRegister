using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace WorkTimeRegister.Views;

public enum ExitErrorChoice { Cancel, Retry, Force }

public sealed class ExitErrorDialog : Window
{
    public ExitErrorDialog(string? error)
    {
        Title = "Не удалось сохранить завершение дня";
        Width = 620;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var retry = new Button { Content = "Повторить" };
        var cancel = new Button { Content = "Остаться" };
        var force = new Button { Content = "Выйти без сохранения" };
        retry.Click += (_, _) => Close(ExitErrorChoice.Retry);
        cancel.Click += (_, _) => Close(ExitErrorChoice.Cancel);
        force.Click += (_, _) => Close(ExitErrorChoice.Force);
        Content = new StackPanel
        {
            Margin = new Thickness(24), Spacing = 16,
            Children =
            {
                new TextBlock { Text = "Завершение рабочего дня не сохранено", FontSize = 22, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new TextBlock { Text = error, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new TextBlock { Text = "При выходе без сохранения следующий запуск может потребовать восстановления неизвестного интервала.", TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { retry, cancel, force } }
            }
        };
    }
}
