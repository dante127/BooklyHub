using BooklyHub.Application.Common.Interfaces;
using BooklyHub.Application.Common.Models;
using BooklyHub.Application.Security;
using BooklyHub.Application.Tenancy.Commands;
using BooklyHub.Infrastructure.Security;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BooklyHub.Api.Controllers;

/// <summary>
/// ONB-01: a clinic and the account that owns it, over HTTP.
///
/// Before these routes the only writer of a <c>Tenant</c> row in <c>src</c> was <c>DatabaseSeeder</c>, and its demo
/// half was the only thing that ever wrote one — so a client's production database had zero tenants, and putting a
/// clinic into it meant running <c>INSERT</c> statements by hand. Both halves of that are one command here, because
/// a tenant nobody can sign into is the state this finding is about rather than a step toward the product.
///
/// <c>tenancy.manage</c> is the gate, and it is held by no tenant role (<c>Permissions.GetDefaultPermissionsForRole</c>
/// takes it off TenantOwner and TenantAdmin), which is the point: creating a clinic is a platform act, while staffing
/// one is the tenant's own, and those two needs different permissions. <c>PlatformAdmin</c> passes every permission
/// check anyway (<c>PermissionAuthorization.cs:22-26</c>), so in practice this is the platform administrator's file.
/// </summary>
[ApiController]
[Route("api/v1/tenants")]
[Authorize]
public class TenantsController : ControllerBase
{
    private readonly ISender _sender;
    private readonly IApplicationDbContext _db;

    public TenantsController(ISender sender, IApplicationDbContext db)
    {
        _sender = sender;
        _db = db;
    }

    public record ProvisionTenantRequest(
        string Name,
        string Slug,
        string TimeZoneId,
        string? Plan,
        string OwnerEmail,
        string OwnerPassword,
        string OwnerFirstName,
        string OwnerLastName,
        string? OwnerPhoneNumber);

    public record SetTenantStatusRequest(bool IsActive);

    [HttpPost]
    [HasPermission(Permissions.Tenancy.Manage)]
    public async Task<ActionResult<ProvisionedTenantDto>> Provision(
        [FromBody] ProvisionTenantRequest request,
        CancellationToken cancellationToken)
    {
        var created = await _sender.Send(
            new ProvisionTenantCommand(
                request.Name,
                request.Slug,
                request.TimeZoneId,
                request.Plan,
                request.OwnerEmail,
                request.OwnerPassword,
                request.OwnerFirstName,
                request.OwnerLastName,
                request.OwnerPhoneNumber),
            cancellationToken);

        return CreatedAtAction(nameof(GetTenants), routeValues: null, created);
    }

    [HttpGet]
    [HasPermission(Permissions.Tenancy.Manage)]
    public async Task<ActionResult> GetTenants(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        // No TenantGuard on purpose: this route stands outside every tenant and reads the whole table, which is
        // what makes it safe to gate on a permission no tenant can hold. Users carries no tenant query filter, and
        // Tenants is not an ITenantEntity either, so there is nothing here to scope and nothing that scopes it for
        // the caller by accident.
        var appliedPage = Paging.NormalizePage(page);
        var size = Paging.NormalizePageSize(pageSize);

        var query = _db.Tenants.AsNoTracking();

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderBy(t => t.Name).ThenBy(t => t.Id)
            .Skip(Paging.Offset(appliedPage, size))
            .Take(size)
            .Select(t => new
            {
                t.Id,
                t.Name,
                t.Slug,
                t.Plan,
                t.TimeZoneId,
                t.IsActive,
                CreatedAtUtc = t.CreatedAtUtc
            })
            .ToListAsync(cancellationToken);

        return Ok(new { total, page = appliedPage, pageSize = size, items });
    }

    /// <summary>
    /// The only way a live deployment can switch a clinic's public booking off. What it does not do is sign anybody
    /// out or stop that tenant's accounts signing in — the honest half of that decision is <c>SEC-03</c>, and it is
    /// still open.
    /// </summary>
    [HttpPut("{id:guid}/status")]
    [HasPermission(Permissions.Tenancy.Manage)]
    public async Task<ActionResult<TenantStatusDto>> SetStatus(
        Guid id,
        [FromBody] SetTenantStatusRequest request,
        CancellationToken cancellationToken)
    {
        return Ok(await _sender.Send(new SetTenantStatusCommand(id, request.IsActive), cancellationToken));
    }
}

/// <summary>
/// ONB-01: the accounts of the tenant the caller is standing in.
///
/// The tenant is never a parameter of these writes. <c>User</c> is not an <c>ITenantEntity</c>, so the save-time
/// cross-tenant rejection in <c>ApplicationDbContext.cs:152-169</c> does not run for it and no global filter narrows a
/// read of it: the explicit predicate below is the entire isolation of this table, and a body that could name a
/// tenant would be a body that could name somebody else's.
/// </summary>
[ApiController]
[Route("api/v1/users")]
[Authorize]
public class UsersController : ControllerBase
{
    private readonly ISender _sender;
    private readonly IApplicationDbContext _db;
    private readonly ITenantContext _tenantContext;

    public UsersController(ISender sender, IApplicationDbContext db, ITenantContext tenantContext)
    {
        _sender = sender;
        _db = db;
        _tenantContext = tenantContext;
    }

    public record CreateAccountRequest(
        string Email,
        string Password,
        string FirstName,
        string LastName,
        string? PhoneNumber,
        IReadOnlyList<string> Roles);

    public record SetAccountStatusRequest(bool IsActive);

    private Guid GetRequiredTenantId() => TenantGuard.RequireId(_tenantContext);

    [HttpGet]
    [HasPermission(Permissions.Users.Read)]
    public async Task<ActionResult> GetAccounts(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var tenantId = GetRequiredTenantId();
        var appliedPage = Paging.NormalizePage(page);
        var size = Paging.NormalizePageSize(pageSize);

        // The predicate, not a filter: `User` has no tenant query filter, so forgetting this line would be a roster
        // of every account on the server answering a manager's list.
        var query = _db.Users
            .AsNoTracking()
            .Where(u => u.TenantId == tenantId);

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            // `Email` is unique server-wide (IX_Users_Email, added with this route), so it is already a total order
            // and needs no id tiebreaker beneath it.
            .OrderBy(u => u.Email)
            .Skip(Paging.Offset(appliedPage, size))
            .Take(size)
            .Select(u => new
            {
                u.Id,
                u.Email,
                u.FirstName,
                u.LastName,
                u.PhoneNumber,
                u.IsActive,
                Roles = u.UserRoles.Select(ur => ur.Role!.Name).OrderBy(name => name).ToList()
            })
            .ToListAsync(cancellationToken);

        // Never the password hash, and never another tenant's row.
        return Ok(new { total, page = appliedPage, pageSize = size, items });
    }

    [HttpPost]
    [HasPermission(Permissions.Users.Manage)]
    public async Task<ActionResult<AccountDto>> CreateAccount(
        [FromBody] CreateAccountRequest request,
        CancellationToken cancellationToken)
    {
        var created = await _sender.Send(
            new CreateAccountCommand(
                GetRequiredTenantId(),
                request.Email,
                request.Password,
                request.FirstName,
                request.LastName,
                request.PhoneNumber,
                request.Roles),
            cancellationToken);

        return CreatedAtAction(nameof(GetAccounts), routeValues: null, created);
    }

    /// <summary>
    /// The only way to switch an account off, which is the write ACT-01 gave a meaning to: an account that is off can
    /// neither sign in nor refresh. Until this route, that rule had readers and no writer, so parting with a
    /// receptionist was an <c>UPDATE</c>.
    /// </summary>
    [HttpPut("{id:guid}/status")]
    [HasPermission(Permissions.Users.Manage)]
    public async Task<ActionResult<AccountStatusDto>> SetStatus(
        Guid id,
        [FromBody] SetAccountStatusRequest request,
        CancellationToken cancellationToken)
    {
        return Ok(await _sender.Send(
            new SetAccountStatusCommand(GetRequiredTenantId(), id, request.IsActive),
            cancellationToken));
    }
}

/// <summary>
/// ONB-01: the roles <c>POST /api/v1/users</c> will accept, read out of this deployment rather than out of a
/// document.
///
/// A caller needs this because the alternative is guessing a name and reading a refusal: the grantable set is a row
/// in <c>Roles</c> with <c>TenantId IS NULL</c> and <c>IsSystemRole</c>, and a database seeded by an older build
/// genuinely has fewer of them than the current code names — which is why this reads the table instead of answering
/// from <c>Roles.All</c>.
/// </summary>
[ApiController]
[Route("api/v1/roles")]
[Authorize]
public class RolesController : ControllerBase
{
    private readonly IApplicationDbContext _db;

    public RolesController(IApplicationDbContext db) => _db = db;

    [HttpGet]
    [HasPermission(Permissions.Users.Read)]
    public async Task<ActionResult> GetRoles(CancellationToken cancellationToken)
    {
        var roles = await _db.Roles
            .AsNoTracking()
            .Where(r => r.TenantId == null && r.IsSystemRole)
            .OrderBy(r => r.Name)
            .Select(r => new
            {
                name = r.Name,
                description = r.Description,
                // The route that creates an account refuses this one, so the read says so rather than letting a
                // client discover it as a 400 on a form it has already filled in.
                grantableToATenantAccount = r.Name != Roles.PlatformAdmin,
                permissions = r.RolePermissions.Select(rp => rp.PermissionId).OrderBy(p => p).ToList()
            })
            .ToListAsync(cancellationToken);

        // Not paged, and not a paging rule waiting for a number: the system roles are seven whichever tenant asks,
        // same shape as GET /api/v1/calendar/business-hours.
        return Ok(new { roles });
    }
}
