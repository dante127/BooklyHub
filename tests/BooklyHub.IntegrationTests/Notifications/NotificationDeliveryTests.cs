using BooklyHub.Application.Common.Interfaces;
using BooklyHub.Domain.Entities.Appointments;
using BooklyHub.Domain.Entities.Customers;
using BooklyHub.Domain.Entities.Organizations;
using BooklyHub.Domain.Entities.Services;
using BooklyHub.Domain.Entities.StaffMembers;
using BooklyHub.Domain.Entities.System;
using BooklyHub.Domain.Entities.Tenancy;
using BooklyHub.Domain.Enums;
using BooklyHub.Infrastructure.BackgroundJobs;
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
/// Outbox delivery and appointment reminders run outside a request, so they only see rows if their scope
/// carries a tenant identity. These tests pin that: the email must reach the customer's address, which is
/// only possible if the worker's tenant filter let it read the customer in the first place.
/// </summary>
public sealed class NotificationDeliveryTests : IAsyncLifetime
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

    private sealed record BookingGraph(Guid TenantId, Guid LocationId, Guid ServiceId, Guid StaffId, Guid CustomerId, string CustomerEmail);

    private sealed class NotificationWebApplicationFactory : BooklyHubWebApplicationFactory
    {
        public RecordingEmailSender EmailSender { get; } = new();

        protected override void ConfigureTestServices(IServiceCollection services)
        {
            services.RemoveAll<IEmailSender>();
            services.AddSingleton<IEmailSender>(EmailSender);
        }
    }

    // Not an IClassFixture: each test gets its own database and its own recording sender, so the
    // "exactly one email" assertions cannot be polluted by the other test in this class.
    private readonly NotificationWebApplicationFactory _factory = new();

    public Task InitializeAsync() => _factory.InitializeAsync();

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private async Task<BookingGraph> SeedGraphAsync(DateTime startAtUtc, AppointmentStatus status)
    {
        var graph = new BookingGraph(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "jane.doe@example.com");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        db.Tenants.Add(new Tenant(graph.TenantId, "Notify Clinic", "notify-clinic", "UTC"));

        db.Locations.Add(new Location
        {
            Id = graph.LocationId,
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
            Name = "Check-up",
            DurationMinutes = 30,
            Price = 80.00m
        });

        var staff = new Staff
        {
            Id = graph.StaffId,
            TenantId = graph.TenantId,
            LocationId = graph.LocationId,
            FirstName = "Rita",
            LastName = "Skeeter",
            Email = "rita@clinic.test"
        };
        staff.StaffServices.Add(new StaffService { TenantId = graph.TenantId, StaffId = graph.StaffId, ServiceId = graph.ServiceId });
        db.StaffMembers.Add(staff);

        db.Customers.Add(new Customer
        {
            Id = graph.CustomerId,
            TenantId = graph.TenantId,
            FirstName = "Jane",
            LastName = "Doe",
            Email = graph.CustomerEmail
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
            80.00m);

        if (status != AppointmentStatus.Pending)
        {
            appointment.TransitionTo(status, DateTime.UtcNow);
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
    public async Task OutboxProcessor_AppointmentCreatedEvent_MustEmailTheCustomerItBelongsTo()
    {
        var graph = await SeedGraphAsync(DateTime.UtcNow.AddDays(2), AppointmentStatus.Pending);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (await db.OutboxMessages.CountAsync(m => m.Type == nameof(Domain.Events.AppointmentCreatedEvent) && m.ProcessedOnUtc == null))
                .Should().BeGreaterThan(0, "creating the appointment must queue the domain event");
        }

        var processed = await OutboxWorker().ProcessPendingMessagesAsync(CancellationToken.None);

        processed.Should().BeGreaterThan(0);

        _factory.EmailSender.Sent.Should().ContainSingle(
            "the tenant filter must let the worker read the customer behind the event");
        _factory.EmailSender.Sent[0].To.Should().Be(graph.CustomerEmail);
        _factory.EmailSender.Sent[0].Subject.Should().Contain("Check-up");

        using (var verify = _factory.Services.CreateScope())
        {
            var db = verify.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (await db.OutboxMessages.CountAsync(m => m.ProcessedOnUtc == null)).Should().Be(0);
        }
    }

    [Fact]
    public async Task ReminderSweep_UpcomingConfirmedAppointment_MustSendExactlyOneReminder()
    {
        await SeedGraphAsync(DateTime.UtcNow.AddHours(6), AppointmentStatus.Confirmed);

        var sentFirst = await ReminderWorker().ProcessUpcomingRemindersAsync(CancellationToken.None);
        sentFirst.Should().Be(1);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var records = await db.NotificationRecords.IgnoreQueryFilters().ToListAsync();
            records.Should().ContainSingle();
            records[0].Recipient.Should().NotBeNullOrWhiteSpace();
            records[0].Subject.Should().Contain("Reminder");
        }

        var sentSecond = await ReminderWorker().ProcessUpcomingRemindersAsync(CancellationToken.None);
        sentSecond.Should().Be(0, "a reminder already recorded must not be sent twice");
    }
}
