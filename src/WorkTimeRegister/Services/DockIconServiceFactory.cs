using System.Diagnostics;

namespace WorkTimeRegister.Services;

internal static class DockIconServiceFactory
{
    public static IDockIconService Create()
    {
        if (!OperatingSystem.IsMacOS())
            return new NoOpDockIconService();

        try
        {
            return new MacOsDockIconService();
        }
        catch (Exception exception)
        {
            Trace.WriteLine($"Unable to initialize the macOS Dock icon: {exception}");
            return new NoOpDockIconService();
        }
    }
}
