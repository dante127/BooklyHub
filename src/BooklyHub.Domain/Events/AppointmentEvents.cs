using BooklyHub.Domain.Common;
using BooklyHub.Domain.Enums;

namespace BooklyHub.Domain.Events;

public record AppointmentCreatedEvent(
    Guid TenantId,
    Guid AppointmentId,
    Guid CustomerId,
    Guid StaffId,
    Guid ServiceId,
    DateTime StartAtUtc,
    DateTime EndAtUtc) : IDomainEvent;

public record AppointmentConfirmedEvent(
    Guid TenantId,
    Guid AppointmentId,
    Guid CustomerId,
    DateTime StartAtUtc) : IDomainEvent;

public record AppointmentCancelledEvent(
    Guid TenantId,
    Guid AppointmentId,
    Guid CustomerId,
    string Reason) : IDomainEvent;

public record AppointmentRescheduledEvent(
    Guid TenantId,
    Guid AppointmentId,
    Guid CustomerId,
    DateTime OldStartAtUtc,
    DateTime NewStartAtUtc,
    DateTime NewEndAtUtc,
    string? Reason) : IDomainEvent;

public record AppointmentCompletedEvent(
    Guid TenantId,
    Guid AppointmentId,
    Guid CustomerId,
    Guid StaffId,
    Guid ServiceId) : IDomainEvent;
