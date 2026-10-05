using BooklyHub.Application.Common.Interfaces;
using BooklyHub.Domain.Entities.Payments;
using BooklyHub.Domain.Entities.System;
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
    private readonly IClock _clock;

    public ProcessPaymentCommandHandler(
        IApplicationDbContext db,
        IPaymentProvider paymentProvider,
        ICurrentUser currentUser,
        IClock clock)
    {
        _db = db;
        _paymentProvider = paymentProvider;
        _currentUser = currentUser;
        _clock = clock;
    }

    public async Task<PaymentDto> Handle(ProcessPaymentCommand request, CancellationToken cancellationToken)
    {
        return await _db.ExecuteInTransactionAsync(async () =>
        {
            await _db.AcquireAppointmentPaymentLockAsync(request.TenantId, request.AppointmentId, cancellationToken);

            var appointment = await _db.Appointments
                .FirstOrDefaultAsync(a => a.Id == request.AppointmentId && a.TenantId == request.TenantId, cancellationToken)
                ?? throw new NotFoundException($"Appointment with ID {request.AppointmentId} was not found.");

            // Read after the lock: another charge can only commit while this transaction holds it, so the
            // totals below are the ones the money rules are actually decided on.
            var payments = await _db.Payments
                .Include(p => p.Refunds)
                .Where(p => p.TenantId == request.TenantId && p.AppointmentId == request.AppointmentId)
                .ToListAsync(cancellationToken);

            if (request.IdempotencyKey is { Length: > 0 })
            {
                // One key buys one charge. Reusing it for this appointment replays it; reusing it for a
                // different appointment is refused instead of silently capturing a second payment, which
                // is what the caller's retry means either way.
                // KEY-02: measured as a lost charge — a case-flipped key carrying 70.00 was answered 200 with the
                // receipt of an earlier 40.00 payment, so the second capture never ran. The SQL equality narrows
                // with the case the collation folds; the identity is decided here, byte for byte.
                var candidates = await _db.Payments
                    .AsNoTracking()
                    .Where(p => p.TenantId == request.TenantId && p.IdempotencyKey == request.IdempotencyKey)
                    .ToListAsync(cancellationToken);
                var keyed = candidates.FirstOrDefault(p =>
                    IdempotencyIdentity.SameKey(p.IdempotencyKey, request.IdempotencyKey));

                if (keyed != null)
                {
                    if (keyed.AppointmentId != request.AppointmentId)
                    {
                        throw new BusinessRuleValidationException(
                            "IdempotencyKeyReused",
                            "This idempotency key already paid for a different appointment.");
                    }

                    return ToDto(keyed);
                }
            }

            var ledger = PaymentLedger.From(payments);
            PaymentLedger.ValidateCharge(
                ledger,
                amountDue: appointment.Price,
                dueCurrency: appointment.Currency,
                requestedCurrency: request.Currency,
                status: appointment.Status,
                amount: request.Amount);

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

            // Children join the graph through the set, not through a parent's collection navigation: a
            // child with a key that reaches the change tracker only through a navigation is treated as an
            // existing row, and the update it generates matches nothing.
            _db.PaymentTransactions.Add(new PaymentTransaction
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
                    _db.AppointmentStatusHistories.Add(
                        appointment.TransitionTo(AppointmentStatus.Confirmed, _clock.UtcNow, "Payment received", _currentUser.UserId?.ToString()));
                }

                await _db.AdjustTotalSpentAsync(request.TenantId, appointment.CustomerId, request.Amount, cancellationToken);
            }

            await _db.SaveChangesAsync(cancellationToken);

            // After the save, because a Pending appointment was just transitioned through the tracker and a
            // second writer of its token would fail the charge. The sweep reads this ledger in a transaction
            // it cannot hold open, so the rowversion is the only way it learns money moved.
            await _db.TouchAppointmentRowAsync(request.TenantId, request.AppointmentId, cancellationToken);

            return ToDto(payment);
        });
    }

    private static PaymentDto ToDto(Payment payment) => new(
        payment.Id,
        payment.TenantId,
        payment.AppointmentId,
        payment.Amount,
        payment.Currency,
        payment.Status,
        payment.ProviderPaymentId,
        payment.CreatedAtUtc);
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
        // Only the appointment this payment belongs to is read here, and only to learn which lock to take.
        // The refundable balance is computed again after the lock, from committed data.
        var appointmentId = await _db.Payments
            .Where(p => p.Id == request.PaymentId && p.TenantId == request.TenantId)
            .Select(p => p.AppointmentId)
            .FirstOrDefaultAsync(cancellationToken);

        if (appointmentId == Guid.Empty)
        {
            throw new NotFoundException($"Payment with ID {request.PaymentId} was not found.");
        }

        return await _db.ExecuteInTransactionAsync(async () =>
        {
            await _db.AcquireAppointmentPaymentLockAsync(request.TenantId, appointmentId, cancellationToken);

            var payments = await _db.Payments
                .Include(p => p.Refunds)
                .Include(p => p.Appointment)
                .Where(p => p.TenantId == request.TenantId && p.AppointmentId == appointmentId)
                .ToListAsync(cancellationToken);

            var payment = payments.FirstOrDefault(p => p.Id == request.PaymentId)
                ?? throw new NotFoundException($"Payment with ID {request.PaymentId} was not found.");

            if (payment.Status != PaymentStatus.Paid && payment.Status != PaymentStatus.PartiallyRefunded)
            {
                throw new BusinessRuleValidationException("InvalidPaymentStatusForRefund", $"Cannot refund payment in {payment.Status} status.");
            }

            var remaining = PaymentLedger.RemainingOnPayment(payment);
            PaymentLedger.ValidateRefund(remaining, payment.Amount, request.Amount);

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

            // Same registration rule as the charge path: children are added through the set.
            _db.Refunds.Add(refund);

            _db.PaymentTransactions.Add(new PaymentTransaction
            {
                PaymentId = payment.Id,
                Amount = request.Amount,
                Type = PaymentTransactionType.Refund,
                Status = PaymentStatus.Refunded,
                ProviderTransactionId = result.RefundId,
                TimestampUtc = DateTime.UtcNow
            });

            payment.Status = remaining - request.Amount <= 0m
                ? PaymentStatus.Refunded
                : PaymentStatus.PartiallyRefunded;

            await _db.AdjustTotalSpentAsync(request.TenantId, payment.Appointment!.CustomerId, -request.Amount, cancellationToken);

            await _db.SaveChangesAsync(cancellationToken);

            // Same reason as the charge path: a refund is the write that turns a settled booking back into a
            // debt, and a closure that had already read the old ledger has to be refused rather than committed.
            await _db.TouchAppointmentRowAsync(request.TenantId, appointmentId, cancellationToken);

            return true;
        });
    }
}

/// <summary>
/// The counter moves in SQL, not in this process: two charges on two different appointments of the same
/// customer hold two different payment locks, so a read-modify-write would drop one of them.
/// </summary>
internal static class CustomerCounterExtensions
{
    public static Task AdjustTotalSpentAsync(
        this IApplicationDbContext db,
        Guid tenantId,
        Guid customerId,
        decimal delta,
        CancellationToken cancellationToken) =>
        db.Customers
            .Where(c => c.Id == customerId && c.TenantId == tenantId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.TotalSpent, c => c.TotalSpent + delta), cancellationToken);

    /// <summary>
    /// Same rule as <see cref="AdjustTotalSpentAsync"/>: two bookings for one customer at two locations
    /// hold no common lock, so the count has to change inside the database, not on a loaded entity.
    /// </summary>
    public static Task AdjustTotalBookingsAsync(
        this IApplicationDbContext db,
        Guid tenantId,
        Guid customerId,
        int delta,
        CancellationToken cancellationToken) =>
        db.Customers
            .Where(c => c.Id == customerId && c.TenantId == tenantId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.TotalBookings, c => c.TotalBookings + delta), cancellationToken);
}
