using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BooklyHub.Application.Security;
using BooklyHub.Domain.Entities.Identity;
using BooklyHub.Infrastructure.Data;
using BooklyHub.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BooklyHub.IntegrationTests.Security;

/// <summary>
/// PW-01: the product could set a password (once, by an operator, in a configuration file) and could not change
/// one. That leaves a leaked secret unfixable by the one party who can tell it leaked, and it leaves every account
/// sharing the seeded password until someone with database access intervenes. These tests pin the four things a
/// route like this can get wrong: that it accepts a caller who never proved the current secret, that it changes
/// the secret but leaves the sessions that were stolen with it alive, that it keeps a second, looser guess counter
/// behind the second door, and that it answers a malformed new password before it answers an unproven one — which
/// is an existence oracle and a free pass on the lockout in one shape.
/// </summary>
/// <remarks>
/// One fact per class where a fact needs the auth tier: the limit is ten requests a minute and its partition is
/// this fixture's own host, so a class holding two guessing runs would be sharing a permit budget with itself. The
/// authenticated client is minted offline by <see cref="BooklyHubWebApplicationFactory.CreateClientForTenant"/>,
/// which costs no permit — the requests counted here are the route's own POSTs and the logins that check them.
/// </remarks>
internal static class PasswordChange
{
    public const string Current = "correct horse battery staple";
    public const string Replacement = "a replacement only this route writes";
    public const string InvalidCredentials = "Invalid email or password.";

    public static async Task<Guid> SeedAsync(BooklyHubWebApplicationFactory factory, string email, bool isActive = true)
    {
        var id = Guid.NewGuid();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        db.Users.Add(new User
        {
            Id = id,
            Email = email,
            PasswordHash = scope.ServiceProvider.GetRequiredService<IPasswordHasher>().HashPassword(Current),
            FirstName = "Rotate",
            LastName = "Secret",
            IsActive = isActive,
            TenantId = null
        });

        await db.SaveChangesAsync();
        return id;
    }

    /// <summary>
    /// A session without spending a request for it. The token carries this user's id, so the route is reached as
    /// that account and nothing else — and the fact stays about the route rather than about the sign-in that got
    /// the caller in.
    /// </summary>
    public static HttpClient ClientFor(BooklyHubWebApplicationFactory factory, Guid userId) =>
        factory.CreateClientForTenant(Guid.NewGuid(), Roles.Staff, null, userId);

    public static Task<HttpResponseMessage> SendAsync(HttpClient client, string? current, string? @new) =>
        client.PostAsJsonAsync("/api/v1/auth/change-password", new { CurrentPassword = current, NewPassword = @new });

    public static Task<HttpResponseMessage> RefreshAsync(BooklyHubWebApplicationFactory factory, string token) =>
        factory.CreateClient().PostAsJsonAsync("/api/v1/auth/refresh-token", new { RefreshToken = token });

    /// <summary>Logs in and returns both halves of the session. Costs the tier one permit.</summary>
    public static async Task<(string AccessToken, string RefreshToken)> LoginAsync(
        BooklyHubWebApplicationFactory factory, string email, string password)
    {
        var response = await factory.CreateClient()
            .PostAsJsonAsync("/api/v1/auth/login", new { Email = email, Password = password });

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (json.RootElement.GetProperty("accessToken").GetString()!,
                json.RootElement.GetProperty("refreshToken").GetString()!);
    }

    public static async Task<HttpStatusCode> SignInStatusAsync(
        BooklyHubWebApplicationFactory factory, string email, string password)
    {
        var response = await factory.CreateClient()
            .PostAsJsonAsync("/api/v1/auth/login", new { Email = email, Password = password });

        response.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests, await response.Content.ReadAsStringAsync());
        return response.StatusCode;
    }

    public static async Task<HttpResponseMessage> GetAsync(BooklyHubWebApplicationFactory factory, string accessToken, string url)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return await client.GetAsync(url);
    }

    public static async Task<string> RotateAsync(BooklyHubWebApplicationFactory factory, string token)
    {
        var response = await RefreshAsync(factory, token);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("refreshToken").GetString()!;
    }

    public static async Task<Snapshot> ReadAsync(BooklyHubWebApplicationFactory factory, Guid userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

        var user = await db.Users.IgnoreQueryFilters().FirstAsync(u => u.Id == userId);

        var sessions = await db.RefreshTokens.IgnoreQueryFilters()
            .Where(t => t.UserId == userId)
            .ToListAsync();

        return new Snapshot(
            user.PasswordHash,
            hasher.VerifyPassword(Current, user.PasswordHash),
            user.FailedLoginCount,
            user.LockoutUntilUtc,
            user.LastModifiedBy,
            sessions.Count(t => t.RevokedAtUtc == null),
            sessions.Count);
    }

    public sealed record Snapshot(
        string PasswordHash,
        bool OldSecretStillVerifies,
        int FailedLoginCount,
        DateTime? LockoutUntilUtc,
        string? LastModifiedBy,
        int LiveSessions,
        int Sessions);
}

public class PasswordChangeRotatesTheSecretTests : IClassFixture<BooklyHubWebApplicationFactory>
{
    private readonly BooklyHubWebApplicationFactory _factory;

    public PasswordChangeRotatesTheSecretTests(BooklyHubWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task TheNewPassword_MustBeTheOnlyOneThatSignsInAfterwards()
    {
        var email = "pw01.rotate@example.test";
        var userId = await PasswordChange.SeedAsync(_factory, email);
        var before = await PasswordChange.ReadAsync(_factory, userId);

        var response = await PasswordChange.SendAsync(
            PasswordChange.ClientFor(_factory, userId), PasswordChange.Current, PasswordChange.Replacement);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync());

        // Read before the sign-ins below: LastModifiedBy is the last writer's name, and a login that got in is a
        // writer. Asserting it afterwards would only ever prove which order the test happens to read in.
        var after = await PasswordChange.ReadAsync(_factory, userId);
        after.PasswordHash.Should().NotBe(before.PasswordHash);
        after.OldSecretStillVerifies.Should().BeFalse(
            "the stored hash is compared against, not read back, so this is the only shape the check can take");
        after.LastModifiedBy.Should().Be("auth:change-password");
        after.LiveSessions.Should().Be(0, "no session was ever minted for this account, so nothing was revoked");

        (await PasswordChange.SignInStatusAsync(_factory, email, PasswordChange.Replacement))
            .Should().Be(HttpStatusCode.OK, "the secret the caller asked for is the secret the account now has");
        (await PasswordChange.SignInStatusAsync(_factory, email, PasswordChange.Current))
            .Should().Be(HttpStatusCode.Unauthorized, "the old secret is not a second key to the same door");
    }
}

public class PasswordChangeSessionRevocationTests : IClassFixture<BooklyHubWebApplicationFactory>
{
    private readonly BooklyHubWebApplicationFactory _factory;

    public PasswordChangeSessionRevocationTests(BooklyHubWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task EverySessionTheAccountHolds_MustStopRefreshingTheMomentTheSecretChanges()
    {
        var email = "pw01.sessions@example.test";
        var userId = await PasswordChange.SeedAsync(_factory, email);

        // Two independent chains, which is what two devices look like. The second one then rotates, so the row
        // that is live at the far end of a chain is covered by the same rule as the row that was never used.
        var deviceA = await PasswordChange.LoginAsync(_factory, email, PasswordChange.Current);
        var deviceB = await PasswordChange.LoginAsync(_factory, email, PasswordChange.Current);
        var tipB = await PasswordChange.RotateAsync(_factory, deviceB.RefreshToken);

        (await PasswordChange.GetAsync(_factory, deviceA.AccessToken, "/api/v1/auth/me"))
            .StatusCode.Should().Be(HttpStatusCode.OK, "this fact is about sessions, not about calls in flight");

        (await PasswordChange.SendAsync(
            PasswordChange.ClientFor(_factory, userId), PasswordChange.Current, PasswordChange.Replacement))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await PasswordChange.RefreshAsync(_factory, deviceA.RefreshToken))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized,
                "the whole point is that whoever held the old password stops being able to renew");
        (await PasswordChange.RefreshAsync(_factory, tipB))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized,
                "a credential minted after the other login is still a session this account holds");

        var after = await PasswordChange.ReadAsync(_factory, userId);
        after.LiveSessions.Should().Be(0);
        after.Sessions.Should().Be(3, "nothing is deleted here; a revoked row is still the record of a session");

        // What the change does not do: reach backwards. The access token device A still holds is a signed promise
        // with its own expiry, and this route has no way to un-sign it. Asserting 200 keeps the documented limit
        // honest instead of letting the wording imply a revocation that only looks complete.
        (await PasswordChange.GetAsync(_factory, deviceA.AccessToken, "/api/v1/auth/me"))
            .StatusCode.Should().Be(HttpStatusCode.OK,
                "the route revokes refresh credentials; Jwt:ExpirationMinutes is what ends a call already signed");
    }
}

public class PasswordChangeProofTests : IClassFixture<BooklyHubWebApplicationFactory>
{
    private readonly BooklyHubWebApplicationFactory _factory;

    public PasswordChangeProofTests(BooklyHubWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task AWrongCurrentPassword_MustRefuseInTheSameWordsAsAnyOtherRefusalAndChangeNothing()
    {
        var email = "pw01.proof@example.test";
        var userId = await PasswordChange.SeedAsync(_factory, email);
        var client = PasswordChange.ClientFor(_factory, userId);

        var wrong = await PasswordChange.SendAsync(client, "not the password", PasswordChange.Replacement);
        var blank = await PasswordChange.SendAsync(client, "", PasswordChange.Replacement);

        foreach (var response in new[] { wrong, blank })
        {
            var problem = await ProblemEnvelope.ReadAsync(response, StatusCodes.Status401Unauthorized);

            // One text for wrong-secret and vanished-account is the same collapse SEC-04(c) bought on login: a
            // route that answered 404 for one of them would let anyone with a token enumerate which ids are real.
            ProblemEnvelope.Field(problem, "title").Should().Be("Unauthorized");
            ProblemEnvelope.Field(problem, "instance").Should().Be("/api/v1/auth/change-password");
            ProblemEnvelope.Field(problem, "detail").Should().Be(PasswordChange.InvalidCredentials);
        }

        // A refusal that echoes the offered secret hands the guesser back their own probe as confirmation.
        (await wrong.Content.ReadAsStringAsync()).Should().NotContain(PasswordChange.Replacement);

        var after = await PasswordChange.ReadAsync(_factory, userId);
        after.OldSecretStillVerifies.Should().BeTrue();
        after.FailedLoginCount.Should().Be(2, "both refusals were wrong-password events, and they are counted");
    }
}

public class PasswordChangeOrderingTests : IClassFixture<BooklyHubWebApplicationFactory>
{
    private readonly BooklyHubWebApplicationFactory _factory;

    public PasswordChangeOrderingTests(BooklyHubWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task TheNewPassword_MustBeJudgedAfterTheCurrentOneIsProven()
    {
        var email = "pw01.order@example.test";
        var userId = await PasswordChange.SeedAsync(_factory, email);
        var client = PasswordChange.ClientFor(_factory, userId);

        // The probe: the door reached with a bad secret and a malformed new one. Answering 400 here would be two
        // leaks — the account's existence, and a way to knock without ever building the streak.
        var unproven = await PasswordChange.SendAsync(client, "not the password", "short");
        unproven.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "a caller who has not proven the secret gets the same refusal as any other, not a form hint");

        var afterProbe = await PasswordChange.ReadAsync(_factory, userId);
        afterProbe.FailedLoginCount.Should().Be(1, "the probe is a password failure whatever else it is");

        // The same body from the account itself is a 400, because the caller has already proven they own it and the
        // wording is for them. It is not a password failure: the streak stays where the probe left it.
        var malformed = await PasswordChange.SendAsync(client, PasswordChange.Current, "short");
        malformed.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var problem = await ProblemEnvelope.ReadAsync(malformed, StatusCodes.Status400BadRequest);
        ProblemEnvelope.Field(problem, "title").Should().Be("Bad Request");
        ProblemEnvelope.Field(problem, "detail").Should()
            .Be($"The new password must be at least {PasswordPolicy.MinLength} characters.");

        var after = await PasswordChange.ReadAsync(_factory, userId);
        after.OldSecretStillVerifies.Should().BeTrue();
        after.FailedLoginCount.Should().Be(1, "a shape refusal is not a guess about the current secret");
    }
}

public class PasswordChangeStreakTests : IClassFixture<BooklyHubWebApplicationFactory>
{
    private readonly BooklyHubWebApplicationFactory _factory;

    public PasswordChangeStreakTests(BooklyHubWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task FailuresAtThisDoor_MustUseTheStreakTheOtherDoorKeeps()
    {
        var email = "pw01.streak@example.test";
        var userId = await PasswordChange.SeedAsync(_factory, email);
        var client = PasswordChange.ClientFor(_factory, userId);

        _factory.Clock.Pin(LoginAttempt.Now);
        try
        {
            for (var i = 1; i <= LoginLockoutPolicy.MaxFailedAttempts; i++)
            {
                var attempt = await PasswordChange.SendAsync(client, $"wrong-{i}", PasswordChange.Replacement);
                attempt.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            }

            var locked = await PasswordChange.ReadAsync(_factory, userId);
            locked.FailedLoginCount.Should().Be(LoginLockoutPolicy.MaxFailedAttempts,
                "five wrong secrets at one door is the same run as five at the other");
            locked.LockoutUntilUtc.Should().Be(LoginAttempt.Now + LoginLockoutPolicy.LockoutDuration);

            // The proof the counter is shared rather than parallel: the correct password, at the login door, is
            // now refused by a deadline this route opened. A second streak here would have left this answer 200.
            (await PasswordChange.SignInStatusAsync(_factory, email, PasswordChange.Current))
                .Should().Be(HttpStatusCode.Unauthorized,
                    "the threshold protects the account, not the route that happens to enforce it");
        }
        finally
        {
            _factory.Clock.Release();
        }
    }
}

public class PasswordChangeAuthenticationTests : IClassFixture<BooklyHubWebApplicationFactory>
{
    private readonly BooklyHubWebApplicationFactory _factory;

    public PasswordChangeAuthenticationTests(BooklyHubWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task ACallWithNoToken_MustBeRefusedByThePipelineAndTouchNothing()
    {
        var email = "pw01.anonymous@example.test";
        var userId = await PasswordChange.SeedAsync(_factory, email);

        var response = await PasswordChange.SendAsync(
            _factory.CreateClient(), PasswordChange.Current, PasswordChange.Replacement);

        var problem = await ProblemEnvelope.ReadAsync(response, StatusCodes.Status401Unauthorized);
        ProblemEnvelope.Field(problem, "title").Should().Be("Unauthorized");

        var after = await PasswordChange.ReadAsync(_factory, userId);
        after.OldSecretStillVerifies.Should().BeTrue();
        after.FailedLoginCount.Should().Be(0,
            "nobody named an account, so there is no account to escalate a failure against");
    }

    [Fact]
    public async Task ATokenWhoseAccountNoLongerExists_MustRefuseLikeAWrongPassword()
    {
        var orphaned = Guid.NewGuid();
        var response = await PasswordChange.SendAsync(
            PasswordChange.ClientFor(_factory, orphaned), PasswordChange.Current, PasswordChange.Replacement);

        // A 404 here would say "this token is valid but its user is gone", which is a second, quieter existence
        // oracle over ids; a 500 would be the same read arriving as an unhandled null.
        var problem = await ProblemEnvelope.ReadAsync(response, StatusCodes.Status401Unauthorized);
        ProblemEnvelope.Field(problem, "detail").Should().Be(PasswordChange.InvalidCredentials);
    }

    [Fact]
    public async Task ADeactivatedAccount_MustNotBeAbleToRotateItsOwnSecret()
    {
        var email = "pw01.deactivated@example.test";
        var userId = await PasswordChange.SeedAsync(_factory, email, isActive: false);

        var response = await PasswordChange.SendAsync(
            PasswordChange.ClientFor(_factory, userId), PasswordChange.Current, PasswordChange.Replacement);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // The gate is the same one ACT-01 put on refreshing: deactivating an account is meant to stop it, and a
        // route that let it rotate its secret would hand the owner a way back in around their own switch.
        (await PasswordChange.ReadAsync(_factory, userId)).OldSecretStillVerifies.Should().BeTrue();
    }
}
