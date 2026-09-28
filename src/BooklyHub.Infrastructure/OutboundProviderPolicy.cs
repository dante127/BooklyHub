using BooklyHub.Infrastructure.Payments;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace BooklyHub.Infrastructure;

/// <summary>
/// The outbound providers this build registers are stand-ins: the payment simulator answers "Paid" with a
/// synthetic transaction id for any positive amount, and the notification senders write a log line instead of
/// contacting an email, SMS or push service. Outside Production that is exactly what development and tests
/// want. In Production the same bindings make the ledger record money no gateway ever captured and let the
/// outbox mark patient reminders as delivered when nothing left the process, so every deployment has to name
/// what it uses and Production is refused until the real providers exist.
/// </summary>
public static class OutboundProviderPolicy
{
    /// <summary>The only payment and notification implementation in this build.</summary>
    public const string Simulated = "Simulated";

    private static readonly ProviderSelection[] Selections =
    [
        new(
            "Payments:Provider",
            "which implementation charges payments",
            $"{nameof(SimulatedPaymentProvider)} marks every positive charge as Paid with a synthetic txn_sim_* transaction id",
            "Implement BooklyHub.Application.Payments.IPaymentProvider against a real gateway"),
        new(
            "Notifications:Provider",
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

    private static string? Check(IConfiguration configuration, IHostEnvironment environment, ProviderSelection selection)
    {
        var value = configuration[selection.Key];

        if (string.IsNullOrWhiteSpace(value))
        {
            return $"Configuration key '{selection.Key}' is missing or empty. It configures {selection.Configures}. " +
                   $"'{Simulated}' is the only implementation in this build and it is refused in Production. " +
                   "There is no built-in fallback value.";
        }

        if (!string.Equals(value.Trim(), Simulated, StringComparison.OrdinalIgnoreCase))
        {
            return $"Configuration key '{selection.Key}' is '{value}', but '{Simulated}' is the only implementation in this build. " +
                   $"Register the provider before naming it here. {selection.Remedy} and add it to DependencyInjection.";
        }

        if (environment.IsProduction())
        {
            return $"Configuration key '{selection.Key}' is '{Simulated}', which is refused when the environment is Production: " +
                   $"{selection.SimulatedBehavior}, so this deployment would report outbound work it never did. " +
                   $"{selection.Remedy} and register it in BooklyHub.Infrastructure.DependencyInjection, " +
                   "or run this host under a non-Production environment name if it is only an evaluation.";
        }

        return null;
    }

    private sealed record ProviderSelection(string Key, string Configures, string SimulatedBehavior, string Remedy);
}
