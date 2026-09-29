using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BooklyHub.Api.Controllers;
using BooklyHub.Application.Common.Models;
using BooklyHub.Application.Payments;
using BooklyHub.Application.Payments.Queries;
using BooklyHub.Application.Security;
using BooklyHub.Domain.Entities.Appointments;
using BooklyHub.Domain.Entities.Customers;
using BooklyHub.Domain.Entities.Organizations;
using BooklyHub.Domain.Entities.Payments;
using BooklyHub.Domain.Entities.Services;
using BooklyHub.Domain.Entities.StaffMembers;
using BooklyHub.Domain.Entities.Tenancy;
using BooklyHub.Domain.Enums;
using BooklyHub.Infrastructure.Data;
using BooklyHub.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BooklyHub.IntegrationTests.Payments;

/// <summary>
/// A booking the clinic is past its window on and still unpaid survives the sweep on purpose: a balance is
/// what the sweep refuses to write off. Until now that made the debt real, provable from the payment rows,
/// and invisible - the sweep only reported a count in a log line and the dashboard counts money it assumes
/// arrived. These tests bind the queue that reads it: who is in it, what it says is owed, and the rule the
/// whole surface rests on, that membership is decided in SQL and must agree with the ledger the charge path
/// enforces.
/// </summary>
public class OutstandingVisitQueueTests : IClassFixture<BooklyHubWebApplicationFactory>
{
    private const decimal Price = 120.00m;

    private static readonly JsonSerializerOptions JsonOptions = BuildJsonOptions();

    private static JsonSerializerOptions BuildJsonOptions()
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private readonly BooklyHubWebApplicationFactory _factory;

    public OutstandingVisitQueueTests(BooklyHubWebApplicationFactory factory)
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

        db.Tenants.Add(new Tenant(graph.TenantId, $"Queue {slug}", $"queue-{slug}-{graph.TenantId:N}", "UTC"));
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
            Price = Price
        });

        var staff = new Staff
        {
            Id = graph.StaffId,
            TenantId = graph.TenantId,
            LocationId = graph.LocationId,
            FirstName = "Ola",
            LastName = "Doc",
            Email = $"ola-{graph.StaffId:N}@queue.test"
        };
        staff.StaffServices.Add(new StaffService
        {
            TenantId = graph.TenantId,
            StaffId = graph.StaffId,
            ServiceId = graph.ServiceId
        });
        db.StaffMembers.Add(staff);

        db.Customers.Add(new Customer
        {
            Id = graph.CustomerId,
            TenantId = graph.TenantId,
            FirstName = "Ziad",
            LastName = "Patient",
            Email = $"ziad-{graph.CustomerId:N}@queue.test"
        });

        await db.SaveChangesAsync();
        return graph;
    }

    private async Task<Guid> SeedVisitAsync(
        Graph graph,
        DateTime endAtUtc,
        AppointmentStatus status,
        decimal price = Price)
    {
        var appointment = Appointment.Create(
            graph.TenantId, graph.LocationId, graph.ServiceId, graph.StaffId, graph.CustomerId,
            endAtUtc.AddMinutes(-30), endAtUtc, 30, price, "USD");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.Appointments.Add(appointment);
        await db.SaveChangesAsync();

        if (status != AppointmentStatus.Pending)
        {
            // Rewritten, not transitioned to: the queue judges a row by its status, and walking the state
            // machine would refuse several of the combinations this test has to prove it excludes.
            await db.Database.ExecuteSqlAsync(
                $"UPDATE Appointments SET Status = {(int)status} WHERE Id = {appointment.Id}");
        }

        return appointment.Id;
    }

    private async Task SeedMoneyAsync(
        Graph graph,
        Guid appointmentId,
        decimal amount,
        PaymentStatus status = PaymentStatus.Paid,
        decimal? refundAmount = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var payment = new Payment
        {
            TenantId = graph.TenantId,
            AppointmentId = appointmentId,
            Amount = amount,
            Currency = "USD",
            Status = status,
            Provider = PaymentProviderType.Simulated,
            ProviderPaymentId = $"prov-{Guid.NewGuid():N}",
            CreatedBy = "test"
        };

        if (refundAmount.HasValue)
        {
            payment.Refunds.Add(new Refund
            {
                PaymentId = payment.Id,
                Amount = refundAmount.Value,
                Status = PaymentStatus.Refunded,
                ProviderRefundId = $"ref-{Guid.NewGuid():N}"
            });
        }

        db.Payments.Add(payment);
        await db.SaveChangesAsync();
    }

    private async Task<PaginatedList<OutstandingVisitDto>> QueueAsync(
        Graph graph,
        int page = 1,
        int pageSize = 20,
        string role = Roles.Accountant)
    {
        var client = _factory.CreateClientForTenant(graph.TenantId, role);
        var response = await client.GetAsync(
            $"/api/v1/payments/outstanding-visits?page={page}&pageSize={pageSize}");

        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);

        var result = await response.Content.ReadFromJsonAsync<PaginatedList<OutstandingVisitDto>>(JsonOptions);
        result.Should().NotBeNull();
        return result!;
    }

    private async Task<Dictionary<Guid, OutstandingVisitDto>> QueueByIdAsync(Graph graph)
    {
        var list = await QueueAsync(graph, pageSize: 100);
        return list.Items.ToDictionary(i => i.AppointmentId);
    }

    private sealed record Visit(Guid Id, decimal Price, AppointmentStatus Status, DateTime EndAtUtc, PaymentLedger Ledger);

    private sealed record MoneyShape(decimal Amount, PaymentStatus Status, decimal? RefundAmount);

    /// <summary>
    /// The tenant's whole book, read through <see cref="PaymentLedger"/> in memory. This is the second,
    /// independent form of the queue's money rule: production decides membership in SQL, this decides it in
    /// C#, and a differential test is the only thing that can tell whether the two still say the same thing.
    /// </summary>
    private async Task<IReadOnlyList<Visit>> VisitsAsync(Graph graph)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var rows = await db.Appointments
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(a => a.TenantId == graph.TenantId)
            .Select(a => new { a.Id, a.Price, a.Status, a.EndAtUtc })
            .ToListAsync();

        var payments = await db.Payments
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Include(p => p.Refunds)
            .Where(p => p.TenantId == graph.TenantId)
            .ToListAsync();

        return rows
            .Select(r => new Visit(
                r.Id,
                r.Price,
                r.Status,
                r.EndAtUtc,
                PaymentLedger.From(payments.Where(p => p.AppointmentId == r.Id))))
            .ToList();
    }

    [Fact]
    public async Task VisitWithNoPaymentHistory_MustBeListedOwingItsFullPrice()
    {
        var graph = await SeedGraphAsync("never-charged");
        var visitId = await SeedVisitAsync(graph, EndAt(10), AppointmentStatus.Confirmed);

        var queued = await QueueByIdAsync(graph);

        queued.Should().ContainSingle();
        var row = queued[visitId];
        row.AmountDue.Should().Be(Price, "nothing was ever charged, so the whole price is still owed");
        row.NetPaid.Should().Be(0m);
        row.Status.Should().Be(AppointmentStatus.Confirmed);
        row.CustomerName.Should().Be("Ziad Patient");
        row.Currency.Should().Be("USD");
    }

    [Fact]
    public async Task VisitPaidInFull_MustNotBeListed()
    {
        var graph = await SeedGraphAsync("settled");
        var visitId = await SeedVisitAsync(graph, EndAt(10), AppointmentStatus.Confirmed);
        await SeedMoneyAsync(graph, visitId, Price);

        var queued = await QueueByIdAsync(graph);

        queued.Should().BeEmpty("listing a booking that paid would accuse a customer who settled");
    }

    [Fact]
    public async Task VisitPartiallyPaid_MustBeListedOwingTheRemainder()
    {
        var graph = await SeedGraphAsync("partial");
        var visitId = await SeedVisitAsync(graph, EndAt(10), AppointmentStatus.Confirmed);
        await SeedMoneyAsync(graph, visitId, 40.00m);

        var row = (await QueueByIdAsync(graph))[visitId];

        row.NetPaid.Should().Be(40.00m);
        row.AmountDue.Should().Be(80.00m);
    }

    [Fact]
    public async Task RefundBackToZero_MustPutTheVisitBackInTheQueue()
    {
        var graph = await SeedGraphAsync("refunded-wholly");
        var visitId = await SeedVisitAsync(graph, EndAt(10), AppointmentStatus.Completed);
        await SeedMoneyAsync(graph, visitId, Price, PaymentStatus.Refunded, refundAmount: Price);

        var row = (await QueueByIdAsync(graph))[visitId];

        row.NetPaid.Should().Be(0m, "the money came in and went back out");
        row.AmountDue.Should().Be(Price, "a refund to zero leaves the visit owed, not settled");
    }

    [Fact]
    public async Task PartialRefund_MustBeTheAmountTheQueueAsksFor()
    {
        var graph = await SeedGraphAsync("refunded-partly");
        var visitId = await SeedVisitAsync(graph, EndAt(10), AppointmentStatus.Completed);
        await SeedMoneyAsync(graph, visitId, Price, PaymentStatus.PartiallyRefunded, refundAmount: 30.00m);

        var row = (await QueueByIdAsync(graph))[visitId];

        row.NetPaid.Should().Be(90.00m);
        row.AmountDue.Should().Be(30.00m);
    }

    [Fact]
    public async Task MoneyThatNeverCaptured_MustNotCountTowardSettlingAVisit()
    {
        var graph = await SeedGraphAsync("not-captured");
        var failed = await SeedVisitAsync(graph, EndAt(10), AppointmentStatus.Confirmed);
        var attempted = await SeedVisitAsync(graph, EndAt(11), AppointmentStatus.Confirmed);
        await SeedMoneyAsync(graph, failed, Price, PaymentStatus.Failed);
        await SeedMoneyAsync(graph, attempted, Price, PaymentStatus.Pending);

        var queued = await QueueByIdAsync(graph);

        queued.Should().HaveCount(2,
            "a Failed or Pending row is an attempt, not money; leaving it out would hide exactly the debt that has to be chased");
        queued[failed].AmountDue.Should().Be(Price);
        queued[attempted].AmountDue.Should().Be(Price);
    }

    [Fact]
    public async Task CompletedVisit_MustStillBeListed()
    {
        var graph = await SeedGraphAsync("done-unpaid");
        var visitId = await SeedVisitAsync(graph, EndAt(10), AppointmentStatus.Completed);

        var queued = await QueueByIdAsync(graph);

        queued.Should().ContainSingle();
        queued[visitId].Status.Should().Be(AppointmentStatus.Completed,
            "the visit was rendered, so the debt is real even though the row is closed");
    }

    [Fact]
    public async Task VisitThatEndedWithNothingOwed_MustNotBeListed()
    {
        var graph = await SeedGraphAsync("cancelled-and-absent");
        await SeedVisitAsync(graph, EndAt(10), AppointmentStatus.Cancelled);
        await SeedVisitAsync(graph, EndAt(10), AppointmentStatus.NoShow);

        var queued = await QueueByIdAsync(graph);

        queued.Should().BeEmpty("neither visit was rendered, so neither has a debt to collect");
    }

    [Fact]
    public async Task VisitTheClinicNeverOwnsYet_MustBeLeftToTheSurfacesThatCloseIt()
    {
        var graph = await SeedGraphAsync("not-collectable");
        await SeedVisitAsync(graph, EndAt(10), AppointmentStatus.Pending);
        await SeedVisitAsync(graph, EndAt(10), AppointmentStatus.CheckedIn);
        await SeedVisitAsync(graph, EndAt(10), AppointmentStatus.InProgress);

        var queued = await QueueByIdAsync(graph);

        queued.Should().BeEmpty(
            "Pending was never accepted, and an open execution row has to be closed before its money question can be answered");
    }

    [Fact]
    public async Task VisitStillInsideTheGraceWindow_MustNotBeListed()
    {
        var graph = await SeedGraphAsync("not-yet-due");
        await SeedVisitAsync(graph, EndAt(2), AppointmentStatus.Confirmed);

        var queued = await QueueByIdAsync(graph);

        queued.Should().BeEmpty(
            "the queue calls a visit overdue on the same window the sweep uses, not on the calendar date");
    }

    [Fact]
    public async Task DebtOlderThanTheSweepsLookback_MustStillBeListed()
    {
        var graph = await SeedGraphAsync("ancient");
        var visitId = await SeedVisitAsync(graph, EndAt(24 * 40), AppointmentStatus.Confirmed);

        var row = (await QueueByIdAsync(graph))[visitId];

        row.AmountDue.Should().Be(Price,
            "the sweep refuses to restate an old period on a timer; a human reading a queue has no such limit");
    }

    [Fact]
    public async Task FreeVisit_MustNotBeListed()
    {
        var graph = await SeedGraphAsync("free");
        await SeedVisitAsync(graph, EndAt(10), AppointmentStatus.Confirmed, price: 0m);

        (await QueueByIdAsync(graph)).Should().BeEmpty("a booking that owed nothing cannot be owed anything");
    }

    [Fact]
    public async Task Queue_MustBeOrderedOldestDebtFirstAndPageWithoutGapsOrDuplicates()
    {
        var graph = await SeedGraphAsync("ordering");
        var newest = await SeedVisitAsync(graph, EndAt(10), AppointmentStatus.Confirmed);
        var middle = await SeedVisitAsync(graph, EndAt(24 * 3), AppointmentStatus.Confirmed);
        var oldest = await SeedVisitAsync(graph, EndAt(24 * 9), AppointmentStatus.Confirmed);

        var firstPage = await QueueAsync(graph, page: 1, pageSize: 2);
        var secondPage = await QueueAsync(graph, page: 2, pageSize: 2);

        firstPage.TotalCount.Should().Be(3);
        firstPage.HasNextPage.Should().BeTrue();
        secondPage.HasPreviousPage.Should().BeTrue();

        firstPage.Items.Select(i => i.AppointmentId).Should().Equal(new[] { oldest, middle },
            "a clinic chases the oldest debt first, and paging on any other order repeats or drops a row");
        secondPage.Items.Select(i => i.AppointmentId).Should().Equal(new[] { newest });

        var all = firstPage.Items.Concat(secondPage.Items).Select(i => i.AppointmentId).ToList();
        all.Should().OnlyHaveUniqueItems();
        all.Should().HaveCount(firstPage.TotalCount);
    }

    [Fact]
    public async Task AcrossTenants_TheQueueMustShowOnlyItsOwnDebts()
    {
        var graphA = await SeedGraphAsync("tenant-a");
        var graphB = await SeedGraphAsync("tenant-b");
        var debtA = await SeedVisitAsync(graphA, EndAt(10), AppointmentStatus.Confirmed);
        var debtB = await SeedVisitAsync(graphB, EndAt(10), AppointmentStatus.Confirmed);

        var queuedA = await QueueByIdAsync(graphA);
        queuedA.Should().ContainSingle().Which.Key.Should().Be(debtA);

        // The same tenant, requested as a platform admin: the global filter is open there, so the handler's
        // own predicate is the only thing scoping the answer.
        var admin = _factory.CreatePlatformAdminClient();
        admin.DefaultRequestHeaders.Add("X-Tenant-Id", graphA.TenantId.ToString());
        var asAdmin = await admin.GetFromJsonAsync<PaginatedList<OutstandingVisitDto>>(
            "/api/v1/payments/outstanding-visits?pageSize=100", JsonOptions);

        asAdmin!.Items.Select(i => i.AppointmentId).Should().Equal(debtA);
        asAdmin.Items.Should().NotContain(i => i.AppointmentId == debtB,
            "an open tenant filter makes the explicit predicate the last line of isolation");
    }

    [Fact]
    public async Task EveryPaymentShape_SqlMembershipAndAmountsMustMatchTheLedger()
    {
        var graph = await SeedGraphAsync("differential");

        // One book holding every shape the money rule can meet at once: no rows, wholly paid, partly paid,
        // refunded back to zero, partly refunded, two payments that settle, two that do not, a capture that
        // never happened next to one that did, an attempt still Pending, three statuses the queue must refuse
        // on status alone, and one row inside grace it must refuse on the window alone.
        //
        // A refund drawn on a payment that never captured is deliberately absent: the refund path refuses any
        // payment that is not Paid or PartiallyRefunded, so that shape cannot be reached through the API.
        var shapes = new[]
        {
            (AppointmentStatus.Confirmed, 10, Array.Empty<MoneyShape>()),
            (AppointmentStatus.Confirmed, 12, new[] { new MoneyShape(Price, PaymentStatus.Paid, null) }),
            (AppointmentStatus.Confirmed, 14, new[] { new MoneyShape(40.00m, PaymentStatus.Paid, null) }),
            (AppointmentStatus.Completed, 16, new[] { new MoneyShape(Price, PaymentStatus.Refunded, Price) }),
            (AppointmentStatus.Completed, 18, new[] { new MoneyShape(Price, PaymentStatus.PartiallyRefunded, 30.00m) }),
            (AppointmentStatus.Confirmed, 20, new[] { new MoneyShape(70.00m, PaymentStatus.Paid, null), new MoneyShape(50.00m, PaymentStatus.Paid, null) }),
            (AppointmentStatus.Confirmed, 22, new[] { new MoneyShape(70.00m, PaymentStatus.Paid, null), new MoneyShape(30.00m, PaymentStatus.Paid, null) }),
            (AppointmentStatus.Confirmed, 24, new[] { new MoneyShape(60.00m, PaymentStatus.Failed, null), new MoneyShape(60.00m, PaymentStatus.Paid, null) }),
            (AppointmentStatus.Confirmed, 26, new[] { new MoneyShape(Price, PaymentStatus.Pending, null) }),
            (AppointmentStatus.Cancelled, 28, Array.Empty<MoneyShape>()),
            (AppointmentStatus.NoShow, 30, Array.Empty<MoneyShape>()),
            (AppointmentStatus.CheckedIn, 32, Array.Empty<MoneyShape>()),
            (AppointmentStatus.Confirmed, 2, Array.Empty<MoneyShape>())
        };

        foreach (var (status, hoursAgo, money) in shapes)
        {
            var visitId = await SeedVisitAsync(graph, EndAt(hoursAgo), status);
            foreach (var shape in money)
            {
                await SeedMoneyAsync(graph, visitId, shape.Amount, shape.Status, shape.RefundAmount);
            }
        }

        // The second statement of the rule, computed in memory by the same object the charge path enforces
        // its limits with. Membership, the amount asked for and the amount already paid all have to agree.
        var expected = (await VisitsAsync(graph))
            .Where(v => AppointmentStatusSet.Collectable.Contains(v.Status) &&
                        v.EndAtUtc <= NoShowClosurePolicy.DeadlineUtc(DateTime.UtcNow))
            .Select(v => (Visit: v, Due: v.Ledger.Outstanding(v.Price)))
            .Where(x => x.Due > 0m)
            .ToDictionary(x => x.Visit.Id, x => (x.Due, x.Visit.Ledger.Net));

        var actual = await QueueByIdAsync(graph);

        expected.Should().HaveCount(7,
            "seven of the thirteen shapes still owe inside the window; a differential test on an empty answer proves nothing");

        actual.Keys.Should().BeEquivalentTo(expected.Keys,
            "the SQL predicate and the in-memory ledger are two statements of one rule, and a membership they " +
            "disagree on is a bug in whichever direction it falls: a settled customer accused, or a debt dropped");

        foreach (var (id, (due, net)) in expected)
        {
            actual[id].AmountDue.Should().Be(due, $"the queue must invite exactly what {id} still owes");
            actual[id].NetPaid.Should().Be(net);
        }
    }

    [Fact]
    public async Task CollectingTheDebt_MustTakeTheVisitOutOfTheQueue()
    {
        var graph = await SeedGraphAsync("collected");
        var visitId = await SeedVisitAsync(graph, EndAt(10), AppointmentStatus.Confirmed);

        (await QueueByIdAsync(graph)).Should().ContainSingle();

        var client = _factory.CreateClientForTenant(graph.TenantId, Roles.Accountant);
        var charge = await client.PostAsJsonAsync(
            "/api/v1/payments/charge",
            new PaymentsController.ProcessPaymentApiRequest(visitId, Price, "USD"));

        var body = await charge.Content.ReadAsStringAsync();
        charge.StatusCode.Should().Be(HttpStatusCode.OK, body);

        var after = await QueueByIdAsync(graph);
        after.Should().BeEmpty("the amount the queue showed was the amount the charge path accepted");
    }

    [Fact]
    public async Task WritingTheDebtOffAsAnAbsence_MustTakeTheVisitOutOfTheQueue()
    {
        var graph = await SeedGraphAsync("written-off");
        var visitId = await SeedVisitAsync(graph, EndAt(10), AppointmentStatus.Confirmed);

        (await QueueByIdAsync(graph)).Should().ContainSingle();

        var client = _factory.CreateClientForTenant(graph.TenantId, Roles.Manager);
        var writeOff = await client.PostAsJsonAsync(
            $"/api/v1/appointments/{visitId}/transition",
            new { NewStatus = nameof(AppointmentStatus.NoShow), Reason = "Debt waived by the manager" });

        var body = await writeOff.Content.ReadAsStringAsync();
        writeOff.StatusCode.Should().Be(HttpStatusCode.OK, body);

        (await QueueByIdAsync(graph)).Should().BeEmpty(
            "a written-off visit is no longer collectable, and leaving it listed would send the desk after it again");
    }

    [Fact]
    public async Task TheWindow_MustBeJudgedOnTheApplicationClockNotTheMachineOne()
    {
        var graph = await SeedGraphAsync("pinned");

        // Three hours past its end: inside grace for the machine clock, and it stays out of the queue.
        var insideGrace = await SeedVisitAsync(graph, EndAt(3), AppointmentStatus.Confirmed);

        try
        {
            _factory.Clock.AdvanceBy(TimeSpan.FromDays(2));

            var queued = await QueueByIdAsync(graph);
            queued.Should().ContainSingle().Which.Value.AppointmentId.Should().Be(insideGrace,
                "passing two days on the application clock puts every window bound out of grace at once");
        }
        finally
        {
            _factory.Clock.Release();
        }

        (await QueueByIdAsync(graph)).Should().BeEmpty("and the queue goes back with the clock");
    }

    [Fact]
    public async Task ADeskWithoutPaymentRead_MustNotBeGivenTheQueue()
    {
        var graph = await SeedGraphAsync("permission");
        await SeedVisitAsync(graph, EndAt(10), AppointmentStatus.Confirmed);

        // Staff carries no payments permission at all, while Receptionist - the desk that actually chases
        // payment - does. That difference is why this surface is gated on payments.read and not reports.read.
        var staff = _factory.CreateClientForTenant(graph.TenantId, Roles.Staff);
        (await staff.GetAsync("/api/v1/payments/outstanding-visits"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var reception = _factory.CreateClientForTenant(graph.TenantId, Roles.Receptionist);
        var response = await reception.GetAsync("/api/v1/payments/outstanding-visits");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Ziad");
    }

    [Fact]
    public async Task TheQueue_MustStayThreeStatementsAndScopeEveryOneOfThem()
    {
        var graph = await SeedGraphAsync("sql-shape");
        await SeedVisitAsync(graph, EndAt(10), AppointmentStatus.Confirmed);
        await SeedVisitAsync(graph, EndAt(11), AppointmentStatus.Completed);

        // Warm the tenant's client first: the token issuance itself touches the book and would be counted.
        var client = _factory.CreateClientForTenant(graph.TenantId, Roles.Accountant);
        await client.GetAsync("/api/v1/payments/outstanding-visits");

        _factory.QueryInterceptor.Reset();
        (await client.GetAsync("/api/v1/payments/outstanding-visits")).EnsureSuccessStatusCode();

        var commands = _factory.QueryInterceptor.ExecutedCommands;

        // Count, the page of bookings, one payment read for the whole page. A fourth statement means the
        // ledger is being rebuilt row by row, which is the N+1 this shape exists to avoid.
        commands.Count.Should().BeLessThanOrEqualTo(3,
            because: $"the queue must not load a payment table per booking. Observed:\n{string.Join("\n---\n", commands)}");
        commands.Should().OnlyContain(command => command.Contains("TenantId"),
            because: $"every statement of a tenant's queue must be tenant-scoped. Observed:\n{string.Join("\n---\n", commands)}");
    }
}
