using BooklyHub.Domain.Entities.Identity;
using FluentAssertions;
using Xunit;

namespace BooklyHub.UnitTests.Security;

/// <summary>
/// SEC-04(b): the account rule has to be the one that fires before the address tier, its window has to restart
/// rather than accumulate for the lifetime of the account, and its penalty has to lift by itself. These are the
/// four numbers and three boundaries the login path is built on; the wire behaviour is pinned in
/// <c>BooklyHub.IntegrationTests/Security/LoginLockoutTests.cs</c>.
/// </summary>
public class LoginLockoutPolicyTests
{
    private static readonly DateTime Now = new(2026, 10, 04, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void TheThresholdIsFive_AndItIsBelowTheTenPermitsTheAddressTierGrants()
    {
        LoginLockoutPolicy.MaxFailedAttempts.Should().Be(5);

        // Five is not a taste number: the tier lets one address make ten attempts a minute, so a threshold of ten
        // or more would only ever be reached by a caller spreading attempts across addresses — the case this rule
        // cannot see. Measured before it was chosen: ten wrong passwords all returned 401 and the owner's own
        // correct attempt was refused 429.
        LoginLockoutPolicy.MaxFailedAttempts.Should().BeLessThan(10);
    }

    [Fact]
    public void AFailureOlderThanTheWindow_StartsANewStreak()
    {
        var last = Now - LoginLockoutPolicy.Window - TimeSpan.FromSeconds(1);

        LoginLockoutPolicy.NextFailedCount(4, last, Now).Should().Be(1,
            "a mistype from before the window is not evidence about this one");
        LoginLockoutPolicy.NextFailedCount(9, null, Now).Should().Be(1);
    }

    [Fact]
    public void AFailureInsideTheWindow_AddsToTheStreak()
    {
        var last = Now - LoginLockoutPolicy.Window + TimeSpan.FromSeconds(1);

        LoginLockoutPolicy.NextFailedCount(4, last, Now).Should().Be(5);
        LoginLockoutPolicy.NextFailedCount(2, last, Now).Should().Be(3);
    }

    [Fact]
    public void TheLockoutOpensOnTheFifthFailureAndNotTheFourth()
    {
        LoginLockoutPolicy.LockoutUntil(4, null, Now).Should().BeNull();
        LoginLockoutPolicy.LockoutUntil(5, null, Now).Should().Be(Now + LoginLockoutPolicy.LockoutDuration);
    }

    [Fact]
    public void AKnockerAlreadyLockedOut_DoesNotPushTheDeadlineFurtherBack()
    {
        var opened = Now + TimeSpan.FromMinutes(4);

        // The deadline is one promise about when the account answers again. Extending it on every further failure
        // would let a guesser who keeps knocking be punished, but would also let the punishment grow without bound
        // for an owner whose client keeps retrying a stale password.
        LoginLockoutPolicy.LockoutUntil(6, opened, Now).Should().Be(opened);
    }

    [Fact]
    public void AStaleLockout_IsReplacedRatherThanKept()
    {
        var expired = Now - TimeSpan.FromSeconds(1);

        // Without this branch a streak that begins right as an old lockout ends would inherit the past deadline
        // and never open a new one, so the sixth failure in a row would be freer than the fifth.
        LoginLockoutPolicy.LockoutUntil(5, expired, Now).Should().Be(Now + LoginLockoutPolicy.LockoutDuration);
    }

    [Fact]
    public void TheLockoutLiftsTheMomentItsDeadlinePasses()
    {
        LoginLockoutPolicy.IsLockedOut(Now + TimeSpan.FromSeconds(1), Now).Should().BeTrue();
        LoginLockoutPolicy.IsLockedOut(Now, Now).Should().BeFalse("the deadline is exclusive, so no second is added");
        LoginLockoutPolicy.IsLockedOut(null, Now).Should().BeFalse();
    }
}
