using System.Net;
using System.Text.Json;
using BooklyHub.Application.Security;
using BooklyHub.Domain.Entities.Appointments;
using BooklyHub.Domain.Entities.Customers;
using BooklyHub.Domain.Entities.Organizations;
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
/// <c>GET /api/v1/appointments</c> took both bounds as optional and sorted <c>StartAtUtc</c> descending, so
/// the request a desk actually makes — no parameters — answered with the furthest future booking first, and
/// <c>totalCount</c> counted a different population from the one being paged. This class binds the replacement:
/// no bounds means the upcoming book, ordered nearest-first, and the count describing exactly that window;
/// either bound supplied means the caller's window and nothing injected into it. It also binds the paging
/// rule the collection queue already runs on, because an order that can tie is an order that can drop a row
/// between two pages.
/// </summary>
public class AppointmentListWindowTests : IClassFixture<BooklyHubWebApplicationFactory>
{
    private static readonly DateTime PinnedNow = new(2027, 6, 1, 9, 0, 0, DateTimeKind.Utc);

    private readonly BooklyHubWebApplicationFactory _factory;

    public AppointmentListWindowTests(BooklyHubWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private sealed record Graph(Guid TenantId, Guid LocationId, Guid ServiceId, Guid StaffId, Guid CustomerId);

    private async Task<Graph> SeedGraphAsync(string slug)
    {
        var graph = new Graph(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        db.Tenants.Add(new Tenant(graph.TenantId, $"List Clinic {slug}", $"list-{slug}-{graph.TenantId:N}", "UTC"));

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

        db.StaffMembers.Add(new Staff
        {
            Id = graph.StaffId,
            TenantId = graph.TenantId,
            LocationId = graph.LocationId,
            FirstName = "Nadia",
            LastName = "Doc",
            Email = $"nadia-{graph.StaffId:N}@list.test"
        });

        db.Customers.Add(new Customer
        {
            Id = graph.CustomerId,
            TenantId = graph.TenantId,
            FirstName = "Eli",
            LastName = "Patient",
            Email = $"eli-{graph.CustomerId:N}@list.test"
        });

        await db.SaveChangesAsync();
        return graph;
    }

    private async Task<Guid> SeedAppointmentAsync(Graph graph, DateTime startAtUtc)
    {
        var appointment = Appointment.Create(
            graph.TenantId, graph.LocationId, graph.ServiceId, graph.StaffId, graph.CustomerId,
            startAtUtc, startAtUtc.AddMinutes(30), 30, 100.00m, "USD");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.Appointments.Add(appointment);
        await db.SaveChangesAsync();

        return appointment.Id;
    }

    private async Task<JsonElement> SearchAsync(Graph graph, string query = "pageSize=50")
    {
        var client = _factory.CreateClientForTenant(graph.TenantId, Roles.Manager);
        var response = await client.GetAsync($"/api/v1/appointments?{query}");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return JsonDocument.Parse(body).RootElement;
    }

    private static List<Guid> IdsOf(JsonElement root)
    {
        var ids = new List<Guid>();
        foreach (var item in root.GetProperty("items").EnumerateArray())
        {
            ids.Add(item.GetProperty("id").GetGuid());
        }

        return ids;
    }

    private static string Stamp(DateTime value) => value.ToString("O");

    [Fact]
    public async Task Search_NoBounds_MustDefaultToTheUpcomingBookNearestFirst()
    {
        var graph = await SeedGraphAsync("default");
        await SeedAppointmentAsync(graph, PinnedNow.AddDays(-40));
        await SeedAppointmentAsync(graph, PinnedNow.AddDays(-1));
        var tomorrow = await SeedAppointmentAsync(graph, PinnedNow.AddDays(1));
        var nextSeason = await SeedAppointmentAsync(graph, PinnedNow.AddDays(90));

        try
        {
            _factory.Clock.Pin(PinnedNow);
            var root = await SearchAsync(graph);

            IdsOf(root).Should().Equal(new[] { tomorrow, nextSeason },
                "the default page is what is coming, nearest first — not the furthest thing in the diary");
            root.GetProperty("totalCount").GetInt32().Should().Be(2,
                "the count describes the window actually paged, not the whole book behind it");
        }
        finally
        {
            _factory.Clock.Release();
        }
    }

    [Fact]
    public async Task Search_WithOneBound_MustBeHonouredWithoutAnythingInjectedIntoTheOtherEnd()
    {
        var graph = await SeedGraphAsync("one-bound");
        var longAgo = await SeedAppointmentAsync(graph, PinnedNow.AddDays(-40));
        var yesterday = await SeedAppointmentAsync(graph, PinnedNow.AddDays(-1));
        await SeedAppointmentAsync(graph, PinnedNow.AddDays(1));

        try
        {
            _factory.Clock.Pin(PinnedNow);

            // The trap this exists to avoid: a caller asking for history up to now, met by a default start of
            // now, would be handed an empty page and read it as an empty book.
            var past = await SearchAsync(graph, $"pageSize=50&toUtc={Stamp(PinnedNow)}");
            IdsOf(past).Should().Equal(longAgo, yesterday);
            past.GetProperty("totalCount").GetInt32().Should().Be(2);

            var both = await SearchAsync(graph,
                $"pageSize=50&fromUtc={Stamp(PinnedNow.AddDays(-45))}&toUtc={Stamp(PinnedNow)}");
            IdsOf(both).Should().Equal(longAgo, yesterday);

            var forward = await SearchAsync(graph, $"pageSize=50&fromUtc={Stamp(PinnedNow.AddDays(2))}");
            IdsOf(forward).Should().BeEmpty("nothing was booked two days out in this book");
        }
        finally
        {
            _factory.Clock.Release();
        }
    }

    [Fact]
    public async Task Search_DefaultWindow_MustMoveWithTheApplicationClock()
    {
        var graph = await SeedGraphAsync("clock");
        var earlier = await SeedAppointmentAsync(graph, PinnedNow.AddDays(-10));
        var later = await SeedAppointmentAsync(graph, PinnedNow.AddDays(10));

        try
        {
            _factory.Clock.Pin(PinnedNow);
            IdsOf(await SearchAsync(graph)).Should().Equal(new[] { later });

            // The same rows, the same query string, the same machine: only the clock moved, and the boundary
            // that decides which booking the default window calls upcoming moved with it. A default reading
            // DateTime.UtcNow would answer identically at both pins, because both are years from real now.
            // The ten-day gap between pins is what keeps this off the boundary of a live clock read.
            _factory.Clock.Pin(PinnedNow.AddDays(-20));
            IdsOf(await SearchAsync(graph)).Should().Equal(new[] { earlier, later },
                "walking the clock back ten days must put the older booking back inside the window");
        }
        finally
        {
            _factory.Clock.Release();
        }
    }

    [Fact]
    public async Task Search_SharedStartMinute_MustPageStablyAcrossRepeatedWalks()
    {
        var graph = await SeedGraphAsync("tie");
        var start = PinnedNow.AddDays(3);

        // Three bookings in the same minute: the page boundaries fall inside the tie, which is where an order
        // that does not fully determine itself drops one row and repeats another.
        var ids = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            ids.Add(await SeedAppointmentAsync(graph, start));
        }

        try
        {
            _factory.Clock.Pin(PinnedNow);

            var firstWalk = await WalkPagesAsync(graph, pageCount: 3);
            var secondWalk = await WalkPagesAsync(graph, pageCount: 3);

            firstWalk.Should().OnlyHaveUniqueItems("three tied bookings must not share a page slot");
            firstWalk.Should().BeEquivalentTo(ids);
            firstWalk.Should().Equal(secondWalk,
                "two walks over the same book have to agree, or a caller paging twice sees two different lists");
        }
        finally
        {
            _factory.Clock.Release();
        }
    }

    private async Task<List<Guid>> WalkPagesAsync(Graph graph, int pageCount)
    {
        var seen = new List<Guid>();
        for (var page = 1; page <= pageCount; page++)
        {
            var root = await SearchAsync(graph, $"page={page}&pageSize=1");
            IdsOf(root).Should().HaveCount(1, $"page {page} of three tied bookings");
            seen.AddRange(IdsOf(root));
        }

        return seen;
    }
}
