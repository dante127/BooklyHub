using BooklyHub.Domain.Common;

namespace BooklyHub.Domain.Entities.Identity;

public class User : AggregateRoot<Guid>, IAuditableEntity, ISoftDeletable
{
    public Guid? TenantId { get; set; } // Nullable for PlatformAdmin
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string? PhoneNumber { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime? LastLoginAtUtc { get; set; }

    public string FullName => $"{FirstName} {LastName}".Trim();

    public ICollection<UserRole> UserRoles { get; set; } = [];
    public ICollection<RefreshToken> RefreshTokens { get; set; } = [];

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public string? CreatedBy { get; set; }
    public DateTime? LastModifiedAtUtc { get; set; }
    public string? LastModifiedBy { get; set; }

    public bool IsDeleted { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
    public string? DeletedBy { get; set; }

    public User()
    {
        Id = Guid.NewGuid();
    }
}

public class Role : Entity<Guid>
{
    public Guid? TenantId { get; set; } // Null for system/global roles
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool IsSystemRole { get; set; }

    public ICollection<UserRole> UserRoles { get; set; } = [];
    public ICollection<RolePermission> RolePermissions { get; set; } = [];

    public Role()
    {
        Id = Guid.NewGuid();
    }

    public Role(string name, string description, bool isSystemRole = false, Guid? tenantId = null)
    {
        Id = Guid.NewGuid();
        Name = name;
        Description = description;
        IsSystemRole = isSystemRole;
        TenantId = tenantId;
    }
}

public class Permission : Entity<string>
{
    public string Category { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    public ICollection<RolePermission> RolePermissions { get; set; } = [];

    public Permission() { }

    public Permission(string id, string category, string description)
    {
        Id = id;
        Category = category;
        Description = description;
    }
}

public class UserRole
{
    public Guid UserId { get; set; }
    public User? User { get; set; }

    public Guid RoleId { get; set; }
    public Role? Role { get; set; }

    public Guid? TenantId { get; set; }
}

public class RolePermission
{
    public Guid RoleId { get; set; }
    public Role? Role { get; set; }

    public string PermissionId { get; set; } = string.Empty;
    public Permission? Permission { get; set; }
}

public class RefreshToken : Entity<Guid>
{
    public Guid UserId { get; set; }
    public User? User { get; set; }

    public string Token { get; set; } = string.Empty;
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? RevokedAtUtc { get; set; }
    public string? ReplacedByToken { get; set; }

    public bool IsExpired => DateTime.UtcNow >= ExpiresAtUtc;
    public bool IsRevoked => RevokedAtUtc != null;
    public bool IsActive => !IsRevoked && !IsExpired;

    public RefreshToken()
    {
        Id = Guid.NewGuid();
    }
}
