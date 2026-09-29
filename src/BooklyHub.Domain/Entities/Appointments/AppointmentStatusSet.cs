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
}
