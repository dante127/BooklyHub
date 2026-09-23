using System.Security.Claims;
using BooklyHub.Application.Common.Interfaces;
using BooklyHub.Application.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace BooklyHub.Infrastructure.MultiTenancy;

public class TenantResolutionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<TenantResolutionMiddleware> _logger;

    public TenantResolutionMiddleware(RequestDelegate next, ILogger<TenantResolutionMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, ITenantContext tenantContext)
    {
        var user = context.User;
        var isPlatformAdmin = user.IsInRole(Roles.PlatformAdmin);

        if (user.Identity?.IsAuthenticated == true)
        {
            if (isPlatformAdmin)
            {
                // Platform admin can optionally view/target a specific tenant via header
                if (context.Request.Headers.TryGetValue("X-Tenant-Id", out var targetTenantHeader) &&
                    Guid.TryParse(targetTenantHeader, out var targetTenantId))
                {
                    tenantContext.SetTenant(targetTenantId, isPlatformAdmin: true);
                }
                else
                {
                    tenantContext.SetTenant(Guid.Empty, isPlatformAdmin: true);
                }
            }
            else
            {
                // Strict isolation: authenticated tenant user ALWAYS takes tenant_id from claims
                var tenantClaim = user.FindFirst("tenant_id")?.Value;
                if (!string.IsNullOrEmpty(tenantClaim) && Guid.TryParse(tenantClaim, out var claimTenantId))
                {
                    tenantContext.SetTenant(claimTenantId, isPlatformAdmin: false);
                }
                else
                {
                    _logger.LogWarning("Authenticated user {UserId} is missing tenant_id claim", user.FindFirst(ClaimTypes.NameIdentifier)?.Value);
                }
            }
        }
        else
        {
            // Anonymous/public endpoints (e.g. public booking portal, login):
            // Read from X-Tenant-Id or query parameter 'tenantId'
            if (context.Request.Headers.TryGetValue("X-Tenant-Id", out var headerTenantId) &&
                Guid.TryParse(headerTenantId, out var parsedTenantId))
            {
                tenantContext.SetTenant(parsedTenantId, isPlatformAdmin: false);
            }
            else if (context.Request.Query.TryGetValue("tenantId", out var queryTenantId) &&
                     Guid.TryParse(queryTenantId, out var parsedQueryTenantId))
            {
                tenantContext.SetTenant(parsedQueryTenantId, isPlatformAdmin: false);
            }
        }

        await _next(context);
    }
}
