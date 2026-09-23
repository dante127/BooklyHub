namespace BooklyHub.Application.Common.Interfaces;

public interface ITenantContext
{
    Guid? TenantId { get; }
    bool IsPlatformAdmin { get; }
    bool HasTenant => TenantId.HasValue;
    void SetTenant(Guid tenantId, bool isPlatformAdmin = false);
}

public interface ICurrentUser
{
    Guid? UserId { get; }
    string? Email { get; }
    IReadOnlyList<string> Roles { get; }
    IReadOnlyList<string> Permissions { get; }
    bool IsAuthenticated { get; }
    bool HasPermission(string permission);
    bool IsInRole(string role);
}

public interface IClock
{
    DateTime UtcNow { get; }
}

public interface ICacheService
{
    Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default);
    Task SetAsync<T>(string key, T value, TimeSpan? expiration = null, CancellationToken cancellationToken = default);
    Task RemoveAsync(string key, CancellationToken cancellationToken = default);
    Task RemoveByPrefixAsync(string prefix, CancellationToken cancellationToken = default);
}

public interface IEmailSender
{
    Task SendEmailAsync(string to, string subject, string htmlBody, CancellationToken cancellationToken = default);
}

public interface ISmsSender
{
    Task SendSmsAsync(string phoneNumber, string message, CancellationToken cancellationToken = default);
}

public interface IPushNotificationSender
{
    Task SendPushAsync(string recipientId, string title, string message, CancellationToken cancellationToken = default);
}
