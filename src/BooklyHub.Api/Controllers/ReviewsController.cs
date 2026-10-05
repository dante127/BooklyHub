using BooklyHub.Application.Common.Interfaces;
using BooklyHub.Application.Common.Models;
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

    private Guid GetRequiredTenantId() => TenantGuard.RequireId(_tenantContext);

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
        [FromQuery(Name = "page")] int requestedPage = 1,
        [FromQuery(Name = "pageSize")] int requestedPageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var tenantId = TenantGuard.RequireId(_tenantContext);

        var query = _db.Reviews
            .AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.IsPublished);

        if (staffId.HasValue) query = query.Where(r => r.StaffId == staffId.Value);
        if (serviceId.HasValue) query = query.Where(r => r.ServiceId == serviceId.Value);

        var page = Paging.NormalizePage(requestedPage);
        var size = Paging.NormalizePageSize(requestedPageSize);

        var total = await query.CountAsync(cancellationToken);
        var reviews = await query
            .OrderByDescending(r => r.CreatedAtUtc)
            .Skip(Paging.Offset(page, size))
            .Take(size)
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

        return Ok(new { total, page, pageSize = size, items = reviews });
    }
}
