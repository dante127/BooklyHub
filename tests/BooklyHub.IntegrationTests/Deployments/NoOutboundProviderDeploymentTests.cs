using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BooklyHub.Api.Controllers;
using BooklyHub.Application.Common.Interfaces;
using BooklyHub.Application.Payments;
using BooklyHub.Application.Security;
using BooklyHub.Domain.Entities.Appointments;
using BooklyHub.Domain.Entities.Customers;
using BooklyHub.Domain.Entities.Organizations;
using BooklyHub.Domain.Entities.Services;
using BooklyHub.Domain.Entities.StaffMembers;
using BooklyHub.Domain.Entities.Tenancy;
using BooklyHub.Domain.Enums;
using BooklyHub.Infrastructure.BackgroundJobs;
using BooklyHub.Infrastructure.Data;
using BooklyHub.Infrastructure.Outbound;
using BooklyHub.Infrastructure.Outbox;
using BooklyHub.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BooklyHub.IntegrationTests.Deployments;

/// <summary>
/// What a client deployment gets when it names <c>None</c> for both outbound providers: the API serves, the
/// money paths refuse instead of inventing, and neither worker records a delivery it did not make. This is the
/// profile Production is allowed to start on, so these facts are the difference between "the guard refused a
/// lie" and "the deployment that told the truth still works".
/// </summary>
public sealed class NoOutboundProviderDeploymentTests : IAsyncLifetime
{
    private sealed record Graph(Guid TenantId, Guid LocationId, Guid ServiceId, Guid StaffId, Guid CustomerId);

    private sealed class NoOutboundFactory : BooklyHubWebApplicationFactory
    {
        // Not ConfigureTestConfiguration: measured here, and the reason this class writes it down. The two
        // provider keys are read inside AddInfrastructure, which Program.cs calls before builder.Build() — and
        // WebApplicationFactory's in-memory configuration source does not exist until Build() runs the
        // ConfigureAppConfiguration callbacks. A host that set these keys that way booted the simulator and
        // charged txn_sim_* while its own configuration answered "None". Service descriptors are the part of
        // this host the test can still reach, so the selection is replaced there; the config-to-binding rule
        // itself is pinned in OutboundProviderPolicyTests, where the value is read at registration time.
        protected override void ConfigureTestServices(IServiceCollection services)
        {
            services.RemoveAll<IPaymentProvider>();
            services.AddSingleton<IPaymentProvider>(new NonePaymentProvider());

            services.RemoveAll<IEmailSender>();
            services.AddSingleton<IEmailSender>(new NoneEmailSender());

            services.RemoveAll<ISmsSender>();
            services.AddSingleton<ISmsSender>(new NoneSmsSender());

            services.RemoveAll<IPushNotificationSender>();
            services.AddSingleton<IPushNotificationSender>(new NonePushNotificationSender());
        }
    }

    // Not an IClassFixture: each fact gets its own database, so the ledger and notification counts below
    // cannot be moved by another fact's rows.
    private readonly NoOutboundFactory _factory = new();

    public Task InitializeAsync() => _factory.InitializeAsync();

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private async Task<Graph> SeedGraphAsync(
        DateTime startAtUtc,
        AppointmentStatus status,
        decimal price = 120.00m)
    {
        var graph = new Graph(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        db.Tenants.Add(new Tenant(graph.TenantId, "Cash Clinic", $"cash-{graph.TenantId:N}", "UTC"));

        db.Locations.Add(new Location
        {
            Id = graph.LocationId,
            TenantId = graph.TenantId,
            Name = "Front",
            Address = "1 Front St",
            City = "Springfield",
            Country = "US",
            TimeZoneId = "UTC"
        });

        db.Services.Add(new Service
        {
            Id = graph.ServiceId,
            TenantId = graph.TenantId,
            Name = "Consult",
            DurationMinutes = 30,
            Price = price
        });

        var staff = new Staff
        {
            Id = graph.StaffId,
            TenantId = graph.TenantId,
            LocationId = graph.LocationId,
            FirstName = "Doc",
            LastName = "One",
            Email = $"doc-{graph.StaffId:N}@cash.test"
        };
        staff.StaffServices.Add(new StaffService { TenantId = graph.TenantId, StaffId = graph.StaffId, ServiceId = graph.ServiceId });
        db.StaffMembers.Add(staff);

        db.Customers.Add(new Customer
        {
            Id = graph.CustomerId,
            TenantId = graph.TenantId,
            FirstName = "Ann",
            LastName = "Patient",
            Email = $"ann-{graph.CustomerId:N}@cash.test"
        });

        var appointment = Appointment.Create(
            graph.TenantId,
            graph.LocationId,
            graph.ServiceId,
            graph.StaffId,
            graph.CustomerId,
            startAtUtc,
            startAtUtc.AddMinutes(30),
            30,
            price,
            "USD");

        if (status != AppointmentStatus.Pending)
        {
            appointment.TransitionTo(status, DateTime.UtcNow, "seeded for the no-provider facts");
        }

        db.Appointments.Add(appointment);
        await db.SaveChangesAsync();

        return graph;
    }

    private OutboxProcessorBackgroundService OutboxWorker() => new(
        _factory.Services.GetRequiredService<IServiceScopeFactory>(),
        _factory.Services.GetRequiredService<ILogger<OutboxProcessorBackgroundService>>());

    private AppointmentReminderBackgroundService ReminderWorker() => new(
        _factory.Services.GetRequiredService<IServiceScopeFactory>(),
        _factory.Services.GetRequiredService<ILogger<AppointmentReminderBackgroundService>>());

    [Fact]
    public void TheConfigurationThatSelectedTheProviders_MustNotBeReadAsTheProvidersThisHostRuns()
    {
        // The trap this class writes down rather than hides: after the overrides above, this host's own
        // settings still name the simulator, because the value was read and acted on before the test could
        // change it. A future fact that asserts a provider choice through configuration would be asserting
        // the Development file, not its own fixture.
        using var scope = _factory.Services.CreateScope();
        var provider = scope.ServiceProvider.GetRequiredService<IPaymentProvider>();

        _factory.Services.GetRequiredService<IConfiguration>()["Payments:Provider"]
            .Should().Be("Simulated");
        provider.Should().BeOfType<NonePaymentProvider>();
    }

    [Fact]
    public async Task TheCatalogReads_MustStillServeADeploymentWithNoOutboundProviders()
    {
        var graph = await SeedGraphAsync(DateTime.UtcNow.AddDays(2), AppointmentStatus.Pending);

        var client = _factory.CreateClientForTenant(graph.TenantId, Roles.TenantOwner);
        var response = await client.GetAsync("/api/v1/services");

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        var body = await response.Content.ReadAsStringAsync();
        JsonDocument.Parse(body).RootElement.GetProperty("items")[0].GetProperty("name").GetString()
            .Should().Be("Consult", "a refusal of outbound work must not reach the reads a clinic runs on");
    }

    [Fact]
    public async Task AChargeOnADeploymentWithNoGateway_MustBeRefusedAndLeaveTheLedgerEmpty()
    {
        var graph = await SeedGraphAsync(DateTime.UtcNow.AddDays(2), AppointmentStatus.Confirmed);
        var appointmentId = await NewAppointmentIdAsync(graph);

        var response = await _factory.CreateClientForTenant(graph.TenantId, Roles.Accountant)
            .PostAsJsonAsync("/api/v1/payments/charge",
                new PaymentsController.ProcessPaymentApiRequest(appointmentId, 120.00m, "USD"));

        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be((HttpStatusCode)422, body);

        using var refused = JsonDocument.Parse(body);
        refused.RootElement.GetProperty("rule").GetString().Should().Be("PaymentFailed");
        refused.RootElement.GetProperty("detail").GetString()!.Should().Contain("Payments:Provider")
            .And.Contain("no money", "the refusal has to say that nothing was charged, not just that it failed");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        (await db.Payments.IgnoreQueryFilters().CountAsync()).Should().Be(0);
        (await db.PaymentTransactions.IgnoreQueryFilters().CountAsync()).Should().Be(0);

        var appointment = await db.Appointments.IgnoreQueryFilters().SingleAsync(a => a.Id == appointmentId);
        appointment.Status.Should().Be(AppointmentStatus.Confirmed,
            "the simulator used to move a Pending booking to Confirmed on a charge that never happened");
    }

    [Fact]
    public async Task TheOutboxPassOnADeploymentWithNoSenders_MustLeaveTheMessageUnprocessedWithItsReason()
    {
        await SeedGraphAsync(DateTime.UtcNow.AddDays(2), AppointmentStatus.Pending);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (await db.OutboxMessages.CountAsync(m => m.ProcessedOnUtc == null))
                .Should().BeGreaterThan(0, "creating the appointment must still queue the domain event");
        }

        await OutboxWorker().ProcessPendingMessagesAsync(CancellationToken.None);

        using var verify = _factory.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var failed = await verifyDb.OutboxMessages
            .IgnoreQueryFilters()
            .Where(m => m.ProcessedOnUtc == null)
            .ToListAsync();

        failed.Should().NotBeEmpty("an undeliverable event is not a delivered one");
        failed.Should().AllSatisfy(m => m.Error.Should().Contain("Notifications:Provider"));
    }

    [Fact]
    public async Task TheReminderSweepOnADeploymentWithNoSenders_MustRecordNoDelivery()
    {
        await SeedGraphAsync(DateTime.UtcNow.AddHours(6), AppointmentStatus.Confirmed);

        var sent = await ReminderWorker().ProcessUpcomingRemindersAsync(CancellationToken.None);

        sent.Should().Be(0);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        (await db.NotificationRecords.IgnoreQueryFilters().CountAsync()).Should().Be(0,
            "a reminder row is the claim that a message left, and nothing left");
    }

    private async Task<Guid> NewAppointmentIdAsync(Graph graph)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.Appointments.IgnoreQueryFilters()
            .Where(a => a.TenantId == graph.TenantId)
            .Select(a => a.Id)
            .SingleAsync();
    }
}
