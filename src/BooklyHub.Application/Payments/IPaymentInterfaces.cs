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
    Task<bool> HasKeyAsync(string key, CancellationToken cancellationToken = default);
    Task<IdempotencyEntry?> GetEntryAsync(string key, CancellationToken cancellationToken = default);
    Task SaveEntryAsync(string key, Guid? tenantId, string requestHash, int statusCode, string responseBody, TimeSpan ttl, CancellationToken cancellationToken = default);
}

public record IdempotencyEntry(
    string Key,
    string RequestHash,
    int StatusCode,
    string ResponseBody,
    DateTime CreatedAtUtc);
