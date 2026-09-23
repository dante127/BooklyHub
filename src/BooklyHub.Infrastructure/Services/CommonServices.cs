using BooklyHub.Application.Common.Interfaces;

namespace BooklyHub.Infrastructure.Services;

public class TenantContext : ITenantContext
{
    public Guid? TenantId { get; private set; }
    public bool IsPlatformAdmin { get; private set; }

    public void SetTenant(Guid tenantId, bool isPlatformAdmin = false)
    {
        TenantId = tenantId;
        IsPlatformAdmin = isPlatformAdmin;
    }
}

public class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}
