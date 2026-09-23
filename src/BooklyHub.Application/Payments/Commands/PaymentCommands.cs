using BooklyHub.Application.Common.Interfaces;
using BooklyHub.Domain.Entities.Payments;
using BooklyHub.Domain.Enums;
using BooklyHub.Domain.Exceptions;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BooklyHub.Application.Payments.Commands;

public record PaymentDto(
    Guid Id,
    Guid TenantId,
    Guid AppointmentId,
    decimal Amount,
    string Currency,
    PaymentStatus Status,
    string? ProviderPaymentId,
    DateTime CreatedAtUtc);

public record ProcessPaymentCommand(
    Guid TenantId,
    Guid AppointmentId,
    decimal Amount,
    string Currency = "USD",
    string? PaymentMethodToken = null,
    string? IdempotencyKey = null) : IRequest<PaymentDto>;

public class ProcessPaymentCommandValidator : AbstractValidator<ProcessPaymentCommand>
{
    public ProcessPaymentCommandValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.AppointmentId).NotEmpty();
        RuleFor(x => x.Amount).GreaterThan(0);
        RuleFor(x => x.Currency).NotEmpty().MaximumLength(10);
    }
}

public class ProcessPaymentCommandHandler : IRequestHandler<ProcessPaymentCommand, PaymentDto>
{
    private readonly IApplicationDbContext _db;
    private readonly IPaymentProvider _paymentProvider;
    private readonly ICurrentUser _currentUser;

    public ProcessPaymentCommandHandler(
        IApplicationDbContext db,
        IPaymentProvider paymentProvider,
        ICurrentUser currentUser)
    {
        _db = db;
        _paymentProvider = paymentProvider;
        _currentUser = currentUser;
    }

    public async Task<PaymentDto> Handle(ProcessPaymentCommand request, CancellationToken cancellationToken)
    {
        var appointment = await _db.Appointments
            .Include(a => a.Customer)
            .FirstOrDefaultAsync(a => a.Id == request.AppointmentId && a.TenantId == request.TenantId, cancellationToken)
            ?? throw new NotFoundException($"Appointment with ID {request.AppointmentId} was not found.");

        // Check if already paid with idempotency key
        if (!string.IsNullOrEmpty(request.IdempotencyKey))
        {
            var existingPayment = await _db.Payments
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.TenantId == request.TenantId && p.IdempotencyKey == request.IdempotencyKey, cancellationToken);

            if (existingPayment != null)
            {
                return new PaymentDto(
                    existingPayment.Id,
                    existingPayment.TenantId,
                    existingPayment.AppointmentId,
                    existingPayment.Amount,
                    existingPayment.Currency,
                    existingPayment.Status,
                    existingPayment.ProviderPaymentId,
                    existingPayment.CreatedAtUtc);
            }
        }

        var providerRequest = new ProcessPaymentRequest(
            request.TenantId,
            request.AppointmentId,
            request.Amount,
            request.Currency,
            request.PaymentMethodToken,
            request.IdempotencyKey);

        var result = await _paymentProvider.ProcessPaymentAsync(providerRequest, cancellationToken);

        if (!result.IsSuccess)
        {
            throw new BusinessRuleValidationException("PaymentFailed", result.ErrorMessage ?? "Payment processing failed.");
        }

        var payment = new Payment
        {
            Id = Guid.NewGuid(),
            TenantId = request.TenantId,
            AppointmentId = request.AppointmentId,
            Amount = request.Amount,
            Currency = request.Currency,
            Status = result.Status,
            Provider = _paymentProvider.ProviderType,
            ProviderPaymentId = result.TransactionId,
            IdempotencyKey = request.IdempotencyKey,
            CreatedBy = _currentUser.UserId?.ToString() ?? "System"
        };

        payment.Transactions.Add(new PaymentTransaction
        {
            PaymentId = payment.Id,
            Amount = request.Amount,
            Type = PaymentTransactionType.Charge,
            Status = result.Status,
            ProviderTransactionId = result.TransactionId,
            TimestampUtc = DateTime.UtcNow
        });

        _db.Payments.Add(payment);

        if (result.Status == PaymentStatus.Paid)
        {
            if (appointment.Status == AppointmentStatus.Pending)
            {
                appointment.TransitionTo(AppointmentStatus.Confirmed, "Payment received", _currentUser.UserId?.ToString());
            }

            if (appointment.Customer != null)
            {
                appointment.Customer.TotalSpent += request.Amount;
            }
        }

        await _db.SaveChangesAsync(cancellationToken);

        return new PaymentDto(
            payment.Id,
            payment.TenantId,
            payment.AppointmentId,
            payment.Amount,
            payment.Currency,
            payment.Status,
            payment.ProviderPaymentId,
            payment.CreatedAtUtc);
    }
}

public record RefundPaymentCommand(
    Guid TenantId,
    Guid PaymentId,
    decimal Amount,
    string? Reason = null,
    string? IdempotencyKey = null) : IRequest<bool>;

public class RefundPaymentCommandHandler : IRequestHandler<RefundPaymentCommand, bool>
{
    private readonly IApplicationDbContext _db;
    private readonly IPaymentProvider _paymentProvider;

    public RefundPaymentCommandHandler(IApplicationDbContext db, IPaymentProvider paymentProvider)
    {
        _db = db;
        _paymentProvider = paymentProvider;
    }

    public async Task<bool> Handle(RefundPaymentCommand request, CancellationToken cancellationToken)
    {
        var payment = await _db.Payments
            .Include(p => p.Refunds)
            .Include(p => p.Appointment)
                .ThenInclude(a => a!.Customer)
            .FirstOrDefaultAsync(p => p.Id == request.PaymentId && p.TenantId == request.TenantId, cancellationToken)
            ?? throw new NotFoundException($"Payment with ID {request.PaymentId} was not found.");

        if (payment.Status != PaymentStatus.Paid && payment.Status != PaymentStatus.PartiallyRefunded)
        {
            throw new BusinessRuleValidationException("InvalidPaymentStatusForRefund", $"Cannot refund payment in {payment.Status} status.");
        }

        var totalAlreadyRefunded = payment.Refunds.Where(r => r.Status == PaymentStatus.Refunded).Sum(r => r.Amount);
        var remainingBalance = payment.Amount - totalAlreadyRefunded;

        if (request.Amount > remainingBalance)
        {
            throw new BusinessRuleValidationException("RefundAmountExceeded", $"Refund amount ({request.Amount}) exceeds remaining balance ({remainingBalance}).");
        }

        var providerRequest = new ProcessRefundRequest(
            request.TenantId,
            request.PaymentId,
            request.Amount,
            request.Reason,
            request.IdempotencyKey);

        var result = await _paymentProvider.ProcessRefundAsync(providerRequest, cancellationToken);
        if (!result.IsSuccess)
        {
            throw new BusinessRuleValidationException("RefundFailed", result.ErrorMessage ?? "Refund processing failed.");
        }

        var refund = new Refund
        {
            Id = Guid.NewGuid(),
            PaymentId = payment.Id,
            Amount = request.Amount,
            Reason = request.Reason,
            Status = PaymentStatus.Refunded,
            ProviderRefundId = result.RefundId,
            CreatedAtUtc = DateTime.UtcNow
        };

        payment.Refunds.Add(refund);

        payment.Transactions.Add(new PaymentTransaction
        {
            PaymentId = payment.Id,
            Amount = request.Amount,
            Type = PaymentTransactionType.Refund,
            Status = PaymentStatus.Refunded,
            ProviderTransactionId = result.RefundId,
            TimestampUtc = DateTime.UtcNow
        });

        // Update status
        var newTotalRefunded = totalAlreadyRefunded + request.Amount;
        payment.Status = newTotalRefunded >= payment.Amount
            ? PaymentStatus.Refunded
            : PaymentStatus.PartiallyRefunded;

        // Adjust customer total spent
        if (payment.Appointment?.Customer != null)
        {
            payment.Appointment.Customer.TotalSpent -= request.Amount;
        }

        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }
}
