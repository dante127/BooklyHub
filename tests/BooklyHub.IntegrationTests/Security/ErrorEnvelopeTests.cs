using System.Net;
using System.Text.Json;
using BooklyHub.Application.Security;
using BooklyHub.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace BooklyHub.IntegrationTests.Security;

/// <summary>Reads a failure the way a client does: body first, content type second.</summary>
internal static class ProblemEnvelope
{
    public static async Task<JsonElement> ReadAsync(HttpResponseMessage response, int status)
    {
        var body = await response.Content.ReadAsStringAsync();

        // The order matters as evidence: a response can carry application/problem+json while being empty,
        // which is what a client's JSON parser then chokes on.
        body.Should().NotBeEmpty($"a {status} with no body is indistinguishable from a dropped connection");
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json",
            $"a {status} has to parse as the same document every other failure returns");

        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    public static string CorrelationIdOf(HttpResponseMessage response)
    {
        response.Headers.TryGetValues("X-Correlation-Id", out var values).Should().BeTrue(
            "the correlation id header is what the body's copy is checked against");
        return values!.Single();
    }

    /// <summary>Names the missing field in the failure text, which a raw GetProperty does not.</summary>
    public static string Field(JsonElement problem, string field)
    {
        problem.TryGetProperty(field, out var value).Should().BeTrue(
            $"the envelope must carry {field}, and it does not:\n{problem.GetRawText()}");
        return value.GetString()!;
    }
}

/// <summary>
/// SEC-10's real residue: a status code the pipeline sets without an action running used to travel as a
/// bare number — no content type, no body — while everything thrown inside an action carried a problem
/// document. These are the statuses that made the difference observable.
/// </summary>
public class ErrorEnvelopeTests : IClassFixture<BooklyHubWebApplicationFactory>
{
    private static readonly string[] CoreFields = { "title", "status", "instance", "correlationId" };

    private readonly BooklyHubWebApplicationFactory _factory;

    public ErrorEnvelopeTests(BooklyHubWebApplicationFactory factory) => _factory = factory;

    private async Task AssertEnvelopeAsync(Func<Task<HttpResponseMessage>> send, int status, string title, string instance)
    {
        var response = await send();

        response.StatusCode.Should().Be((HttpStatusCode)status);

        var problem = await ProblemEnvelope.ReadAsync(response, status);
        problem.GetProperty("status").GetInt32().Should().Be(status);
        ProblemEnvelope.Field(problem, "title").Should().Be(title);
        ProblemEnvelope.Field(problem, "instance").Should().Be(instance);
        ProblemEnvelope.Field(problem, "correlationId").Should().Be(ProblemEnvelope.CorrelationIdOf(response),
            "a reported denial is otherwise unfindable in the logs");
    }

    [Fact]
    public async Task UnauthorizedChallenge_MustCarryTheProblemEnvelope()
    {
        await AssertEnvelopeAsync(() => _factory.CreateClient().GetAsync("/api/v1/appointments"),
            StatusCodes.Status401Unauthorized, "Unauthorized", "/api/v1/appointments");
    }

    [Fact]
    public async Task PermissionDenial_MustCarryTheProblemEnvelope()
    {
        // Staff is a role that exists and authenticates cleanly; only the permission check refuses it,
        // so this is the authorization handler's 403 and not the JWT challenge's 401 in disguise.
        var client = _factory.CreateClientForTenant(Guid.NewGuid(), Roles.Staff);
        await AssertEnvelopeAsync(() => client.GetAsync("/api/v1/reports/dashboard"),
            StatusCodes.Status403Forbidden, "Forbidden", "/api/v1/reports/dashboard");
    }

    [Fact]
    public async Task UnmatchedRoute_MustCarryTheProblemEnvelope()
    {
        await AssertEnvelopeAsync(() => _factory.CreateClient().GetAsync("/api/v1/there-is-no-such-route"),
            StatusCodes.Status404NotFound, "Not Found", "/api/v1/there-is-no-such-route");
    }

    [Fact]
    public async Task MissingAppointmentFromTheActionPath_MustStillExplainItself()
    {
        var client = _factory.CreateClientForTenant(Guid.NewGuid(), Roles.TenantOwner);
        var url = $"/api/v1/appointments/{Guid.NewGuid()}";

        var response = await client.GetAsync(url);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var problem = await ProblemEnvelope.ReadAsync(response, StatusCodes.Status404NotFound);
        // The action path knows which appointment it looked for, which is the half a client can act on; the
        // pipeline path has no such detail to give. The fields both shapes do owe are asserted separately.
        ProblemEnvelope.Field(problem, "detail").Should().Contain("was not found");
        ProblemEnvelope.Field(problem, "title").Should().Be("Resource Not Found");
    }

    [Fact]
    public async Task BothErrorPaths_MustExposeTheSameCoreFields()
    {
        var challenge = await ProblemEnvelope.ReadAsync(
            await _factory.CreateClient().GetAsync("/api/v1/appointments"), StatusCodes.Status401Unauthorized);
        var owner = _factory.CreateClientForTenant(Guid.NewGuid(), Roles.TenantOwner);
        var thrown = await ProblemEnvelope.ReadAsync(
            await owner.GetAsync($"/api/v1/appointments/{Guid.NewGuid()}"), StatusCodes.Status404NotFound);

        foreach (var (label, problem) in new[] { ("challenge", challenge), ("exception", thrown) })
        {
            var present = CoreFields.Where(field => problem.TryGetProperty(field, out _)).ToList();
            present.Should().Equal(CoreFields,
                $"{label} must answer with all of the fields the other path answers with, or a client has to " +
                "parse two shapes for one failure");
        }
    }

    [Fact]
    public async Task ASuccessfulList_MustNotBeTurnedIntoAProblemDocument()
    {
        var client = _factory.CreateClientForTenant(Guid.NewGuid(), Roles.TenantOwner);

        var response = await client.GetAsync("/api/v1/appointments");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json",
            "the envelope is for failures; wrapping what a page renders would be a new bug, not a fix");

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("items").GetArrayLength().Should().Be(0);
    }
}

/// <summary>
/// The rate limiter rejection is a third status nobody writes a body for, and it is the one the framework's
/// own status table forgets: it arrives with a status and no title. It gets its own class because the limiter
/// partitions by caller address, which is shared for every request this host sees.
/// </summary>
public class RateLimitEnvelopeTests : IClassFixture<BooklyHubWebApplicationFactory>
{
    private readonly BooklyHubWebApplicationFactory _factory;

    public RateLimitEnvelopeTests(BooklyHubWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task RateLimitRejection_MustStillNameWhatHappened()
    {
        using var client = _factory.CreateClient();

        // 100 permits per window plus a 10-deep queue, so a burst this far over the budget cannot miss the
        // rejection even if the window replenishes partway through it. The target is a protected endpoint
        // because that is where the rejection is observable: on an unmatched route the framework fills the
        // title in by itself, and the test would pass with the fallback removed.
        var responses = await Task.WhenAll(Enumerable.Range(0, 250)
            .Select(_ => client.GetAsync("/api/v1/appointments")));

        var rejected = responses.FirstOrDefault(r => r.StatusCode == HttpStatusCode.TooManyRequests);

        rejected.Should().NotBeNull("the burst has to outrun the limiter or this test asserts nothing");

        var problem = await ProblemEnvelope.ReadAsync(rejected!, StatusCodes.Status429TooManyRequests);
        problem.GetProperty("status").GetInt32().Should().Be(StatusCodes.Status429TooManyRequests);
        ProblemEnvelope.Field(problem, "title").Should().Be("Too Many Requests",
            "the framework's status-to-problem table has no entry for 429, so without the fallback the " +
            "client is told a number and nothing else");
        ProblemEnvelope.Field(problem, "correlationId").Should().Be(ProblemEnvelope.CorrelationIdOf(rejected!));
    }
}
