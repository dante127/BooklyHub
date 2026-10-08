namespace BooklyHub.Application.Scheduling;

public record GetAvailabilityQuery(
    Guid TenantId,
    Guid LocationId,
    Guid ServiceId,
    Guid? StaffId,
    DateOnly Date);

public record AvailableSlotDto(
    DateTime StartAtUtc,
    DateTime EndAtUtc,
    DateTime StartAtLocal,
    DateTime EndAtLocal,
    Guid StaffId,
    string StaffName,
    Guid ServiceId,
    string ServiceName,
    int DurationMinutes,
    decimal Price,
    string Currency,
    IReadOnlyList<Guid> AvailableResourceIds);

public record DayAvailabilityDto(
    DateOnly Date,
    string TimeZoneId,
    bool IsOpen,
    IReadOnlyList<AvailableSlotDto> Slots,
    bool SlotsTruncated);

/// <summary>
/// PERF-04: the ceiling on what one anonymous availability day may answer with.
///
/// The day was the one response in this API whose size the tenant's data chose: the grid walks every candidate start
/// for every staff member who can perform the service, so the body grew linearly with the roster — measured on a live
/// host at the default 15-minute step (`docs/PERFORMANCE.md` §6): 1 staff 6.7 KB, 10 staff 111 KB, 50 staff
/// **683 KB**, all from an `[AllowAnonymous]` route with no bound on it.
///
/// 500 is a quarter of that heaviest day (50 staff × 40 starts on a 15-minute grid is about 2,000 slots) and it is
/// priced in bytes rather than in slots: measured on this host by `AvailabilityPayloadBoundTests`, a 500-slot answer
/// is **188,586 bytes** (≈377 B a slot), where the same fixture's unbounded days were 384,627 bytes for a 1,020-slot
/// roster and 215,354 for a 571-slot one-minute grid. It is not a ceiling a real clinic meets: a 08:00–18:00 shift
/// with a 30-minute service offers about 20 slots a person, so 500 is twenty-five staff-days, and a day under it
/// answers every slot it has with `slotsTruncated: false` — pinned at eight staff, 160 slots, 60,407 bytes.
///
/// The roster itself is deliberately **not** capped. The same measurement says the work is not the cost — 19.9 ms for
/// a 50-staff day, 8.4 ms for 1,440 starts on one — so a cap there would buy milliseconds and take a named staff
/// member's day away from a portal that asked for it, which is the trade `PERFORMANCE.md` §6 already refused once
/// for the same number.
/// </summary>
public static class AvailabilityLimits
{
    public const int MaxSlotsPerDay = 500;
}

public enum SlotUnavailableReason
{
    /// <summary>The tenant, location or service is not bookable, or the date is a holiday.</summary>
    Closed,

    /// <summary>The staff member is inactive, not assigned to this service, has no shift, or the slot falls in a break or approved time off.</summary>
    StaffNotAvailable,

    /// <summary>The slot is inside a shift but violates the tenant's minimum notice or advance booking horizon.</summary>
    OutsideBookingWindow,

    /// <summary>Another appointment, including its buffer, already occupies the slot for this staff member.</summary>
    StaffBusy,

    /// <summary>Not enough resources of a required group are free for the slot.</summary>
    ResourceUnavailable
}

/// <summary>
/// Why a slot was refused, and — when it was not — the resources the guard allocated for it. The caller
/// must persist <see cref="ResourceIds"/> rather than picking resources itself: picking outside the guard's
/// lock is how two concurrent bookings end up with the same room.
/// </summary>
public readonly record struct SlotCheckResult(SlotUnavailableReason? Reason, IReadOnlyList<Guid> ResourceIds)
{
    public bool IsAvailable => Reason is null;

    public string Message => Reason switch
    {
        SlotUnavailableReason.Closed => "This location or service is closed for booking on the selected date.",
        SlotUnavailableReason.StaffNotAvailable => "The selected staff member is not available at that time.",
        SlotUnavailableReason.OutsideBookingWindow => "The selected time is outside the allowed booking window for this tenant.",
        SlotUnavailableReason.StaffBusy => "That time slot is already booked for the selected staff member.",
        SlotUnavailableReason.ResourceUnavailable => "The resources required by this service are not available for that time slot.",
        _ => "The selected slot is not available."
    };
}

public record SlotCandidate(DateTime StartAtUtc, DateTime EndAtUtc);

public interface IAvailabilityService
{
    Task<DayAvailabilityDto> GetAvailabilityAsync(
        GetAvailabilityQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Final authorization check before an appointment is written. Enforces the same calendar rules as
    /// GetAvailabilityAsync, so any slot the preview offers here is accepted (modulo concurrency).
    /// </summary>
    Task<SlotCheckResult> CheckSlotAsync(
        Guid tenantId,
        Guid locationId,
        Guid serviceId,
        Guid staffId,
        DateTime startAtUtc,
        DateTime endAtUtc,
        Guid? excludeAppointmentId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Bulk form of CheckSlotAsync for one staff member: the calendar loads once for the whole span, so a
    /// recurring series costs a single round of queries instead of one per occurrence. Results line up with
    /// <paramref name="candidates"/> by index. Candidates are judged in time order and each accepted one
    /// holds its time and its rooms for the rest of the batch, so the whole batch can be written in one
    /// transaction; only the slot that comes first in time wins an overlap.
    /// </summary>
    Task<IReadOnlyList<SlotCheckResult>> CheckSlotsAsync(
        Guid tenantId,
        Guid locationId,
        Guid serviceId,
        Guid staffId,
        IReadOnlyList<SlotCandidate> candidates,
        Guid? excludeAppointmentId = null,
        CancellationToken cancellationToken = default);
}
