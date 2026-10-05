using System.Text.RegularExpressions;
using BooklyHub.Domain.Entities.System;
using FluentAssertions;
using Xunit;

namespace BooklyHub.UnitTests.Security;

/// <summary>
/// KEY-02 pins the two halves of one rule — what a key's identity <em>is</em> in the store, and when two stored
/// keys count as the same one. The integration facts prove the three doors behave; these prove the function those
/// doors share cannot fold two different keys, which no wire test can see, because the wire only shows whichever
/// comparison SQL happened to make.
/// </summary>
public class IdempotencyIdentityTests
{
    [Theory]
    [InlineData("tenant:AB12", "tenant:ab12")]
    [InlineData("global:KEY", "global:key")]
    [InlineData("tenant:Ab1", "tenant:aB1")]
    public void TwoKeysDifferingOnlyByCase_MustNotShareAnIdentity(string first, string second)
    {
        IdempotencyIdentity.StorageId(first).Should().NotBe(IdempotencyIdentity.StorageId(second),
            "the database's default collation calls these two strings equal, and the store must not inherit that");
    }

    [Theory]
    [InlineData("tenant:AB12 ")]
    [InlineData("tenant:  ")]
    public void AKeyPaddedWithTrailingSpaces_MustNotShareAnIdentityWithItselfWithoutThem(string padded)
    {
        var bare = padded.TrimEnd();

        // SQL's equality ignores trailing spaces, so a padded key folded onto a bare one even before case did.
        IdempotencyIdentity.StorageId(padded).Should().NotBe(IdempotencyIdentity.StorageId(bare));
        IdempotencyIdentity.SameKey(bare, padded).Should().BeFalse();
    }

    [Fact]
    public void TheSameKey_MustDigestToTheSameIdentityEveryTime()
    {
        var key = "3f2a:60e6e8a622d34c448904912a7acb2bfb";

        IdempotencyIdentity.StorageId(key).Should().Be(IdempotencyIdentity.StorageId(key));
        IdempotencyIdentity.StorageId(key).Should().Be(IdempotencyIdentity.StorageId(new string(key.ToCharArray())));
    }

    [Fact]
    public void TheStoredIdentity_MustBeLowercaseHexThatFitsTheColumnItIsWrittenTo()
    {
        var id = IdempotencyIdentity.StorageId("global:any-key-at-all");

        Regex.IsMatch(id, "^[0-9a-f]{64}$").Should().BeTrue(
            $"a digest with an uppercase letter would leave the collation a case to fold, and the shape was {id}");
        id.Length.Should().BeLessThanOrEqualTo(256,
            "the primary key column is nvarchar(256), so this fix must not need a migration to be stored");
    }

    [Fact]
    public void DistinctKeys_MustGetDistinctIdentities()
    {
        var ids = new[] { "tenant:a", "tenant:b", "global:a", "tenant:A" }
            .Select(IdempotencyIdentity.StorageId)
            .ToList();

        ids.Distinct().Should().HaveCount(4, "two keys that mean different requests must not collide in the store");
    }

    [Fact]
    public void TheRowDoors_MustCompareByteForByteAndTreatAMissingKeyAsNoMatch()
    {
        IdempotencyIdentity.SameKey("AB12", "AB12").Should().BeTrue("an honest retry is the whole point");
        IdempotencyIdentity.SameKey("ab12", "AB12").Should().BeFalse();
        IdempotencyIdentity.SameKey(null, "AB12").Should().BeFalse(
            "a row that carries no key cannot be the answer to a request that does");
        IdempotencyIdentity.SameKey(string.Empty, string.Empty).Should().BeTrue();
    }
}
