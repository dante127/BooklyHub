using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BooklyHub.Api.Controllers;
using BooklyHub.Application.Security;
using BooklyHub.Domain.Entities.Appointments;
using BooklyHub.Domain.Entities.Customers;
using BooklyHub.Domain.Entities.Organizations;
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
/// Every money rule is decided by reading what has already been charged. Against a real SQL Server two
/// clients can pass that read at the same moment, so these tests hold requests at the race and require the
/// appointment payment lock to leave the totals honest.
/// </summary>
public class PaymentIntegrityTests : IClassFixture<BooklyHubWebApplicationFactory>
{
    private const decimal Price = 120.00m;

    private readonly BooklyHubWebApplicationFactory _factory;

    public PaymentIntegrityTests(BooklyHubWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private sealed record Graph(Guid TenantId, Guid LocationId, Guid ServiceId, Guid StaffId, Guid CustomerId);

    private async Task<Graph> SeedGraphAsync()
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
            Price = Price
        });

        db.StaffMembers.Add(new Staff
        {
            Id = graph.StaffId,
            TenantId = graph.TenantId,
            LocationId = graph.LocationId,
            FirstName = "Doc",
            LastName = "One",
            Email = $"doc-{graph.StaffId:N}@cash.test"
        });

        db.Customers.Add(new Customer
        {
            Id = graph.CustomerId,
            TenantId = graph.TenantId,
            FirstName = "Ann",
            LastName = "Patient",
            Email = $"ann-{graph.CustomerId:N}@cash.test"
        });

        await db.SaveChangesAsync();
        return graph;
    }

    /// <summary>
    /// Appointments are written straight to the table because the booking guard is pinned by the scheduling
    /// tests; these ones are about what happens to the money afterwards.
    /// </summary>
    private async Task<Guid> SeedAppointmentAsync(
        Graph graph,
        decimal price = Price,
        string currency = "USD",
        AppointmentStatus status = AppointmentStatus.Confirmed)
    {
        var startAtUtc = DateTime.UtcNow.Date.AddDays(2).AddHours(9);

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
            currency);

        if (status != AppointmentStatus.Pending)
        {
            appointment.TransitionTo(status, "seeded for payment tests");
        }

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.Appointments.Add(appointment);
        await db.SaveChangesAsync();

        return appointment.Id;
    }

    private HttpClient MoneyClient(Graph graph) => _factory.CreateClientForTenant(graph.TenantId, Roles.Accountant);

    private async Task<HttpResponseMessage> ChargeAsync(
        Graph graph,
        Guid appointmentId,
        decimal amount,
        string currency = "USD",
        string? idempotencyKey = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/payments/charge")
        {
            Content = JsonContent.Create(new PaymentsController.ProcessPaymentApiRequest(appointmentId, amount, currency))
        };

        if (idempotencyKey != null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        return await MoneyClient(graph).SendAsync(request);
    }

    private async Task<Guid> ChargeAndReadPaymentIdAsync(Graph graph, Guid appointmentId, decimal amount)
    {
        var result = await AssertOkAsync(await ChargeAsync(graph, appointmentId, amount));
        return result.GetProperty("id").GetGuid();
    }

    private sealed record Outcome(int Status, string Body);

    /// <summary>
    /// Concurrent attempts all have to be judged together, and a 500 is only diagnosable when the failure
    /// message carries what every client actually got back.
    /// </summary>
    private static async Task<Outcome[]> OutcomesAsync(params Task<HttpResponseMessage>[] attempts)
    {
        var responses = await Task.WhenAll(attempts);
        var outcomes = new Outcome[responses.Length];

        for (var i = 0; i < responses.Length; i++)
        {
            outcomes[i] = new Outcome((int)responses[i].StatusCode, await responses[i].Content.ReadAsStringAsync());
        }

        return outcomes;
    }

    private static string RuleOf(Outcome outcome) =>
        JsonDocument.Parse(outcome.Body).RootElement.TryGetProperty("rule", out var rule)
            ? rule.GetString()!
            : $"<no rule {outcome.Status}: {outcome.Body}>";

    private static int CountOf(Outcome[] outcomes, HttpStatusCode status) =>
        outcomes.Count(o => o.Status == (int)status);

    private async Task<HttpResponseMessage> RefundAsync(Graph graph, Guid paymentId, decimal amount) =>
        await MoneyClient(graph).PostAsJsonAsync("/api/v1/payments/refund",
            new PaymentsController.RefundPaymentApiRequest(paymentId, amount, "test refund"));

    /// <summary>
    /// ProblemDetails serializes its extensions flattened, so the rule name sits at the root of the document.
    /// </summary>
    private static async Task<string?> RuleAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);

        return document.RootElement.TryGetProperty("rule", out var rule)
            ? rule.GetString()
            : $"<no rule {(int)response.StatusCode}: {body}>";
    }

    /// <summary>
    /// A failed money request has to be readable in the failure message, so this uses xunit's verbatim
    /// assertion instead of FluentAssertions, which treats a JSON body as a format string.
    /// </summary>
    private static async Task<JsonElement> AssertOkAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.StatusCode == HttpStatusCode.OK,
            $"expected 200 OK, got {(int)response.StatusCode}: {body}");

        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private sealed record Ledger(decimal TotalSpent, int PaymentCount, decimal Captured, decimal Refunded);

    private async Task<Ledger> ReadLedgerAsync(Graph graph, Guid appointmentId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var payments = await db.Payments
            .IgnoreQueryFilters()
            .Where(p => p.TenantId == graph.TenantId && p.AppointmentId == appointmentId)
            .Select(p => new
            {
                p.Amount,
                p.Status,
                Refunded = p.Refunds.Where(r => r.Status == PaymentStatus.Refunded).Sum(r => r.Amount)
            })
            .ToListAsync();

        var totalSpent = await db.Customers
            .IgnoreQueryFilters()
            .Where(c => c.Id == graph.CustomerId)
            .Select(c => c.TotalSpent)
            .SingleAsync();

        return new Ledger(
            totalSpent,
            payments.Count,
            payments.Where(p => p.Status is PaymentStatus.Paid or PaymentStatus.PartiallyRefunded or PaymentStatus.Refunded)
                .Sum(p => p.Amount),
            payments.Sum(p => p.Refunded));
    }

    [Fact]
    public async Task Charge_FullPriceTwice_SecondMustBeRefusedAsAlreadyPaid()
    {
        var graph = await SeedGraphAsync();
        var appointmentId = await SeedAppointmentAsync(graph);

        (await ChargeAsync(graph, appointmentId, Price)).StatusCode.Should().Be(HttpStatusCode.OK);

        var second = await ChargeAsync(graph, appointmentId, Price);

        second.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await RuleAsync(second)).Should().Be("AppointmentAlreadyPaid");

        var ledger = await ReadLedgerAsync(graph, appointmentId);
        ledger.PaymentCount.Should().Be(1);
        ledger.TotalSpent.Should().Be(Price);
    }

    [Fact]
    public async Task Charge_AboveTheBookedPrice_MustBeRefusedAndStoreNothing()
    {
        var graph = await SeedGraphAsync();
        var appointmentId = await SeedAppointmentAsync(graph);

        var response = await ChargeAsync(graph, appointmentId, Price + 50.00m);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await RuleAsync(response)).Should().Be("PaymentExceedsAmountDue");

        var ledger = await ReadLedgerAsync(graph, appointmentId);
        ledger.PaymentCount.Should().Be(0, "a refused charge may not leave a row behind");
        ledger.TotalSpent.Should().Be(0m);
    }

    [Fact]
    public async Task Charge_InSteps_MustStopExactlyAtThePrice()
    {
        var graph = await SeedGraphAsync();
        var appointmentId = await SeedAppointmentAsync(graph);

        (await ChargeAsync(graph, appointmentId, 40.00m)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await ChargeAsync(graph, appointmentId, 80.00m)).StatusCode.Should().Be(HttpStatusCode.OK);

        var third = await ChargeAsync(graph, appointmentId, 10.00m);
        (await RuleAsync(third)).Should().Be("AppointmentAlreadyPaid");

        var ledger = await ReadLedgerAsync(graph, appointmentId);
        ledger.Captured.Should().Be(Price);
        ledger.TotalSpent.Should().Be(Price, "two deposits must add up to exactly what was collected");
    }

    [Fact]
    public async Task Charge_CancelledAppointment_MustBeRefused()
    {
        var graph = await SeedGraphAsync();
        var appointmentId = await SeedAppointmentAsync(graph, status: AppointmentStatus.Cancelled);

        var response = await ChargeAsync(graph, appointmentId, Price);

        (await RuleAsync(response)).Should().Be("AppointmentNotChargeable");
        (await ReadLedgerAsync(graph, appointmentId)).PaymentCount.Should().Be(0);
    }

    [Fact]
    public async Task Charge_OtherCurrency_MustBeRefusedBeforeTheAmountIsConsidered()
    {
        var graph = await SeedGraphAsync();
        var appointmentId = await SeedAppointmentAsync(graph);

        var response = await ChargeAsync(graph, appointmentId, 10.00m, currency: "SYP");

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await RuleAsync(response)).Should().Be("PaymentCurrencyMismatch");
    }

    [Fact]
    public async Task ConcurrentCharges_FourClientsOneAppointment_MustCaptureThePriceOnce()
    {
        var graph = await SeedGraphAsync();
        var appointmentId = await SeedAppointmentAsync(graph);

        var attempts = Enumerable.Range(0, 4)
            .Select(_ => ChargeAsync(graph, appointmentId, Price))
            .ToArray();

        var outcomes = await OutcomesAsync(attempts);

        Assert.True(
            CountOf(outcomes, HttpStatusCode.OK) == 1,
            $"exactly one of {attempts.Length} clients may settle a {Price:0.00} appointment, got: " +
            string.Join(" | ", outcomes.Select(o => $"{o.Status} {o.Body}")));

        foreach (var loser in outcomes.Where(o => o.Status == 422))
        {
            // A loser that got a 500 lands here with its own body, so the race is readable when it breaks.
            RuleOf(loser).Should().Be("AppointmentAlreadyPaid");
        }

        var ledger = await ReadLedgerAsync(graph, appointmentId);
        ledger.PaymentCount.Should().Be(1);
        ledger.Captured.Should().Be(Price);
        ledger.TotalSpent.Should().Be(Price, "double capture would have counted the same appointment twice");
    }

    [Fact]
    public async Task ConcurrentRefunds_SixtyPercentEach_MustAllowOnlyOne()
    {
        var graph = await SeedGraphAsync();
        var appointmentId = await SeedAppointmentAsync(graph);
        var paymentId = await ChargeAndReadPaymentIdAsync(graph, appointmentId, Price);

        var outcomes = await OutcomesAsync(
            RefundAsync(graph, paymentId, 72.00m),
            RefundAsync(graph, paymentId, 72.00m));

        CountOf(outcomes, HttpStatusCode.OK).Should().Be(1);

        var loser = outcomes.Single(o => o.Status == 422);
        RuleOf(loser).Should().Be("RefundAmountExceeded");

        var ledger = await ReadLedgerAsync(graph, appointmentId);
        ledger.Refunded.Should().Be(72.00m, "two refunds of 72 against a payment of 120 is money that never existed");
        ledger.TotalSpent.Should().Be(48.00m);
    }

    [Fact]
    public async Task Refund_FullBalance_MustSettleThePaymentAndDropTheCustomerTotal()
    {
        var graph = await SeedGraphAsync();
        var appointmentId = await SeedAppointmentAsync(graph);
        var paymentId = await ChargeAndReadPaymentIdAsync(graph, appointmentId, Price);

        await AssertOkAsync(await RefundAsync(graph, paymentId, Price));

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var status = await db.Payments
                .IgnoreQueryFilters()
                .Where(p => p.Id == paymentId)
                .Select(p => p.Status)
                .SingleAsync();

            status.Should().Be(PaymentStatus.Refunded);
        }

        var ledger = await ReadLedgerAsync(graph, appointmentId);
        ledger.Refunded.Should().Be(Price);
        ledger.TotalSpent.Should().Be(0m, "money handed back must not stay in what the customer has spent");

        var again = await RefundAsync(graph, paymentId, 1.00m);
        (await RuleAsync(again)).Should().Be("InvalidPaymentStatusForRefund");
    }

    [Fact]
    public async Task ConcurrentCharges_TwoAppointmentsOfOneCustomer_MustBothCount()
    {
        var graph = await SeedGraphAsync();
        var first = await SeedAppointmentAsync(graph);
        var second = await SeedAppointmentAsync(graph);

        var outcomes = await OutcomesAsync(
            ChargeAsync(graph, first, Price),
            ChargeAsync(graph, second, Price));

        Assert.True(CountOf(outcomes, HttpStatusCode.OK) == 2, string.Join(" | ", outcomes.Select(o => o.Body)));

        var firstLedger = await ReadLedgerAsync(graph, first);

        firstLedger.Captured.Should().Be(Price);
        (await ReadLedgerAsync(graph, second)).Captured.Should().Be(Price);
        firstLedger.TotalSpent.Should().Be(Price * 2,
            "the two charges hold different locks, so the shared customer total has to be moved by SQL");
    }

    [Fact]
    public async Task Charge_SameIdempotencyKeyTwice_MustProduceOnePayment()
    {
        var graph = await SeedGraphAsync();
        var appointmentId = await SeedAppointmentAsync(graph);
        var key = Guid.NewGuid().ToString("N");

        var first = await ChargeAsync(graph, appointmentId, Price, idempotencyKey: key);
        var replay = await ChargeAsync(graph, appointmentId, Price, idempotencyKey: key);

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        replay.Headers.Contains("X-Idempotent-Replay").Should().BeTrue("the key must be recognised as a replay");

        var ledger = await ReadLedgerAsync(graph, appointmentId);
        ledger.PaymentCount.Should().Be(1);
        ledger.TotalSpent.Should().Be(Price);
    }
}
