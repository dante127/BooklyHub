using BooklyHub.Application.Common.Interfaces;
using BooklyHub.Domain.Entities.Organizations;
using BooklyHub.Domain.Entities.Scheduling;
using BooklyHub.Domain.Exceptions;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BooklyHub.Application.Scheduling.Commands;

/// <summary>
/// CAL-01: the three calendar tables the booking guard reads and nothing in this repository wrote.
///
/// <c>AvailabilityService.LoadCalendarAsync</c> decides a day from <c>Holidays</c> (L476), <c>BusinessHours</c> (L517)
/// and <c>AvailabilityExceptions</c> (L525) before it ever looks at an appointment, and until these commands existed
/// the only way to put a row in any of them was an <c>INSERT</c> in the database. So the guard's close-check was dead
/// code on every deployed calendar: <c>SlotUnavailableReason.Closed</c> had one producer — <c>closedDates</c>, built
/// from a table with no writer — and a clinic that needed to close Eid, or send one nurse on sick leave, could only do
/// it by hand, with no permission gate, no validation, and no way to know whether the slot the portal is offering is
/// the slot the clinic means to refuse.
///
/// The bounds below are this file's policy, chosen against what the guard's own reads can act on, and each one is a
/// measured fact in <c>CalendarWriteGuardTests</c> rather than a number that looked reasonable.
/// </summary>
public static class CalendarLimits
{
    /// <summary>How far ahead a dated or annually-recurring holiday may be anchored.</summary>
    public const int MaxHolidayHorizonDays = 3650;

    /// <summary>The longest single absence the guard will be asked to block. A year-long row would sit in every calendar load the tenant does.</summary>
    public const int MaxExceptionSpanDays = 92;
}

public record HolidayDto(Guid Id, Guid? LocationId, string Name, DateOnly Date, bool RecurringAnnually);

public record CreateHolidayCommand(
    Guid TenantId,
    Guid? LocationId,
    string Name,
    DateOnly Date,
    bool RecurringAnnually) : IRequest<HolidayDto>;

public class CreateHolidayCommandValidator : AbstractValidator<CreateHolidayCommand>
{
    // Read inside the rule, not captured at construction, so a singleton validator cannot freeze the floor.
    public CreateHolidayCommandValidator(IClock clock)
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("A holiday needs a name; the day is what closes, the name is what a reader recognizes.")
            .MaximumLength(150).WithMessage("Holiday name must be 150 characters or fewer.");

        RuleFor(x => x.Date)
            .Must(d => d <= DateOnly.FromDateTime(clock.UtcNow).AddDays(CalendarLimits.MaxHolidayHorizonDays))
            .WithMessage($"Holiday must be within {CalendarLimits.MaxHolidayHorizonDays} days from today.")
            // A dated holiday in the past is a row the guard will never meet: closedDates is built from the loaded
            // window, which is the requested day and its buffer neighbours. An annual one is the opposite case — the
            // anchor's month and day are what match, so the anchor itself is routinely a past date.
            .Must((command, date) => command.RecurringAnnually ||
                                     date >= DateOnly.FromDateTime(clock.UtcNow))
            .WithMessage("A non-recurring holiday cannot be dated in the past; it would close nothing.");
    }
}

public class CreateHolidayCommandHandler : IRequestHandler<CreateHolidayCommand, HolidayDto>
{
    private readonly IApplicationDbContext _db;

    public CreateHolidayCommandHandler(IApplicationDbContext db) => _db = db;

    public async Task<HolidayDto> Handle(CreateHolidayCommand request, CancellationToken cancellationToken)
    {
        if (request.LocationId is { } locationId &&
            !await _db.Locations.AnyAsync(l => l.Id == locationId && l.TenantId == request.TenantId, cancellationToken))
        {
            throw new NotFoundException($"Location with ID {locationId} was not found.");
        }

        // Same scope means the same day closed twice over: a tenant-wide row and a location row for one date are two
        // different statements, so only an identical scope collides.
        var already = await _db.Holidays
            .AsNoTracking()
            .AnyAsync(h => h.TenantId == request.TenantId &&
                           h.Date == request.Date &&
                           h.LocationId == request.LocationId,
                cancellationToken);

        if (already)
        {
            throw new BusinessRuleValidationException(
                "HolidayAlreadyOnThisDate",
                "This tenant already has a holiday on that date for the same location scope.");
        }

        var holiday = new Holiday
        {
            Id = Guid.NewGuid(),
            TenantId = request.TenantId,
            LocationId = request.LocationId,
            Name = request.Name,
            Date = request.Date,
            RecurringAnnually = request.RecurringAnnually
        };

        _db.Holidays.Add(holiday);
        await _db.SaveChangesAsync(cancellationToken);

        return ToDto(holiday);
    }

    internal static HolidayDto ToDto(Holiday h) => new(h.Id, h.LocationId, h.Name, h.Date, h.RecurringAnnually);
}

public record DeleteHolidayCommand(Guid TenantId, Guid Id) : IRequest<Unit>;

public class DeleteHolidayCommandHandler : IRequestHandler<DeleteHolidayCommand, Unit>
{
    private readonly IApplicationDbContext _db;

    public DeleteHolidayCommandHandler(IApplicationDbContext db) => _db = db;

    public async Task<Unit> Handle(DeleteHolidayCommand request, CancellationToken cancellationToken)
    {
        // The id alone is not a scope: another tenant's row is not found rather than forbidden, which is what every
        // other tenant-scoped write in this API answers.
        var holiday = await _db.Holidays
            .FirstOrDefaultAsync(h => h.Id == request.Id && h.TenantId == request.TenantId, cancellationToken)
            ?? throw new NotFoundException($"Holiday with ID {request.Id} was not found.");

        _db.Holidays.Remove(holiday);
        await _db.SaveChangesAsync(cancellationToken);

        return Unit.Value;
    }
}

public record BusinessHourDto(DayOfWeek DayOfWeek, TimeSpan OpenTime, TimeSpan CloseTime, bool IsClosed);

public record BusinessWeekDto(Guid LocationId, string TimeZoneId, IReadOnlyList<BusinessHourDto> Days);

public record BusinessHourInput(DayOfWeek DayOfWeek, TimeSpan OpenTime, TimeSpan CloseTime, bool IsClosed);

public record ReplaceBusinessHoursCommand(
    Guid TenantId,
    Guid LocationId,
    IReadOnlyList<BusinessHourInput> Days) : IRequest<BusinessWeekDto>;

public class ReplaceBusinessHoursCommandValidator : AbstractValidator<ReplaceBusinessHoursCommand>
{
    public ReplaceBusinessHoursCommandValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.LocationId).NotEmpty();
        RuleFor(x => x.Days)
            .NotEmpty().WithMessage("A business-week write has to name at least one day.")
            .Must(days => days.Count <= 7).WithMessage("A week has seven days, so a write names each of them at most once.")
            .Must(days => days.Select(d => d.DayOfWeek).Distinct().Count() == days.Count)
            .WithMessage("A day named twice would be two different answers to which one wins.");

        RuleForEach(x => x.Days).ChildRules(day =>
        {
            // A TimeSpan here is a wall-clock time inside the location's own day, and the guard turns it into a
            // TimeOnly — which folds 24:00 back to 00:00 and would silently delete the window it was given.
            day.RuleFor(d => d.OpenTime)
                .Must(t => t >= TimeSpan.Zero && t < TimeSpan.FromHours(24))
                .WithMessage("Opening time must fall inside one day.")
                .Must(t => t.Ticks % TimeSpan.TicksPerMinute == 0)
                .WithMessage("Opening time must be a whole minute.");
            day.RuleFor(d => d.CloseTime)
                .Must(t => t >= TimeSpan.Zero && t < TimeSpan.FromHours(24))
                .WithMessage("Closing time must fall inside one day, not wrap past midnight.")
                .Must(t => t.Ticks % TimeSpan.TicksPerMinute == 0)
                .WithMessage("Closing time must be a whole minute.");
            day.RuleFor(d => d.CloseTime)
                .Must((d, close) => d.IsClosed || close > d.OpenTime)
                .WithMessage("Closing time must be after opening time unless the day is closed.");
        });
    }
}

public class ReplaceBusinessHoursCommandHandler : IRequestHandler<ReplaceBusinessHoursCommand, BusinessWeekDto>
{
    private readonly IApplicationDbContext _db;

    public ReplaceBusinessHoursCommandHandler(IApplicationDbContext db) => _db = db;

    public async Task<BusinessWeekDto> Handle(ReplaceBusinessHoursCommand request, CancellationToken cancellationToken)
    {
        var timeZoneId = await _db.Locations
            .AsNoTracking()
            .Where(l => l.Id == request.LocationId && l.TenantId == request.TenantId)
            .Select(l => l.TimeZoneId)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException($"Location with ID {request.LocationId} was not found.");

        var existing = await _db.BusinessHours
            .Where(b => b.TenantId == request.TenantId &&
                        b.LocationId == request.LocationId &&
                        request.Days.Select(d => d.DayOfWeek).Contains(b.DayOfWeek))
            .ToListAsync(cancellationToken);

        // Days the write does not name are left exactly as they were. The alternative — a PUT that replaces the whole
        // week and so deletes any day it was not told about — would turn a client that sends Monday to Friday into a
        // clinic that closed its Saturday without saying so, and that is the one edit a calendar API has no business
        // making on behalf of the caller.
        foreach (var input in request.Days)
        {
            var row = existing.FirstOrDefault(b => b.DayOfWeek == input.DayOfWeek);

            if (row is null)
            {
                _db.BusinessHours.Add(new BusinessHour
                {
                    Id = Guid.NewGuid(),
                    TenantId = request.TenantId,
                    LocationId = request.LocationId,
                    DayOfWeek = input.DayOfWeek,
                    OpenTime = input.OpenTime,
                    CloseTime = input.CloseTime,
                    IsClosed = input.IsClosed
                });
            }
            else
            {
                row.OpenTime = input.OpenTime;
                row.CloseTime = input.CloseTime;
                row.IsClosed = input.IsClosed;
            }
        }

        await _db.SaveChangesAsync(cancellationToken);

        var week = await _db.BusinessHours
            .AsNoTracking()
            .Where(b => b.TenantId == request.TenantId && b.LocationId == request.LocationId)
            .OrderBy(b => b.DayOfWeek)
            .ThenBy(b => b.Id)
            .Select(b => new BusinessHourDto(b.DayOfWeek, b.OpenTime, b.CloseTime, b.IsClosed))
            .ToListAsync(cancellationToken);

        return new BusinessWeekDto(request.LocationId, timeZoneId, week);
    }
}

public record AvailabilityExceptionDto(
    Guid Id,
    Guid StaffId,
    string StaffName,
    DateTime StartAtUtc,
    DateTime EndAtUtc,
    bool IsAvailable,
    string? Reason);

/// <summary>
/// Writes an absence. There is deliberately no way to ask this route for <c>isAvailable: true</c>, the other half of
/// the entity's flag: the guard's calendar builds its blocked list from <c>!IsAvailable</c> rows only
/// (<c>AvailabilityService.cs:566-568</c>) and never reads a true row as a shift, so writing one would answer 201 for
/// a row that changes no slot. Refusing is the honest half of supporting the thing the reader actually reads.
/// </summary>
public record CreateAvailabilityExceptionCommand(
    Guid TenantId,
    Guid StaffId,
    DateTime StartAtUtc,
    DateTime EndAtUtc,
    bool IsAvailable,
    string? Reason) : IRequest<AvailabilityExceptionDto>;

public class CreateAvailabilityExceptionCommandValidator : AbstractValidator<CreateAvailabilityExceptionCommand>
{
    public CreateAvailabilityExceptionCommandValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.StaffId).NotEmpty();
        RuleFor(x => x.StartAtUtc).NotEmpty();
        RuleFor(x => x.EndAtUtc)
            .Must((command, end) => end > command.StartAtUtc)
            .WithMessage("An absence has to end after it starts.");
        RuleFor(x => x.EndAtUtc)
            .Must((command, end) => end - command.StartAtUtc <= TimeSpan.FromDays(CalendarLimits.MaxExceptionSpanDays))
            .WithMessage($"An absence block may span at most {CalendarLimits.MaxExceptionSpanDays} days.");
        RuleFor(x => x.Reason).MaximumLength(300);
        RuleFor(x => x.IsAvailable)
            .Must(available => !available)
            .WithMessage("This route writes time off. A custom shift has no reader in the booking guard, so nothing here can promise it would book.");
    }
}

public class CreateAvailabilityExceptionCommandHandler : IRequestHandler<CreateAvailabilityExceptionCommand, AvailabilityExceptionDto>
{
    private readonly IApplicationDbContext _db;
    private readonly IClock _clock;

    public CreateAvailabilityExceptionCommandHandler(IApplicationDbContext db, IClock clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<AvailabilityExceptionDto> Handle(CreateAvailabilityExceptionCommand request,
        CancellationToken cancellationToken)
    {
        var staff = await _db.StaffMembers
            .AsNoTracking()
            .Where(s => s.Id == request.StaffId && s.TenantId == request.TenantId)
            .Select(s => new { s.Id, s.FirstName, s.LastName })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException($"Staff member with ID {request.StaffId} was not found.");

        // The calendar load is bounded by the days it is asked about, so a block that already ended can never overlap
        // a candidate — it is a row that says nothing, and a 201 for it would be a promise the guard cannot keep.
        if (request.EndAtUtc <= _clock.UtcNow)
        {
            throw new BusinessRuleValidationException(
                "ExceptionAlreadyPast",
                "This absence has already ended, so it would block nothing.");
        }

        var exception = new AvailabilityException
        {
            Id = Guid.NewGuid(),
            TenantId = request.TenantId,
            StaffId = request.StaffId,
            StartDateTimeUtc = request.StartAtUtc,
            EndDateTimeUtc = request.EndAtUtc,
            IsAvailable = false,
            Reason = request.Reason
        };

        _db.AvailabilityExceptions.Add(exception);
        await _db.SaveChangesAsync(cancellationToken);

        return new AvailabilityExceptionDto(
            exception.Id, exception.StaffId, $"{staff.FirstName} {staff.LastName}",
            exception.StartDateTimeUtc, exception.EndDateTimeUtc, exception.IsAvailable, exception.Reason);
    }
}

public record DeleteAvailabilityExceptionCommand(Guid TenantId, Guid Id) : IRequest<Unit>;

public class DeleteAvailabilityExceptionCommandHandler : IRequestHandler<DeleteAvailabilityExceptionCommand, Unit>
{
    private readonly IApplicationDbContext _db;

    public DeleteAvailabilityExceptionCommandHandler(IApplicationDbContext db) => _db = db;

    public async Task<Unit> Handle(DeleteAvailabilityExceptionCommand request, CancellationToken cancellationToken)
    {
        var row = await _db.AvailabilityExceptions
            .FirstOrDefaultAsync(e => e.Id == request.Id && e.TenantId == request.TenantId, cancellationToken)
            ?? throw new NotFoundException($"Availability exception with ID {request.Id} was not found.");

        _db.AvailabilityExceptions.Remove(row);
        await _db.SaveChangesAsync(cancellationToken);

        return Unit.Value;
    }
}
