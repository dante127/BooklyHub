using System.Net;
using System.Net.Http.Json;
using BooklyHub.Api.Controllers;
using BooklyHub.Application.Security;
using BooklyHub.Domain.Entities.Appointments;
using BooklyHub.Domain.Entities.Customers;
using BooklyHub.Domain.Entities.Organizations;
using BooklyHub.Domain.Entities.Scheduling;
using BooklyHub.Domain.Entities.Services;
using BooklyHub.Domain.Entities.StaffMembers;
using BooklyHub.Domain.Entities.Tenancy;
using BooklyHub.Infrastructure.Data;
using BooklyHub.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BooklyHub.IntegrationTests.Appointments;

/// <summary>
/// "Bookings must start in the future" is decided twice: by the request validators on a hardcoded
/// five-minute floor, and by the tenant's MinBookingNoticeMinutes inside the handler. Only the handler read
/// the application clock, so a request could be judged against two different "nows" inside one call. These
/// tests hold the endpoint over HTTP with the clock pinned away from the machine's time, which is the only
/// way to show the container builds the validators with the clock and uses it.
/// </summary>
public class FutureStartFloorTests : IClassFixture<BooklyHubWebApplicationFactory>
{
    private readonly BooklyHubWebApplicationFactory _factory;

    public FutureStartFloorTests(BooklyHubWebApplicationFactory factory)
    {
        _factory = factory;
    }

    // Tomorrow at 11:00 is more than a day away whatever the machine clock says, and always inside the
    // seeded working hours, so the only variable left is where the application clock is pinned.
    private static DateTime OrdinarySlot => DateTime.UtcNow.Date.AddDays(1).AddHours(11);

    private sealed record Graph(Guid TenantId, Guid LocationId, Guid ServiceId, Guid StaffId, Guid CustomerId);

    private async Task<Graph> SeedGraphAsync()
    {
        var graph = new Graph(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var tenant = new Tenant(graph.TenantId, "Floor Clinic", $"floor-{graph.TenantId:N}", "UTC");
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
            FirstName = "Ivy",
            LastName = "Doc",
            Email = $"ivy-{graph.StaffId:N}@floor.test"
        };
        staff.StaffServices.Add(new StaffService { TenantId = graph.TenantId, StaffId = graph.StaffId, ServiceId = graph.ServiceId });

        var workingHour = new WorkingHour
        {
            Id = Guid.NewGuid(),
            TenantId = graph.TenantId,
            StaffId = graph.StaffId,
            LocationId = graph.LocationId,
            DayOfWeek = OrdinarySlot.DayOfWeek,
            IsWorkingDay = true
        };
        workingHour.Intervals.Add(new WorkingHourInterval(new TimeSpan(8, 0, 0), new TimeSpan(18, 0, 0), false));
        staff.WorkingHours.Add(workingHour);
        db.StaffMembers.Add(staff);

        db.Customers.Add(new Customer
        {
            Id = graph.CustomerId,
            TenantId = graph.TenantId,
            FirstName = "Cy",
            LastName = "Patient",
            Email = $"cy-{graph.CustomerId:N}@floor.test"
        });

        await db.SaveChangesAsync();
        return graph;
    }

    private Task<HttpResponseMessage> BookAsync(Graph graph, DateTime startAtUtc) =>
        _factory.CreateClientForTenant(graph.TenantId, Roles.Receptionist).PostAsJsonAsync(
            "/api/v1/appointments",
            new AppointmentsController.BookAppointmentRequest(
                graph.LocationId, graph.ServiceId, graph.StaffId, graph.CustomerId, startAtUtc, "booked by test"));

    [Fact]
    public async Task Booking_BehindThePinnedClock_MustBeRefusedByTheValidatorOnTheWire()
    {
        var graph = await SeedGraphAsync();

        try
        {
            // The slot is half an hour ahead of the machine, so a validator reading the machine lets it
            // through and the handler answers with a 422 MinimumNoticeViolation instead. Two hours on the
            // application clock put the same slot in the past.
            _factory.Clock.AdvanceBy(TimeSpan.FromHours(2));

            var response = await BookAsync(graph, DateTime.UtcNow.AddMinutes(30));
            var body = await response.Content.ReadAsStringAsync();

            Assert.True(response.StatusCode == HttpStatusCode.BadRequest,
                $"expected 400 from the validation floor, got {(int)response.StatusCode}: {body}");
            body.Should().Contain("Appointment start time must be in the future.");
        }
        finally
        {
            _factory.Clock.Release();
        }
    }

    [Fact]
    public async Task Booking_AheadOfThePinnedClock_MustStillBeCreated()
    {
        var graph = await SeedGraphAsync();

        try
        {
            _factory.Clock.AdvanceBy(TimeSpan.FromHours(2));

            var response = await BookAsync(graph, OrdinarySlot);
            var body = await response.Content.ReadAsStringAsync();

            Assert.True(response.StatusCode == HttpStatusCode.Created,
                $"expected 201, got {(int)response.StatusCode}: {body}");
        }
        finally
        {
            _factory.Clock.Release();
        }
    }

    [Fact]
    public async Task Reschedule_BehindThePinnedClock_MustBeRefusedByTheValidatorOnTheWire()
    {
        var graph = await SeedGraphAsync();

        Guid appointmentId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var appointment = Appointment.Create(
                graph.TenantId, graph.LocationId, graph.ServiceId, graph.StaffId, graph.CustomerId,
                OrdinarySlot, OrdinarySlot.AddMinutes(30), 30, 100.00m, "USD");
            db.Appointments.Add(appointment);
            await db.SaveChangesAsync();
            appointmentId = appointment.Id;
        }

        try
        {
            _factory.Clock.AdvanceBy(TimeSpan.FromHours(4));

            var response = await _factory.CreateClientForTenant(graph.TenantId, Roles.Receptionist).PostAsJsonAsync(
                $"/api/v1/appointments/{appointmentId}/reschedule",
                new AppointmentsController.RescheduleAppointmentRequest(
                    DateTime.UtcNow.AddMinutes(90), "moved by test"));
            var body = await response.Content.ReadAsStringAsync();

            // 90 minutes ahead of the machine, 150 behind the application clock. The validator runs before
            // the handler, so this answer can only come from the floor, not from the tenant's cutoffs.
            Assert.True(response.StatusCode == HttpStatusCode.BadRequest,
                $"expected 400 from the validation floor, got {(int)response.StatusCode}: {body}");
            body.Should().Contain("Rescheduled time must be in the future.");
        }
        finally
        {
            _factory.Clock.Release();
        }
    }
}
