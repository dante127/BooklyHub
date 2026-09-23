using BooklyHub.Domain.Common;

namespace BooklyHub.Domain.Entities.Tenancy;

public class Tenant : AggregateRoot<Guid>, IAuditableEntity, ISoftDeletable
{
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string Plan { get; set; } = "Standard";
    public string TimeZoneId { get; set; } = "UTC";
    public bool IsActive { get; set; } = true;

    public TenantSetting? Settings { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public string? CreatedBy { get; set; }
    public DateTime? LastModifiedAtUtc { get; set; }
    public string? LastModifiedBy { get; set; }

    public bool IsDeleted { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
    public string? DeletedBy { get; set; }

    public Tenant()
    {
        Id = Guid.NewGuid();
    }

    public Tenant(Guid id, string name, string slug, string timeZoneId, string plan = "Standard")
    {
        Id = id;
        Name = name;
        Slug = slug.ToLowerInvariant().Trim();
        TimeZoneId = timeZoneId;
        Plan = plan;
        IsActive = true;
    }
}

public class TenantSetting : Entity<Guid>, ITenantEntity
{
    public Guid TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    public int MinBookingNoticeMinutes { get; set; } = 120; // 2 hours
    public int MaxAdvanceBookingDays { get; set; } = 60; // 60 days
    public int CancellationCutoffHours { get; set; } = 24; // 24 hours
    public int ReschedulingCutoffHours { get; set; } = 12; // 12 hours
    public int SlotIntervalMinutes { get; set; } = 15; // default grid step
    public bool ConfirmationEmailEnabled { get; set; } = true;
    public int ReminderNoticeHours { get; set; } = 24;
    public bool RequireUpfrontPayment { get; set; } = false;

    public TenantSetting()
    {
        Id = Guid.NewGuid();
    }

    public TenantSetting(Guid tenantId)
    {
        Id = Guid.NewGuid();
        TenantId = tenantId;
    }
}
