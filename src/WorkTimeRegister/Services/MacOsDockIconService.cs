using System.Diagnostics;
using Avalonia.Platform;
using Avalonia.Threading;
using SkiaSharp;

namespace WorkTimeRegister.Services;

internal sealed class MacOsDockIconService : IDockIconService
{
    private static readonly Uri BaseIconUri = new("avares://WorkTimeRegister/Assets/AppIcon-1024.png");

    private readonly SKBitmap baseIcon;
    private DockIconPresentation? currentPresentation;
    private bool hasPresentation;
    private bool disabled;
    private bool disposed;

    public MacOsDockIconService()
    {
        using var stream = AssetLoader.Open(BaseIconUri);
        baseIcon = SKBitmap.Decode(stream)
            ?? throw new InvalidOperationException("Unable to load the application icon.");
    }

    public void Update(DockIconPresentation? presentation)
    {
        if (disposed || disabled)
            return;

        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => Update(presentation));
            return;
        }

        if (hasPresentation && Equals(currentPresentation, presentation))
            return;

        currentPresentation = presentation;
        hasPresentation = true;
        try
        {
            if (presentation is null)
                MacOsApplicationIcon.Reset();
            else
                MacOsApplicationIcon.Set(MacOsDockIconRenderer.Render(baseIcon, presentation));
        }
        catch (Exception exception)
        {
            disabled = true;
            ReportFailure("Unable to update the macOS Dock icon", exception);
            TryReset();
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        if (Dispatcher.UIThread.CheckAccess())
            DisposeCore();
        else
            Dispatcher.UIThread.Post(DisposeCore);
    }

    private void DisposeCore()
    {
        TryReset();
        baseIcon.Dispose();
    }

    private static void TryReset()
    {
        try
        {
            MacOsApplicationIcon.Reset();
        }
        catch (Exception exception)
        {
            ReportFailure("Unable to reset the macOS Dock icon", exception);
        }
    }

    private static void ReportFailure(string message, Exception exception)
    {
        var details = $"{message}: {exception}";
        Trace.WriteLine(details);
        Console.Error.WriteLine(details);
    }
}
