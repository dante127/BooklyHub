using BooklyHub.Application.Common.Helpers;
using BooklyHub.Application.Common.Interfaces;
using BooklyHub.Application.Scheduling;
using BooklyHub.Domain.Entities.Organizations;
using BooklyHub.Domain.Entities.Scheduling;
using BooklyHub.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace BooklyHub.Infrastructure.Services;

/// <summary>
/// Single source of truth for when a staff member can be booked. GetAvailabilityAsync enumerates the
/// day's candidate slots and CheckSlotAsync validates one slot, both through the same Classify method,
/// so the preview cannot offer a slot that the booking guard would reject.
/// </summary>
public class AvailabilityService : IAvailabilityService
{
    private readonly IApplicationDbContext _db;
    private readonly IClock _clock;

    public AvailabilityService(IApplicationDbContext db, IClock clock)
    {
        _db = db;
        _clock = clock;
    }

    private sealed record ResourceRequirement(Guid ResourceGroupId, int QuantityRequired);

    private sealed record Occupancy(Guid AppointmentId, Guid StaffId, DateTime StartUtc, DateTime EndUtc, List<Guid> ResourceIds);

    /// <summary>Shift time the staff member owns, and time inside it they do not (breaks, approved absence).</summary>
    private sealed record StaffSchedule(List<TimeInterval> ShiftWindows, List<TimeInterval> Blocked);

    /// <summary>Tenant, location and service facts that do not vary by date.</summary>
    private sealed record BookingContext(
        bool IsBookable,
        Guid TenantId,
        Guid LocationId,
        string TimeZoneId,
        TimeZoneInfo TimeZone,
        string ServiceName,
        int ServiceDurationMinutes,
        decimal Price,
        string Currency,
        int BufferBeforeMinutes,
        int BufferAfterMinutes,
        List<ResourceRequirement> ResourceRequirements,
        int MinNoticeMinutes,
        int MaxAdvanceDays,
        int SlotIntervalMinutes)
    {
        public static BookingContext NotBookable(string timeZoneId) => new(
            false, Guid.Empty, Guid.Empty, timeZoneId, TimeZoneHelper.ResolveTimeZone(timeZoneId),
            string.Empty, 0, 0m, "USD", 0, 0, [], 0, 0, 1);
    }

    /// <summary>
    /// Roster and occupancy for the queried date plus its neighbours, merged per staff member so a
    /// booking whose buffers cross local midnight is judged against the shift pattern it actually spans.
    /// </summary>
    private sealed record DayCalendar(
        IReadOnlySet<DateOnly> ClosedDates,
        IReadOnlyDictionary<Guid, StaffSchedule> Schedules,
        IReadOnlyList<Occupancy> Occupancy);

    /// <summary>The reason a slot was refused, and the resources it would hold when it was not.</summary>
    private sealed record SlotEvaluation(SlotUnavailableReason? Reason, List<Guid>? ResourceIds)
    {
        public static SlotEvaluation Rejected(SlotUnavailableReason reason) => new(reason, null);
        public static SlotEvaluation Accepted(List<Guid> resourceIds) => new(null, resourceIds);
    }

    public async Task<DayAvailabilityDto> GetAvailabilityAsync(
        GetAvailabilityQuery query,
        CancellationToken cancellationToken = default)
    {
        var staffQuery = _db.StaffMembers
            .AsNoTracking()
            .Where(s => s.TenantId == query.TenantId &&
                        s.LocationId == query.LocationId &&
                        s.IsActive)
            .Where(s => s.StaffServices.Any(ss => ss.ServiceId == query.ServiceId));

        if (query.StaffId.HasValue)
        {
            staffQuery = staffQuery.Where(s => s.Id == query.StaffId.Value);
        }

        var candidateStaffList = await staffQuery
            .Select(s => new
            {
                s.Id,
                s.FirstName,
                s.LastName,
                CustomDuration = s.StaffServices.Where(ss => ss.ServiceId == query.ServiceId).Select(ss => ss.CustomDurationMinutes).FirstOrDefault(),
                CustomPrice = s.StaffServices.Where(ss => ss.ServiceId == query.ServiceId).Select(ss => ss.CustomPrice).FirstOrDefault()
            })
            .ToListAsync(cancellationToken);

        var context = await LoadBookingContextAsync(query.TenantId, query.LocationId, query.ServiceId, cancellationToken);

        if (!context.IsBookable)
        {
            return new DayAvailabilityDto(query.Date, context.TimeZoneId, false, []);
        }

        // One day either side: a slot's buffers can reach over local midnight.
        var calendar = await LoadCalendarAsync(
            context,
            candidateStaffList.Select(s => s.Id).ToList(),
            new[] { query.Date.AddDays(-1), query.Date, query.Date.AddDays(1) },
            cancellationToken);

        if (calendar.ClosedDates.Contains(query.Date))
        {
            return new DayAvailabilityDto(query.Date, context.TimeZoneId, false, []);
        }

        if (candidateStaffList.Count == 0)
        {
            return new DayAvailabilityDto(query.Date, context.TimeZoneId, true, []);
        }

        var minBookingTimeUtc = _clock.UtcNow.AddMinutes(context.MinNoticeMinutes);
        var maxBookingTimeUtc = _clock.UtcNow.AddDays(context.MaxAdvanceDays);
        var resourcesByGroup = await LoadResourcesByGroupAsync(context, cancellationToken);

        var slots = new List<AvailableSlotDto>();

        foreach (var staff in candidateStaffList)
        {
            var duration = staff.CustomDuration ?? context.ServiceDurationMinutes;
            var price = staff.CustomPrice ?? context.Price;

            foreach (var start in CandidateStartsUtc(query.Date, context))
            {
                var evaluation = Classify(
                    context, calendar, staff.Id, start, start.AddMinutes(duration),
                    minBookingTimeUtc, maxBookingTimeUtc, excludeAppointmentId: null, resourcesByGroup);

                if (evaluation.Reason is not null)
                {
                    continue;
                }

                slots.Add(new AvailableSlotDto(
                    StartAtUtc: start,
                    EndAtUtc: start.AddMinutes(duration),
                    StartAtLocal: TimeZoneHelper.ToLocal(start, context.TimeZone),
                    EndAtLocal: TimeZoneHelper.ToLocal(start.AddMinutes(duration), context.TimeZone),
                    StaffId: staff.Id,
                    StaffName: $"{staff.FirstName} {staff.LastName}".Trim(),
                    ServiceId: query.ServiceId,
                    ServiceName: context.ServiceName,
                    DurationMinutes: duration,
                    Price: price,
                    Currency: context.Currency,
                    AvailableResourceIds: evaluation.ResourceIds ?? []));
            }
        }

        var sortedSlots = slots
            .OrderBy(s => s.StartAtUtc)
            .ThenBy(s => s.StaffName)
            .ToList();

        return new DayAvailabilityDto(query.Date, context.TimeZoneId, true, sortedSlots);
    }

    public async Task<SlotCheckResult> CheckSlotAsync(
        Guid tenantId,
        Guid locationId,
        Guid serviceId,
        Guid staffId,
        DateTime startAtUtc,
        DateTime endAtUtc,
        Guid? excludeAppointmentId = null,
        CancellationToken cancellationToken = default)
    {
        var evaluations = await EvaluateAsync(
            tenantId, locationId, serviceId, staffId,
            [new SlotCandidate(startAtUtc, endAtUtc)], excludeAppointmentId, cancellationToken);

        return new SlotCheckResult(evaluations[0].Reason, evaluations[0].ResourceIds ?? []);
    }

    public async Task<IReadOnlyList<SlotCheckResult>> CheckSlotsAsync(
        Guid tenantId,
        Guid locationId,
        Guid serviceId,
        Guid staffId,
        IReadOnlyList<SlotCandidate> candidates,
        Guid? excludeAppointmentId = null,
        CancellationToken cancellationToken = default)
    {
        var evaluations = await EvaluateAsync(
            tenantId, locationId, serviceId, staffId, candidates, excludeAppointmentId, cancellationToken);

        return evaluations.Select(e => new SlotCheckResult(e.Reason, e.ResourceIds ?? [])).ToList();
    }

    private async Task<IReadOnlyList<SlotEvaluation>> EvaluateAsync(
        Guid tenantId,
        Guid locationId,
        Guid serviceId,
        Guid staffId,
        IReadOnlyList<SlotCandidate> candidates,
        Guid? excludeAppointmentId,
        CancellationToken ct)
    {
        var evaluations = new SlotEvaluation?[candidates.Count];

        for (var i = 0; i < candidates.Count; i++)
        {
            if (candidates[i].EndAtUtc <= candidates[i].StartAtUtc)
            {
                evaluations[i] = SlotEvaluation.Rejected(SlotUnavailableReason.StaffNotAvailable);
            }
        }

        var pending = candidates
            .Select((candidate, index) => (candidate, index))
            .Where(x => evaluations[x.index] is null)
            .ToList();

        if (pending.Count == 0)
        {
            return evaluations.Select(e => e!).ToList();
        }

        var context = await LoadBookingContextAsync(tenantId, locationId, serviceId, ct);

        if (!context.IsBookable)
        {
            return Fill(evaluations, pending, SlotUnavailableReason.Closed);
        }

        // Booking and reschedule validate eligibility themselves; this is the only check the recurring
        // series path applies, and it mirrors the staff filter the availability preview uses.
        var isEligible = await _db.StaffMembers
            .AsNoTracking()
            .AnyAsync(s => s.Id == staffId &&
                           s.TenantId == tenantId &&
                           s.LocationId == locationId &&
                           s.IsActive &&
                           s.StaffServices.Any(ss => ss.ServiceId == serviceId),
                      ct);

        if (!isEligible)
        {
            return Fill(evaluations, pending, SlotUnavailableReason.StaffNotAvailable);
        }

        // Buffers can reach across local midnight, where the shift pattern belongs to another date.
        var dates = new HashSet<DateOnly>();

        foreach (var (candidate, _) in pending)
        {
            dates.Add(LocalDate(candidate.StartAtUtc, context));
            dates.Add(LocalDate(candidate.EndAtUtc, context));
            dates.Add(LocalDate(candidate.StartAtUtc.AddMinutes(-context.BufferBeforeMinutes), context));
            dates.Add(LocalDate(candidate.EndAtUtc.AddMinutes(context.BufferAfterMinutes), context));
        }

        var calendar = await LoadCalendarAsync(context, [staffId], dates, ct);

        var minBookingTimeUtc = _clock.UtcNow.AddMinutes(context.MinNoticeMinutes);
        var maxBookingTimeUtc = _clock.UtcNow.AddDays(context.MaxAdvanceDays);
        var resourcesByGroup = await LoadResourcesByGroupAsync(context, ct);

        // The calendar only holds committed bookings, so a batch that was not written yet is invisible to
        // its own later candidates. Each accepted candidate is replayed as a synthetic occupancy, in time
        // order, so one request can never reserve the same minute or the same room twice. Guid.Empty
        // stands in for the appointment id because no caller may exclude a slot it has not booked.
        var reservations = new List<Occupancy>();

        foreach (var (candidate, index) in pending
                     .OrderBy(x => x.candidate.StartAtUtc)
                     .ThenBy(x => x.index))
        {
            var evaluation = Classify(
                context,
                calendar with { Occupancy = [.. calendar.Occupancy, .. reservations] },
                staffId,
                candidate.StartAtUtc,
                candidate.EndAtUtc,
                minBookingTimeUtc,
                maxBookingTimeUtc,
                excludeAppointmentId,
                resourcesByGroup);

            evaluations[index] = evaluation;

            if (evaluation.Reason is null)
            {
                reservations.Add(new Occupancy(
                    Guid.Empty,
                    staffId,
                    candidate.StartAtUtc,
                    candidate.EndAtUtc,
                    evaluation.ResourceIds ?? []));
            }
        }

        return evaluations.Select(e => e!).ToList();
    }

    private static IReadOnlyList<SlotEvaluation> Fill(
        SlotEvaluation?[] evaluations,
        List<(SlotCandidate candidate, int index)> pending,
        SlotUnavailableReason reason)
    {
        foreach (var (_, index) in pending)
        {
            evaluations[index] = SlotEvaluation.Rejected(reason);
        }

        return evaluations.Select(e => e!).ToList();
    }

    private static DateOnly LocalDate(DateTime utc, BookingContext context)
        => DateOnly.FromDateTime(TimeZoneHelper.ToLocal(utc, context.TimeZone));

    /// <summary>
    /// The one rule set both entry points share. Buffers are charged once: the candidate's prep and
    /// cleanup time must fit inside the staff member's own shift, and the candidate's core must not
    /// intrude on the buffer-expanded time an existing appointment already reserves.
    /// </summary>
    private static SlotEvaluation Classify(
        BookingContext context,
        DayCalendar calendar,
        Guid staffId,
        DateTime startAtUtc,
        DateTime endAtUtc,
        DateTime minBookingTimeUtc,
        DateTime maxBookingTimeUtc,
        Guid? excludeAppointmentId,
        Dictionary<Guid, List<Guid>> resourcesByGroup)
    {
        var startDate = LocalDate(startAtUtc, context);

        if (calendar.ClosedDates.Contains(startDate))
        {
            return SlotEvaluation.Rejected(SlotUnavailableReason.Closed);
        }

        if (!calendar.Schedules.TryGetValue(staffId, out var schedule))
        {
            return SlotEvaluation.Rejected(SlotUnavailableReason.StaffNotAvailable);
        }

        var core = new TimeInterval(startAtUtc, endAtUtc);
        var buffered = new TimeInterval(
            startAtUtc.AddMinutes(-context.BufferBeforeMinutes),
            endAtUtc.AddMinutes(context.BufferAfterMinutes));

        if (!schedule.ShiftWindows.Any(w => Contains(w, buffered)))
        {
            return SlotEvaluation.Rejected(SlotUnavailableReason.StaffNotAvailable);
        }

        if (core.StartUtc < minBookingTimeUtc || core.EndUtc > maxBookingTimeUtc)
        {
            return SlotEvaluation.Rejected(SlotUnavailableReason.OutsideBookingWindow);
        }

        var busy = BusyIntervals(context, calendar.Occupancy, staffId, excludeAppointmentId);

        if (busy.Any(b => b.Overlaps(core)))
        {
            return SlotEvaluation.Rejected(SlotUnavailableReason.StaffBusy);
        }

        if (schedule.Blocked.Any(b => b.Overlaps(buffered)))
        {
            return SlotEvaluation.Rejected(SlotUnavailableReason.StaffNotAvailable);
        }

        var allocatedResourceIds = TryAllocateResources(
            context, calendar.Occupancy, core.StartUtc, core.EndUtc, excludeAppointmentId, resourcesByGroup);

        return allocatedResourceIds is null
            ? SlotEvaluation.Rejected(SlotUnavailableReason.ResourceUnavailable)
            : SlotEvaluation.Accepted(allocatedResourceIds);
    }

    /// <summary>Slot grid for one local day, anchored on local midnight so slots stay round in the location's own time.</summary>
    private static IEnumerable<DateTime> CandidateStartsUtc(DateOnly date, BookingContext context)
    {
        var localMidnight = date.ToDateTime(TimeOnly.MinValue);

        for (var offsetMinutes = 0; offsetMinutes < 24 * 60; offsetMinutes += context.SlotIntervalMinutes)
        {
            yield return TimeZoneHelper.ToUtc(localMidnight.AddMinutes(offsetMinutes), context.TimeZone);
        }
    }

    private async Task<BookingContext> LoadBookingContextAsync(
        Guid tenantId,
        Guid locationId,
        Guid serviceId,
        CancellationToken ct)
    {
        var tenant = await _db.Tenants
            .AsNoTracking()
            .Include(t => t.Settings)
            .FirstOrDefaultAsync(t => t.Id == tenantId, ct);

        if (tenant is null || !tenant.IsActive)
        {
            return BookingContext.NotBookable("UTC");
        }

        var location = await _db.Locations
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == locationId && l.TenantId == tenantId, ct);

        if (location is null || !location.IsActive)
        {
            return BookingContext.NotBookable("UTC");
        }

        var service = await _db.Services
            .AsNoTracking()
            .Include(s => s.ResourceRequirements)
            .FirstOrDefaultAsync(s => s.Id == serviceId && s.TenantId == tenantId, ct);

        if (service is null || !service.IsActive)
        {
            return BookingContext.NotBookable(location.TimeZoneId);
        }

        return new BookingContext(
            true,
            tenantId,
            locationId,
            location.TimeZoneId,
            TimeZoneHelper.ResolveTimeZone(location.TimeZoneId),
            service.Name,
            service.DurationMinutes,
            service.Price,
            service.Currency,
            service.BufferBeforeMinutes,
            service.BufferAfterMinutes,
            service.ResourceRequirements
                .Select(r => new ResourceRequirement(r.ResourceGroupId, r.QuantityRequired))
                .ToList(),
            tenant.Settings?.MinBookingNoticeMinutes ?? 120,
            tenant.Settings?.MaxAdvanceBookingDays ?? 60,
            Math.Max(1, tenant.Settings?.SlotIntervalMinutes ?? 15));
    }

    private async Task<DayCalendar> LoadCalendarAsync(
        BookingContext context,
        IReadOnlyCollection<Guid> staffIds,
        IReadOnlyCollection<DateOnly> dates,
        CancellationToken ct)
    {
        var orderedDates = dates.Distinct().Order().ToList();
        var firstDate = orderedDates[0];
        var lastDate = orderedDates[^1];
        var rangeStartUtc = TimeZoneHelper.ToUtc(firstDate.ToDateTime(TimeOnly.MinValue), context.TimeZone);
        var rangeEndUtc = TimeZoneHelper.ToUtc(lastDate.ToDateTime(TimeOnly.MaxValue), context.TimeZone);
        var daysOfWeek = orderedDates.Select(d => d.DayOfWeek).Distinct().ToList();

        // Bounded by the range rather than an IN-list of dates, so a year-long series stays one query.
        var holidays = await _db.Holidays
            .AsNoTracking()
            .Where(h => h.TenantId == context.TenantId &&
                        (h.LocationId == null || h.LocationId == context.LocationId) &&
                        ((h.Date >= firstDate && h.Date <= lastDate) || h.RecurringAnnually))
            .Select(h => new { h.Date, h.RecurringAnnually })
            .ToListAsync(ct);

        var closedDates = orderedDates
            .Where(d => holidays.Any(h => h.Date == d ||
                                          (h.RecurringAnnually && h.Date.Month == d.Month && h.Date.Day == d.Day)))
            .ToHashSet();

        // Resource contention is location-wide, so appointments are loaded regardless of which staff
        // member is being evaluated, and closed days still contribute their existing bookings. Only a
        // cancellation frees a slot: any other live row (including a legacy Rescheduled one) still blocks it.
        var occupancy = await _db.Appointments
            .AsNoTracking()
            .Include(a => a.AppointmentResources)
            .Where(a => a.TenantId == context.TenantId &&
                        a.LocationId == context.LocationId &&
                        a.Status != AppointmentStatus.Cancelled &&
                        a.StartAtUtc < rangeEndUtc &&
                        a.EndAtUtc > rangeStartUtc)
            .Select(a => new Occupancy(
                a.Id,
                a.StaffId,
                a.StartAtUtc,
                a.EndAtUtc,
                a.AppointmentResources.Select(ar => ar.ResourceId).ToList()))
            .ToListAsync(ct);

        var workingHours = await _db.WorkingHours
            .AsNoTracking()
            .Include(w => w.Intervals)
            .Where(w => w.TenantId == context.TenantId &&
                        w.LocationId == context.LocationId &&
                        daysOfWeek.Contains(w.DayOfWeek) &&
                        staffIds.Contains(w.StaffId))
            .ToListAsync(ct);

        var businessHours = await _db.BusinessHours
            .AsNoTracking()
            .Where(b => b.TenantId == context.TenantId &&
                        b.LocationId == context.LocationId &&
                        daysOfWeek.Contains(b.DayOfWeek) &&
                        !b.IsClosed)
            .ToListAsync(ct);

        var exceptions = await _db.AvailabilityExceptions
            .AsNoTracking()
            .Where(e => e.TenantId == context.TenantId &&
                        staffIds.Contains(e.StaffId) &&
                        e.StartDateTimeUtc < rangeEndUtc &&
                        e.EndDateTimeUtc > rangeStartUtc)
            .ToListAsync(ct);

        var schedules = new Dictionary<Guid, StaffSchedule>();

        foreach (var staffId in staffIds)
        {
            var shiftWindows = new List<TimeInterval>();
            var blocked = new List<TimeInterval>();

            foreach (var date in orderedDates)
            {
                if (closedDates.Contains(date))
                {
                    continue;
                }

                var workingHour = workingHours.FirstOrDefault(w => w.StaffId == staffId && w.DayOfWeek == date.DayOfWeek);

                if (workingHour is not null && workingHour.IsWorkingDay)
                {
                    var dayShift = ToUtcDayIntervals(workingHour.Intervals.Where(i => !i.IsBreak), date, context.TimeZone);
                    var dayBreaks = ToUtcDayIntervals(workingHour.Intervals.Where(i => i.IsBreak), date, context.TimeZone);

                    shiftWindows.AddRange(dayShift);
                    blocked.AddRange(dayBreaks);

                    // A roster row carrying only breaks defers to the location's opening hours; a row
                    // with no intervals at all means the staff member is not bookable that day.
                    if (dayShift.Count == 0 && dayBreaks.Count > 0)
                    {
                        shiftWindows.AddRange(BusinessWindows(date, businessHours, context));
                    }
                }
            }

            blocked.AddRange(exceptions
                .Where(e => e.StaffId == staffId && !e.IsAvailable)
                .Select(e => new TimeInterval(e.StartDateTimeUtc, e.EndDateTimeUtc)));

            schedules[staffId] = new StaffSchedule(shiftWindows, blocked);
        }

        return new DayCalendar(closedDates, schedules, occupancy);
    }

    private static IEnumerable<TimeInterval> BusinessWindows(
        DateOnly date,
        List<BusinessHour> businessHours,
        BookingContext context)
        => businessHours
            .Where(b => b.DayOfWeek == date.DayOfWeek)
            .Select(b => new TimeInterval(
                TimeZoneHelper.ToUtc(date.ToDateTime(TimeOnly.FromTimeSpan(b.OpenTime)), context.TimeZone),
                TimeZoneHelper.ToUtc(date.ToDateTime(TimeOnly.FromTimeSpan(b.CloseTime)), context.TimeZone)));

    private static List<TimeInterval> BusyIntervals(
        BookingContext context,
        IReadOnlyCollection<Occupancy> occupancy,
        Guid staffId,
        Guid? excludeAppointmentId)
        => occupancy
            .Where(a => a.StaffId == staffId && a.AppointmentId != excludeAppointmentId)
            .Select(a => new TimeInterval(
                a.StartUtc.AddMinutes(-context.BufferBeforeMinutes),
                a.EndUtc.AddMinutes(context.BufferAfterMinutes)))
            .ToList();

    private static bool Contains(TimeInterval window, TimeInterval candidate)
        => window.StartUtc <= candidate.StartUtc && candidate.EndUtc <= window.EndUtc;

    private static List<TimeInterval> ToUtcDayIntervals(
        IEnumerable<WorkingHourInterval> intervals,
        DateOnly date,
        TimeZoneInfo timeZone)
        => intervals
            .Select(i => new TimeInterval(
                TimeZoneHelper.ToUtc(date.ToDateTime(TimeOnly.FromTimeSpan(i.StartTime)), timeZone),
                TimeZoneHelper.ToUtc(date.ToDateTime(TimeOnly.FromTimeSpan(i.EndTime)), timeZone)))
            .ToList();

    private async Task<Dictionary<Guid, List<Guid>>> LoadResourcesByGroupAsync(BookingContext context, CancellationToken ct)
    {
        var rawResources = await _db.Resources
            .AsNoTracking()
            .Where(r => r.TenantId == context.TenantId && r.LocationId == context.LocationId && r.IsActive)
            .Select(r => new { r.ResourceGroupId, r.Id })
            .ToListAsync(ct);

        return rawResources
            .GroupBy(r => r.ResourceGroupId)
            .ToDictionary(g => g.Key, g => g.Select(r => r.Id).ToList());
    }

    /// <summary>
    /// Picks one free resource per required group, or null when the location cannot supply them. Existing
    /// bookings reserve their resources for their core span only: buffers belong to the staff member, not
    /// the room. <paramref name="excludeAppointmentId"/> is dropped just like in the staff check, so a
    /// reschedule is never blocked by the room it already holds.
    /// </summary>
    private static List<Guid>? TryAllocateResources(
        BookingContext context,
        IReadOnlyCollection<Occupancy> occupancy,
        DateTime startUtc,
        DateTime endUtc,
        Guid? excludeAppointmentId,
        Dictionary<Guid, List<Guid>> resourcesByGroup)
    {
        if (context.ResourceRequirements.Count == 0)
        {
            return [];
        }

        var busyResourceIds = occupancy
            .Where(a => a.AppointmentId != excludeAppointmentId && a.StartUtc < endUtc && a.EndUtc > startUtc)
            .SelectMany(a => a.ResourceIds)
            .ToHashSet();

        var allocatedResourceIds = new List<Guid>();

        foreach (var req in context.ResourceRequirements)
        {
            if (!resourcesByGroup.TryGetValue(req.ResourceGroupId, out var groupResourceIds))
            {
                return null;
            }

            var freeResourceIds = groupResourceIds
                .Where(rId => !busyResourceIds.Contains(rId))
                .ToList();

            if (freeResourceIds.Count < req.QuantityRequired)
            {
                return null;
            }

            allocatedResourceIds.AddRange(freeResourceIds.Take(req.QuantityRequired));
        }

        return allocatedResourceIds;
    }
}
