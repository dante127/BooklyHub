using System.Text.Json;
using BooklyHub.Application.Common.Interfaces;
using BooklyHub.Domain.Common;
using BooklyHub.Domain.Entities.Customers;
using BooklyHub.Domain.Entities.Organizations;
using BooklyHub.Domain.Entities.Services;
using BooklyHub.Domain.Entities.StaffMembers;
using BooklyHub.Domain.Entities.System;
using BooklyHub.Domain.Entities.Tenancy;
using BooklyHub.Domain.Events;
using BooklyHub.Infrastructure.Data;
using BooklyHub.Infrastructure.Outbox;
using BooklyHub.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BooklyHub.IntegrationTests.Notifications;

/// <summary>
/// The outbox worker publishes in a platform-admin scope, so the tenant query filter is deliberately open
/// there and a notification handler that reads a customer by id alone will find a row belonging to any
/// tenant. These tests pin the handler's own scope: an event that names one tenant but points at another
/// tenant's customer must produce no delivery at all. The messages below are inserted by hand because no
/// live path builds one today — both create commands validate that the customer belongs to the requesting
/// tenant — so this is the structural guarantee, not a reproduction of a reachable leak.
/// </summary>
public sealed class CrossTenantNotificationTests : IAsyncLifetime
{
    private sealed record SentEmail(string To, string Subject, string Body);

    private sealed record SentSms(string To, string Body);

    private sealed class RecordingEmailSender : IEmailSender
    {
        public List<SentEmail> Sent { get; } = [];

        public Task SendEmailAsync(string to, string subject, string htmlBody, CancellationToken cancellationToken = default)
        {
            lock (Sent)
            {
                Sent.Add(new SentEmail(to, subject, htmlBody));
            }

            return Task.CompletedTask;
        }
    }

    private sealed class RecordingSmsSender : ISmsSender
    {
        public List<SentSms> Sent { get; } = [];

        public Task SendSmsAsync(string phoneNumber, string message, CancellationToken cancellationToken = default)
        {
            lock (Sent)
            {
                Sent.Add(new SentSms(phoneNumber, message));
            }

            return Task.CompletedTask;
        }
    }

    private sealed class CrossTenantWebApplicationFactory : BooklyHubWebApplicationFactory
    {
        public RecordingEmailSender EmailSender { get; } = new();

        public RecordingSmsSender SmsSender { get; } = new();

        protected override void ConfigureTestServices(IServiceCollection services)
        {
            services.RemoveAll<IEmailSender>();
            services.AddSingleton<IEmailSender>(EmailSender);
            services.RemoveAll<ISmsSender>();
            services.AddSingleton<ISmsSender>(SmsSender);
        }
    }

    private sealed record TenantGraph(Guid TenantId, Guid ServiceId, Guid CustomerId, string CustomerEmail, string ServiceName);

    private readonly CrossTenantWebApplicationFactory _factory = new();

    public Task InitializeAsync() => _factory.InitializeAsync();

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private async Task<TenantGraph> SeedTenantAsync(string slug, string clinicName, string serviceName, string customerEmail, string? customerPhone = null)
    {
        var graph = new TenantGraph(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), customerEmail, serviceName);
        var locationId = Guid.NewGuid();
        var staffId = Guid.NewGuid();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        db.Tenants.Add(new Tenant(graph.TenantId, clinicName, slug, "UTC"));

        db.Locations.Add(new Location
        {
            Id = locationId,
            TenantId = graph.TenantId,
            Name = "Main",
            Address = "1 Main St",
            City = "Springfield",
            Country = "US",
            TimeZoneId = "UTC"
        });

        db.Services.Add(new Service
        {
            Id = graph.ServiceId,
            TenantId = graph.TenantId,
            Name = graph.ServiceName,
            DurationMinutes = 30,
            Price = 80.00m
        });

        var staff = new Staff
        {
            Id = staffId,
            TenantId = graph.TenantId,
            LocationId = locationId,
            FirstName = "Rita",
            LastName = "Skeeter",
            Email = $"rita@{slug}.test"
        };
        staff.StaffServices.Add(new StaffService { TenantId = graph.TenantId, StaffId = staffId, ServiceId = graph.ServiceId });
        db.StaffMembers.Add(staff);

        db.Customers.Add(new Customer
        {
            Id = graph.CustomerId,
            TenantId = graph.TenantId,
            FirstName = "Jane",
            LastName = "Doe",
            Email = graph.CustomerEmail,
            PhoneNumber = customerPhone
        });

        await db.SaveChangesAsync();

        return graph;
    }

    private async Task QueueEventAsync(IDomainEvent domainEvent)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        db.OutboxMessages.Add(new OutboxMessage(
            domainEvent.GetType().Name,
            JsonSerializer.Serialize(domainEvent, domainEvent.GetType())));

        await db.SaveChangesAsync();
    }

    private OutboxProcessorBackgroundService OutboxWorker() => new(
        _factory.Services.GetRequiredService<IServiceScopeFactory>(),
        _factory.Services.GetRequiredService<ILogger<OutboxProcessorBackgroundService>>());

    [Fact]
    public async Task CreatedEvent_NamingAnotherTenantsCustomer_MustSendNoEmail()
    {
        var tenantA = await SeedTenantAsync("cross-a", "Clinic A", "Check-up", "a.customer@clinic-a.test");
        var tenantB = await SeedTenantAsync("cross-b", "Clinic B", "Blood Test", "b.customer@clinic-b.test");

        await QueueEventAsync(new AppointmentCreatedEvent(
            tenantA.TenantId,
            Guid.NewGuid(),
            tenantB.CustomerId,
            Guid.NewGuid(),
            tenantA.ServiceId,
            DateTime.UtcNow.AddDays(2),
            DateTime.UtcNow.AddDays(2).AddMinutes(30)));

        var processed = await OutboxWorker().ProcessPendingMessagesAsync(CancellationToken.None);

        processed.Should().BeGreaterThan(0, "the message is consumed either way; only the delivery is in question");
        _factory.EmailSender.Sent.Should().BeEmpty(
            "the event's tenant does not own that customer, so nobody is mailed about an appointment they do not have");
    }

    [Fact]
    public async Task CreatedEvent_NamingAnotherTenantsService_MustNotPutThatNameInTheSubject()
    {
        var tenantA = await SeedTenantAsync("cross-a", "Clinic A", "Check-up", "a.customer@clinic-a.test");
        var tenantB = await SeedTenantAsync("cross-b", "Clinic B", "Blood Test", "b.customer@clinic-b.test");

        await QueueEventAsync(new AppointmentCreatedEvent(
            tenantA.TenantId,
            Guid.NewGuid(),
            tenantA.CustomerId,
            Guid.NewGuid(),
            tenantB.ServiceId,
            DateTime.UtcNow.AddDays(2),
            DateTime.UtcNow.AddDays(2).AddMinutes(30)));

        await OutboxWorker().ProcessPendingMessagesAsync(CancellationToken.None);

        _factory.EmailSender.Sent.Should().ContainSingle("the customer is the event's own, so the confirmation still goes out");
        _factory.EmailSender.Sent[0].To.Should().Be(tenantA.CustomerEmail);
        _factory.EmailSender.Sent[0].Subject.Should().NotContain(tenantB.ServiceName)
            .And.Contain("Service", "an unreadable service falls back to the generic word instead of another tenant's catalog");
    }

    [Fact]
    public async Task CancelledEvent_NamingAnotherTenantsCustomer_MustSendNoEmail()
    {
        var tenantA = await SeedTenantAsync("cross-a", "Clinic A", "Check-up", "a.customer@clinic-a.test");
        var tenantB = await SeedTenantAsync("cross-b", "Clinic B", "Blood Test", "b.customer@clinic-b.test");

        await QueueEventAsync(new AppointmentCancelledEvent(
            tenantA.TenantId,
            Guid.NewGuid(),
            tenantB.CustomerId,
            "Staff unavailable"));

        await OutboxWorker().ProcessPendingMessagesAsync(CancellationToken.None);

        _factory.EmailSender.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task RescheduledEvent_NamingAnotherTenantsCustomer_MustSendNoEmail()
    {
        var tenantA = await SeedTenantAsync("cross-a", "Clinic A", "Check-up", "a.customer@clinic-a.test");
        var tenantB = await SeedTenantAsync("cross-b", "Clinic B", "Blood Test", "b.customer@clinic-b.test");

        await QueueEventAsync(new AppointmentRescheduledEvent(
            tenantA.TenantId,
            Guid.NewGuid(),
            tenantB.CustomerId,
            DateTime.UtcNow.AddDays(2),
            DateTime.UtcNow.AddDays(3),
            DateTime.UtcNow.AddDays(3).AddMinutes(30),
            "Staff leave"));

        await OutboxWorker().ProcessPendingMessagesAsync(CancellationToken.None);

        _factory.EmailSender.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task CreatedEvent_WithinOneTenant_MustStillMailItsOwnCustomer()
    {
        var tenantA = await SeedTenantAsync("cross-a", "Clinic A", "Check-up", "a.customer@clinic-a.test");

        await QueueEventAsync(new AppointmentCreatedEvent(
            tenantA.TenantId,
            Guid.NewGuid(),
            tenantA.CustomerId,
            Guid.NewGuid(),
            tenantA.ServiceId,
            DateTime.UtcNow.AddDays(2),
            DateTime.UtcNow.AddDays(2).AddMinutes(30)));

        await OutboxWorker().ProcessPendingMessagesAsync(CancellationToken.None);

        _factory.EmailSender.Sent.Should().ContainSingle(
            "the guard must not silence real deliveries; without this the empty inboxes above prove nothing");
        _factory.EmailSender.Sent[0].To.Should().Be(tenantA.CustomerEmail);
        _factory.EmailSender.Sent[0].Subject.Should().Contain("Check-up");
    }

    [Fact]
    public async Task CreatedEvent_NamingAnotherTenantsCustomer_MustSendNoSms()
    {
        var tenantA = await SeedTenantAsync("cross-a", "Clinic A", "Check-up", "a.customer@clinic-a.test");
        var tenantB = await SeedTenantAsync("cross-b", "Clinic B", "Blood Test", "b.customer@clinic-b.test", "+963900000002");

        await QueueEventAsync(new AppointmentCreatedEvent(
            tenantA.TenantId,
            Guid.NewGuid(),
            tenantB.CustomerId,
            Guid.NewGuid(),
            tenantA.ServiceId,
            DateTime.UtcNow.AddDays(2),
            DateTime.UtcNow.AddDays(2).AddMinutes(30)));

        await OutboxWorker().ProcessPendingMessagesAsync(CancellationToken.None);

        _factory.SmsSender.Sent.Should().BeEmpty(
            "the SMS branch is fed by the same customer row, so it must be closed by the same guard");
    }

    [Fact]
    public async Task CreatedEvent_WithinOneTenant_MustAlsoTextItsOwnCustomer()
    {
        var tenantA = await SeedTenantAsync("cross-a", "Clinic A", "Check-up", "a.customer@clinic-a.test", "+963900000001");

        await QueueEventAsync(new AppointmentCreatedEvent(
            tenantA.TenantId,
            Guid.NewGuid(),
            tenantA.CustomerId,
            Guid.NewGuid(),
            tenantA.ServiceId,
            DateTime.UtcNow.AddDays(2),
            DateTime.UtcNow.AddDays(2).AddMinutes(30)));

        await OutboxWorker().ProcessPendingMessagesAsync(CancellationToken.None);

        _factory.SmsSender.Sent.Should().ContainSingle();
        _factory.SmsSender.Sent[0].To.Should().Be("+963900000001");
    }
}
