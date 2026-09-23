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
        var resolvedTenantId = _tenantContext.TenantId ?? tenantId;
        if (!resolvedTenantId.HasValue)
        {
            return BadRequest(new { message = "Tenant ID must be specified either via context, header, or query." });
        }

        var query = new GetAvailabilityQuery(
            resolvedTenantId.Value,
            locationId,
            serviceId,
            staffId,
            date);

        var result = await _availabilityService.GetAvailabilityAsync(query, cancellationToken);
        return Ok(result);
    }
}
