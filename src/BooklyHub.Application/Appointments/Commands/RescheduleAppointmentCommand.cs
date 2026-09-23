using BooklyHub.Application.Appointments.Dtos;
using BooklyHub.Application.Common.Interfaces;
using BooklyHub.Application.Scheduling;
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

        // Rescheduling cutoff check
        var cutoffHours = tenant.Settings?.ReschedulingCutoffHours ?? 12;
        if (appointment.StartAtUtc - _clock.UtcNow < TimeSpan.FromHours(cutoffHours))
        {
            throw new BusinessRuleValidationException("RescheduleCutoffExceeded", $"Appointments cannot be rescheduled within {cutoffHours} hours of the start time.");
        }

        var newEndAtUtc = request.NewStartAtUtc.AddMinutes(appointment.DurationMinutes);

        // Atomic transaction: verify new slot and reserve within transaction
        await _db.ExecuteInTransactionAsync(async () =>
        {
            await _db.AcquireStaffLockAsync(appointment.StaffId, request.TenantId, cancellationToken);

            var isAvailable = await _availabilityService.IsSlotAvailableAsync(
                request.TenantId,
                appointment.LocationId,
                appointment.ServiceId,
                appointment.StaffId,
                request.NewStartAtUtc,
                newEndAtUtc,
                excludeAppointmentId: appointment.Id,
                cancellationToken);

            if (!isAvailable)
            {
                throw new BookingConflictException("The selected new slot is not available for rescheduling.");
            }

            // Apply domain rescheduling (keeps original slot intact until committed)
            appointment.Reschedule(request.NewStartAtUtc, newEndAtUtc, request.Reason, _currentUser.UserId?.ToString());

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
