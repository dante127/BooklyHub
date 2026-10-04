using BooklyHub.Domain.Entities.Identity;
using FluentAssertions;
using Xunit;

namespace BooklyHub.UnitTests.Security;

/// <summary>
/// FAN-01: the arithmetic behind "cap the account's live sessions at five". Two things are pinned here that the
/// controller's eviction cannot be trusted to reveal on its own: that a mint inside the cap evicts nothing, and
/// that the excess is always smaller than the live set, which is what lets the eviction take the *oldest* rows
/// without ever reaching the credential the current caller is being handed.
/// </summary>
public class SessionFanOutPolicyTests
{
    [Fact]
    public void ACountAtOrBelowTheCap_EvictsNothing()
    {
        for (var live = 0; live <= SessionFanOutPolicy.MaxLiveSessions; live++)
            SessionFanOutPolicy.EvictionsNeeded(live).Should().Be(0,
                $"an account holding {live} of {SessionFanOutPolicy.MaxLiveSessions} sessions is not over the rule");
    }

    [Fact]
    public void TheExcess_IsAlwaysSmallerThanTheLiveSet()
    {
        // The eviction takes exactly this many of the oldest live rows. If the count could ever equal the live set,
        // the newest credential — the one this very request minted for the caller — would be in the taken range on
        // an account whose sessions are all the same age.
        for (var live = SessionFanOutPolicy.MaxLiveSessions + 1; live <= 60; live++)
            SessionFanOutPolicy.EvictionsNeeded(live).Should().BeLessThan(live,
                $"evicting all {live} live credentials would sign the caller out of the session it just opened");
    }

    [Fact]
    public void EachAdditionalSignIn_OverTheCap_EvictsOneMore()
    {
        SessionFanOutPolicy.EvictionsNeeded(SessionFanOutPolicy.MaxLiveSessions + 1).Should().Be(1);
        SessionFanOutPolicy.EvictionsNeeded(SessionFanOutPolicy.MaxLiveSessions + 3).Should().Be(3);
    }
}
