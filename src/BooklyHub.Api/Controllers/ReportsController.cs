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

    public ReportsController(ISender sender, ITenantContext tenantContext)
    {
        _sender = sender;
        _tenantContext = tenantContext;
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

        var start = fromUtc ?? DateTime.UtcNow.AddDays(-30);
        var end = toUtc ?? DateTime.UtcNow;

        var query = new GetDashboardReportQuery(_tenantContext.TenantId.Value, start, end);
        var report = await _sender.Send(query, cancellationToken);
        return Ok(report);
    }
}
