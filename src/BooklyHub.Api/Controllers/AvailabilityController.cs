using BooklyHub.Application.Common.Interfaces;
using BooklyHub.Application.Scheduling;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BooklyHub.Api.Controllers;

[ApiController]
[Route("api/v1/availability")]
public class AvailabilityController : ControllerBase
{
    private readonly IAvailabilityService _availabilityService;
    private readonly ITenantContext _tenantContext;

    public AvailabilityController(IAvailabilityService availabilityService, ITenantContext tenantContext)
    {
        _availabilityService = availabilityService;
        _tenantContext = tenantContext;
    }

    [HttpGet]
    [AllowAnonymous]
    public async Task<ActionResult<DayAvailabilityDto>> GetAvailability(
        [FromQuery] Guid locationId,
        [FromQuery] Guid serviceId,
        [FromQuery] DateOnly date,
        [FromQuery] Guid? staffId = null,
        [FromQuery] Guid? tenantId = null,
        CancellationToken cancellationToken = default)
    {
        // The public booking portal is the one caller allowed to name the tenant itself, so this guard's condition
        // really does differ from every other one in the API and gets to say where to put the value.
        var resolvedTenantId = TenantGuard.RequireId(_tenantContext.TenantId ?? tenantId,
            "Tenant ID must be specified either via context, header, or query.");

        var query = new GetAvailabilityQuery(
            resolvedTenantId,
            locationId,
            serviceId,
            staffId,
            date);

        var result = await _availabilityService.GetAvailabilityAsync(query, cancellationToken);
        return Ok(result);
    }
}
