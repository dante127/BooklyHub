using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BooklyHub.Api;
using BooklyHub.Api.Middlewares;
using BooklyHub.Application.Security;
using BooklyHub.Domain.Entities.Identity;
using BooklyHub.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BooklyHub.IntegrationTests.Security;

/// <summary>
/// ENV-01's residue: a request whose tenant was never resolved. Six of these guards answered
/// <c>400 application/json</c> with <c>{ "message": ... }</c> in three different wordings, outside the envelope every
/// other failure on the same server writes; three more routes threw <c>BadHttpRequestException</c>, a type the
/// exception boundary had never mapped, and so answered <c>500</c> — "An unexpected server error occurred. Please
/// contact support with your Correlation ID." — for a mistake the caller makes. Both halves are pinned here from
/// the wire, because the two failures looked nothing alike from the outside and only one of them was in the finding.
/// </summary>
/// <remarks>
/// None of these routes is in the authentication tier, so the class can hold every fact on one host. The
/// tenantless principal is a token with no <c>tenant_id</c> claim and no PlatformAdmin role, which is exactly what
/// <c>AuthServices</c> mints for a user row whose <c>TenantId</c> is null — and what
/// <c>TenantResolutionMiddleware</c> already logs a warning about rather than refusing.
/// </remarks>
public class TenantRefusalTests : IClassFixture<BooklyHubWebApplicationFactory>
{
    private readonly BooklyHubWebApplicationFactory _factory;

    public TenantRefusalTests(BooklyHubWebApplicationFactory factory) => _factory = factory;

    private sealed record Refusal(int Status, string? ContentType, JsonElement Body, string? HeaderCorrelationId)
    {
        public string? Field(string name) =>
            Body.ValueKind == JsonValueKind.Object && Body.TryGetProperty(name, out var value) ? value.GetString() : null;

        public bool HasField(string name) => Body.ValueKind == JsonValueKind.Object
            && Body.EnumerateObject().Any(p => p.Name == name);
    }

    private static async Task<Refusal> ReadAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        return new Refusal((int)response.StatusCode,
            response.Content.Headers.ContentType?.MediaType,
            JsonDocument.Parse(text).RootElement.Clone(),
            response.Headers.TryGetValues(CorrelationIdMiddleware.CorrelationIdHeader, out var values)
                ? values.FirstOrDefault()
                : null);
    }

    private HttpClient TenantlessClient(string role)
    {
        var client = _factory.CreateClient();
        using var scope = _factory.Services.CreateScope();
        var generator = scope.ServiceProvider.GetRequiredService<IJwtTokenGenerator>();

        // No TenantId on the principal, and not a platform admin: the middleware has neither a claim to read nor a
        // header it is allowed to honour, so the request reaches the route with nothing resolved.
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = $"tenantless-{role.ToLowerInvariant()}@example.test",
            FirstName = "No",
            LastName = "Tenant",
            TenantId = null
        };

        var token = generator.GenerateAccessToken(user, [role], Permissions.GetDefaultPermissionsForRole(role));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task AMissingTenantOnACatalogRoute_MustAnswerTheStandardProblemEnvelope()
    {
        var refusal = await ReadAsync(await _factory.CreateClient().GetAsync("/api/v1/services"));

        refusal.Status.Should().Be(400);
        refusal.ContentType.Should().Be("application/problem+json",
            "the body the client is told to parse for every other failure is the body this one has to be in");
        refusal.Field("detail").Should().Be(TenantGuard.MissingTenant);
        refusal.Field("title").Should().Be("Bad Request");
        refusal.Field("instance").Should().Be("/api/v1/services");
        refusal.HasField("message").Should().BeFalse(
            "the lowercase field was the whole document before, and a client that reads the envelope never saw it");
    }

    [Fact]
    public async Task TheRefusalMustCarryInTheBodyTheIdTheHeaderAlreadyGives()
    {
        var refusal = await ReadAsync(await _factory.CreateClient().GetAsync("/api/v1/services"));

        refusal.HeaderCorrelationId.Should().NotBeNullOrEmpty();
        refusal.Field("correlationId").Should().Be(refusal.HeaderCorrelationId,
            "the correlation id is the handle support searches logs by, and a body without it is a dead end");
    }

    [Fact]
    public async Task OneCondition_MustGiveOneTextWhateverTheRouteAsksFor()
    {
        var services = await ReadAsync(await _factory.CreateClient().GetAsync("/api/v1/services"));
        var staff = await ReadAsync(await _factory.CreateClient().GetAsync("/api/v1/staff"));
        var reviews = await ReadAsync(await _factory.CreateClient().GetAsync("/api/v1/reviews"));
        var reports = await ReadAsync(await TenantlessClient(Roles.TenantAdmin).GetAsync("/api/v1/reports/dashboard"));

        // Three wordings for one condition meant a client had to match prose per route: "Tenant ID is required.",
        // "Active tenant context is required.", and availability's own sentence. What is pinned is that they are one
        // answer now, so the assertion is a literal and not TenantGuard.MissingTenant - reading the constant back
        // would let a route drift to its own sentence and still pass.
        var details = new[] { services, staff, reviews, reports }.Select(r => r.Field("detail")).ToList();

        details.Distinct().Should().HaveCount(1, "one condition, one wording");
        details[0].Should().Be("Active tenant context is required.");

        foreach (var refusal in new[] { services, staff, reviews, reports })
        {
            refusal.Status.Should().Be(400);
            refusal.ContentType.Should().Be("application/problem+json");
        }
    }

    [Fact]
    public async Task ATokenCarryingNoTenantClaim_IsAClientErrorNotAServerFault()
    {
        var appointments = await ReadAsync(await TenantlessClient(Roles.Staff).GetAsync("/api/v1/appointments"));

        appointments.Status.Should().Be(400,
            "this route threw BadHttpRequestException, which the boundary had never mapped, so it answered 500");
        appointments.Field("detail").Should().Be(TenantGuard.MissingTenant);
        appointments.Field("title").Should().Be("Bad Request");

        // The old body was the generic one, which sends a caller to support for a mistake they made.
        (appointments.Field("detail") ?? "").Should().NotContain("contact support");
        appointments.HasField("message").Should().BeFalse();
    }

    [Fact]
    public async Task AWriteOnTheSameCondition_IsRefusedTheSameWay()
    {
        var response = await TenantlessClient(Roles.Staff).PostAsJsonAsync(
            "/api/v1/reviews", new { AppointmentId = Guid.NewGuid(), Rating = 5 });

        var refusal = await ReadAsync(response);

        refusal.Status.Should().Be(400, "a review has no more right to a 500 than a list does");
        refusal.Field("detail").Should().Be(TenantGuard.MissingTenant);
        refusal.Field("instance").Should().Be("/api/v1/reviews");
    }

    [Fact]
    public async Task ThePublicPortalNamesThreeSources_BecauseItsConditionHasThree()
    {
        var client = _factory.CreateClient();
        var missing = await ReadAsync(await client.GetAsync(
            $"/api/v1/availability?locationId={Guid.NewGuid()}&serviceId={Guid.NewGuid()}&date=2026-10-05"));

        missing.Status.Should().Be(400);
        missing.ContentType.Should().Be("application/problem+json");
        missing.Field("detail").Should().Be("Tenant ID must be specified either via context, header, or query.",
            "this is the one guard whose condition genuinely differs: the route accepts the tenant from the request");

        // And the fallback it describes still works, which is what keeps the widened guard from tightening the rule.
        var answered = await client.GetAsync(
            $"/api/v1/availability?locationId={Guid.NewGuid()}&serviceId={Guid.NewGuid()}&date=2026-10-05" +
            $"&tenantId={Guid.NewGuid()}");

        answered.StatusCode.Should().Be(HttpStatusCode.OK, await answered.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ARequestThatDoesCarryATenant_StillGetsItsPage()
    {
        var anonymous = _factory.CreateClient();
        anonymous.DefaultRequestHeaders.Add("X-Tenant-Id", Guid.NewGuid().ToString());

        var catalog = await ReadAsync(await anonymous.GetAsync("/api/v1/services"));
        catalog.Status.Should().Be(200);
        // PERF-04's residue: this route used to answer a bare `[]` (the tenant's whole catalog, unbounded). An
        // unknown tenant is still an empty page, but now it is an empty page inside the envelope.
        catalog.HasField("items").Should().BeTrue("a paginated object, not a bare array");
        catalog.Body.GetProperty("total").GetInt32().Should().Be(0);
        catalog.Body.GetProperty("items").GetArrayLength().Should().Be(0,
            "an unknown tenant is an empty page, not a refusal - the guard is about resolution, not existence");

        var customers = await ReadAsync(await _factory
            .CreateClientForTenant(Guid.NewGuid(), Roles.Staff)
            .GetAsync("/api/v1/customers"));

        customers.Status.Should().Be(200);
        customers.ContentType.Should().Be("application/json");
        customers.HasField("items").Should().BeTrue("a paginated object, not a problem document");
        customers.HasField("detail").Should().BeFalse();
    }

    [Fact]
    public async Task TheHeaderCannotRescueAPrincipalWhoseTokenCarriesNoTenantClaim()
    {
        var client = TenantlessClient(Roles.Staff);
        client.DefaultRequestHeaders.Add("X-Tenant-Id", Guid.NewGuid().ToString());

        var refusal = await ReadAsync(await client.GetAsync("/api/v1/appointments"));

        // For an authenticated tenant user the tenant comes from the claim and only from the claim. So a header
        // naming some other tenant is not a way to reach it, and here it is not a way to fill the gap either: the
        // refusal is the same 400 the request would have got without the header.
        refusal.Status.Should().Be(400);
        refusal.Field("detail").Should().Be(TenantGuard.MissingTenant);
    }
}
