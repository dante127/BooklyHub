namespace BooklyHub.Application.Common.Helpers;

public static class TimeZoneHelper
{
    public static TimeZoneInfo ResolveTimeZone(string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            return TimeZoneInfo.Utc;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            if (TimeZoneInfo.TryConvertIanaIdToWindowsId(timeZoneId, out var windowsId) &&
                TimeZoneInfo.TryFindSystemTimeZoneById(windowsId, out var tzFromWindows))
            {
                return tzFromWindows;
            }

            if (TimeZoneInfo.TryConvertWindowsIdToIanaId(timeZoneId, out var ianaId) &&
                TimeZoneInfo.TryFindSystemTimeZoneById(ianaId, out var tzFromIana))
            {
                return tzFromIana;
            }

            return TimeZoneInfo.Utc;
        }
    }

    public static DateTime ToUtc(DateTime localDateTime, TimeZoneInfo timeZone)
    {
        // Handle invalid time during DST spring forward
        if (timeZone.IsInvalidTime(localDateTime))
        {
            // Advance by the gap duration
            var adjustment = timeZone.GetAdjustmentRules()
                .FirstOrDefault(r => r.DateStart <= localDateTime.Date && r.DateEnd >= localDateTime.Date);
            var gap = adjustment?.DaylightDelta ?? TimeSpan.FromHours(1);
            localDateTime = localDateTime.Add(gap);
        }

        // Handle ambiguous time during DST fall back: pick standard time
        if (timeZone.IsAmbiguousTime(localDateTime))
        {
            var offsets = timeZone.GetAmbiguousTimeOffsets(localDateTime);
            var standardOffset = offsets.Min();
            var offsetDateTime = new DateTimeOffset(localDateTime, standardOffset);
            return offsetDateTime.UtcDateTime;
        }

        return TimeZoneInfo.ConvertTimeToUtc(localDateTime, timeZone);
    }

    public static DateTime ToLocal(DateTime utcDateTime, TimeZoneInfo timeZone)
    {
        var utc = DateTime.SpecifyKind(utcDateTime, DateTimeKind.Utc);
        return TimeZoneInfo.ConvertTimeFromUtc(utc, timeZone);
    }
}
