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
/// BL-13 settled *which* window each tenant is judged against. This file is about how the pass is executed,
/// which the sweep had gotten into a shape that only looks safe while the platform is small:
///
///  - one cross-tenant candidate read with no ceiling, so the tick's cost grew with the whole platform;
///  - "have I already reminded this?" asked once per appointment, so a batch of N cost N+1 reads;
///  - every record written in one save at the end of the tick, so a store failure after the emails had left
///    un-recorded all of them and the next tick re-mailed every patient on the platform. A reminder is
///    supposed to be sent once, so the only thing standing between a clinic and a mail storm is a row that
///    was written before the mail went out.
///
/// These tests bind the replacement: a bounded pass per tenant, the already-sent question inside that read,
/// and each reminder recorded as it leaves. A send that fails records nothing, which is the recoverable
/// direction; a send that succeeds and cannot be recorded ends that tenant's pass rather than continuing to
/// produce reminders with no trace.
/// </summary>
public sealed class ReminderSweepExecutionTests : IAsyncLifetime
{
    private static readonly DateTime PinnedNow = new(2027, 6, 1, 9, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// The ceiling itself, restated here rather than read from the worker because a test that derives its
    /// expectation from the constant it is guarding guards nothing. 201 candidates are seeded against it.
    /// </summary>
    private const int ExpectedBatchCeiling = 200;

    private sealed record SentEmail(string To, string Subject, string Body);

    private sealed record StoredReminder(Guid? AppointmentId, Guid TenantId, string Subject);

    private sealed record SeededTenant(Guid TenantId, string CustomerEmail, List<Guid> AppointmentIds);

    /// <summary>
    /// Stands in for SMTP with one extra degree of freedom: a hook that runs at the instant a message is
    /// being sent. That is the only place a test can see what the store already holds *while* the sweep is
    /// still working, which is the whole difference between recording a reminder and remembering to record
    /// one at the end. A hook that throws stands for a provider that refused the message — and refused it
    /// before it left, which is what the sweep has to be conservative about.
    /// </summary>
    private sealed class HookedEmailSender : IEmailSender
    {
        private readonly List<SentEmail> _sent = [];
        private int _calls;

        public Func<SentEmail, int, Task>? OnSend { get; set; }

        public IReadOnlyList<SentEmail> Sent
        {
            get
            {
                lock (_sent)
                {
                    return _sent.ToList();
                }
            }
        }

        public async Task SendEmailAsync(string to, string subject, string htmlBody, CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _calls);

            if (OnSend is not null)
            {
                await OnSend(new SentEmail(to, subject, htmlBody), call);
            }

            lock (_sent)
            {
                _sent.Add(new SentEmail(to, subject, htmlBody));
            }
        }
    }

    private sealed class ReminderSweepWebApplicationFactory : BooklyHubWebApplicationFactory
    {
        public HookedEmailSender EmailSender { get; } = new();

        protected override void ConfigureTestServices(IServiceCollection services)
        {
            services.RemoveAll<IEmailSender>();
            services.AddSingleton<IEmailSender>(EmailSender);
        }
    }

    // Its own database and sender per test, so a count of emails or of queries cannot be answered by
    // another test's leftovers.
    private readonly ReminderSweepWebApplicationFactory _factory = new();

    public Task InitializeAsync() => _factory.InitializeAsync();

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    /// <summary>
    /// Seeds one tenant whose confirmed appointments start at the given offsets from the pinned clock, in
    /// the order given. Offsets are the whole input a reminder decision has, so a test names the offsets it
    /// expects the sweep to reach and the ids come back so it can say which bookings were left out.
    /// </summary>
    private async Task<SeededTenant> SeedTenantAsync(
        string slug,
        int? noticeHours,
        double[] startsInHours,
        bool active = true)
    {
        var tenantId = Guid.NewGuid();
        var serviceId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var customerEmail = $"{slug}-{Guid.NewGuid():N}@execution.test";
        var locationId = Guid.NewGuid();
        var staffId = Guid.NewGuid();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var tenant = new Tenant(tenantId, $"Sweep Clinic {slug}", $"exe-{slug}-{tenantId:N}", "UTC");
        if (noticeHours is int hours)
        {
            tenant.Settings = new TenantSetting(tenantId) { ReminderNoticeHours = hours };
        }

        // The constructor sets IsActive true, so deactivating is a statement about this tenant rather than
        // an absence of configuration.
        tenant.IsActive = active;
        db.Tenants.Add(tenant);

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

        db.Services.Add(new Service
        {
            Id = serviceId,
            TenantId = tenantId,
            Name = $"Check-up {slug}",
            DurationMinutes = 30,
            Price = 80.00m
        });

        var staff = new Staff
        {
            Id = staffId,
            TenantId = tenantId,
            LocationId = locationId,
            FirstName = "Rita",
            LastName = "Skeeter",
            Email = $"rita-{tenantId:N}@execution.test"
        };
        staff.StaffServices.Add(new StaffService { TenantId = tenantId, StaffId = staffId, ServiceId = serviceId });
        db.StaffMembers.Add(staff);

        db.Customers.Add(new Customer
        {
            Id = customerId,
            TenantId = tenantId,
            FirstName = "Jane",
            LastName = "Doe",
            Email = customerEmail
        });

        var appointmentIds = new List<Guid>();

        foreach (var offset in startsInHours)
        {
            var startAtUtc = PinnedNow.AddHours(offset);
            var appointment = Appointment.Create(
                tenantId, locationId, serviceId, staffId, customerId,
                startAtUtc, startAtUtc.AddMinutes(30), 30, 80.00m);
            appointment.TransitionTo(AppointmentStatus.Confirmed, PinnedNow);
            db.Appointments.Add(appointment);
            appointmentIds.Add(appointment.Id);
        }

        await db.SaveChangesAsync();

        return new SeededTenant(tenantId, customerEmail, appointmentIds);
    }

    private async Task<int> SweepAsync(Guid? tenantFilter = null)
    {
        var sweep = new AppointmentReminderBackgroundService(
            _factory.Services.GetRequiredService<IServiceScopeFactory>(),
            _factory.Services.GetRequiredService<ILogger<AppointmentReminderBackgroundService>>());

        try
        {
            _factory.Clock.Pin(PinnedNow);
            return await sweep.ProcessUpcomingRemindersAsync(CancellationToken.None, tenantFilter);
        }
        finally
        {
            _factory.Clock.Release();
        }
    }

    /// <summary>
    /// Reads the notification table the way an operator would, ignoring the tenant filter: the scopes below
    /// have no tenant bound, so the filter hides every row and an assertion against such a read passes
    /// whether or not the sweep wrote anything.
    /// </summary>
    private async Task<List<StoredReminder>> StoredRemindersAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.NotificationRecords
            .IgnoreQueryFilters()
            .Select(n => new StoredReminder(n.AppointmentId, n.TenantId, n.Subject))
            .ToListAsync();
    }

    [Fact]
    public async Task ReminderSweep_WritesEachRecordBeforeTheNextEmailLeaves()
    {
        var tenant = await SeedTenantAsync("durability", 48, [1, 2]);

        // Observed from inside the send of the *second* reminder: the first has already left, so this is the
        // exact instant a crash would cost. A store that dies here loses one email's worth of history, not
        // the whole tick's.
        var committedWhenSecondLeft = -1;
        _factory.EmailSender.OnSend = async (_, call) =>
        {
            if (call == 2)
            {
                committedWhenSecondLeft = (await StoredRemindersAsync()).Count;
            }
        };

        var sent = await SweepAsync();

        sent.Should().Be(2);
        committedWhenSecondLeft.Should().Be(1,
            "a reminder is only safe once its record is in the store, so the write has to precede the next " +
            "send rather than the end of the pass");

        var stored = await StoredRemindersAsync();
        stored.Should().HaveCount(2);
        stored.Select(r => r.AppointmentId!.Value).Should().BeEquivalentTo(tenant.AppointmentIds);
        stored.Should().OnlyContain(r => r.TenantId == tenant.TenantId);
    }

    [Fact]
    public async Task ReminderSweep_WhenASendFails_RecordsNothingAndStillRemindsTheRest()
    {
        var tenant = await SeedTenantAsync("send-fails", 48, [1, 2]);

        _factory.EmailSender.OnSend = (_, call) => call == 1
            ? Task.FromException(new InvalidOperationException("The SMTP provider refused the message."))
            : Task.CompletedTask;

        var sent = await SweepAsync();

        // Nothing is written for a message that never left, and the pass continues to the next booking.
        // NotificationRecord has an Error column and this sweep does not use it: a row whose subject says
        // Reminder is a row the dedupe treats as spent, so recording a refusal would suppress the retry that
        // is the only recovery available.
        sent.Should().Be(1);
        _factory.EmailSender.Sent.Should().ContainSingle();
        var afterFirstPass = await StoredRemindersAsync();
        afterFirstPass.Should().ContainSingle()
            .Which.AppointmentId.Should().Be(tenant.AppointmentIds[1]);

        _factory.EmailSender.OnSend = null;

        var retried = await SweepAsync();

        retried.Should().Be(1, "the booking whose email never left is still unreminded, and the next tick is the recovery");
        _factory.EmailSender.Sent.Should().HaveCount(2);
        (await StoredRemindersAsync()).Select(r => r.AppointmentId).Should().BeEquivalentTo(tenant.AppointmentIds);
    }

    [Fact]
    public async Task ReminderSweep_BacklogBeyondOneBatch_CapsThePassAndDrainsFromTheNearest()
    {
        // 201 bookings inside a 48-hour window, one tenant. Offsets are ascending, so the appointment list
        // doubles as the order the sweep is supposed to honour: soonest first.
        var offsets = Enumerable.Range(0, ExpectedBatchCeiling + 1).Select(i => 1.0 + i * 0.1).ToArray();
        var tenant = await SeedTenantAsync("backlog", 48, offsets);

        var firstPass = await SweepAsync();
        var afterFirstPass = await StoredRemindersAsync();

        firstPass.Should().Be(ExpectedBatchCeiling,
            "a pass with no ceiling loads and holds whatever the platform has, and the tick gets longer as the " +
            "platform does rather than staying bounded");
        afterFirstPass.Should().HaveCount(ExpectedBatchCeiling);
        afterFirstPass.Select(r => r.AppointmentId!.Value)
            .Should().BeEquivalentTo(tenant.AppointmentIds.Take(ExpectedBatchCeiling),
                "the bookings left over from a capped pass have to be the ones furthest away");

        var secondPass = await SweepAsync();

        // The property the ceiling would silently break: the spent head of the page must not block the
        // bookings behind it. Deduplicating after the cap is taken means this pass re-reads the same 200
        // reminders, discards all of them, and the 201st booking is never reminded while its clinic keeps
        // being busy — invisible from the outside, because the sweep reports a clean zero.
        secondPass.Should().Be(1,
            "the cap has to be taken over bookings that still need a reminder, not over the whole window and " +
            "then filtered");
        (await StoredRemindersAsync()).Select(r => r.AppointmentId!.Value)
            .Should().BeEquivalentTo(tenant.AppointmentIds);
        _factory.EmailSender.Sent.Should().HaveCount(ExpectedBatchCeiling + 1, "no patient is reminded twice");
    }

    [Fact]
    public async Task ReminderSweep_AskTheNotificationTableOncePerTenantPass()
    {
        await SeedTenantAsync("queries", 48, [1, 2, 3]);

        _factory.QueryInterceptor.Reset();

        var sent = await SweepAsync();

        sent.Should().Be(3, "the pass has to do its work for the count to mean anything");

        // The candidate read carries the already-sent question as a subquery, so it mentions the table; what
        // must not scale with the batch is the number of reads that mention it. One per appointment is the
        // N+1 this test exists to catch, and it is what makes a 200-booking pass cost 200 round trips.
        //
        // Restricted to SELECTs because the interceptor sees the reminder writes as well, so a raw mention
        // count would be a count of the work done rather than a count of the reads that ask the same question
        // over again.
        var notificationReads = _factory.QueryInterceptor.ExecutedCommands
            .Count(command => command.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) &&
                              command.Contains("NotificationRecords", StringComparison.OrdinalIgnoreCase));

        notificationReads.Should().Be(1,
            "one bounded read per tenant pass, not one dedupe read per appointment inside it");
    }

    [Fact]
    public async Task ReminderSweep_InactiveTenantIsNotReminded()
    {
        await SeedTenantAsync("deactivated", 48, [1], active: false);

        var sent = await SweepAsync();

        // A deactivated tenant is not a tenant to email: its patients are not being seen. The no-show sweep
        // already reads the same flag, and a worker that quietly kept mailing a closed clinic is the kind of
        // behaviour nobody notices until the complaints arrive.
        sent.Should().Be(0);
        _factory.EmailSender.Sent.Should().BeEmpty();
        (await StoredRemindersAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task ReminderSweep_TenantFilterRunsThatTenantAlone()
    {
        var first = await SeedTenantAsync("filtered-a", 48, [1]);
        var second = await SeedTenantAsync("filtered-b", 48, [2]);

        var sent = await SweepAsync(tenantFilter: first.TenantId);

        // The filter exists so a test, or an operator re-running one clinic's pass, is not obliged to sweep
        // the platform. It is a restriction on the tenant list, not on the window.
        sent.Should().Be(1);
        _factory.EmailSender.Sent.Should().ContainSingle()
            .Which.To.Should().Be(first.CustomerEmail);
        var stored = await StoredRemindersAsync();
        stored.Should().ContainSingle().Which.TenantId.Should().Be(first.TenantId);
        stored.Should().NotContain(r => r.AppointmentId == second.AppointmentIds[0],
            "the tenant that was not asked for stays untouched, reminder included");
    }

    [Fact]
    public async Task ReminderSweep_TwoTenantPasses_EachTenantDecidesOnlyForItsOwnBookings()
    {
        // Deliberately unequal windows, because equal ones hide the leak: with both tenants asking for the
        // same notice, a pass that reads the whole platform still reaches the same conclusions and the sweep
        // looks correct. Here the wide clinic's page would have to cover the narrow clinic's ten-hour
        // booking, and the only thing that stops it is the predicate on the candidate read.
        var wide = await SeedTenantAsync("scoped-wide", 48, [1]);
        var narrow = await SeedTenantAsync("scoped-narrow", 2, [10]);

        var sent = await SweepAsync();

        sent.Should().Be(1);
        _factory.EmailSender.Sent.Should().ContainSingle()
            .Which.To.Should().Be(wide.CustomerEmail,
                "the narrow clinic's patient must not be mailed because a wider clinic's pass reached them");

        var stored = await StoredRemindersAsync();
        stored.Should().ContainSingle().Which.TenantId.Should().Be(wide.TenantId);
        stored.Should().NotContain(r => r.AppointmentId == narrow.AppointmentIds[0]);
    }
}
