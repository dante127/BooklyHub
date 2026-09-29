using BooklyHub.Application.Payments;
using BooklyHub.Domain.Entities.Appointments;
using BooklyHub.Domain.Entities.Payments;
using BooklyHub.Domain.Enums;
using BooklyHub.Domain.Exceptions;
using FluentAssertions;
using Xunit;

namespace BooklyHub.UnitTests.Payments;

/// <summary>
/// The money rules are arithmetic over a ledger, so they are pinned here without a database. The integration
/// tests cover what only real SQL Server can show: that two concurrent requests cannot both pass these rules.
/// </summary>
public class PaymentLedgerTests
{
    private static Payment Charged(decimal amount, PaymentStatus status = PaymentStatus.Paid) => new()
    {
        TenantId = Guid.NewGuid(),
        Amount = amount,
        Status = status
    };

    private static void WithRefund(Payment payment, decimal amount, PaymentStatus status = PaymentStatus.Refunded) =>
        payment.Refunds.Add(new Refund { Amount = amount, Status = status, PaymentId = payment.Id });

    private static void ValidateCharge(PaymentLedger ledger, decimal amount, decimal amountDue = 120m, string currency = "USD", AppointmentStatus status = AppointmentStatus.Confirmed) =>
        PaymentLedger.ValidateCharge(ledger, amountDue, currency, currency, status, amount);

    [Fact]
    public void From_EmptyLedger_OwesTheFullPrice()
    {
        var ledger = PaymentLedger.From([]);

        ledger.Captured.Should().Be(0m);
        ledger.Net.Should().Be(0m);
        ledger.Outstanding(120m).Should().Be(120m);
    }

    [Fact]
    public void From_PartialCharge_OwesTheRemainder()
    {
        var ledger = PaymentLedger.From([Charged(40m)]);

        ledger.Outstanding(120m).Should().Be(80m);
    }

    [Fact]
    public void From_FailedAndPendingPayments_CaptureNothing()
    {
        var ledger = PaymentLedger.From([
            Charged(50m, PaymentStatus.Failed),
            Charged(60m, PaymentStatus.Pending),
            Charged(20m, PaymentStatus.Authorized)]);

        ledger.Captured.Should().Be(0m, "only money that actually moved may settle a debt");
        ledger.Outstanding(120m).Should().Be(120m);
    }

    [Fact]
    public void From_PartiallyRefundedPayment_NetsTheRefundOffTheCapture()
    {
        var payment = Charged(120m, PaymentStatus.PartiallyRefunded);
        WithRefund(payment, 30m);

        var ledger = PaymentLedger.From([payment]);

        ledger.Captured.Should().Be(120m);
        ledger.Net.Should().Be(90m);
        ledger.Outstanding(120m).Should().Be(30m, "money handed back reopens the debt it settled");
    }

    [Fact]
    public void From_FullyRefundedPayment_LeavesNothingCollected()
    {
        var payment = Charged(120m, PaymentStatus.Refunded);
        WithRefund(payment, 120m);

        PaymentLedger.From([payment]).Net.Should().Be(0m);
    }

    [Fact]
    public void From_RefundRowThatDidNotLand_DoesNotReduceTheCapture()
    {
        var payment = Charged(120m, PaymentStatus.PartiallyRefunded);
        WithRefund(payment, 60m, PaymentStatus.Failed);

        PaymentLedger.From([payment]).Captured.Should().Be(120m);
        PaymentLedger.RemainingOnPayment(payment).Should().Be(120m);
    }

    [Fact]
    public void ValidateCharge_WhenSettled_ThrowsAlreadyPaid()
    {
        var ledger = PaymentLedger.From([Charged(120m)]);

        var ex = Assert.Throws<BusinessRuleValidationException>(() => ValidateCharge(ledger, 120m));

        ex.RuleName.Should().Be("AppointmentAlreadyPaid");
    }

    [Fact]
    public void ValidateCharge_AboveOutstanding_ThrowsExceedsAmountDue()
    {
        var ledger = PaymentLedger.From([Charged(90m)]);

        var ex = Assert.Throws<BusinessRuleValidationException>(() => ValidateCharge(ledger, 40m));

        ex.RuleName.Should().Be("PaymentExceedsAmountDue");
        ex.Message.Should().Contain("30.00 outstanding", "the caller has to see the ceiling it hit");
    }

    [Fact]
    public void ValidateCharge_UpToOutstanding_IsAccepted()
    {
        var ledger = PaymentLedger.From([Charged(90m)]);

        var act = () => ValidateCharge(ledger, 30m);

        act.Should().NotThrow("a deposit path must be able to settle the remainder exactly");
    }

    [Theory]
    [InlineData(AppointmentStatus.Cancelled)]
    [InlineData(AppointmentStatus.NoShow)]
    [InlineData(AppointmentStatus.Rescheduled)]
    public void ValidateCharge_DeadAppointment_IsNeverChargeable(AppointmentStatus status)
    {
        var ex = Assert.Throws<BusinessRuleValidationException>(
            () => ValidateCharge(PaymentLedger.From([]), 10m, status: status));

        ex.RuleName.Should().Be("AppointmentNotChargeable");
    }

    [Fact]
    public void ValidateCharge_LiveAppointment_IsChargeable()
    {
        foreach (var status in new[] { AppointmentStatus.Pending, AppointmentStatus.Confirmed, AppointmentStatus.Completed })
        {
            var act = () => ValidateCharge(PaymentLedger.From([]), 10m, status: status);
            act.Should().NotThrow($"a {status} appointment still owes its price");
        }
    }

    [Fact]
    public void ValidateCharge_OtherCurrency_ThrowsBeforeTouchingTheAmount()
    {
        var ex = Assert.Throws<BusinessRuleValidationException>(() =>
            PaymentLedger.ValidateCharge(
                PaymentLedger.From([]),
                amountDue: 120m,
                dueCurrency: "USD",
                requestedCurrency: "SYP",
                status: AppointmentStatus.Confirmed,
                amount: 10m));

        ex.RuleName.Should().Be("PaymentCurrencyMismatch");
    }

    [Fact]
    public void ValidateCharge_CurrencyCaseDifference_IsNotAMismatch()
    {
        var act = () => PaymentLedger.ValidateCharge(
            PaymentLedger.From([]),
            amountDue: 120m,
            dueCurrency: "USD",
            requestedCurrency: "usd",
            status: AppointmentStatus.Confirmed,
            amount: 10m);

        act.Should().NotThrow("currency codes are case-insensitive by ISO 4217 convention");
    }

    [Fact]
    public void ValidateRefund_ExactlyTheRemainingBalance_IsAllowed()
    {
        var payment = Charged(120m, PaymentStatus.PartiallyRefunded);
        WithRefund(payment, 70m);

        var act = () => PaymentLedger.ValidateRefund(PaymentLedger.RemainingOnPayment(payment), payment.Amount, 50m);

        act.Should().NotThrow();
    }

    [Fact]
    public void ValidateRefund_AboveRemainingBalance_Throws()
    {
        var payment = Charged(120m, PaymentStatus.PartiallyRefunded);
        WithRefund(payment, 70m);

        var ex = Assert.Throws<BusinessRuleValidationException>(
            () => PaymentLedger.ValidateRefund(PaymentLedger.RemainingOnPayment(payment), payment.Amount, 51m));

        ex.RuleName.Should().Be("RefundAmountExceeded");
        ex.Message.Should().Contain("50.00 still refundable");
    }

    [Fact]
    public void RemainingOnPayment_NeverDrawsOnAnotherPaymentOfTheSameAppointment()
    {
        var first = Charged(120m);
        var deposit = Charged(30m);

        PaymentLedger.RemainingOnPayment(deposit).Should().Be(30m,
            "refunding a deposit may not consume the money taken for the main charge");
    }

    /// <summary>
    /// The write-off gate and the charge refusal read one predicate, so this agreement is true by construction
    /// today. It is pinned anyway: the day somebody re-splits the refusal into its own literal list, the two
    /// halves of one money rule start disagreeing and this is what notices.
    /// </summary>
    [Theory]
    [InlineData(AppointmentStatus.Pending)]
    [InlineData(AppointmentStatus.Confirmed)]
    [InlineData(AppointmentStatus.CheckedIn)]
    [InlineData(AppointmentStatus.InProgress)]
    [InlineData(AppointmentStatus.Completed)]
    [InlineData(AppointmentStatus.Cancelled)]
    [InlineData(AppointmentStatus.NoShow)]
    [InlineData(AppointmentStatus.Rescheduled)]
    public void EndsCollection_MustBeExactlyTheStatusesChargingRefuses(AppointmentStatus status)
    {
        PaymentLedger.EndsCollection(status)
            .Should().Be(ChargeRefusedAt(status), $"a {status} appointment either ends collection or it does not");
    }

    /// <summary>
    /// The other reader of the same money fact is in a different module: the outstanding queue lists a status
    /// as owed, and a status that strands a balance can never be one of those, or the debt would be both
    /// uncollectable and reported as chaseable. Nothing but this test crosses that seam.
    /// </summary>
    [Theory]
    [InlineData(AppointmentStatus.Pending)]
    [InlineData(AppointmentStatus.Confirmed)]
    [InlineData(AppointmentStatus.CheckedIn)]
    [InlineData(AppointmentStatus.InProgress)]
    [InlineData(AppointmentStatus.Completed)]
    [InlineData(AppointmentStatus.Cancelled)]
    [InlineData(AppointmentStatus.NoShow)]
    [InlineData(AppointmentStatus.Rescheduled)]
    public void EndsCollection_NeverNamesAStatusTheQueueStillLists(AppointmentStatus status)
    {
        if (!PaymentLedger.EndsCollection(status)) return;

        AppointmentStatusSet.Collectable.Should().NotContain(status,
            $"a {status} row is invisible to the queue by rule, so moving money into it writes the debt off");
    }

    private static bool ChargeRefusedAt(AppointmentStatus status)
    {
        try
        {
            ValidateCharge(PaymentLedger.From([Charged(10m)]), 10m, status: status);
            return false;
        }
        catch (BusinessRuleValidationException ex) when (ex.RuleName == "AppointmentNotChargeable")
        {
            return true;
        }
    }

    [Theory]
    [InlineData(AppointmentStatus.Cancelled)]
    [InlineData(AppointmentStatus.NoShow)]
    [InlineData(AppointmentStatus.Rescheduled)]
    public void ValidateWriteOff_UnpaidAndUnexplained_MustBeRefused(AppointmentStatus status)
    {
        var ex = Assert.Throws<BusinessRuleValidationException>(() =>
            PaymentLedger.ValidateWriteOff(status, 120m, PaymentLedger.From([Charged(20m)]), null));

        ex.RuleName.Should().Be("DebtWriteOffReasonRequired");
        ex.Message.Should().Contain("100.00 of 120.00",
            "the writer has to see the size of what is about to die with the status");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateWriteOff_BlankEveryShape_MustBeRefused(string? reason)
    {
        var act = () => PaymentLedger.ValidateWriteOff(
            AppointmentStatus.NoShow, 120m, PaymentLedger.From([]), reason);

        act.Should().Throw<BusinessRuleValidationException>()
            .WithMessage("*writes the balance off*");
    }

    [Fact]
    public void ValidateWriteOff_Explained_MustBeAccepted()
    {
        var act = () => PaymentLedger.ValidateWriteOff(
            AppointmentStatus.NoShow, 120m, PaymentLedger.From([]), "patient disputed the visit and we waived it");

        act.Should().NotThrow("a written-off debt with an author is a decision, not a leak");
    }

    [Fact]
    public void ValidateWriteOff_SettledBooking_NeedsNoReason()
    {
        var act = () => PaymentLedger.ValidateWriteOff(
            AppointmentStatus.NoShow, 120m, PaymentLedger.From([Charged(120m)]), null);

        act.Should().NotThrow("there is no balance to write off, so the gate has nothing to protect");
    }

    [Fact]
    public void ValidateWriteOff_LiveStatus_NeverAsksEvenWithADebt()
    {
        foreach (var status in new[]
                 {
                     AppointmentStatus.Pending,
                     AppointmentStatus.Confirmed,
                     AppointmentStatus.CheckedIn,
                     AppointmentStatus.InProgress,
                     AppointmentStatus.Completed
                 })
        {
            var act = () => PaymentLedger.ValidateWriteOff(status, 120m, PaymentLedger.From([]), null);

            act.Should().NotThrow($"a {status} appointment can still be charged, so no money is at risk");
        }
    }
}
