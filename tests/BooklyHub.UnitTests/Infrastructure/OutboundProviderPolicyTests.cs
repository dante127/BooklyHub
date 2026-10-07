using BooklyHub.Application.Common.Interfaces;
using BooklyHub.Application.Payments;
using BooklyHub.Domain.Enums;
using BooklyHub.Infrastructure;
using BooklyHub.Infrastructure.BackgroundJobs;
using BooklyHub.Infrastructure.Outbound;
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
/// The container binds outbound providers from two names the operator has to type: a stand-in that invents
/// money and delivery, and a refusal that does neither. These tests pin who may select each, that nothing is
/// selected silently, that Production cannot start on the stand-in, and that the selection Production does
/// allow binds providers whose answers cannot be mistaken for work that happened.
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
    public void AnUnregisteredProviderName_MustAbortStartupAndNameTheTwoThatExist()
    {
        // PaymentProviderType.Stripe exists in the domain but no implementation does, so this is the
        // value an operator would plausibly configure by mistake.
        var act = () => Validate(new Dictionary<string, string?>
        {
            [PaymentsKey] = "Stripe",
            [NotificationsKey] = "Simulated"
        }, "Development");

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain($"Configuration key '{PaymentsKey}' is 'Stripe'")
            .And.Contain("'None'")
            .And.Contain("'Simulated'");
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

    [Fact]
    public void Production_MayStartOnTheProvidersThatRefuseEverything()
    {
        // The point of the guard was never "Production must be unreachable": it is that Production must not
        // report outbound work it did not do. 'None' does not report it, so there is nothing here to refuse,
        // and a clinic that takes payment on arrival is a deployment rather than a plan.
        var act = () => Validate(new Dictionary<string, string?>
        {
            [PaymentsKey] = "None",
            [NotificationsKey] = "None"
        }, "Production");

        act.Should().NotThrow();
    }

    [Fact]
    public void SelectingNone_MustBindTheProvidersThatRefuseInsteadOfTheOnesThatInvent()
    {
        var services = new ServiceCollection();

        // Production also requires a Redis connection, so this host supplies one: without it the fact would
        // be about the cache guard rather than about what the None selection binds.
        services.AddInfrastructure(Build(PaymentsAndNotifications("None", "None", "redis:6379")), HostEnvironment("Production"));

        ImplementedType<IPaymentProvider>(services).Should().Be<NonePaymentProvider>();
        ImplementedType<IEmailSender>(services).Should().Be<NoneEmailSender>();
        ImplementedType<ISmsSender>(services).Should().Be<NoneSmsSender>();
        ImplementedType<IPushNotificationSender>(services).Should().Be<NonePushNotificationSender>();
    }

    [Fact]
    public void TheBinding_MustReadTheNameTheSameWayTheGuardDid()
    {
        // The guard trims and ignores casing, so if the binding did not, a compose file that says
        // " simulated " would pass validation and then bind the refusing provider — the deployment would
        // charge nothing while its operator believes the simulator is running.
        var services = new ServiceCollection();

        services.AddInfrastructure(Build(PaymentsAndNotifications("  Simulated ", "Simulated ")), HostEnvironment("Development"));

        ImplementedType<IPaymentProvider>(services).Should().Be<SimulatedPaymentProvider>();
        ImplementedType<IEmailSender>(services).Should().Be<SimulatedEmailSender>();
    }

    [Fact]
    public async Task NonePaymentProvider_MustRefuseTheChargeTheSimulatorWouldHaveApproved()
    {
        var provider = new NonePaymentProvider();

        var result = await provider.ProcessPaymentAsync(
            new ProcessPaymentRequest(Guid.NewGuid(), Guid.NewGuid(), 120.00m, "USD"));

        result.IsSuccess.Should().BeFalse();
        result.Status.Should().Be(PaymentStatus.Failed);
        result.TransactionId.Should().BeNull("a synthetic id is what makes the simulator's answer believed");
        result.ErrorMessage.Should().Contain("Payments:Provider");
        provider.ProviderType.Should().Be(PaymentProviderType.None);
    }

    [Fact]
    public async Task NonePaymentProvider_MustRefuseARefundWithoutInventingARefundId()
    {
        var provider = new NonePaymentProvider();

        var result = await provider.ProcessRefundAsync(
            new ProcessRefundRequest(Guid.NewGuid(), Guid.NewGuid(), 120.00m, "test"));

        result.IsSuccess.Should().BeFalse();
        result.Status.Should().Be(PaymentStatus.Failed);
        result.RefundId.Should().BeNull();
        result.ErrorMessage.Should().Contain("Payments:Provider");
    }

    [Fact]
    public async Task EveryNoneSender_MustThrowBecauseACompletedTaskIsWhatTheCallerReadsAsDelivered()
    {
        // Task.CompletedTask is the only "it worked" these signatures can say, and both workers read that as
        // permission to record the send. Throwing is the shape that keeps a reminder unrecorded.
        var attempts = new Func<Task>[]
        {
            () => new NoneEmailSender().SendEmailAsync("a@test", "subject", "body"),
            () => new NoneSmsSender().SendSmsAsync("+963", "message"),
            () => new NonePushNotificationSender().SendPushAsync("dev-1", "title", "message"),
        };

        foreach (var send in attempts)
        {
            await send.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Notifications:Provider*");
        }
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
