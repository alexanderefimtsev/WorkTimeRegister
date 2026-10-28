namespace WorkTimeRegister.Models;

public enum WorkDayCloseReason
{
    Manual,
    ApplicationExit,
    Midnight,
    Migration
}

public sealed record WorkDay(
    long Id,
    DateOnly Date,
    DateTimeOffset DayStart,
    DateTimeOffset DayEnd,
    string TimeZoneId,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt,
    TimeEntryKind LastMode,
    bool WasPaused,
    WorkDayCloseReason? CloseReason)
{
    public bool IsOpen => StartedAt.HasValue && !EndedAt.HasValue;
}

public sealed record WorkDaySettings(
    bool AutoStartOnLaunch = false,
    bool RestoreOnLaunch = false,
    bool AutoContinueAtMidnight = false);

public enum WorkDayOperation
{
    Initialize,
    Refresh,
    StartDay,
    EndDay,
    StartWork,
    StartBreak,
    ToggleMode,
    TogglePause,
    Exit,
    SaveSettings,
    AdjustTime,
    AdjustCurrentPeriod
}

public sealed record WorkDayRequest(
    WorkDayOperation Operation,
    DateTimeOffset Now,
    TimeZoneInfo TimeZone,
    WorkDaySettings? Settings = null,
    TimeAdjustmentOperation Adjustment = TimeAdjustmentOperation.Add,
    TimeEntryKind Category = TimeEntryKind.Work,
    TimeSpan Amount = default);

public sealed record WorkDaySnapshot(
    WorkDay? Day,
    DateOnly Date,
    TimeSummary Summary,
    WorkDaySettings Settings);
