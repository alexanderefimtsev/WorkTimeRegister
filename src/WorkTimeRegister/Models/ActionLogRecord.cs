namespace WorkTimeRegister.Models;

public sealed record ActionLogRecord(
    long Id,
    DateTimeOffset RegisteredAt,
    string Action,
    TimeEntryKind? CurrentMode,
    TimeSpan TotalWork,
    TimeSpan TotalBreak,
    TimeSpan CurrentPeriod,
    bool IsPaused = false)
{
    public DateOnly? WorkDayDate { get; init; }
    public bool? IsWorkDayOpen { get; init; }
}
