using System.Linq.Expressions;
using BooklyHub.Application.Common.Interfaces;
using BooklyHub.Domain.Common;
using BooklyHub.Domain.Entities.System;
using BooklyHub.Infrastructure.MultiTenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BooklyHub.Infrastructure.BackgroundJobs;

public sealed record RetentionSweepResult(
    int IdempotencyRecords,
    int RefreshTokens,
    int OutboxMessages,
    int NotificationRecords);

/// <summary>
/// DB-04 / SEC-08: removes the operational rows that have stopped answering any question, on the horizons in
/// <see cref="RetentionPolicy"/>. The four append-only tables grow with the calendar and nothing else has ever
/// shrunk them, so this is the purge the <c>ExpiresAtUtc</c> index was designed for and the deadline the stored
/// response bodies were always supposed to have.
///
/// It is deliberately not a per-tenant pass. Every predicate is a column age, so a tenant that has been quiet for
/// a year is not owed a sweep, and the delete is platform-wide through the system scope the way the outbox read is.
/// Nor does it need a lock: unlike the dispatcher, there is nothing here that a second instance could do twice —
/// two instances deleting the same aged rows reach the same end state and the loser simply finds nothing. A
/// duplicated delivery is a bug; a duplicated purge is not.
///
/// What it does not touch, and why: <c>AppointmentStatusHistories</c> is the only accountability record of who moved
/// a booking and when, because the <c>AuditLogs</c> table built for that job is still dead schema; and soft-deleted
/// rows stay, because a timer that finalizes a patient's deletion would preempt the clinic's own decision to undo it.
/// Both are stated rather than left implied, since "no cleanup job exists" and "this row is kept on purpose" look
/// identical in the table.
/// </summary>
public class RetentionSweepBackgroundService : BackgroundService
{
    /// <summary>
    /// Rows per delete statement. One unbounded <c>DELETE</c> over a backlog that has never been purged would take
    /// the whole table in one lock and hold it for as long as it takes; a batch is a bounded lock, and a sweep that
    /// runs out of batches finishes the rest on the next tick.
    /// </summary>
    private const int DefaultBatchSize = 2_000;

    private const int MaxBatchesPerTable = 50;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RetentionSweepBackgroundService> _logger;
    private readonly TimeSpan _checkInterval = TimeSpan.FromHours(6);

    public RetentionSweepBackgroundService(
        IServiceScopeFactory scopeFactory,
        ILogger<RetentionSweepBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("RetentionSweepBackgroundService started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred executing the retention sweep.");
            }

            await Task.Delay(_checkInterval, stoppingToken);
        }

        _logger.LogInformation("RetentionSweepBackgroundService stopped.");
    }

    public async Task<RetentionSweepResult> SweepAsync(
        CancellationToken cancellationToken,
        int batchSize = DefaultBatchSize)
    {
        using var scope = _scopeFactory.CreateSystemScope();
        var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
        var nowUtc = scope.ServiceProvider.GetRequiredService<IClock>().UtcNow;

        var idempotency = await PurgeAgedAsync(
            db.IdempotencyRecords,
            r => r.ExpiresAtUtc <= nowUtc,
            r => r.Id,
            batchSize,
            cancellationToken);

        var tokens = await PurgeAgedAsync(
            db.RefreshTokens,
            r => r.ExpiresAtUtc <= nowUtc - RetentionPolicy.RefreshTokenRetentionAfterExpiry,
            r => r.Id,
            batchSize,
            cancellationToken);

        var outbox = await PurgeAgedAsync(
            db.OutboxMessages,
            m => m.ProcessedOnUtc != null && m.Error == null
                 && m.OccurredOnUtc <= nowUtc - RetentionPolicy.OutboxRetention,
            m => m.Id,
            batchSize,
            cancellationToken);

        var notifications = await PurgeAgedAsync(
            db.NotificationRecords,
            n => n.IsSent && n.SentAtUtc != null
                 && n.SentAtUtc <= nowUtc - RetentionPolicy.NotificationRetention,
            n => n.Id,
            batchSize,
            cancellationToken);

        _logger.LogInformation(
            "Retention sweep removed {Idempotency} idempotency records, {Tokens} expired refresh tokens, " +
            "{Outbox} delivered outbox messages and {Notifications} sent notifications as of {NowUtc}.",
            idempotency, tokens, outbox, notifications, nowUtc);

        return new RetentionSweepResult(idempotency, tokens, outbox, notifications);
    }

    /// <summary>
    /// Deletes the aged rows a batch at a time, oldest page first. The ids are read before the delete because
    /// <c>ExecuteDeleteAsync</c> cannot carry a <c>TOP</c>, and a purge that cannot bound its own statement is a
    /// maintenance job that turns into an outage the first time the backlog is real.
    /// </summary>
    private static async Task<int> PurgeAgedAsync<TEntity, TId>(
        IQueryable<TEntity> table,
        Expression<Func<TEntity, bool>> aged,
        Expression<Func<TEntity, TId>> keySelector,
        int batchSize,
        CancellationToken cancellationToken)
        where TEntity : Entity<TId>
    {
        var deleted = 0;

        for (var batch = 0; batch < MaxBatchesPerTable; batch++)
        {
            var ids = await table.Where(aged).Select(keySelector).Take(batchSize).ToListAsync(cancellationToken);
            if (ids.Count == 0) break;

            var pending = ids;
            var removed = await table.Where(e => pending.Contains(e.Id)).ExecuteDeleteAsync(cancellationToken);

            // A statement that matched nothing means somebody else is already removing this age range, or a row
            // moved out of it while the ids were being read. Either way the next read would return the same page,
            // so the batch bound is what stops here rather than the count.
            if (removed == 0) break;

            deleted += removed;
            if (ids.Count < batchSize) break;
        }

        return deleted;
    }
}
