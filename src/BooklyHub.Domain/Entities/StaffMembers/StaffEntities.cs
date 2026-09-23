using BooklyHub.Domain.Common;
using BooklyHub.Domain.Entities.Organizations;
using BooklyHub.Domain.Entities.Resources;
using BooklyHub.Domain.Entities.Scheduling;

namespace BooklyHub.Domain.Entities.StaffMembers;

public class Staff : AggregateRoot<Guid>, ITenantEntity, IAuditableEntity, ISoftDeletable
{
    public Guid TenantId { get; set; }
    public Guid LocationId { get; set; }
    public Location? Location { get; set; }

    public Guid? UserId { get; set; } // Optional link to User account

    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? PhoneNumber { get; set; }
    public string? Title { get; set; }
    public string? Bio { get; set; }
    public string ColorHex { get; set; } = "#3B82F6";
    public bool IsActive { get; set; } = true;

    public string FullName => $"{FirstName} {LastName}".Trim();

    public ICollection<StaffService> StaffServices { get; set; } = [];
    public ICollection<WorkingHour> WorkingHours { get; set; } = [];
    public ICollection<AvailabilityException> AvailabilityExceptions { get; set; } = [];

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public string? CreatedBy { get; set; }
    public DateTime? LastModifiedAtUtc { get; set; }
    public string? LastModifiedBy { get; set; }

    public bool IsDeleted { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
    public string? DeletedBy { get; set; }

    public Staff()
    {
        Id = Guid.NewGuid();
    }
}

public class StaffService : ITenantEntity
{
    public Guid TenantId { get; set; }
    public Guid StaffId { get; set; }
    public Staff? Staff { get; set; }

    public Guid ServiceId { get; set; }
    public Services.Service? Service { get; set; }

    public int? CustomDurationMinutes { get; set; }
    public decimal? CustomPrice { get; set; }
}
