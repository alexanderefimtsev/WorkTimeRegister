using Avalonia;
using WorkTimeRegister.Services;

namespace WorkTimeRegister;

internal static class Program
{
    internal static string DatabasePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WorkTimeRegister", "worktime.db");
    internal static SingleInstanceService? Instance { get; private set; }
    internal static Exception? StartupFailure { get; private set; }
    private static bool instanceAcquiredOnRetry;

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--shutdown", StringComparer.Ordinal))
            return SingleInstanceService.RequestShutdownAsync(DatabasePath).GetAwaiter().GetResult() ? 0 : 1;

        SingleInstanceService? instance;
        try { instance = SingleInstanceService.TryAcquire(DatabasePath); }
        catch (IOException exception)
        {
            StartupFailure = exception;
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        using (instance)
        {
            if (instance is null)
            {
                if (SingleInstanceService.SendAsync(DatabasePath, exit: false).GetAwaiter().GetResult())
                    return 0;
                StartupFailure = new IOException("Другой экземпляр уже ведёт учёт, но связь с ним недоступна. Закройте его и повторите запуск.");
                return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            }
            Instance = instance;
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
    }

    internal static bool RetryInstance()
    {
        try
        {
            Instance = SingleInstanceService.TryAcquire(DatabasePath);
            StartupFailure = Instance is null ? new IOException("Другое приложение уже ведёт учёт времени.") : null;
            instanceAcquiredOnRetry = Instance is not null;
            return Instance is not null;
        }
        catch (IOException exception)
        {
            StartupFailure = exception;
            return false;
        }
    }

    internal static void ReleaseRetriedInstance()
    {
        if (instanceAcquiredOnRetry)
            Instance?.Dispose();
        instanceAcquiredOnRetry = false;
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
