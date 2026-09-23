using BooklyHub.Application.Appointments.Dtos;
using BooklyHub.Application.Common.Interfaces;
using BooklyHub.Application.Scheduling;
using BooklyHub.Domain.Entities.Appointments;
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

        var service = await _db.Services.AsNoTracking().FirstOrDefaultAsync(s => s.Id == request.ServiceId && s.TenantId == request.TenantId, cancellationToken)
            ?? throw new NotFoundException($"Service with ID {request.ServiceId} was not found.");

        var staff = await _db.StaffMembers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == request.StaffId && s.TenantId == request.TenantId, cancellationToken)
            ?? throw new NotFoundException($"Staff member with ID {request.StaffId} was not found.");

        var customer = await _db.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == request.CustomerId && c.TenantId == request.TenantId, cancellationToken)
            ?? throw new NotFoundException($"Customer with ID {request.CustomerId} was not found.");

        // Generate target dates
        var targetDates = GenerateDates(request.Pattern, request.Interval, request.StartDate, request.EndDate, request.MaxOccurrences);

        if (targetDates.Count == 0)
        {
            throw new BusinessRuleValidationException("NoDatesGenerated", "Recurrence rules produced no valid appointment dates.");
        }

        var recurringApptId = Guid.NewGuid();
        var (createdAppointments, skippedCount) = await _db.ExecuteInTransactionAsync(async () =>
        {
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

            await _db.AcquireStaffLockAsync(request.StaffId, request.TenantId, cancellationToken);

            var created = new List<Appointment>();
            var skipped = 0;

            foreach (var date in targetDates)
            {
                var localDateTime = date.ToDateTime(request.StartTimeOfDay);
                var tz = Common.Helpers.TimeZoneHelper.ResolveTimeZone(location.TimeZoneId);
                var startAtUtc = Common.Helpers.TimeZoneHelper.ToUtc(localDateTime, tz);
                var endAtUtc = startAtUtc.AddMinutes(service.DurationMinutes);

                var isAvailable = await _availabilityService.IsSlotAvailableAsync(
                    request.TenantId,
                    request.LocationId,
                    request.ServiceId,
                    request.StaffId,
                    startAtUtc,
                    endAtUtc,
                    excludeAppointmentId: null,
                    cancellationToken);

                if (!isAvailable)
                {
                    if (request.ConflictPolicy == RecurrenceConflictPolicy.AbortSeries)
                    {
                        throw new BookingConflictException($"Conflict detected on {date:yyyy-MM-dd}. Recurring series was aborted.");
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
                    service.DurationMinutes,
                    service.Price,
                    service.Currency,
                    request.Notes,
                    recurringAppointmentId: recurringApptId,
                    createdBy: _currentUser.UserId?.ToString() ?? "System");

                appt.TransitionTo(AppointmentStatus.Confirmed, "Confirmed recurring appointment", _currentUser.UserId?.ToString());

                created.Add(appt);
                _db.Appointments.Add(appt);
            }

            recurringAppt.CreatedAppointmentsCount = created.Count;
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
            [],
            a.CreatedAtUtc)).ToList();

        return new RecurringAppointmentResultDto(
            recurringApptId,
            targetDates.Count,
            createdAppointments.Count,
            skippedCount,
            dtos);
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
