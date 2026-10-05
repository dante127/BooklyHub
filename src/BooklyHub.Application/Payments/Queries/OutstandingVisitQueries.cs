using BooklyHub.Application.Common.Interfaces;
using BooklyHub.Application.Common.Models;
using BooklyHub.Domain.Entities.Appointments;
using BooklyHub.Domain.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BooklyHub.Application.Payments.Queries;

/// <summary>
/// One overdue visit that still owes money, with the amount computed from the payment rows rather than from
/// a flag on the appointment. AmountDue and NetPaid are both reported so the desk can see the queue is
/// reading the ledger and not the price tag.
/// </summary>
public record OutstandingVisitDto(
    Guid AppointmentId,
    Guid CustomerId,
    string CustomerName,
    string ServiceName,
    string StaffName,
    DateTime EndAtUtc,
    decimal Price,
    decimal NetPaid,
    decimal AmountDue,
    string Currency,
    AppointmentStatus Status);

/// <summary>
/// The collection queue RPT-01 left open: visits the clinic is past its window on and still unpaid. The
/// sweep cannot close these (a balance is exactly what it refuses to write off) and the dashboard only counts
/// revenue, so without this surface the debt is real, provable from the rows, and invisible.
///
/// Ordered oldest-first because that is the order a clinic chases in, and paging on an unstable order would
/// show the same booking on two pages while hiding another.
/// </summary>
public record GetOutstandingVisitsQuery(
    Guid TenantId,
    DateTime NowUtc,
    int Page = 1,
    int PageSize = 20) : IRequest<PaginatedList<OutstandingVisitDto>>;

public class GetOutstandingVisitsQueryHandler : IRequestHandler<GetOutstandingVisitsQuery, PaginatedList<OutstandingVisitDto>>
{
    private readonly IApplicationDbContext _db;

    public GetOutstandingVisitsQueryHandler(IApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<PaginatedList<OutstandingVisitDto>> Handle(
        GetOutstandingVisitsQuery request,
        CancellationToken cancellationToken)
    {
        var page = Paging.NormalizePage(request.Page);
        var pageSize = Paging.NormalizePageSize(request.PageSize);

        // The same window the sweep closes on, so the two surfaces cannot disagree about when a visit became
        // overdue. No lookback bound: the sweep needs one because it writes, a queue needs none because a
        // debt from three months ago is still owed and dropping it from the list does not collect it.
        var deadline = NoShowClosurePolicy.DeadlineUtc(request.NowUtc);

        var query = _db.Appointments
            .AsNoTracking()
            .Where(a => a.TenantId == request.TenantId &&
                        AppointmentStatusSet.Collectable.Contains(a.Status) &&
                        a.EndAtUtc <= deadline)
            .WhereOwing(_db.Payments);

        var totalCount = await query.CountAsync(cancellationToken);

        var rows = await query
            .OrderBy(a => a.EndAtUtc)
            .ThenBy(a => a.Id)
            .Skip(Paging.Offset(page, pageSize))
            .Take(pageSize)
            .Select(a => new
            {
                a.Id,
                a.CustomerId,
                CustomerName = a.Customer!.FirstName + " " + a.Customer.LastName,
                ServiceName = a.Service!.Name,
                StaffName = a.Staff!.FirstName + " " + a.Staff.LastName,
                a.EndAtUtc,
                a.Price,
                a.Currency,
                a.Status
            })
            .ToListAsync(cancellationToken);

        // One payment read for the page, then the in-memory ledger fills in the amounts. Membership was
        // decided in SQL, the money shown here is decided by PaymentLedger - the same object the charge
        // path enforces - so the amount a caller is invited to collect is the amount it can actually charge.
        var appointmentIds = rows.Select(r => r.Id).ToList();

        var paymentsByAppointment = (await _db.Payments
                .AsNoTracking()
                .Include(p => p.Refunds)
                .Where(p => p.TenantId == request.TenantId && appointmentIds.Contains(p.AppointmentId))
                .ToListAsync(cancellationToken))
            .GroupBy(p => p.AppointmentId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var items = rows.Select(r =>
        {
            var ledger = PaymentLedger.From(
                paymentsByAppointment.TryGetValue(r.Id, out var payments) ? payments : []);

            return new OutstandingVisitDto(
                r.Id,
                r.CustomerId,
                r.CustomerName,
                r.ServiceName,
                r.StaffName,
                r.EndAtUtc,
                r.Price,
                ledger.Net,
                ledger.Outstanding(r.Price),
                r.Currency,
                r.Status);
        }).ToList();

        return new PaginatedList<OutstandingVisitDto>(items, totalCount, page, pageSize);
    }
}
