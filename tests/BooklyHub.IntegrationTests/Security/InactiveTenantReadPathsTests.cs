using System.Net;
using System.Text.Json;
using BooklyHub.Domain.Entities.Organizations;
using BooklyHub.Domain.Entities.Scheduling;
using BooklyHub.Domain.Entities.Services;
using BooklyHub.Domain.Entities.StaffMembers;
using BooklyHub.Domain.Entities.Tenancy;
using BooklyHub.Infrastructure.Data;
using BooklyHub.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BooklyHub.IntegrationTests.Security;

/// <summary>
/// <c>docs/MULTI-TENANCY.md</c> §2 promised that the tenant middleware "confirms that the resolved TenantId exists
/// in the database and <c>IsActive == true</c>", rejecting an invalid or inactive tenant with <c>400</c> or
/// <c>403</c>. The middleware makes no database call at all (<c>TenantResolutionMiddleware.cs</c> never resolves a
/// <c>DbContext</c>), and the only place <c>Tenant.IsActive</c> is consulted on a read path is
/// <c>AvailabilityService.LoadBookingContextAsync</c> — which answers an empty day, not a refusal.
/// </summary>
/// <remarks>
/// The first fact exists to keep the second one honest: an open tenant with a rostered staff member must be able to
/// produce slots on the same request, otherwise "the switched-off tenant produced no slots" would also be true of a
/// graph that never could.
/// </remarks>
public class InactiveTenantReadPathsTests : IClassFixture<BooklyHubWebApplicationFactory>
{
    private readonly BooklyHubWebApplicationFactory _factory;

    public InactiveTenantReadPathsTests(BooklyHubWebApplicationFactory factory) => _factory = factory;

    private sealed record Graph(Guid TenantId, Guid LocationId, Guid ServiceId, Guid StaffId);

    private async Task<Graph> SeedRosteredGraphAsync(string slug)
    {
        var graph = new Graph(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        db.Tenants.Add(new Tenant(graph.TenantId, $"Calendar {slug}", $"calendar-{slug}-{graph.TenantId:N}", "UTC"));
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
            Price = 50.00m
        });

        var staff = new Staff
        {
            Id = graph.StaffId,
            TenantId = graph.TenantId,
            LocationId = graph.LocationId,
            FirstName = "Ola",
            LastName = "Doc",
            Email = $"ola-{graph.StaffId:N}@calendar.test"
        };
        staff.StaffServices.Add(new StaffService
        {
            TenantId = graph.TenantId,
            StaffId = graph.StaffId,
            ServiceId = graph.ServiceId
        });

        // Mon-Fri 08:00-18:00, the shape the roster reader expects: a working row whose non-break interval spans
        // the day. Without it the same request answers empty for an *open* tenant too, and the switched-off fact
        // below would prove nothing.
        for (var day = DayOfWeek.Monday; day <= DayOfWeek.Friday; day++)
        {
            var workingHour = new WorkingHour
            {
                TenantId = graph.TenantId,
                StaffId = graph.StaffId,
                LocationId = graph.LocationId,
                DayOfWeek = day,
                IsWorkingDay = true
            };
            workingHour.Intervals.Add(new WorkingHourInterval(new TimeSpan(8, 0, 0), new TimeSpan(18, 0, 0), false));
            staff.WorkingHours.Add(workingHour);
        }

        db.StaffMembers.Add(staff);
        await db.SaveChangesAsync();

        return graph;
    }

    private static DateOnly NextBookableWeekday()
    {
        var date = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(5));
        while (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            date = date.AddDays(1);
        }

        return date;
    }

    private HttpClient AnonymousClient(Guid tenantId)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenantId.ToString());
        return client;
    }

    private async Task SwitchTenantOffAsync(Guid tenantId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.Database.ExecuteSqlAsync(
            $"UPDATE Tenants SET IsActive = 0 WHERE Id = {tenantId}");
    }

    private async Task<JsonElement> AskAvailabilityAsync(HttpClient client, Graph graph, DateOnly date)
    {
        var response = await client.GetAsync(
            $"/api/v1/availability?locationId={graph.LocationId}&serviceId={graph.ServiceId}&date={date:yyyy-MM-dd}");

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }

    [Fact]
    public async Task AnOpenTenantWithARosteredStaff_AnswersSlotsForABookableWeekday()
    {
        var graph = await SeedRosteredGraphAsync("open");
        using var client = AnonymousClient(graph.TenantId);

        var day = await AskAvailabilityAsync(client, graph, NextBookableWeekday());

        day.GetProperty("isOpen").GetBoolean().Should().BeTrue("the tenant is active, the location and service are, and the staff member is rostered");
        day.GetProperty("slots").GetArrayLength().Should().BeGreaterThan(0,
            "this graph must be capable of producing slots, or the switched-off fact below proves nothing");
    }

    [Fact]
    public async Task ASwitchedOffTenant_IsAnsweredWithAnEmptyDayNotA400OrA403()
    {
        var graph = await SeedRosteredGraphAsync("off");
        await SwitchTenantOffAsync(graph.TenantId);

        using var client = AnonymousClient(graph.TenantId);
        var day = await AskAvailabilityAsync(client, graph, NextBookableWeekday());

        // The refusal the doc named is not what this server does; it answers a normal empty day.
        day.GetProperty("isOpen").GetBoolean().Should().BeFalse("the tenant is switched off, so the day is not bookable");
        day.GetProperty("slots").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task ASwitchedOffTenant_StillAnswersItsAnonymousCatalogAndReviewReads()
    {
        var graph = await SeedRosteredGraphAsync("catalog");
        await SwitchTenantOffAsync(graph.TenantId);

        using var client = AnonymousClient(graph.TenantId);

        var staff = await client.GetAsync("/api/v1/staff");
        staff.StatusCode.Should().Be(HttpStatusCode.OK, await staff.Content.ReadAsStringAsync());
        IdsOf(JsonDocument.Parse(await staff.Content.ReadAsStringAsync()).RootElement)
            .Should().Contain(graph.StaffId,
                "no read on this route asks whether the tenant is active — only whether the staff row is");

        var services = await client.GetAsync("/api/v1/services");
        services.StatusCode.Should().Be(HttpStatusCode.OK, await services.Content.ReadAsStringAsync());
        IdsOf(JsonDocument.Parse(await services.Content.ReadAsStringAsync()).RootElement)
            .Should().Contain(graph.ServiceId);

        var reviews = await client.GetAsync("/api/v1/reviews");
        reviews.StatusCode.Should().Be(HttpStatusCode.OK, await reviews.Content.ReadAsStringAsync());
    }

    /// <summary>Both catalog routes page since PERF-04's residue closed, so the ids a caller reads are in `items`.</summary>
    private static List<Guid> IdsOf(JsonElement envelope) =>
        envelope.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToList();
}
