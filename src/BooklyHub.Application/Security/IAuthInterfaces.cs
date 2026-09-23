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
