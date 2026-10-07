using BooklyHub.Application.Common.Interfaces;
using BooklyHub.Application.Payments;
using BooklyHub.Domain.Enums;

namespace BooklyHub.Infrastructure.Outbound;

/// <summary>
/// What an operator selects when this deployment moves no money and sends no message. These are not a
/// development convenience and not a fallback: <c>Simulated</c> exists to be believed by tests and is refused
/// in Production because its output is invented, and <c>None</c> exists to be survived by a real deployment
/// because its output is a refusal the ledger and the outbox can both show the reason for.
/// </summary>
public sealed class NonePaymentProvider : IPaymentProvider
{
    /// <summary>
    /// Never persisted. A charge this provider answers is refused by the caller before a <c>Payment</c> row is
    /// constructed, which is the whole difference between it and the simulator.
    /// </summary>
    public PaymentProviderType ProviderType => PaymentProviderType.None;

    public Task<PaymentResult> ProcessPaymentAsync(ProcessPaymentRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(new PaymentResult(false, null, PaymentStatus.Failed, OutboundNotConfigured.Payments));

    public Task<RefundResult> ProcessRefundAsync(ProcessRefundRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(new RefundResult(false, null, PaymentStatus.Failed, OutboundNotConfigured.Payments));
}

/// <summary>
/// The senders throw rather than return, because their signature has no failure value to return: a completed
/// task is the only thing the caller reads as "it left". Failing here is what keeps a reminder unrecorded and
/// an outbox row's <c>Error</c> filled with the reason, which is the honest version of a deployment with no
/// delivery provider.
/// </summary>
public sealed class NoneEmailSender : IEmailSender
{
    public Task SendEmailAsync(string to, string subject, string htmlBody, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException(OutboundNotConfigured.Notifications);
}

public sealed class NoneSmsSender : ISmsSender
{
    public Task SendSmsAsync(string phoneNumber, string message, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException(OutboundNotConfigured.Notifications);
}

public sealed class NonePushNotificationSender : IPushNotificationSender
{
    public Task SendPushAsync(string recipientId, string title, string message, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException(OutboundNotConfigured.Notifications);
}

/// <summary>
/// One source for the two texts, because they surface in three places that must not disagree about what was
/// refused: the 422 a charge answers, the <c>OutboxMessages.Error</c> column a dead-lettered notification
/// carries, and the reminder worker's log line.
/// </summary>
public static class OutboundNotConfigured
{
    public const string Payments =
        "This deployment moves no money: 'Payments:Provider' is 'None'. Nothing was charged and nothing was " +
        "written to the ledger. Register an IPaymentProvider against a real gateway and name it in that key.";

    public const string Notifications =
        "This deployment sends no messages: 'Notifications:Provider' is 'None'. Nothing left the process, so " +
        "nothing is recorded as delivered. Register the sender interfaces against a real provider and name it in that key.";
}
