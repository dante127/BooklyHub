using BooklyHub.Application.Common.Interfaces;
using BooklyHub.Application.Payments;
using BooklyHub.Domain.Entities.Appointments;
using BooklyHub.Domain.Enums;
using BooklyHub.Infrastructure.MultiTenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BooklyHub.Infrastructure.BackgroundJobs;

public sealed record NoShowSweepResult(
    int Closed,
    int SkippedWithBalance,
    int SkippedLockedTenant,
    int SkippedContendedTenant);

/// <summary>
/// Closes bookings that were confirmed, never attended and are now past their visit window. Without this
/// sweep an unattended booking stays Confirmed forever, and a Confirmed row reads as a visit that happened.
/// </summary>
public class AppointmentNoShowBackgroundService : BackgroundService
{
    // The actor is recorded, not guessed at: a history row saying nobody is a row nobody can trace.
    private const string SweepActor = "system:no-show-sweep";
    private const string ClosureReason = "Not attended; closed automatically after the visit window.";
    private const int BatchSize = 200;

    /// <summary>
    /// How many times one tenant's closure batch is re-judged after the store says a row moved under it.
    /// Every conflict takes that row out of the next read, so three in a row is a busy front desk rather
    /// than a livelock; past that the tenant defers to the next tick instead of the sweep spinning while
    /// every tenant behind it waits.
    /// </summary>
    private const int MaxConflictAttempts = 3;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AppointmentNoShowBackgroundService> _logger;
    private readonly TimeSpan _checkInterval = TimeSpan.FromMinutes(15);

    public AppointmentNoShowBackgroundService(
        IServiceScopeFactory scopeFactory,
        ILogger<AppointmentNoShowBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("AppointmentNoShowBackgroundService started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CloseUnattendedAppointmentsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred executing the no-show closure sweep.");
            }

            await Task.Delay(_checkInterval, stoppingToken);
        }

        _logger.LogInformation("AppointmentNoShowBackgroundService stopped.");
    }

    public async Task<NoShowSweepResult> CloseUnattendedAppointmentsAsync(
        CancellationToken cancellationToken,
        Guid? tenantFilter = null)
    {
        List<Guid> tenantIds;
        DateTime nowUtc;

        using (var read = _scopeFactory.CreateSystemScope())
        {
            var db = read.ServiceProvider.GetRequiredService<IApplicationDbContext>();
            var clock = read.ServiceProvider.GetRequiredService<IClock>();

            // One clock read for the whole tick: the deadline, the lookback and every tenant's window have
            // to be the same instant, or a booking could be closed by two different "now"s.
            nowUtc = clock.UtcNow;

            tenantIds = await db.Tenants
                .AsNoTracking()
                .Where(t => t.IsActive && (tenantFilter == null || t.Id == tenantFilter))
                .Select(t => t.Id)
                .ToListAsync(cancellationToken);
        }

        var closed = 0;
        var skippedWithBalance = 0;
        var skippedLocked = 0;
        var skippedContended = 0;

        foreach (var tenantId in tenantIds)
        {
            var (tenantClosed, tenantSkipped, lockHeld, contention) =
                await SweepTenantAsync(tenantId, nowUtc, cancellationToken);

            closed += tenantClosed;
            skippedWithBalance += tenantSkipped;
            if (!lockHeld) skippedLocked++;
            if (contention) skippedContended++;
        }

        if (closed > 0 || skippedWithBalance > 0 || skippedContended > 0)
        {
            _logger.LogInformation(
                "No-show closure sweep: {Closed} closed, {Skipped} left with a balance, {Locked} tenants busy, " +
                "{Contended} tenants deferred by concurrent edits.",
                closed, skippedWithBalance, skippedLocked, skippedContended);
        }

        return new NoShowSweepResult(closed, skippedWithBalance, skippedLocked, skippedContended);
    }

    private async Task<(int Closed, int SkippedWithBalance, bool LockHeld, bool DeferredByContention)> SweepTenantAsync(
        Guid tenantId,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                // A fresh context per attempt. A rejected SaveChanges leaves the whole batch dirty in the
                // change tracker, so re-running on the same context would re-send the writes the store just
                // refused instead of re-reading the state the conflicting writer left.
                using var scope = _scopeFactory.CreateSystemScope();
                var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();

                return await SweepBatchAsync(db, tenantId, nowUtc, cancellationToken);
            }
            catch (DbUpdateConcurrencyException ex)
            {
                var moved = ex.Entries
                    .Select(entry => entry.Entity)
                    .OfType<Appointment>()
                    .Select(a => a.Id)
                    .ToList();

                if (attempt >= MaxConflictAttempts)
                {
                    _logger.LogWarning(
                        "No-show closures for tenant {TenantId} were re-judged {Attempts} times and appointments " +
                        "{AppointmentIds} were still moving under the sweep; deferring the tenant to the next tick.",
                        tenantId, attempt, string.Join(", ", moved));

                    return (0, 0, true, true);
                }

                _logger.LogInformation(
                    "No-show sweep for tenant {TenantId} lost a row to a concurrent edit ({AppointmentIds}); " +
                    "the tenant's batch was rolled back and is being re-judged.",
                    tenantId, string.Join(", ", moved));
            }
        }
    }

    private async Task<(int Closed, int SkippedWithBalance, bool LockHeld, bool DeferredByContention)> SweepBatchAsync(
        IApplicationDbContext db,
        Guid tenantId,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var deadlineUtc = NoShowClosurePolicy.DeadlineUtc(nowUtc);
        var earliestEndUtc = NoShowClosurePolicy.EarliestEndUtc(nowUtc);

        var closed = 0;
        var skippedWithBalance = 0;
        var lockHeld = true;

        // One transaction per tenant, with the lock held inside it: the candidate read, the money read and
        // the status write have to describe the same instant, or a payment that lands in between would be
        // judged against a ledger that no longer exists.
        await db.ExecuteInTransactionAsync(async () =>
        {
            if (!await db.TryAcquireNoShowSweepLockAsync(tenantId, cancellationToken))
            {
                lockHeld = false;
                return;
            }

            // Confirmed is the only status this closes: Pending was never accepted, and CheckedIn or
            // InProgress mean the customer appeared.
            var candidates = await db.Appointments
                .Where(a => a.TenantId == tenantId &&
                            a.Status == AppointmentStatus.Confirmed &&
                            a.EndAtUtc <= deadlineUtc &&
                            a.EndAtUtc >= earliestEndUtc)
                .OrderBy(a => a.EndAtUtc)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);

            if (candidates.Count == 0) return;

            var candidateIds = candidates.Select(a => a.Id).ToList();

            var payments = await db.Payments
                .AsNoTracking()
                .Include(p => p.Refunds)
                .Where(p => p.TenantId == tenantId && candidateIds.Contains(p.AppointmentId))
                .ToListAsync(cancellationToken);

            // The same ledger the charge path reads, so the sweep cannot decide a balance is settled by a
            // rule the money code disagrees with.
            var ledgers = payments
                .GroupBy(p => p.AppointmentId)
                .ToDictionary(g => g.Key, g => PaymentLedger.From(g));

            foreach (var appointment in candidates)
            {
                var outstanding = ledgers.TryGetValue(appointment.Id, out var ledger)
                    ? ledger.Outstanding(appointment.Price)
                    : appointment.Price;

                if (outstanding > 0m)
                {
                    // A NoShow cannot be charged at all, so closing a booking that still owes money would
                    // quietly write the balance off. That is somebody's decision, not a timer's.
                    skippedWithBalance++;
                    continue;
                }

                // TransitionTo returns the history row it appended, and it must be registered as new rather
                // than discovered through the navigation.
                db.AppointmentStatusHistories.Add(
                    appointment.TransitionTo(AppointmentStatus.NoShow, nowUtc, ClosureReason, SweepActor));

                closed++;
            }

            if (closed > 0)
            {
                // The appointments carry a rowversion, so this throws rather than overwriting a status a
                // staff member wrote after the candidate read. The batch is one transaction, so the whole
                // tenant rolls back with it and SweepTenantAsync re-judges it; an attended visit is never
                // restated as an absence, and never silently costs the rest of the tick either.
                await db.SaveChangesAsync(cancellationToken);
            }
        }, cancellationToken);

        return (closed, skippedWithBalance, lockHeld, false);
    }
}
