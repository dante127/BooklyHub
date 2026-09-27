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
    IReadOnlyList<AvailableSlotDto> Slots);

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

public readonly record struct SlotCheckResult(SlotUnavailableReason? Reason)
{
    public bool IsAvailable => Reason is null;

    public static readonly SlotCheckResult Available = new(null);

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
    /// <paramref name="candidates"/> by index and match what CheckSlotAsync returns for each slot alone.
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
