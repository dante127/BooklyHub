using System.Net;
using System.Text.Json;
using BooklyHub.Application.Security;
using BooklyHub.Domain.Entities.Organizations;
using BooklyHub.Domain.Entities.Services;
using BooklyHub.Domain.Entities.StaffMembers;
using BooklyHub.Domain.Entities.Tenancy;
using BooklyHub.Infrastructure.Data;
using BooklyHub.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BooklyHub.IntegrationTests.Security;

/// <summary>
/// API-08: <c>GET /api/v1/staff</c> is <c>[AllowAnonymous]</c> and answered <c>email</c> and <c>phoneNumber</c> for
/// every active member of whatever tenant the caller named in <c>X-Tenant-Id</c> — a directory of contact details,
/// with no token and no rate tier in front of it. The names themselves are the public half of the product (the
/// booking portal shows them, and <c>GET /api/v1/availability</c> already puts <c>staffName</c> in every slot), so
/// the finding is not the roster and was never going to be fixed by hiding it.
///
/// The fix is the two fields that turn a name into a phone call. They are now gated on the same requirement the
/// authorized staff routes carry — <c>staff.read</c> — evaluated per request instead of on the action, because this
/// route still has to answer anonymously. Deleting the fields outright would have taken the only read of staff
/// contact details away from the tenant's own staff and receptionist, which is why the second fact below asserts
/// that an authorized caller still gets them.
/// </summary>
public class StaffContactDisclosureTests : IClassFixture<BooklyHubWebApplicationFactory>
{
    private readonly BooklyHubWebApplicationFactory _factory;

    public StaffContactDisclosureTests(BooklyHubWebApplicationFactory factory) => _factory = factory;

    private sealed record Graph(Guid TenantId, Guid LocationId, Guid ServiceId, string Email, string PhoneNumber);

    /// <summary>A tenant with one rostered member whose contact details are unique enough to search a body for.</summary>
    private async Task<Graph> SeedStaffAsync(string slug)
    {
        var graph = new Graph(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            $"ava-{slug}-{Guid.NewGuid():N}@disclose.test",
            $"+963{Guid.NewGuid().ToString("N")[..9]}");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        db.Tenants.Add(new Tenant(graph.TenantId, $"Disclose {slug}", $"disclose-{slug}-{graph.TenantId:N}", "UTC"));
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

        var staffId = Guid.NewGuid();
        var staff = new Staff
        {
            Id = staffId,
            TenantId = graph.TenantId,
            LocationId = graph.LocationId,
            FirstName = "Ava",
            LastName = "Doc",
            Title = "Dr",
            Bio = "bio",
            Email = graph.Email,
            PhoneNumber = graph.PhoneNumber
        };
        staff.StaffServices.Add(new StaffService
        {
            TenantId = graph.TenantId,
            StaffId = staffId,
            ServiceId = graph.ServiceId
        });
        db.StaffMembers.Add(staff);

        await db.SaveChangesAsync();
        return graph;
    }

    private HttpClient Anonymous(Graph graph)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", graph.TenantId.ToString());
        return client;
    }

    private async Task<(JsonElement Root, string Body, HttpStatusCode Status)> ReadAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/v1/staff");
        var body = await response.Content.ReadAsStringAsync();
        return (JsonDocument.Parse(body).RootElement, body, response.StatusCode);
    }

    /// <summary>The one item the seeded graph has, with the guard that it is really there.</summary>
    private static JsonElement OnlyStaff(JsonElement envelope)
    {
        var items = envelope.GetProperty("items");
        items.GetArrayLength().Should().Be(1,
            "a disclosure fact read over an empty page is green for the wrong reason — the roster row did not survive seeding");
        return items[0];
    }

    [Fact]
    public async Task AnAnonymousCaller_MustNotBeGivenStaffContactDetails()
    {
        var graph = await SeedStaffAsync("anon");

        var (root, body, status) = await ReadAsync(Anonymous(graph));

        status.Should().Be(HttpStatusCode.OK, body);
        var staff = OnlyStaff(root);

        staff.GetProperty("email").ValueKind.Should().Be(JsonValueKind.Null,
            "the key stays in the shape — a booking portal parses one contract whatever its caller — but the value is not a contact address");
        staff.GetProperty("phoneNumber").ValueKind.Should().Be(JsonValueKind.Null);

        // The key staying is a choice, so the stronger check is on the text: nothing in the body may carry the address.
        body.Should().NotContain(graph.Email);
        body.Should().NotContain(graph.PhoneNumber);

        // And the part the public portal actually renders is untouched — this is a disclosure fix, not a shutdown.
        staff.GetProperty("firstName").GetString().Should().Be("Ava");
        staff.GetProperty("lastName").GetString().Should().Be("Doc");
        staff.GetProperty("title").GetString().Should().Be("Dr");
        staff.GetProperty("offeredServices")[0].GetProperty("serviceName").GetString().Should().Be("Consult");
    }

    [Fact]
    public async Task ACallerHoldingStaffRead_MustStillGetTheContactDetailsItAlwaysHad()
    {
        var graph = await SeedStaffAsync("staff-role");

        // Roles.Staff carries staff.read, and so do TenantOwner, TenantAdmin, Manager and Receptionist; this is the
        // caller that used to reach the same two fields anonymously and now has to be signed in to see them.
        var (root, body, status) = await ReadAsync(_factory.CreateClientForTenant(graph.TenantId, Roles.Staff));

        status.Should().Be(HttpStatusCode.OK, body);
        OnlyStaff(root).GetProperty("email").GetString().Should().Be(graph.Email,
            "the gate moves these fields behind a permission, it does not remove the route that reads them");
        body.Should().Contain(graph.PhoneNumber);
    }

    [Fact]
    public async Task ATokenIsNotEnough_TheGateIsThePermission()
    {
        var graph = await SeedStaffAsync("no-permission");

        // Accountant holds payments.read, reports.read and appointments.read — and not staff.read. Without this fact
        // the fix would pass for an implementation that only checked "is there a bearer token".
        var (root, body, status) = await ReadAsync(_factory.CreateClientForTenant(graph.TenantId, Roles.Accountant));

        status.Should().Be(HttpStatusCode.OK, body, "the route is still anonymous-readable; a caller without the permission loses fields, not the call");
        OnlyStaff(root).GetProperty("email").ValueKind.Should().Be(JsonValueKind.Null);
        body.Should().NotContain(graph.Email,
            $"an authenticated {Roles.Accountant} is not a staff directory: {graph.Email} appeared in the body");
    }

    [Fact]
    public async Task TheServiceCatalog_MustNotHaveGainedAnythingToDisclose()
    {
        var graph = await SeedStaffAsync("services");

        var response = await Anonymous(graph).GetAsync("/api/v1/services");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        var item = JsonDocument.Parse(body).RootElement.GetProperty("items")[0];

        // The projection is named here so a future column addition is a decision about disclosure, not a merge
        // conflict nobody noticed: nine fields, none of them contact details, none of them patient data.
        item.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(new[]
        {
            "id", "name", "description", "durationMinutes", "price", "currency",
            "bufferBeforeMinutes", "bufferAfterMinutes", "categoryName"
        }, "the anonymous catalog read publishes a bookable service, and that is the whole list");
    }
}
