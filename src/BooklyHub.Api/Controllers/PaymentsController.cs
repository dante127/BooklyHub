using BooklyHub.Application.Common.Interfaces;
using BooklyHub.Application.Payments.Commands;
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

    public PaymentsController(ISender sender, ITenantContext tenantContext)
    {
        _sender = sender;
        _tenantContext = tenantContext;
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

    private Guid GetRequiredTenantId()
    {
        if (!_tenantContext.TenantId.HasValue)
        {
            throw new BadHttpRequestException("Active tenant context is required.");
        }
        return _tenantContext.TenantId.Value;
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
