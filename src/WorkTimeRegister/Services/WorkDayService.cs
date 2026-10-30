using WorkTimeRegister.Data;
using WorkTimeRegister.Models;

namespace WorkTimeRegister.Services;

public sealed class WorkDayService(ITimeEntryRepository repository, TimeProvider timeProvider, Guid? session = null)
{
    private static readonly Guid ProcessSessionId = Guid.NewGuid();
    private readonly Guid sessionId = session ?? ProcessSessionId;
    public Task<WorkDaySnapshot> ExecuteAsync(
        WorkDayOperation operation,
        WorkDaySettings? settings = null,
        TimeAdjustmentOperation adjustment = TimeAdjustmentOperation.Add,
        TimeEntryKind category = TimeEntryKind.Work,
        TimeSpan amount = default,
        RecoveryChoice recovery = RecoveryChoice.Exclude,
        DateTimeOffset? at = null,
        Guid powerCycleId = default,
        bool? paused = null) =>
        repository.ProcessWorkDayAsync(new WorkDayRequest(
            operation,
            at ?? timeProvider.GetUtcNow(),
            timeProvider.LocalTimeZone,
            settings,
            adjustment,
            category,
            amount,
            sessionId,
            recovery,
            powerCycleId,
            paused));

    public static DateTimeOffset Boundary(DateOnly date, TimeZoneInfo timeZone)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        while (timeZone.IsInvalidTime(local))
            local = local.AddMinutes(1);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, timeZone));
    }
}
