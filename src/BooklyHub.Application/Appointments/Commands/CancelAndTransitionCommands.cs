using BooklyHub.Application.Appointments.Dtos;
using BooklyHub.Application.Common.Interfaces;
using BooklyHub.Domain.Enums;
using BooklyHub.Domain.Exceptions;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BooklyHub.Application.Appointments.Commands;

public record CancelAppointmentCommand(
    Guid TenantId,
    Guid AppointmentId,
    string Reason) : IRequest<bool>;

public class CancelAppointmentCommandValidator : AbstractValidator<CancelAppointmentCommand>
{
    public CancelAppointmentCommandValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.AppointmentId).NotEmpty();
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(500);
    }
}

public class CancelAppointmentCommandHandler : IRequestHandler<CancelAppointmentCommand, bool>
{
    private readonly IApplicationDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IClock _clock;

    public CancelAppointmentCommandHandler(IApplicationDbContext db, ICurrentUser currentUser, IClock clock)
    {
        _db = db;
        _currentUser = currentUser;
        _clock = clock;
    }

    public async Task<bool> Handle(CancelAppointmentCommand request, CancellationToken cancellationToken)
    {
        var tenant = await _db.Tenants
            .AsNoTracking()
            .Include(t => t.Settings)
            .FirstOrDefaultAsync(t => t.Id == request.TenantId, cancellationToken)
            ?? throw new NotFoundException($"Tenant with ID {request.TenantId} was not found.");

        var appointment = await _db.Appointments
            .FirstOrDefaultAsync(a => a.Id == request.AppointmentId && a.TenantId == request.TenantId, cancellationToken)
            ?? throw new NotFoundException($"Appointment with ID {request.AppointmentId} was not found.");

        // Check cancellation cutoff for customers/non-admin staff
        var isStaffOrAdmin = _currentUser.IsInRole("PlatformAdmin") || _currentUser.IsInRole("TenantOwner") || _currentUser.IsInRole("TenantAdmin") || _currentUser.IsInRole("Manager");
        if (!isStaffOrAdmin)
        {
            var cutoffHours = tenant.Settings?.CancellationCutoffHours ?? 24;
            if (appointment.StartAtUtc - _clock.UtcNow < TimeSpan.FromHours(cutoffHours))
            {
                throw new BusinessRuleValidationException("CancellationCutoffExceeded", $"Appointments cannot be cancelled within {cutoffHours} hours of the start time.");
            }
        }

        appointment.TransitionTo(AppointmentStatus.Cancelled, request.Reason, _currentUser.UserId?.ToString());

        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }
}

public record TransitionAppointmentStatusCommand(
    Guid TenantId,
    Guid AppointmentId,
    AppointmentStatus NewStatus,
    string? Reason = null) : IRequest<AppointmentDto>;

public class TransitionAppointmentStatusCommandHandler : IRequestHandler<TransitionAppointmentStatusCommand, AppointmentDto>
{
    private readonly IApplicationDbContext _db;
    private readonly ICurrentUser _currentUser;

    public TransitionAppointmentStatusCommandHandler(IApplicationDbContext db, ICurrentUser currentUser)
    {
        _db = db;
        _currentUser = currentUser;
    }

    public async Task<AppointmentDto> Handle(TransitionAppointmentStatusCommand request, CancellationToken cancellationToken)
    {
        var appointment = await _db.Appointments
            .Include(a => a.Location)
            .Include(a => a.Service)
            .Include(a => a.Staff)
            .Include(a => a.Customer)
            .Include(a => a.AppointmentResources)
            .FirstOrDefaultAsync(a => a.Id == request.AppointmentId && a.TenantId == request.TenantId, cancellationToken)
            ?? throw new NotFoundException($"Appointment with ID {request.AppointmentId} was not found.");

        appointment.TransitionTo(request.NewStatus, request.Reason, _currentUser.UserId?.ToString());

        await _db.SaveChangesAsync(cancellationToken);

        return new AppointmentDto(
            appointment.Id,
            appointment.TenantId,
            appointment.LocationId,
            appointment.Location?.Name ?? "",
            appointment.ServiceId,
            appointment.Service?.Name ?? "",
            appointment.StaffId,
            appointment.Staff?.FullName ?? "",
            appointment.CustomerId,
            appointment.Customer?.FullName ?? "",
            appointment.StartAtUtc,
            appointment.EndAtUtc,
            appointment.DurationMinutes,
            appointment.Price,
            appointment.Currency,
            appointment.Status,
            appointment.Notes,
            appointment.CancellationReason,
            appointment.AppointmentResources.Select(ar => ar.ResourceId).ToList(),
            appointment.CreatedAtUtc);
    }
}
