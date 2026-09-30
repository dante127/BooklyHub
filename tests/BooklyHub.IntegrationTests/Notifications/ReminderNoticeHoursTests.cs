using BooklyHub.Application.Common.Interfaces;
using BooklyHub.Domain.Entities.Appointments;
using BooklyHub.Domain.Entities.Customers;
using BooklyHub.Domain.Entities.Organizations;
using BooklyHub.Domain.Entities.Services;
using BooklyHub.Domain.Entities.StaffMembers;
using BooklyHub.Domain.Entities.Tenancy;
using BooklyHub.Domain.Enums;
using BooklyHub.Infrastructure.BackgroundJobs;
using BooklyHub.Infrastructure.Data;
using BooklyHub.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BooklyHub.IntegrationTests.Notifications;

/// <summary>
/// <c>TenantSetting.ReminderNoticeHours</c> existed in the model, in the table and in the entity's own
/// constructor — and nothing in <c>src</c> read it. The sweep compared every appointment on the platform
/// against one window of its own choosing, so the setting was a knob with no machine behind it. These tests
/// bind the replacement: each tenant's appointment is judged against that tenant's row, and a tenant with no
/// row at all is judged against the same default the entity documents rather than dropped from the sweep.
///
/// The window is the whole finding, because a reminder sends at most once per appointment: getting the window
/// wrong is not a late email, it is the one email never arriving.
/// </summary>
public sealed class ReminderNoticeHoursTests : IAsyncLifetime
{
    private static readonly DateTime PinnedNow = new(2027, 6, 1, 9, 0, 0, DateTimeKind.Utc);

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

    private sealed class ReminderWebApplicationFactory : BooklyHubWebApplicationFactory
    {
        public RecordingEmailSender EmailSender { get; } = new();

        protected override void ConfigureTestServices(IServiceCollection services)
        {
            services.RemoveAll<IEmailSender>();
            services.AddSingleton<IEmailSender>(EmailSender);
        }
    }

    private sealed record TenantGraph(Guid TenantId, Guid ServiceId, Guid CustomerId, string CustomerEmail);

    // Its own database and its own sender per test, so "exactly one email" cannot be answered by another
    // test's leftovers.
    private readonly ReminderWebApplicationFactory _factory = new();

    public Task InitializeAsync() => _factory.InitializeAsync();

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    /// <summary>
    /// Seeds one tenant whose confirmed appointment starts <paramref name="startsInHours"/> after the pinned
    /// clock. A null <paramref name="reminderNoticeHours"/> means no settings row at all, which is a state the
    /// sweep has to answer for and not merely an absence of configuration.
    /// </summary>
    private async Task<TenantGraph> SeedTenantAsync(
        string slug,
        int? reminderNoticeHours,
        double startsInHours)
    {
        var graph = new TenantGraph(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), $"{slug}-{Guid.NewGuid():N}@reminder.test");
        var locationId = Guid.NewGuid();
        var staffId = Guid.NewGuid();
        var startAtUtc = PinnedNow.AddHours(startsInHours);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var tenant = new Tenant(graph.TenantId, $"Reminder Clinic {slug}", $"rem-{slug}-{graph.TenantId:N}", "UTC");
        if (reminderNoticeHours is int hours)
        {
            tenant.Settings = new TenantSetting(graph.TenantId) { ReminderNoticeHours = hours };
        }

        db.Tenants.Add(tenant);

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
            Name = $"Check-up {slug}",
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
            Email = $"rita-{graph.TenantId:N}@reminder.test"
        };
        staff.StaffServices.Add(new StaffService { TenantId = graph.TenantId, StaffId = staffId, ServiceId = graph.ServiceId });
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
            graph.TenantId, locationId, graph.ServiceId, staffId, graph.CustomerId,
            startAtUtc, startAtUtc.AddMinutes(30), 30, 80.00m);
        appointment.TransitionTo(AppointmentStatus.Confirmed, PinnedNow);
        db.Appointments.Add(appointment);

        await db.SaveChangesAsync();
        return graph;
    }

    private AppointmentReminderBackgroundService Sweep() => new(
        _factory.Services.GetRequiredService<IServiceScopeFactory>(),
        _factory.Services.GetRequiredService<ILogger<AppointmentReminderBackgroundService>>());

    private async Task<int> SweepOnceAsync()
    {
        try
        {
            _factory.Clock.Pin(PinnedNow);
            return await Sweep().ProcessUpcomingRemindersAsync(CancellationToken.None);
        }
        finally
        {
            _factory.Clock.Release();
        }
    }

    private async Task<List<string>> StoredReminderSubjectsAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.NotificationRecords.AsNoTracking()
            .Select(n => n.Subject)
            .ToListAsync();
    }

    [Fact]
    public async Task ReminderSweep_TwoHourNoticeTenant_MustNotBeRemindedTenHoursOut()
    {
        await SeedTenantAsync("two-early", reminderNoticeHours: 2, startsInHours: 10);

        var sent = await SweepOnceAsync();

        // This is the case the hardcoded window got wrong in the direction nobody notices: the email arrives,
        // so the sweep looks healthy, and the tenant's own two-hour window is never reached because the
        // reminder is already spent.
        sent.Should().Be(0);
        _factory.EmailSender.Sent.Should().BeEmpty();
        (await StoredReminderSubjectsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task ReminderSweep_TwoHourNoticeTenant_MustBeRemindedInsideItsOwnWindow()
    {
        var graph = await SeedTenantAsync("two-inside", reminderNoticeHours: 2, startsInHours: 1);

        var sent = await SweepOnceAsync();

        sent.Should().Be(1, "tightening the window must not simply close it");
        _factory.EmailSender.Sent.Should().ContainSingle()
            .Which.To.Should().Be(graph.CustomerEmail);
    }

    [Fact]
    public async Task ReminderSweep_FortyEightHourNoticeTenant_MustBeRemindedThirtyHoursOut()
    {
        await SeedTenantAsync("wide", reminderNoticeHours: 48, startsInHours: 30);

        var sent = await SweepOnceAsync();

        // And the opposite direction: a tenant asking for more notice used to get none, because a thirty-hour
        // appointment was outside the sweep until it was inside twenty-four — by which point the notice the
        // tenant asked for had already been given away.
        sent.Should().Be(1);
        _factory.EmailSender.Sent.Should().ContainSingle();
    }

    [Fact]
    public async Task ReminderSweep_TenantWithNoSettingsRow_MustBeJudgedByTheDocumentedDefault()
    {
        var inside = await SeedTenantAsync("bare-inside", reminderNoticeHours: null, startsInHours: 10);
        await SeedTenantAsync("bare-outside", reminderNoticeHours: null, startsInHours: 30);

        var sent = await SweepOnceAsync();

        // A missing settings row has to fall back to the same 24 the entity documents. An inner join here
        // reads as a working sweep to every tenant that *does* have a row, and silently un-reminds the ones
        // that do not — and un-reminding is invisible from the outside.
        sent.Should().Be(1);
        _factory.EmailSender.Sent.Should().ContainSingle()
            .Which.To.Should().Be(inside.CustomerEmail);
    }

    [Fact]
    public async Task ReminderSweep_OnePassTwoTenants_EachAppointmentIsJudgedByItsOwnTenantsWindow()
    {
        var narrow = await SeedTenantAsync("narrow", reminderNoticeHours: 2, startsInHours: 10);
        var wide = await SeedTenantAsync("wide-pass", reminderNoticeHours: 48, startsInHours: 10);

        var sent = await SweepOnceAsync();

        // Two appointments ten hours out, two windows, one pass. This is the test that fails if the join
        // collapses to any single window — whether a constant, the first settings row, or the whole
        // platform's — because both rows then move together.
        sent.Should().Be(1);
        var recipients = _factory.EmailSender.Sent.Select(e => e.To).ToList();
        recipients.Should().Equal(wide.CustomerEmail);
        recipients.Should().NotContain(narrow.CustomerEmail);
    }
}
