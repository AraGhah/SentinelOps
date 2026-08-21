namespace SentinelOps.Api.Common;

// FindSystemTimeZoneById throws rather than returning null for an unknown id;
// centralize the try/catch instead of repeating it in every caller.
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
