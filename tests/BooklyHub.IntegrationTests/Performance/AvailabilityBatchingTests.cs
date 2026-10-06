using System.Net;
using System.Text.Json;
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

namespace BooklyHub.IntegrationTests.Performance;

/// <summary>
/// The booking guard reads five calendar families before it can say a word about a slot. `PERF-02` found them
/// being read two or three times per candidate staff member, and the fix moved the staff list into a single
/// parameterised read per family. That fix had no test: a future handler that walks the roster and loads each
/// staff member's own shift would answer identically and cost one round trip per staff, which is exactly the
/// shape this route is exposed in — anonymous, and multiplying with the roster (see `PERFORMANCE.md` §6).
/// These facts pin the count, not the answer.
/// </summary>
public class AvailabilityBatchingTests : IClassFixture<BooklyHubWebApplicationFactory>
{
    private static readonly DateTime Now = new(2026, 10, 5, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly QueryDate = new(2026, 10, 5);

    private readonly BooklyHubWebApplicationFactory _factory;

    public AvailabilityBatchingTests(BooklyHubWebApplicationFactory factory) => _factory = factory;

    private sealed record Graph(Guid TenantId, Guid LocationId, Guid ServiceId, Guid CustomerId);

    /// <summary>A bookable day with a known number of candidate staff, none of them pre-booked.</summary>
    private async Task<Graph> SeedGraphAsync(int staffCount)
    {
        var graph = new Graph(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var tenant = new Tenant(graph.TenantId, "Batched Clinic", $"batched-{graph.TenantId:N}", "UTC");
        tenant.Settings = new TenantSetting(graph.TenantId) { MinBookingNoticeMinutes = 60, SlotIntervalMinutes = 30 };
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

        db.Services.Add(new Service
        {
            Id = graph.ServiceId,
            TenantId = graph.TenantId,
            Name = "Consult",
            DurationMinutes = 30,
            Price = 100.00m
        });

        db.Customers.Add(new Customer
        {
            Id = graph.CustomerId,
            TenantId = graph.TenantId,
            FirstName = "Ann",
            LastName = "Patient",
            Email = $"ann-{graph.CustomerId:N}@batched.test"
        });

        // 2026-10-05 is a Monday, and every staff member works the same 08:00-18:00 day, so the roster size is
        // the only variable between the two runs this class compares.
        for (var i = 0; i < staffCount; i++)
        {
            var staffId = Guid.NewGuid();
            var staff = new Staff
            {
                Id = staffId,
                TenantId = graph.TenantId,
                LocationId = graph.LocationId,
                FirstName = "Doc",
                LastName = $"Number{i}",
                Email = $"doc{i}-{staffId:N}@batched.test"
            };
            staff.StaffServices.Add(new StaffService
            {
                TenantId = graph.TenantId,
                StaffId = staffId,
                ServiceId = graph.ServiceId
            });
            var workingHour = new WorkingHour
            {
                Id = Guid.NewGuid(),
                TenantId = graph.TenantId,
                StaffId = staffId,
                LocationId = graph.LocationId,
                DayOfWeek = DayOfWeek.Monday,
                IsWorkingDay = true
            };
            workingHour.Intervals.Add(new WorkingHourInterval(new TimeSpan(8, 0, 0), new TimeSpan(18, 0, 0), false));
            staff.WorkingHours.Add(workingHour);
            db.StaffMembers.Add(staff);
        }

        await db.SaveChangesAsync();
        return graph;
    }

    private sealed record Day(int Queries, int SlotCount, string[] CommandTexts);

    private async Task<Day> ReadDayAsync(Graph graph)
    {
        _factory.Clock.Pin(Now);
        _factory.QueryInterceptor.Reset();
        try
        {
            var client = _factory.CreateClient();
            var response = await client.GetAsync(
                $"/api/v1/availability?tenantId={graph.TenantId}&locationId={graph.LocationId}" +
                $"&serviceId={graph.ServiceId}&date={QueryDate:yyyy-MM-dd}");
            var body = await response.Content.ReadAsStringAsync();

            response.StatusCode.Should().Be(HttpStatusCode.OK, body);
            var slots = JsonDocument.Parse(body).RootElement.GetProperty("slots");

            return new Day(
                _factory.QueryInterceptor.QueryCount,
                slots.EnumerateArray().Count(),
                [.. _factory.QueryInterceptor.ExecutedCommands]);
        }
        finally
        {
            _factory.QueryInterceptor.Reset();
            _factory.Clock.Release();
        }
    }

    /// <summary>How many statements touch a table, counted from the text the code actually sent.</summary>
    private static int ReadsOf(Day day, string table) =>
        day.CommandTexts.Count(text => text.Contains($"[{table}]", StringComparison.Ordinal));

    [Fact]
    public async Task TheCalendarFamilies_MustBeReadOnceHoweverManyStaffAreCandidates()
    {
        var one = await ReadDayAsync(await SeedGraphAsync(1));
        var eight = await ReadDayAsync(await SeedGraphAsync(8));

        // Without these two the fact could be satisfied by a day that answered nothing: an empty roster, or a
        // closed day, reads no calendars either.
        one.SlotCount.Should().BeGreaterThan(0, "a day that produced no slots never reached the calendar reads");
        eight.SlotCount.Should().Be(one.SlotCount * 8,
            "eight identical staff members offer eight copies of the same day, so the roster really was evaluated");

        foreach (var table in new[] { "Holidays", "Appointments", "WorkingHours", "BusinessHours", "AvailabilityExceptions" })
        {
            ReadsOf(one, table).Should().Be(ReadsOf(eight, table),
                $"the {table} read is parameterised by the whole candidate list, so it cannot depend on roster size");
            ReadsOf(eight, table).Should().Be(1,
                $"one batched statement per family is what closed PERF-02; {ReadsOf(eight, table)} were sent for eight staff");
        }
    }

    [Fact]
    public async Task TheWholeDay_MustCostAFixedNumberOfStatements()
    {
        var one = await ReadDayAsync(await SeedGraphAsync(1));
        var eight = await ReadDayAsync(await SeedGraphAsync(8));

        eight.Queries.Should().Be(one.Queries,
            "no read on this path is allowed to be issued per candidate staff member");
        one.Queries.Should().BeGreaterThan(5,
            "the day loads a tenant, a location, a service, a roster and five calendar families");
    }
}
