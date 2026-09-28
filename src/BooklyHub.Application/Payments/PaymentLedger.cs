using BooklyHub.Domain.Entities.Payments;
using BooklyHub.Domain.Enums;
using BooklyHub.Domain.Exceptions;

namespace BooklyHub.Application.Payments;

/// <summary>
/// What an appointment has been charged and refunded, computed from the payment rows themselves rather
/// than from a status flag on the appointment. Charge and refund both read their limits here, so the two
/// paths cannot disagree about what is owed the way the availability preview and the booking guard did.
/// </summary>
public readonly record struct PaymentLedger
{
    private PaymentLedger(decimal captured, decimal returned)
    {
        Captured = captured;
        Returned = returned;
    }

    /// <summary>Sum of every payment that actually took money, including ones later refunded.</summary>
    public decimal Captured { get; }

    public decimal Returned { get; }

    /// <summary>Money the tenant still holds for this appointment.</summary>
    public decimal Net => Captured - Returned;

    public static PaymentLedger From(IEnumerable<Payment> payments)
    {
        var materialized = payments.ToList();

        return new PaymentLedger(
            materialized.Where(p => p.Status is PaymentStatus.Paid or PaymentStatus.PartiallyRefunded or PaymentStatus.Refunded)
                .Sum(p => p.Amount),
            materialized.SelectMany(p => p.Refunds)
                .Where(r => r.Status == PaymentStatus.Refunded)
                .Sum(r => r.Amount));
    }

    public decimal Outstanding(decimal amountDue) => Math.Max(0m, amountDue - Net);

    /// <summary>
    /// A refund draws on its own payment, never on the appointment's whole balance, so refunding part of
    /// one visit cannot silently eat the money taken for another.
    /// </summary>
    public static decimal RemainingOnPayment(Payment payment) =>
        payment.Amount - payment.Refunds.Where(r => r.Status == PaymentStatus.Refunded).Sum(r => r.Amount);

    /// <summary>
    /// The whole charge rule set for one appointment. The appointment carries the price it was booked at,
    /// so a client cannot be charged for more than the visit is worth, and a second charge on a settled
    /// appointment is refused on the ledger rather than on a key the caller may not have sent.
    /// </summary>
    public static void ValidateCharge(
        PaymentLedger ledger,
        decimal amountDue,
        string dueCurrency,
        string requestedCurrency,
        AppointmentStatus status,
        decimal amount)
    {
        if (status is AppointmentStatus.Cancelled or AppointmentStatus.NoShow or AppointmentStatus.Rescheduled)
        {
            throw new BusinessRuleValidationException(
                "AppointmentNotChargeable",
                $"A {status} appointment cannot be charged.");
        }

        if (!string.Equals(dueCurrency, requestedCurrency, StringComparison.OrdinalIgnoreCase))
        {
            throw new BusinessRuleValidationException(
                "PaymentCurrencyMismatch",
                $"This appointment is priced in {dueCurrency}, so {amount:0.00} {requestedCurrency} cannot be charged against it.");
        }

        var outstanding = ledger.Outstanding(amountDue);

        if (outstanding == 0m)
        {
            throw new BusinessRuleValidationException(
                "AppointmentAlreadyPaid",
                $"This appointment is already paid in full ({amountDue:0.00} {dueCurrency}); the ledger holds {ledger.Net:0.00}.");
        }

        if (amount > outstanding)
        {
            throw new BusinessRuleValidationException(
                "PaymentExceedsAmountDue",
                $"Cannot charge {amount:0.00}: this appointment has {outstanding:0.00} outstanding of {amountDue:0.00}.");
        }
    }

    public static void ValidateRefund(decimal remainingOnPayment, decimal paymentAmount, decimal amount)
    {
        if (amount > remainingOnPayment)
        {
            throw new BusinessRuleValidationException(
                "RefundAmountExceeded",
                $"Refund amount ({amount:0.00}) exceeds the {remainingOnPayment:0.00} still refundable on this " +
                $"payment of {paymentAmount:0.00}.");
        }
    }
}
