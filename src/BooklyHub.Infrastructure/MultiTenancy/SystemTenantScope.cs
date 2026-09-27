using BooklyHub.Application.Common.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace BooklyHub.Infrastructure.MultiTenancy;

/// <summary>
/// Host background work has no request principal, so its TenantContext starts unbound and the global
/// tenant filter hides every row it is about to read. A system scope binds the platform-admin identity
/// that cross-tenant sweeps (outbox delivery, appointment reminders) need. Request-path code must
/// never use this: it resolves tenants from the caller's credentials instead.
/// </summary>
public static class SystemTenantScope
{
    public static IServiceScope CreateSystemScope(this IServiceScopeFactory scopeFactory)
    {
        var scope = scopeFactory.CreateScope();

        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(Guid.Empty, isPlatformAdmin: true);

        return scope;
    }
}
