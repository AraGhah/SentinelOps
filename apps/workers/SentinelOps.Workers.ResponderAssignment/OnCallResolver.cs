using SentinelOps.Api.Domain;

namespace SentinelOps.Workers.ResponderAssignment;

// Not a general RRULE engine — matches ScheduleRotation's own "simple recurring
// weekly slot" model (see Schedule.cs). A ScheduleOverride active for `nowUtc`
// always wins, including one whose ResponderUserId is null (an intentional gap).
public static class OnCallResolver
{
    public static Guid? Resolve(
        Schedule schedule, IReadOnlyList<ScheduleRotation> rotations, IReadOnlyList<ScheduleOverride> overrides, DateTimeOffset nowUtc)
    {
        var activeOverride = overrides.FirstOrDefault(o => o.StartsAtUtc <= nowUtc && o.EndsAtUtc > nowUtc);
        if (activeOverride is not null) return activeOverride.ResponderUserId;

        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(schedule.TimeZoneId);
        var localNow = TimeZoneInfo.ConvertTime(nowUtc, timeZone);
        var dayOfWeek = (int)localNow.DayOfWeek;
        var timeOfDay = TimeOnly.FromDateTime(localNow.DateTime);

        var candidates = rotations.Where(r => r.DayOfWeek == dayOfWeek && !r.IsBackup
            && (r.EffectiveFromUtc is null || r.EffectiveFromUtc <= nowUtc)
            && (r.EffectiveToUtc is null || r.EffectiveToUtc > nowUtc));

        foreach (var rotation in candidates)
        {
            var inWindow = rotation.EndTimeLocal < rotation.StartTimeLocal
                ? timeOfDay >= rotation.StartTimeLocal || timeOfDay < rotation.EndTimeLocal
                : timeOfDay >= rotation.StartTimeLocal && timeOfDay < rotation.EndTimeLocal;
            if (inWindow) return rotation.ResponderUserId;
        }

        return null;
    }
}
