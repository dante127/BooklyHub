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
    private readonly ILogger<IdempotencyService> _logger;

    public IdempotencyService(
        IApplicationDbContext db,
        ICacheService cache,
        ILogger<IdempotencyService> logger)
    {
        _db = db;
        _cache = cache;
        _logger = logger;
    }

    public async Task<bool> HasKeyAsync(string key, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;

        var cached = await _cache.GetAsync<IdempotencyEntry>($"idemp:{key}", cancellationToken);
        if (cached != null) return true;

        return await _db.IdempotencyRecords
            .AsNoTracking()
            .AnyAsync(r => r.Id == key && r.ExpiresAtUtc > DateTime.UtcNow, cancellationToken);
    }

    public async Task<IdempotencyEntry?> GetEntryAsync(string key, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;

        var cached = await _cache.GetAsync<IdempotencyEntry>($"idemp:{key}", cancellationToken);
        if (cached != null) return cached;

        var record = await _db.IdempotencyRecords
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == key && r.ExpiresAtUtc > DateTime.UtcNow, cancellationToken);

        if (record == null) return null;

        var entry = new IdempotencyEntry(
            record.Id,
            record.RequestHash,
            record.ResponseStatusCode,
            record.ResponseBody,
            record.CreatedAtUtc);

        await _cache.SetAsync($"idemp:{key}", entry, TimeSpan.FromHours(24), cancellationToken);
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

        var record = new IdempotencyRecord(key, tenantId, requestHash, statusCode, responseBody, ttl);
        _db.IdempotencyRecords.Add(record);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);

            var entry = new IdempotencyEntry(key, requestHash, statusCode, responseBody, DateTime.UtcNow);
            await _cache.SetAsync($"idemp:{key}", entry, ttl, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist idempotency key {Key}", key);
        }
    }
}
