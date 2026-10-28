using WorkTimeRegister.Data;
using WorkTimeRegister.Models;

namespace WorkTimeRegister.Services;

public sealed class WorkDayService(ITimeEntryRepository repository, TimeProvider timeProvider)
{
    public Task<WorkDaySnapshot> ExecuteAsync(
        WorkDayOperation operation,
        WorkDaySettings? settings = null,
        TimeAdjustmentOperation adjustment = TimeAdjustmentOperation.Add,
        TimeEntryKind category = TimeEntryKind.Work,
        TimeSpan amount = default) =>
        repository.ProcessWorkDayAsync(new WorkDayRequest(
            operation,
            timeProvider.GetUtcNow(),
            timeProvider.LocalTimeZone,
            settings,
            adjustment,
            category,
            amount));

    public static DateTimeOffset Boundary(DateOnly date, TimeZoneInfo timeZone)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        while (timeZone.IsInvalidTime(local))
            local = local.AddMinutes(1);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, timeZone));
    }
}
