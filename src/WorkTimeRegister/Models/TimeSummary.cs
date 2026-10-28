namespace WorkTimeRegister.Models;

public sealed record TimeSummary(
    TimeSpan TotalWork,
    TimeSpan TotalBreak,
    TimeSpan CurrentPeriod,
    TimeEntryKind? ActiveKind,
    bool IsPaused = false);
