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
    /// <summary>
    /// The only text a failed sign-in produces. Naming it once is what keeps the three refusal reasons from
    /// drifting back into three different bodies.
    /// </summary>
    private const string InvalidCredentials = "Invalid email or password.";

    private readonly IApplicationDbContext _db;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IJwtTokenGenerator _tokenGenerator;
    private readonly IRefreshTokenProtector _protector;
    private readonly ICurrentUser _currentUser;
    private readonly IClock _clock;

    public AuthController(
        IApplicationDbContext db,
        IPasswordHasher passwordHasher,
        IJwtTokenGenerator tokenGenerator,
        IRefreshTokenProtector protector,
        ICurrentUser currentUser,
        IClock clock)
    {
        _db = db;
        _passwordHasher = passwordHasher;
        _tokenGenerator = tokenGenerator;
        _protector = protector;
        _currentUser = currentUser;
        _clock = clock;
    }

    public record LoginRequest(string Email, string Password);
    public record AuthResponse(string AccessToken, string RefreshToken, DateTime ExpiresAtUtc, UserDto User);
    public record UserDto(Guid Id, string Email, string FirstName, string LastName, Guid? TenantId, IReadOnlyList<string> Roles, IReadOnlyList<string> Permissions);
    public record RefreshTokenRequest(string RefreshToken);

    /// <summary>
    /// ENV-01: <c>Unauthorized(new { message })</c> is an <c>ObjectResult</c>, so a refused sign-in answered
    /// <c>application/json</c> with one lowercase field while the JWT challenge on the same status code answered
    /// <c>application/problem+json</c> with <c>status</c>, <c>title</c>, <c>instance</c> and <c>correlationId</c>.
    /// A client that has to read one shape per status code reads two here, and the field the support desk asks
    /// for — the correlation id — was in the body of neither.
    /// </summary>
    private ObjectResult Refusal(string detail) => Problem(
        detail: detail,
        title: "Unauthorized",
        statusCode: StatusCodes.Status401Unauthorized,
        instance: HttpContext.Request.Path.Value);

    /// <summary>
    /// SEC-05: the column stores a digest of the credential, so the presented string is digested before it is
    /// compared and a read of the table authorizes nothing. The second read is the transition, not a permanent
    /// compatibility branch: rows written before this change carry the wire string itself, and each one is
    /// rewritten to its digest the moment it is redeemed — which is how the table empties of plaintext without
    /// every session of every user being destroyed on deploy day.
    /// </summary>
    private async Task<RefreshToken?> FindPresentedTokenAsync(string presented, CancellationToken cancellationToken)
    {
        async Task<RefreshToken?> ByStoredValue(string stored) => await _db.RefreshTokens
            .Include(rt => rt.User)
                .ThenInclude(u => u!.UserRoles)
                    .ThenInclude(ur => ur.Role!)
                        .ThenInclude(r => r.RolePermissions)
            .FirstOrDefaultAsync(rt => rt.Token == stored, cancellationToken);

        var record = await ByStoredValue(_protector.Protect(presented));
        if (record != null) return record;

        record = await ByStoredValue(presented);
        if (record != null)
        {
            record.Token = _protector.Protect(presented);

            // A row old enough to hold a plaintext credential holds a plaintext successor link too; leaving that
            // behind would keep the next session's credential recoverable from this row.
            if (record.ReplacedByToken != null)
                record.ReplacedByToken = _protector.Protect(record.ReplacedByToken);
        }

        return record;
    }

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

        // One answer for three reasons — no such user, wrong password, inactive account. The password is hashed
        // before the activity check on every path that reaches it, so collapsing the branches does not hand the
        // caller a cheaper question than the body already does.
        if (user == null
            || !_passwordHasher.VerifyPassword(request.Password, user.PasswordHash)
            || !user.IsActive)
        {
            return Refusal(InvalidCredentials);
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
            Token = _protector.Protect(refreshTokenString),
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
        var tokenRecord = await FindPresentedTokenAsync(request.RefreshToken, cancellationToken);

        // Four reasons, one answer: never issued, expired, revoked, or the account behind it is no longer
        // allowed to sign in. `IsActive` was a login-only rule, so deactivating a user was a delay of up to the
        // refresh token's seven days rather than a stop. No `IsDeleted` test here on purpose: `User` carries the
        // soft-delete query filter, so a deleted account already arrives as `tokenRecord.User == null`.
        if (tokenRecord == null
            || !tokenRecord.IsActive(_clock.UtcNow)
            || tokenRecord.User is not { IsActive: true })
        {
            return Refusal("Invalid or expired refresh token.");
        }

        // Revoke current token and issue new pair (rotation)
        tokenRecord.RevokedAtUtc = _clock.UtcNow;
        var newRefreshToken = _tokenGenerator.GenerateRefreshToken();
        tokenRecord.ReplacedByToken = _protector.Protect(newRefreshToken);

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
            Token = _protector.Protect(newRefreshToken),
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
