namespace BooklyHub.Domain.Entities.System;

/// <summary>
/// DB-04: the operational tables append one row per reminder, per booking replay, per credential rotation and per
/// dispatched event, and nothing in this codebase has ever removed one. A horizon nobody can name is a horizon nobody
/// can audit, so every "how long does a row that answers no question stay here" lives in this class and nowhere else.
/// </summary>
public static class RetentionPolicy
{
    /// <summary>
    /// How long an idempotency key keeps replaying the response it stored. The middleware mints the record's deadline
    /// from this and the sweep deletes past it, because they are one rule: past the replay window the stored body is
    /// not a cache, it is a second copy of a patient's booking in a table nobody reads. That is <c>SEC-08</c>'s
    /// complaint about unredacted response bodies, answered by letting the copy die with its usefulness rather than by
    /// scrubbing fields a replay still needs to deliver byte-for-byte.
    /// </summary>
    public static readonly TimeSpan IdempotencyWindow = TimeSpan.FromHours(24);

    /// <summary>
    /// Measured from <see cref="RefreshToken.ExpiresAtUtc"/>, not from creation. A chain's replay signal lives in its
    /// spent rows and <c>SEC-05b</c> needs them for as long as any successor could still be live — and nothing downwind
    /// of a row expired this long can be: every successor was created before this row expired, because redeeming an
    /// expired credential is refused, so its own expiry is at most the token lifetime later.
    /// </summary>
    public static readonly TimeSpan RefreshTokenRetentionAfterExpiry = TimeSpan.FromDays(30);

    /// <summary>
    /// Counted from <see cref="OutboxMessage.OccurredOnUtc"/>, and only for rows that went out clean. A delivered
    /// message is a dispatch receipt for an event that has already produced the notification rows it describes; a row
    /// carrying an <c>Error</c> is kept indefinitely because it is the only record that a notification was owed and
    /// never happened, and the distinct dead-letter status <c>DB-03</c> asks for has not been built.
    /// </summary>
    public static readonly TimeSpan OutboxRetention = TimeSpan.FromDays(30);

    /// <summary>
    /// Longer than the outbox on purpose: a sent notification is the proof a patient was told, and the dispute that
    /// asks for that proof arrives after the visit, not after the dispatch.
    /// </summary>
    public static readonly TimeSpan NotificationRetention = TimeSpan.FromDays(90);
}
