namespace WorkTimeRegister.Services;

internal static class DockIconServiceFactory
{
    public static IDockIconService Create(Action<Exception>? failed = null)
    {
        if (!OperatingSystem.IsMacOS())
            return new NoOpDockIconService();

        return new MacOsDockIconService(failed);
    }
}
