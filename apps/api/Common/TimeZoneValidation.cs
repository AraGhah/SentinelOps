namespace SentinelOps.Api.Common;

// TimeZoneInfo.FindSystemTimeZoneById never returns null for an unknown id —
// it throws. Centralizing the try/catch here keeps every caller (Schedules,
// NotificationPreferences, Organizations) from repeating a null check that
// can never trigger.
public static class TimeZoneValidation
{
    public static bool IsValid(string timeZoneId)
    {
        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            return true;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false;
        }
    }
}
