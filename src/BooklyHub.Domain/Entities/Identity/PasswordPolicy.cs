namespace BooklyHub.Domain.Entities.Identity;

/// <summary>
/// PW-01: until an account could choose its own password, the only password that ever existed was the one an
/// operator put in the seeding configuration, so there was nothing to judge. This is the rule for the one path
/// that can ask for it, and it deliberately does not run at sign-in — refusing a login because a stored hash
/// no longer fits a rule written later would lock the owner out of their own account with no door to fix it.
///
/// Length is the whole rule on purpose. NIST SP 800-63B asks for a floor of 8 and forbids composition
/// requirements and mandatory rotation, because a symbol-and-digit soup pushes people to write the result down;
/// 12 is this product's own pick above that floor, not a citation. The ceiling is arithmetic rather than taste:
/// the hasher costs 100,000 PBKDF2 iterations over the input, so an unbounded body is a way to buy CPU at the
/// price of one request, and the auth tier permits ten a minute.
/// </summary>
public static class PasswordPolicy
{
    public const int MinLength = 12;
    public const int MaxLength = 256;

    /// <summary>
    /// Returns the text to show the person choosing the password, or <c>null</c> when it is acceptable. Reachable
    /// only after the caller has proved the current credential, which is what makes two distinct messages here
    /// safe in a way they are not on the sign-in path.
    /// </summary>
    public static string? RefusalFor(string? password) =>
        password is null || password.Length < MinLength
            ? $"The new password must be at least {MinLength} characters."
            : password.Length > MaxLength
                ? $"The new password must be at most {MaxLength} characters."
                : null;
}
