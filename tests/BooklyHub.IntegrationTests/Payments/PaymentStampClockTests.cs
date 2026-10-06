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
using BooklyHub.Infrastructure.Data;
using BooklyHub.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BooklyHub.IntegrationTests.Payments;

/// <summary>
/// QUAL-04's residue was three money writes reading the machine clock while the handler beside them read
/// <see cref="BooklyHub.Application.Common.Interfaces.IClock"/>. Two of them were the ledger rows — a
/// <see cref="BooklyHub.Domain.Entities.Payments.PaymentTransaction"/> is not auditable, so the handler writing it
/// is its only time source — and the third was <see cref="Refund.CreatedAtUtc"/>, which the change tracker also
/// skips. The charge's own row is stamped by the tracker from the same clock, which is why the pair agrees instead
/// of racing. Pinning the clock makes each of these instants the application's own, and a fact that requires the
/// pinned instant cannot be satisfied by a wall clock two days away from it.
/// </summary>
public class PaymentStampClockTests : IClassFixture<BooklyHubWebApplicationFactory>
{
    private const decimal Price = 120.00m;

    private static readonly DateTime ChargeInstant = new(2026, 11, 3, 8, 15, 0, DateTimeKind.Utc);
    private static readonly DateTime RefundInstant = new(2026, 11, 4, 9, 30, 0, DateTimeKind.Utc);

    private readonly BooklyHubWebApplicationFactory _factory;

    public PaymentStampClockTests(BooklyHubWebApplicationFactory factory) => _factory = factory;

    private sealed record Graph(Guid TenantId, Guid LocationId, Guid ServiceId, Guid StaffId, Guid CustomerId);

    private async Task<Graph> SeedGraphAsync()
    {
        var graph = new Graph(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        db.Tenants.Add(new Tenant(graph.TenantId, "Timed Clinic", $"timed-{graph.TenantId:N}", "UTC"));
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
            Email = $"doc-{graph.StaffId:N}@timed.test"
        });
        db.Customers.Add(new Customer
        {
            Id = graph.CustomerId,
            TenantId = graph.TenantId,
            FirstName = "Ann",
            LastName = "Patient",
            Email = $"ann-{graph.CustomerId:N}@timed.test"
        });

        await db.SaveChangesAsync();
        return graph;
    }

    private async Task<Guid> SeedAppointmentAsync(Graph graph)
    {
        var startAtUtc = DateTime.UtcNow.Date.AddDays(2).AddHours(9);
        var appointment = Appointment.Create(
            graph.TenantId, graph.LocationId, graph.ServiceId, graph.StaffId, graph.CustomerId,
            startAtUtc, startAtUtc.AddMinutes(30), 30, Price, "USD");
        appointment.TransitionTo(AppointmentStatus.Confirmed, DateTime.UtcNow, "seeded for clock tests");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.Appointments.Add(appointment);
        await db.SaveChangesAsync();
        return appointment.Id;
    }

    private async Task<JsonElement> ChargeAsync(Graph graph, Guid appointmentId, decimal amount)
    {
        var client = _factory.CreateClientForTenant(graph.TenantId, Roles.Accountant);
        var response = await client.PostAsJsonAsync("/api/v1/payments/charge",
            new PaymentsController.ProcessPaymentApiRequest(appointmentId, amount, "USD"));
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private async Task<bool> RefundAsync(Graph graph, Guid paymentId, decimal amount)
    {
        var client = _factory.CreateClientForTenant(graph.TenantId, Roles.Accountant);
        var response = await client.PostAsJsonAsync("/api/v1/payments/refund",
            new PaymentsController.RefundPaymentApiRequest(paymentId, amount, "clock test refund"));
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return true;
    }

    private async Task<List<PaymentTransaction>> TransactionsAsync(Graph graph, Guid paymentId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.PaymentTransactions
            .IgnoreQueryFilters()
            .Where(t => t.PaymentId == paymentId)
            .ToListAsync();
    }

    [Fact]
    public async Task ACharge_MustStampItsLedgerEntryFromTheApplicationClock()
    {
        var graph = await SeedGraphAsync();
        var appointmentId = await SeedAppointmentAsync(graph);

        _factory.Clock.Pin(ChargeInstant);
        try
        {
            var payment = await ChargeAsync(graph, appointmentId, Price);
            var paymentId = payment.GetProperty("id").GetGuid();

            // The row's stamp is the change tracker's (Payment is IAuditableEntity), the ledger entry's is the
            // handler's. Two writers, one clock, and the pair has to name the pinned instant rather than the
            // machine's — which is what it meant for the ledger row to read DateTime.UtcNow.
            payment.GetProperty("createdAtUtc").GetDateTime().Should().Be(ChargeInstant);

            var transactions = await TransactionsAsync(graph, paymentId);
            transactions.Should().ContainSingle();
            transactions[0].Type.Should().Be(PaymentTransactionType.Charge);
            transactions[0].TimestampUtc.Should().Be(ChargeInstant,
                "the charge and its ledger entry are one event, and one event has one instant");
        }
        finally
        {
            _factory.Clock.Release();
        }
    }

    [Fact]
    public async Task ARefund_MustStampBothRowsAtTheClockOfTheRefundAndNotOfTheCharge()
    {
        var graph = await SeedGraphAsync();
        var appointmentId = await SeedAppointmentAsync(graph);

        Guid paymentId;
        _factory.Clock.Pin(ChargeInstant);
        try
        {
            paymentId = (await ChargeAsync(graph, appointmentId, Price)).GetProperty("id").GetGuid();
        }
        finally
        {
            _factory.Clock.Release();
        }

        _factory.Clock.Pin(RefundInstant);
        try
        {
            await RefundAsync(graph, paymentId, 40.00m);

            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var refund = await db.Refunds.IgnoreQueryFilters().SingleAsync(r => r.PaymentId == paymentId);
            refund.CreatedAtUtc.Should().Be(RefundInstant);

            var transactions = await TransactionsAsync(graph, paymentId);
            transactions.Should().HaveCount(2);

            var refundRow = transactions.Single(t => t.Type == PaymentTransactionType.Refund);
            refundRow.TimestampUtc.Should().Be(RefundInstant,
                "the refund row and its ledger entry are written by one handler, so they cannot name two instants");
            refundRow.TimestampUtc.Should().Be(refund.CreatedAtUtc);

            // The second pin is also what stops a single cached clock value from passing: had the handler read the
            // clock once and kept it, this row would carry the charge's instant.
            transactions.Single(t => t.Type == PaymentTransactionType.Charge)
                .TimestampUtc.Should().Be(ChargeInstant);
        }
        finally
        {
            _factory.Clock.Release();
        }
    }

    [Fact]
    public void TheApplicationLayer_MustNotReachForTheWallClock()
    {
        // QUAL-04's residue was three ledger stamps reading DateTime.UtcNow beside a handler reading IClock. The
        // closure is a source rule, and a source rule nobody checks drifts back, so this reads the shipped source:
        // no Application-layer file may name DateTime.UtcNow at all. The one wall-clock read the remediation
        // accepts is the JWT `expires` claim (Infrastructure/Security/AuthServices.cs), whose reason is that a
        // token has to agree with the clock its verifier really runs — that file is outside this rule by layer,
        // not by exception.
        var root = FindRepositoryRoot();

        var offenders = Directory
            .EnumerateFiles(Path.Combine(root, "src", "BooklyHub.Application"), "*.cs", SearchOption.AllDirectories)
            .SelectMany(file => File.ReadAllLines(file).Select((line, index) => (file, line, number: index + 1)))
            .Where(x => x.line.Contains("DateTime.UtcNow", StringComparison.Ordinal))
            .Select(x => $"{Path.GetRelativePath(root, x.file)}:{x.number}: {x.line.Trim()}")
            .ToList();

        offenders.Should().BeEmpty(
            "the Application layer has exactly one time source, and reading the machine clock bypasses it");
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "src", "BooklyHub.Application")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException(
            $"No src/BooklyHub.Application was found above {AppContext.BaseDirectory}.");
    }
}
