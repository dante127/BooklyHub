using BooklyHub.Domain.Entities.Identity;
using FluentAssertions;
using Xunit;

namespace BooklyHub.UnitTests.Security;

/// <summary>
/// The refresh-token life rule is a deadline, so what matters is which side of the instant it answers on.
/// The window is pinned to a fixed date and the clock is passed in, which is the only way the boundary
/// itself can be observed rather than raced.
/// </summary>
public class RefreshTokenExpiryRuleTests
{
    private static readonly DateTime IssuedAt = new(2026, 3, 5, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Deadline = IssuedAt.AddDays(7);

    private static RefreshToken Token(DateTime? revokedAtUtc = null) => new()
    {
        Token = "opaque-token",
        ExpiresAtUtc = Deadline,
        CreatedAtUtc = IssuedAt,
        RevokedAtUtc = revokedAtUtc
    };

    [Fact]
    public void OneMomentBeforeTheDeadline_MustStillBeActive()
    {
        Token().IsActive(Deadline.AddMilliseconds(-1)).Should().BeTrue();
    }

    [Fact]
    public void TheDeadlineItself_MustAlreadyBeExpired()
    {
        var token = Token();

        token.IsExpired(Deadline).Should().BeTrue("the boundary is inclusive: a token living to Tuesday 10:00 is not alive at Tuesday 10:00");
        token.IsActive(Deadline).Should().BeFalse();
    }

    [Fact]
    public void ARevokedToken_MustBeInactiveInsideItsOwnLife()
    {
        Token(revokedAtUtc: IssuedAt.AddHours(1)).IsActive(IssuedAt.AddHours(2)).Should().BeFalse(
            "rotation revokes the old token long before it expires, and revocation must not need the clock to move");
    }

    [Fact]
    public void TheExpiryRule_MustNotReadTheSystemClock()
    {
        // A rule that consulted DateTime.UtcNow would answer the same for both of these, and the
        // difference between them is the entire rule.
        Token().IsActive(IssuedAt).Should().BeTrue();
        Token().IsActive(Deadline.AddSeconds(1)).Should().BeFalse();
    }
}
