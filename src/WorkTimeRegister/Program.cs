using Avalonia;
using WorkTimeRegister.Services;

namespace WorkTimeRegister;

internal static class Program
{
    internal static string DatabasePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WorkTimeRegister", "worktime.db");
    internal static SingleInstanceService? Instance { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--shutdown", StringComparer.Ordinal))
        {
            if (SingleInstanceService.SendAsync(DatabasePath, exit: true).GetAwaiter().GetResult())
                return 0;
            for (var attempt = 0; attempt < 20; attempt++)
            {
                using var stopped = SingleInstanceService.TryAcquire(DatabasePath);
                if (stopped is not null)
                    return 0;
                Thread.Sleep(100);
            }
            return 1;
        }

        using var instance = SingleInstanceService.TryAcquire(DatabasePath);
        if (instance is null)
            return SingleInstanceService.SendAsync(DatabasePath, exit: false).GetAwaiter().GetResult() ? 0 : 1;
        Instance = instance;
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
