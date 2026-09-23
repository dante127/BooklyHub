using BooklyHub.Application.Common.Helpers;
using BooklyHub.Application.Common.Interfaces;
using BooklyHub.Application.Scheduling;
using BooklyHub.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace BooklyHub.Infrastructure.Services;

public class AvailabilityService : IAvailabilityService
{
    private readonly IApplicationDbContext _db;
    private readonly IClock _clock;

    public AvailabilityService(IApplicationDbContext db, IClock clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<DayAvailabilityDto> GetAvailabilityAsync(
        GetAvailabilityQuery query,
        CancellationToken cancellationToken = default)
    {
        var tenant = await _db.Tenants
            .AsNoTracking()
            .Include(t => t.Settings)
            .FirstOrDefaultAsync(t => t.Id == query.TenantId, cancellationToken);

        if (tenant == null || !tenant.IsActive)
        {
            return new DayAvailabilityDto(query.Date, "UTC", false, []);
        }

        var location = await _db.Locations
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == query.LocationId && l.TenantId == query.TenantId, cancellationToken);

        if (location == null || !location.IsActive)
        {
            return new DayAvailabilityDto(query.Date, "UTC", false, []);
        }

        var timeZone = TimeZoneHelper.ResolveTimeZone(location.TimeZoneId);

        var service = await _db.Services
            .AsNoTracking()
            .Include(s => s.ResourceRequirements)
            .FirstOrDefaultAsync(s => s.Id == query.ServiceId && s.TenantId == query.TenantId, cancellationToken);

        if (service == null || !service.IsActive)
        {
            return new DayAvailabilityDto(query.Date, location.TimeZoneId, false, []);
        }

        // Check Holidays
        var isHoliday = await _db.Holidays
            .AsNoTracking()
            .AnyAsync(h => h.TenantId == query.TenantId &&
                           (h.LocationId == null || h.LocationId == query.LocationId) &&
                           ((h.Date == query.Date) || (h.RecurringAnnually && h.Date.Month == query.Date.Month && h.Date.Day == query.Date.Day)),
                      cancellationToken);

        if (isHoliday)
        {
            return new DayAvailabilityDto(query.Date, location.TimeZoneId, false, []);
        }

        // Calculate UTC day window
        var localDayStart = query.Date.ToDateTime(TimeOnly.MinValue);
        var localDayEnd = query.Date.ToDateTime(TimeOnly.MaxValue);

        var dayStartUtc = TimeZoneHelper.ToUtc(localDayStart, timeZone);
        var dayEndUtc = TimeZoneHelper.ToUtc(localDayEnd, timeZone);

        // Booking notice & advance bounds
        var minNoticeMinutes = tenant.Settings?.MinBookingNoticeMinutes ?? 120;
        var maxAdvanceDays = tenant.Settings?.MaxAdvanceBookingDays ?? 60;
        var slotIntervalMinutes = tenant.Settings?.SlotIntervalMinutes ?? 15;

        var minBookingTimeUtc = _clock.UtcNow.AddMinutes(minNoticeMinutes);
        var maxBookingTimeUtc = _clock.UtcNow.AddDays(maxAdvanceDays);

        // Resolve staff candidates
        var staffQuery = _db.StaffMembers
            .AsNoTracking()
            .Where(s => s.TenantId == query.TenantId && s.LocationId == query.LocationId && s.IsActive)
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

        if (candidateStaffList.Count == 0)
        {
            return new DayAvailabilityDto(query.Date, location.TimeZoneId, true, []);
        }

        // Query resources for this location
        var rawResources = await _db.Resources
            .AsNoTracking()
            .Where(r => r.TenantId == query.TenantId && r.LocationId == query.LocationId && r.IsActive)
            .Select(r => new { r.ResourceGroupId, r.Id })
            .ToListAsync(cancellationToken);

        var resourcesByGroup = rawResources
            .GroupBy(r => r.ResourceGroupId)
            .ToDictionary(g => g.Key, g => g.Select(r => r.Id).ToList());

        // Query overlapping appointments for the day to check resource and staff occupation
        var dayAppointments = await _db.Appointments
            .AsNoTracking()
            .Include(a => a.AppointmentResources)
            .Where(a => a.TenantId == query.TenantId &&
                        a.LocationId == query.LocationId &&
                        a.Status != AppointmentStatus.Cancelled &&
                        a.Status != AppointmentStatus.Rescheduled &&
                        a.StartAtUtc < dayEndUtc &&
                        a.EndAtUtc > dayStartUtc)
            .Select(a => new
            {
                a.Id,
                a.StaffId,
                a.StartAtUtc,
                a.EndAtUtc,
                AllocatedResourceIds = a.AppointmentResources.Select(ar => ar.ResourceId).ToList()
            })
            .ToListAsync(cancellationToken);

        var allAvailableSlots = new List<AvailableSlotDto>();

        foreach (var staff in candidateStaffList)
        {
            var duration = staff.CustomDuration ?? service.DurationMinutes;
            var price = staff.CustomPrice ?? service.Price;

            // Load staff working hours for this day of week
            var targetDayOfWeek = query.Date.DayOfWeek;
            var workingHour = await _db.WorkingHours
                .AsNoTracking()
                .Include(w => w.Intervals)
                .FirstOrDefaultAsync(w => w.TenantId == query.TenantId &&
                                          w.StaffId == staff.Id &&
                                          w.LocationId == query.LocationId &&
                                          w.DayOfWeek == targetDayOfWeek,
                                     cancellationToken);

            if (workingHour == null || !workingHour.IsWorkingDay)
            {
                continue;
            }

            // Convert working shifts to UTC intervals
            var workingIntervals = new List<TimeInterval>();
            var breakIntervals = new List<TimeInterval>();

            foreach (var interval in workingHour.Intervals)
            {
                var intervalStartLocal = query.Date.ToDateTime(TimeOnly.FromTimeSpan(interval.StartTime));
                var intervalEndLocal = query.Date.ToDateTime(TimeOnly.FromTimeSpan(interval.EndTime));

                var intervalStartUtc = TimeZoneHelper.ToUtc(intervalStartLocal, timeZone);
                var intervalEndUtc = TimeZoneHelper.ToUtc(intervalEndLocal, timeZone);

                if (interval.IsBreak)
                {
                    breakIntervals.Add(new TimeInterval(intervalStartUtc, intervalEndUtc));
                }
                else
                {
                    workingIntervals.Add(new TimeInterval(intervalStartUtc, intervalEndUtc));
                }
            }

            // If no explicit intervals configured, default to location business hours
            if (workingIntervals.Count == 0)
            {
                var businessHour = await _db.BusinessHours
                    .AsNoTracking()
                    .FirstOrDefaultAsync(b => b.TenantId == query.TenantId &&
                                              b.LocationId == query.LocationId &&
                                              b.DayOfWeek == targetDayOfWeek,
                                         cancellationToken);

                if (businessHour != null && !businessHour.IsClosed)
                {
                    var openUtc = TimeZoneHelper.ToUtc(query.Date.ToDateTime(TimeOnly.FromTimeSpan(businessHour.OpenTime)), timeZone);
                    var closeUtc = TimeZoneHelper.ToUtc(query.Date.ToDateTime(TimeOnly.FromTimeSpan(businessHour.CloseTime)), timeZone);
                    workingIntervals.Add(new TimeInterval(openUtc, closeUtc));
                }
            }

            if (workingIntervals.Count == 0) continue;

            // Subtract breaks
            var availableIntervals = TimeInterval.SubtractMany(workingIntervals, breakIntervals);

            // Apply availability exceptions
            var exceptions = await _db.AvailabilityExceptions
                .AsNoTracking()
                .Where(e => e.TenantId == query.TenantId &&
                            e.StaffId == staff.Id &&
                            e.StartDateTimeUtc < dayEndUtc &&
                            e.EndDateTimeUtc > dayStartUtc)
                .ToListAsync(cancellationToken);

            var exceptionTimeOffs = exceptions
                .Where(e => !e.IsAvailable)
                .Select(e => new TimeInterval(e.StartDateTimeUtc, e.EndDateTimeUtc))
                .ToList();

            availableIntervals = TimeInterval.SubtractMany(availableIntervals, exceptionTimeOffs);

            // Subtract existing appointments for this staff member
            var staffBlockedIntervals = dayAppointments
                .Where(a => a.StaffId == staff.Id)
                .Select(a => new TimeInterval(
                    a.StartAtUtc.AddMinutes(-service.BufferBeforeMinutes),
                    a.EndAtUtc.AddMinutes(service.BufferAfterMinutes)))
                .ToList();

            availableIntervals = TimeInterval.SubtractMany(availableIntervals, staffBlockedIntervals);

            // Discretize available intervals into valid appointment slots
            foreach (var interval in availableIntervals)
            {
                // Slot start must allow buffer before
                var currentSlotStart = interval.StartUtc.AddMinutes(service.BufferBeforeMinutes);

                // Align to slotIntervalMinutes
                var minutesMod = currentSlotStart.Minute % slotIntervalMinutes;
                if (minutesMod != 0)
                {
                    currentSlotStart = currentSlotStart.AddMinutes(slotIntervalMinutes - minutesMod);
                }

                while (currentSlotStart.AddMinutes(duration + service.BufferAfterMinutes) <= interval.EndUtc)
                {
                    var slotEnd = currentSlotStart.AddMinutes(duration);

                    // Check notice and advance limits
                    if (currentSlotStart >= minBookingTimeUtc && slotEnd <= maxBookingTimeUtc)
                    {
                        // Check resource allocation
                        var resourcesAvailable = true;
                        var allocatedResourceIds = new List<Guid>();

                        if (service.ResourceRequirements.Count > 0)
                        {
                            foreach (var req in service.ResourceRequirements)
                            {
                                if (!resourcesByGroup.TryGetValue(req.ResourceGroupId, out var groupResourceIds))
                                {
                                    resourcesAvailable = false;
                                    break;
                                }

                                // Find which resources in this group are already booked during currentSlotStart -> slotEnd
                                var busyResourceIds = dayAppointments
                                    .Where(a => a.StartAtUtc < slotEnd && a.EndAtUtc > currentSlotStart)
                                    .SelectMany(a => a.AllocatedResourceIds)
                                    .ToHashSet();

                                var freeResourceIds = groupResourceIds
                                    .Where(rId => !busyResourceIds.Contains(rId))
                                    .ToList();

                                if (freeResourceIds.Count < req.QuantityRequired)
                                {
                                    resourcesAvailable = false;
                                    break;
                                }

                                allocatedResourceIds.AddRange(freeResourceIds.Take(req.QuantityRequired));
                            }
                        }

                        if (resourcesAvailable)
                        {
                            var localSlotStart = TimeZoneHelper.ToLocal(currentSlotStart, timeZone);
                            var localSlotEnd = TimeZoneHelper.ToLocal(slotEnd, timeZone);

                            allAvailableSlots.Add(new AvailableSlotDto(
                                StartAtUtc: currentSlotStart,
                                EndAtUtc: slotEnd,
                                StartAtLocal: localSlotStart,
                                EndAtLocal: localSlotEnd,
                                StaffId: staff.Id,
                                StaffName: $"{staff.FirstName} {staff.LastName}".Trim(),
                                ServiceId: service.Id,
                                ServiceName: service.Name,
                                DurationMinutes: duration,
                                Price: price,
                                Currency: service.Currency,
                                AvailableResourceIds: allocatedResourceIds));
                        }
                    }

                    currentSlotStart = currentSlotStart.AddMinutes(slotIntervalMinutes);
                }
            }
        }

        var sortedSlots = allAvailableSlots
            .OrderBy(s => s.StartAtUtc)
            .ThenBy(s => s.StaffName)
            .ToList();

        return new DayAvailabilityDto(query.Date, location.TimeZoneId, true, sortedSlots);
    }

    public async Task<bool> IsSlotAvailableAsync(
        Guid tenantId,
        Guid locationId,
        Guid serviceId,
        Guid staffId,
        DateTime startAtUtc,
        DateTime endAtUtc,
        Guid? excludeAppointmentId = null,
        CancellationToken cancellationToken = default)
    {
        var service = await _db.Services
            .AsNoTracking()
            .Include(s => s.ResourceRequirements)
            .FirstOrDefaultAsync(s => s.Id == serviceId && s.TenantId == tenantId, cancellationToken);

        if (service == null || !service.IsActive) return false;

        // Check for conflicting appointments for the staff (including buffer times)
        var bufferStart = startAtUtc.AddMinutes(-service.BufferBeforeMinutes);
        var bufferEnd = endAtUtc.AddMinutes(service.BufferAfterMinutes);

        var staffConflict = await _db.Appointments
            .AsNoTracking()
            .AnyAsync(a => a.TenantId == tenantId &&
                           a.StaffId == staffId &&
                           a.Id != excludeAppointmentId &&
                           a.Status != AppointmentStatus.Cancelled &&
                           a.Status != AppointmentStatus.Rescheduled &&
                           a.StartAtUtc < bufferEnd &&
                           a.EndAtUtc > bufferStart,
                      cancellationToken);

        if (staffConflict) return false;

        // Check resources conflict
        if (service.ResourceRequirements.Count > 0)
        {
            var overlappingAppointments = await _db.Appointments
                .AsNoTracking()
                .Include(a => a.AppointmentResources)
                .Where(a => a.TenantId == tenantId &&
                            a.LocationId == locationId &&
                            a.Id != excludeAppointmentId &&
                            a.Status != AppointmentStatus.Cancelled &&
                            a.Status != AppointmentStatus.Rescheduled &&
                            a.StartAtUtc < endAtUtc &&
                            a.EndAtUtc > startAtUtc)
                .SelectMany(a => a.AppointmentResources.Select(ar => ar.ResourceId))
                .ToListAsync(cancellationToken);

            var busyResources = overlappingAppointments.ToHashSet();

            foreach (var req in service.ResourceRequirements)
            {
                var totalAvailableInGroup = await _db.Resources
                    .AsNoTracking()
                    .CountAsync(r => r.TenantId == tenantId &&
                                     r.LocationId == locationId &&
                                     r.ResourceGroupId == req.ResourceGroupId &&
                                     r.IsActive &&
                                     !busyResources.Contains(r.Id),
                                cancellationToken);

                if (totalAvailableInGroup < req.QuantityRequired)
                {
                    return false;
                }
            }
        }

        return true;
    }
}
