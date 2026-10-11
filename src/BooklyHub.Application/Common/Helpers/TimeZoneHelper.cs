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

    /// <summary>
    /// ONB-01: the question <see cref="ResolveTimeZone"/> cannot answer about itself. That method has to return a
    /// zone, so an id it does not know becomes UTC (<c>:30</c>) — which is right for a reader that must not fall
    /// over, and wrong as permission to store such an id: a clinic written as <c>Asia/Damascas</c> would answer a
    /// whole day of slots two hours early, in a response that reports no error anywhere. So every route that
    /// persists a <c>TimeZoneId</c> asks this first and refuses.
    ///
    /// The ladder is deliberately the same three steps <see cref="ResolveTimeZone"/> walks, in the same order: a
    /// validator that disagrees with the reader is a second opinion nobody needs. Blank is false here and UTC there,
    /// because "no zone named" is a caller mistake on a write and a tolerated absence on a read.
    /// </summary>
    public static bool IsKnownTimeZoneId(string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId)) return false;
        if (TimeZoneInfo.TryFindSystemTimeZoneById(timeZoneId, out _)) return true;

        if (TimeZoneInfo.TryConvertIanaIdToWindowsId(timeZoneId, out var windowsId) &&
            TimeZoneInfo.TryFindSystemTimeZoneById(windowsId, out _))
        {
            return true;
        }

        return TimeZoneInfo.TryConvertWindowsIdToIanaId(timeZoneId, out var ianaId) &&
               TimeZoneInfo.TryFindSystemTimeZoneById(ianaId, out _);
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
