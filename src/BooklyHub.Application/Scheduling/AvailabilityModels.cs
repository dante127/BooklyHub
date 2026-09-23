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

public interface IAvailabilityService
{
    Task<DayAvailabilityDto> GetAvailabilityAsync(
        GetAvailabilityQuery query,
        CancellationToken cancellationToken = default);

    Task<bool> IsSlotAvailableAsync(
        Guid tenantId,
        Guid locationId,
        Guid serviceId,
        Guid staffId,
        DateTime startAtUtc,
        DateTime endAtUtc,
        Guid? excludeAppointmentId = null,
        CancellationToken cancellationToken = default);
}
