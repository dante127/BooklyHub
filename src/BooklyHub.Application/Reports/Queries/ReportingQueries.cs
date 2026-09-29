using BooklyHub.Application.Common.Interfaces;
using BooklyHub.Domain.Entities.Appointments;
using BooklyHub.Domain.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BooklyHub.Application.Reports.Queries;

public record DashboardReportDto(
    DateTime FromUtc,
    DateTime ToUtc,
    int TotalAppointments,
    int CompletedCount,
    int ConfirmedCount,
    int CancelledCount,
    int NoShowCount,
    decimal GrossRevenue,
    double CancellationRatePercent,
    double NoShowRatePercent,
    int UpcomingCount,
    int UpcomingPendingCount,
    int UpcomingConfirmedCount,
    DateTime? NextAppointmentAtUtc,
    IReadOnlyList<ServicePerformanceDto> TopServices,
    IReadOnlyList<StaffPerformanceDto> TopStaff);

public record ServicePerformanceDto(
    Guid ServiceId,
    string ServiceName,
    int BookingsCount,
    decimal Revenue);

public record StaffPerformanceDto(
    Guid StaffId,
    string StaffName,
    int CompletedBookings,
    double AverageRating);

public record GetDashboardReportQuery(
    Guid TenantId,
    DateTime FromUtc,
    DateTime ToUtc,
    DateTime NowUtc) : IRequest<DashboardReportDto>;

public class GetDashboardReportQueryHandler : IRequestHandler<GetDashboardReportQuery, DashboardReportDto>
{
    private readonly IApplicationDbContext _db;

    public GetDashboardReportQueryHandler(IApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<DashboardReportDto> Handle(GetDashboardReportQuery request, CancellationToken cancellationToken)
    {
        var apptQuery = _db.Appointments
            .AsNoTracking()
            .Where(a => a.TenantId == request.TenantId &&
                        a.StartAtUtc >= request.FromUtc &&
                        a.StartAtUtc <= request.ToUtc);

        // SQL aggregate counts by status
        var statusCounts = await apptQuery
            .GroupBy(a => a.Status)
            .Select(g => new { Status = g.Key, Count = g.Count(), Revenue = g.Sum(a => a.Price) })
            .ToListAsync(cancellationToken);

        var total = statusCounts.Sum(x => x.Count);
        var completed = statusCounts.FirstOrDefault(x => x.Status == AppointmentStatus.Completed)?.Count ?? 0;
        var confirmed = statusCounts.FirstOrDefault(x => x.Status == AppointmentStatus.Confirmed)?.Count ?? 0;
        var cancelled = statusCounts.FirstOrDefault(x => x.Status == AppointmentStatus.Cancelled)?.Count ?? 0;
        var noShow = statusCounts.FirstOrDefault(x => x.Status == AppointmentStatus.NoShow)?.Count ?? 0;

        var grossRevenue = statusCounts
            .Where(x => x.Status == AppointmentStatus.Completed || x.Status == AppointmentStatus.Confirmed)
            .Sum(x => x.Revenue);

        var cancellationRate = total > 0 ? Math.Round((double)cancelled / total * 100, 1) : 0.0;
        var noShowRate = total > 0 ? Math.Round((double)noShow / total * 100, 1) : 0.0;

        // Anchored on the clock instant rather than ToUtc: a caller asking about a closed period still needs
        // to know what is owed next, and both halves of the report must agree on what "now" is.
        var upcomingByStatus = await _db.Appointments
            .AsNoTracking()
            .Where(a => a.TenantId == request.TenantId &&
                        a.StartAtUtc > request.NowUtc &&
                        !AppointmentStatusSet.Closed.Contains(a.Status))
            .GroupBy(a => a.Status)
            .Select(g => new { Status = g.Key, Count = g.Count(), EarliestStart = g.Min(a => a.StartAtUtc) })
            .ToListAsync(cancellationToken);

        var upcomingCount = upcomingByStatus.Sum(x => x.Count);
        var upcomingPendingCount = upcomingByStatus.Where(x => x.Status == AppointmentStatus.Pending).Sum(x => x.Count);
        var upcomingConfirmedCount = upcomingByStatus.Where(x => x.Status == AppointmentStatus.Confirmed).Sum(x => x.Count);
        DateTime? nextAppointmentAtUtc = upcomingByStatus.Count == 0
            ? null
            : upcomingByStatus.Min(x => x.EarliestStart);

        // Top services aggregated in SQL by scalar ServiceId
        var topServicesRaw = await apptQuery
            .GroupBy(a => a.ServiceId)
            .Select(g => new
            {
                ServiceId = g.Key,
                BookingsCount = g.Count(),
                Revenue = g.Sum(a => a.Price)
            })
            .OrderByDescending(s => s.BookingsCount)
            .Take(5)
            .ToListAsync(cancellationToken);

        var serviceIds = topServicesRaw.Select(s => s.ServiceId).ToList();
        Dictionary<Guid, string> serviceNames = [];
        if (serviceIds.Count > 0)
        {
            serviceNames = await _db.Services
                .AsNoTracking()
                .Where(s => serviceIds.Contains(s.Id))
                .ToDictionaryAsync(s => s.Id, s => s.Name, cancellationToken);
        }

        var topServices = topServicesRaw.Select(s => new ServicePerformanceDto(
            s.ServiceId,
            serviceNames.TryGetValue(s.ServiceId, out var name) ? name : "Unknown Service",
            s.BookingsCount,
            s.Revenue)).ToList();

        // Staff completed bookings and average ratings
        var staffCompletedRaw = await apptQuery
            .Where(a => a.Status == AppointmentStatus.Completed)
            .GroupBy(a => a.StaffId)
            .Select(g => new
            {
                StaffId = g.Key,
                Completed = g.Count()
            })
            .OrderByDescending(s => s.Completed)
            .Take(5)
            .ToListAsync(cancellationToken);

        var staffIds = staffCompletedRaw.Select(s => s.StaffId).ToList();
        Dictionary<Guid, string> staffNames = [];
        Dictionary<Guid, double> staffRatings = [];
        if (staffIds.Count > 0)
        {
            staffNames = await _db.StaffMembers
                .AsNoTracking()
                .Where(s => staffIds.Contains(s.Id))
                .ToDictionaryAsync(s => s.Id, s => s.FirstName + " " + s.LastName, cancellationToken);

            staffRatings = await _db.Reviews
                .AsNoTracking()
                .Where(r => r.TenantId == request.TenantId && staffIds.Contains(r.StaffId))
                .GroupBy(r => r.StaffId)
                .Select(g => new { StaffId = g.Key, AvgRating = g.Average(r => r.Rating) })
                .ToDictionaryAsync(x => x.StaffId, x => x.AvgRating, cancellationToken);
        }

        var topStaff = staffCompletedRaw.Select(s => new StaffPerformanceDto(
            s.StaffId,
            staffNames.TryGetValue(s.StaffId, out var name) ? name : "Unknown Staff",
            s.Completed,
            staffRatings.TryGetValue(s.StaffId, out var rating) ? Math.Round(rating, 2) : 5.0)).ToList();

        return new DashboardReportDto(
            request.FromUtc,
            request.ToUtc,
            total,
            completed,
            confirmed,
            cancelled,
            noShow,
            grossRevenue,
            cancellationRate,
            noShowRate,
            upcomingCount,
            upcomingPendingCount,
            upcomingConfirmedCount,
            nextAppointmentAtUtc,
            topServices,
            topStaff);
    }
}
