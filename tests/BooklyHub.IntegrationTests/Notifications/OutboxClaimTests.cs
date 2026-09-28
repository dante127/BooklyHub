using BooklyHub.Application.Common.Interfaces;
using BooklyHub.Domain.Entities.Appointments;
using BooklyHub.Domain.Entities.Customers;
using BooklyHub.Domain.Entities.Organizations;
using BooklyHub.Domain.Entities.Services;
using BooklyHub.Domain.Entities.StaffMembers;
using BooklyHub.Domain.Entities.Tenancy;
using BooklyHub.Domain.Enums;
using BooklyHub.Infrastructure.Data;
using BooklyHub.Infrastructure.Outbox;
using BooklyHub.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BooklyHub.IntegrationTests.Notifications;

/// <summary>
/// More than one app instance polls the outbox, and a message must still be delivered once. These tests
/// race two processors over the same pending batch and require the claim to keep delivery single, because
/// a customer who gets the same booking email twice reads it as the system double-booking them.
/// </summary>
public sealed class OutboxClaimTests : IAsyncLifetime
{
    private sealed record SentEmail(string To, string Subject, string Body);

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

    private sealed class OutboxWebApplicationFactory : BooklyHubWebApplicationFactory
    {
        public RecordingEmailSender EmailSender { get; } = new();

        protected override void ConfigureTestServices(IServiceCollection services)
        {
            services.RemoveAll<IEmailSender>();
            services.AddSingleton<IEmailSender>(EmailSender);
        }
    }

    private readonly OutboxWebApplicationFactory _factory = new();

    public Task InitializeAsync() => _factory.InitializeAsync();

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private OutboxProcessorBackgroundService OutboxWorker() => new(
        _factory.Services.GetRequiredService<IServiceScopeFactory>(),
        _factory.Services.GetRequiredService<ILogger<OutboxProcessorBackgroundService>>());

    private async Task<int> SeedPendingEventsAsync(int appointmentCount)
    {
        var tenantId = Guid.NewGuid();
        var locationId = Guid.NewGuid();
        var serviceId = Guid.NewGuid();
        var staffId = Guid.NewGuid();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        db.Tenants.Add(new Tenant(tenantId, "Claim Clinic", $"claim-{tenantId:N}", "UTC"));
        db.Locations.Add(new Location
        {
            Id = locationId,
            TenantId = tenantId,
            Name = "Main",
            Address = "1 Main St",
            City = "Springfield",
            Country = "US",
            TimeZoneId = "UTC"
        });
        db.Services.Add(new Service { Id = serviceId, TenantId = tenantId, Name = "Check-up", DurationMinutes = 30, Price = 80.00m });
        db.StaffMembers.Add(new Staff
        {
            Id = staffId,
            TenantId = tenantId,
            LocationId = locationId,
            FirstName = "Rita",
            LastName = "Skeeter",
            Email = "rita@clinic.test"
        });

        var startAtUtc = DateTime.UtcNow.AddDays(2);
        for (var i = 0; i < appointmentCount; i++)
        {
            var customerId = Guid.NewGuid();
            db.Customers.Add(new Customer
            {
                Id = customerId,
                TenantId = tenantId,
                FirstName = $"Cust{i}",
                LastName = "Omer",
                Email = $"cust{i}-{customerId:N}@claim.test"
            });

            db.Appointments.Add(Appointment.Create(
                tenantId,
                locationId,
                serviceId,
                staffId,
                customerId,
                startAtUtc.AddHours(i),
                startAtUtc.AddHours(i).AddMinutes(30),
                30,
                80.00m));
        }

        await db.SaveChangesAsync();

        return await db.OutboxMessages.CountAsync(m => m.ProcessedOnUtc == null);
    }

    [Fact]
    public async Task TwoProcessorsRacingTheSameBatch_MustDeliverEachMessageOnce()
    {
        var pending = await SeedPendingEventsAsync(appointmentCount: 4);
        pending.Should().Be(4, "each created appointment queues its own outbox event");

        var results = await Task.WhenAll(
            OutboxWorker().ProcessPendingMessagesAsync(CancellationToken.None),
            OutboxWorker().ProcessPendingMessagesAsync(CancellationToken.None));

        results.Sum().Should().Be(4, "both workers together account for the whole batch exactly once");

        _factory.EmailSender.Sent.Should().HaveCount(4, "a duplicated delivery means the claim let both processors work the same rows");
        _factory.EmailSender.Sent.Select(e => e.To).Distinct().Should().HaveCount(4);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.OutboxMessages.CountAsync(m => m.ProcessedOnUtc == null)).Should().Be(0);
    }
}
