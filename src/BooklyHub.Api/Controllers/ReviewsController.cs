using BooklyHub.Application.Common.Interfaces;
using BooklyHub.Application.Reviews.Commands;
using BooklyHub.Application.Security;
using BooklyHub.Infrastructure.Security;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BooklyHub.Api.Controllers;

[ApiController]
[Route("api/v1/reviews")]
public class ReviewsController : ControllerBase
{
    private readonly ISender _sender;
    private readonly IApplicationDbContext _db;
    private readonly ITenantContext _tenantContext;

    public ReviewsController(ISender sender, IApplicationDbContext db, ITenantContext tenantContext)
    {
        _sender = sender;
        _db = db;
        _tenantContext = tenantContext;
    }

    public record SubmitReviewRequest(
        Guid AppointmentId,
        int Rating,
        string? Comment = null);

    private Guid GetRequiredTenantId()
    {
        if (!_tenantContext.TenantId.HasValue)
        {
            throw new BadHttpRequestException("Active tenant context is required.");
        }
        return _tenantContext.TenantId.Value;
    }

    [HttpPost]
    [Authorize]
    public async Task<ActionResult<ReviewDto>> SubmitReview([FromBody] SubmitReviewRequest request, CancellationToken cancellationToken)
    {
        var tenantId = GetRequiredTenantId();
        var command = new SubmitReviewCommand(tenantId, request.AppointmentId, request.Rating, request.Comment);
        var result = await _sender.Send(command, cancellationToken);
        return CreatedAtAction(nameof(GetReviews), new { appointmentId = result.AppointmentId }, result);
    }

    [HttpGet]
    [AllowAnonymous]
    public async Task<ActionResult> GetReviews(
        [FromQuery] Guid? staffId,
        [FromQuery] Guid? serviceId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        if (!_tenantContext.TenantId.HasValue) return BadRequest(new { message = "Tenant ID is required." });

        var query = _db.Reviews
            .AsNoTracking()
            .Where(r => r.TenantId == _tenantContext.TenantId.Value && r.IsPublished);

        if (staffId.HasValue) query = query.Where(r => r.StaffId == staffId.Value);
        if (serviceId.HasValue) query = query.Where(r => r.ServiceId == serviceId.Value);

        var total = await query.CountAsync(cancellationToken);
        var reviews = await query
            .OrderByDescending(r => r.CreatedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(r => new
            {
                r.Id,
                r.Rating,
                r.Comment,
                r.Response,
                r.StaffId,
                StaffName = r.Staff != null ? r.Staff.FirstName + " " + r.Staff.LastName : "",
                r.ServiceId,
                ServiceName = r.Service != null ? r.Service.Name : "",
                r.CreatedAtUtc
            })
            .ToListAsync(cancellationToken);

        return Ok(new { total, page, pageSize, items = reviews });
    }
}
