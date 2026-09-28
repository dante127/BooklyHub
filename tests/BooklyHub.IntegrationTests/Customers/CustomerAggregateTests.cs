using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BooklyHub.Api.Controllers;
using BooklyHub.Application.Appointments.Commands;
using BooklyHub.Application.Security;
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

namespace BooklyHub.IntegrationTests.Customers;

/// <summary>
/// TotalBookings is a denormalized counter that every booking path must move exactly once: +1 when a
/// booking is created (single or per recurring occurrence), -1 when it is cancelled (either cancel
/// endpoint). The counter moves in SQL because two bookings for one customer at two locations hold no
/// common lock, so a read-modify-write would silently drop one of them (V16).
/// </summary>
public class CustomerAggregateTests : IClassFixture<BooklyHubWebApplicationFactory>
{
    private readonly BooklyHubWebApplicationFactory _factory;

    public CustomerAggregateTests(BooklyHubWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private sealed record Graph(
        Guid TenantId,
        Guid Location1Id,
        Guid Location2Id,
        Guid ServiceId,
        Guid Staff1Id,
        Guid Staff2Id,
        Guid CustomerId);

    private async Task<Graph> SeedGraphAsync(DateTime bookingDay)
    {
        var graph = new Graph(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var tenant = new Tenant(graph.TenantId, "Aggregate Clinic", $"agg-{graph.TenantId:N}", "UTC");
        tenant.Settings = new TenantSetting(graph.TenantId)
        {
            MinBookingNoticeMinutes = 10,
            MaxAdvanceBookingDays = 60,
            SlotIntervalMinutes = 30
        };
        db.Tenants.Add(tenant);

        foreach (var (locationId, name) in new[] { (graph.Location1Id, "North"), (graph.Location2Id, "South") })
        {
            db.Locations.Add(new Location
            {
                Id = locationId,
                TenantId = graph.TenantId,
                Name = name,
                Address = "1 Main St",
                City = "Springfield",
                Country = "US",
                TimeZoneId = "UTC"
            });
        }

        db.Services.Add(new Service
        {
            Id = graph.ServiceId,
            TenantId = graph.TenantId,
            Name = "Consult",
            DurationMinutes = 30,
            Price = 100.00m
        });

        foreach (var (staffId, locationId, first) in new[]
        {
            (graph.Staff1Id, graph.Location1Id, "Ava"),
            (graph.Staff2Id, graph.Location2Id, "Ben")
        })
        {
            var staff = new Staff
            {
                Id = staffId,
                TenantId = graph.TenantId,
                LocationId = locationId,
                FirstName = first,
                LastName = "Doc",
                Email = $"{first}-{staffId:N}@agg.test"
            };
            staff.StaffServices.Add(new StaffService { TenantId = graph.TenantId, StaffId = staffId, ServiceId = graph.ServiceId });

            var workingHour = new WorkingHour
            {
                Id = Guid.NewGuid(),
                TenantId = graph.TenantId,
                StaffId = staffId,
                LocationId = locationId,
                DayOfWeek = bookingDay.DayOfWeek,
                IsWorkingDay = true
            };
            workingHour.Intervals.Add(new WorkingHourInterval(new TimeSpan(8, 0, 0), new TimeSpan(18, 0, 0), false));
            staff.WorkingHours.Add(workingHour);

            db.StaffMembers.Add(staff);
        }

        db.Customers.Add(new Customer
        {
            Id = graph.CustomerId,
            TenantId = graph.TenantId,
            FirstName = "Cam",
            LastName = "Patient",
            Email = $"cam-{graph.CustomerId:N}@agg.test"
        });

        await db.SaveChangesAsync();
        return graph;
    }

    private HttpClient StaffClient(Graph graph) => _factory.CreateClientForTenant(graph.TenantId, Roles.Staff);

    private async Task<HttpResponseMessage> BookAsync(Graph graph, Guid locationId, Guid staffId, DateTime startAtUtc) =>
        await StaffClient(graph).PostAsJsonAsync("/api/v1/appointments",
            new AppointmentsController.BookAppointmentRequest(
                locationId, graph.ServiceId, staffId, graph.CustomerId, startAtUtc, "aggregate test"));

    private async Task<int> ReadTotalBookingsAsync(Graph graph)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.Customers
            .IgnoreQueryFilters()
            .Where(c => c.Id == graph.CustomerId)
            .Select(c => c.TotalBookings)
            .SingleAsync();
    }

    private static async Task<Guid> ReadAppointmentIdAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task Booking_IncrementsTotalBookings()
    {
        var graph = await SeedGraphAsync(DateTime.UtcNow.Date.AddDays(2));

        var response = await BookAsync(graph, graph.Location1Id, graph.Staff1Id, DateTime.UtcNow.Date.AddDays(2).AddHours(9));

        Assert.True(response.StatusCode == HttpStatusCode.Created,
            $"expected 201, got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        (await ReadTotalBookingsAsync(graph)).Should().Be(1);
    }

    [Fact]
    public async Task Cancel_DecrementsTotalBookings()
    {
        var graph = await SeedGraphAsync(DateTime.UtcNow.Date.AddDays(2));
        var booked = await BookAsync(graph, graph.Location1Id, graph.Staff1Id, DateTime.UtcNow.Date.AddDays(2).AddHours(9));
        var appointmentId = await ReadAppointmentIdAsync(booked);

        var response = await _factory.CreateClientForTenant(graph.TenantId, Roles.Manager).PostAsJsonAsync(
            $"/api/v1/appointments/{appointmentId}/cancel",
            new AppointmentsController.CancelAppointmentRequest("no longer needed"));

        Assert.True(response.StatusCode == HttpStatusCode.NoContent,
            $"expected 204, got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        (await ReadTotalBookingsAsync(graph)).Should().Be(0, "the booking was counted when created; cancelling gives the count back");
    }

    [Fact]
    public async Task TransitionToCancelled_DecrementsTotalBookings()
    {
        var graph = await SeedGraphAsync(DateTime.UtcNow.Date.AddDays(2));
        var booked = await BookAsync(graph, graph.Location1Id, graph.Staff1Id, DateTime.UtcNow.Date.AddDays(2).AddHours(9));
        var appointmentId = await ReadAppointmentIdAsync(booked);

        var response = await _factory.CreateClientForTenant(graph.TenantId, Roles.Manager).PostAsJsonAsync(
            $"/api/v1/appointments/{appointmentId}/transition",
            new AppointmentsController.TransitionStatusRequest(AppointmentStatus.Cancelled, "cancelled via transition"));

        Assert.True(response.StatusCode == HttpStatusCode.OK,
            $"expected 200, got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        (await ReadTotalBookingsAsync(graph)).Should().Be(0, "a cancel is a cancel on every path, or the counter drifts by path choice");
    }

    [Fact]
    public async Task RecurringBooking_IncrementsByCreatedOccurrences()
    {
        var bookingDay = DateTime.UtcNow.Date.AddDays(2);
        var graph = await SeedGraphAsync(bookingDay);

        var response = await StaffClient(graph).PostAsJsonAsync("/api/v1/appointments/recurring",
            new CreateRecurringAppointmentCommand(
                graph.TenantId,
                graph.Location1Id,
                graph.ServiceId,
                graph.Staff1Id,
                graph.CustomerId,
                new TimeOnly(9, 0),
                RecurrencePattern.Weekly,
                1,
                DateOnly.FromDateTime(bookingDay),
                EndDate: null,
                MaxOccurrences: 3,
                RecurrenceConflictPolicy.AbortSeries,
                "weekly check-up"));

        Assert.True(response.StatusCode == HttpStatusCode.OK,
            $"expected 200, got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("bookedCount").GetInt32().Should().Be(3);

        (await ReadTotalBookingsAsync(graph)).Should().Be(3, "every created occurrence is a booking");
    }

    [Fact]
    public async Task ConcurrentBookings_SameCustomerTwoLocations_MustCountBoth()
    {
        var bookingDay = DateTime.UtcNow.Date.AddDays(2);
        var graph = await SeedGraphAsync(bookingDay);
        var slot = bookingDay.AddHours(9);

        // Two locations and two staff: no shared booking lock, exactly the interleaving that made the
        // read-modify-write drop one of the two increments (V16).
        var results = await Task.WhenAll(
            BookAsync(graph, graph.Location1Id, graph.Staff1Id, slot),
            BookAsync(graph, graph.Location2Id, graph.Staff2Id, slot));

        foreach (var response in results)
        {
            Assert.True(response.StatusCode == HttpStatusCode.Created,
                $"expected 201, got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        }

        (await ReadTotalBookingsAsync(graph)).Should().Be(2, "two committed bookings must move the counter twice");
    }
}
