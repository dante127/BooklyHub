using BooklyHub.Application.Common.Interfaces;
using BooklyHub.Domain.Events;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BooklyHub.Application.Notifications;

public class AppointmentNotificationHandlers :
    INotificationHandler<AppointmentCreatedNotification>,
    INotificationHandler<AppointmentCancelledNotification>,
    INotificationHandler<AppointmentRescheduledNotification>
{
    private readonly IApplicationDbContext _db;
    private readonly IEmailSender _emailSender;
    private readonly ISmsSender _smsSender;
    private readonly ILogger<AppointmentNotificationHandlers> _logger;

    public AppointmentNotificationHandlers(
        IApplicationDbContext db,
        IEmailSender emailSender,
        ISmsSender smsSender,
        ILogger<AppointmentNotificationHandlers> logger)
    {
        _db = db;
        _emailSender = emailSender;
        _smsSender = smsSender;
        _logger = logger;
    }

    public async Task Handle(AppointmentCreatedNotification notification, CancellationToken cancellationToken)
    {
        var e = notification.Event;
        var customer = await _db.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == e.CustomerId, cancellationToken);
        if (customer == null) return;

        var service = await _db.Services.AsNoTracking().FirstOrDefaultAsync(s => s.Id == e.ServiceId, cancellationToken);

        var subject = $"Appointment Confirmation - {service?.Name ?? "Service"}";
        var body = $"<p>Dear {customer.FirstName},</p><p>Your appointment has been booked for {e.StartAtUtc:f} UTC.</p>";

        await _emailSender.SendEmailAsync(customer.Email, subject, body, cancellationToken);

        if (!string.IsNullOrEmpty(customer.PhoneNumber))
        {
            await _smsSender.SendSmsAsync(customer.PhoneNumber, $"Your appointment is confirmed for {e.StartAtUtc:g} UTC.", cancellationToken);
        }
    }

    public async Task Handle(AppointmentCancelledNotification notification, CancellationToken cancellationToken)
    {
        var e = notification.Event;
        var customer = await _db.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == e.CustomerId, cancellationToken);
        if (customer == null) return;

        var subject = "Appointment Cancelled";
        var body = $"<p>Dear {customer.FirstName},</p><p>Your appointment has been cancelled. Reason: {e.Reason}</p>";

        await _emailSender.SendEmailAsync(customer.Email, subject, body, cancellationToken);
    }

    public async Task Handle(AppointmentRescheduledNotification notification, CancellationToken cancellationToken)
    {
        var e = notification.Event;
        var customer = await _db.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == e.CustomerId, cancellationToken);
        if (customer == null) return;

        var subject = "Appointment Rescheduled";
        var body = $"<p>Dear {customer.FirstName},</p><p>Your appointment has been rescheduled to {e.NewStartAtUtc:f} UTC.</p>";

        await _emailSender.SendEmailAsync(customer.Email, subject, body, cancellationToken);
    }
}

public record AppointmentCreatedNotification(AppointmentCreatedEvent Event) : INotification;
public record AppointmentCancelledNotification(AppointmentCancelledEvent Event) : INotification;
public record AppointmentRescheduledNotification(AppointmentRescheduledEvent Event) : INotification;
