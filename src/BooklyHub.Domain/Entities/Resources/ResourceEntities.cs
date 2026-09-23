using BooklyHub.Domain.Common;
using BooklyHub.Domain.Entities.Appointments;
using BooklyHub.Domain.Entities.Organizations;
using BooklyHub.Domain.Entities.Services;

namespace BooklyHub.Domain.Entities.Resources;

public class ResourceGroup : AggregateRoot<Guid>, ITenantEntity, IAuditableEntity
{
    public Guid TenantId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    public ICollection<Resource> Resources { get; set; } = [];

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public string? CreatedBy { get; set; }
    public DateTime? LastModifiedAtUtc { get; set; }
    public string? LastModifiedBy { get; set; }

    public ResourceGroup()
    {
        Id = Guid.NewGuid();
    }
}

public class Resource : AggregateRoot<Guid>, ITenantEntity, IAuditableEntity, ISoftDeletable
{
    public Guid TenantId { get; set; }
    public Guid ResourceGroupId { get; set; }
    public ResourceGroup? ResourceGroup { get; set; }

    public Guid LocationId { get; set; }
    public Location? Location { get; set; }

    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public string? CreatedBy { get; set; }
    public DateTime? LastModifiedAtUtc { get; set; }
    public string? LastModifiedBy { get; set; }

    public bool IsDeleted { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
    public string? DeletedBy { get; set; }

    public Resource()
    {
        Id = Guid.NewGuid();
    }
}

public class ServiceResourceRequirement : ITenantEntity
{
    public Guid TenantId { get; set; }
    public Guid ServiceId { get; set; }
    public Service? Service { get; set; }

    public Guid ResourceGroupId { get; set; }
    public ResourceGroup? ResourceGroup { get; set; }

    public int QuantityRequired { get; set; } = 1;
}

public class AppointmentResource : ITenantEntity
{
    public Guid TenantId { get; set; }
    public Guid AppointmentId { get; set; }
    public Appointment? Appointment { get; set; }

    public Guid ResourceId { get; set; }
    public Resource? Resource { get; set; }
}
