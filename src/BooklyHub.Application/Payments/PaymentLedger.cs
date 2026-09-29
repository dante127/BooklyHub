using BooklyHub.Domain.Entities.Appointments;
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
    /// The statuses after which nothing more can be collected, which is the list <see cref="ValidateCharge"/>
    /// refuses to charge. Moving a booking into one of them while it still owes money writes the balance off,
    /// and the outstanding queue loses the row at the same moment because its own membership is the mirror
    /// image of this list. Rescheduled is named because the charge rule names it; BL-06 leaves it unreachable,
    /// so no path is widened by including it.
    /// </summary>
    public static bool EndsCollection(AppointmentStatus status) =>
        status is AppointmentStatus.Cancelled or AppointmentStatus.NoShow or AppointmentStatus.Rescheduled;

    /// <summary>
    /// A balance written off by a status change is a decision somebody has to own. The ledger cannot tell a
    /// waiver from a slip, and once the status lands the debt is off every queue, so a blank reason leaves the
    /// money with no author and nothing left to ask about it.
    /// </summary>
    public static void ValidateWriteOff(
        AppointmentStatus status,
        decimal amountDue,
        PaymentLedger ledger,
        string? reason)
    {
        if (!EndsCollection(status)) return;

        var outstanding = ledger.Outstanding(amountDue);
        if (outstanding == 0m || !string.IsNullOrWhiteSpace(reason)) return;

        throw new BusinessRuleValidationException(
            "DebtWriteOffReasonRequired",
            $"This appointment still owes {outstanding:0.00} of {amountDue:0.00} and a {status} appointment cannot " +
            "be charged, so this transition writes the balance off. Say why.");
    }

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
        if (EndsCollection(status))
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

/// <summary>
/// The ledger's money question written for the database, kept in this file so the two forms of one rule are
/// read side by side.
///
/// <see cref="PaymentLedger.Outstanding"/> can only answer for payments already in memory, which makes it
/// useless for paging: a queue that filtered in C# would have to load every overdue appointment to know
/// which dozen to show. So the same test is stated here as a translatable predicate. Nothing but a test
/// keeps the two agreeable, and <c>OutstandingVisitQueueTests</c> runs both forms over every payment shape -
/// because a queue that listed a settled booking would accuse a customer who paid, and one that dropped a
/// debt would reopen exactly the hole the queue exists to close.
/// </summary>
public static class PaymentLedgerQuery
{
    public static IQueryable<Appointment> WhereOwing(
        this IQueryable<Appointment> appointments,
        IQueryable<Payment> payments) =>
        appointments.Where(a => a.Price >
            (payments.Where(p => p.AppointmentId == a.Id &&
                                 (p.Status == PaymentStatus.Paid ||
                                  p.Status == PaymentStatus.PartiallyRefunded ||
                                  p.Status == PaymentStatus.Refunded))
                .Sum(p => (decimal?)p.Amount) ?? 0m) -
            (payments.Where(p => p.AppointmentId == a.Id)
                .SelectMany(p => p.Refunds.Where(r => r.Status == PaymentStatus.Refunded))
                .Sum(r => (decimal?)r.Amount) ?? 0m));
}
