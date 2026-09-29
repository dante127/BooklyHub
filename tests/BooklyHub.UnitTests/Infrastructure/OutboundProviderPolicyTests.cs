using BooklyHub.Application.Common.Interfaces;
using BooklyHub.Application.Payments;
using BooklyHub.Infrastructure;
using BooklyHub.Infrastructure.BackgroundJobs;
using BooklyHub.Infrastructure.Outbox;
using BooklyHub.Infrastructure.Payments;
using BooklyHub.Infrastructure.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;

namespace BooklyHub.UnitTests.Infrastructure;

/// <summary>
/// The container binds stand-in outbound providers: a payment simulator that always answers "Paid" and senders
/// that only log. These tests pin who may select them, that nothing is selected silently, and that Production
/// cannot start on the stand-ins.
/// </summary>
public class OutboundProviderPolicyTests
{
    private const string PaymentsKey = "Payments:Provider";
    private const string NotificationsKey = "Notifications:Provider";

    [Theory]
    [InlineData("Development")]
    [InlineData("Staging")]
    public void ANonProductionHost_MaySelectTheSimulatedProviders(string environmentName)
    {
        var act = () => Validate(new Dictionary<string, string?>
        {
            [PaymentsKey] = "Simulated",
            [NotificationsKey] = "Simulated"
        }, environmentName);

        act.Should().NotThrow();
    }

    [Fact]
    public void SurroundingWhitespaceAndDifferentCasing_MustStillCountAsSimulated()
    {
        var act = () => Validate(new Dictionary<string, string?>
        {
            [PaymentsKey] = "  simulated ",
            [NotificationsKey] = "Simulated"
        }, "Development");

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(PaymentsKey)]
    [InlineData(NotificationsKey)]
    public void AProviderThatIsNeverNamed_MustAbortStartupInsteadOfDefaultingToSimulated(string key)
    {
        var values = new Dictionary<string, string?>
        {
            [PaymentsKey] = "Simulated",
            [NotificationsKey] = "Simulated"
        };
        values.Remove(key);

        var act = () => Validate(values, "Development");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"Configuration key '{key}' is missing or empty*There is no built-in fallback value.");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyProviderName_MustAbortStartupTheSameWay(string value)
    {
        var act = () => Validate(new Dictionary<string, string?>
        {
            [PaymentsKey] = value,
            [NotificationsKey] = "Simulated"
        }, "Development");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"Configuration key '{PaymentsKey}' is missing or empty*");
    }

    [Fact]
    public void AProviderNobodyHasImplemented_MustAbortStartupInsteadOfSilentlyFallingBack()
    {
        // PaymentProviderType.Stripe exists in the domain but no implementation does, so this is the
        // value an operator would plausibly configure by mistake.
        var act = () => Validate(new Dictionary<string, string?>
        {
            [PaymentsKey] = "Stripe",
            [NotificationsKey] = "Simulated"
        }, "Development");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"Configuration key '{PaymentsKey}' is 'Stripe', but 'Simulated' is the only implementation in this build*");
    }

    [Fact]
    public void ProductionOnTheSimulatedProviders_MustRefuseAndExplainTheSyntheticCharge()
    {
        var act = () => Validate(new Dictionary<string, string?>
        {
            [PaymentsKey] = "Simulated",
            [NotificationsKey] = "Simulated"
        }, "Production");

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain(PaymentsKey)
            .And.Contain("refused when the environment is Production")
            .And.Contain("txn_sim_")
            .And.Contain("IPaymentProvider");
    }

    [Fact]
    public void Production_MustRefuseTheLogOnlySendersInTheSameStartupPass()
    {
        var act = () => Validate(new Dictionary<string, string?>
        {
            [PaymentsKey] = "Simulated",
            [NotificationsKey] = "Simulated"
        }, "Production");

        // An operator that fixes only the payment selection must not have to restart to discover the
        // notification one, so both refusals are reported together.
        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain(NotificationsKey)
            .And.Contain("SimulatedEmailSender")
            .And.Contain("DISPATCHED");
    }

    [Fact]
    public void RegisteringInfrastructureInProduction_MustBindNoOutboundProviderAtAll()
    {
        var services = new ServiceCollection();

        // Production also refuses a missing Redis connection, so this host supplies one: without it the
        // test would pass on the wrong guard.
        var act = () => services.AddInfrastructure(Build(PaymentsAndNotifications("Simulated", "Simulated", "redis:6379")), HostEnvironment("Production"));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*refused when the environment is Production*");

        services.Should().NotContain(d => d.ServiceType == typeof(IPaymentProvider));
        services.Should().NotContain(d => d.ServiceType == typeof(IEmailSender));
        services.Should().NotContain(d => d.ServiceType == typeof(ISmsSender));
        services.Should().NotContain(d => d.ServiceType == typeof(IPushNotificationSender));
    }

    [Fact]
    public void RegisteringInfrastructureOutsideProduction_MustBindTheSimulatedProviders()
    {
        var services = new ServiceCollection();

        services.AddInfrastructure(Build(PaymentsAndNotifications("Simulated", "Simulated")), HostEnvironment("Development"));

        ImplementedType<IPaymentProvider>(services).Should().Be<SimulatedPaymentProvider>();
        ImplementedType<IEmailSender>(services).Should().Be<SimulatedEmailSender>();
        ImplementedType<ISmsSender>(services).Should().Be<SimulatedSmsSender>();
        ImplementedType<IPushNotificationSender>(services).Should().Be<SimulatedPushNotificationSender>();
    }

    [Fact]
    public void RegisteringInfrastructure_MustBindEveryPollingWorker()
    {
        var services = new ServiceCollection();

        services.AddInfrastructure(Build(PaymentsAndNotifications("Simulated", "Simulated")), HostEnvironment("Development"));

        // The test host removes these workers so its queries stay deterministic, which means no integration
        // test would notice a sweep that never gets registered at all. This does.
        var workers = services
            .Where(d => d.ServiceType == typeof(IHostedService))
            .Select(d => d.ImplementationType)
            .ToList();

        workers.Should().Contain(new[]
        {
            typeof(OutboxProcessorBackgroundService),
            typeof(AppointmentReminderBackgroundService),
            typeof(AppointmentNoShowBackgroundService)
        });
    }

    private static void Validate(Dictionary<string, string?> values, string environmentName) =>
        OutboundProviderPolicy.Validate(Build(values), HostEnvironment(environmentName));

    private static Dictionary<string, string?> PaymentsAndNotifications(
        string payments, string notifications, string redis = "") => new()
    {
        ["ConnectionStrings:DefaultConnection"] = "Server=localhost;Database=BooklyHub;Trusted_Connection=True;TrustServerCertificate=True",
        ["ConnectionStrings:Redis"] = redis,
        ["Jwt:Secret"] = "Testing_Super_Secret_Key_For_BooklyHub_SaaS_2026_1234567890!",
        ["Jwt:Issuer"] = "BooklyHub",
        ["Jwt:Audience"] = "BooklyHubClients",
        [PaymentsKey] = payments,
        [NotificationsKey] = notifications
    };

    private static Type? ImplementedType<T>(ServiceCollection services) =>
        services.Single(d => d.ServiceType == typeof(T)).ImplementationType;

    private static IConfiguration Build(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static IHostEnvironment HostEnvironment(string name)
    {
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(name);
        return environment;
    }
}
