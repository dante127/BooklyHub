using BooklyHub.Application.Common.Interfaces;
using BooklyHub.Application.Reports.Queries;
using BooklyHub.Application.Security;
using BooklyHub.Infrastructure.Security;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BooklyHub.Api.Controllers;

[ApiController]
[Route("api/v1/reports")]
[Authorize]
public class ReportsController : ControllerBase
{
    private readonly ISender _sender;
    private readonly ITenantContext _tenantContext;
    private readonly IClock _clock;

    public ReportsController(ISender sender, ITenantContext tenantContext, IClock clock)
    {
        _sender = sender;
        _tenantContext = tenantContext;
        _clock = clock;
    }

    [HttpGet("dashboard")]
    [HasPermission(Permissions.Reports.Read)]
    public async Task<ActionResult<DashboardReportDto>> GetDashboard(
        [FromQuery] DateTime? fromUtc,
        [FromQuery] DateTime? toUtc,
        CancellationToken cancellationToken)
    {
        if (!_tenantContext.TenantId.HasValue)
        {
            return BadRequest(new { message = "Active tenant context is required." });
        }

        // One instant for both bounds, so the period is 30 days wide rather than 30 days plus the gap between two reads.
        var nowUtc = _clock.UtcNow;
        var start = fromUtc ?? nowUtc.AddDays(-30);
        var end = toUtc ?? nowUtc;

        var query = new GetDashboardReportQuery(_tenantContext.TenantId.Value, start, end, nowUtc);
        var report = await _sender.Send(query, cancellationToken);
        return Ok(report);
    }
}
