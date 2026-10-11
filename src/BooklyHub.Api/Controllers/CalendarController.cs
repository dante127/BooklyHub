using BooklyHub.Application.Common.Interfaces;
using BooklyHub.Application.Common.Models;
using BooklyHub.Application.Scheduling.Commands;
using BooklyHub.Application.Security;
using BooklyHub.Domain.Entities.Scheduling;
using BooklyHub.Infrastructure.Security;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BooklyHub.Api.Controllers;

/// <summary>
/// CAL-01: the calendar the booking guard decides a day from. These three tables had a reader in
/// <c>AvailabilityService.LoadCalendarAsync</c> and no writer anywhere in <c>src</c>, so on a deployed database every
/// one of them is empty and the guard's close-check cannot fire — a clinic that wants to close a day, or send one
/// staff member on leave, had to do it with an <c>INSERT</c>.
/// </summary>
[ApiController]
[Route("api/v1/calendar")]
[Authorize]
public class CalendarController : ControllerBase
{
    private readonly ISender _sender;
    private readonly IApplicationDbContext _db;
    private readonly ITenantContext _tenantContext;
    private readonly IClock _clock;

    public CalendarController(ISender sender, IApplicationDbContext db, ITenantContext tenantContext, IClock clock)
    {
        _sender = sender;
        _db = db;
        _tenantContext = tenantContext;
        _clock = clock;
    }

    public record CreateHolidayRequest(Guid? LocationId, string Name, DateOnly Date, bool RecurringAnnually);

    public record BusinessHourDayRequest(DayOfWeek DayOfWeek, TimeSpan OpenTime, TimeSpan CloseTime, bool IsClosed);

    public record SetBusinessHoursRequest(Guid LocationId, IReadOnlyList<BusinessHourDayRequest> Days);

    public record CreateAvailabilityExceptionRequest(
        Guid StaffId,
        DateTime StartAtUtc,
        DateTime EndAtUtc,
        bool IsAvailable,
        string? Reason);

    private Guid GetRequiredTenantId() => TenantGuard.RequireId(_tenantContext);

    [HttpGet("holidays")]
    [HasPermission(Permissions.Schedules.Read)]
    public async Task<ActionResult> GetHolidays(
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        [FromQuery] Guid? locationId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var tenantId = GetRequiredTenantId();
        var appliedPage = Paging.NormalizePage(page);
        var size = Paging.NormalizePageSize(pageSize);

        var query = _db.Holidays
            .AsNoTracking()
            .Where(h => h.TenantId == tenantId);

        if (locationId is { } scopedLocation)
        {
            // "Which closures apply at this branch" — a tenant-wide row applies there too, so it is in the answer.
            query = query.Where(h => h.LocationId == null || h.LocationId == scopedLocation);
        }

        // Deliberately the guard's own predicate (AvailabilityService.cs:480): a window asks which closures can fall
        // inside it, and an annually-recurring row can fall inside it whatever year its anchor carries. Filtering
        // recurring rows by their anchor date would hide from a manager the one row that closes the day they are
        // looking at.
        if (from is { } fromDay)
        {
            var toDay = to ?? fromDay;
            query = query.Where(h => h.RecurringAnnually || (h.Date >= fromDay && h.Date <= toDay));
        }
        else if (to is { } toOnly)
        {
            query = query.Where(h => h.RecurringAnnually || h.Date <= toOnly);
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderBy(h => h.Date).ThenBy(h => h.Id)
            .Skip(Paging.Offset(appliedPage, size))
            .Take(size)
            .Select(h => new
            {
                h.Id,
                h.LocationId,
                h.Name,
                h.Date,
                h.RecurringAnnually
            })
            .ToListAsync(cancellationToken);

        return Ok(new { total, page = appliedPage, pageSize = size, items });
    }

    [HttpPost("holidays")]
    [HasPermission(Permissions.Schedules.Manage)]
    public async Task<ActionResult<HolidayDto>> CreateHoliday(
        [FromBody] CreateHolidayRequest request,
        CancellationToken cancellationToken)
    {
        var holiday = await _sender.Send(
            new CreateHolidayCommand(
                GetRequiredTenantId(),
                request.LocationId,
                request.Name,
                request.Date,
                request.RecurringAnnually),
            cancellationToken);

        // The Location header names the collection, not the row: there is no GET-by-id route for a holiday, and a
        // header pointing at one would be a URL a client cannot call.
        return CreatedAtAction(nameof(GetHolidays), routeValues: null, holiday);
    }

    [HttpDelete("holidays/{id:guid}")]
    [HasPermission(Permissions.Schedules.Manage)]
    public async Task<IActionResult> DeleteHoliday(Guid id, CancellationToken cancellationToken)
    {
        await _sender.Send(new DeleteHolidayCommand(GetRequiredTenantId(), id), cancellationToken);
        return NoContent();
    }

    [HttpGet("business-hours")]
    [HasPermission(Permissions.Schedules.Read)]
    public async Task<ActionResult> GetBusinessHours(
        [FromQuery] Guid locationId,
        CancellationToken cancellationToken)
    {
        var tenantId = GetRequiredTenantId();

        var location = await _db.Locations
            .AsNoTracking()
            .Where(l => l.Id == locationId && l.TenantId == tenantId)
            .Select(l => new { l.Id, l.TimeZoneId })
            .FirstOrDefaultAsync(cancellationToken);

        if (location is null)
        {
            return NotFound(new { detail = $"Location with ID {locationId} was not found." });
        }

        var days = await _db.BusinessHours
            .AsNoTracking()
            .Where(b => b.TenantId == tenantId && b.LocationId == location.Id)
            .OrderBy(b => b.DayOfWeek)
            .ThenBy(b => b.Id)
            .Select(b => new { b.DayOfWeek, b.OpenTime, b.CloseTime, b.IsClosed })
            .ToListAsync(cancellationToken);

        // Not paged, and not a paging rule waiting for a number: one row per day of the week is seven, whichever way
        // the tenant's data grows.
        return Ok(new { locationId = location.Id, timeZoneId = location.TimeZoneId, days });
    }

    [HttpPut("business-hours")]
    [HasPermission(Permissions.Schedules.Manage)]
    public async Task<ActionResult<BusinessWeekDto>> SetBusinessHours(
        [FromBody] SetBusinessHoursRequest request,
        CancellationToken cancellationToken)
    {
        var week = await _sender.Send(
            new ReplaceBusinessHoursCommand(
                GetRequiredTenantId(),
                request.LocationId,
                request.Days.Select(d => new BusinessHourInput(d.DayOfWeek, d.OpenTime, d.CloseTime, d.IsClosed)).ToList()),
            cancellationToken);

        return Ok(week);
    }

    [HttpGet("availability-exceptions")]
    [HasPermission(Permissions.Schedules.Read)]
    public async Task<ActionResult> GetAvailabilityExceptions(
        [FromQuery] Guid? staffId,
        [FromQuery] DateTime? fromUtc,
        [FromQuery] DateTime? toUtc,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var tenantId = GetRequiredTenantId();
        var appliedPage = Paging.NormalizePage(page);
        var size = Paging.NormalizePageSize(pageSize);

        var query = _db.AvailabilityExceptions
            .AsNoTracking()
            .Where(e => e.TenantId == tenantId);

        if (staffId is { } staff)
        {
            query = query.Where(e => e.StaffId == staff);
        }

        if (fromUtc is { } from)
        {
            query = query.Where(e => e.EndDateTimeUtc > from);
        }
        else
        {
            // With no window asked for, the list answers "what is this roster missing", because this API refuses to
            // write an absence that has already ended — so a default that opened on the past would page through rows
            // that block nothing.
            var nowUtc = _clock.UtcNow;
            query = query.Where(e => e.EndDateTimeUtc >= nowUtc);
        }

        if (toUtc is { } to)
        {
            query = query.Where(e => e.StartDateTimeUtc <= to);
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderBy(e => e.StartDateTimeUtc)
            .ThenBy(e => e.Id)
            .Skip(Paging.Offset(appliedPage, size))
            .Take(size)
            .Select(e => new
            {
                e.Id,
                e.StaffId,
                StaffName = e.Staff!.FirstName + " " + e.Staff.LastName,
                StartAtUtc = e.StartDateTimeUtc,
                EndAtUtc = e.EndDateTimeUtc,
                e.IsAvailable,
                e.Reason
            })
            .ToListAsync(cancellationToken);

        return Ok(new { total, page = appliedPage, pageSize = size, items });
    }

    [HttpPost("availability-exceptions")]
    [HasPermission(Permissions.Schedules.Manage)]
    public async Task<ActionResult<AvailabilityExceptionDto>> CreateAvailabilityException(
        [FromBody] CreateAvailabilityExceptionRequest request,
        CancellationToken cancellationToken)
    {
        var created = await _sender.Send(
            new CreateAvailabilityExceptionCommand(
                GetRequiredTenantId(),
                request.StaffId,
                UtcInstant.Resolve(request.StartAtUtc),
                UtcInstant.Resolve(request.EndAtUtc),
                request.IsAvailable,
                request.Reason),
            cancellationToken);

        return CreatedAtAction(nameof(GetAvailabilityExceptions), routeValues: null, created);
    }

    [HttpDelete("availability-exceptions/{id:guid}")]
    [HasPermission(Permissions.Schedules.Manage)]
    public async Task<IActionResult> DeleteAvailabilityException(Guid id, CancellationToken cancellationToken)
    {
        await _sender.Send(new DeleteAvailabilityExceptionCommand(GetRequiredTenantId(), id), cancellationToken);
        return NoContent();
    }
}
