using System.Net;
using System.Net.Http.Json;
using BooklyHub.Api.Controllers;
using BooklyHub.Application.Security;
using BooklyHub.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace BooklyHub.IntegrationTests.Security;

/// <summary>
/// SEC-04's rate-limit half. docs/SECURITY.md 3.1 promised ten login attempts per minute per address and the
/// only limiter in the pipeline was the general one, so the measured behaviour was fifteen consecutive failed
/// logins answered 401, 401, 401 — nothing paced a guesser at all. One walk per host, because the limiter's
/// partition is the test host itself: a second fact in this class would start inside an exhausted tier.
/// </summary>
public class AuthRateLimitTests : IClassFixture<BooklyHubWebApplicationFactory>
{
    private readonly BooklyHubWebApplicationFactory _factory;

    public AuthRateLimitTests(BooklyHubWebApplicationFactory factory) => _factory = factory;

    private static async Task<HttpResponseMessage> AttemptLoginAsync(HttpClient client) =>
        await client.PostAsJsonAsync("/api/v1/auth/login",
            new AuthController.LoginRequest("guesser@nowhere.test", "hunter2"));

    [Fact]
    public async Task FailedLogins_HitATierOfTheirOwnAtElevenAttempts()
    {
        var client = _factory.CreateClient();

        var attempts = new List<int>();
        for (var i = 0; i < 10; i++)
        {
            attempts.Add((int)(await AttemptLoginAsync(client)).StatusCode);
        }

        attempts.Should().OnlyContain(s => s == StatusCodes.Status401Unauthorized,
            "the tier has to let ten attempts reach the handler — judging a caller before that would refuse " +
            "traffic the documented budget says is allowed");

        var refused = await AttemptLoginAsync(client);

        refused.StatusCode.Should().Be(HttpStatusCode.TooManyRequests,
            "eleven wrong passwords from one address inside a minute is the pattern the tier exists to stop; " +
            "before it existed this attempt was answered 401 like the ten before it");

        var problem = await ProblemEnvelope.ReadAsync(refused, StatusCodes.Status429TooManyRequests);
        ProblemEnvelope.Field(problem, "title").Should().Be("Too Many Requests",
            "a caller that is being rate-limited has to be able to tell that apart from a wrong password, " +
            "which is the one thing a bare status code never said");

        refused.Headers.TryGetValues("Retry-After", out var values).Should().BeTrue(
            "a refusal with no deadline sends the caller straight back into the wall, and every rejection in " +
            "the measurement came without one");
        int.Parse(values!.Single()).Should().BeInRange(1, 60,
            "the window is one minute, so a hint outside that range is something a caller cannot act on");

        // The tier is its own partition: eleven refused attempts spent ten of the shared hundred permits, so a
        // caller on another endpoint must still be judged by authentication and not by somebody else's burst.
        (await client.GetAsync("/api/v1/appointments")).StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "the refusal above came from a tier of ten, not from exhausting the shared budget of a hundred");

        // A guessed refresh token is the other half of the same attack, and the shared budget still has
        // ~85 permits left here, so a refusal on this route can only have come from the tier.
        var refresh = await client.PostAsJsonAsync("/api/v1/auth/refresh-token",
            new AuthController.RefreshTokenRequest("not-a-real-token"));
        refresh.StatusCode.Should().Be(HttpStatusCode.TooManyRequests,
            "the tier covers the refresh route too; without it a token guesser gets the full hundred per minute");
    }
}
