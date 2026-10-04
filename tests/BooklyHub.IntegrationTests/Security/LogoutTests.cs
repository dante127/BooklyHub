using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BooklyHub.Domain.Entities.Identity;
using BooklyHub.Infrastructure.Data;
using BooklyHub.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BooklyHub.IntegrationTests.Security;

/// <summary>
/// SEC-05c: the product had no way to stop a refresh credential it had already issued. Four endpoints for it were
/// probed (<c>logout</c>, <c>revoke</c>, <c>sessions</c>, <c>tokens</c>) and all answered <c>404</c>, so the only
/// thing a user could do with a session was wait seven days for it. These facts pin what <c>/auth/logout</c>
/// revokes, what it refuses to do to the rows around it, and that it tells a caller nothing about which
/// credentials exist.
/// </summary>
internal static class LogoutProbe
{
    public static async Task<HttpResponseMessage> Logout(BooklyHubWebApplicationFactory factory, string token) =>
        await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/logout", new { RefreshToken = token });

    public static async Task<List<RefreshToken>> RowsAsync(
        BooklyHubWebApplicationFactory factory, params string[] storedValues)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.RefreshTokens.IgnoreQueryFilters()
            .Where(t => storedValues.Contains(t.Token))
            .ToListAsync();
    }

    public static string Digest(string credential) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(credential))).ToLowerInvariant();
}

public class LogoutRevokesTests : IClassFixture<BooklyHubWebApplicationFactory>
{
    private readonly BooklyHubWebApplicationFactory _factory;

    public LogoutRevokesTests(BooklyHubWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task ATokenThatHasBeenLoggedOut_MustBuyNothingAndRecordNoSuccessor()
    {
        var (userId, token) = await TokenReuse.LoginAsync(_factory, "sec05c.logout.works@example.test");

        var response = await LogoutProbe.Logout(_factory, token);
        response.StatusCode.Should().Be(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync());

        (await TokenReuse.Refresh(_factory, token)).StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "a credential its owner ended buys nothing, however far inside its seven days it still is");

        var counts = await TokenReuse.CountsAsync(_factory, userId);
        counts.Live.Should().Be(0);
        counts.Rows.Should().Be(1, "ending a session revokes the row it was given and mints nothing around it");

        var rows = await LogoutProbe.RowsAsync(_factory, LogoutProbe.Digest(token));
        rows.Should().ContainSingle("the row is still there, keyed by its digest");
        rows[0].RevokedAtUtc.Should().NotBeNull();

        // A logout is an ending, not a rotation: writing a successor here would make the owner's own sign-out read
        // as a reuse signal and burn whatever the client rotated forward to.
        rows[0].ReplacedByToken.Should().BeNull("nothing replaced this credential, so nothing may claim to");
    }
}

public class LogoutScopeTests : IClassFixture<BooklyHubWebApplicationFactory>
{
    private readonly BooklyHubWebApplicationFactory _factory;

    public LogoutScopeTests(BooklyHubWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task LoggingOutOneSession_MustLeaveTheAccountHeldStanding()
    {
        // Two sign-ins are two parallel chains for one user (`FAN-01`), and ending one is what a user signing out
        // of a device asks for. Revoking the user's every row would turn a sign-out on a shared phone into a
        // lockout of their own account, so this fact is the direction guard on the burn's sibling.
        var (userId, first) = await TokenReuse.LoginAsync(_factory, "sec05c.scope@example.test");
        var secondLogin = await _factory.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new
        {
            Email = "sec05c.scope@example.test",
            Password = "correct horse battery staple"
        });
        secondLogin.StatusCode.Should().Be(HttpStatusCode.OK, await secondLogin.Content.ReadAsStringAsync());
        string second;
        using (var json = JsonDocument.Parse(await secondLogin.Content.ReadAsStringAsync()))
            second = json.RootElement.GetProperty("refreshToken").GetString()!;

        (await LogoutProbe.Logout(_factory, first)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await TokenReuse.Refresh(_factory, first)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var standing = await TokenReuse.Refresh(_factory, second);
        standing.StatusCode.Should().Be(HttpStatusCode.OK,
            "the other session belongs to the same person, who did not ask to end it");

        var counts = await TokenReuse.CountsAsync(_factory, userId);
        counts.Rows.Should().Be(3, "one row ended, one row rotated, nothing else touched");
        counts.Live.Should().Be(1);
    }
}

public class LogoutAnswerShapeTests : IClassFixture<BooklyHubWebApplicationFactory>
{
    private readonly BooklyHubWebApplicationFactory _factory;

    public LogoutAnswerShapeTests(BooklyHubWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task ACredentialThatWasNeverIssued_MustBeAnsweredExactlyLikeALiveSession()
    {
        var (userId, token) = await TokenReuse.LoginAsync(_factory, "sec05c.shape@example.test");

        var stranger = await LogoutProbe.Logout(_factory, "a-string-nothing-issued");
        var real = await LogoutProbe.Logout(_factory, token);

        // An endpoint that answers 204 for a session and 404 for a string that is not one is a way to ask the
        // database whether a credential exists, which is the same oracle SEC-05 spent three commits closing.
        stranger.StatusCode.Should().Be(HttpStatusCode.NoContent);
        real.StatusCode.Should().Be(HttpStatusCode.NoContent);
        real.StatusCode.Should().Be(stranger.StatusCode, "the two cases must not be distinguishable");
        (await stranger.Content.ReadAsStringAsync()).Should().BeEmpty();
        (await real.Content.ReadAsStringAsync()).Should().BeEmpty();

        // And it is idempotent: knocking twice with an ended credential changes no row and mints nothing.
        var again = await LogoutProbe.Logout(_factory, token);
        again.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var counts = await TokenReuse.CountsAsync(_factory, userId);
        counts.Rows.Should().Be(1, "the never-issued call and the repeat call both wrote nothing");
        counts.Live.Should().Be(0);
    }
}

public class LogoutLegacyRowTests : IClassFixture<BooklyHubWebApplicationFactory>
{
    private readonly BooklyHubWebApplicationFactory _factory;

    public LogoutLegacyRowTests(BooklyHubWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task ALegacyPlaintextRow_WhenLoggedOut_MustBeRevokedAndLeaveNoPlaintextBehind()
    {
        var (userId, _) = await TokenReuse.LoginAsync(_factory, "sec05c.legacy@example.test");

        // The row shape the code before SEC-05a wrote: the credential itself, stored as presented.
        var legacyRaw = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.RefreshTokens.Add(new RefreshToken
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Token = legacyRaw,
                ExpiresAtUtc = _factory.Clock.UtcNow.AddDays(7),
                CreatedAtUtc = _factory.Clock.UtcNow
            });
            await db.SaveChangesAsync();
        }

        (await LogoutProbe.Logout(_factory, legacyRaw)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var rows = await LogoutProbe.RowsAsync(_factory, LogoutProbe.Digest(legacyRaw));
        rows.Should().ContainSingle("a logout reads a legacy row and rewrites it as a digest on the way out");
        rows[0].RevokedAtUtc.Should().NotBeNull();
        (await LogoutProbe.RowsAsync(_factory, legacyRaw)).Should().BeEmpty(
            "ending a session must not leave the credential that authorized it in the table it just touched");
    }

    [Fact]
    public async Task AnAlreadyRevokedPlaintextRow_MustStillBeRewrittenWhenItIsKnockedOn()
    {
        var (userId, _) = await TokenReuse.LoginAsync(_factory, "sec05c.legacy2@example.test");

        // The write that ends a session is skipped here — the row is already ended — and the upgrade still has to
        // reach the database, or a plaintext credential survives because its owner signed out twice.
        var legacyRaw = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.RefreshTokens.Add(new RefreshToken
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Token = legacyRaw,
                ExpiresAtUtc = _factory.Clock.UtcNow.AddDays(7),
                CreatedAtUtc = _factory.Clock.UtcNow,
                RevokedAtUtc = _factory.Clock.UtcNow
            });
            await db.SaveChangesAsync();
        }

        (await LogoutProbe.Logout(_factory, legacyRaw)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await LogoutProbe.RowsAsync(_factory, legacyRaw)).Should().BeEmpty(
            "a row that had nothing left to revoke still left the call with its credential digested");
        (await LogoutProbe.RowsAsync(_factory, LogoutProbe.Digest(legacyRaw))).Should().ContainSingle();
    }
}
