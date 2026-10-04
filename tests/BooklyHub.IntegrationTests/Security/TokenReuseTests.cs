using System.Net;
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
/// SEC-05b: the table already records which credential replaced which, so a spent credential being offered a
/// second time is a signal the code can read. Refusing it and moving on throws that signal away — whoever holds
/// the old copy keeps the rest of the chain. These tests replay spent credentials and require the later links to
/// stop working, require the answer to stay as uninformative as any other refusal, and require a chain in normal
/// use to keep working.
/// </summary>
/// <remarks>
/// One fact per class: the auth tier is ten requests per minute and its partition is the test host, so a class
/// holding several rotation sequences would be sharing a permit budget with itself.
/// </remarks>
internal static class TokenReuse
{
    private const string Password = "correct horse battery staple";

    public static async Task<(Guid UserId, string Token)> LoginAsync(BooklyHubWebApplicationFactory factory, string email)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

        var userId = Guid.NewGuid();
        db.Users.Add(new User
        {
            Id = userId,
            Email = email,
            PasswordHash = hasher.HashPassword(Password),
            FirstName = "Chain",
            LastName = "Burn",
            TenantId = null
        });
        await db.SaveChangesAsync();

        var response = await factory.CreateClient()
            .PostAsJsonAsync("/api/v1/auth/login", new { Email = email, Password });

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (userId, json.RootElement.GetProperty("refreshToken").GetString()!);
    }

    public static Task<HttpResponseMessage> Refresh(BooklyHubWebApplicationFactory factory, string token) =>
        factory.CreateClient().PostAsJsonAsync("/api/v1/auth/refresh-token", new { RefreshToken = token });

    public static async Task<string> RotateAsync(BooklyHubWebApplicationFactory factory, string token)
    {
        var response = await Refresh(factory, token);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("refreshToken").GetString()!;
    }

    public static async Task<(int Rows, int Live)> CountsAsync(BooklyHubWebApplicationFactory factory, Guid userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var now = factory.Clock.UtcNow;

        var rows = await db.RefreshTokens.IgnoreQueryFilters().CountAsync(t => t.UserId == userId);
        var live = await db.RefreshTokens.IgnoreQueryFilters()
            .CountAsync(t => t.UserId == userId && t.RevokedAtUtc == null && t.ExpiresAtUtc > now);

        return (rows, live);
    }
}

public class TokenReuseBurnTests : IClassFixture<BooklyHubWebApplicationFactory>
{
    private readonly BooklyHubWebApplicationFactory _factory;

    public TokenReuseBurnTests(BooklyHubWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task AReplayedSpentCredential_MustBurnEveryLaterLinkInItsChain()
    {
        var (userId, head) = await TokenReuse.LoginAsync(_factory, "sec05b.burn@example.test");
        var second = await TokenReuse.RotateAsync(_factory, head);
        var third = await TokenReuse.RotateAsync(_factory, second);

        (await TokenReuse.Refresh(_factory, head)).StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "a credential the rotation already consumed buys nothing a second time");

        // Measured before any later link is presented, so this is the replay's own doing and nothing else's. The
        // link `head` points at was already revoked by honest rotation, so reaching `third` means walking through
        // a revoked row — which is the only shape a stolen old copy ever arrives in.
        var counts = await TokenReuse.CountsAsync(_factory, userId);
        counts.Live.Should().Be(0, "no credential in this user's chain survives a reuse signal");
        counts.Rows.Should().Be(3, "a burn revokes what exists; it does not mint replacements");

        (await TokenReuse.Refresh(_factory, third)).StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "and the burn follows the chain, it does not stop at the first link");
        (await TokenReuse.Refresh(_factory, second)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}

/// <summary>
/// The depth claim at a second depth: a chain of four is walked through three already-revoked rows to reach the
/// live one. A burn that stops at the first revoked link, or that only ever burns one successor per replay,
/// leaves this credential working.
/// </summary>
public class TokenReuseDeepChainTests : IClassFixture<BooklyHubWebApplicationFactory>
{
    private readonly BooklyHubWebApplicationFactory _factory;

    public TokenReuseDeepChainTests(BooklyHubWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task AReplayedSpentCredential_MustWalkPastEveryAlreadyRevokedLink()
    {
        var (userId, head) = await TokenReuse.LoginAsync(_factory, "sec05b.deep@example.test");
        var second = await TokenReuse.RotateAsync(_factory, head);
        var third = await TokenReuse.RotateAsync(_factory, second);
        var fourth = await TokenReuse.RotateAsync(_factory, third);

        (await TokenReuse.Refresh(_factory, head)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var counts = await TokenReuse.CountsAsync(_factory, userId);
        counts.Live.Should().Be(0, "three links behind the replay are already spent; the live one is past all of them");
        counts.Rows.Should().Be(4);

        (await TokenReuse.Refresh(_factory, fourth)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        third.Should().NotBe(second);
    }
}

public class TokenReuseRefusalShapeTests : IClassFixture<BooklyHubWebApplicationFactory>
{
    private const string RefreshRefusal = "Invalid or expired refresh token.";

    private readonly BooklyHubWebApplicationFactory _factory;

    public TokenReuseRefusalShapeTests(BooklyHubWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task TheBurn_MustAnswerTheSameWordsAsAnyOtherRefusal()
    {
        var (userId, head) = await TokenReuse.LoginAsync(_factory, "sec05b.shape@example.test");
        await TokenReuse.RotateAsync(_factory, head);

        var replayProblem = await ProblemEnvelope.ReadAsync(
            await TokenReuse.Refresh(_factory, head), StatusCodes.Status401Unauthorized);

        ProblemEnvelope.Field(replayProblem, "detail").Should().Be(RefreshRefusal,
            "a reuse refusal must not be readable as a reuse refusal");

        var strangerProblem = await ProblemEnvelope.ReadAsync(
            await TokenReuse.Refresh(_factory, "a-string-nothing-issued"), StatusCodes.Status401Unauthorized);

        foreach (var field in new[] { "type", "title", "detail", "instance" })
        {
            ProblemEnvelope.Field(replayProblem, field).Should().Be(ProblemEnvelope.Field(strangerProblem, field),
                $"burning a chain and refusing a stranger must agree on {field}");
        }

        var counts = await TokenReuse.CountsAsync(_factory, userId);
        counts.Live.Should().Be(0, "the caller gets the quiet body; the signal stays in the data");
    }
}

public class TokenReuseNormalChainTests : IClassFixture<BooklyHubWebApplicationFactory>
{
    private readonly BooklyHubWebApplicationFactory _factory;

    public TokenReuseNormalChainTests(BooklyHubWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task AChainInNormalUse_MustKeepRotatingAndBurnNothing()
    {
        var (userId, first) = await TokenReuse.LoginAsync(_factory, "sec05b.normal@example.test");

        var second = await TokenReuse.RotateAsync(_factory, first);
        var third = await TokenReuse.RotateAsync(_factory, second);

        (await TokenReuse.Refresh(_factory, third)).StatusCode.Should().Be(HttpStatusCode.OK,
            "a chain that has not been replayed must not be treated as one");

        // Without this direction the burn could be "revoke everything, always" and still pass the tests above.
        // The row count is a constant on purpose: 4 rows with `Live` at 0 means the burn fired, and 6 means
        // redemption started minting pairs.
        var counts = await TokenReuse.CountsAsync(_factory, userId);
        counts.Rows.Should().Be(4);
        counts.Live.Should().Be(1, "each redemption spends one credential and hands out exactly one");
        third.Should().NotBe(second);
    }
}

public class TokenReuseSecondReplayTests : IClassFixture<BooklyHubWebApplicationFactory>
{
    private readonly BooklyHubWebApplicationFactory _factory;

    public TokenReuseSecondReplayTests(BooklyHubWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task ASecondReplayOfABurnedChain_MustRefuseAgainAndMintNothing()
    {
        var (userId, head) = await TokenReuse.LoginAsync(_factory, "sec05b.twice@example.test");
        var successor = await TokenReuse.RotateAsync(_factory, head);

        (await TokenReuse.Refresh(_factory, head)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var again = await TokenReuse.Refresh(_factory, head);
        again.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "the second replay sees a chain that is already burned and still gets no new credential");

        (await TokenReuse.Refresh(_factory, successor)).StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "the burn ended this credential without it ever being presented a second time");

        var counts = await TokenReuse.CountsAsync(_factory, userId);
        counts.Rows.Should().Be(2, "a burned chain mints nothing, however often it is knocked on");
        counts.Live.Should().Be(0);
    }
}
