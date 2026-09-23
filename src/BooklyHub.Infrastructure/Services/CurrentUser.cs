using System.Security.Claims;
using BooklyHub.Application.Common.Interfaces;
using Microsoft.AspNetCore.Http;

namespace BooklyHub.Infrastructure.Services;

public class CurrentUser : ICurrentUser
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public CurrentUser(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    private ClaimsPrincipal? User => _httpContextAccessor.HttpContext?.User;

    public Guid? UserId
    {
        get
        {
            var idClaim = User?.FindFirst(ClaimTypes.NameIdentifier)?.Value 
                          ?? User?.FindFirst("sub")?.Value 
                          ?? User?.FindFirst("uid")?.Value;

            return Guid.TryParse(idClaim, out var id) ? id : null;
        }
    }

    public string? Email => User?.FindFirst(ClaimTypes.Email)?.Value ?? User?.FindFirst("email")?.Value;

    public IReadOnlyList<string> Roles => User?.FindAll(ClaimTypes.Role).Select(c => c.Value).Distinct().ToList() 
                                         ?? User?.FindAll("role").Select(c => c.Value).Distinct().ToList() 
                                         ?? (IReadOnlyList<string>)Array.Empty<string>();

    public IReadOnlyList<string> Permissions => User?.FindAll("permission").Select(c => c.Value).Distinct().ToList() 
                                               ?? (IReadOnlyList<string>)Array.Empty<string>();

    public bool IsAuthenticated => User?.Identity?.IsAuthenticated ?? false;

    public bool HasPermission(string permission)
    {
        return Permissions.Contains(permission, StringComparer.OrdinalIgnoreCase);
    }

    public bool IsInRole(string role)
    {
        return Roles.Contains(role, StringComparer.OrdinalIgnoreCase);
    }
}
