using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BooklyHub.Api.Controllers;
using BooklyHub.Application.Security;
using BooklyHub.Domain.Entities.Appointments;
using BooklyHub.Domain.Entities.Customers;
using BooklyHub.Domain.Entities.Organizations;
using BooklyHub.Domain.Entities.Payments;
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
using Microsoft.Extensions.Logging;
using Xunit;

namespace BooklyHub.IntegrationTests.Appointments;

/// <summary>
/// An unattended booking used to stay Confirmed forever, and a Confirmed row reads as a visit that
/// happened: it holds grossRevenue, it holds a slot in noShowRate at zero, and it never leaves the
/// dashboard. The sweep closes those rows, but only where the absence is evidenced and no money is still
/// being chased, and it decides on the application clock rather than the machine's.
/// </summary>
public class NoShowClosureSweepTests : IClassFixture<BooklyHubWebApplicationFactory>
{
    private readonly BooklyHubWebApplicationFactory _factory;

    public NoShowClosureSweepTests(BooklyHubWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private sealed record Graph(Guid TenantId, Guid LocationId, Guid ServiceId, Guid StaffId, Guid CustomerId);

    private static DateTime EndAt(int hoursAgo) => DateTime.UtcNow.AddHours(-hoursAgo);

    private async Task<Graph> SeedGraphAsync(string slug)
    {
        var graph = new Graph(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        db.Tenants.Add(new Tenant(graph.TenantId, $"Sweep {slug}", $"sweep-{slug}-{graph.TenantId:N}", "UTC"));
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
            Name = "Consult",
            DurationMinutes = 30,
            Price = 100.00m
        });

        var staff = new Staff
        {
            Id = graph.StaffId,
            TenantId = graph.TenantId,
            LocationId = graph.LocationId,
            FirstName = "Ola",
            LastName = "Doc",
            Email = $"ola-{graph.StaffId:N}@sweep.test"
        };
        staff.StaffServices.Add(new StaffService { TenantId = graph.TenantId, StaffId = graph.StaffId, ServiceId = graph.ServiceId });
        db.StaffMembers.Add(staff);

        db.Customers.Add(new Customer
        {
            Id = graph.CustomerId,
            TenantId = graph.TenantId,
            FirstName = "Ziad",
            LastName = "Patient",
            Email = $"ziad-{graph.CustomerId:N}@sweep.test"
        });

        await db.SaveChangesAsync();
        return graph;
    }

    private async Task<Guid> SeedAppointmentAsync(
        Graph graph,
        DateTime endAtUtc,
        AppointmentStatus status,
        decimal price = 100.00m)
    {
        var start = endAtUtc.AddMinutes(-30);
        var appointment = Appointment.Create(
            graph.TenantId, graph.LocationId, graph.ServiceId, graph.StaffId, graph.CustomerId,
            start, endAtUtc, 30, price, "USD");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.Appointments.Add(appointment);
        await db.SaveChangesAsync();

        if (status != AppointmentStatus.Pending)
        {
            // The stored status is rewritten rather than transitioned to: the sweep judges a row by its
            // status, and walking the state machine would seed a history row per step and make the
            // closure's own row impossible to count.
            await db.Database.ExecuteSqlAsync(
                $"UPDATE Appointments SET Status = {(int)status} WHERE Id = {appointment.Id}");
        }

        return appointment.Id;
    }

    private async Task SeedPaymentAsync(Graph graph, Guid appointmentId, decimal amount)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        db.Payments.Add(new Payment
        {
            Id = Guid.NewGuid(),
            TenantId = graph.TenantId,
            AppointmentId = appointmentId,
            Amount = amount,
            Currency = "USD",
            Status = PaymentStatus.Paid,
            Provider = PaymentProviderType.Simulated,
            ProviderPaymentId = $"prov-{Guid.NewGuid():N}",
            CreatedBy = "test"
        });

        await db.SaveChangesAsync();
    }

    private AppointmentNoShowBackgroundService SweepWorker() => new(
        _factory.Services.GetRequiredService<IServiceScopeFactory>(),
        _factory.Services.GetRequiredService<ILogger<AppointmentNoShowBackgroundService>>());

    private Task<NoShowSweepResult> SweepAsync(Graph graph) =>
        SweepWorker().CloseUnattendedAppointmentsAsync(CancellationToken.None, graph.TenantId);

    private sealed record Stored(AppointmentStatus Status, int HistoryRows, string? ChangedBy, int NotificationRows);

    private async Task<Stored> ReadAsync(Guid appointmentId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var row = await db.Appointments
            .IgnoreQueryFilters()
            .Where(a => a.Id == appointmentId)
            .Select(a => new
            {
                a.Status,
                Histories = a.StatusHistories.Count,
                LastChangedBy = a.StatusHistories.OrderBy(h => h.ChangedAtUtc).Last().ChangedBy,
                Notifications = db.NotificationRecords.Count(n => n.AppointmentId == appointmentId)
            })
            .FirstAsync();

        return new Stored(row.Status, row.Histories, row.LastChangedBy, row.Notifications);
    }

    private async Task<JsonElement> DashboardAsync(Graph graph)
    {
        var client = _factory.CreateClientForTenant(graph.TenantId, Roles.Manager);
        var response = await client.GetAsync("/api/v1/reports/dashboard");
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.StatusCode == HttpStatusCode.OK,
            $"expected 200, got {(int)response.StatusCode}: {body}");

        return JsonDocument.Parse(body).RootElement;
    }

    private static DateTime HoursAgoUtc(int hours) => DateTime.UtcNow.AddHours(-hours);

    private static DateTime ReadUtc(JsonElement root, string property) =>
        DateTime.Parse(root.GetProperty(property).GetString()!, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    [Fact]
    public async Task SettledAbsence_MustBeClosedWithOneTraceableHistoryRow()
    {
        var graph = await SeedGraphAsync("settled");
        var appointmentId = await SeedAppointmentAsync(graph, EndAt(8), AppointmentStatus.Confirmed);
        await SeedPaymentAsync(graph, appointmentId, 100.00m);

        var result = await SweepAsync(graph);

        result.Closed.Should().Be(1);
        result.SkippedWithBalance.Should().Be(0);

        var stored = await ReadAsync(appointmentId);
        stored.Status.Should().Be(AppointmentStatus.NoShow);
        stored.HistoryRows.Should().Be(2, "the booking's own creation row plus the sweep's closure row");
        stored.ChangedBy.Should().Be("system:no-show-sweep",
            "a closure nobody can attribute is a closure nobody can audit");
    }

    [Fact]
    public async Task FreeBookingWithNoPaymentHistory_MustBeClosed()
    {
        var graph = await SeedGraphAsync("free");
        var appointmentId = await SeedAppointmentAsync(graph, EndAt(8), AppointmentStatus.Confirmed, price: 0m);

        var result = await SweepAsync(graph);

        result.Closed.Should().Be(1);
        (await ReadAsync(appointmentId)).Status.Should().Be(AppointmentStatus.NoShow);
    }

    [Fact]
    public async Task AbsenceThatStillOwesMoney_MustStayConfirmedAndStayChargeable()
    {
        var graph = await SeedGraphAsync("owed");
        var appointmentId = await SeedAppointmentAsync(graph, EndAt(8), AppointmentStatus.Confirmed);

        var result = await SweepAsync(graph);

        result.Closed.Should().Be(0);
        result.SkippedWithBalance.Should().Be(1);
        (await ReadAsync(appointmentId)).Status.Should().Be(AppointmentStatus.Confirmed,
            "a NoShow cannot be charged, so closing it would write the balance off by timer");

        // The whole point of the skip: the money door has to still be open after the sweep ran.
        var client = _factory.CreateClientForTenant(graph.TenantId, Roles.Accountant);
        var charge = await client.PostAsJsonAsync(
            "/api/v1/payments/charge",
            new PaymentsController.ProcessPaymentApiRequest(appointmentId, 100.00m, "USD"));

        var body = await charge.Content.ReadAsStringAsync();
        Assert.True(charge.StatusCode == HttpStatusCode.OK,
            $"the skipped booking must still be chargeable, got {(int)charge.StatusCode}: {body}");
    }

    [Fact]
    public async Task PartiallyPaidAbsence_MustStayConfirmed()
    {
        var graph = await SeedGraphAsync("partial");
        var appointmentId = await SeedAppointmentAsync(graph, EndAt(8), AppointmentStatus.Confirmed);
        await SeedPaymentAsync(graph, appointmentId, 40.00m);

        var result = await SweepAsync(graph);

        result.Closed.Should().Be(0, "60 is still owed, and the ledger the sweep reads is the charging ledger");
        result.SkippedWithBalance.Should().Be(1);
    }

    [Fact]
    public async Task RowOpenedButNeverClosed_MustNotBecomeAnAbsenceAndMustBeReportedAsStale()
    {
        var graph = await SeedGraphAsync("stale");
        var checkedIn = await SeedAppointmentAsync(graph, EndAt(8), AppointmentStatus.CheckedIn);
        var inProgress = await SeedAppointmentAsync(graph, EndAt(9), AppointmentStatus.InProgress);

        // Settled in full, so the only thing that can keep these rows open is their status: an unpaid
        // booking survives the sweep for a money reason and the status rule goes untested.
        await SeedPaymentAsync(graph, checkedIn, 100.00m);
        await SeedPaymentAsync(graph, inProgress, 100.00m);

        var result = await SweepAsync(graph);

        result.Closed.Should().Be(0, "the customer appeared; this is the clinic's own reporting gap");
        result.SkippedWithBalance.Should().Be(0, "both visits were paid for, so money is not why they survive");
        (await ReadAsync(checkedIn)).Status.Should().Be(AppointmentStatus.CheckedIn);
        (await ReadAsync(inProgress)).Status.Should().Be(AppointmentStatus.InProgress);

        var root = await DashboardAsync(graph);
        root.GetProperty("staleExecutionCount").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task PendingBookingPastItsWindow_MustBeLeftForTheConfirmationRule()
    {
        var graph = await SeedGraphAsync("pending");
        var appointmentId = await SeedAppointmentAsync(graph, EndAt(8), AppointmentStatus.Pending);
        await SeedPaymentAsync(graph, appointmentId, 100.00m);

        var result = await SweepAsync(graph);

        result.Closed.Should().Be(0);
        result.SkippedWithBalance.Should().Be(0, "the booking is settled, so its status is the only reason it stays open");
        (await ReadAsync(appointmentId)).Status.Should().Be(AppointmentStatus.Pending,
            "the clinic never accepted this booking, so the customer cannot be marked absent from it (BL-11)");
    }

    [Fact]
    public async Task AbsenceInsideTheGraceWindow_MustNotBeClosed()
    {
        var graph = await SeedGraphAsync("grace");
        var appointmentId = await SeedAppointmentAsync(graph, EndAt(2), AppointmentStatus.Confirmed);
        await SeedPaymentAsync(graph, appointmentId, 100.00m);

        var result = await SweepAsync(graph);

        result.Closed.Should().Be(0);
        (await ReadAsync(appointmentId)).Status.Should().Be(AppointmentStatus.Confirmed,
            "the visit window only just shut; a late arrival would still be an arrival");
    }

    [Fact]
    public async Task AbsenceOlderThanTheLookback_MustNotBeClosedByAHousekeepingTick()
    {
        var graph = await SeedGraphAsync("lookback");
        var appointmentId = await SeedAppointmentAsync(graph, EndAt(24 * 20), AppointmentStatus.Confirmed);
        await SeedPaymentAsync(graph, appointmentId, 100.00m);

        var result = await SweepAsync(graph);

        result.Closed.Should().Be(0, "closing a 20-day-old row restates a reported period; that is a backfill decision");
        (await ReadAsync(appointmentId)).Status.Should().Be(AppointmentStatus.Confirmed);
    }

    [Fact]
    public async Task SecondSweep_MustNotCloseTheSameAbsenceTwice()
    {
        var graph = await SeedGraphAsync("twice");
        var appointmentId = await SeedAppointmentAsync(graph, EndAt(8), AppointmentStatus.Confirmed);
        await SeedPaymentAsync(graph, appointmentId, 100.00m);

        await SweepAsync(graph);
        var second = await SweepAsync(graph);

        second.Closed.Should().Be(0);
        (await ReadAsync(appointmentId)).HistoryRows.Should().Be(2,
            "one closure must produce exactly one history row, however many ticks run");
    }

    [Fact]
    public async Task TwoSweepsRacingTheSameTenant_MustCloseEachAbsenceOnce()
    {
        var graph = await SeedGraphAsync("racing");
        var first = await SeedAppointmentAsync(graph, EndAt(8), AppointmentStatus.Confirmed);
        var second = await SeedAppointmentAsync(graph, EndAt(9), AppointmentStatus.Confirmed);
        await SeedPaymentAsync(graph, first, 100.00m);
        await SeedPaymentAsync(graph, second, 100.00m);

        var results = await Task.WhenAll(SweepWorker().CloseUnattendedAppointmentsAsync(
                CancellationToken.None, graph.TenantId),
            SweepWorker().CloseUnattendedAppointmentsAsync(CancellationToken.None, graph.TenantId));

        // Which instance wins is a scheduling fact and asserting on it makes the test flaky; that the pair
        // never closes a booking twice is the property the lock exists for.
        results.Sum(r => r.Closed).Should().Be(2,
            $"two instances must not close the same booking twice (observed {string.Join(" + ", results.Select(r => r.Closed))})");
        results.Sum(r => r.SkippedWithBalance).Should().Be(0);

        (await ReadAsync(first)).HistoryRows.Should().Be(2);
        (await ReadAsync(second)).HistoryRows.Should().Be(2);
    }

    [Fact]
    public async Task ATenantAlreadyBeingSwept_MustBeSkippedAndRetriedByTheNextTick()
    {
        var graph = await SeedGraphAsync("busy");
        var appointmentId = await SeedAppointmentAsync(graph, EndAt(8), AppointmentStatus.Confirmed);
        await SeedPaymentAsync(graph, appointmentId, 100.00m);

        // A second instance holding the tenant's lock, deterministic where a race cannot be: the sweep has
        // to give up on that tenant for this tick rather than queue behind it or work the same rows. Held
        // on a bare connection rather than through EF, whose retrying execution strategy owns the context's
        // transaction.
        using var holder = new Microsoft.Data.SqlClient.SqlConnection(_factory.ConnectionString);
        await holder.OpenAsync();
        using var holderTransaction = holder.BeginTransaction();

        using (var hold = holder.CreateCommand())
        {
            hold.Transaction = holderTransaction;
            hold.CommandText = "DECLARE @res INT; EXEC @res = sp_getapplock @Resource = @name, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 0; IF @res < 0 THROW 50000, 'test could not take the sweep lock', 1;";
            hold.Parameters.Add("@name", System.Data.SqlDbType.NVarChar, 255).Value = $"NoShow_Sweep_{graph.TenantId:N}";
            await hold.ExecuteNonQueryAsync();
        }

        var busy = await SweepAsync(graph);

        busy.Closed.Should().Be(0);
        busy.SkippedLockedTenant.Should().Be(1, "the tenant is busy, and waiting 15s for it would stall every other tenant behind it");
        (await ReadAsync(appointmentId)).Status.Should().Be(AppointmentStatus.Confirmed);

        // The lock is owned by the transaction, so releasing it is what makes the next tick succeed.
        holderTransaction.Rollback();

        var retried = await SweepAsync(graph);

        retried.Closed.Should().Be(1, "a skipped tenant is retried, not lost");
        (await ReadAsync(appointmentId)).Status.Should().Be(AppointmentStatus.NoShow);
    }

    [Fact]
    public async Task Closure_MustNotNotifyAnyone()
    {
        var graph = await SeedGraphAsync("silent");
        var appointmentId = await SeedAppointmentAsync(graph, EndAt(8), AppointmentStatus.Confirmed);
        await SeedPaymentAsync(graph, appointmentId, 100.00m);

        // Seeding the Confirmed row already queued its own confirmation event, and the outbox processor is
        // not running in the test host, so the claim has to be a delta rather than an empty table.
        int outboxBefore;
        using (var baseline = _factory.Services.CreateScope())
        {
            outboxBefore = await baseline.ServiceProvider
                .GetRequiredService<ApplicationDbContext>().OutboxMessages.CountAsync();
        }

        await SweepAsync(graph);

        using (var verify = _factory.Services.CreateScope())
        {
            var db = verify.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            (await db.OutboxMessages.CountAsync())
                .Should().Be(outboxBefore, "an automatic closure is a bookkeeping fact, not a message to anyone");
            (await db.OutboxMessages.CountAsync(m => m.Content.Contains(appointmentId.ToString())))
                .Should().Be(1, "only the seeded confirmation may mention this booking");
        }

        (await ReadAsync(appointmentId)).NotificationRows.Should().Be(0);
    }

    [Fact]
    public async Task AcrossTenants_EachClosureMustLandOnItsOwnBookingsOnly()
    {
        var graphA = await SeedGraphAsync("tenant-a");
        var graphB = await SeedGraphAsync("tenant-b");

        var rowA = await SeedAppointmentAsync(graphA, EndAt(8), AppointmentStatus.Confirmed);
        var rowB = await SeedAppointmentAsync(graphB, EndAt(8), AppointmentStatus.Confirmed);
        await SeedPaymentAsync(graphA, rowA, 100.00m);
        await SeedPaymentAsync(graphB, rowB, 100.00m);

        // One tenant's sweep may not reach the other's rows, even though the worker itself runs with the
        // tenant filter open: the predicate is the only scoping left.
        var result = await SweepAsync(graphA);

        result.Closed.Should().Be(1);
        (await ReadAsync(rowA)).Status.Should().Be(AppointmentStatus.NoShow);
        (await ReadAsync(rowB)).Status.Should().Be(AppointmentStatus.Confirmed);

        var sweptB = await SweepWorker().CloseUnattendedAppointmentsAsync(
            CancellationToken.None, graphB.TenantId);
        sweptB.Closed.Should().Be(1);
        (await ReadAsync(rowB)).Status.Should().Be(AppointmentStatus.NoShow);
    }

    [Fact]
    public async Task Sweep_MustJudgeTheWindowOnTheApplicationClockNotTheMachineOne()
    {
        var graph = await SeedGraphAsync("pinned");

        // Two hours past its end by the machine clock: inside grace. Eight days ahead on the application
        // clock the same row is six days past grace and still inside the lookback, so it must close.
        var insideGraceForTheMachine = await SeedAppointmentAsync(graph, EndAt(2), AppointmentStatus.Confirmed);
        await SeedPaymentAsync(graph, insideGraceForTheMachine, 100.00m);

        // Nine days past its end by the machine clock: inside the lookback. Eight further days on the
        // application clock puts it seventeen days back, beyond the bound, so it must not be restated.
        var beyondLookbackForTheClock = await SeedAppointmentAsync(graph, EndAt(24 * 9), AppointmentStatus.Confirmed);
        await SeedPaymentAsync(graph, beyondLookbackForTheClock, 100.00m);

        try
        {
            _factory.Clock.AdvanceBy(TimeSpan.FromDays(8));
            var result = await SweepAsync(graph);

            result.Closed.Should().Be(1,
                "the clock decides which rows are past their window, in both directions");
            (await ReadAsync(insideGraceForTheMachine)).Status.Should().Be(AppointmentStatus.NoShow);
            (await ReadAsync(beyondLookbackForTheClock)).Status.Should().Be(AppointmentStatus.Confirmed);
        }
        finally
        {
            _factory.Clock.Release();
        }
    }

    [Fact]
    public async Task ClosedAbsences_MustLeaveRevenueAndEnterTheNoShowRate()
    {
        var graph = await SeedGraphAsync("report");
        var closed = await SeedAppointmentAsync(graph, EndAt(8), AppointmentStatus.Confirmed);
        await SeedPaymentAsync(graph, closed, 100.00m);

        var before = await DashboardAsync(graph);
        before.GetProperty("confirmedCount").GetInt32().Should().Be(1);
        before.GetProperty("grossRevenue").GetDecimal().Should().Be(100.00m,
            "the visit was booked inside the period, so the report currently credits it");

        await SweepAsync(graph);

        var after = await DashboardAsync(graph);
        after.GetProperty("confirmedCount").GetInt32().Should().Be(0);
        after.GetProperty("noShowCount").GetInt32().Should().Be(1);
        after.GetProperty("grossRevenue").GetDecimal().Should().Be(0.00m,
            "a visit that never happened cannot be revenue");
        after.GetProperty("upcomingCount").GetInt32().Should().Be(0,
            "a closed row is no longer owed, whatever its start time looked like before");
        ReadUtc(after, "toUtc").Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(5));
    }
}
