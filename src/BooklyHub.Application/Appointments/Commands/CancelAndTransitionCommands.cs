using BooklyHub.Application.Appointments.Dtos;
using BooklyHub.Application.Common.Interfaces;
using BooklyHub.Application.Payments.Commands;
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

        // The status change and the counter move commit or roll back together: an appointment whose
        // cancel saved without the decrement would inflate TotalBookings forever.
        return await _db.ExecuteInTransactionAsync(async () =>
        {
            var appointment = await _db.Appointments
                .FirstOrDefaultAsync(a => a.Id == request.AppointmentId && a.TenantId == request.TenantId, cancellationToken)
                ?? throw new NotFoundException($"Appointment with ID {request.AppointmentId} was not found.");

            // The tenant's cancellation cutoff, asked from the one policy object so the generic transition
            // to Cancelled cannot be used to sidestep it. Owners, admins and managers may override.
            AppointmentCutoffPolicy.EnsureCancellable(tenant.Settings, appointment.StartAtUtc, _clock.UtcNow, _currentUser);

            // TransitionTo returns the history row it appended; it must be registered as new, not discovered
            // through the navigation (see the domain method).
            _db.AppointmentStatusHistories.Add(
                appointment.TransitionTo(AppointmentStatus.Cancelled, _clock.UtcNow, request.Reason, _currentUser.UserId?.ToString()));

            // The booking was counted when it was created; cancelling gives the count back.
            await _db.AdjustTotalBookingsAsync(request.TenantId, appointment.CustomerId, -1, cancellationToken);

            await _db.SaveChangesAsync(cancellationToken);
            return true;
        }, cancellationToken);
    }
}

public record TransitionAppointmentStatusCommand(
    Guid TenantId,
    Guid AppointmentId,
    AppointmentStatus NewStatus,
    string? Reason = null) : IRequest<AppointmentDto>;

public class TransitionAppointmentStatusCommandValidator : AbstractValidator<TransitionAppointmentStatusCommand>
{
    public TransitionAppointmentStatusCommandValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.AppointmentId).NotEmpty();
        RuleFor(x => x.NewStatus).IsInEnum();
        RuleFor(x => x.Reason).MaximumLength(500);
    }
}

public class TransitionAppointmentStatusCommandHandler : IRequestHandler<TransitionAppointmentStatusCommand, AppointmentDto>
{
    private readonly IApplicationDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IClock _clock;

    public TransitionAppointmentStatusCommandHandler(IApplicationDbContext db, ICurrentUser currentUser, IClock clock)
    {
        _db = db;
        _currentUser = currentUser;
        _clock = clock;
    }

    public async Task<AppointmentDto> Handle(TransitionAppointmentStatusCommand request, CancellationToken cancellationToken)
    {
        // A transition to Cancelled is a cancel on this path too, so the counter moves in the same
        // transaction as the status change here as well.
        var appointment = await _db.ExecuteInTransactionAsync(async () =>
        {
            var loaded = await _db.Appointments
                .Include(a => a.Location)
                .Include(a => a.Service)
                .Include(a => a.Staff)
                .Include(a => a.Customer)
                .Include(a => a.AppointmentResources)
                .FirstOrDefaultAsync(a => a.Id == request.AppointmentId && a.TenantId == request.TenantId, cancellationToken)
                ?? throw new NotFoundException($"Appointment with ID {request.AppointmentId} was not found.");

            // Cancelling through this endpoint used to skip the tenant's cancellation cutoff, which made the
            // rule advisory: any actor holding appointments.update could cancel minutes before the start
            // time by choosing this URL over /cancel.
            if (request.NewStatus == AppointmentStatus.Cancelled)
            {
                var settings = await _db.Tenants
                    .AsNoTracking()
                    .Where(t => t.Id == request.TenantId)
                    .Select(t => t.Settings)
                    .FirstOrDefaultAsync(cancellationToken);

                AppointmentCutoffPolicy.EnsureCancellable(settings, loaded.StartAtUtc, _clock.UtcNow, _currentUser);
            }

            // TransitionTo returns the history row it appended; it must be registered as new, not discovered
            // through the navigation (see the domain method).
            _db.AppointmentStatusHistories.Add(
                loaded.TransitionTo(request.NewStatus, _clock.UtcNow, request.Reason, _currentUser.UserId?.ToString()));

            if (request.NewStatus == AppointmentStatus.Cancelled)
            {
                await _db.AdjustTotalBookingsAsync(request.TenantId, loaded.CustomerId, -1, cancellationToken);
            }

            await _db.SaveChangesAsync(cancellationToken);
            return loaded;
        }, cancellationToken);

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
