using BooklyHub.Application.Common.Interfaces;
using BooklyHub.Application.Security;
using BooklyHub.Domain.Entities.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace BooklyHub.Api.Controllers;

[ApiController]
[Route("api/v1/auth")]
public class AuthController : ControllerBase
{
    private readonly IApplicationDbContext _db;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IJwtTokenGenerator _tokenGenerator;
    private readonly ICurrentUser _currentUser;
    private readonly IClock _clock;

    public AuthController(
        IApplicationDbContext db,
        IPasswordHasher passwordHasher,
        IJwtTokenGenerator tokenGenerator,
        ICurrentUser currentUser,
        IClock clock)
    {
        _db = db;
        _passwordHasher = passwordHasher;
        _tokenGenerator = tokenGenerator;
        _currentUser = currentUser;
        _clock = clock;
    }

    public record LoginRequest(string Email, string Password);
    public record AuthResponse(string AccessToken, string RefreshToken, DateTime ExpiresAtUtc, UserDto User);
    public record UserDto(Guid Id, string Email, string FirstName, string LastName, Guid? TenantId, IReadOnlyList<string> Roles, IReadOnlyList<string> Permissions);
    public record RefreshTokenRequest(string RefreshToken);

    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public async Task<ActionResult<AuthResponse>> Login([FromBody] LoginRequest request, CancellationToken cancellationToken)
    {
        var user = await _db.Users
            .IgnoreQueryFilters()
            .Include(u => u.UserRoles)
                .ThenInclude(ur => ur.Role!)
                    .ThenInclude(r => r.RolePermissions)
            .FirstOrDefaultAsync(u => u.Email == request.Email && !u.IsDeleted, cancellationToken);

        if (user == null || !_passwordHasher.VerifyPassword(request.Password, user.PasswordHash))
        {
            return Unauthorized(new { message = "Invalid email or password." });
        }

        if (!user.IsActive)
        {
            return Unauthorized(new { message = "User account has been deactivated." });
        }

        var roles = user.UserRoles.Select(ur => ur.Role!.Name).Distinct().ToList();
        var permissions = user.UserRoles
            .SelectMany(ur => ur.Role!.RolePermissions.Select(rp => rp.PermissionId))
            .Distinct()
            .ToList();

        var accessToken = _tokenGenerator.GenerateAccessToken(user, roles, permissions);
        var refreshTokenString = _tokenGenerator.GenerateRefreshToken();

        var refreshToken = new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            Token = refreshTokenString,
            ExpiresAtUtc = _clock.UtcNow.AddDays(7),
            CreatedAtUtc = _clock.UtcNow
        };

        _db.RefreshTokens.Add(refreshToken);
        user.LastLoginAtUtc = _clock.UtcNow;

        await _db.SaveChangesAsync(cancellationToken);

        var userDto = new UserDto(user.Id, user.Email, user.FirstName, user.LastName, user.TenantId, roles, permissions);
        return Ok(new AuthResponse(accessToken, refreshTokenString, _clock.UtcNow.AddMinutes(60), userDto));
    }

    [HttpPost("refresh-token")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public async Task<ActionResult<AuthResponse>> RefreshToken([FromBody] RefreshTokenRequest request, CancellationToken cancellationToken)
    {
        var tokenRecord = await _db.RefreshTokens
            .Include(rt => rt.User)
                .ThenInclude(u => u!.UserRoles)
                    .ThenInclude(ur => ur.Role!)
                        .ThenInclude(r => r.RolePermissions)
            .FirstOrDefaultAsync(rt => rt.Token == request.RefreshToken, cancellationToken);

        if (tokenRecord == null || !tokenRecord.IsActive(_clock.UtcNow) || tokenRecord.User == null)
        {
            return Unauthorized(new { message = "Invalid or expired refresh token." });
        }

        // Revoke current token and issue new pair (rotation)
        tokenRecord.RevokedAtUtc = _clock.UtcNow;
        var newRefreshToken = _tokenGenerator.GenerateRefreshToken();
        tokenRecord.ReplacedByToken = newRefreshToken;

        var user = tokenRecord.User;
        var roles = user.UserRoles.Select(ur => ur.Role!.Name).Distinct().ToList();
        var permissions = user.UserRoles
            .SelectMany(ur => ur.Role!.RolePermissions.Select(rp => rp.PermissionId))
            .Distinct()
            .ToList();

        var newAccessToken = _tokenGenerator.GenerateAccessToken(user, roles, permissions);

        _db.RefreshTokens.Add(new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            Token = newRefreshToken,
            ExpiresAtUtc = _clock.UtcNow.AddDays(7),
            CreatedAtUtc = _clock.UtcNow
        });

        await _db.SaveChangesAsync(cancellationToken);

        var userDto = new UserDto(user.Id, user.Email, user.FirstName, user.LastName, user.TenantId, roles, permissions);
        return Ok(new AuthResponse(newAccessToken, newRefreshToken, _clock.UtcNow.AddMinutes(60), userDto));
    }

    [HttpGet("me")]
    [Authorize]
    public async Task<ActionResult<UserDto>> GetMe(CancellationToken cancellationToken)
    {
        if (!_currentUser.UserId.HasValue) return Unauthorized();

        var user = await _db.Users
            .AsNoTracking()
            .Include(u => u.UserRoles)
                .ThenInclude(ur => ur.Role!)
                    .ThenInclude(r => r.RolePermissions)
            .FirstOrDefaultAsync(u => u.Id == _currentUser.UserId.Value, cancellationToken);

        if (user == null) return NotFound();

        var roles = user.UserRoles.Select(ur => ur.Role!.Name).Distinct().ToList();
        var permissions = user.UserRoles
            .SelectMany(ur => ur.Role!.RolePermissions.Select(rp => rp.PermissionId))
            .Distinct()
            .ToList();

        return Ok(new UserDto(user.Id, user.Email, user.FirstName, user.LastName, user.TenantId, roles, permissions));
    }
}
