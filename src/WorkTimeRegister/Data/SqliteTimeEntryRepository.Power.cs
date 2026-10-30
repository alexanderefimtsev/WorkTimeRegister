using WorkTimeRegister.Models;

namespace WorkTimeRegister.Data;

public sealed partial class SqliteTimeEntryRepository
{
    private static bool IsStalePowerEvent(WorkDayRequest request, RuntimeState state)
    {
        if (state.LastPowerEventAt >= request.Now || state.LastUserActionAt > request.Now)
            return true;
        if (request.Operation == WorkDayOperation.Suspend)
            return state.SleepStartedAt.HasValue;
        if (state.SleepStartedAt.HasValue)
            return request.PowerCycleId != Guid.Empty && state.SleepCycleId is not null &&
                   state.SleepCycleId != request.PowerCycleId.ToString();
        return request.PowerCycleId != Guid.Empty || state.LastUserActionAt >= request.Now;
    }
}
