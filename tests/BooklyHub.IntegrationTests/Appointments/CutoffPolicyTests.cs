using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BooklyHub.Api.Controllers;
using BooklyHub.Application.Security;
using BooklyHub.Domain.Entities.Appointments;
using BooklyHub.Domain.Entities.Customers;
using BooklyHub.Domain.Entities.Organizations;
using BooklyHub.Domain.Entities.Scheduling;
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
/// A tenant's cancellation cutoff was only enforced on /cancel. The same actor holding appointments.update
/// could reach the same Cancelled status through /transition without the rule ever being asked, and the
/// reschedule cutoff was applied to everyone including the roles the cancellation rule lets override. Both
/// verbs now answer the same policy object, and both cancel paths go through it (BL-09).
/// </summary>
public class CutoffPolicyTests : IClassFixture<BooklyHubWebApplicationFactory>
{
    private readonly BooklyHubWebApplicationFactory _factory;

    public CutoffPolicyTests(BooklyHubWebApplicationFactory factory)
    {
        _factory = factory;
    }

    // Tomorrow at 09:00 UTC is at most ~33 hours away and at least ~10, so a 48 hour cutoff always covers
    // it no matter when the suite runs.
    private static DateTime CutoffBoundStart => DateTime.UtcNow.Date.AddDays(1).AddHours(9);
    private static DateTime FreshSlot => DateTime.UtcNow.Date.AddDays(1).AddHours(11);

    private sealed record Graph(Guid TenantId, Guid LocationId, Guid ServiceId, Guid StaffId, Guid CustomerId);

    private async Task<Graph> SeedGraphAsync()
    {
        var graph = new Graph(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var tenant = new Tenant(graph.TenantId, "Cutoff Clinic", $"cutoff-{graph.TenantId:N}", "UTC");
        tenant.Settings = new TenantSetting(graph.TenantId)
        {
            MinBookingNoticeMinutes = 10,
            MaxAdvanceBookingDays = 30,
            SlotIntervalMinutes = 30,
            CancellationCutoffHours = 48,
            ReschedulingCutoffHours = 48
        };
        db.Tenants.Add(tenant);

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
            FirstName = "Ava",
            LastName = "Doc",
            Email = $"ava-{graph.StaffId:N}@cutoff.test"
        };
        staff.StaffServices.Add(new StaffService { TenantId = graph.TenantId, StaffId = graph.StaffId, ServiceId = graph.ServiceId });

        var workingHour = new WorkingHour
        {
            Id = Guid.NewGuid(),
            TenantId = graph.TenantId,
            StaffId = graph.StaffId,
            LocationId = graph.LocationId,
            DayOfWeek = CutoffBoundStart.DayOfWeek,
            IsWorkingDay = true
        };
        workingHour.Intervals.Add(new WorkingHourInterval(new TimeSpan(8, 0, 0), new TimeSpan(18, 0, 0), false));
        staff.WorkingHours.Add(workingHour);
        db.StaffMembers.Add(staff);

        db.Customers.Add(new Customer
        {
            Id = graph.CustomerId,
            TenantId = graph.TenantId,
            FirstName = "Cam",
            LastName = "Patient",
            Email = $"cam-{graph.CustomerId:N}@cutoff.test"
        });

        await db.SaveChangesAsync();
        return graph;
    }

    private async Task<Guid> SeedAppointmentAsync(Graph graph, DateTime startAtUtc)
    {
        var appointment = Appointment.Create(
            graph.TenantId,
            graph.LocationId,
            graph.ServiceId,
            graph.StaffId,
            graph.CustomerId,
            startAtUtc,
            startAtUtc.AddMinutes(30),
            30,
            100.00m,
            "USD");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.Appointments.Add(appointment);
        await db.SaveChangesAsync();

        return appointment.Id;
    }

    private HttpClient Client(Graph graph, string role) => _factory.CreateClientForTenant(graph.TenantId, role);

    private Task<HttpResponseMessage> CancelAsync(Graph graph, Guid appointmentId, string role) =>
        Client(graph, role).PostAsJsonAsync(
            $"/api/v1/appointments/{appointmentId}/cancel",
            new AppointmentsController.CancelAppointmentRequest("patient asked"));

    private Task<HttpResponseMessage> TransitionAsync(Graph graph, Guid appointmentId, AppointmentStatus status, string role) =>
        Client(graph, role).PostAsJsonAsync(
            $"/api/v1/appointments/{appointmentId}/transition",
            new AppointmentsController.TransitionStatusRequest(status, $"moved to {status} by {role}"));

    private Task<HttpResponseMessage> RescheduleAsync(Graph graph, Guid appointmentId, DateTime newStart, string role) =>
        Client(graph, role).PostAsJsonAsync(
            $"/api/v1/appointments/{appointmentId}/reschedule",
            new AppointmentsController.RescheduleAppointmentRequest(newStart, "moved by staff"));

    private static async Task<string> BodyAsync(HttpResponseMessage response) => await response.Content.ReadAsStringAsync();

    private static string ReadRule(string body) =>
        JsonDocument.Parse(body).RootElement.GetProperty("rule").GetString()!;

    private sealed record Stored(DateTime StartAtUtc, AppointmentStatus Status, int HistoryRows);

    private async Task<Stored> ReadAsync(Guid appointmentId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var row = await db.Appointments
            .IgnoreQueryFilters()
            .Where(a => a.Id == appointmentId)
            .Select(a => new { a.StartAtUtc, a.Status, Histories = a.StatusHistories.Count })
            .FirstAsync();

        return new Stored(row.StartAtUtc, row.Status, row.Histories);
    }

    [Fact]
    public async Task CancelInsideTheCutoff_ByReceptionist_MustBeRefusedWithTheCancellationRule()
    {
        var graph = await SeedGraphAsync();
        var appointmentId = await SeedAppointmentAsync(graph, CutoffBoundStart);

        var response = await CancelAsync(graph, appointmentId, Roles.Receptionist);
        var body = await BodyAsync(response);

        Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity,
            $"expected 422, got {(int)response.StatusCode}: {body}");
        ReadRule(body).Should().Be("CancellationCutoffExceeded");
        body.Should().Contain("within 48 hours");
    }

    [Fact]
    public async Task TransitionToCancelledInsideTheCutoff_MustBeRefusedExactlyLikeCancel()
    {
        var graph = await SeedGraphAsync();
        var appointmentId = await SeedAppointmentAsync(graph, CutoffBoundStart);

        var response = await TransitionAsync(graph, appointmentId, AppointmentStatus.Cancelled, Roles.Receptionist);
        var body = await BodyAsync(response);

        // Before this rule was shared, this call returned 200: /cancel enforced the cutoff and this
        // endpoint was the way around it for the same actor on the same appointment.
        Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity,
            $"expected 422 from the transition path too, got {(int)response.StatusCode}: {body}");
        ReadRule(body).Should().Be("CancellationCutoffExceeded");
    }

    [Fact]
    public async Task RefusedTransitionToCancelled_MustLeaveTheAppointmentAndItsHistoryUntouched()
    {
        var graph = await SeedGraphAsync();
        var appointmentId = await SeedAppointmentAsync(graph, CutoffBoundStart);
        var before = await ReadAsync(appointmentId);

        var response = await TransitionAsync(graph, appointmentId, AppointmentStatus.Cancelled, Roles.Receptionist);

        Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity,
            $"expected 422, got {(int)response.StatusCode}: {await BodyAsync(response)}");

        var after = await ReadAsync(appointmentId);
        after.Status.Should().Be(before.Status);
        after.HistoryRows.Should().Be(before.HistoryRows, "a rejected cancel must not append a history row on the way out");
    }

    [Fact]
    public async Task LateCancelByManager_MustBeAllowedOnBothCancelPaths()
    {
        var graph = await SeedGraphAsync();
        var viaCancel = await SeedAppointmentAsync(graph, CutoffBoundStart);
        var viaTransition = await SeedAppointmentAsync(graph, CutoffBoundStart.AddDays(1));

        var cancelResponse = await CancelAsync(graph, viaCancel, Roles.Manager);
        var transitionResponse = await TransitionAsync(graph, viaTransition, AppointmentStatus.Cancelled, Roles.Manager);

        Assert.True(cancelResponse.StatusCode == HttpStatusCode.NoContent,
            $"expected 204, got {(int)cancelResponse.StatusCode}: {await BodyAsync(cancelResponse)}");
        Assert.True(transitionResponse.StatusCode == HttpStatusCode.OK,
            $"expected 200, got {(int)transitionResponse.StatusCode}: {await BodyAsync(transitionResponse)}");

        (await ReadAsync(viaCancel)).Status.Should().Be(AppointmentStatus.Cancelled);
        (await ReadAsync(viaTransition)).Status.Should().Be(AppointmentStatus.Cancelled);
    }

    [Fact]
    public async Task RescheduleInsideTheCutoff_ByReceptionist_MustBeRefusedWithTheRescheduleRule()
    {
        var graph = await SeedGraphAsync();
        var appointmentId = await SeedAppointmentAsync(graph, CutoffBoundStart);

        var response = await RescheduleAsync(graph, appointmentId, FreshSlot, Roles.Receptionist);
        var body = await BodyAsync(response);

        Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity,
            $"expected 422, got {(int)response.StatusCode}: {body}");
        ReadRule(body).Should().Be("RescheduleCutoffExceeded");
    }

    [Fact]
    public async Task RescheduleInsideTheCutoff_ByManager_MustBeAllowedLikeCancel()
    {
        var graph = await SeedGraphAsync();
        var appointmentId = await SeedAppointmentAsync(graph, CutoffBoundStart);

        // The cancellation cutoff has always let managers through; the reschedule cutoff refused everyone,
        // so a manager could end a booking but not move it.
        var response = await RescheduleAsync(graph, appointmentId, FreshSlot, Roles.Manager);

        Assert.True(response.StatusCode == HttpStatusCode.OK,
            $"expected 200, got {(int)response.StatusCode}: {await BodyAsync(response)}");
        (await ReadAsync(appointmentId)).StartAtUtc.Should().Be(FreshSlot);
    }

    [Fact]
    public async Task TransitionToConfirmedInsideTheCutoff_MustNotBeGatedByTheCancellationRule()
    {
        var graph = await SeedGraphAsync();
        var appointmentId = await SeedAppointmentAsync(graph, CutoffBoundStart);

        var response = await TransitionAsync(graph, appointmentId, AppointmentStatus.Confirmed, Roles.Receptionist);

        Assert.True(response.StatusCode == HttpStatusCode.OK,
            $"expected 200, got {(int)response.StatusCode}: {await BodyAsync(response)}");
        (await ReadAsync(appointmentId)).Status.Should().Be(AppointmentStatus.Confirmed);
    }
}
