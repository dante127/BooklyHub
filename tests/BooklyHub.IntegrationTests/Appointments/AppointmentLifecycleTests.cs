using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BooklyHub.Api.Controllers;
using BooklyHub.Application.Security;
using BooklyHub.Domain.Entities.Appointments;
using BooklyHub.Domain.Entities.Customers;
using BooklyHub.Domain.Entities.Organizations;
using BooklyHub.Domain.Entities.Resources;
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
/// Cancel, transition, and reschedule all change a stored appointment and write a status-history row for
/// it. These tests hold the endpoints over HTTP and require the change to reach the database, because a
/// history row that is only appended to the in-memory collection is silently treated as an update of a row
/// that does not exist.
/// </summary>
public class AppointmentLifecycleTests : IClassFixture<BooklyHubWebApplicationFactory>
{
    private readonly BooklyHubWebApplicationFactory _factory;

    public AppointmentLifecycleTests(BooklyHubWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private sealed record Graph(Guid TenantId, Guid LocationId, Guid ServiceId, Guid StaffId, Guid CustomerId, Guid RoomId);

    private async Task<Graph> SeedGraphAsync(bool withRoom = false)
    {
        var graph = new Graph(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var tenant = new Tenant(graph.TenantId, "Lifecycle Clinic", $"life-{graph.TenantId:N}", "UTC");
        tenant.Settings = new TenantSetting(graph.TenantId)
        {
            MinBookingNoticeMinutes = 10,
            MaxAdvanceBookingDays = 30,
            SlotIntervalMinutes = 30
        };
        db.Tenants.Add(tenant);

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

        var service = new Service
        {
            Id = graph.ServiceId,
            TenantId = graph.TenantId,
            Name = "Consult",
            DurationMinutes = 30,
            Price = 100.00m,
            BufferBeforeMinutes = 0,
            BufferAfterMinutes = 0
        };

        if (withRoom)
        {
            var groupId = Guid.NewGuid();
            service.ResourceRequirements.Add(new ServiceResourceRequirement
            {
                TenantId = graph.TenantId,
                ServiceId = graph.ServiceId,
                ResourceGroupId = groupId,
                QuantityRequired = 1
            });
            db.ResourceGroups.Add(new ResourceGroup { Id = groupId, TenantId = graph.TenantId, Name = "Rooms" });
            db.Resources.Add(new Resource
            {
                Id = graph.RoomId,
                TenantId = graph.TenantId,
                ResourceGroupId = groupId,
                LocationId = graph.LocationId,
                Name = "Room 1"
            });
        }

        db.Services.Add(service);

        var staff = new Staff
        {
            Id = graph.StaffId,
            TenantId = graph.TenantId,
            LocationId = graph.LocationId,
            FirstName = "Doc",
            LastName = "One",
            Email = $"doc-{graph.StaffId:N}@life.test"
        };
        staff.StaffServices.Add(new StaffService { TenantId = graph.TenantId, StaffId = graph.StaffId, ServiceId = graph.ServiceId });

        var bookingDay = DateTime.UtcNow.Date.AddDays(2);
        var workingHour = new WorkingHour
        {
            Id = Guid.NewGuid(),
            TenantId = graph.TenantId,
            StaffId = graph.StaffId,
            LocationId = graph.LocationId,
            DayOfWeek = bookingDay.DayOfWeek,
            IsWorkingDay = true
        };
        workingHour.Intervals.Add(new WorkingHourInterval(new TimeSpan(8, 0, 0), new TimeSpan(18, 0, 0), false));
        staff.WorkingHours.Add(workingHour);
        db.StaffMembers.Add(staff);

        db.Customers.Add(new Customer
        {
            Id = graph.CustomerId,
            TenantId = graph.TenantId,
            FirstName = "Ann",
            LastName = "Patient",
            Email = $"ann-{graph.CustomerId:N}@life.test"
        });

        await db.SaveChangesAsync();
        return graph;
    }

    private async Task<Guid> SeedConfirmedAppointmentAsync(Graph graph, DateTime startAtUtc, bool withRoom = false)
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
        appointment.TransitionTo(AppointmentStatus.Confirmed, "seeded for lifecycle tests");

        if (withRoom)
        {
            appointment.AppointmentResources.Add(new AppointmentResource
            {
                TenantId = graph.TenantId,
                AppointmentId = appointment.Id,
                ResourceId = graph.RoomId
            });
        }

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.Appointments.Add(appointment);
        await db.SaveChangesAsync();
        return appointment.Id;
    }

    private HttpClient ManagerClient(Graph graph) => _factory.CreateClientForTenant(graph.TenantId, Roles.Manager);

    private async Task<Appointment> ReadAppointmentAsync(Guid appointmentId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.Appointments.IgnoreQueryFilters().FirstAsync(a => a.Id == appointmentId);
    }

    private async Task<List<AppointmentStatusHistory>> ReadHistoryAsync(Guid appointmentId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.AppointmentStatusHistories
            .IgnoreQueryFilters()
            .Where(h => h.AppointmentId == appointmentId)
            .ToListAsync();
    }

    [Fact]
    public async Task Cancel_ConfirmedAppointment_MustPersistAndWriteHistory()
    {
        var graph = await SeedGraphAsync();
        var appointmentId = await SeedConfirmedAppointmentAsync(graph, DateTime.UtcNow.Date.AddDays(2).AddHours(9));

        var response = await ManagerClient(graph).PostAsJsonAsync(
            $"/api/v1/appointments/{appointmentId}/cancel",
            new AppointmentsController.CancelAppointmentRequest("patient asked"));

        Assert.True(response.StatusCode == HttpStatusCode.NoContent,
            $"expected 204, got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        var stored = await ReadAppointmentAsync(appointmentId);
        stored.Status.Should().Be(AppointmentStatus.Cancelled);
        stored.CancellationReason.Should().Be("patient asked");

        var history = await ReadHistoryAsync(appointmentId);
        history.Should().Contain(h => h.ToStatus == AppointmentStatus.Cancelled && h.FromStatus == AppointmentStatus.Confirmed,
            "the cancel must be written as a new history row, not lost in change tracking");
    }

    [Fact]
    public async Task Transition_ConfirmedToCheckedIn_MustPersistAndWriteHistory()
    {
        var graph = await SeedGraphAsync();
        // Check-in is temporally gated, so the appointment must be close enough to now to be check-in-able.
        var appointmentId = await SeedConfirmedAppointmentAsync(graph, DateTime.UtcNow.AddMinutes(20));

        var response = await ManagerClient(graph).PostAsJsonAsync(
            $"/api/v1/appointments/{appointmentId}/transition",
            new AppointmentsController.TransitionStatusRequest(AppointmentStatus.CheckedIn, "arrived"));

        Assert.True(response.StatusCode == HttpStatusCode.OK,
            $"expected 200, got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        (await ReadAppointmentAsync(appointmentId)).Status.Should().Be(AppointmentStatus.CheckedIn);

        var history = await ReadHistoryAsync(appointmentId);
        history.Should().Contain(h => h.ToStatus == AppointmentStatus.CheckedIn);
    }

    [Fact]
    public async Task Transition_ConfirmedToCompleted_MustPersistAndWriteHistory()
    {
        var graph = await SeedGraphAsync();
        var appointmentId = await SeedConfirmedAppointmentAsync(graph, DateTime.UtcNow.AddHours(-2));

        var response = await ManagerClient(graph).PostAsJsonAsync(
            $"/api/v1/appointments/{appointmentId}/transition",
            new AppointmentsController.TransitionStatusRequest(AppointmentStatus.Completed, "visit done, no check-in ceremony"));

        Assert.True(response.StatusCode == HttpStatusCode.OK,
            $"expected 200, got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        (await ReadAppointmentAsync(appointmentId)).Status.Should().Be(AppointmentStatus.Completed);

        var history = await ReadHistoryAsync(appointmentId);
        history.Should().Contain(h => h.ToStatus == AppointmentStatus.Completed && h.FromStatus == AppointmentStatus.Confirmed);
    }

    [Fact]
    public async Task Transition_ToRescheduled_MustBeRejected()
    {
        var graph = await SeedGraphAsync();
        var appointmentId = await SeedConfirmedAppointmentAsync(graph, DateTime.UtcNow.Date.AddDays(2).AddHours(9));

        var response = await ManagerClient(graph).PostAsJsonAsync(
            $"/api/v1/appointments/{appointmentId}/transition",
            new AppointmentsController.TransitionStatusRequest(AppointmentStatus.Rescheduled, "moved by hand"));

        Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity,
            $"expected 422, got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        (await ReadAppointmentAsync(appointmentId)).Status.Should().Be(AppointmentStatus.Confirmed,
            "Reschedule() moves the time in place, so nothing may ever set the Rescheduled status");
    }

    [Fact]
    public async Task Transition_CheckInForFutureAppointment_MustBeRejected()
    {
        var graph = await SeedGraphAsync();
        var appointmentId = await SeedConfirmedAppointmentAsync(graph, DateTime.UtcNow.Date.AddDays(2).AddHours(9));

        var response = await ManagerClient(graph).PostAsJsonAsync(
            $"/api/v1/appointments/{appointmentId}/transition",
            new AppointmentsController.TransitionStatusRequest(AppointmentStatus.CheckedIn, "arrived two days early"));

        Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity,
            $"expected 422, got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        (await ReadAppointmentAsync(appointmentId)).Status.Should().Be(AppointmentStatus.Confirmed,
            "a rejected transition must leave the stored status untouched");
    }

    [Fact]
    public async Task RescheduledRow_StillOccupiesItsSlot()
    {
        // A legacy row in the reserved Rescheduled state still holds a real customer and a real time.
        // If availability ignored it, the slot would double-book while the row is still displayed.
        var graph = await SeedGraphAsync();
        var slotStart = DateTime.UtcNow.Date.AddDays(2).AddHours(9);
        var appointmentId = await SeedConfirmedAppointmentAsync(graph, slotStart);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.Database.ExecuteSqlAsync($"UPDATE Appointments SET Status = {(int)AppointmentStatus.Rescheduled} WHERE Id = {appointmentId}");
        }

        var client = _factory.CreateClientForTenant(graph.TenantId, Roles.Staff);
        var response = await client.PostAsJsonAsync("/api/v1/appointments",
            new AppointmentsController.BookAppointmentRequest(
                graph.LocationId, graph.ServiceId, graph.StaffId, graph.CustomerId, slotStart, "overlap attempt"));

        Assert.True(response.StatusCode == HttpStatusCode.Conflict,
            $"expected 409, got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    [Fact]
    public async Task Reschedule_ToAnotherSlot_MustMoveTimeAndWriteHistory()
    {
        var graph = await SeedGraphAsync();
        var day = DateTime.UtcNow.Date.AddDays(2);
        var appointmentId = await SeedConfirmedAppointmentAsync(graph, day.AddHours(9));

        var response = await ManagerClient(graph).PostAsJsonAsync(
            $"/api/v1/appointments/{appointmentId}/reschedule",
            new AppointmentsController.RescheduleAppointmentRequest(day.AddHours(11), "moved by staff"));

        Assert.True(response.StatusCode == HttpStatusCode.OK,
            $"expected 200, got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        var stored = await ReadAppointmentAsync(appointmentId);
        stored.StartAtUtc.Should().Be(day.AddHours(11));
        stored.EndAtUtc.Should().Be(day.AddHours(11).AddMinutes(30));
        stored.Status.Should().Be(AppointmentStatus.Confirmed, "rescheduling moves the time, not the state");

        var history = await ReadHistoryAsync(appointmentId);
        history.Should().Contain(h => h.Reason != null && h.Reason.Contains("Rescheduled"),
            "the reschedule must be written as a new history row, not lost in change tracking");
    }

    [Fact]
    public async Task Reschedule_WithRoomRequirement_MustKeepExactlyTheAllocatedResourceRows()
    {
        var graph = await SeedGraphAsync(withRoom: true);
        var day = DateTime.UtcNow.Date.AddDays(2);
        var appointmentId = await SeedConfirmedAppointmentAsync(graph, day.AddHours(9), withRoom: true);

        var response = await ManagerClient(graph).PostAsJsonAsync(
            $"/api/v1/appointments/{appointmentId}/reschedule",
            new AppointmentsController.RescheduleAppointmentRequest(day.AddHours(11), "moved by staff"));

        Assert.True(response.StatusCode == HttpStatusCode.OK,
            $"expected 200, got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var rows = await db.AppointmentResources
            .IgnoreQueryFilters()
            .Where(ar => ar.AppointmentId == appointmentId)
            .ToListAsync();

        rows.Should().ContainSingle("the room is still allocated at the new time; dropping the row would double-book it");
        rows[0].ResourceId.Should().Be(graph.RoomId);
    }
}
