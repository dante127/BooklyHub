using BooklyHub.Application.Common.Interfaces;
using BooklyHub.Application.Payments;
using BooklyHub.Domain.Entities.System;
using BooklyHub.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BooklyHub.Infrastructure.Payments;

public class SimulatedPaymentProvider : IPaymentProvider
{
    private readonly ILogger<SimulatedPaymentProvider> _logger;

    public SimulatedPaymentProvider(ILogger<SimulatedPaymentProvider> logger)
    {
        _logger = logger;
    }

    public PaymentProviderType ProviderType => PaymentProviderType.Simulated;

    public Task<PaymentResult> ProcessPaymentAsync(ProcessPaymentRequest request, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Processing simulated payment of {Amount} {Currency} for appointment {AppointmentId}", 
            request.Amount, request.Currency, request.AppointmentId);

        // Always succeed for valid positive amounts in simulated mode
        if (request.Amount <= 0)
        {
            return Task.FromResult(new PaymentResult(false, null, PaymentStatus.Failed, "Invalid payment amount."));
        }

        var transactionId = $"txn_sim_{Guid.NewGuid():N}";
        return Task.FromResult(new PaymentResult(true, transactionId, PaymentStatus.Paid, null));
    }

    public Task<RefundResult> ProcessRefundAsync(ProcessRefundRequest request, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Processing simulated refund of {Amount} for payment {PaymentId}", 
            request.Amount, request.PaymentId);

        if (request.Amount <= 0)
        {
            return Task.FromResult(new RefundResult(false, null, PaymentStatus.Failed, "Invalid refund amount."));
        }

        var refundId = $"re_sim_{Guid.NewGuid():N}";
        return Task.FromResult(new RefundResult(true, refundId, PaymentStatus.Refunded, null));
    }
}

public class IdempotencyService : IIdempotencyService
{
    private readonly IApplicationDbContext _db;
    private readonly ICacheService _cache;
    private readonly IClock _clock;
    private readonly ILogger<IdempotencyService> _logger;

    public IdempotencyService(
        IApplicationDbContext db,
        ICacheService cache,
        IClock clock,
        ILogger<IdempotencyService> logger)
    {
        _db = db;
        _cache = cache;
        _clock = clock;
        _logger = logger;
    }

    public async Task<IdempotencyEntry?> GetEntryAsync(string key, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;

        // KEY-02: the row is addressed by a digest, so the collation has no case left to fold and the cache and
        // the store are handed the same identity — which they were not, because an in-memory cache key is
        // ordinal while SQL's equality was case-insensitive.
        var storageId = IdempotencyIdentity.StorageId(key);
        var cacheKey = CacheKey(storageId);
        var cached = await _cache.GetAsync<IdempotencyEntry>(cacheKey, cancellationToken);

        // The cache holds a copy of the record, so a copy must die when the record does. Checking the window
        // only in SQL left the cache as the one path that replayed a key whose window had closed — and it
        // could do so for hours, because the cached copy carried no deadline to check itself against.
        if (cached is not null)
        {
            if (cached.ExpiresAtUtc > _clock.UtcNow) return cached;
            await _cache.RemoveAsync(cacheKey, cancellationToken);
        }

        var nowUtc = _clock.UtcNow;
        var record = await _db.IdempotencyRecords
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == storageId && r.ExpiresAtUtc > nowUtc, cancellationToken);

        if (record == null) return null;

        var entry = new IdempotencyEntry(
            record.Id,
            record.RequestHash,
            record.ResponseStatusCode,
            record.ResponseBody,
            record.CreatedAtUtc,
            record.ExpiresAtUtc);

        // The copy lives for whatever the record has left, never for a duration of its own choosing: a fixed
        // TTL would outlive the window for any key read close to the end of it.
        await _cache.SetAsync(cacheKey, entry, record.ExpiresAtUtc - nowUtc, cancellationToken);
        return entry;
    }

    public async Task SaveEntryAsync(
        string key,
        Guid? tenantId,
        string requestHash,
        int statusCode,
        string responseBody,
        TimeSpan ttl,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key)) return;

        var storageId = IdempotencyIdentity.StorageId(key);
        var nowUtc = _clock.UtcNow;
        var record = new IdempotencyRecord(storageId, tenantId, requestHash, statusCode, responseBody, ttl, nowUtc);
        _db.IdempotencyRecords.Add(record);

        try
        {
            // An expired row is no longer a promise, but it is still a row carrying this key's primary key:
            // the read above had stopped serving it while the write below would collide with it. So every retry
            // that arrived after the window closed re-ran the request and then could not store its own answer —
            // the replay guarantee silently stopped working for exactly the keys that had been used before.
            // Guarded on expiry because a row that is still inside its window is somebody else's promise: two
            // concurrent first-time requests with one key must not have the loser's response overwrite the
            // winner's, and deleting only the dead row is what leaves the live one to be replayed.
            await _db.IdempotencyRecords
                .Where(r => r.Id == storageId && r.ExpiresAtUtc <= nowUtc)
                .ExecuteDeleteAsync(cancellationToken);

            await _db.SaveChangesAsync(cancellationToken);

            await _cache.SetAsync(
                CacheKey(storageId),
                new IdempotencyEntry(storageId, requestHash, statusCode, responseBody, nowUtc, record.ExpiresAtUtc),
                ttl,
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist idempotency key {Key} (stored as {StorageId})", key, storageId);
        }
    }

    private static string CacheKey(string key) => $"idemp:{key}";
}
