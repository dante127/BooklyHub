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

public record CreateRecurringAppointmentCommand(
    Guid TenantId,
    Guid LocationId,
    Guid ServiceId,
    Guid StaffId,
    Guid CustomerId,
    TimeOnly StartTimeOfDay,
    RecurrencePattern Pattern,
    int Interval,
    DateOnly StartDate,
    DateOnly? EndDate,
    int? MaxOccurrences,
    RecurrenceConflictPolicy ConflictPolicy = RecurrenceConflictPolicy.SkipConflicts,
    string? Notes = null) : IRequest<RecurringAppointmentResultDto>;

public record RecurringAppointmentResultDto(
    Guid RecurringAppointmentId,
    int TotalOccurrencesRequested,
    int BookedCount,
    int SkippedCount,
    IReadOnlyList<AppointmentDto> CreatedAppointments);

public class CreateRecurringAppointmentCommandValidator : AbstractValidator<CreateRecurringAppointmentCommand>
{
    public CreateRecurringAppointmentCommandValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.LocationId).NotEmpty();
        RuleFor(x => x.ServiceId).NotEmpty();
        RuleFor(x => x.StaffId).NotEmpty();
        RuleFor(x => x.CustomerId).NotEmpty();
        RuleFor(x => x.Interval).GreaterThanOrEqualTo(1);
        RuleFor(x => x.StartDate).NotEmpty();
    }
}

public class CreateRecurringAppointmentCommandHandler : IRequestHandler<CreateRecurringAppointmentCommand, RecurringAppointmentResultDto>
{
    private readonly IApplicationDbContext _db;
    private readonly IAvailabilityService _availabilityService;
    private readonly ICurrentUser _currentUser;

    public CreateRecurringAppointmentCommandHandler(
        IApplicationDbContext db,
        IAvailabilityService availabilityService,
        ICurrentUser currentUser)
    {
        _db = db;
        _availabilityService = availabilityService;
        _currentUser = currentUser;
    }

    public async Task<RecurringAppointmentResultDto> Handle(CreateRecurringAppointmentCommand request, CancellationToken cancellationToken)
    {
        var tenant = await _db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == request.TenantId, cancellationToken)
            ?? throw new NotFoundException($"Tenant with ID {request.TenantId} was not found.");

        var location = await _db.Locations.AsNoTracking().FirstOrDefaultAsync(l => l.Id == request.LocationId && l.TenantId == request.TenantId, cancellationToken)
            ?? throw new NotFoundException($"Location with ID {request.LocationId} was not found.");

        if (!location.IsActive)
        {
            throw new BusinessRuleValidationException("LocationInactive", "This location is not currently open for booking.");
        }

        var service = await _db.Services.AsNoTracking().FirstOrDefaultAsync(s => s.Id == request.ServiceId && s.TenantId == request.TenantId, cancellationToken)
            ?? throw new NotFoundException($"Service with ID {request.ServiceId} was not found.");

        if (!service.IsActive)
        {
            throw new BusinessRuleValidationException("ServiceInactive", "The requested service is not currently active.");
        }

        var staff = await _db.StaffMembers
            .AsNoTracking()
            .Include(s => s.StaffServices)
            .FirstOrDefaultAsync(s => s.Id == request.StaffId && s.TenantId == request.TenantId && s.LocationId == request.LocationId, cancellationToken)
            ?? throw new NotFoundException($"Staff member with ID {request.StaffId} was not found at this location.");

        if (!staff.IsActive)
        {
            throw new BusinessRuleValidationException("StaffInactive", "The requested staff member is not currently active.");
        }

        var customer = await _db.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == request.CustomerId && c.TenantId == request.TenantId, cancellationToken)
            ?? throw new NotFoundException($"Customer with ID {request.CustomerId} was not found.");

        if (customer.IsBlocked)
        {
            throw new BusinessRuleValidationException("CustomerBlocked", "This customer account has been blocked from booking appointments.");
        }

        // Generate target dates
        var targetDates = GenerateDates(request.Pattern, request.Interval, request.StartDate, request.EndDate, request.MaxOccurrences);

        if (targetDates.Count == 0)
        {
            throw new BusinessRuleValidationException("NoDatesGenerated", "Recurrence rules produced no valid appointment dates.");
        }

        var staffService = staff.StaffServices.FirstOrDefault(ss => ss.ServiceId == request.ServiceId)
            ?? throw new BusinessRuleValidationException("StaffServiceMismatch", "This staff member does not provide the selected service.");

        var durationMinutes = staffService.CustomDurationMinutes ?? service.DurationMinutes;
        var price = staffService.CustomPrice ?? service.Price;
        var timeZone = Common.Helpers.TimeZoneHelper.ResolveTimeZone(location.TimeZoneId);

        var candidates = targetDates
            .Select(date =>
            {
                var startAtUtc = Common.Helpers.TimeZoneHelper.ToUtc(date.ToDateTime(request.StartTimeOfDay), timeZone);
                return new SlotCandidate(startAtUtc, startAtUtc.AddMinutes(durationMinutes));
            })
            .ToList();

        ValidateOccurrences(targetDates, request.StartTimeOfDay, durationMinutes);

        var recurringApptId = Guid.NewGuid();

        var (createdAppointments, skippedCount) = await _db.ExecuteInTransactionAsync(async () =>
        {
            // Lock order is location then staff, everywhere a booking is written.
            await _db.AcquireLocationBookingLockAsync(request.TenantId, request.LocationId, cancellationToken);
            await _db.AcquireStaffLockAsync(request.StaffId, request.TenantId, cancellationToken);

            var recurringAppt = new RecurringAppointment
            {
                Id = recurringApptId,
                TenantId = request.TenantId,
                Pattern = request.Pattern,
                Interval = request.Interval,
                StartDate = request.StartDate,
                EndDate = request.EndDate,
                NumberOfOccurrences = targetDates.Count,
                CreatedBy = _currentUser.UserId?.ToString() ?? "System"
            };

            _db.RecurringAppointments.Add(recurringAppt);

            // Every occurrence is judged in one pass: the guard loads the calendar once for the whole span.
            var slotChecks = await _availabilityService.CheckSlotsAsync(
                request.TenantId,
                request.LocationId,
                request.ServiceId,
                request.StaffId,
                candidates,
                excludeAppointmentId: null,
                cancellationToken);

            var created = new List<Appointment>();
            var skipped = 0;

            for (var i = 0; i < candidates.Count; i++)
            {
                var date = targetDates[i];
                var (startAtUtc, endAtUtc) = candidates[i];
                var slotCheck = slotChecks[i];

                if (!slotCheck.IsAvailable)
                {
                    if (request.ConflictPolicy == RecurrenceConflictPolicy.AbortSeries)
                    {
                        throw new BookingConflictException($"Conflict detected on {date:yyyy-MM-dd}: {slotCheck.Message} Recurring series was aborted.");
                    }

                    skipped++;
                    continue;
                }

                var appt = Appointment.Create(
                    request.TenantId,
                    request.LocationId,
                    request.ServiceId,
                    request.StaffId,
                    request.CustomerId,
                    startAtUtc,
                    endAtUtc,
                    durationMinutes,
                    price,
                    service.Currency,
                    request.Notes,
                    recurringAppointmentId: recurringApptId,
                    createdBy: _currentUser.UserId?.ToString() ?? "System");

                _db.AppointmentStatusHistories.Add(
                    appt.TransitionTo(AppointmentStatus.Confirmed, "Confirmed recurring appointment", _currentUser.UserId?.ToString()));

                foreach (var resourceId in slotCheck.ResourceIds)
                {
                    appt.AppointmentResources.Add(new AppointmentResource
                    {
                        TenantId = request.TenantId,
                        AppointmentId = appt.Id,
                        ResourceId = resourceId
                    });
                }

                created.Add(appt);
                _db.Appointments.Add(appt);
            }

            recurringAppt.CreatedAppointmentsCount = created.Count;

            // Every created occurrence is a booking, so the counter moves by the created count in the
            // same transaction, in SQL — a second booking flow touching this customer concurrently must
            // not lose either delta.
            if (created.Count > 0)
            {
                await _db.AdjustTotalBookingsAsync(request.TenantId, request.CustomerId, created.Count, cancellationToken);
            }

            await _db.SaveChangesAsync(cancellationToken);

            return (created, skipped);
        }, cancellationToken);

        var dtos = createdAppointments.Select(a => new AppointmentDto(
            a.Id,
            a.TenantId,
            a.LocationId,
            location.Name,
            a.ServiceId,
            service.Name,
            a.StaffId,
            staff.FullName,
            a.CustomerId,
            customer.FullName,
            a.StartAtUtc,
            a.EndAtUtc,
            a.DurationMinutes,
            a.Price,
            a.Currency,
            a.Status,
            a.Notes,
            a.CancellationReason,
            a.AppointmentResources.Select(ar => ar.ResourceId).ToList(),
            a.CreatedAtUtc)).ToList();

        return new RecurringAppointmentResultDto(
            recurringApptId,
            targetDates.Count,
            createdAppointments.Count,
            skippedCount,
            dtos);
    }

    /// <summary>
    /// The guard reports an occupied slot as a conflict, so occurrences that overlap each other would be
    /// silently skipped under SkipConflicts or blamed on an existing appointment under AbortSeries. The
    /// pattern itself is what is unschedulable, so it is rejected here with the dates that collide.
    /// </summary>
    private static void ValidateOccurrences(
        List<DateOnly> dates,
        TimeOnly timeOfDay,
        int durationMinutes)
    {
        var spans = dates
            .Select(date => (date.ToDateTime(timeOfDay), date.ToDateTime(timeOfDay).AddMinutes(durationMinutes)))
            .ToList();

        var colliding = new List<string>();

        for (var i = 1; i < spans.Count; i++)
        {
            if (spans[i - 1].Item2 > spans[i].Item1)
            {
                colliding.Add($"{dates[i - 1]:yyyy-MM-dd} and {dates[i]:yyyy-MM-dd}");
            }
        }

        if (colliding.Count > 0)
        {
            throw new BusinessRuleValidationException(
                "RecurrenceOccurrencesOverlap",
                $"This recurrence places appointments on top of each other because the service duration " +
                $"({durationMinutes} minutes) is longer than the gap between occurrences: {string.Join("; ", colliding)}.");
        }
    }

    public static List<DateOnly> GenerateDates(
        RecurrencePattern pattern,
        int interval,
        DateOnly startDate,
        DateOnly? endDate,
        int? maxOccurrences)
    {
        var dates = new List<DateOnly>();
        var current = startDate;
        var limit = maxOccurrences ?? 12; // safety cap default 12 if no end date
        if (limit > 52) limit = 52; // max cap 1 year

        while (dates.Count < limit)
        {
            if (endDate.HasValue && current > endDate.Value)
            {
                break;
            }

            dates.Add(current);

            current = pattern switch
            {
                RecurrencePattern.Daily => current.AddDays(interval),
                RecurrencePattern.Weekly => current.AddDays(7 * interval),
                RecurrencePattern.Biweekly => current.AddDays(14 * interval),
                RecurrencePattern.Monthly => current.AddMonths(interval),
                _ => current.AddDays(7)
            };
        }

        return dates;
    }
}
