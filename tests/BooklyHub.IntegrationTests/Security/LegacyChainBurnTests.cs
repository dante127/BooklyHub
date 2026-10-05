using System.Net;
using System.Text.RegularExpressions;
using BooklyHub.IntegrationTests.Infrastructure;
using FluentAssertions;
using Xunit;

namespace BooklyHub.IntegrationTests.Security;

/// <summary>
/// <c>SEC-05d</c>: the transition branch that lets a row written before <c>SEC-05a</c> still redeem also rewrites
/// what that row holds — its credential and its successor link — and it does so <em>before</em> <c>SEC-05b</c>'s
/// burn walk reads the link. A legacy row's link is the plaintext credential of the next session; the rewrite turns
/// it into a digest, and a digest matches nothing in a chain whose later rows were never presented. Measured before
/// the fix: replaying a spent legacy head refused the caller and burned nothing, leaving the thief's successor live
/// and able to mint a session.
/// </summary>
/// <remarks>
/// One fact per class: the auth tier is ten requests per minute and its partition is the test host.
/// </remarks>
public class LegacyChainBurnTests : IClassFixture<BooklyHubWebApplicationFactory>
{
    private readonly BooklyHubWebApplicationFactory _factory;

    public LegacyChainBurnTests(BooklyHubWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task AReplayedSpentLegacyRow_MustBurnThroughThePlaintextLinkItStillHolds()
    {
        var userId = await CredentialRows.SeedUserAsync(_factory, "sec05d.legacy@example.test");
        var chain = await CredentialRows.SeedLegacyChainAsync(_factory, userId, links: 3);
        var (head, middle, tip) = (chain[0], chain[1], chain[2]);

        var middleSpentAt = (await CredentialRows.ForAsync(_factory, userId))
            .Single(t => t.Token == middle).RevokedAtUtc;

        var replay = await TokenReuse.Refresh(_factory, head);
        replay.StatusCode.Should().Be(HttpStatusCode.Unauthorized, await replay.Content.ReadAsStringAsync());

        var counts = await TokenReuse.CountsAsync(_factory, userId);
        counts.Live.Should().Be(0,
            "the replay is the theft signal, and a link stored as plaintext is still the link — burning nothing " +
            "leaves the thief's next credential live and minting");
        counts.Rows.Should().Be(3, "a burn revokes what exists; it does not mint replacements");

        var tipAttempt = await TokenReuse.Refresh(_factory, tip);
        tipAttempt.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "and the walk reaches the tip of a chain that never saw a digest");

        // The walk's own invariant, on the legacy path: revoking a row already spent must not move the timestamp
        // that records when the honest client spent it.
        var stillMiddle = (await CredentialRows.ForAsync(_factory, userId)).Single(t => t.Token == middle);
        stillMiddle.RevokedAtUtc.Should().Be(middleSpentAt,
            "the burn adds a revocation, it does not rewrite the history of one already there");
    }
}

/// <summary>
/// The other half of the same walk, and the reason the fix cannot be "stop rewriting the link": a chain that
/// crossed the release the other way — a plaintext parent whose successor was upgraded by being presented — has a
/// plaintext link pointing at a digest row. Both directions have to resolve. This fact passes before the fix
/// precisely because the rewrite normalises the link, so it guards the fix's direction rather than proving the bug.
/// </summary>
public class MixedChainBurnTests : IClassFixture<BooklyHubWebApplicationFactory>
{
    private readonly BooklyHubWebApplicationFactory _factory;

    public MixedChainBurnTests(BooklyHubWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task APlaintextLinkPointingAtADigestedRow_MustBurnItToo()
    {
        var userId = await CredentialRows.SeedUserAsync(_factory, "sec05d.mixed@example.test");

        var headRaw = $"legacy-{Guid.NewGuid():N}";
        var middleRaw = $"legacy-{Guid.NewGuid():N}";
        var tipRaw = $"legacy-{Guid.NewGuid():N}";
        var spentAt = _factory.Clock.UtcNow.AddHours(-2);

        // The head never saw the release: plaintext credential, plaintext link. Its successor was presented after
        // it, so that row and its own link are digests.
        await CredentialRows.SeedRowAsync(_factory, userId, headRaw, middleRaw, spentAt);
        await CredentialRows.SeedRowAsync(_factory, userId, CredentialRows.Digest(_factory, middleRaw),
            CredentialRows.Digest(_factory, tipRaw), spentAt.AddMinutes(1));
        await CredentialRows.SeedRowAsync(_factory, userId, CredentialRows.Digest(_factory, tipRaw), null, null);

        var replay = await TokenReuse.Refresh(_factory, headRaw);
        replay.StatusCode.Should().Be(HttpStatusCode.Unauthorized, await replay.Content.ReadAsStringAsync());

        var counts = await TokenReuse.CountsAsync(_factory, userId);
        counts.Live.Should().Be(0, "a walk that guessed one link shape would burn only the half of the deploy it guessed");

        var tipAttempt = await TokenReuse.Refresh(_factory, tipRaw);
        tipAttempt.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "the tip is stored as a digest, so this is the presentation that has to stop working");
    }
}

/// <summary>
/// What the rewrite is for, and what the fix is not allowed to throw away: a row this code touches must not keep a
/// recoverable credential for the next session. On the replay path the row is not rotated, so the link the old code
/// left behind survives in the row unless the upgrade rewrites it — read here off the stored columns, not off a
/// response.
/// </summary>
public class LegacyHeadUpgradeTests : IClassFixture<BooklyHubWebApplicationFactory>
{
    private readonly BooklyHubWebApplicationFactory _factory;

    public LegacyHeadUpgradeTests(BooklyHubWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task TheReplayedLegacyHead_MustNotBeLeftHoldingEitherPlaintextValue()
    {
        var userId = await CredentialRows.SeedUserAsync(_factory, "sec05d.upgrade@example.test");
        var chain = await CredentialRows.SeedLegacyChainAsync(_factory, userId, links: 2);

        await TokenReuse.Refresh(_factory, chain[0]);

        // The head is the row that carries a link: the tip it points at has none, whether or not the burn reached it.
        var head = (await CredentialRows.ForAsync(_factory, userId)).Single(t => t.ReplacedByToken != null);

        Regex.IsMatch(head.Token, "^[0-9a-f]{64}$").Should().BeTrue(
            "a read of the table must not hand out the credential that was issued; the row still held " + head.Token);
        Regex.IsMatch(head.ReplacedByToken!, "^[0-9a-f]{64}$").Should().BeTrue(
            "and the same for the successor link this row carries — it is the next session's credential, and left " +
            "plaintext it stays recoverable: it held " + head.ReplacedByToken);

        head.ReplacedByToken.Should().Be(CredentialRows.Digest(_factory, chain[1]),
            "the digest it holds is the digest of the value that was there, so the link still names its successor");
    }
}
