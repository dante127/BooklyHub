using BooklyHub.Domain.Entities.Identity;

namespace BooklyHub.Application.Security;

public interface IJwtTokenGenerator
{
    string GenerateAccessToken(User user, IReadOnlyList<string> roles, IReadOnlyList<string> permissions);

    /// <summary>
    /// The instant <see cref="GenerateAccessToken"/> told the verifier that credential stops working, read back
    /// out of it rather than recomputed. EXP-01: a caller that answers "it is valid for N minutes" from its own
    /// copy of N reports a lifetime the token does not have whenever the configured value, or the clock the two
    /// sides read, differ. Parsing is cheap and this is the only form that cannot drift.
    /// </summary>
    DateTime GetAccessTokenExpiryUtc(string accessToken);

    string GenerateRefreshToken();
}

public interface IPasswordHasher
{
    string HashPassword(string password);
    bool VerifyPassword(string password, string hash);
}

/// <summary>
/// SEC-05: maps the credential a client presents to the value the database stores for it. The stored value is a
/// digest, so reading the table cannot authorize anything.
/// </summary>
public interface IRefreshTokenProtector
{
    string Protect(string token);

    /// <summary>
    /// True when <paramref name="storedValue"/> already has the form <see cref="Protect"/> writes. This is the
    /// only sound way to tell a row that predates the digest from a row that was written by it: the two shapes are
    /// disjoint for every credential this code has ever issued, so the question is answered from the stored value
    /// itself and not from a timestamp or a deploy marker.
    /// </summary>
    bool IsProtected(string storedValue);
}
