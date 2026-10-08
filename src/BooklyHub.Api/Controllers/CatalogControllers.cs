using BooklyHub.Application.Common.Interfaces;
using BooklyHub.Application.Common.Models;
using BooklyHub.Application.Security;
using BooklyHub.Domain.Entities.Customers;
using BooklyHub.Domain.Entities.Services;
using BooklyHub.Domain.Entities.StaffMembers;
using BooklyHub.Infrastructure.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BooklyHub.Api.Controllers;

[ApiController]
[Route("api/v1/services")]
public class ServicesController : ControllerBase
{
    private readonly IApplicationDbContext _db;
    private readonly ITenantContext _tenantContext;

    public ServicesController(IApplicationDbContext db, ITenantContext tenantContext)
    {
        _db = db;
        _tenantContext = tenantContext;
    }

    [HttpGet]
    [AllowAnonymous]
    public async Task<ActionResult> GetServices(
        [FromQuery] Guid? categoryId,
        [FromQuery(Name = "page")] int requestedPage = 1,
        [FromQuery(Name = "pageSize")] int requestedPageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var tenantId = TenantGuard.RequireId(_tenantContext);

        var query = _db.Services
            .AsNoTracking()
            .Where(s => s.TenantId == tenantId && s.IsActive);

        if (categoryId.HasValue) query = query.Where(s => s.CategoryId == categoryId.Value);

        var page = Paging.NormalizePage(requestedPage);
        var size = Paging.NormalizePageSize(requestedPageSize);

        var total = await query.CountAsync(cancellationToken);
        var services = await query
            // ThenBy(Id) because OrderBy(Name) alone is not a total order: two services with the same name page
            // unpredictably, and SQL Server gives no promise about ties.
            .OrderBy(s => s.Name)
            .ThenBy(s => s.Id)
            .Skip(Paging.Offset(page, size))
            .Take(size)
            .Select(s => new
            {
                s.Id,
                s.Name,
                s.Description,
                s.DurationMinutes,
                s.Price,
                s.Currency,
                s.BufferBeforeMinutes,
                s.BufferAfterMinutes,
                CategoryName = s.Category != null ? s.Category.Name : null
            })
            .ToListAsync(cancellationToken);

        return Ok(new { total, page, pageSize = size, items = services });
    }
}

[ApiController]
[Route("api/v1/staff")]
public class StaffController : ControllerBase
{
    private readonly IApplicationDbContext _db;
    private readonly ITenantContext _tenantContext;
    private readonly IAuthorizationService _authorization;

    public StaffController(IApplicationDbContext db, ITenantContext tenantContext, IAuthorizationService authorization)
    {
        _db = db;
        _tenantContext = tenantContext;
        _authorization = authorization;
    }

    [HttpGet]
    [AllowAnonymous]
    public async Task<ActionResult> GetStaff(
        [FromQuery] Guid? locationId,
        [FromQuery] Guid? serviceId,
        [FromQuery(Name = "page")] int requestedPage = 1,
        [FromQuery(Name = "pageSize")] int requestedPageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var tenantId = TenantGuard.RequireId(_tenantContext);

        var query = _db.StaffMembers
            .AsNoTracking()
            .Where(s => s.TenantId == tenantId && s.IsActive);

        if (locationId.HasValue) query = query.Where(s => s.LocationId == locationId.Value);
        if (serviceId.HasValue) query = query.Where(s => s.StaffServices.Any(ss => ss.ServiceId == serviceId.Value));

        var page = Paging.NormalizePage(requestedPage);
        var size = Paging.NormalizePageSize(requestedPageSize);

        // API-08: which caller may read a staff member's contact details. The same requirement the authorized
        // staff routes are gated by, evaluated here instead of on the action, because this route has to answer
        // anonymously — the booking portal's staff picker is one of its callers and needs no token.
        var mayReadContact = (await _authorization.AuthorizeAsync(
            User, resource: null, new PermissionRequirement(Permissions.Staff.Read))).Succeeded;

        var total = await query.CountAsync(cancellationToken);
        var staff = await query
            .OrderBy(s => s.LastName)
            .ThenBy(s => s.FirstName)
            .ThenBy(s => s.Id)
            .Skip(Paging.Offset(page, size))
            .Take(size)
            .Select(s => new
            {
                s.Id,
                s.FirstName,
                s.LastName,
                s.Title,
                s.Bio,
                Email = mayReadContact ? s.Email : null,
                PhoneNumber = mayReadContact ? s.PhoneNumber : null,
                s.ColorHex,
                s.LocationId,
                OfferedServices = s.StaffServices.Select(ss => new
                {
                    ss.ServiceId,
                    ServiceName = ss.Service!.Name,
                    CustomDuration = ss.CustomDurationMinutes,
                    CustomPrice = ss.CustomPrice
                }).ToList()
            })
            .ToListAsync(cancellationToken);

        return Ok(new { total, page, pageSize = size, items = staff });
    }
}

[ApiController]
[Route("api/v1/customers")]
[Authorize]
public class CustomersController : ControllerBase
{
    private readonly IApplicationDbContext _db;
    private readonly ITenantContext _tenantContext;

    public CustomersController(IApplicationDbContext db, ITenantContext tenantContext)
    {
        _db = db;
        _tenantContext = tenantContext;
    }

    public record CreateCustomerRequest(string FirstName, string LastName, string Email, string? PhoneNumber = null, string? Notes = null);

    /// <summary>
    /// The longest <c>search</c> term this route can find anything with. The three columns the term is matched
    /// against hold at most 256 characters (first and last name are 100, email is 256), so a longer term is not
    /// in any row no matter what it says — and sending it to the server anyway is not free: past 3,998 characters
    /// the <c>LIKE</c> pattern it becomes runs into SQL Server's 4,000-character pattern limit and the route
    /// answers 500. Measured on this host: 3,998 ok, 3,999 <c>String or binary data would be truncated</c>.
    /// <c>SearchTermLengthTests</c> pins this number against the model's own column widths, because widening
    /// email would move the boundary and the constant with it.
    /// </summary>
    public const int MaxSearchTermCharacters = 256;

    [HttpGet]
    [HasPermission(Permissions.Customers.Read)]
    public async Task<ActionResult> Search([FromQuery] string? search, [FromQuery(Name = "page")] int requestedPage = 1, [FromQuery(Name = "pageSize")] int requestedPageSize = 20, CancellationToken cancellationToken = default)
    {
        var tenantId = TenantGuard.RequireId(_tenantContext);

        var page = Paging.NormalizePage(requestedPage);
        var size = Paging.NormalizePageSize(requestedPageSize);

        // The binder turns a term of nothing into no term at all, so a length is only ever seen here on a string
        // someone means to search for.
        if (search is { Length: > MaxSearchTermCharacters })
        {
            return Ok(new { total = 0, page, pageSize = size, items = Array.Empty<object>() });
        }

        var query = _db.Customers
            .AsNoTracking()
            .Where(c => c.TenantId == tenantId);

        if (!string.IsNullOrWhiteSpace(search))
        {
            // Not escaped here, and that is measured rather than assumed: EF Core translates this to
            // LIKE @p_contains ESCAPE N'\' with the caller's %, _ and [ already prefixed by the escape character,
            // so the term is a literal substring and a caller cannot turn the box into a match-anything query.
            query = query.Where(c => c.FirstName.Contains(search) || c.LastName.Contains(search) || c.Email.Contains(search));
        }

        var total = await query.CountAsync(cancellationToken);
        var customers = await query
            .OrderBy(c => c.LastName)
            .ThenBy(c => c.FirstName)
            .Skip(Paging.Offset(page, size))
            .Take(size)
            .Select(c => new
            {
                c.Id,
                c.FirstName,
                c.LastName,
                c.Email,
                c.PhoneNumber,
                c.TotalBookings,
                c.TotalSpent,
                c.IsBlocked
            })
            .ToListAsync(cancellationToken);

        return Ok(new { total, page, pageSize = size, items = customers });
    }

    [HttpPost]
    [HasPermission(Permissions.Customers.Create)]
    public async Task<ActionResult> Create([FromBody] CreateCustomerRequest request, CancellationToken cancellationToken)
    {
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantId = TenantGuard.RequireId(_tenantContext),
            FirstName = request.FirstName,
            LastName = request.LastName,
            Email = request.Email,
            PhoneNumber = request.PhoneNumber,
            Notes = request.Notes
        };

        _db.Customers.Add(customer);
        await _db.SaveChangesAsync(cancellationToken);

        // LOC-01: `Search` takes no `id`, so the old route values went out as a query string on a route that
        // ignores it, and the body was the aggregate. These are the eight fields the list route projects.
        return CreatedAtAction(nameof(Search), routeValues: null, new
        {
            customer.Id,
            customer.FirstName,
            customer.LastName,
            customer.Email,
            customer.PhoneNumber,
            customer.TotalBookings,
            customer.TotalSpent,
            customer.IsBlocked
        });
    }
}
