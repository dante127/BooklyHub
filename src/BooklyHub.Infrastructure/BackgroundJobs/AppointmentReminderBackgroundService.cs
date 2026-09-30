using BooklyHub.Application.Common.Interfaces;
using BooklyHub.Domain.Entities.System;
using BooklyHub.Domain.Entities.Tenancy;
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

        // How much notice a reminder is worth is a tenant's decision, recorded on its own settings row, and
        // this sweep serves every tenant in one pass - so the window has to come from the row, not from the
        // worker. A single hardcoded window is wrong in both directions at once, and wrong permanently:
        // because a reminder sends at most once per appointment, a clinic configured for two hours is reminded
        // a full day early and never reminded at the hour it asked for, while a clinic configured for
        // forty-eight is never reached before its visit begins.
        //
        // The join is a left join and the fallback is TenantSetting's own default, because a tenant with no
        // settings row is still a tenant with appointments. TenantSettings.TenantId is unique, so this join
        // cannot duplicate a candidate.
        var candidates = await (
            from a in db.Appointments.AsNoTracking()
            from s in db.TenantSettings.AsNoTracking()
                .Where(s => s.TenantId == a.TenantId)
                .DefaultIfEmpty()
            where a.Status == AppointmentStatus.Confirmed &&
                  a.Customer != null &&
                  a.StartAtUtc >= now &&
                  a.StartAtUtc <= now.AddHours(s == null
                      ? TenantSetting.DefaultReminderNoticeHours
                      : s.ReminderNoticeHours)
            orderby a.StartAtUtc
            select new ReminderCandidate(
                a.Id,
                a.TenantId,
                a.StartAtUtc,
                a.Customer!.Email,
                a.Customer.FirstName,
                a.Service != null ? a.Service.Name : null))
            .ToListAsync(cancellationToken);

        if (candidates.Count == 0) return 0;

        var remindersSent = 0;

        foreach (var appt in candidates)
        {
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
                var subject = $"Appointment Reminder - {appt.ServiceName ?? "Service"}";
                var body = $"<p>Dear {appt.CustomerFirstName},</p><p>This is a reminder for your upcoming appointment on {appt.StartAtUtc:f} UTC.</p>";

                await emailSender.SendEmailAsync(appt.CustomerEmail, subject, body, cancellationToken);

                db.NotificationRecords.Add(new NotificationRecord
                {
                    Id = Guid.NewGuid(),
                    TenantId = appt.TenantId,
                    AppointmentId = appt.Id,
                    Recipient = appt.CustomerEmail,
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

    /// <summary>
    /// One appointment the sweep may remind about, with the few customer fields the message needs. Projected
    /// rather than loaded because the sweep now reaches across to the tenant's settings row, and an
    /// <c>Include</c> cannot ride on that join - which is the same reason it reads no navigation properties.
    /// </summary>
    private sealed record ReminderCandidate(
        Guid Id,
        Guid TenantId,
        DateTime StartAtUtc,
        string CustomerEmail,
        string CustomerFirstName,
        string? ServiceName);
}
