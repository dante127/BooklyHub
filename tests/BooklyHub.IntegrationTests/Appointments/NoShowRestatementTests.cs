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
/// The sweep decides attendance from a timer: a Confirmed row more than six hours past its end with nothing
/// owing becomes an absence, and the clinic never sees that decision. It can be wrong — a patient who was in
/// the chair while nobody recorded the visit is a patient written off as a no-show, and her visit drops out
/// of the completed counts and the revenue the same way. Until SWP-03 the book had no door back: NoShow had
/// no outgoing transition at all, so the money could still move through the refund path while the fact about
/// the visit could not be corrected through any endpoint. These tests bind the one edge that was added,
/// NoShow to Completed, and the two things taken with it: a correction has to say what it was correcting, and
/// it may not make the booking live again.
/// </summary>
public class NoShowRestatementTests : IClassFixture<BooklyHubWebApplicationFactory>
{
    private const decimal Price = 100.00m;

    private readonly BooklyHubWebApplicationFactory _factory;

    public NoShowRestatementTests(BooklyHubWebApplicationFactory factory)
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

        db.Tenants.Add(new Tenant(graph.TenantId, $"Restatement Clinic {slug}", $"restate-{slug}-{graph.TenantId:N}", "UTC"));

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
            Email = $"doc-{graph.StaffId:N}@restate.test"
        });

        db.Customers.Add(new Customer
        {
            Id = graph.CustomerId,
            TenantId = graph.TenantId,
            FirstName = "Ann",
            LastName = "Patient",
            Email = $"ann-{graph.CustomerId:N}@restate.test"
        });

        await db.SaveChangesAsync();
        return graph;
    }

    /// <summary>
    /// A confirmed booking, settled or not, whose visit window closed more than the grace allows — the exact
    /// population the sweep judges. Seeded straight into the table because the booking guard is pinned
    /// elsewhere and the sweep's candidate read looks only at status and time.
    /// </summary>
    private async Task<Guid> SeedVisitAsync(Graph graph, bool settled)
    {
        var endAt = EndAt(8);
        var appointment = Appointment.Create(
            graph.TenantId,
            graph.LocationId,
            graph.ServiceId,
            graph.StaffId,
            graph.CustomerId,
            endAt.AddMinutes(-30),
            endAt,
            30,
            Price,
            "USD");

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

        // Written as Confirmed rather than walked through the machine, so the closure is the first history
        // row the test has to account for.
        await db.Database.ExecuteSqlAsync(
            $"UPDATE Appointments SET Status = {(int)AppointmentStatus.Confirmed} WHERE Id = {appointment.Id}");

        return appointment.Id;
    }

    private async Task<NoShowSweepResult> SweepAsync(Graph graph) =>
        await new AppointmentNoShowBackgroundService(
            _factory.Services.GetRequiredService<IServiceScopeFactory>(),
            _factory.Services.GetRequiredService<ILogger<AppointmentNoShowBackgroundService>>())
            .CloseUnattendedAppointmentsAsync(CancellationToken.None, graph.TenantId);

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

    private sealed record Stored(
        AppointmentStatus Status,
        int HistoryRows,
        AppointmentStatus? LastFrom,
        AppointmentStatus? LastTo,
        string? LastReason,
        string? CancellationReason);

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
                Last = a.StatusHistories.OrderBy(h => h.ChangedAtUtc).Last()
            })
            .FirstAsync();

        return new Stored(
            row.Status, row.Histories, row.Last.FromStatus, row.Last.ToStatus, row.Last.Reason, row.CancellationReason);
    }

    private async Task<JsonElement> DashboardAsync(Graph graph)
    {
        var response = await Desk(graph).GetAsync("/api/v1/reports/dashboard");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return JsonDocument.Parse(body).RootElement;
    }

    /// <summary>
    /// The queue is the debt's only surface, so a correction that brings a waived balance back has to be
    /// visible there. Read as raw JSON because the assertion is membership and amount together.
    /// </summary>
    private async Task<(bool Listed, decimal AmountDue)> QueueRowAsync(Graph graph, Guid appointmentId)
    {
        var response = await Money(graph).GetAsync("/api/v1/payments/outstanding-visits?pageSize=100");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);

        using var document = JsonDocument.Parse(body);
        foreach (var item in document.RootElement.GetProperty("items").EnumerateArray())
        {
            if (item.GetProperty("appointmentId").GetGuid() == appointmentId)
            {
                return (true, item.GetProperty("amountDue").GetDecimal());
            }
        }

        return (false, 0m);
    }

    private static string ReadProperty(string body, string property) =>
        JsonDocument.Parse(body).RootElement.TryGetProperty(property, out var value) ? value.GetString() ?? "" : "";

    [Fact]
    public async Task Restatement_SweptAbsenceThatWasAttended_MustBecomeACompletedVisitAgainWithTheCorrectionOnRecord()
    {
        var graph = await SeedGraphAsync("attended");
        var visit = await SeedVisitAsync(graph, settled: true);

        (await SweepAsync(graph)).Closed.Should().Be(1, "the sweep is what got this one wrong");
        var wrong = await ReadAsync(visit);
        wrong.Status.Should().Be(AppointmentStatus.NoShow);

        (await DashboardAsync(graph)).GetProperty("noShowCount").GetInt32().Should().Be(1);

        var response = await TransitionAsync(
            graph, visit, AppointmentStatus.Completed, "patient was in the chair; the desk never checked her in");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);

        var stored = await ReadAsync(visit);
        stored.Status.Should().Be(AppointmentStatus.Completed);
        stored.LastFrom.Should().Be(AppointmentStatus.NoShow, "the wrong absence has to be the row this one replies to");
        stored.LastReason.Should().Be("patient was in the chair; the desk never checked her in");
        stored.HistoryRows.Should().Be(wrong.HistoryRows + 1,
            "a correction appends, it does not rewrite the closure it is correcting");
        stored.CancellationReason.Should().BeNull("restating an attendance is not a cancellation");

        // The reason the finding was raised: reporting has to be able to forget the absence it invented.
        var after = await DashboardAsync(graph);
        after.GetProperty("noShowCount").GetInt32().Should().Be(0);
        after.GetProperty("completedCount").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Restatement_WithoutAReason_MustBeRefusedAndLeaveTheAbsenceStanding()
    {
        var graph = await SeedGraphAsync("silent");
        var visit = await SeedVisitAsync(graph, settled: true);

        await SweepAsync(graph);
        var wrong = await ReadAsync(visit);

        foreach (var reason in new string?[] { null, "", "   " })
        {
            var response = await TransitionAsync(graph, visit, AppointmentStatus.Completed, reason);
            var body = await response.Content.ReadAsStringAsync();

            response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity, body);
            ReadProperty(body, "rule").Should().Be("NoShowRestatementRequiresReason", body);
        }

        var stored = await ReadAsync(visit);
        stored.Status.Should().Be(AppointmentStatus.NoShow,
            "an unexplained second thought about a closure changes nothing about it");
        stored.HistoryRows.Should().Be(wrong.HistoryRows);
    }

    [Theory]
    [InlineData(AppointmentStatus.Pending)]
    [InlineData(AppointmentStatus.Confirmed)]
    [InlineData(AppointmentStatus.CheckedIn)]
    [InlineData(AppointmentStatus.InProgress)]
    [InlineData(AppointmentStatus.Cancelled)]
    [InlineData(AppointmentStatus.NoShow)]
    [InlineData(AppointmentStatus.Rescheduled)]
    public async Task Restatement_MustNotMakeTheBookingLiveOrMoveItAnywhereElse(AppointmentStatus target)
    {
        var graph = await SeedGraphAsync($"edge-{target}");
        var visit = await SeedVisitAsync(graph, settled: true);

        await SweepAsync(graph);

        var response = await TransitionAsync(graph, visit, target, "the visit did happen");
        var body = await response.Content.ReadAsStringAsync();

        // 422 either way — the machine and the money rules both answer ProblemDetails — but the title is what
        // separates "no such edge" from "not without a reason", and this test is about the edges.
        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity, body);
        ReadProperty(body, "title").Should().Be("Invalid State Transition",
            $"{target} must be refused because no such edge exists, not because a reason was missing");

        (await ReadAsync(visit)).Status.Should().Be(AppointmentStatus.NoShow);
    }

    [Fact]
    public async Task Restatement_OfAWaivedBalance_MustPutTheDebtBackOnTheQueue()
    {
        var graph = await SeedGraphAsync("waived");
        var visit = await SeedVisitAsync(graph, settled: false);

        // The debt stranded on purpose (WRI-01): explained, so it is written off, and a NoShow is invisible to
        // a queue that lists only the statuses the charge door still opens for.
        var waiver = await TransitionAsync(graph, visit, AppointmentStatus.NoShow, "waived after a dispute");
        var waiverBody = await waiver.Content.ReadAsStringAsync();

        waiver.StatusCode.Should().Be(HttpStatusCode.OK, waiverBody);
        (await QueueRowAsync(graph, visit)).Listed.Should().BeFalse();

        // The absence turns out to be wrong. Completed is a status the queue lists again, so the balance the
        // waiver hid comes back into sight — which is the deliberate consequence of taking the edge: the
        // waiver was granted because the visit never happened, and it did.
        var restated = await TransitionAsync(graph, visit, AppointmentStatus.Completed, "the visit did happen after all");
        var restatedBody = await restated.Content.ReadAsStringAsync();

        restated.StatusCode.Should().Be(HttpStatusCode.OK, restatedBody);

        var (listed, amountDue) = await QueueRowAsync(graph, visit);
        listed.Should().BeTrue("a corrected absence is owed its money question again");
        amountDue.Should().Be(Price);

        // And it is collectable, not merely listed: the charge door is open on the status the row is now in.
        var charge = await Money(graph).PostAsJsonAsync(
            "/api/v1/payments/charge",
            new PaymentsController.ProcessPaymentApiRequest(visit, Price, "USD"));

        var chargeBody = await charge.Content.ReadAsStringAsync();
        charge.StatusCode.Should().Be(HttpStatusCode.OK, chargeBody);
    }
}
