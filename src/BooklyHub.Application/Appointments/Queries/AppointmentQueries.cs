using BooklyHub.Application.Appointments.Dtos;
using BooklyHub.Application.Common.Interfaces;
using BooklyHub.Application.Common.Models;
using BooklyHub.Domain.Enums;
using BooklyHub.Domain.Exceptions;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BooklyHub.Application.Appointments.Queries;

public record GetAppointmentByIdQuery(Guid TenantId, Guid AppointmentId) : IRequest<AppointmentDto>;

public class GetAppointmentByIdQueryHandler : IRequestHandler<GetAppointmentByIdQuery, AppointmentDto>
{
    private readonly IApplicationDbContext _db;

    public GetAppointmentByIdQueryHandler(IApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<AppointmentDto> Handle(GetAppointmentByIdQuery request, CancellationToken cancellationToken)
    {
        var appointment = await _db.Appointments
            .AsNoTracking()
            .Where(a => a.Id == request.AppointmentId && a.TenantId == request.TenantId)
            .Select(a => new AppointmentDto(
                a.Id,
                a.TenantId,
                a.LocationId,
                a.Location!.Name,
                a.ServiceId,
                a.Service!.Name,
                a.StaffId,
                a.Staff!.FirstName + " " + a.Staff.LastName,
                a.CustomerId,
                a.Customer!.FirstName + " " + a.Customer.LastName,
                a.StartAtUtc,
                a.EndAtUtc,
                a.DurationMinutes,
                a.Price,
                a.Currency,
                a.Status,
                a.Notes,
                a.CancellationReason,
                a.AppointmentResources.Select(ar => ar.ResourceId).ToList(),
                a.CreatedAtUtc))
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException($"Appointment with ID {request.AppointmentId} was not found.");

        return appointment;
    }
}

public record SearchAppointmentsQuery(
    Guid TenantId,
    Guid? LocationId = null,
    Guid? StaffId = null,
    Guid? CustomerId = null,
    AppointmentStatus? Status = null,
    DateTime? FromUtc = null,
    DateTime? ToUtc = null,
    int Page = 1,
    int PageSize = 20) : IRequest<PaginatedList<AppointmentDto>>;

public class SearchAppointmentsQueryHandler : IRequestHandler<SearchAppointmentsQuery, PaginatedList<AppointmentDto>>
{
    private readonly IApplicationDbContext _db;

    public SearchAppointmentsQueryHandler(IApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<PaginatedList<AppointmentDto>> Handle(SearchAppointmentsQuery request, CancellationToken cancellationToken)
    {
        var page = request.Page < 1 ? 1 : request.Page;
        var pageSize = request.PageSize is < 1 or > 100 ? 20 : request.PageSize;

        var query = _db.Appointments
            .AsNoTracking()
            .Where(a => a.TenantId == request.TenantId);

        if (request.LocationId.HasValue)
        {
            query = query.Where(a => a.LocationId == request.LocationId.Value);
        }

        if (request.StaffId.HasValue)
        {
            query = query.Where(a => a.StaffId == request.StaffId.Value);
        }

        if (request.CustomerId.HasValue)
        {
            query = query.Where(a => a.CustomerId == request.CustomerId.Value);
        }

        if (request.Status.HasValue)
        {
            query = query.Where(a => a.Status == request.Status.Value);
        }

        if (request.FromUtc.HasValue)
        {
            query = query.Where(a => a.StartAtUtc >= request.FromUtc.Value);
        }

        if (request.ToUtc.HasValue)
        {
            query = query.Where(a => a.StartAtUtc <= request.ToUtc.Value);
        }

        var totalCount = await query.CountAsync(cancellationToken);

        var items = await query
            .OrderByDescending(a => a.StartAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(a => new AppointmentDto(
                a.Id,
                a.TenantId,
                a.LocationId,
                a.Location!.Name,
                a.ServiceId,
                a.Service!.Name,
                a.StaffId,
                a.Staff!.FirstName + " " + a.Staff.LastName,
                a.CustomerId,
                a.Customer!.FirstName + " " + a.Customer.LastName,
                a.StartAtUtc,
                a.EndAtUtc,
                a.DurationMinutes,
                a.Price,
                a.Currency,
                a.Status,
                a.Notes,
                a.CancellationReason,
                a.AppointmentResources.Select(ar => ar.ResourceId).ToList(),
                a.CreatedAtUtc))
            .ToListAsync(cancellationToken);

        return new PaginatedList<AppointmentDto>(items, totalCount, page, pageSize);
    }
}
