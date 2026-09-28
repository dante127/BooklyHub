using BooklyHub.Application.Appointments.Dtos;
using BooklyHub.Application.Common.Interfaces;
using BooklyHub.Application.Scheduling;
using BooklyHub.Domain.Entities.Resources;
using BooklyHub.Domain.Exceptions;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BooklyHub.Application.Appointments.Commands;

public record RescheduleAppointmentCommand(
    Guid TenantId,
    Guid AppointmentId,
    DateTime NewStartAtUtc,
    string? Reason = null) : IRequest<AppointmentDto>;

public class RescheduleAppointmentCommandValidator : AbstractValidator<RescheduleAppointmentCommand>
{
    public RescheduleAppointmentCommandValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.AppointmentId).NotEmpty();
        RuleFor(x => x.NewStartAtUtc)
            .NotEmpty()
            .Must(d => d > DateTime.UtcNow)
            .WithMessage("Rescheduled time must be in the future.");
    }
}

public class RescheduleAppointmentCommandHandler : IRequestHandler<RescheduleAppointmentCommand, AppointmentDto>
{
    private readonly IApplicationDbContext _db;
    private readonly IAvailabilityService _availabilityService;
    private readonly ICurrentUser _currentUser;
    private readonly IClock _clock;

    public RescheduleAppointmentCommandHandler(
        IApplicationDbContext db,
        IAvailabilityService availabilityService,
        ICurrentUser currentUser,
        IClock clock)
    {
        _db = db;
        _availabilityService = availabilityService;
        _currentUser = currentUser;
        _clock = clock;
    }

    public async Task<AppointmentDto> Handle(RescheduleAppointmentCommand request, CancellationToken cancellationToken)
    {
        var tenant = await _db.Tenants
            .AsNoTracking()
            .Include(t => t.Settings)
            .FirstOrDefaultAsync(t => t.Id == request.TenantId, cancellationToken)
            ?? throw new NotFoundException($"Tenant with ID {request.TenantId} was not found.");

        var appointment = await _db.Appointments
            .Include(a => a.Service)
            .Include(a => a.Staff)
            .Include(a => a.Customer)
            .Include(a => a.AppointmentResources)
            .FirstOrDefaultAsync(a => a.Id == request.AppointmentId && a.TenantId == request.TenantId, cancellationToken)
            ?? throw new NotFoundException($"Appointment with ID {request.AppointmentId} was not found.");

        // The tenant's rescheduling cutoff, from the same policy object that guards cancellation, and with
        // the same override roles: a manager trusted to cancel late is trusted to move an appointment late.
        AppointmentCutoffPolicy.EnsureReschedulable(tenant.Settings, appointment.StartAtUtc, _clock.UtcNow, _currentUser);

        var newEndAtUtc = request.NewStartAtUtc.AddMinutes(appointment.DurationMinutes);

        // Atomic transaction: verify new slot and reserve within transaction
        await _db.ExecuteInTransactionAsync(async () =>
        {
            // Lock order is location then staff, everywhere a booking is written.
            await _db.AcquireLocationBookingLockAsync(request.TenantId, appointment.LocationId, cancellationToken);
            await _db.AcquireStaffLockAsync(appointment.StaffId, request.TenantId, cancellationToken);

            var slotCheck = await _availabilityService.CheckSlotAsync(
                request.TenantId,
                appointment.LocationId,
                appointment.ServiceId,
                appointment.StaffId,
                request.NewStartAtUtc,
                newEndAtUtc,
                excludeAppointmentId: appointment.Id,
                cancellationToken);

            if (!slotCheck.IsAvailable)
            {
                throw new BookingConflictException($"The selected new slot is not available: {slotCheck.Message}");
            }

            // Apply domain rescheduling (keeps original slot intact until committed). The returned history
            // row must be registered as new, not discovered through the navigation (see the domain method).
            var history = appointment.Reschedule(request.NewStartAtUtc, newEndAtUtc, request.Reason, _currentUser.UserId?.ToString());
            _db.AppointmentStatusHistories.Add(history);

            // The guard allocated rooms for the new time while holding the location lock. Keeping the old
            // rows instead would leave the appointment on a resource someone else took in the meantime.
            // Rows still allocated stay; the rest leave and join through the set, because children that
            // reach the tracker only through the collection are treated as rows that already exist.
            var selectedResourceIds = slotCheck.ResourceIds.ToHashSet();
            var staleResources = appointment.AppointmentResources
                .Where(ar => !selectedResourceIds.Contains(ar.ResourceId))
                .ToList();
            foreach (var stale in staleResources)
            {
                appointment.AppointmentResources.Remove(stale);
                _db.AppointmentResources.Remove(stale);
            }

            var keptResourceIds = appointment.AppointmentResources.Select(ar => ar.ResourceId).ToHashSet();
            foreach (var resourceId in selectedResourceIds)
            {
                if (keptResourceIds.Contains(resourceId))
                {
                    continue;
                }

                _db.AppointmentResources.Add(new AppointmentResource
                {
                    TenantId = request.TenantId,
                    AppointmentId = appointment.Id,
                    ResourceId = resourceId
                });
            }

            await _db.SaveChangesAsync(cancellationToken);
            return true;
        }, cancellationToken);

        var locationName = await _db.Locations
            .AsNoTracking()
            .Where(l => l.Id == appointment.LocationId)
            .Select(l => l.Name)
            .FirstOrDefaultAsync(cancellationToken) ?? "";

        return new AppointmentDto(
            appointment.Id,
            appointment.TenantId,
            appointment.LocationId,
            locationName,
            appointment.ServiceId,
            appointment.Service?.Name ?? "",
            appointment.StaffId,
            appointment.Staff?.FullName ?? "",
            appointment.CustomerId,
            appointment.Customer?.FullName ?? "",
            appointment.StartAtUtc,
            appointment.EndAtUtc,
            appointment.DurationMinutes,
            appointment.Price,
            appointment.Currency,
            appointment.Status,
            appointment.Notes,
            appointment.CancellationReason,
            appointment.AppointmentResources.Select(ar => ar.ResourceId).ToList(),
            appointment.CreatedAtUtc);
    }
}
