using WorkTimeRegister.Data;
using WorkTimeRegister.Models;

namespace WorkTimeRegister.Services;

public sealed class WorkDayService(ITimeEntryRepository repository, TimeProvider timeProvider)
{
    private readonly Guid sessionId = Guid.NewGuid();
    public Task<WorkDaySnapshot> ExecuteAsync(
        WorkDayOperation operation,
        WorkDaySettings? settings = null,
        TimeAdjustmentOperation adjustment = TimeAdjustmentOperation.Add,
        TimeEntryKind category = TimeEntryKind.Work,
        TimeSpan amount = default,
        RecoveryChoice recovery = RecoveryChoice.Exclude,
        DateTimeOffset? at = null) =>
        repository.ProcessWorkDayAsync(new WorkDayRequest(
            operation,
            at ?? timeProvider.GetUtcNow(),
            timeProvider.LocalTimeZone,
            settings,
            adjustment,
            category,
            amount,
            sessionId,
            recovery));

    public static DateTimeOffset Boundary(DateOnly date, TimeZoneInfo timeZone)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        while (timeZone.IsInvalidTime(local))
            local = local.AddMinutes(1);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, timeZone));
    }
}
