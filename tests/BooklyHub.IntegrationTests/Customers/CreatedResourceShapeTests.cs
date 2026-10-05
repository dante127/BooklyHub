using System.Net;
using System.Net.Http.Json;
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

namespace BooklyHub.IntegrationTests.Customers;

/// <summary>
/// LOC-01: the two routes on this server that create a row answered <c>201</c> with a <c>Location</c> the same
/// server does not understand. <c>POST /api/v1/customers</c> pointed at <c>/api/v1/customers?id=&lt;guid&gt;</c>
/// — a route whose parameters are <c>search</c>, <c>page</c> and <c>pageSize</c> — so following the link returned
/// page 1 of the whole book, and its body was the <c>Customer</c> aggregate itself, including
/// <c>domainEvents</c>, the soft-delete columns and an unloaded <c>customerNotes</c> collection.
/// <c>POST /api/v1/reviews</c> did the same with <c>appointmentId</c>, which the review wall also ignores.
/// </summary>
/// <remarks>
/// The shape test compares the create body against the *same tenant's list response* rather than against a
/// hard-coded field list: the claim worth pinning is that one row does not look two ways depending on which door
/// the client came in through.
/// </remarks>
public class CreatedResourceShapeTests : IClassFixture<BooklyHubWebApplicationFactory>
{
    private readonly BooklyHubWebApplicationFactory _factory;

    public CreatedResourceShapeTests(BooklyHubWebApplicationFactory factory) => _factory = factory;

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = false };

    private sealed record Graph(Guid TenantId, Guid LocationId, Guid ServiceId, Guid StaffId, Guid CustomerId);

    private async Task<Graph> SeedGraphAsync(string slug)
    {
        var graph = new Graph(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        db.Tenants.Add(new Tenant(graph.TenantId, $"Shape {slug}", $"shape-{slug}-{graph.TenantId:N}", "UTC"));
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
            FirstName = "Ola",
            LastName = "Doc",
            Email = $"ola-{graph.StaffId:N}@shape.test"
        };
        staff.StaffServices.Add(new StaffService
        {
            TenantId = graph.TenantId,
            StaffId = graph.StaffId,
            ServiceId = graph.ServiceId
        });
        db.StaffMembers.Add(staff);

        await db.SaveChangesAsync();
        return graph;
    }

    /// <summary>A visit that has finished, because a review is only accepted for one that reached `Completed`.</summary>
    private async Task<Guid> SeedCompletedVisitAsync(Graph graph)
    {
        var ends = DateTime.UtcNow.AddHours(-2);
        var appointment = Appointment.Create(
            graph.TenantId, graph.LocationId, graph.ServiceId, graph.StaffId, graph.CustomerId,
            ends.AddMinutes(-30), ends, 30, 100.00m, "USD");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        db.Customers.Add(new Customer
        {
            Id = graph.CustomerId,
            TenantId = graph.TenantId,
            FirstName = "Ziad",
            LastName = "Patient",
            Email = $"ziad-{graph.CustomerId:N}@shape.test"
        });
        db.Appointments.Add(appointment);
        await db.SaveChangesAsync();

        // Rewritten rather than transitioned: walking the state machine to Completed needs the clock on its side,
        // and this fact is about the response a review answers with, not about how the visit got closed.
        await db.Database.ExecuteSqlAsync(
            $"UPDATE Appointments SET Status = {(int)AppointmentStatus.Completed} WHERE Id = {appointment.Id}");

        return appointment.Id;
    }

    private static List<string> Keys(JsonElement element) =>
        element.EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToList();

    [Fact]
    public async Task TheCreatedCustomer_MustBeAnsweredInTheSameShapeTheListAnswersIt()
    {
        var graph = await SeedGraphAsync("cust-shape");
        using var client = _factory.CreateClientForTenant(graph.TenantId, Roles.TenantOwner);

        var created = await client.PostAsJsonAsync("/api/v1/customers", new
        {
            FirstName = "Nour",
            LastName = "Ali",
            Email = $"nour-{Guid.NewGuid():N}@shape.test",
            PhoneNumber = "+9639000000",
            Notes = "prefers mornings"
        }, Indented);

        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement;

        var listed = await client.GetAsync("/api/v1/customers");
        listed.EnsureSuccessStatusCode();
        var listItem = JsonDocument.Parse(await listed.Content.ReadAsStringAsync())
            .RootElement.GetProperty("items")[0];

        Keys(body).Should().BeEquivalentTo(Keys(listItem),
            "one row must not look two ways depending on which door the client came in through");

        // Named rather than left to the comparison above: these are this server's own plumbing, and an assertion
        // that they are absent says so in the failure text if a future projection reaches for the entity again.
        foreach (var internalField in new[] { "domainEvents", "isDeleted", "deletedAtUtc", "deletedBy", "customerNotes", "createdBy", "tenantId" })
        {
            body.TryGetProperty(internalField, out _).Should().BeFalse(
                $"{internalField} is persistence detail; a public 201 body that carries it gets read as contract");
        }
    }

    [Fact]
    public async Task TheCreatedCustomer_LocationMustBeAUrlTheSameClientCanFetch()
    {
        var graph = await SeedGraphAsync("cust-location");
        using var client = _factory.CreateClientForTenant(graph.TenantId, Roles.TenantOwner);

        var response = await client.PostAsJsonAsync("/api/v1/customers", new
        {
            FirstName = "Ziad",
            LastName = "Haddad",
            Email = $"ziad-{Guid.NewGuid():N}@shape.test"
        }, Indented);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var location = response.Headers.Location;
        location.Should().NotBeNull("a 201 without a Location is a created row nobody can ask for again");

        var created = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        var id = created.GetProperty("id").GetGuid();

        // The defect was a link that resolved to a page that ignored the id it carried, so the check is that the
        // link has nothing to ignore and that the row is reachable through it.
        location!.Query.Should().BeEmpty(
            $"the route behind {location} reads no such parameter, so a query string here is a link that lies");

        var followed = await client.GetAsync(location!.ToString());
        followed.StatusCode.Should().Be(HttpStatusCode.OK, await followed.Content.ReadAsStringAsync());
        var followedBody = JsonDocument.Parse(await followed.Content.ReadAsStringAsync()).RootElement;
        followedBody.GetProperty("items").EnumerateArray()
            .Any(i => i.GetProperty("id").GetGuid() == id)
            .Should().BeTrue("following the Location a 201 hands out has to reach the row that 201 created");
    }

    [Fact]
    public async Task TheCreatedReview_LocationMustNotCarryAParameterTheWallIgnores()
    {
        var graph = await SeedGraphAsync("review-location");
        var appointmentId = await SeedCompletedVisitAsync(graph);

        using var client = _factory.CreateClientForTenant(graph.TenantId, Roles.TenantOwner);
        var response = await client.PostAsJsonAsync("/api/v1/reviews", new
        {
            appointmentId,
            rating = 5,
            comment = "Clean clinic, quick visit."
        }, Indented);

        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Created, text);

        var location = response.Headers.Location;
        location.Should().NotBeNull();
        location!.AbsolutePath.Should().Be("/api/v1/reviews");
        location.Query.Should().BeEmpty(
            $"GET /api/v1/reviews filters on staffId and serviceId, so {location.Query} was a link to a filter that does not exist");

        var followed = await client.GetAsync(location!.ToString());
        followed.StatusCode.Should().Be(HttpStatusCode.OK, await followed.Content.ReadAsStringAsync());

        var reviewId = JsonDocument.Parse(text).RootElement.GetProperty("id").GetGuid();
        JsonDocument.Parse(await followed.Content.ReadAsStringAsync()).RootElement
            .GetProperty("items").EnumerateArray()
            .Any(i => i.GetProperty("id").GetGuid() == reviewId)
            .Should().BeTrue("the wall the Location points at has to contain the review it was issued for");
    }
}
