using BooklyHub.Application.Common.Interfaces;
using BooklyHub.Application.Common.Models;
using BooklyHub.Application.Payments.Commands;
using BooklyHub.Application.Payments.Queries;
using BooklyHub.Application.Security;
using BooklyHub.Infrastructure.Security;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BooklyHub.Api.Controllers;

[ApiController]
[Route("api/v1/payments")]
[Authorize]
public class PaymentsController : ControllerBase
{
    private readonly ISender _sender;
    private readonly ITenantContext _tenantContext;
    private readonly IClock _clock;

    public PaymentsController(ISender sender, ITenantContext tenantContext, IClock clock)
    {
        _sender = sender;
        _tenantContext = tenantContext;
        _clock = clock;
    }

    public record ProcessPaymentApiRequest(
        Guid AppointmentId,
        decimal Amount,
        string Currency = "USD",
        string? PaymentMethodToken = null);

    public record RefundPaymentApiRequest(
        Guid PaymentId,
        decimal Amount,
        string? Reason = null);

    private Guid GetRequiredTenantId() => TenantGuard.RequireId(_tenantContext);

    [HttpGet("outstanding-visits")]
    [HasPermission(Permissions.Payments.Read)]
    public async Task<ActionResult<PaginatedList<OutstandingVisitDto>>> GetOutstandingVisits(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var tenantId = GetRequiredTenantId();

        // The window is derived from the application clock, not from the query string: a caller that could
        // pass its own "now" could pull a not-yet-overdue booking into the collection queue.
        var query = new GetOutstandingVisitsQuery(tenantId, _clock.UtcNow, page, pageSize);
        var result = await _sender.Send(query, cancellationToken);
        return Ok(result);
    }

    [HttpPost("charge")]
    [HasPermission(Permissions.Payments.Manage)]
    public async Task<ActionResult<PaymentDto>> Charge(
        [FromBody] ProcessPaymentApiRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var tenantId = GetRequiredTenantId();
        var command = new ProcessPaymentCommand(
            tenantId,
            request.AppointmentId,
            request.Amount,
            request.Currency,
            request.PaymentMethodToken,
            idempotencyKey);

        var result = await _sender.Send(command, cancellationToken);
        return Ok(result);
    }

    [HttpPost("refund")]
    [HasPermission(Permissions.Payments.Refund)]
    public async Task<ActionResult> Refund(
        [FromBody] RefundPaymentApiRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var tenantId = GetRequiredTenantId();
        var command = new RefundPaymentCommand(
            tenantId,
            request.PaymentId,
            request.Amount,
            request.Reason,
            idempotencyKey);

        await _sender.Send(command, cancellationToken);
        return Ok(new { success = true });
    }
}
