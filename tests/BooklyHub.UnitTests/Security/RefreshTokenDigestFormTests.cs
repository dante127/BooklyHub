using System.Security.Cryptography;
using BooklyHub.Infrastructure.Security;
using FluentAssertions;
using Xunit;

namespace BooklyHub.UnitTests.Security;

/// <summary>
/// <c>KEY-01</c>: the auth path has to decide, from the stored value alone, whether a row still carries the
/// credential as it was issued or already carries its digest — the transition read may only reach the first kind.
/// That decision is only sound while the two shapes are disjoint, so the invariant is pinned here rather than
/// assumed from a comment: everything <see cref="RefreshTokenProtector.Protect"/> writes is recognised, and no
/// credential as the generator issues it ever is.
/// </summary>
public class RefreshTokenDigestFormTests
{
    private readonly RefreshTokenProtector _protector = new();

    private static string IssuedCredential() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));

    [Fact]
    public void EveryValueTheWriterProduces_MustBeRecognisedAsADigest()
    {
        // The one definition of the stored form, tested against the writer instead of against a hard-coded shape:
        // if Protect ever stopped lowercasing, or stopped being 64 hex characters, the transition read would start
        // treating fresh rows as legacy and the guard would be open again — silently.
        for (var attempt = 0; attempt < 200; attempt++)
        {
            _protector.IsProtected(_protector.Protect(IssuedCredential())).Should().BeTrue(
                $"the value Protect wrote for credential #{attempt} must be readable as a digest");
        }
    }

    [Fact]
    public void NoIssuedCredential_MustBeRecognisedAsADigest()
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var issued = IssuedCredential();

            issued.Should().HaveLength(88, "a credential is 64 random bytes as base64, which is the legacy row shape");
            _protector.IsProtected(issued).Should().BeFalse(
                $"the credential {issued[..8]}… is what a client holds, not what the digesting code stores");
        }
    }

    [Fact]
    public void OnlyTheExactDigestForm_IsRecognised()
    {
        var digest = _protector.Protect(IssuedCredential());

        _protector.IsProtected(digest).Should().BeTrue();
        _protector.IsProtected(digest[..63]).Should().BeFalse("one character short is not a SHA-256 digest");
        _protector.IsProtected(digest + "0").Should().BeFalse("one character over is not a SHA-256 digest");
        _protector.IsProtected(digest.ToUpperInvariant()).Should().BeFalse(
            "the writer emits lowercase, so a value in another case was not written by it and is not excused from the transition read's byte-exact match");
        _protector.IsProtected(new string('g', 64)).Should().BeFalse("64 characters is not enough; the alphabet is hex");
        _protector.IsProtected("").Should().BeFalse();
    }

    [Fact]
    public void AnEmptyPresentedCredential_MustNotDigestToSomethingStorable()
    {
        // Guards the shape rule against the other end: the digest of any input is still 64 hex characters, so no
        // presentation can produce a value the column would misread as legacy.
        _protector.IsProtected(_protector.Protect(string.Empty)).Should().BeTrue();
    }
}
