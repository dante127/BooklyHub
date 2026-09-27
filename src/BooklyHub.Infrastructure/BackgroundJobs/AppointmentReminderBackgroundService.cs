using BooklyHub.Application.Common.Interfaces;
using BooklyHub.Domain.Entities.System;
using BooklyHub.Domain.Enums;
using BooklyHub.Infrastructure.MultiTenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BooklyHub.Infrastructure.BackgroundJobs;

public class AppointmentReminderBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AppointmentReminderBackgroundService> _logger;
    private readonly TimeSpan _checkInterval = TimeSpan.FromMinutes(10);

    public AppointmentReminderBackgroundService(
        IServiceScopeFactory scopeFactory,
        ILogger<AppointmentReminderBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("AppointmentReminderBackgroundService started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessUpcomingRemindersAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred executing appointment reminders loop.");
            }

            await Task.Delay(_checkInterval, stoppingToken);
        }

        _logger.LogInformation("AppointmentReminderBackgroundService stopped.");
    }

    public async Task<int> ProcessUpcomingRemindersAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateSystemScope();
        var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
        var emailSender = scope.ServiceProvider.GetRequiredService<IEmailSender>();
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();

        var now = clock.UtcNow;
        var reminderWindowEnd = now.AddHours(24);

        // Fetch upcoming confirmed appointments in next 24h
        var upcomingAppointments = await db.Appointments
            .AsNoTracking()
            .Include(a => a.Customer)
            .Include(a => a.Service)
            .Where(a => a.Status == AppointmentStatus.Confirmed &&
                        a.StartAtUtc >= now &&
                        a.StartAtUtc <= reminderWindowEnd)
            .ToListAsync(cancellationToken);

        if (upcomingAppointments.Count == 0) return 0;

        var remindersSent = 0;

        foreach (var appt in upcomingAppointments)
        {
            if (appt.Customer == null) continue;

            // Idempotency check: has reminder already been sent for this appointment?
            var alreadySent = await db.NotificationRecords
                .AsNoTracking()
                .AnyAsync(n => n.TenantId == appt.TenantId &&
                               n.AppointmentId == appt.Id &&
                               n.Subject.Contains("Reminder"),
                          cancellationToken);

            if (alreadySent) continue;

            try
            {
                var subject = $"Appointment Reminder - {appt.Service?.Name ?? "Service"}";
                var body = $"<p>Dear {appt.Customer.FirstName},</p><p>This is a reminder for your upcoming appointment on {appt.StartAtUtc:f} UTC.</p>";

                await emailSender.SendEmailAsync(appt.Customer.Email, subject, body, cancellationToken);

                db.NotificationRecords.Add(new NotificationRecord
                {
                    Id = Guid.NewGuid(),
                    TenantId = appt.TenantId,
                    AppointmentId = appt.Id,
                    Recipient = appt.Customer.Email,
                    Channel = "Email",
                    Subject = subject,
                    Body = body,
                    IsSent = true,
                    SentAtUtc = clock.UtcNow,
                    CreatedAtUtc = clock.UtcNow
                });

                remindersSent++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send reminder for appointment {AppointmentId}", appt.Id);
            }
        }

        await db.SaveChangesAsync(cancellationToken);

        return remindersSent;
    }
}
