using System.Security.Cryptography;
using System.Text;

namespace BooklyHub.Domain.Entities.System;

/// <summary>
/// KEY-02: a client's idempotency key is an identity, and every comparison that decides "is this the same request
/// I already answered" is a decision about that identity. The database's default collation
/// (<c>SQL_Latin1_General_CP1_CI_AS</c>, measured at runtime by <c>KEY-01</c>) folds case in every one of those
/// comparisons, so <c>AB12</c> and <c>ab12</c> — two different promises from two different requests — were one key.
/// Measured on the three doors this rule governs: a case-flipped key with a different body was refused
/// <c>409</c> and never ran, a case-flipped key with the same body replayed the first answer, and — the one that
/// costs money — a case-flipped key carrying a <c>70.00</c> charge was answered <c>200</c> with the receipt of an
/// earlier <c>40.00</c> charge, so the second payment silently never happened.
/// </summary>
public static class IdempotencyIdentity
{
    /// <summary>
    /// The primary key of the replay store for a tenant-scoped key. A digest rather than the caller's string,
    /// because a primary key cannot hold two values the collation considers equal no matter how the code compares
    /// them afterwards: the fix has to change what is <em>stored</em>. Lowercase hex is 64 characters, which fits
    /// the existing <c>nvarchar(256)</c> and its clustered index, so this costs no migration.
    /// </summary>
    public static string StorageId(string scopedKey)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(scopedKey));
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    /// <summary>
    /// Whether a key stored on a domain row is the key the caller presented. The rows that carry a key as data —
    /// an appointment, a payment — keep the caller's string so a support ticket can read it and so a retry that
    /// arrives after this release still matches the row written before it; what becomes byte-exact is the
    /// comparison. Read together with a SQL equality test, which is what narrows the candidate set: this decides.
    /// </summary>
    public static bool SameKey(string? stored, string presented) =>
        string.Equals(stored, presented, StringComparison.Ordinal);
}
