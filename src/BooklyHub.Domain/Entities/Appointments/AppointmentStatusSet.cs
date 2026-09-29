using BooklyHub.Domain.Enums;

namespace BooklyHub.Domain.Entities.Appointments;

/// <summary>
/// The statuses that end an appointment, so anything else is still owed a visit. Reads of "what is
/// upcoming" and "what occupies a slot" have to agree on this or the two answers describe different books.
/// </summary>
public static class AppointmentStatusSet
{
    /// <summary>
    /// Rescheduled is deliberately not here: BL-06 makes it unreachable for new rows, but a legacy row in
    /// it still holds a customer and a time, so it is a live booking whose visit has not happened.
    /// </summary>
    public static readonly AppointmentStatus[] Closed =
    [
        AppointmentStatus.Completed,
        AppointmentStatus.Cancelled,
        AppointmentStatus.NoShow
    ];

    public static bool IsClosed(AppointmentStatus status) => Closed.Contains(status);

    /// <summary>
    /// The customer appeared and staff never recorded how the visit ended. Reporting these separately from a
    /// no-show matters: closing one of them as NoShow would blame the customer for the clinic's own gap.
    /// </summary>
    public static readonly AppointmentStatus[] AwaitingOutcome =
    [
        AppointmentStatus.CheckedIn,
        AppointmentStatus.InProgress
    ];

    /// <summary>
    /// Statuses a debt can still be chased for: the clinic accepted the visit, or performed it. Pending is
    /// excluded because nothing was ever agreed to render, and AwaitingOutcome is excluded because that row
    /// has to be closed before its money question can be answered at all. Closed is not reused here: a
    /// Completed row ends the visit and still owes money, while a Cancelled or NoShow row ends the visit
    /// with nothing owed, so the same status answers both questions differently.
    /// </summary>
    public static readonly AppointmentStatus[] Collectable =
    [
        AppointmentStatus.Confirmed,
        AppointmentStatus.Completed
    ];
}
