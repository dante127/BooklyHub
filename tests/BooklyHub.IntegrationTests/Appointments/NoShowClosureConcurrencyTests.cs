using System.Data;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
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
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BooklyHub.IntegrationTests.Appointments;

/// <summary>
/// The sweep judges a booking from a read and then writes the verdict, so anything a staff member writes in
/// between is a fact the sweep never saw. Appointments carry a rowversion and the sweep writes through the
/// change tracker, so the store refuses the stale write instead of letting an attended visit be restated as
/// an absence. What this binds is the cost of that refusal: one edited booking may not roll back its tenant's
/// whole batch, may not abort the tenants queued behind it, and may not be counted as a closure that never
/// happened. The money the same closure is decided on lives in another table, so the second half of the class
/// binds the writer that moves nothing the token can see.
/// </summary>
public class NoShowClosureConcurrencyTests : IClassFixture<BooklyHubWebApplicationFactory>
{
    /// <summary>
    /// The sweep's candidate read is the only statement in the tick that selects from Appointments, so
    /// matching it is how a test knows it landed its write inside the window and not somewhere else.
    /// </summary>
    private const string CandidateRead = "FROM [Appointments]";

    /// <summary>
    /// The sweep's money read. Everything between this reader closing and the status write is a fact the
    /// closure was not decided on, and a payment row lives in exactly that gap.
    /// </summary>
    private const string LedgerRead = "FROM [Payments]";

    /// <summary>
    /// The money path's read of the appointment, which is the statement that captures its token: one fan-out
    /// query pulling the payment, its refunds and the appointment it belongs to. Arming on the earlier lookup
    /// that only finds the appointment id would land the competing edit before the money path ever held a
    /// version of the row, so the race would be against nothing.
    /// </summary>
    private const string PaymentWithAppointmentRead = "LEFT JOIN [Refunds]";

    private readonly BooklyHubWebApplicationFactory _factory;

    public NoShowClosureConcurrencyTests(BooklyHubWebApplicationFactory factory)
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

        db.Tenants.Add(new Tenant(graph.TenantId, $"Race {slug}", $"race-{slug}-{graph.TenantId:N}", "UTC"));
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
            Email = $"ola-{graph.StaffId:N}@race.test"
        };
        staff.StaffServices.Add(new StaffService { TenantId = graph.TenantId, StaffId = graph.StaffId, ServiceId = graph.ServiceId });
        db.StaffMembers.Add(staff);

        db.Customers.Add(new Customer
        {
            Id = graph.CustomerId,
            TenantId = graph.TenantId,
            FirstName = "Ziad",
            LastName = "Patient",
            Email = $"ziad-{graph.CustomerId:N}@race.test"
        });

        await db.SaveChangesAsync();
        return graph;
    }

    /// <summary>
    /// A past-its-window Confirmed booking, by default paid in full: exactly the row the sweep intends to close.
    /// </summary>
    private async Task<Guid> SeedVisitAsync(Graph graph, DateTime endAtUtc, bool settled = true)
    {
        var appointment = Appointment.Create(
            graph.TenantId, graph.LocationId, graph.ServiceId, graph.StaffId, graph.CustomerId,
            endAtUtc.AddMinutes(-30), endAtUtc, 30, 100.00m, "USD");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.Appointments.Add(appointment);
        await db.SaveChangesAsync();

        // Rewritten rather than transitioned to, so the seeded row carries one history row and the sweep's
        // own closure is the only one left to count.
        await db.Database.ExecuteSqlAsync(
            $"UPDATE Appointments SET Status = {(int)AppointmentStatus.Confirmed} WHERE Id = {appointment.Id}");

        if (settled)
        {
            db.Payments.Add(new Payment
            {
                Id = Guid.NewGuid(),
                TenantId = graph.TenantId,
                AppointmentId = appointment.Id,
                Amount = 100.00m,
                Currency = "USD",
                Status = PaymentStatus.Paid,
                Provider = PaymentProviderType.Simulated,
                ProviderPaymentId = $"prov-{Guid.NewGuid():N}",
                CreatedBy = "test"
            });
            await db.SaveChangesAsync();
        }

        return appointment.Id;
    }

    private sealed record Stored(AppointmentStatus Status, int HistoryRows);

    private async Task<Stored> ReadAsync(Guid appointmentId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var row = await db.Appointments
            .IgnoreQueryFilters()
            .Where(a => a.Id == appointmentId)
            .Select(a => new { a.Status, Histories = a.StatusHistories.Count })
            .FirstAsync();

        return new Stored(row.Status, row.Histories);
    }

    private async Task<int> CountAsync(Guid tenantId, AppointmentStatus status)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.Appointments
            .IgnoreQueryFilters()
            .CountAsync(a => a.TenantId == tenantId && a.Status == status);
    }

    private Task<NoShowSweepResult> SweepAsync(Guid? tenantId = null) => new AppointmentNoShowBackgroundService(
        _factory.Services.GetRequiredService<IServiceScopeFactory>(),
        _factory.Services.GetRequiredService<ILogger<AppointmentNoShowBackgroundService>>())
        .CloseUnattendedAppointmentsAsync(CancellationToken.None, tenantId);

    [Fact]
    public async Task AVisitTheStaffClosedWhileTheSweepWasJudging_MustNotBeRestatedAsAnAbsence()
    {
        var graph = await SeedGraphAsync("attended");
        var appointmentId = await SeedVisitAsync(graph, EndAt(8));

        _factory.QueryInterceptor.DisarmAfterRead();
        _factory.QueryInterceptor.ArmAfterRead(CandidateRead,
            reading => WriteStatusOfEarliestAsync(reading, graph.TenantId, AppointmentStatus.Completed));

        var result = await SweepAsync(graph.TenantId);

        _factory.QueryInterceptor.ArmHits.Should().Be(1,
            "the staff write has to land between the sweep's read and its write, or the test races nothing");

        var stored = await ReadAsync(appointmentId);
        stored.Status.Should().Be(AppointmentStatus.Completed,
            "the customer appeared, and a timer may not answer that question differently afterwards");
        stored.HistoryRows.Should().Be(1,
            "a closure that did not happen must not be written into the status log");
        result.Closed.Should().Be(0);
    }

    [Fact]
    public async Task AContendedBooking_MustNotCostItsNeighboursClosures()
    {
        var graph = await SeedGraphAsync("neighbours");
        var contended = await SeedVisitAsync(graph, EndAt(9));
        var neighbour = await SeedVisitAsync(graph, EndAt(8));

        _factory.QueryInterceptor.DisarmAfterRead();
        _factory.QueryInterceptor.ArmAfterRead(CandidateRead,
            reading => WriteStatusOfEarliestAsync(reading, graph.TenantId, AppointmentStatus.Completed));

        var result = await SweepAsync(graph.TenantId);

        (await ReadAsync(neighbour)).Status.Should().Be(AppointmentStatus.NoShow,
            "the other booking is a settled absence on its own facts; a row moving elsewhere is not its business");
        (await ReadAsync(contended)).Status.Should().Be(AppointmentStatus.Completed);

        result.Closed.Should().Be(1);
        result.SkippedContendedTenant.Should().Be(0,
            "one conflict is re-judged on the spot, not deferred to the next tick");
    }

    [Fact]
    public async Task TheReportedCounts_MustBeTheBookingsThatActuallyClosed()
    {
        var graph = await SeedGraphAsync("counts");
        var ids = new List<Guid>();
        for (var i = 0; i < 4; i++)
        {
            ids.Add(await SeedVisitAsync(graph, EndAt(12 - i)));
        }

        _factory.QueryInterceptor.DisarmAfterRead();
        _factory.QueryInterceptor.ArmAfterRead(CandidateRead,
            reading => WriteStatusOfEarliestAsync(reading, graph.TenantId, AppointmentStatus.Completed));

        var result = await SweepAsync(graph.TenantId);

        var storedClosures = await CountAsync(graph.TenantId, AppointmentStatus.NoShow);

        result.Closed.Should().Be(storedClosures,
            "a sweep that reports closures it did not commit turns a bookkeeping job into a false record");
        result.Closed.Should().Be(3, "four were closable, one moved under the sweep, and one conflict costs a re-judgement");

        foreach (var id in ids.Skip(1))
        {
            (await ReadAsync(id)).Status.Should().Be(AppointmentStatus.NoShow);
        }
    }

    [Fact]
    public async Task ARescheduledBookingInTheWindow_MustNotBeClosedAsAnAbsence()
    {
        var graph = await SeedGraphAsync("rescheduled");
        var appointmentId = await SeedVisitAsync(graph, EndAt(8));

        // The status is untouched, so a write that merely re-checked "still Confirmed" would let this row be
        // closed against a visit that now happens next month. Only the row the store actually holds can say
        // the sweep judged an old copy.
        _factory.QueryInterceptor.DisarmAfterRead();
        _factory.QueryInterceptor.ArmAfterRead(CandidateRead,
            reading => MoveEarliestAsync(reading, graph.TenantId, DateTime.UtcNow.AddDays(20)));

        var result = await SweepAsync(graph.TenantId);

        _factory.QueryInterceptor.ArmHits.Should().Be(1);
        var stored = await ReadAsync(appointmentId);
        stored.Status.Should().Be(AppointmentStatus.Confirmed);
        stored.HistoryRows.Should().Be(1);
        result.Closed.Should().Be(0);
    }

    [Fact]
    public async Task ATenantWhoseRowsKeepMoving_MustBeDeferredWithoutCostingTheOtherTenants()
    {
        var busy = await SeedGraphAsync("keeping-moving");
        var quiet = await SeedGraphAsync("quiet");

        for (var i = 0; i < 4; i++)
        {
            await SeedVisitAsync(busy, EndAt(12 - i));
        }

        var quietVisit = await SeedVisitAsync(quiet, EndAt(8));

        // Every re-judgement finds another row edited, so this tenant cannot be judged at all inside one
        // tick. It still has to end as a deferred tenant rather than an exception that takes the tick with it.
        _factory.QueryInterceptor.DisarmAfterRead();
        var flips = 0;
        _factory.QueryInterceptor.ArmAfterRead(CandidateRead, async reading =>
        {
            flips += await WriteStatusOfEarliestAsync(reading, busy.TenantId, AppointmentStatus.Completed);
        }, repeat: true);

        var closedBefore = await CountAsync(quiet.TenantId, AppointmentStatus.NoShow);
        var result = await SweepAsync();
        _factory.QueryInterceptor.DisarmAfterRead();

        flips.Should().Be(3,
            "the sweep has to be told three separate times that this tenant's rows moved before it gives up");
        result.SkippedContendedTenant.Should().Be(1,
            "one tenant that never settles is reported, not swallowed into a stack trace");
        result.Closed.Should().BeGreaterThan(0);

        (await CountAsync(quiet.TenantId, AppointmentStatus.NoShow)).Should().Be(closedBefore + 1,
            "a quiet tenant's closure is not the busy tenant's to withhold");
        (await ReadAsync(quietVisit)).Status.Should().Be(AppointmentStatus.NoShow);

        (await CountAsync(busy.TenantId, AppointmentStatus.NoShow)).Should().Be(0,
            "a tenant that could not be judged was rolled back whole, not closed by guesswork");
        (await CountAsync(busy.TenantId, AppointmentStatus.Confirmed)).Should().Be(1,
            "the last deferred booking stays open for the next tick");
    }

    [Fact]
    public async Task ABookingDeferredByContention_MustBeClosedByTheNextTick()
    {
        var graph = await SeedGraphAsync("next-tick");
        var contended = await SeedVisitAsync(graph, EndAt(9));
        var deferred = await SeedVisitAsync(graph, EndAt(8));

        _factory.QueryInterceptor.DisarmAfterRead();
        _factory.QueryInterceptor.ArmAfterRead(CandidateRead,
            reading => WriteStatusOfEarliestAsync(reading, graph.TenantId, AppointmentStatus.Completed));

        var first = await SweepAsync(graph.TenantId);
        _factory.QueryInterceptor.DisarmAfterRead();
        var second = await SweepAsync(graph.TenantId);

        first.Closed.Should().Be(1);
        second.Closed.Should().Be(0, "the booking left open by the conflict is Completed, so there is nothing to close twice");
        (await ReadAsync(deferred)).Status.Should().Be(AppointmentStatus.NoShow);
        (await ReadAsync(contended)).Status.Should().Be(AppointmentStatus.Completed);
    }

    [Fact]
    public async Task AReJudgedBatch_MustAskTheMoneyQuestionAgain()
    {
        var graph = await SeedGraphAsync("money-race");
        var earliest = await SeedVisitAsync(graph, EndAt(10));
        var closable = await SeedVisitAsync(graph, EndAt(9));
        var unpaid = await SeedVisitAsync(graph, EndAt(8), settled: false);

        _factory.QueryInterceptor.DisarmAfterRead();
        _factory.QueryInterceptor.ArmAfterRead(CandidateRead,
            reading => WriteStatusOfEarliestAsync(reading, graph.TenantId, AppointmentStatus.Completed));

        var result = await SweepAsync(graph.TenantId);

        result.Closed.Should().Be(1);
        result.SkippedWithBalance.Should().Be(1,
            "the re-judgement re-reads the ledger, so a skip carried over from the rolled-back attempt " +
            "could turn a booking somebody paid for in between into an absence");
        (await ReadAsync(earliest)).Status.Should().Be(AppointmentStatus.Completed);
        (await ReadAsync(closable)).Status.Should().Be(AppointmentStatus.NoShow);
        (await ReadAsync(unpaid)).Status.Should().Be(AppointmentStatus.Confirmed);
    }

    /// <summary>
    /// The money the closure is decided on lives in another table, so a refund can enter and leave between
    /// the sweep's ledger read and its write without moving the row the sweep is about to update. The result
    /// is a booking recorded as an absence that owes its whole price: a status the charge path refuses, and
    /// a row the outstanding queue cannot even list. This is the harm <see cref="LedgerRead"/> lands on
    /// purpose, and the appointment touch on the money paths is what makes the store refuse the closure.
    /// </summary>
    [Fact]
    public async Task ARefundThatLandsAfterTheLedgerRead_MustNotWriteItsDebtOntoAnUnchargeableStatus()
    {
        var graph = await SeedGraphAsync("refund-after-ledger-read");
        var visit = await SeedVisitAsync(graph, EndAt(9));
        var paymentId = await ReadPaymentIdAsync(visit);

        _factory.QueryInterceptor.DisarmAfterRead();
        _factory.QueryInterceptor.ArmAfterRead(LedgerRead, async _ => await RefundAsync(graph, paymentId, 100.00m));

        var result = await SweepAsync(graph.TenantId);
        _factory.QueryInterceptor.DisarmAfterRead();

        var stored = await ReadAsync(visit);

        _factory.QueryInterceptor.ArmHits.Should().Be(1,
            "the refund has to commit in the gap between the sweep's ledger read and its write");
        result.Closed.Should().Be(0,
            "the closure was judged on a ledger that no longer exists, so the store has to refuse it");
        result.SkippedWithBalance.Should().Be(1,
            "the re-judged batch re-reads the money and finds the debt the refund just created");
        stored.Status.Should().Be(AppointmentStatus.Confirmed,
            "a Confirmed row can still be charged and is still listed as owed; a NoShow is neither");
        stored.HistoryRows.Should().Be(1, "the rolled-back closure may not leave its history row behind");
    }

    /// <summary>
    /// The invariant the test above depends on, stated on its own so a future money path cannot quietly stop
    /// honouring it: both write paths carry the appointment's audit columns, which is what moves the token.
    /// </summary>
    [Fact]
    public async Task BothMoneyWrites_MustMoveTheTokenTheSweepJudgesOn()
    {
        var graph = await SeedGraphAsync("money-touches-the-row");
        var visit = await SeedVisitAsync(graph, EndAt(9), settled: false);

        var before = await ReadTokenAsync(visit);
        await ChargeAsync(graph, visit, 100.00m);
        var charged = await ReadTokenAsync(visit);

        charged.Token.Should().NotBeEquivalentTo(before.Token,
            "a charge settles the visit, and the sweep decides settlement from a read it cannot hold open");
        charged.LastModifiedAtUtc.Should().NotBeNull("the touch is an audit fact, not a hidden side channel");

        var paymentId = await ReadPaymentIdAsync(visit);
        await RefundAsync(graph, paymentId, 40.00m);
        var refunded = await ReadTokenAsync(visit);

        refunded.Token.Should().NotBeEquivalentTo(charged.Token,
            "a refund is the write that turns a settled booking back into a debt");
    }

    /// <summary>
    /// The touch is unconditional by id, and this is the reason: a money write that raced an edit of the
    /// appointment must not come back as a failure. A provider has already moved the amount, and a 500 would
    /// tell the desk it did not.
    /// </summary>
    [Fact]
    public async Task ARaceBetweenARefundAndAnEditOfTheAppointment_MustNotFailTheMoneyThatWasTaken()
    {
        var graph = await SeedGraphAsync("refund-races-an-edit");
        var visit = await SeedVisitAsync(graph, EndAt(9));
        var paymentId = await ReadPaymentIdAsync(visit);

        _factory.QueryInterceptor.DisarmAfterRead();
        _factory.QueryInterceptor.ArmAfterRead(
            PaymentWithAppointmentRead, _ => WriteStatusOfAsync(visit, AppointmentStatus.Completed));

        var response = await RefundRequestAsync(graph, paymentId, 100.00m);
        _factory.QueryInterceptor.DisarmAfterRead();

        _factory.QueryInterceptor.ArmHits.Should().Be(1,
            "the desk's edit has to land while the refund still holds the row it read, not before it read it");
        await AssertCommittedAsync(response);
        (await ReadAsync(visit)).Status.Should().Be(AppointmentStatus.Completed,
            "and the desk's edit survives a money write that landed after it");
    }

    /// <summary>
    /// The second writer: another session, outside the sweep's transaction, committing the instant the
    /// sweep's candidate reader closes. This is the one interleaving a test cannot reach by racing tasks.
    /// The sweep reads candidates oldest-visit-first, so the row it edits is the one the sweep put at the
    /// head of its own batch — and only while that batch belongs to the tenant the race is about.
    /// </summary>
    private async Task<int> WriteStatusOfEarliestAsync(
        IReadOnlyCollection<Guid> reading, Guid tenantId, AppointmentStatus status)
    {
        // The sweep visits every tenant in one tick, so the race only applies while the read in flight
        // belongs to this test's tenant.
        if (!reading.Contains(tenantId))
        {
            return 0;
        }

        await using var connection = new SqlConnection(_factory.ConnectionString);
        await using var command = connection.CreateCommand();

        command.CommandText =
            "UPDATE Appointments SET Status = @status WHERE Id = " +
            "(SELECT TOP 1 Id FROM Appointments WHERE TenantId = @tenantId AND Status = @confirmed ORDER BY EndAtUtc);";
        command.Parameters.Add("@status", SqlDbType.Int).Value = (int)status;
        AddTenantAndConfirmed(command, tenantId);

        await connection.OpenAsync();
        return await command.ExecuteNonQueryAsync();
    }

    private async Task MoveEarliestAsync(IReadOnlyCollection<Guid> reading, Guid tenantId, DateTime newStartUtc)
    {
        if (!reading.Contains(tenantId))
        {
            return;
        }

        await using var connection = new SqlConnection(_factory.ConnectionString);
        await using var command = connection.CreateCommand();

        command.CommandText =
            "UPDATE Appointments SET StartAtUtc = @start, EndAtUtc = @end WHERE Id = " +
            "(SELECT TOP 1 Id FROM Appointments WHERE TenantId = @tenantId AND Status = @confirmed ORDER BY EndAtUtc);";
        command.Parameters.Add("@start", SqlDbType.DateTime2).Value = newStartUtc;
        command.Parameters.Add("@end", SqlDbType.DateTime2).Value = newStartUtc.AddMinutes(30);
        AddTenantAndConfirmed(command, tenantId);

        await connection.OpenAsync();
        await command.ExecuteNonQueryAsync();
    }

    private static void AddTenantAndConfirmed(SqlCommand command, Guid tenantId)
    {
        command.Parameters.Add("@tenantId", SqlDbType.UniqueIdentifier).Value = tenantId;
        command.Parameters.Add("@confirmed", SqlDbType.Int).Value = (int)AppointmentStatus.Confirmed;
    }

    private async Task WriteStatusOfAsync(Guid appointmentId, AppointmentStatus status)
    {
        await using var connection = new SqlConnection(_factory.ConnectionString);
        await using var command = connection.CreateCommand();

        command.CommandText = "UPDATE Appointments SET Status = @status WHERE Id = @id;";
        command.Parameters.Add("@status", SqlDbType.Int).Value = (int)status;
        command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = appointmentId;

        await connection.OpenAsync();
        await command.ExecuteNonQueryAsync();
    }

    private async Task<Guid> ReadPaymentIdAsync(Guid appointmentId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.Payments
            .IgnoreQueryFilters()
            .Where(p => p.AppointmentId == appointmentId)
            .Select(p => p.Id)
            .FirstAsync();
    }

    private sealed record Row(byte[] Token, DateTime? LastModifiedAtUtc);

    private async Task<Row> ReadTokenAsync(Guid appointmentId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var row = await db.Appointments
            .IgnoreQueryFilters()
            .Where(a => a.Id == appointmentId)
            .Select(a => new { a.RowVersion, a.LastModifiedAtUtc })
            .FirstAsync();

        return new Row(row.RowVersion, row.LastModifiedAtUtc);
    }

    private HttpClient MoneyClient(Graph graph) => _factory.CreateClientForTenant(graph.TenantId, Roles.Accountant);

    /// <summary>
    /// The arm action swallows nothing, but a writer that silently 500s would leave the race unlanded and the
    /// test passing for the wrong reason. A JSON body is asserted with xunit's verbatim assertion because
    /// FluentAssertions treats the braces in it as a format string.
    /// </summary>
    private static async Task AssertCommittedAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.StatusCode == HttpStatusCode.OK,
            $"the money writer this race needs has to actually commit, got {(int)response.StatusCode}: {body}");
    }

    private async Task ChargeAsync(Graph graph, Guid appointmentId, decimal amount)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/payments/charge")
        {
            Content = JsonContent.Create(new PaymentsController.ProcessPaymentApiRequest(appointmentId, amount, "USD"))
        };

        await AssertCommittedAsync(await MoneyClient(graph).SendAsync(request));
    }

    private Task<HttpResponseMessage> RefundRequestAsync(Graph graph, Guid paymentId, decimal amount) =>
        MoneyClient(graph).PostAsJsonAsync("/api/v1/payments/refund",
            new PaymentsController.RefundPaymentApiRequest(paymentId, amount, "race refund"));

    private async Task RefundAsync(Graph graph, Guid paymentId, decimal amount) =>
        await AssertCommittedAsync(await RefundRequestAsync(graph, paymentId, amount));
}
