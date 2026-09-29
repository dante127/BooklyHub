using BooklyHub.Domain.Enums;

namespace BooklyHub.Domain.Entities.Appointments;

/// <summary>
/// When a booking that was never attended may be closed on the customer's behalf. The clock decides, so
/// the bound is one place: the sweep that writes the status, the report that explains it and the tests
/// that prove both have to read the same numbers.
/// </summary>
public static class NoShowClosurePolicy
{
    /// <summary>
    /// Measured from the end of the visit, not its start, because a late arrival is still an arrival.
    /// </summary>
    public const int GraceHours = 6;

    /// <summary>
    /// How far back one sweep may reach. Without it the first run restates every open booking the tenant
    /// ever had, which is a financial restatement disguised as a housekeeping job.
    /// </summary>
    public const int LookbackDays = 14;

    public static DateTime DeadlineUtc(DateTime nowUtc) => nowUtc - TimeSpan.FromHours(GraceHours);

    public static DateTime EarliestEndUtc(DateTime nowUtc) => DeadlineUtc(nowUtc) - TimeSpan.FromDays(LookbackDays);

    /// <summary>
    /// Only a Confirmed booking is a customer's absence: Pending was never accepted by the clinic, and
    /// CheckedIn or InProgress mean the customer appeared and staff never closed the visit.
    /// </summary>
    public static bool IsUnattendedAndPastItsWindow(AppointmentStatus status, DateTime endAtUtc, DateTime nowUtc) =>
        status == AppointmentStatus.Confirmed &&
        endAtUtc <= DeadlineUtc(nowUtc) &&
        endAtUtc >= EarliestEndUtc(nowUtc);
}
