using BooklyHub.Domain.Common;
using BooklyHub.Domain.Enums;

namespace BooklyHub.Domain.Entities.System;

public class OutboxMessage : Entity<Guid>
{
    public DateTime OccurredOnUtc { get; set; } = DateTime.UtcNow;
    public string Type { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public DateTime? ProcessedOnUtc { get; set; }
    public string? Error { get; set; }
    public int RetryCount { get; set; }
    public DateTime? NextRetryTimeUtc { get; set; }

    public OutboxMessage()
    {
        Id = Guid.NewGuid();
    }

    public OutboxMessage(string type, string content)
    {
        Id = Guid.NewGuid();
        Type = type;
        Content = content;
        OccurredOnUtc = DateTime.UtcNow;
    }
}

public class AuditLog : Entity<Guid>
{
    public Guid? TenantId { get; set; }
    public Guid? UserId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public string? CorrelationId { get; set; }
    public string? BeforeJson { get; set; }
    public string? AfterJson { get; set; }

    public AuditLog()
    {
        Id = Guid.NewGuid();
    }
}

public class IdempotencyRecord : Entity<string>
{
    public Guid? TenantId { get; set; }
    public string RequestHash { get; set; } = string.Empty;
    public int ResponseStatusCode { get; set; }
    public string ResponseBody { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAtUtc { get; set; }

    public IdempotencyRecord() { }

    /// <summary>
    /// The window is opened from the clock the caller was given, not from the wall clock, so a replay can be
    /// tested against a deadline instead of against whatever time the machine happens to think it is. One
    /// reading anchors both ends, so <paramref name="ttl"/> is measured from the same instant
    /// <see cref="CreatedAtUtc"/> claims.
    /// </summary>
    public IdempotencyRecord(string key, Guid? tenantId, string requestHash, int responseStatusCode, string responseBody, TimeSpan ttl, DateTime nowUtc)
    {
        Id = key;
        TenantId = tenantId;
        RequestHash = requestHash;
        ResponseStatusCode = responseStatusCode;
        ResponseBody = responseBody;
        CreatedAtUtc = nowUtc;
        ExpiresAtUtc = nowUtc.Add(ttl);
    }
}

public class NotificationRecord : Entity<Guid>, ITenantEntity
{
    public Guid TenantId { get; set; }
    public Guid? AppointmentId { get; set; }
    public string Recipient { get; set; } = string.Empty;
    public string Channel { get; set; } = "Email"; // Email, SMS, Push
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public bool IsSent { get; set; }
    public DateTime? SentAtUtc { get; set; }
    public string? Error { get; set; }
    public int RetryCount { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public NotificationRecord()
    {
        Id = Guid.NewGuid();
    }
}
