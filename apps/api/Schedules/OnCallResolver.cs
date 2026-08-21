using SentinelOps.Api.Domain;

namespace SentinelOps.Api.Schedules;

// Single source of truth for "who's on call right now," used by both SchedulesController
// and the ResponderAssignment worker. Not a general RRULE engine, just a simple recurring
// weekly slot model. An active ScheduleOverride always wins, including a null
// ResponderUserId (an intentional gap).
public static class OnCallResolver
{
    public static Guid? Resolve(
        Schedule schedule, IReadOnlyList<ScheduleRotation> rotations, IReadOnlyList<ScheduleOverride> overrides, DateTimeOffset nowUtc)
    {
        var activeOverride = overrides.FirstOrDefault(o => o.StartsAtUtc <= nowUtc && nowUtc < o.EndsAtUtc);
        if (activeOverride is not null) return activeOverride.ResponderUserId;

        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(schedule.TimeZoneId);
        var localNow = TimeZoneInfo.ConvertTime(nowUtc, timeZone);
        var localDayOfWeek = (int)localNow.DayOfWeek;
        var localTime = TimeOnly.FromDateTime(localNow.DateTime);

        var candidates = rotations.Where(r =>
            (r.EffectiveFromUtc is null || r.EffectiveFromUtc <= nowUtc)
            && (r.EffectiveToUtc is null || nowUtc < r.EffectiveToUtc)
            && IsActiveAt(r, localDayOfWeek, localTime));

        // Prefer a primary rotation; only use a backup responder when no
        // primary rotation covers this instant.
        var match = candidates.OrderBy(r => r.IsBackup).FirstOrDefault();
        return match?.ResponderUserId;
    }

    private static bool IsActiveAt(ScheduleRotation rotation, int localDayOfWeek, TimeOnly localTime)
    {
        if (rotation.EndTimeLocal > rotation.StartTimeLocal)
        {
            return rotation.DayOfWeek == localDayOfWeek
                && localTime >= rotation.StartTimeLocal && localTime < rotation.EndTimeLocal;
        }

        // Overnight shift: a query after local midnight lands on the next calendar day,
        // so it must also be checked against the previous day's rotation.
        var previousDay = (localDayOfWeek + 6) % 7;
        return (rotation.DayOfWeek == localDayOfWeek && localTime >= rotation.StartTimeLocal)
            || (rotation.DayOfWeek == previousDay && localTime < rotation.EndTimeLocal);
    }
}
