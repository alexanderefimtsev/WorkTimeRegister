namespace WorkTimeRegister.Models;

public enum TimeEntryKind
{
    Work,
    Break
}

public sealed record TimeEntry(
    long Id,
    TimeEntryKind Kind,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    TimeSpan CurrentPeriodAdjustment = default);
