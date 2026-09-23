using BooklyHub.Domain.Common;
using BooklyHub.Domain.Entities.Resources;
using BooklyHub.Domain.Entities.StaffMembers;

namespace BooklyHub.Domain.Entities.Services;

public class ServiceCategory : AggregateRoot<Guid>, ITenantEntity, IAuditableEntity
{
    public Guid TenantId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int DisplayOrder { get; set; }

    public ICollection<Service> Services { get; set; } = [];

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public string? CreatedBy { get; set; }
    public DateTime? LastModifiedAtUtc { get; set; }
    public string? LastModifiedBy { get; set; }

    public ServiceCategory()
    {
        Id = Guid.NewGuid();
    }
}

public class Service : AggregateRoot<Guid>, ITenantEntity, IAuditableEntity, ISoftDeletable
{
    public Guid TenantId { get; set; }
    public Guid? CategoryId { get; set; }
    public ServiceCategory? Category { get; set; }

    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int DurationMinutes { get; set; } = 30;
    public decimal Price { get; set; } = 0.00m;
    public string Currency { get; set; } = "USD";
    public int BufferBeforeMinutes { get; set; } = 0;
    public int BufferAfterMinutes { get; set; } = 0;
    public bool RequiresAllResources { get; set; } = false;
    public bool IsActive { get; set; } = true;

    public int TotalDurationWithBuffers => BufferBeforeMinutes + DurationMinutes + BufferAfterMinutes;

    public ICollection<StaffService> StaffServices { get; set; } = [];
    public ICollection<ServiceResourceRequirement> ResourceRequirements { get; set; } = [];

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public string? CreatedBy { get; set; }
    public DateTime? LastModifiedAtUtc { get; set; }
    public string? LastModifiedBy { get; set; }

    public bool IsDeleted { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
    public string? DeletedBy { get; set; }

    public Service()
    {
        Id = Guid.NewGuid();
    }
}
