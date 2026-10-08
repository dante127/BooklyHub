using System.Net;
using System.Text.Json;
using BooklyHub.Application.Scheduling;
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
/// PERF-04's last open half: the availability day was the one anonymous response whose size the tenant's data chose.
/// `docs/PERFORMANCE.md` §6 measured it at 6.7 KB for one staff member, 111 KB for ten and **683 KB for fifty**, and
/// the same table says the *work* is worth nothing (19.9 ms at fifty, 8.4 ms at 1,440 starts), which is why the bound
/// is on the payload and not on the roster. These three facts are the ceiling, the cut it makes, and the promise that
/// the cut never touches a day a real clinic would have.
/// </summary>
public class AvailabilityPayloadBoundTests : IClassFixture<BooklyHubWebApplicationFactory>
{
    /// <summary>A Monday, so the seeded shift matches the day the query asks about.</summary>
    private static readonly DateTime Now = new(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly QueryDate = new(2026, 10, 5);

    private readonly BooklyHubWebApplicationFactory _factory;

    public AvailabilityPayloadBoundTests(BooklyHubWebApplicationFactory factory) => _factory = factory;

    private sealed record Graph(Guid TenantId, Guid LocationId, Guid ServiceId);

    /// <summary>
    /// A tenant whose whole roster works the identical 08:00–18:00 day, so the only things that move the slot count
    /// are <paramref name="staffCount"/> and the grid step. Notice is zero and the clock is pinned to midnight, so
    /// every start on the day is bookable and the ceiling is reached by size alone.
    /// </summary>
    private async Task<Graph> SeedGraphAsync(int staffCount, int slotIntervalMinutes)
    {
        var graph = new Graph(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var tenant = new Tenant(graph.TenantId, "Dense Clinic", $"dense-{graph.TenantId:N}", "UTC");
        tenant.Settings = new TenantSetting(graph.TenantId)
        {
            MinBookingNoticeMinutes = 0,
            MaxAdvanceBookingDays = 365,
            SlotIntervalMinutes = slotIntervalMinutes
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

        db.Services.Add(new Service
        {
            Id = graph.ServiceId,
            TenantId = graph.TenantId,
            Name = "Consult",
            DurationMinutes = 30,
            Price = 100.00m
        });

        for (var i = 0; i < staffCount; i++)
        {
            var staffId = Guid.NewGuid();
            var staff = new Staff
            {
                Id = staffId,
                TenantId = graph.TenantId,
                LocationId = graph.LocationId,
                FirstName = "Doc",
                LastName = $"Number{i:000}",
                Email = $"doc{i}-{staffId:N}@dense.test"
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

    private async Task<(JsonElement Root, int Bytes)> ReadDayAsync(Graph graph)
    {
        _factory.Clock.Pin(Now);
        try
        {
            var response = await _factory.CreateClient().GetAsync(
                $"/api/v1/availability?tenantId={graph.TenantId}&locationId={graph.LocationId}" +
                $"&serviceId={graph.ServiceId}&date={QueryDate:yyyy-MM-dd}");
            var body = await response.Content.ReadAsStringAsync();

            response.StatusCode.Should().Be(HttpStatusCode.OK, body);
            return (JsonDocument.Parse(body).RootElement, System.Text.Encoding.UTF8.GetByteCount(body));
        }
        finally
        {
            _factory.Clock.Release();
        }
    }

    private static int SlotCount(JsonElement root) => root.GetProperty("slots").GetArrayLength();

    [Fact]
    public async Task ADayDenserThanTheCeiling_MustAnswerTheCeilingAndSaySo()
    {
        // Fifty-one identical staff on a 30-minute grid is about 1,020 slots, so the ceiling has to bite.
        var root = (await ReadDayAsync(await SeedGraphAsync(51, 30))).Root;

        root.GetProperty("isOpen").GetBoolean().Should().BeTrue("this day is open, just long");
        SlotCount(root).Should().Be(AvailabilityLimits.MaxSlotsPerDay,
            $"a dense roster answered {SlotCount(root)} slots where the stated ceiling is {AvailabilityLimits.MaxSlotsPerDay}");
        root.GetProperty("slotsTruncated").GetBoolean().Should().BeTrue(
            "a day that was cut has to say it was, or a portal reads the missing afternoon as nobody being free");
    }

    [Fact]
    public async Task TheCut_MustKeepTheEarliestSlotsOfTheDay()
    {
        var (root, _) = await ReadDayAsync(await SeedGraphAsync(51, 30));
        var slots = root.GetProperty("slots").EnumerateArray()
            .Select(s => s.GetProperty("startAtUtc").GetDateTime()).ToList();

        slots.Should().BeInAscendingOrder(
            "the half a portal loses is the afternoon, so what it shows is a real contiguous morning rather than a sample of the day");

        // The first slot of the day survives a cut; if it did not, the ceiling would be dropping staff rather than
        // trimming the tail, and the flag would be describing a different event than the one that happened.
        slots[0].Should().Be(new DateTime(2026, 10, 5, 8, 0, 0, DateTimeKind.Utc),
            "the day's earliest bookable start has to be on a page a truncated day still answers");
    }

    [Fact]
    public async Task AGridNoCallerCanAskFor_MustStillFitUnderTheCeiling()
    {
        // SlotIntervalMinutes has no writer outside the seeder, so a one-minute grid is an operator editing a row —
        // and a day of starts finer than the ceiling is the shape the bound exists for, whatever the roster is.
        var (root, bytes) = await ReadDayAsync(await SeedGraphAsync(1, 1));

        root.GetProperty("slotsTruncated").GetBoolean().Should().BeTrue(
            "one staff member on a one-minute grid answers more slots than the ceiling allows, and that is the density the bound is for");
        SlotCount(root).Should().Be(AvailabilityLimits.MaxSlotsPerDay,
            $"the bound is on the response, not on the roster: {SlotCount(root)} slots came back");

        // Bytes are what the finding was about. A 500-slot answer is 188,586 bytes measured (~377 B a slot), where
        // the same fixture unbounded answered 215,354 here and 384,627 for the 1,020-slot dense roster — and the day
        // `docs/PERFORMANCE.md` §6 measured at 683 KB was about 2,000 slots. 250,000 leaves room for a slot to grow
        // without letting the bound mean nothing.
        bytes.Should().BeLessThan(250_000,
            $"the ceiling exists to bound the body, and this response was {bytes} bytes");
    }

    [Fact]
    public async Task ADayUnderTheCeiling_MustLoseNothing()
    {
        var root = (await ReadDayAsync(await SeedGraphAsync(8, 30))).Root;

        root.GetProperty("slotsTruncated").GetBoolean().Should().BeFalse(
            "eight staff on a ten-hour day is 160 slots, so a bound that fires here is cutting a clinic's own day");
        SlotCount(root).Should().Be(160,
            "every slot of a day under the ceiling is answered — this is the number the finding never objected to");

        var staffIds = root.GetProperty("slots").EnumerateArray()
            .Select(s => s.GetProperty("staffId").GetGuid()).Distinct().ToList();
        staffIds.Should().HaveCount(8,
            "a payload bound that dropped whole staff members would still be able to answer 160 slots, and that is a different defect");
    }
}
