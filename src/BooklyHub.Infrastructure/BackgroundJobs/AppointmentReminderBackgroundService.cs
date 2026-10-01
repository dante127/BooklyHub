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
    // A pass with no ceiling grows with the platform: it loads every confirmed appointment inside every
    // tenant's window and holds them while it emails. 200 per tenant per tick, nearest start first, so a
    // clinic with a backlog is reminded of what is coming soonest and the rest drains a page at a time over
    // the following ticks. The ceiling is silent by nature, which is why a test binds it.
    private const int BatchSize = 200;

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

    /// <summary>
    /// One pass per tenant, each judged against that tenant's own notice window. The tenant list is read once
    /// and the appointments are then fetched per tenant, because a single cross-tenant query can only carry
    /// one window, and the window is not the sweep's to choose.
    /// </summary>
    /// <param name="tenantFilter">Restricts the pass to one tenant. Exists because a sweep that can only run
    /// for the whole platform can only be tested against the whole platform.</param>
    public async Task<int> ProcessUpcomingRemindersAsync(
        CancellationToken cancellationToken,
        Guid? tenantFilter = null)
    {
        List<ReminderWindow> tenants;
        DateTime now;

        using (var read = _scopeFactory.CreateSystemScope())
        {
            var db = read.ServiceProvider.GetRequiredService<IApplicationDbContext>();
            var clock = read.ServiceProvider.GetRequiredService<IClock>();

            // One clock read for the tick, so no appointment is judged against a different "now" than its
            // neighbours, and the stamps every reminder carries are the instant the pass was taken.
            now = clock.UtcNow;

            // How much notice a reminder is worth is each tenant's own decision, recorded on its settings row.
            // A hardcoded window is wrong in both directions at once, and wrong permanently, because a
            // reminder sends at most once per appointment: a clinic configured for two hours is reminded a day
            // early and reaches the hour it asked for with that reminder already spent, while a clinic
            // configured for forty-eight is never reached before its visit. The fallback is TenantSetting's
            // own default, because a tenant with no settings row is still a tenant with appointments — and a
            // tenant that quietly stops being reminded is invisible from the outside.
            tenants = await (
                from t in db.Tenants.AsNoTracking()
                from s in db.TenantSettings.AsNoTracking()
                    .Where(s => s.TenantId == t.Id)
                    .DefaultIfEmpty()
                where t.IsActive && (tenantFilter == null || t.Id == tenantFilter)
                select new ReminderWindow(
                    t.Id,
                    s == null ? TenantSetting.DefaultReminderNoticeHours : s.ReminderNoticeHours))
                .ToListAsync(cancellationToken);
        }

        var sent = 0;

        foreach (var tenant in tenants)
        {
            sent += await SweepTenantAsync(tenant, now, cancellationToken);
        }

        return sent;
    }

    private async Task<int> SweepTenantAsync(
        ReminderWindow tenant,
        DateTime now,
        CancellationToken cancellationToken)
    {
        // A fresh context per tenant. A reminder record the store refused stays Added in the change tracker,
        // and the next save on that context would write it again beside an unrelated appointment's row.
        using var scope = _scopeFactory.CreateSystemScope();
        var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
        var emailSender = scope.ServiceProvider.GetRequiredService<IEmailSender>();

        var windowEnd = now.AddHours(tenant.NoticeHours);

        // The system scope holds the tenant filter open on purpose, so this predicate is the only thing that
        // keeps one clinic's pass out of another clinic's patients.
        //
        // The reminder check is part of the candidate query rather than a filter applied to its results,
        // because the query is capped: a page taken first and deduplicated afterwards is a page whose spent
        // rows never leave. A clinic with more in-window bookings than one batch would re-read its already
        // reminded head every tick and never reach the bookings behind it, silently and permanently. Here the
        // cap is taken over unreminded bookings only, so a backlog drains one batch per tick and the ceiling
        // costs nobody their reminder.
        var candidates = await db.Appointments
            .AsNoTracking()
            .Where(a => a.TenantId == tenant.TenantId &&
                        a.Status == AppointmentStatus.Confirmed &&
                        a.Customer != null &&
                        a.StartAtUtc >= now &&
                        a.StartAtUtc <= windowEnd &&
                        !db.NotificationRecords.Any(n => n.TenantId == a.TenantId &&
                                                         n.AppointmentId == a.Id &&
                                                         n.Subject.Contains("Reminder")))
            .OrderBy(a => a.StartAtUtc)
            .Take(BatchSize)
            .Select(a => new ReminderCandidate(
                a.Id,
                a.TenantId,
                a.StartAtUtc,
                a.Customer!.Email,
                a.Customer.FirstName,
                a.Service != null ? a.Service.Name : null))
            .ToListAsync(cancellationToken);

        if (candidates.Count == 0) return 0;

        var sent = 0;

        foreach (var appt in candidates)
        {
            var subject = $"Appointment Reminder - {appt.ServiceName ?? "Service"}";
            var body = $"<p>Dear {appt.CustomerFirstName},</p><p>This is a reminder for your upcoming appointment on {appt.StartAtUtc:f} UTC.</p>";

            try
            {
                await emailSender.SendEmailAsync(appt.CustomerEmail, subject, body, cancellationToken);
            }
            catch (Exception ex)
            {
                // Nothing is written, so the next tick tries this appointment again. A reminder that never
                // left is recoverable; one that left without being written down is the duplicate.
                _logger.LogError(ex, "Failed to send reminder for appointment {AppointmentId}", appt.Id);
                continue;
            }

            // Recorded as soon as it left, not at the end of the pass. One save for the whole tick meant that
            // if it failed, every email already sent became unrecorded and the next tick re-sends all of them.
            // At-least-once is the deliberate direction: a duplicate reminder is the cheaper mistake.
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
                SentAtUtc = now,
                CreatedAtUtc = now
            });

            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                // This row is now both sent and unrecorded, and every appointment behind it would be the same.
                // Ending the tenant's pass is what keeps one store failure from becoming a stream of
                // duplicates; the rest of the window is picked up on the next tick.
                _logger.LogError(ex,
                    "Reminder for appointment {AppointmentId} was sent but could not be recorded; ending the " +
                    "reminder pass for tenant {TenantId}.", appt.Id, tenant.TenantId);
                break;
            }

            sent++;
        }

        return sent;
    }

    /// <summary>
    /// A tenant and the notice it asked for, resolved once for the tick.
    /// </summary>
    private sealed record ReminderWindow(Guid TenantId, int NoticeHours);

    /// <summary>
    /// One appointment the sweep may remind about, with the few customer fields the message needs. Projected
    /// rather than loaded because the message reads four scalar fields and nothing else.
    /// </summary>
    private sealed record ReminderCandidate(
        Guid Id,
        Guid TenantId,
        DateTime StartAtUtc,
        string CustomerEmail,
        string CustomerFirstName,
        string? ServiceName);
}
