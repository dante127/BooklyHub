using BooklyHub.Domain.Entities.Identity;
using FluentAssertions;
using Xunit;

namespace BooklyHub.UnitTests.Security;

/// <summary>
/// PW-01: the rule a person choosing their own password is measured against. Length is the entire rule, so these
/// facts are about the two boundaries and about nothing else being asked — the second is the one that decays
/// quietly, because "make it harder" reads like a security improvement and a composition requirement is the
/// reason people write the password on the monitor.
/// </summary>
public class PasswordPolicyTests
{
    private static string Of(int length) => new string('a', length);

    [Fact]
    public void TheBoundary_isExactlyTheConfiguredFloor()
    {
        PasswordPolicy.RefusalFor(Of(PasswordPolicy.MinLength)).Should().BeNull();
        PasswordPolicy.RefusalFor(Of(PasswordPolicy.MinLength - 1))
            .Should().Be($"The new password must be at least {PasswordPolicy.MinLength} characters.",
                "the message has to quote the rule that fired, not a number someone typed next to it");
    }

    [Fact]
    public void TheCeiling_isTheOneTheHasherNeeds()
    {
        // PBKDF2 here costs 100,000 iterations over the input, so the upper bound is a cost limit rather than a
        // style preference. Past it the caller is told the number, which is the whole reply they can act on.
        PasswordPolicy.RefusalFor(Of(PasswordPolicy.MaxLength)).Should().BeNull();
        PasswordPolicy.RefusalFor(Of(PasswordPolicy.MaxLength + 1))
            .Should().Be($"The new password must be at most {PasswordPolicy.MaxLength} characters.");
    }

    [Fact]
    public void AnAbsentSecret_isRefusedInTheSameWordsAsATooShortOne()
    {
        // The route reads a JSON body, so a missing field arrives as null rather than as the empty string, and the
        // two must not be two different answers to the same question.
        PasswordPolicy.RefusalFor(null).Should().StartWith("The new password must be at least");
        PasswordPolicy.RefusalFor(string.Empty).Should().StartWith("The new password must be at least");
    }

    [Fact]
    public void NothingBesideLength_isAsked()
    {
        // One character class, no digits, no symbols, and repeated: weak as a guess, legal as a rule, and exactly
        // what NIST SP 800-63B's ban on composition requirements means. A future edit that adds "must contain a
        // digit" fails here rather than shipping and being discovered by the person locked out of their own clinic.
        PasswordPolicy.RefusalFor(new string('7', PasswordPolicy.MinLength)).Should().BeNull();
        PasswordPolicy.RefusalFor("password password").Should().BeNull();
        PasswordPolicy.RefusalFor("   trailing spaces only   ").Should().BeNull();
    }
}
