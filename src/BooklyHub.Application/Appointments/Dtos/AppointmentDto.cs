using BooklyHub.Domain.Enums;

namespace BooklyHub.Application.Appointments.Dtos;

public record AppointmentDto(
    Guid Id,
    Guid TenantId,
    Guid LocationId,
    string LocationName,
    Guid ServiceId,
    string ServiceName,
    Guid StaffId,
    string StaffName,
    Guid CustomerId,
    string CustomerName,
    DateTime StartAtUtc,
    DateTime EndAtUtc,
    int DurationMinutes,
    decimal Price,
    string Currency,
    AppointmentStatus Status,
    string? Notes,
    string? CancellationReason,
    IReadOnlyList<Guid> AllocatedResourceIds,
    DateTime CreatedAtUtc);
