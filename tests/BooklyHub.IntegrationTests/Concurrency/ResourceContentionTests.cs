using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BooklyHub.Api.Controllers;
using BooklyHub.Application.Appointments.Dtos;
using BooklyHub.Application.Appointments.Commands;
using BooklyHub.Application.Security;
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

namespace BooklyHub.IntegrationTests.Concurrency;

/// <summary>
/// Rooms belong to the location, not to a staff member, so two staff booking at the same time compete for
/// the same resource. These tests hold two HTTP requests at that competition and require the location-wide
/// booking lock to keep the ledger honest.
/// </summary>
public class ResourceContentionTests : IClassFixture<BooklyHubWebApplicationFactory>
{
    private readonly BooklyHubWebApplicationFactory _factory;

    public ResourceContentionTests(BooklyHubWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private sealed record Graph(
        Guid TenantId,
        Guid LocationId,
        Guid ServiceId,
        Guid RoomId,
        Guid[] StaffIds,
        Guid[] CustomerIds);

    private async Task<Graph> SeedAsync(int staffCount, int roomCount)
    {
        var graph = new Graph(
            TenantId: Guid.NewGuid(),
            LocationId: Guid.NewGuid(),
            ServiceId: Guid.NewGuid(),
            RoomId: Guid.NewGuid(),
            StaffIds: Enumerable.Range(0, staffCount).Select(_ => Guid.NewGuid()).ToArray(),
            CustomerIds: Enumerable.Range(0, staffCount).Select(_ => Guid.NewGuid()).ToArray());

        var groupId = Guid.NewGuid();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var tenant = new Tenant(graph.TenantId, "Resource Clinic", $"resource-{graph.TenantId:N}", "UTC");
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

        var service = new Service
        {
            Id = graph.ServiceId,
            TenantId = graph.TenantId,
            Name = "Procedure",
            DurationMinutes = 30,
            Price = 120.00m,
            BufferBeforeMinutes = 0,
            BufferAfterMinutes = 0
        };
        service.ResourceRequirements.Add(new ServiceResourceRequirement
        {
            TenantId = graph.TenantId,
            ServiceId = graph.ServiceId,
            ResourceGroupId = groupId,
            QuantityRequired = 1
        });
        db.Services.Add(service);

        db.ResourceGroups.Add(new ResourceGroup
        {
            Id = groupId,
            TenantId = graph.TenantId,
            Name = "Treatment rooms"
        });

        for (var i = 0; i < roomCount; i++)
        {
            db.Resources.Add(new Resource
            {
                Id = i == 0 ? graph.RoomId : Guid.NewGuid(),
                TenantId = graph.TenantId,
                ResourceGroupId = groupId,
                LocationId = graph.LocationId,
                Name = $"Room {i + 1}"
            });
        }

        for (var i = 0; i < staffCount; i++)
        {
            var staffId = graph.StaffIds[i];

            var staff = new Staff
            {
                Id = staffId,
                TenantId = graph.TenantId,
                LocationId = graph.LocationId,
                FirstName = $"Nurse{i}",
                LastName = "One",
                Email = $"nurse{i}@resource.test"
            };
            staff.StaffServices.Add(new StaffService
            {
                TenantId = graph.TenantId,
                StaffId = staffId,
                ServiceId = graph.ServiceId
            });

            // The roster is keyed by weekday, and a recurring series walks across several of them.
            foreach (var dayOfWeek in Enum.GetValues<DayOfWeek>())
            {
                var workingHour = new WorkingHour
                {
                    Id = Guid.NewGuid(),
                    TenantId = graph.TenantId,
                    StaffId = staffId,
                    LocationId = graph.LocationId,
                    DayOfWeek = dayOfWeek,
                    IsWorkingDay = true
                };
                workingHour.Intervals.Add(new WorkingHourInterval(new TimeSpan(8, 0, 0), new TimeSpan(18, 0, 0), isBreak: false));
                staff.WorkingHours.Add(workingHour);
            }

            db.StaffMembers.Add(staff);

            db.Customers.Add(new Customer
            {
                Id = graph.CustomerIds[i],
                TenantId = graph.TenantId,
                FirstName = $"Patient{i}",
                LastName = "Smith",
                Email = $"patient{i}@resource.test"
            });
        }

        await db.SaveChangesAsync();
        return graph;
    }

    private async Task<HttpStatusCode[]> BookSameTimeForAllAsync(Graph graph, DateTime startAtUtc)
    {
        var attempts = graph.StaffIds.Zip(graph.CustomerIds, (staffId, customerId) => (staffId, customerId))
            .Select(pair =>
            {
                var client = _factory.CreateClientForTenant(graph.TenantId, Roles.Staff);
                var payload = new AppointmentsController.BookAppointmentRequest(
                    LocationId: graph.LocationId,
                    ServiceId: graph.ServiceId,
                    StaffId: pair.staffId,
                    CustomerId: pair.customerId,
                    StartAtUtc: startAtUtc,
                    Notes: "resource contention test");

                return client.PostAsJsonAsync("/api/v1/appointments", payload);
            })
            .ToArray();

        var responses = await Task.WhenAll(attempts);
        return responses.Select(r => r.StatusCode).ToArray();
    }

    [Fact]
    public async Task ConcurrentBookings_ByDifferentStaff_ForOneRoom_MustAllowExactlyOneSuccess()
    {
        var startAtUtc = DateTime.UtcNow.Date.AddDays(2).AddHours(10);
        var graph = await SeedAsync(staffCount: 4, roomCount: 1);

        var results = await BookSameTimeForAllAsync(graph, startAtUtc);

        results.Count(s => s == HttpStatusCode.Created || s == HttpStatusCode.OK)
            .Should().Be(1, $"the location has one room, so only one of {results.Length} staff can be booked into it");
        results.Count(s => s == HttpStatusCode.Conflict)
            .Should().Be(results.Length - 1, "every other attempt must be told the room is taken");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var roomBookings = await db.AppointmentResources
            .IgnoreQueryFilters()
            .Where(ar => ar.TenantId == graph.TenantId && ar.ResourceId == graph.RoomId)
            .ToListAsync();

        roomBookings.Should().HaveCount(1, "a room must never be handed out twice for the same time");
    }

    [Fact]
    public async Task ConcurrentBookings_WithEnoughRoomsForEveryone_MustAllSucceed()
    {
        var startAtUtc = DateTime.UtcNow.Date.AddDays(2).AddHours(11);
        var graph = await SeedAsync(staffCount: 3, roomCount: 3);

        var results = await BookSameTimeForAllAsync(graph, startAtUtc);

        results.Should().OnlyContain(s => s == HttpStatusCode.Created || s == HttpStatusCode.OK,
            "the location lock serialises bookings, it must not turn parallel demand into false conflicts");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var allocated = await db.Appointments
            .IgnoreQueryFilters()
            .Where(a => a.TenantId == graph.TenantId && a.StartAtUtc == startAtUtc)
            .SelectMany(a => a.AppointmentResources.Select(ar => ar.ResourceId))
            .ToListAsync();

        allocated.Should().HaveCount(3, "each appointment holds a room");
        allocated.Distinct().Should().HaveCount(3, "no room may be allocated to two appointments");
    }

    [Fact]
    public async Task RecurringSeries_MustHoldTheRoomsItsOccurrencesWereAllocated()
    {
        var graph = await SeedAsync(staffCount: 2, roomCount: 1);
        var firstStartUtc = DateTime.UtcNow.Date.AddDays(2).AddHours(10);

        var command = new CreateRecurringAppointmentCommand(
            TenantId: graph.TenantId,
            LocationId: graph.LocationId,
            ServiceId: graph.ServiceId,
            StaffId: graph.StaffIds[0],
            CustomerId: graph.CustomerIds[0],
            StartTimeOfDay: TimeOnly.FromDateTime(firstStartUtc),
            Pattern: RecurrencePattern.Daily,
            Interval: 1,
            StartDate: DateOnly.FromDateTime(firstStartUtc),
            EndDate: null,
            MaxOccurrences: 3,
            ConflictPolicy: RecurrenceConflictPolicy.AbortSeries,
            Notes: "recurring resource test");

        var client = _factory.CreateClientForTenant(graph.TenantId, Roles.Staff);
        var response = await client.PostAsJsonAsync("/api/v1/appointments/recurring", command);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // The API writes enums as strings, so the counters are read straight from the JSON document.
        using (var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
        {
            result.RootElement.GetProperty("bookedCount").GetInt32().Should().Be(3);
            result.RootElement.GetProperty("skippedCount").GetInt32().Should().Be(0);
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var holdings = await db.AppointmentResources
                .IgnoreQueryFilters()
                .Where(ar => ar.TenantId == graph.TenantId && ar.ResourceId == graph.RoomId)
                .CountAsync();

            holdings.Should().Be(3, "an occurrence that was given the room has to hold it on the ledger");
        }

        // The series is only real if the next booking sees it: a second staff member is free at that
        // minute, so the room the series holds is what must refuse them.
        var challenger = _factory.CreateClientForTenant(graph.TenantId, Roles.Staff);
        var conflicting = await challenger.PostAsJsonAsync("/api/v1/appointments",
            new AppointmentsController.BookAppointmentRequest(
                LocationId: graph.LocationId,
                ServiceId: graph.ServiceId,
                StaffId: graph.StaffIds[1],
                CustomerId: graph.CustomerIds[1],
                StartAtUtc: firstStartUtc,
                Notes: "must conflict with the series"));

        conflicting.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }
}
