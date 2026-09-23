using BooklyHub.Domain.Common;

namespace BooklyHub.Domain.Entities.Organizations;

public class Location : AggregateRoot<Guid>, ITenantEntity, IAuditableEntity, ISoftDeletable
{
    public Guid TenantId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string? State { get; set; }
    public string? PostalCode { get; set; }
    public string Country { get; set; } = "US";
    public string TimeZoneId { get; set; } = "UTC";
    public string? PhoneNumber { get; set; }
    public string? Email { get; set; }
    public bool IsActive { get; set; } = true;

    public ICollection<BusinessHour> BusinessHours { get; set; } = [];

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public string? CreatedBy { get; set; }
    public DateTime? LastModifiedAtUtc { get; set; }
    public string? LastModifiedBy { get; set; }

    public bool IsDeleted { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
    public string? DeletedBy { get; set; }

    public Location()
    {
        Id = Guid.NewGuid();
    }
}

public class BusinessHour : Entity<Guid>, ITenantEntity
{
    public Guid TenantId { get; set; }
    public Guid LocationId { get; set; }
    public Location? Location { get; set; }

    public DayOfWeek DayOfWeek { get; set; }
    public TimeSpan OpenTime { get; set; }
    public TimeSpan CloseTime { get; set; }
    public bool IsClosed { get; set; }

    public BusinessHour()
    {
        Id = Guid.NewGuid();
    }
}
