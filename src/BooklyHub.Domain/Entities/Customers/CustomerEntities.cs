using BooklyHub.Domain.Common;

namespace BooklyHub.Domain.Entities.Customers;

public class Customer : AggregateRoot<Guid>, ITenantEntity, IAuditableEntity, ISoftDeletable
{
    public Guid TenantId { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? PhoneNumber { get; set; }
    public string? Notes { get; set; }
    public bool IsBlocked { get; set; }

    public int TotalBookings { get; set; }
    public decimal TotalSpent { get; set; }

    public string FullName => $"{FirstName} {LastName}".Trim();

    public ICollection<CustomerNote> CustomerNotes { get; set; } = [];

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public string? CreatedBy { get; set; }
    public DateTime? LastModifiedAtUtc { get; set; }
    public string? LastModifiedBy { get; set; }

    public bool IsDeleted { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
    public string? DeletedBy { get; set; }

    public Customer()
    {
        Id = Guid.NewGuid();
    }
}

public class CustomerNote : Entity<Guid>, ITenantEntity
{
    public Guid TenantId { get; set; }
    public Guid CustomerId { get; set; }
    public Customer? Customer { get; set; }

    public Guid? AuthorUserId { get; set; }
    public string NoteText { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public CustomerNote()
    {
        Id = Guid.NewGuid();
    }
}
