using System.Net;
using System.Security.Cryptography;
using BooklyHub.Application.Security;
using BooklyHub.Domain.Entities.Identity;
using BooklyHub.Infrastructure.Data;
using BooklyHub.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BooklyHub.IntegrationTests.Security;

internal static class CredentialRows
{
    public static string Digest(BooklyHubWebApplicationFactory factory, string value)
    {
        using var scope = factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<IRefreshTokenProtector>().Protect(value);
    }

    /// <summary>Writes a row in the shape the code produced before SEC-05a: the credential itself, no digest.</summary>
    public static async Task SeedRowAsync(BooklyHubWebApplicationFactory factory, Guid userId, string storedToken)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        db.RefreshTokens.Add(new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Token = storedToken,
            ExpiresAtUtc = factory.Clock.UtcNow.AddDays(7),
            CreatedAtUtc = factory.Clock.UtcNow
        });

        await db.SaveChangesAsync();
    }

    public static async Task<List<RefreshToken>> ForAsync(BooklyHubWebApplicationFactory factory, Guid userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.RefreshTokens.IgnoreQueryFilters()
            .Where(t => t.UserId == userId)
            .OrderBy(t => t.CreatedAtUtc)
            .ToListAsync();
    }
}

/// <summary>
/// <c>KEY-01</c>, the half the audit did not name. The transition read added by SEC-05a compares the <em>presented</em>
/// string against the stored column, so left unchecked it accepts a row's own stored value — and a row now stores a
/// digest. Measured before the fix: presenting <c>SHA-256(wire)</c> as a refresh token answered 200 and minted a
/// session, which hands back to a table read exactly what digesting the column was supposed to take away. It is not a
/// legacy-window artefact that drains on its own either: digest rows are every row this code writes from now on, so
/// the branch would stay open forever.
/// </summary>
/// <remarks>
/// One fact per class: the auth tier is ten requests per minute and its partition is the test host.
/// </remarks>
public class StoredDigestIsNotACredentialTests : IClassFixture<BooklyHubWebApplicationFactory>
{
    private readonly BooklyHubWebApplicationFactory _factory;

    public StoredDigestIsNotACredentialTests(BooklyHubWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task PresentingTheStoredDigest_MintsNothingAndLeavesTheSessionItAlone()
    {
        var (userId, wire) = await TokenReuse.LoginAsync(_factory, "key01.storedigest@example.test");
        var stored = CredentialRows.Digest(_factory, wire);

        var refused = await TokenReuse.Refresh(_factory, stored);

        refused.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "the value in the column is what a table read gives you, and a table read must not buy a session");
        (await refused.Content.ReadAsStringAsync())
            .Should().Contain("Invalid or expired refresh token.",
            "and the refusal is the one this endpoint already gives, so closing the hole does not become an oracle for which rows are digested");

        var rows = await CredentialRows.ForAsync(_factory, userId);
        rows.Should().ContainSingle("a refused presentation does not mint a replacement");
        rows[0].Token.Should().Be(stored, "the row is not rewritten by a presentation it refused");
        rows[0].RevokedAtUtc.Should().BeNull("refusing a table read is not revoking the honest session behind it");

        // The honest client is unchanged by the guard: the same credential still redeems after the refusal.
        (await TokenReuse.Refresh(_factory, wire)).StatusCode.Should().Be(HttpStatusCode.OK,
            "the digest branch is still the path an issued credential takes");
    }

    [Fact]
    public async Task PresentingTheStoredDigestWithItsCaseFlipped_IsAlsoRefused()
    {
        // These columns are SQL_Latin1_General_CP1_CI_AS, so the forgiving read does not need the caller to hold the
        // digest exactly — UPPER(digest) used to match it too.
        var (userId, wire) = await TokenReuse.LoginAsync(_factory, "key01.storedigestci@example.test");
        var flipped = CredentialRows.Digest(_factory, wire).ToUpperInvariant();

        var refused = await TokenReuse.Refresh(_factory, flipped);

        refused.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "case is not part of a credential's identity in this database, so the transition read must not be able to answer for a digested row at all");

        var rows = await CredentialRows.ForAsync(_factory, userId);
        rows.Should().ContainSingle();
        rows[0].Token.Should().Be(CredentialRows.Digest(_factory, wire), "the row still holds the lowercase digest it was written with");
    }
}

/// <summary>
/// The same hole had a second consequence, measured before the fix: presenting a <em>spent</em> row's digest reached
/// that row through the transition read, and a spent row with a successor is the reuse signal — so a caller who can
/// only read the table could burn an honest user's live chain.
/// </summary>
public class ATableReadMustNotBurnALiveChainTests : IClassFixture<BooklyHubWebApplicationFactory>
{
    private readonly BooklyHubWebApplicationFactory _factory;

    public ATableReadMustNotBurnALiveChainTests(BooklyHubWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task PresentingASpentRowsDigest_MustBurnNothingBehindIt()
    {
        var (userId, head) = await TokenReuse.LoginAsync(_factory, "key01.burndos@example.test");
        var successor = await TokenReuse.RotateAsync(_factory, head);

        var spentDigest = CredentialRows.Digest(_factory, head);
        var spent = (await CredentialRows.ForAsync(_factory, userId)).Single(t => t.Token == spentDigest);
        spent.ReplacedByToken.Should().Be(CredentialRows.Digest(_factory, successor),
            "rotation records the live successor, digested, on the row it just spent");

        (await TokenReuse.Refresh(_factory, spentDigest)).StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "the stored digest is not a credential, so it cannot even reach the reuse signal");

        // Refusing the presentation has to leave the row's own record alone too. Measured under control CT1: the
        // transition branch rewrote this row's credential and its successor link before the burn walked the link,
        // so a replayed spent row burned nothing at all (named SEC-05d, left open — it only reaches rows the
        // transition branch can still match).
        var stillSpent = (await CredentialRows.ForAsync(_factory, userId)).Single(t => t.RevokedAtUtc != null);
        stillSpent.Token.Should().Be(spentDigest, "a refused presentation does not rewrite the row it refused");
        stillSpent.ReplacedByToken.Should().Be(spent.ReplacedByToken,
            "and it does not corrupt the link the reuse signal is read from");

        (await TokenReuse.Refresh(_factory, successor)).StatusCode.Should().Be(HttpStatusCode.OK,
            "and the honest session that was never touched still owns its chain");
    }
}

/// <summary>
/// <c>KEY-01</c> as the audit filed it: SQL Server folds case and ignores trailing space when it compares character
/// values, so a mangled copy of a legacy plaintext credential redeemed it. Measured before the fix: both a
/// case-flipped copy and a space-padded copy answered 200. The credential's identity is the string that was issued,
/// byte for byte — and the transition branch is the only place that had to say so.
/// </summary>
public class LegacyCredentialMustMatchByteForByteTests : IClassFixture<BooklyHubWebApplicationFactory>
{
    private readonly BooklyHubWebApplicationFactory _factory;

    public LegacyCredentialMustMatchByteForByteTests(BooklyHubWebApplicationFactory factory) => _factory = factory;

    private async Task<(Guid UserId, string Credential)> SeedLegacySessionAsync(string email)
    {
        var (userId, _) = await TokenReuse.LoginAsync(_factory, email);
        var credential = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
        await CredentialRows.SeedRowAsync(_factory, userId, credential);

        return (userId, credential);
    }

    [Fact]
    public async Task TheCredentialExactlyAsIssued_MustStillRedeem()
    {
        // The positive control for the two facts below: without it they would only prove the branch is dead.
        var (userId, credential) = await SeedLegacySessionAsync("key01.legacyexact@example.test");

        (await TokenReuse.Refresh(_factory, credential)).StatusCode.Should().Be(HttpStatusCode.OK,
            "a legacy session is upgraded on touch, which is what let SEC-05a ship without logging anyone out");

        var stored = (await CredentialRows.ForAsync(_factory, userId)).Single(t => t.RevokedAtUtc != null);
        stored.Token.Should().Be(CredentialRows.Digest(_factory, credential), "and it leaves no plaintext behind");
    }

    [Fact]
    public async Task ACaseFlippedCopyOfALegacyCredential_MustNotRedeemIt()
    {
        var (userId, credential) = await SeedLegacySessionAsync("key01.legacyci@example.test");
        credential.Any(char.IsLower).Should().BeTrue("a base64 credential has letters for case folding to touch");

        (await TokenReuse.Refresh(_factory, credential.ToUpperInvariant()))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized,
                "whoever holds a mangled copy of a credential holds a read of it, not the credential");

        var rows = await CredentialRows.ForAsync(_factory, userId);
        rows.Should().HaveCount(2, "a refused presentation neither mints nor burns");
        rows.Single(t => t.Token == credential).RevokedAtUtc.Should().BeNull(
            "and the honest session keeps the plaintext row it can still redeem");
    }

    [Fact]
    public async Task ASpacePaddedCopyOfALegacyCredential_MustNotRedeemIt()
    {
        // Trailing space is the part a column collation would not have fixed: = ignores it for character types
        // whatever the collation is, so the byte-exact test has to live where the credential is checked.
        var (userId, credential) = await SeedLegacySessionAsync("key01.legacyspace@example.test");

        (await TokenReuse.Refresh(_factory, credential + " "))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized,
                "SQL Server's = ignores trailing space, so the match is confirmed in code and not left to the database");

        var rows = await CredentialRows.ForAsync(_factory, userId);
        rows.Should().HaveCount(2);
        rows.Single(t => t.Token == credential).RevokedAtUtc.Should().BeNull();
    }
}
