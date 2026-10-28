namespace WorkTimeRegister.Models;

public sealed record TimeTransfer(
    long Id,
    DateTimeOffset RegisteredAt,
    TimeSpan WorkAdjustment,
    TimeSpan BreakAdjustment);
