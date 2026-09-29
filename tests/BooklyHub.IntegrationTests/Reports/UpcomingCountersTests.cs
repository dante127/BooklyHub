using System.Globalization;
using System.Net;
using System.Text.Json;
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

namespace BooklyHub.IntegrationTests.Reports;

/// <summary>
/// The dashboard used to report only what had already happened: its period ends at now, so every booking
/// still to come was invisible, and a row left in Confirmed after its time passed looked exactly like an
/// upcoming one. These tests hold the new counters against a clock pinned away from the machine, because a
/// counter that reads the machine's clock would answer the same whatever the application thinks the time is.
/// </summary>
public class UpcomingCountersTests : IClassFixture<BooklyHubWebApplicationFactory>
{
    private readonly BooklyHubWebApplicationFactory _factory;

    public UpcomingCountersTests(BooklyHubWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private sealed record Graph(Guid TenantId, Guid LocationId, Guid ServiceId, Guid StaffId, Guid CustomerId);

    private async Task<Graph> SeedGraphAsync(string slug)
    {
        var graph = new Graph(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        db.Tenants.Add(new Tenant(graph.TenantId, $"Upcoming {slug}", $"upcoming-{slug}-{graph.TenantId:N}", "UTC"));

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
            FirstName = "Rana",
            LastName = "Doc",
            Email = $"rana-{graph.StaffId:N}@upcoming.test"
        };
        staff.StaffServices.Add(new StaffService { TenantId = graph.TenantId, StaffId = graph.StaffId, ServiceId = graph.ServiceId });
        db.StaffMembers.Add(staff);

        db.Customers.Add(new Customer
        {
            Id = graph.CustomerId,
            TenantId = graph.TenantId,
            FirstName = "Sam",
            LastName = "Patient",
            Email = $"sam-{graph.CustomerId:N}@upcoming.test"
        });

        await db.SaveChangesAsync();
        return graph;
    }

    // The status is written after the insert because three of the four counters cover rows whose visit has
    // not happened yet in a state the state machine will not put them in on purpose.
    private async Task SeedAppointmentAsync(Graph graph, DateTime startAtUtc, AppointmentStatus status)
    {
        var appointment = Appointment.Create(
            graph.TenantId, graph.LocationId, graph.ServiceId, graph.StaffId, graph.CustomerId,
            startAtUtc, startAtUtc.AddMinutes(30), 30, 100.00m, "USD");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.Appointments.Add(appointment);
        await db.SaveChangesAsync();

        if (status != AppointmentStatus.Pending)
        {
            await db.Database.ExecuteSqlAsync(
                $"UPDATE Appointments SET Status = {(int)status} WHERE Id = {appointment.Id}");
        }
    }

    private async Task<JsonElement> DashboardAsync(Graph graph, string? queryString = null, string role = Roles.Manager)
    {
        var client = _factory.CreateClientForTenant(graph.TenantId, role);
        var response = await client.GetAsync($"/api/v1/reports/dashboard{queryString}");
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.StatusCode == HttpStatusCode.OK,
            $"expected 200, got {(int)response.StatusCode}: {body}");

        return JsonDocument.Parse(body).RootElement;
    }

    private static DateTime? ReadNullableUtc(JsonElement root, string property)
    {
        var value = root.GetProperty(property);
        return value.ValueKind == JsonValueKind.Null
            ? null
            : DateTime.Parse(value.GetString()!, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
    }

    private static DateTime Hours(int hours) => DateTime.UtcNow.AddHours(hours);

    [Fact]
    public async Task UpcomingCounters_MustBeZeroAndNullForATenantWithNoBookings()
    {
        var graph = await SeedGraphAsync("empty");

        var root = await DashboardAsync(graph);

        root.GetProperty("upcomingCount").GetInt32().Should().Be(0);
        ReadNullableUtc(root, "nextAppointmentAtUtc").Should().BeNull(
            "an empty tenant has no next visit; the aggregate must not fall over looking for one");
    }

    [Fact]
    public async Task UpcomingCount_MustExcludeClosedStatusesAndSplitPendingFromConfirmed()
    {
        var graph = await SeedGraphAsync("statuses");
        foreach (var status in new[]
                 {
                     AppointmentStatus.Pending,
                     AppointmentStatus.Confirmed,
                     AppointmentStatus.CheckedIn,
                     AppointmentStatus.InProgress,
                     AppointmentStatus.Completed,
                     AppointmentStatus.Cancelled,
                     AppointmentStatus.NoShow
                 })
        {
            await SeedAppointmentAsync(graph, Hours(6), status);
        }

        var root = await DashboardAsync(graph);

        // The two execution statuses are still counted: a row checked in ahead of its slot is a visit that
        // has not happened yet, and it is the same set that decides whether a slot is occupied.
        root.GetProperty("upcomingCount").GetInt32().Should().Be(4);
        root.GetProperty("upcomingPendingCount").GetInt32().Should().Be(1);
        root.GetProperty("upcomingConfirmedCount").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task UpcomingCount_MustIgnoreBookingsThatAlreadyStarted()
    {
        var graph = await SeedGraphAsync("started");
        await SeedAppointmentAsync(graph, Hours(-8), AppointmentStatus.Confirmed);
        await SeedAppointmentAsync(graph, Hours(-1), AppointmentStatus.Confirmed);
        await SeedAppointmentAsync(graph, Hours(3), AppointmentStatus.Confirmed);

        var root = await DashboardAsync(graph);

        root.GetProperty("upcomingCount").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task UpcomingCount_MustNotReachIntoAnotherTenant()
    {
        var graphA = await SeedGraphAsync("tenant-a");
        var graphB = await SeedGraphAsync("tenant-b");

        await SeedAppointmentAsync(graphB, Hours(4), AppointmentStatus.Confirmed);
        await SeedAppointmentAsync(graphB, Hours(9), AppointmentStatus.Pending);

        var root = await DashboardAsync(graphA);

        root.GetProperty("upcomingCount").GetInt32().Should().Be(0);
        ReadNullableUtc(root, "nextAppointmentAtUtc").Should().BeNull();
    }

    [Fact]
    public async Task UpcomingCount_MustNotReachIntoAnotherTenantEvenForAPlatformAdmin()
    {
        var graphA = await SeedGraphAsync("admin-a");
        var graphB = await SeedGraphAsync("admin-b");

        await SeedAppointmentAsync(graphA, Hours(3), AppointmentStatus.Confirmed);
        await SeedAppointmentAsync(graphB, Hours(1), AppointmentStatus.Confirmed);
        await SeedAppointmentAsync(graphB, Hours(2), AppointmentStatus.Confirmed);

        // A platform admin targeting a tenant resolves the context with the query filter open, so the
        // predicate written into the report is the only thing left separating the two books. Tenant B's
        // bookings are deliberately the earlier ones, so a leak shows up as the next visit, not just a count.
        var root = await DashboardAsync(graphA, null, Roles.PlatformAdmin);

        root.GetProperty("upcomingCount").GetInt32().Should().Be(1);
        ReadNullableUtc(root, "nextAppointmentAtUtc").Should().BeCloseTo(Hours(3), TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task NextAppointmentAtUtc_MustBeTheEarliestLiveStartAcrossStatuses()
    {
        var graph = await SeedGraphAsync("earliest");
        await SeedAppointmentAsync(graph, Hours(1), AppointmentStatus.Completed);
        await SeedAppointmentAsync(graph, Hours(6), AppointmentStatus.Confirmed);
        await SeedAppointmentAsync(graph, Hours(2), AppointmentStatus.Pending);

        var root = await DashboardAsync(graph);

        // The closed row at +1h is earlier than the live ones, and the Pending row is not the first group the
        // aggregate returns, so this answer can only come from taking the minimum across every status.
        ReadNullableUtc(root, "nextAppointmentAtUtc").Should().BeCloseTo(Hours(2), TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task UpcomingCounters_MustStayAnchoredToTheClockWhenTheAskedPeriodClosedInThePast()
    {
        var graph = await SeedGraphAsync("period");
        await SeedAppointmentAsync(graph, Hours(5), AppointmentStatus.Confirmed);
        await SeedAppointmentAsync(graph, Hours(30), AppointmentStatus.Pending);

        var from = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2020, 2, 1, 0, 0, 0, DateTimeKind.Utc);

        var root = await DashboardAsync(graph, $"?fromUtc={from:O}&toUtc={to:O}");

        root.GetProperty("totalAppointments").GetInt32().Should().Be(0, "the asked period really is empty");
        root.GetProperty("upcomingCount").GetInt32().Should().Be(2,
            "both bookings are still to come whatever period the caller asked history about");
        ReadNullableUtc(root, "toUtc").Should().Be(to, "the period echoed must stay the period asked for");
    }

    [Fact]
    public async Task UpcomingCounters_MustStayAnchoredToTheClockWhenTheAskedPeriodClosesInTheFuture()
    {
        var graph = await SeedGraphAsync("future-period");
        await SeedAppointmentAsync(graph, Hours(5), AppointmentStatus.Confirmed);

        var from = Hours(-720);
        var to = Hours(720);

        var root = await DashboardAsync(graph, $"?fromUtc={from:O}&toUtc={to:O}");

        // The booking is inside the asked period, so both readings agree the period holds one appointment.
        // They only agree on "upcoming" if the counter is anchored on the clock: anchored on the period's
        // close, a booking 5 hours away is behind a horizon 720 hours out and the dashboard reports nothing
        // owed, which is the wrong answer at exactly the moment a receptionist reads it.
        root.GetProperty("totalAppointments").GetInt32().Should().Be(1);
        root.GetProperty("upcomingCount").GetInt32().Should().Be(1);
        ReadNullableUtc(root, "nextAppointmentAtUtc").Should().NotBeNull();
    }

    [Fact]
    public async Task UpcomingCount_MustTreatALegacyRescheduledRowAsStillOwedAVisit()
    {
        var graph = await SeedGraphAsync("legacy");
        await SeedAppointmentAsync(graph, Hours(6), AppointmentStatus.Rescheduled);

        var root = await DashboardAsync(graph);

        // BL-06 made Rescheduled unreachable for new rows but did not delete the old ones, so the rule for
        // "is this visit still owed" has to be an exclusion of the closing statuses rather than a list of the
        // two booking statuses the UI knows about.
        root.GetProperty("upcomingCount").GetInt32().Should().Be(1);
        ReadNullableUtc(root, "nextAppointmentAtUtc").Should().NotBeNull();
    }

    [Fact]
    public async Task UpcomingCount_MustNotSlideForwardWithAWindowThatClosesInTheFuture()
    {
        var graph = await SeedGraphAsync("wide-window");
        await SeedAppointmentAsync(graph, Hours(4), AppointmentStatus.Confirmed);
        await SeedAppointmentAsync(graph, Hours(26), AppointmentStatus.Pending);

        var from = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = DateTime.UtcNow.AddDays(720);

        var root = await DashboardAsync(graph, $"?fromUtc={from:O}&toUtc={to:O}");

        // The asked period swallows both bookings and then some, so answering "what is upcoming" from its
        // closing bound would report nothing owed for the next two years. The counters belong to the clock.
        root.GetProperty("upcomingCount").GetInt32().Should().Be(2);
        root.GetProperty("upcomingConfirmedCount").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task UpcomingCount_MustEmptyItselfWhenTheApplicationClockPassesTheBookings()
    {
        var graph = await SeedGraphAsync("pinned");
        await SeedAppointmentAsync(graph, Hours(48), AppointmentStatus.Confirmed);
        await SeedAppointmentAsync(graph, Hours(60), AppointmentStatus.Pending);

        try
        {
            _factory.Clock.AdvanceBy(TimeSpan.FromDays(10));
            var root = await DashboardAsync(graph);

            root.GetProperty("upcomingCount").GetInt32().Should().Be(0,
                "ten days on the application clock makes both bookings history");
            ReadNullableUtc(root, "nextAppointmentAtUtc").Should().BeNull();
        }
        finally
        {
            _factory.Clock.Release();
        }
    }
}
