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

namespace BooklyHub.IntegrationTests.Appointments;

/// <summary>
/// A NoShow or a Cancelled row cannot be charged, so moving a booking into one of them while it still owes
/// money writes the balance off — and the outstanding queue loses that row in the same instant, because its
/// membership is the mirror image of the refusal. Until now /transition accepted that without a word of
/// explanation while /cancel demanded one, so the reason was a rule a caller could step around by choosing a
/// different URL (WRI-01). These tests bind the door: the money has to be named by whoever strands it, and the
/// debt stays collectable and listed until somebody does.
/// </summary>
public class DebtWriteOffTests : IClassFixture<BooklyHubWebApplicationFactory>
{
    private const decimal Price = 100.00m;

    private readonly BooklyHubWebApplicationFactory _factory;

    public DebtWriteOffTests(BooklyHubWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private sealed record Graph(Guid TenantId, Guid LocationId, Guid ServiceId, Guid StaffId, Guid CustomerId);

    /// <summary>
    /// Past the sweep's six-hour grace, so the visit is genuinely one the queue would show and the temporal
    /// gate cannot object to closing.
    /// </summary>
    private static DateTime PastStart() => DateTime.UtcNow.AddHours(-9.5);

    private async Task<Graph> SeedGraphAsync(string slug)
    {
        var graph = new Graph(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        db.Tenants.Add(new Tenant(graph.TenantId, $"Write-off Clinic {slug}", $"writeoff-{slug}-{graph.TenantId:N}", "UTC"));

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
            Email = $"doc-{graph.StaffId:N}@writeoff.test"
        });

        db.Customers.Add(new Customer
        {
            Id = graph.CustomerId,
            TenantId = graph.TenantId,
            FirstName = "Ann",
            LastName = "Patient",
            Email = $"ann-{graph.CustomerId:N}@writeoff.test"
        });

        await db.SaveChangesAsync();
        return graph;
    }

    /// <summary>
    /// Seeded straight into the table because the booking guard is pinned elsewhere; these are about what the
    /// status door does to a balance.
    /// </summary>
    private async Task<Guid> SeedVisitAsync(Graph graph, bool settled = false)
    {
        var start = PastStart();

        var appointment = Appointment.Create(
            graph.TenantId,
            graph.LocationId,
            graph.ServiceId,
            graph.StaffId,
            graph.CustomerId,
            start,
            start.AddMinutes(30),
            30,
            Price,
            "USD");

        appointment.TransitionTo(AppointmentStatus.Confirmed, DateTime.UtcNow, "seeded");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.Appointments.Add(appointment);

        if (settled)
        {
            db.Payments.Add(new Payment
            {
                Id = Guid.NewGuid(),
                TenantId = graph.TenantId,
                AppointmentId = appointment.Id,
                Amount = Price,
                Currency = "USD",
                Status = PaymentStatus.Paid,
                Provider = PaymentProviderType.Simulated,
                ProviderPaymentId = $"prov-{Guid.NewGuid():N}",
                CreatedBy = "test"
            });
        }

        await db.SaveChangesAsync();
        return appointment.Id;
    }

    private HttpClient Desk(Graph graph) => _factory.CreateClientForTenant(graph.TenantId, Roles.Manager);

    private HttpClient Money(Graph graph) => _factory.CreateClientForTenant(graph.TenantId, Roles.Accountant);

    private Task<HttpResponseMessage> TransitionAsync(
        Graph graph,
        Guid appointmentId,
        AppointmentStatus status,
        string? reason) =>
        Desk(graph).PostAsJsonAsync(
            $"/api/v1/appointments/{appointmentId}/transition",
            new AppointmentsController.TransitionStatusRequest(status, reason));

    private async Task<HttpResponseMessage> ChargeAsync(Graph graph, Guid appointmentId, decimal amount)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/payments/charge")
        {
            Content = JsonContent.Create(new PaymentsController.ProcessPaymentApiRequest(appointmentId, amount, "USD"))
        };

        return await Money(graph).SendAsync(request);
    }

    /// <summary>
    /// The queue is the debt's only surface, so "refused" has to mean "still listed". Read as raw JSON because
    /// the assertion is membership, not the projected amount.
    /// </summary>
    private async Task<bool> IsListedAsOwedAsync(Graph graph, Guid appointmentId)
    {
        var response = await Money(graph).GetAsync("/api/v1/payments/outstanding-visits?pageSize=100");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);

        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("items").EnumerateArray()
            .Any(i => i.GetProperty("appointmentId").GetGuid() == appointmentId);
    }

    /// <summary>
    /// A NoShow write-off keeps no field on the appointment: <see cref="Appointment"/> writes
    /// <see cref="Appointment.CancellationReason"/> only for a Cancelled status, so the history row is the
    /// whole record of who stranded the money and why.
    /// </summary>
    private sealed record Stored(AppointmentStatus Status, int HistoryRows, string? CancellationReason, string? NoShowReason);

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
                a.CancellationReason,
                Histories = a.StatusHistories.Count,
                NoShowReason = a.StatusHistories
                    .Where(h => h.ToStatus == AppointmentStatus.NoShow)
                    .OrderByDescending(h => h.ChangedAtUtc)
                    .Select(h => h.Reason)
                    .FirstOrDefault()
            })
            .FirstAsync();

        return new Stored(row.Status, row.Histories, row.CancellationReason, row.NoShowReason);
    }

    private static string ReadRule(string body) =>
        JsonDocument.Parse(body).RootElement.GetProperty("rule").GetString()!;

    [Fact]
    public async Task Transition_UnpaidVisitToNoShowWithoutAReason_MustBeRefusedAndStayCollectable()
    {
        var graph = await SeedGraphAsync("refused");
        var visit = await SeedVisitAsync(graph);

        var before = await ReadAsync(visit);
        before.Status.Should().Be(AppointmentStatus.Confirmed);
        (await IsListedAsOwedAsync(graph, visit)).Should().BeTrue("the debt starts out on the queue");

        var response = await TransitionAsync(graph, visit, AppointmentStatus.NoShow, null);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity, body);
        ReadRule(body).Should().Be("DebtWriteOffReasonRequired");
        body.Should().Contain("100.00 of 100.00", "the writer has to see the size of what is about to die");

        var stored = await ReadAsync(visit);
        stored.Status.Should().Be(AppointmentStatus.Confirmed, "a refused write-off leaves the money chaseable");
        stored.HistoryRows.Should().Be(before.HistoryRows,
            "the rejected transition may not even leave its history row behind");
        (await IsListedAsOwedAsync(graph, visit)).Should().BeTrue("and the queue has to keep showing the debt");
    }

    [Fact]
    public async Task Transition_UnpaidVisitToNoShowWithAReason_MustWriteTheBalanceOffAndLeaveTheQueue()
    {
        var graph = await SeedGraphAsync("explained");
        var visit = await SeedVisitAsync(graph);

        var response = await TransitionAsync(
            graph, visit, AppointmentStatus.NoShow, "patient disputed the visit; balance waived by the owner");

        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);

        var stored = await ReadAsync(visit);
        stored.Status.Should().Be(AppointmentStatus.NoShow);
        stored.CancellationReason.Should().BeNull("NoShow is not a cancellation");
        stored.NoShowReason.Should().Be(
            "patient disputed the visit; balance waived by the owner",
            "the history row is the only trace a NoShow write-off leaves, so it has to carry the words");

        (await IsListedAsOwedAsync(graph, visit)).Should().BeFalse(
            "a written-off debt is no longer owed money — which is exactly why the reason was demanded first");
    }

    [Fact]
    public async Task Transition_SettledVisitToNoShowWithoutAReason_MustNotBeGated()
    {
        var graph = await SeedGraphAsync("paid");
        var visit = await SeedVisitAsync(graph, settled: true);

        var response = await TransitionAsync(graph, visit, AppointmentStatus.NoShow, null);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        (await ReadAsync(visit)).Status.Should().Be(AppointmentStatus.NoShow,
            "nothing is stranded, so the gate has no business stopping the routine closure");
    }

    [Fact]
    public async Task Transition_PartiallyPaidVisitToNoShow_MustAskForTheRemainderOnly()
    {
        var graph = await SeedGraphAsync("deposit");
        var visit = await SeedVisitAsync(graph);

        (await ChargeAsync(graph, visit, 40.00m)).StatusCode.Should().Be(HttpStatusCode.OK);

        var response = await TransitionAsync(graph, visit, AppointmentStatus.NoShow, "   ");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity, body);
        ReadRule(body).Should().Be("DebtWriteOffReasonRequired");
        body.Should().Contain("60.00 of 100.00",
            "the gate reads the ledger the charge path writes, not a flag on the appointment");

        var explained = await TransitionAsync(graph, visit, AppointmentStatus.NoShow, "waived after a complaint");
        var explainedBody = await explained.Content.ReadAsStringAsync();

        explained.StatusCode.Should().Be(HttpStatusCode.OK, explainedBody);
    }

    [Fact]
    public async Task Transition_ToCancelledWithoutAReason_MustFailValidationTheWayCancelDoes()
    {
        var graph = await SeedGraphAsync("both-doors");
        var visit = await SeedVisitAsync(graph, settled: true);

        var viaTransition = await TransitionAsync(graph, visit, AppointmentStatus.Cancelled, null);
        var transitionBody = await viaTransition.Content.ReadAsStringAsync();

        viaTransition.StatusCode.Should().Be(HttpStatusCode.BadRequest, transitionBody);
        (await ReadAsync(visit)).Status.Should().Be(AppointmentStatus.Confirmed,
            "a rejected cancel through either door may not move the booking");

        // The settled booking is deliberate: with no balance at stake the only rule left standing between the
        // request and a Cancelled row is the validator, which is the door being tested here.
        var viaCancel = await Desk(graph).PostAsJsonAsync(
            $"/api/v1/appointments/{visit}/cancel",
            new AppointmentsController.CancelAppointmentRequest(""));

        var cancelBody = await viaCancel.Content.ReadAsStringAsync();
        viaCancel.StatusCode.Should().Be(HttpStatusCode.BadRequest, cancelBody);

        var explained = await TransitionAsync(graph, visit, AppointmentStatus.Cancelled, "patient cancelled");
        var explainedBody = await explained.Content.ReadAsStringAsync();

        explained.StatusCode.Should().Be(HttpStatusCode.OK, explainedBody);
        (await ReadAsync(visit)).Status.Should().Be(AppointmentStatus.Cancelled);
    }
}
