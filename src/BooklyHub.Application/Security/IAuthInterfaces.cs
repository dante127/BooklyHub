using BooklyHub.Domain.Entities.Identity;

namespace BooklyHub.Application.Security;

public interface IJwtTokenGenerator
{
    string GenerateAccessToken(User user, IReadOnlyList<string> roles, IReadOnlyList<string> permissions);
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
}
