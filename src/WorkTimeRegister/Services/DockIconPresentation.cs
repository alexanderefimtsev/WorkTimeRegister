using WorkTimeRegister.Models;

namespace WorkTimeRegister.Services;

public enum DockIconMode
{
    Work,
    Break,
    Pause
}

public sealed record DockIconPresentation(DockIconMode Mode, string ModeText, string TimeText)
{
    public string TotalTimeText { get; init; } = "0m";

    public static DockIconPresentation? Create(
        TimeEntryKind? activeKind,
        bool isPaused,
        TimeSpan currentPeriod) =>
        Create(activeKind, isPaused, currentPeriod, TimeSpan.Zero, TimeSpan.Zero);

    public static DockIconPresentation? Create(
        TimeEntryKind? activeKind,
        bool isPaused,
        TimeSpan currentPeriod,
        TimeSpan totalWork,
        TimeSpan totalBreak)
    {
        if (!activeKind.HasValue)
            return null;

        var mode = isPaused
            ? DockIconMode.Pause
            : activeKind == TimeEntryKind.Work ? DockIconMode.Work : DockIconMode.Break;
        var modeText = mode switch
        {
            DockIconMode.Work => "РАБОТА",
            DockIconMode.Break => "ПЕРЕРЫВ",
            DockIconMode.Pause => "ПАУЗА",
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };

        var totalCurrentMode = activeKind == TimeEntryKind.Work ? totalWork : totalBreak;
        return new DockIconPresentation(mode, modeText, Format(currentPeriod))
        {
            TotalTimeText = Format(totalCurrentMode)
        };
    }

    private static string Format(TimeSpan value)
    {
        var totalMinutes = Math.Max(0, value.Ticks / TimeSpan.TicksPerMinute);
        var hours = totalMinutes / 60;
        var minutes = totalMinutes % 60;
        return hours == 0
            ? $"{minutes}m"
            : minutes == 0 ? $"{hours}h" : $"{hours}h {minutes}m";
    }
}
