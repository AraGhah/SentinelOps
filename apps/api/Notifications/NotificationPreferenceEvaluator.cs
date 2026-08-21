using SentinelOps.Api.Domain;

namespace SentinelOps.Api.Notifications;

// Used by the notification worker before attempting delivery.
public static class NotificationPreferenceEvaluator
{
    // No preference row means all defaults: email enabled, no quiet hours.
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
        catch (InvalidTimeZoneException)
        {
            // Corrupt zone data on this host; treat quiet hours as unconfigured.
            return false;
        }

        var localNow = TimeZoneInfo.ConvertTime(nowUtc, timeZone);
        var localTime = TimeOnly.FromDateTime(localNow.DateTime);
        var start = preference.QuietHoursStartLocal.Value;
        var end = preference.QuietHoursEndLocal.Value;

        // End <= Start means the window crosses midnight (same handling as OnCallResolver).
        return end > start
            ? localTime >= start && localTime < end
            : localTime >= start || localTime < end;
    }
}
