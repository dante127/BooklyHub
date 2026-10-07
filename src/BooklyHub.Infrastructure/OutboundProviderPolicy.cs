using BooklyHub.Infrastructure.Payments;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace BooklyHub.Infrastructure;

/// <summary>
/// The outbound providers this build registers are a stand-in and a refusal. The stand-in is the payment
/// simulator, which answers "Paid" with a synthetic transaction id for any positive amount, and the log-only
/// senders, which write a line instead of contacting an email, SMS or push service. Outside Production that is
/// exactly what development and tests want. In Production the same bindings make the ledger record money no
/// gateway ever captured and let the outbox mark patient reminders as delivered when nothing left the process.
/// The refusal is <c>None</c>: it charges nothing and sends nothing, and it says so in the shape the ledger,
/// the outbox row and the log all carry. So a deployment that has not wired a gateway is a configuration this
/// build can start, and a deployment that pretends to have one is not.
/// </summary>
public static class OutboundProviderPolicy
{
    /// <summary>The only payment and notification implementation in this build.</summary>
    public const string Simulated = "Simulated";

    /// <summary>Registers nothing outbound: every charge is refused and every send fails with its reason.</summary>
    public const string None = "None";

    public const string PaymentsKey = "Payments:Provider";
    public const string NotificationsKey = "Notifications:Provider";

    private static readonly ProviderSelection[] Selections =
    [
        new(
            PaymentsKey,
            "which implementation charges payments",
            $"{nameof(SimulatedPaymentProvider)} marks every positive charge as Paid with a synthetic txn_sim_* transaction id",
            "Implement BooklyHub.Application.Payments.IPaymentProvider against a real gateway"),
        new(
            NotificationsKey,
            "which implementation delivers email, SMS and push",
            "SimulatedEmailSender, SimulatedSmsSender and SimulatedPushNotificationSender write a '[... DISPATCHED]' log line and report success",
            "Implement BooklyHub.Application.Common.Interfaces.IEmailSender, ISmsSender and IPushNotificationSender against a real provider"),
    ];

    /// <summary>
    /// Reports every misconfigured provider at once: a Production host that is missing both selections
    /// should learn about both lies before it is restarted.
    /// </summary>
    public static void Validate(IConfiguration configuration, IHostEnvironment environment)
    {
        var failures = Selections
            .Select(selection => Check(configuration, environment, selection))
            .Where(error => error is not null)
            .ToList();

        if (failures.Count > 0)
        {
            throw new InvalidOperationException(string.Join(" ", failures));
        }
    }

    /// <summary>
    /// The comparison <see cref="Validate"/> already made, offered so the bindings in
    /// <c>DependencyInjection</c> cannot disagree with the guard that admitted them. Trimming and casing are
    /// decided here once: an operator that writes <c>Simulated </c> in a compose file gets the provider they
    /// named, and gets it from the same rule the refusal used.
    /// </summary>
    public static bool IsSelected(IConfiguration configuration, string key, string provider) =>
        string.Equals(configuration[key]?.Trim(), provider, StringComparison.OrdinalIgnoreCase);

    private static string? Check(IConfiguration configuration, IHostEnvironment environment, ProviderSelection selection)
    {
        var value = configuration[selection.Key];

        if (string.IsNullOrWhiteSpace(value))
        {
            return $"Configuration key '{selection.Key}' is missing or empty. It configures {selection.Configures}. " +
                   $"Name '{None}' for a deployment that does not do this at all — a charge is then refused and a " +
                   $"send fails with its reason, so nothing is recorded that did not happen — or '{Simulated}', the " +
                   $"only stand-in in this build, which is refused when the environment is Production. " +
                   "There is no built-in fallback value.";
        }

        if (IsSelected(configuration, selection.Key, None))
        {
            // Nothing to refuse: the None bindings report a failure rather than a success, in every environment.
            return null;
        }

        if (!IsSelected(configuration, selection.Key, Simulated))
        {
            return $"Configuration key '{selection.Key}' is '{value}', but this build registers only '{None}' and " +
                   $"'{Simulated}'. {selection.Remedy} and add it to DependencyInjection, or name one of the two.";
        }

        if (environment.IsProduction())
        {
            return $"Configuration key '{selection.Key}' is '{Simulated}', which is refused when the environment is Production: " +
                   $"{selection.SimulatedBehavior}, so this deployment would report outbound work it never did. " +
                   $"{selection.Remedy} and register it in BooklyHub.Infrastructure.DependencyInjection, " +
                   $"name '{None}' to run without it, or run this host under a non-Production environment name if " +
                   "it is only an evaluation.";
        }

        return null;
    }

    private sealed record ProviderSelection(string Key, string Configures, string SimulatedBehavior, string Remedy);
}
