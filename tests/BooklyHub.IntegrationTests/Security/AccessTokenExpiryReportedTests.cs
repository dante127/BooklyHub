using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BooklyHub.Application.Security;
using BooklyHub.Domain.Entities.Identity;
using BooklyHub.Infrastructure.Data;
using BooklyHub.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BooklyHub.IntegrationTests.Security;

/// <summary>
/// EXP-01: both auth responses stated their own access-token lifetime — <c>_clock.UtcNow.AddMinutes(60)</c>, twice,
/// written in the controller — while the credential's real life came from <c>Jwt:ExpirationMinutes</c> two projects
/// away. The two agreed only because the shipped config happens to say 60, and nothing in the suite would have
/// noticed the day it said something else, even though a client schedules its next refresh from this number and a
/// verifier enforces that one. These facts read the answer back out of the token the same response handed out,
/// decoded here by hand so the test does not share the production code's way of parsing it.
/// </summary>
/// <remarks>
/// The pinned-clock fact is the load-bearing one: it makes the two sources disagree on purpose. With the clock at
/// 2031 and the token minted from real time, a response that recomputes rather than reports is caught by seconds
/// of skew that no untampered run would ever show.
/// </remarks>
internal static class AuthWire
{
    public const string Password = "correct horse battery staple";

    /// <summary>A clock nowhere near the one that minted the credential, so a recomputed expiry cannot pass.</summary>
    public static readonly DateTime Pinned = new(2031, 3, 1, 6, 30, 0, DateTimeKind.Utc);

    public static async Task<Guid> SeedUserAsync(BooklyHubWebApplicationFactory factory, string email)
    {
        var id = Guid.NewGuid();
        using var scope = factory.Services.CreateScope();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        db.Users.Add(new User
        {
            Id = id,
            Email = email,
            PasswordHash = hasher.HashPassword(Password),
            FirstName = "Expiry",
            LastName = "Reported",
            TenantId = null
        });

        await db.SaveChangesAsync();
        return id;
    }

    public sealed record Session(string AccessToken, string RefreshToken, DateTime ExpiresAtUtc);

    /// <summary>Seeds the account and signs into it, because a sign-in for an address that does not exist is a 401 about something else.</summary>
    public static async Task<Session> LoginAsync(BooklyHubWebApplicationFactory factory, string email)
    {
        await SeedUserAsync(factory, email);

        var response = await factory.CreateClient()
            .PostAsJsonAsync("/api/v1/auth/login", new { Email = email, Password });

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return Read(await response.Content.ReadAsStringAsync());
    }

    public static async Task<Session> RefreshAsync(BooklyHubWebApplicationFactory factory, string refreshToken)
    {
        var response = await factory.CreateClient()
            .PostAsJsonAsync("/api/v1/auth/refresh-token", new { RefreshToken = refreshToken });

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return Read(await response.Content.ReadAsStringAsync());
    }

    private static Session Read(string body)
    {
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;

        return new Session(
            root.GetProperty("accessToken").GetString()!,
            root.GetProperty("refreshToken").GetString()!,
            root.GetProperty("expiresAtUtc").GetDateTime());
    }

    /// <summary>
    /// exp as the token itself carries it: whole seconds since the epoch in the middle segment. Decoded by hand
    /// rather than with the JWT stack, so a wrong claim in the response cannot be confirmed by the same library
    /// that the production code used to write it. Measured while writing this: the payload this code mints carries
    /// <c>exp</c> and no <c>iat</c>, so a lifetime can only be judged against the window the request ran in.
    /// </summary>
    public static DateTime ExpiryFromToken(string accessToken)
    {
        var segments = accessToken.Split('.');
        segments.Should().HaveCount(3, "an access token is three base64url segments");

        var payload = segments[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');

        using var json = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
        var root = json.RootElement;

        if (!root.TryGetProperty("exp", out var value))
        {
            throw new InvalidOperationException($"The access token payload has no 'exp' claim. Payload was: {root.GetRawText()}");
        }

        return DateTimeOffset.FromUnixTimeSeconds(value.GetInt64()).UtcDateTime;
    }
}

public class AccessTokenExpiryReportedTests : IClassFixture<BooklyHubWebApplicationFactory>
{
    private readonly BooklyHubWebApplicationFactory _factory;

    public AccessTokenExpiryReportedTests(BooklyHubWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Login_MustReportTheExpiryWrittenIntoTheTokenItHandedOut()
    {
        var session = await AuthWire.LoginAsync(_factory, "exp01.login@example.test");

        // Exact, not approximate: exp is whole seconds and the response now carries that same instant, so any
        // difference at all means one side computed a lifetime instead of reporting one.
        session.ExpiresAtUtc.Should().Be(AuthWire.ExpiryFromToken(session.AccessToken),
            "the client is told when the credential stops working, and the credential is the only authority on that");
    }

    [Fact]
    public async Task Refresh_MustReportTheExpiryOfTheAccessTokenItJustMinted()
    {
        var first = await AuthWire.LoginAsync(_factory, "exp01.refresh@example.test");
        var rotated = await AuthWire.RefreshAsync(_factory, first.RefreshToken);

        // The rotation path is where a stale copy of the number hurts most: this is the response a client uses to
        // schedule the following refresh, and it describes a credential the login response never mentioned.
        rotated.ExpiresAtUtc.Should().Be(AuthWire.ExpiryFromToken(rotated.AccessToken),
            "a refresh hands out a new credential and the reported life must be that one's");
        rotated.AccessToken.Should().NotBe(first.AccessToken, "the rotation actually minted a new token to describe");
    }

    [Fact]
    public async Task ARecomputedExpiry_IsCaughtByPuttingTheTwoSourcesAgainstAClockTheyDoNotShare()
    {
        var email = "exp01.pinned@example.test";

        _factory.Clock.Pin(AuthWire.Pinned);
        try
        {
            var session = await AuthWire.LoginAsync(_factory, email);

            // The token is minted from the machine clock inside the issuer, so with the application clock at 2031
            // any answer derived from _clock.UtcNow is hours away from the instant the verifier will enforce.
            var tokenExpiry = AuthWire.ExpiryFromToken(session.AccessToken);

            session.ExpiresAtUtc.Should().Be(tokenExpiry,
                "the answer must come from the credential, not from a clock the credential was not minted against");
            Math.Abs((session.ExpiresAtUtc - AuthWire.Pinned).TotalDays).Should().BeGreaterThan(1,
                "and this is only evidence because the pinned clock is far enough away to make a recomputed answer wrong");

            // Nothing here depends on the pinned clock being plausible: the token still says what it says.
            tokenExpiry.Should().BeBefore(AuthWire.Pinned,
                "the minted credential is in the real-time past relative to the pin, which is what makes the two directions visible");
        }
        finally
        {
            _factory.Clock.Release();
        }
    }

    [Fact]
    public async Task TheLifetimeThatIsReported_MustBeTheConfiguredOne()
    {
        var email = "exp01.configured@example.test";

        // The issuer stamps exp off the machine clock and the payload carries no iat, so the configured lifetime is
        // judged against the window the request occupied rather than against a second claim. That is still the
        // question EXP-01 asks: does the number the client is told come from the setting or from a constant.
        var requestStarted = DateTime.UtcNow;
        var session = await AuthWire.LoginAsync(_factory, email);
        var requestFinished = DateTime.UtcNow;

        var configured = int.Parse(_factory.Services.GetRequiredService<IConfiguration>()["Jwt:ExpirationMinutes"]!);
        var expiry = AuthWire.ExpiryFromToken(session.AccessToken);

        expiry.Should().BeOnOrAfter(requestStarted.AddMinutes(configured).AddSeconds(-1),
            "exp is minted from the configured minutes, truncated to whole seconds");
        expiry.Should().BeBefore(requestFinished.AddMinutes(configured).AddSeconds(1));
        session.ExpiresAtUtc.Should().Be(expiry,
            "and the response reports that instant rather than its own copy of the setting");
    }
}
