using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BooklyHub.Application.Security;
using BooklyHub.Domain.Entities.Identity;
using BooklyHub.Infrastructure.Data;
using BooklyHub.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BooklyHub.IntegrationTests.Security;

/// <summary>
/// FAN-01: a sign-in mints a seven-day credential and, before this, nothing counted how many of them one account
/// ends up holding. Measured on the row count rather than argued: the tables only ever grew, and every live row is
/// an independent way back into the account that outlives the password it was issued beside. These three facts pin
/// the bound and, because a bound that bites the wrong thing is an outage, pin what it does <em>not</em> count:
/// a refresh is not a session, and a spent link in a chain is not a live credential.
/// </summary>
/// <remarks>
/// The clock is pinned and stepped one minute per request, so "the oldest live credential" is a fact the test
/// establishes instead of a race it hopes to win: every row gets a distinct, ordered <c>CreatedAtUtc</c> and
/// <c>ExpiresAtUtc</c>, and an eviction that picked by anything else would be visible in which row moved.
/// The auth tier is ten requests a minute partitioned per test host, so each class holds one fact and spends at
/// most nine permits; a sign-in helper that asserts it was not throttled makes the budget bite visible rather
/// than mysterious.
/// </remarks>
internal static class FanOut
{
    public const string Password = "correct horse battery staple";

    public static readonly DateTime Now = new(2026, 10, 4, 9, 0, 0, DateTimeKind.Utc);

    public static async Task<Guid> SeedAsync(BooklyHubWebApplicationFactory factory, string email)
    {
        var id = Guid.NewGuid();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        db.Users.Add(new User
        {
            Id = id,
            Email = email,
            PasswordHash = scope.ServiceProvider.GetRequiredService<IPasswordHasher>().HashPassword(Password),
            FirstName = "Many",
            LastName = "Chains",
            TenantId = null
        });

        await db.SaveChangesAsync();
        return id;
    }

    /// <summary>Costs the tier one permit. Returns the refresh credential the sign-in was handed.</summary>
    public static async Task<string> LoginAsync(BooklyHubWebApplicationFactory factory, string email)
    {
        var response = await factory.CreateClient()
            .PostAsJsonAsync("/api/v1/auth/login", new { Email = email, Password });

        response.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests, await response.Content.ReadAsStringAsync());
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("refreshToken").GetString()!;
    }

    public static async Task<List<RefreshToken>> RowsAsync(BooklyHubWebApplicationFactory factory, Guid userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.RefreshTokens.IgnoreQueryFilters()
            .Where(t => t.UserId == userId)
            .OrderBy(t => t.CreatedAtUtc)
            .ThenBy(t => t.Id)
            .ToListAsync();
    }

    public static int Live(List<RefreshToken> rows) => rows.Count(t => t.RevokedAtUtc == null);
}

public class FanOutWithinCapTests : IClassFixture<BooklyHubWebApplicationFactory>
{
    private readonly BooklyHubWebApplicationFactory _factory;

    public FanOutWithinCapTests(BooklyHubWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task SignInsWithinTheCap_MustAllKeepTheirSessions()
    {
        var email = "fan01.within@example.test";
        var userId = await FanOut.SeedAsync(_factory, email);

        _factory.Clock.Pin(FanOut.Now);
        try
        {
            for (var i = 0; i < SessionFanOutPolicy.MaxLiveSessions; i++)
            {
                await FanOut.LoginAsync(_factory, email);
                _factory.Clock.AdvanceBy(TimeSpan.FromMinutes(1));
            }

            var rows = await FanOut.RowsAsync(_factory, userId);

            // The cap is a ceiling, not a count to trim down to: five sign-ins on a five-session account lose
            // nothing. Enforcing the rule one request too early would sign a device out on an ordinary Tuesday,
            // and because an eviction is deliberately not announced, nobody would be told which one.
            rows.Should().HaveCount(SessionFanOutPolicy.MaxLiveSessions);
            FanOut.Live(rows).Should().Be(SessionFanOutPolicy.MaxLiveSessions,
                "reaching the cap is not passing it");
            rows.Should().OnlyContain(t => t.RevokedAtUtc == null, "no row is revoked while the account is inside the rule");
        }
        finally
        {
            _factory.Clock.Release();
        }
    }
}

public class FanOutCapTests : IClassFixture<BooklyHubWebApplicationFactory>
{
    private readonly BooklyHubWebApplicationFactory _factory;

    public FanOutCapTests(BooklyHubWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task ASignInPastTheCap_MustRevokeTheOldestLiveCredentialAndRefuseItAfterwards()
    {
        var email = "fan01.cap@example.test";
        var userId = await FanOut.SeedAsync(_factory, email);

        var issued = new List<string>();
        _factory.Clock.Pin(FanOut.Now);
        try
        {
            for (var i = 0; i <= SessionFanOutPolicy.MaxLiveSessions; i++)
            {
                issued.Add(await FanOut.LoginAsync(_factory, email));
                _factory.Clock.AdvanceBy(TimeSpan.FromMinutes(1));
            }

            var rows = await FanOut.RowsAsync(_factory, userId);
            var capDeadline = FanOut.Now.AddMinutes(SessionFanOutPolicy.MaxLiveSessions);

            rows.Should().HaveCount(6, "the bound revokes rows, it does not delete them");
            FanOut.Live(rows).Should().Be(SessionFanOutPolicy.MaxLiveSessions,
                "the sixth sign-in is the one that stops counting, not the one that gets refused");

            // Identity of the evicted row is asserted, not inferred from a count: the oldest live credential goes,
            // and the newest — the session the caller is holding right now — cannot.
            rows[0].RevokedAtUtc.Should().Be(capDeadline,
                "the eviction stamps the deadline of the sign-in that triggered it");
            rows[0].ReplacedByToken.Should().BeNull(
                "an ended session writes no successor link, so it cannot later read as a replayed credential");
            rows[5].RevokedAtUtc.Should().BeNull(
                "the sign-in that exceeded the cap is not the one the cap revokes");
            rows.Skip(1).Take(4).Should().OnlyContain(t => t.RevokedAtUtc == null,
                "exactly the overshoot is revoked, not a page of them");

            // The evicted credential is now worth nothing, and it says the ordinary words.
            var refused = await TokenReuse.Refresh(_factory, issued[0]);
            refused.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await refused.Content.ReadAsStringAsync())
                .Should().Contain("Invalid or expired refresh token.",
                    "the refusal is the one this endpoint already gives, so an eviction is not a new answer to read");

            var after = await FanOut.RowsAsync(_factory, userId);
            FanOut.Live(after).Should().Be(SessionFanOutPolicy.MaxLiveSessions,
                "replaying an evicted credential burns nothing behind it — it was a tip, and revoking it was a sign-out");
            after.Should().HaveCount(6, "and a refusal mints nothing");

            // 8 tier requests: six sign-ins and two refreshes.
            (await TokenReuse.Refresh(_factory, issued[5])).StatusCode.Should().Be(HttpStatusCode.OK,
                "the client that tripped the cap keeps the session it just opened");
        }
        finally
        {
            _factory.Clock.Release();
        }
    }
}

public class FanOutCountsLiveTipsTests : IClassFixture<BooklyHubWebApplicationFactory>
{
    private readonly BooklyHubWebApplicationFactory _factory;

    public FanOutCountsLiveTipsTests(BooklyHubWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task ALongChainAndFourShortOnes_AreFiveSessionsAndTheChainIsOneOfThem()
    {
        var email = "fan01.livetips@example.test";
        var userId = await FanOut.SeedAsync(_factory, email);

        _factory.Clock.Pin(FanOut.Now);
        try
        {
            // One device that refreshed twice, then four that only signed in.
            var chain = await FanOut.LoginAsync(_factory, email);
            _factory.Clock.AdvanceBy(TimeSpan.FromMinutes(1));
            chain = await TokenReuse.RotateAsync(_factory, chain);
            _factory.Clock.AdvanceBy(TimeSpan.FromMinutes(1));
            chain = await TokenReuse.RotateAsync(_factory, chain);

            for (var i = 0; i < SessionFanOutPolicy.MaxLiveSessions - 1; i++)
            {
                _factory.Clock.AdvanceBy(TimeSpan.FromMinutes(1));
                await FanOut.LoginAsync(_factory, email);
            }

            var before = await FanOut.RowsAsync(_factory, userId);
            before.Should().HaveCount(7, "two rotations wrote two links, and five sign-ins wrote five tips");
            FanOut.Live(before).Should().Be(SessionFanOutPolicy.MaxLiveSessions,
                "a refresh is the same session, so the chain is one live credential however many rows it left");

            // The sign-in that passes the cap must evict the oldest *live* credential. That is the chain's tip,
            // which is younger than the chain's own first row — and the two spent links behind it are not
            // candidates for anything, because they already end a session.
            _factory.Clock.AdvanceBy(TimeSpan.FromMinutes(1));
            var newest = await FanOut.LoginAsync(_factory, email);

            var after = await FanOut.RowsAsync(_factory, userId);

            // Eight rows in creation order, each stamped one minute apart: 0 and 1 are the chain's spent links,
            // 2 is the chain's tip, 3..6 are the four sign-ins, 7 is the sign-in that passed the cap.
            var deadline = FanOut.Now.AddMinutes(7);

            after.Should().HaveCount(8, "the bound revokes rows, it does not delete them");
            FanOut.Live(after).Should().Be(SessionFanOutPolicy.MaxLiveSessions,
                "the bound counts live credentials, never the rows a chain leaves behind");

            after[2].RevokedAtUtc.Should().Be(deadline,
                "the chain's tip is the oldest live credential, so it is the one that goes");
            after[2].ReplacedByToken.Should().BeNull("it leaves no successor link behind, like any ended session");

            // Its two spent links keep exactly what they had: the record of when honest use spent them.
            // Re-stamping either one would read as an eviction where there was none, and rewriting a successor link
            // would be SEC-05b's theft signal appearing on a row that was spent normally.
            after[0].RevokedAtUtc.Should().Be(FanOut.Now.AddMinutes(1));
            after[1].RevokedAtUtc.Should().Be(FanOut.Now.AddMinutes(2));
            after[0].ReplacedByToken.Should().NotBeNull();
            after[1].ReplacedByToken.Should().NotBeNull();

            after.Skip(3).Take(4).Should().OnlyContain(t => t.RevokedAtUtc == null,
                "the four other sign-ins are untouched by a bound that only needed one");

            CredentialRows.Digest(_factory, newest).Should().Be(after[7].Token,
                "the credential handed to the caller is the newest row, not one of the ones already on the account");
            after[7].RevokedAtUtc.Should().BeNull(
                "and the sign-in that tripped the cap is never the one revoked");

            (await TokenReuse.Refresh(_factory, newest)).StatusCode.Should().Be(HttpStatusCode.OK,
                "9 tier requests in this fact, and the session that just opened still redeems");
        }
        finally
        {
            _factory.Clock.Release();
        }
    }
}
