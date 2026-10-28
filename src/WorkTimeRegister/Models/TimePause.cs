namespace WorkTimeRegister.Models;

public sealed record TimePause(
    long Id,
    long TimeEntryId,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt);
