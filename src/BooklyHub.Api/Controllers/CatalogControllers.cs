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
    public async Task<ActionResult> GetServices([FromQuery] Guid? categoryId, CancellationToken cancellationToken)
    {
        var tenantId = TenantGuard.RequireId(_tenantContext);

        var query = _db.Services
            .AsNoTracking()
            .Where(s => s.TenantId == tenantId && s.IsActive);

        if (categoryId.HasValue) query = query.Where(s => s.CategoryId == categoryId.Value);

        var services = await query
            .OrderBy(s => s.Name)
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

        return Ok(services);
    }
}

[ApiController]
[Route("api/v1/staff")]
public class StaffController : ControllerBase
{
    private readonly IApplicationDbContext _db;
    private readonly ITenantContext _tenantContext;

    public StaffController(IApplicationDbContext db, ITenantContext tenantContext)
    {
        _db = db;
        _tenantContext = tenantContext;
    }

    [HttpGet]
    [AllowAnonymous]
    public async Task<ActionResult> GetStaff([FromQuery] Guid? locationId, [FromQuery] Guid? serviceId, CancellationToken cancellationToken)
    {
        var tenantId = TenantGuard.RequireId(_tenantContext);

        var query = _db.StaffMembers
            .AsNoTracking()
            .Where(s => s.TenantId == tenantId && s.IsActive);

        if (locationId.HasValue) query = query.Where(s => s.LocationId == locationId.Value);
        if (serviceId.HasValue) query = query.Where(s => s.StaffServices.Any(ss => ss.ServiceId == serviceId.Value));

        var staff = await query
            .OrderBy(s => s.LastName)
            .Select(s => new
            {
                s.Id,
                s.FirstName,
                s.LastName,
                s.Title,
                s.Bio,
                s.Email,
                s.PhoneNumber,
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

        return Ok(staff);
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

    [HttpGet]
    [HasPermission(Permissions.Customers.Read)]
    public async Task<ActionResult> Search([FromQuery] string? search, [FromQuery(Name = "page")] int requestedPage = 1, [FromQuery(Name = "pageSize")] int requestedPageSize = 20, CancellationToken cancellationToken = default)
    {
        var tenantId = TenantGuard.RequireId(_tenantContext);

        var page = Paging.NormalizePage(requestedPage);
        var size = Paging.NormalizePageSize(requestedPageSize);

        var query = _db.Customers
            .AsNoTracking()
            .Where(c => c.TenantId == tenantId);

        if (!string.IsNullOrWhiteSpace(search))
        {
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

        return CreatedAtAction(nameof(Search), new { id = customer.Id }, customer);
    }
}
