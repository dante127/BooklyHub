using BooklyHub.Application.Appointments.Dtos;
using BooklyHub.Application.Common.Interfaces;
using BooklyHub.Application.Payments.Commands;
using BooklyHub.Application.Scheduling;
using BooklyHub.Domain.Entities.Appointments;
using BooklyHub.Domain.Entities.Resources;
using BooklyHub.Domain.Enums;
using BooklyHub.Domain.Exceptions;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BooklyHub.Application.Appointments.Commands;

public record BookAppointmentCommand(
    Guid TenantId,
    Guid LocationId,
    Guid ServiceId,
    Guid StaffId,
    Guid CustomerId,
    DateTime StartAtUtc,
    string? Notes = null,
    string? IdempotencyKey = null) : IRequest<AppointmentDto>;

public class BookAppointmentCommandValidator : AbstractValidator<BookAppointmentCommand>
{
    // The clock is read inside the rule, not captured at construction, so a singleton validator cannot freeze the floor.
    public BookAppointmentCommandValidator(IClock clock)
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.LocationId).NotEmpty();
        RuleFor(x => x.ServiceId).NotEmpty();
        RuleFor(x => x.StaffId).NotEmpty();
        RuleFor(x => x.CustomerId).NotEmpty();
        RuleFor(x => x.StartAtUtc)
            .NotEmpty()
            .Must(d => d > clock.UtcNow.AddMinutes(5))
            .WithMessage("Appointment start time must be in the future.");
    }
}

public class BookAppointmentCommandHandler : IRequestHandler<BookAppointmentCommand, AppointmentDto>
{
    private readonly IApplicationDbContext _db;
    private readonly IAvailabilityService _availabilityService;
    private readonly IClock _clock;
    private readonly ICurrentUser _currentUser;

    public BookAppointmentCommandHandler(
        IApplicationDbContext db,
        IAvailabilityService availabilityService,
        IClock clock,
        ICurrentUser currentUser)
    {
        _db = db;
        _availabilityService = availabilityService;
        _clock = clock;
        _currentUser = currentUser;
    }

    public async Task<AppointmentDto> Handle(BookAppointmentCommand request, CancellationToken cancellationToken)
    {
        // 1. Load tenant and settings
        var tenant = await _db.Tenants
            .AsNoTracking()
            .Include(t => t.Settings)
            .FirstOrDefaultAsync(t => t.Id == request.TenantId, cancellationToken)
            ?? throw new NotFoundException($"Tenant with ID {request.TenantId} was not found.");

        // 2. Load service
        var service = await _db.Services
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == request.ServiceId && s.TenantId == request.TenantId, cancellationToken)
            ?? throw new NotFoundException($"Service with ID {request.ServiceId} was not found.");

        if (!service.IsActive)
        {
            throw new BusinessRuleValidationException("ServiceInactive", "The requested service is not currently active.");
        }

        // 3. Load staff & custom overrides
        var staff = await _db.StaffMembers
            .AsNoTracking()
            .Include(s => s.StaffServices)
            .FirstOrDefaultAsync(s => s.Id == request.StaffId && s.TenantId == request.TenantId && s.LocationId == request.LocationId, cancellationToken)
            ?? throw new NotFoundException($"Staff member with ID {request.StaffId} was not found at this location.");

        if (!staff.IsActive)
        {
            throw new BusinessRuleValidationException("StaffInactive", "The requested staff member is not currently active.");
        }

        var staffService = staff.StaffServices.FirstOrDefault(ss => ss.ServiceId == request.ServiceId);
        if (staffService == null)
        {
            throw new BusinessRuleValidationException("StaffServiceMismatch", "This staff member does not provide the selected service.");
        }

        // 4. Calculate duration and end time
        var durationMinutes = staffService.CustomDurationMinutes ?? service.DurationMinutes;
        var price = staffService.CustomPrice ?? service.Price;
        var endAtUtc = request.StartAtUtc.AddMinutes(durationMinutes);

        // 5. Validate customer
        var customer = await _db.Customers
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == request.CustomerId && c.TenantId == request.TenantId, cancellationToken)
            ?? throw new NotFoundException($"Customer with ID {request.CustomerId} was not found.");

        if (customer.IsBlocked)
        {
            throw new BusinessRuleValidationException("CustomerBlocked", "This customer account has been blocked from booking appointments.");
        }

        // 6. Booking rules: notice & advance horizon
        var minNoticeMinutes = tenant.Settings?.MinBookingNoticeMinutes ?? 120;
        var maxAdvanceDays = tenant.Settings?.MaxAdvanceBookingDays ?? 60;

        if (request.StartAtUtc < _clock.UtcNow.AddMinutes(minNoticeMinutes))
        {
            throw new BusinessRuleValidationException("MinimumNoticeViolation", $"Appointments must be booked at least {minNoticeMinutes} minutes in advance.");
        }

        if (request.StartAtUtc > _clock.UtcNow.AddDays(maxAdvanceDays))
        {
            throw new BusinessRuleValidationException("MaxAdvanceViolation", $"Appointments cannot be booked more than {maxAdvanceDays} days in advance.");
        }

        // 7. Atomic transaction with concurrency check
        return await _db.ExecuteInTransactionAsync(async () =>
        {
            // Lock order is location then staff, everywhere a booking is written.
            await _db.AcquireLocationBookingLockAsync(request.TenantId, request.LocationId, cancellationToken);
            await _db.AcquireStaffLockAsync(request.StaffId, request.TenantId, cancellationToken);

            if (request.IdempotencyKey is { Length: > 0 } idempotencyKey)
            {
                // One key buys one booking. A retry that arrives while the first request is still in
                // flight waits on the locks above and finds the committed row here, so the client gets its
                // booking back instead of a conflict for a slot it already owns.
                var keyed = await _db.Appointments
                    .AsNoTracking()
                    .FirstOrDefaultAsync(a => a.TenantId == request.TenantId && a.IdempotencyKey == idempotencyKey, cancellationToken);

                if (keyed != null)
                {
                    if (keyed.LocationId != request.LocationId ||
                        keyed.ServiceId != request.ServiceId ||
                        keyed.StaffId != request.StaffId ||
                        keyed.CustomerId != request.CustomerId ||
                        keyed.StartAtUtc != request.StartAtUtc)
                    {
                        throw new BusinessRuleValidationException(
                            "IdempotencyKeyReused",
                            "This idempotency key already booked a different appointment.");
                    }

                    var keyedResourceIds = await _db.AppointmentResources
                        .AsNoTracking()
                        .Where(ar => ar.AppointmentId == keyed.Id)
                        .Select(ar => ar.ResourceId)
                        .ToListAsync(cancellationToken);

                    var keyedLocationName = await _db.Locations
                        .AsNoTracking()
                        .Where(l => l.Id == keyed.LocationId)
                        .Select(l => l.Name)
                        .FirstOrDefaultAsync(cancellationToken) ?? "";

                    return new AppointmentDto(
                        keyed.Id,
                        keyed.TenantId,
                        keyed.LocationId,
                        keyedLocationName,
                        keyed.ServiceId,
                        service.Name,
                        keyed.StaffId,
                        staff.FullName,
                        keyed.CustomerId,
                        customer.FullName,
                        keyed.StartAtUtc,
                        keyed.EndAtUtc,
                        keyed.DurationMinutes,
                        keyed.Price,
                        keyed.Currency,
                        keyed.Status,
                        keyed.Notes,
                        keyed.CancellationReason,
                        keyedResourceIds,
                        keyed.CreatedAtUtc);
                }
            }

            // Concurrency Guard: re-check availability inside transaction
            var slotCheck = await _availabilityService.CheckSlotAsync(
                request.TenantId,
                request.LocationId,
                request.ServiceId,
                request.StaffId,
                request.StartAtUtc,
                endAtUtc,
                excludeAppointmentId: null,
                cancellationToken);

            if (!slotCheck.IsAvailable)
            {
                throw new BookingConflictException(slotCheck.Message);
            }

            // The guard picked the resources while holding the location lock; persisting its choice is
            // what keeps two concurrent bookings from taking the same room.
            var allocatedResourceIds = slotCheck.ResourceIds;

            // Create appointment
            var appointment = Appointment.Create(
                request.TenantId,
                request.LocationId,
                request.ServiceId,
                request.StaffId,
                request.CustomerId,
                request.StartAtUtc,
                endAtUtc,
                durationMinutes,
                price,
                service.Currency,
                request.Notes,
                recurringAppointmentId: null,
                createdBy: _currentUser.UserId?.ToString() ?? "System");
            appointment.IdempotencyKey = request.IdempotencyKey;

            // Automatically confirm if upfront payment is not required
            if (tenant.Settings?.RequireUpfrontPayment != true)
            {
                _db.AppointmentStatusHistories.Add(
                    appointment.TransitionTo(AppointmentStatus.Confirmed, _clock.UtcNow, "Auto-confirmed on booking", _currentUser.UserId?.ToString()));
            }

            foreach (var resourceId in allocatedResourceIds)
            {
                appointment.AppointmentResources.Add(new AppointmentResource
                {
                    TenantId = request.TenantId,
                    AppointmentId = appointment.Id,
                    ResourceId = resourceId
                });
            }

            _db.Appointments.Add(appointment);

            // Two bookings for one customer at two locations hold no common lock, so the counter moves
            // in SQL rather than on a loaded entity.
            await _db.AdjustTotalBookingsAsync(request.TenantId, request.CustomerId, 1, cancellationToken);

            await _db.SaveChangesAsync(cancellationToken);

            var locationName = (await _db.Locations.AsNoTracking().Where(l => l.Id == appointment.LocationId).Select(l => l.Name).FirstOrDefaultAsync(cancellationToken)) ?? "";

            return new AppointmentDto(
                appointment.Id,
                appointment.TenantId,
                appointment.LocationId,
                locationName,
                appointment.ServiceId,
                service.Name,
                appointment.StaffId,
                staff.FullName,
                appointment.CustomerId,
                customer.FullName,
                appointment.StartAtUtc,
                appointment.EndAtUtc,
                appointment.DurationMinutes,
                appointment.Price,
                appointment.Currency,
                appointment.Status,
                appointment.Notes,
                appointment.CancellationReason,
                allocatedResourceIds,
                appointment.CreatedAtUtc);
        }, cancellationToken);
    }
}
