using SentinelOps.Api.Domain;

namespace SentinelOps.Api.Notifications;

// Shared between NotificationPreferencesController (no read use yet, but kept
// alongside the entity it evaluates) and the notification worker, which
// checks this before attempting delivery.
public static class NotificationPreferenceEvaluator
{
    // A missing preference row (never configured) means all defaults: email
    // enabled, no quiet hours.
    public static bool IsChannelEnabled(NotificationPreference? preference, string channel) =>
        channel != "email" || preference?.EmailEnabled != false;

    public static bool IsQuietHoursActive(NotificationPreference? preference, DateTimeOffset nowUtc)
    {
        if (preference is null) return false;
        if (preference.QuietHoursStartLocal is null || preference.QuietHoursEndLocal is null) return false;
        if (string.IsNullOrEmpty(preference.TimeZoneId)) return false;

        TimeZoneInfo timeZone;
        try
        {
            timeZone = TimeZoneInfo.FindSystemTimeZoneById(preference.TimeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            return false;
        }

        var localNow = TimeZoneInfo.ConvertTime(nowUtc, timeZone);
        var localTime = TimeOnly.FromDateTime(localNow.DateTime);
        var start = preference.QuietHoursStartLocal.Value;
        var end = preference.QuietHoursEndLocal.Value;

        // Same overnight-wraparound handling as ScheduleRotation/OnCallResolver
        // (see apps/api/Schedules/OnCallResolver.cs) — End <= Start means the
        // window crosses midnight.
        return end > start
            ? localTime >= start && localTime < end
            : localTime >= start || localTime < end;
    }
}
