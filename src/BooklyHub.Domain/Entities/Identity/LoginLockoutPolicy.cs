namespace BooklyHub.Domain.Entities.Identity;

/// <summary>
/// SEC-04(b): the address tier says how fast one machine may knock; this says how many knocks one *account* may
/// absorb before it stops answering. A threshold above the tier's 10 permits per minute would be unreachable from
/// a single address — measured, ten wrong passwords all returned 401 and the owner's own correct attempt at #11 was
/// refused with 429 — so the account rule has to be the one that fires first, and five is what does that.
///
/// The count measures an attempt rate, not a history: a failure older than the window is not part of this streak,
/// otherwise every mistyped password since the account was created would load toward a lockout.
/// </summary>
public static class LoginLockoutPolicy
{
    public const int MaxFailedAttempts = 5;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    public static DateTime WindowStart(DateTime nowUtc) => nowUtc - Window;

    public static bool IsOutsideTheWindow(DateTime? lastFailedAtUtc, DateTime nowUtc) =>
        lastFailedAtUtc == null || lastFailedAtUtc < WindowStart(nowUtc);

    public static int NextFailedCount(int currentCount, DateTime? lastFailedAtUtc, DateTime nowUtc) =>
        IsOutsideTheWindow(lastFailedAtUtc, nowUtc) ? 1 : currentCount + 1;

    /// <summary>
    /// Reaching the threshold opens a window of silence; failing again while it is already open does not extend it,
    /// because the deadline is one promise about when the account answers again, not a running penalty.
    /// </summary>
    public static DateTime? LockoutUntil(int nextFailedCount, DateTime? currentLockoutUntilUtc, DateTime nowUtc) =>
        nextFailedCount >= MaxFailedAttempts &&
        (currentLockoutUntilUtc == null || currentLockoutUntilUtc <= nowUtc)
            ? nowUtc + LockoutDuration
            : currentLockoutUntilUtc;

    public static bool IsLockedOut(DateTime? lockoutUntilUtc, DateTime nowUtc) =>
        lockoutUntilUtc > nowUtc;
}
