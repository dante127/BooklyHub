using BooklyHub.Application.Appointments.Commands;
using BooklyHub.Application.Appointments.Dtos;
using BooklyHub.Application.Appointments.Queries;
using BooklyHub.Application.Common.Interfaces;
using BooklyHub.Application.Common.Models;
using BooklyHub.Application.Security;
using BooklyHub.Domain.Enums;
using BooklyHub.Infrastructure.Security;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BooklyHub.Api.Controllers;

[ApiController]
[Route("api/v1/appointments")]
[Authorize]
public class AppointmentsController : ControllerBase
{
    private readonly ISender _sender;
    private readonly ITenantContext _tenantContext;

    public AppointmentsController(ISender sender, ITenantContext tenantContext)
    {
        _sender = sender;
        _tenantContext = tenantContext;
    }

    public record BookAppointmentRequest(
        Guid LocationId,
        Guid ServiceId,
        Guid StaffId,
        Guid CustomerId,
        DateTime StartAtUtc,
        string? Notes = null);

    public record RescheduleAppointmentRequest(
        DateTime NewStartAtUtc,
        string? Reason = null);

    public record CancelAppointmentRequest(
        string Reason);

    public record TransitionStatusRequest(
        AppointmentStatus NewStatus,
        string? Reason = null);

    private Guid GetRequiredTenantId()
    {
        if (!_tenantContext.TenantId.HasValue)
        {
            throw new BadHttpRequestException("Active tenant context is required.");
        }
        return _tenantContext.TenantId.Value;
    }

    [HttpPost]
    [HasPermission(Permissions.Appointments.Create)]
    public async Task<ActionResult<AppointmentDto>> Book(
        [FromBody] BookAppointmentRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var tenantId = GetRequiredTenantId();

        var command = new BookAppointmentCommand(
            tenantId,
            request.LocationId,
            request.ServiceId,
            request.StaffId,
            request.CustomerId,
            request.StartAtUtc,
            request.Notes,
            idempotencyKey);

        var result = await _sender.Send(command, cancellationToken);
        return CreatedAtAction(nameof(GetById), new { id = result.Id }, result);
    }

    [HttpGet]
    [HasPermission(Permissions.Appointments.Read)]
    public async Task<ActionResult<PaginatedList<AppointmentDto>>> Search(
        [FromQuery] Guid? locationId,
        [FromQuery] Guid? staffId,
        [FromQuery] Guid? customerId,
        [FromQuery] AppointmentStatus? status,
        [FromQuery] DateTime? fromUtc,
        [FromQuery] DateTime? toUtc,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var tenantId = GetRequiredTenantId();

        var query = new SearchAppointmentsQuery(
            tenantId,
            locationId,
            staffId,
            customerId,
            status,
            fromUtc,
            toUtc,
            page,
            pageSize);

        var result = await _sender.Send(query, cancellationToken);
        return Ok(result);
    }

    [HttpGet("{id:guid}")]
    [HasPermission(Permissions.Appointments.Read)]
    public async Task<ActionResult<AppointmentDto>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var tenantId = GetRequiredTenantId();
        var query = new GetAppointmentByIdQuery(tenantId, id);
        var result = await _sender.Send(query, cancellationToken);
        return Ok(result);
    }

    [HttpPost("{id:guid}/reschedule")]
    [HasPermission(Permissions.Appointments.Reschedule)]
    public async Task<ActionResult<AppointmentDto>> Reschedule(
        Guid id,
        [FromBody] RescheduleAppointmentRequest request,
        CancellationToken cancellationToken)
    {
        var tenantId = GetRequiredTenantId();
        var command = new RescheduleAppointmentCommand(tenantId, id, request.NewStartAtUtc, request.Reason);
        var result = await _sender.Send(command, cancellationToken);
        return Ok(result);
    }

    [HttpPost("{id:guid}/cancel")]
    [HasPermission(Permissions.Appointments.Cancel)]
    public async Task<ActionResult> Cancel(
        Guid id,
        [FromBody] CancelAppointmentRequest request,
        CancellationToken cancellationToken)
    {
        var tenantId = GetRequiredTenantId();
        var command = new CancelAppointmentCommand(tenantId, id, request.Reason);
        await _sender.Send(command, cancellationToken);
        return NoContent();
    }

    [HttpPost("{id:guid}/transition")]
    [HasPermission(Permissions.Appointments.Update)]
    public async Task<ActionResult<AppointmentDto>> TransitionStatus(
        Guid id,
        [FromBody] TransitionStatusRequest request,
        CancellationToken cancellationToken)
    {
        var tenantId = GetRequiredTenantId();
        var command = new TransitionAppointmentStatusCommand(tenantId, id, request.NewStatus, request.Reason);
        var result = await _sender.Send(command, cancellationToken);
        return Ok(result);
    }

    [HttpPost("recurring")]
    [HasPermission(Permissions.Appointments.Create)]
    public async Task<ActionResult<RecurringAppointmentResultDto>> BookRecurring(
        [FromBody] CreateRecurringAppointmentCommand command,
        CancellationToken cancellationToken)
    {
        var tenantId = GetRequiredTenantId();
        var verifiedCommand = command with { TenantId = tenantId };
        var result = await _sender.Send(verifiedCommand, cancellationToken);
        return Ok(result);
    }
}
