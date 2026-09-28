using BooklyHub.Domain.Enums;

namespace BooklyHub.Application.Payments;

public record ProcessPaymentRequest(
    Guid TenantId,
    Guid AppointmentId,
    decimal Amount,
    string Currency,
    string? PaymentMethodToken = null,
    string? IdempotencyKey = null);

public record PaymentResult(
    bool IsSuccess,
    string? TransactionId,
    PaymentStatus Status,
    string? ErrorMessage);

public record ProcessRefundRequest(
    Guid TenantId,
    Guid PaymentId,
    decimal Amount,
    string? Reason = null,
    string? IdempotencyKey = null);

public record RefundResult(
    bool IsSuccess,
    string? RefundId,
    PaymentStatus Status,
    string? ErrorMessage);

public interface IPaymentProvider
{
    PaymentProviderType ProviderType { get; }
    Task<PaymentResult> ProcessPaymentAsync(ProcessPaymentRequest request, CancellationToken cancellationToken = default);
    Task<RefundResult> ProcessRefundAsync(ProcessRefundRequest request, CancellationToken cancellationToken = default);
}

public interface IIdempotencyService
{
    Task<IdempotencyEntry?> GetEntryAsync(string key, CancellationToken cancellationToken = default);
    Task SaveEntryAsync(string key, Guid? tenantId, string requestHash, int statusCode, string responseBody, TimeSpan ttl, CancellationToken cancellationToken = default);
}

/// <summary>
/// A stored response plus the end of the window in which replaying it is still the honest answer.
/// <see cref="ExpiresAtUtc"/> has to travel with the entry: the cache is a copy of the record, and a copy
/// that carries no deadline turns the record's own window into a rule the database enforces and the cache
/// ignores.
/// </summary>
public record IdempotencyEntry(
    string Key,
    string RequestHash,
    int StatusCode,
    string ResponseBody,
    DateTime CreatedAtUtc,
    DateTime ExpiresAtUtc);
