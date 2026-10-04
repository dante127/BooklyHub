using BooklyHub.Api.Middlewares;
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
    private readonly ILogger<AuthController> _logger;

    public AuthController(
        IApplicationDbContext db,
        IPasswordHasher passwordHasher,
        IJwtTokenGenerator tokenGenerator,
        IRefreshTokenProtector protector,
        ICurrentUser currentUser,
        IClock clock,
        ILogger<AuthController> logger)
    {
        _db = db;
        _passwordHasher = passwordHasher;
        _tokenGenerator = tokenGenerator;
        _protector = protector;
        _currentUser = currentUser;
        _clock = clock;
        _logger = logger;
    }

    public record LoginRequest(string Email, string Password);
    public record AuthResponse(string AccessToken, string RefreshToken, DateTime ExpiresAtUtc, UserDto User);
    public record UserDto(Guid Id, string Email, string FirstName, string LastName, Guid? TenantId, IReadOnlyList<string> Roles, IReadOnlyList<string> Permissions);
    public record RefreshTokenRequest(string RefreshToken);
    public record LogoutRequest(string RefreshToken);
    public record ChangePasswordRequest(string CurrentPassword, string NewPassword);

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
    /// The <c>400</c> twin of <see cref="Refusal"/>, and named <c>InvalidRequest</c> rather than
    /// <c>BadRequest</c> on purpose: a member called <c>BadRequest</c> hides the inherited
    /// <c>ControllerBase.BadRequest(object)</c> overloads for every action in this file, which is how an
    /// <c>ObjectResult</c> silently turns back into the bare <c>application/json</c> body ENV-01 removed.
    /// </summary>
    private ObjectResult InvalidRequest(string detail) => Problem(
        detail: detail,
        title: "Bad Request",
        statusCode: StatusCodes.Status400BadRequest,
        instance: HttpContext.Request.Path.Value);

    /// <summary>
    /// SEC-05: the column stores a digest of the credential, so the presented string is digested before it is
    /// compared and a read of the table authorizes nothing. The second read is the transition, not a permanent
    /// compatibility branch: rows written before this change carry the wire string itself, and each one is
    /// rewritten to its digest the moment it is redeemed — which is how the table empties of plaintext without
    /// every session of every user being destroyed on deploy day.
    ///
    /// <c>KEY-01</c>: that transition read compares the presented string against the stored column, so unchecked
    /// it accepts the stored value itself — a digest included. Whoever could read the table could then spend a
    /// digest and mint a session, which is precisely the access the digest was added to take away, and the branch
    /// never drains because digest rows are the ones this code writes from now on. Two tests close it, both
    /// load-bearing: the row must not already be a digest (measured: presenting <c>SHA-256(wire)</c> as a refresh
    /// token used to answer 200 and rewrite the row), and the match must be byte-for-byte (measured: these columns
    /// are <c>SQL_Latin1_General_CP1_CI_AS</c>, and SQL Server's <c>=</c> ignores trailing space too, so a
    /// case-flipped or space-padded copy of a legacy credential used to redeem it).
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
        if (record == null || _protector.IsProtected(record.Token) || !string.Equals(record.Token, presented, StringComparison.Ordinal))
            return null;

        record.Token = _protector.Protect(presented);

        // A row old enough to hold a plaintext credential holds a plaintext successor link too; leaving that
        // behind would keep the next session's credential recoverable from this row.
        if (record.ReplacedByToken != null)
            record.ReplacedByToken = _protector.Protect(record.ReplacedByToken);

        return record;
    }

    /// <summary>
    /// SEC-05b: revokes every row this spent credential leads to, following the successor links rotation already
    /// writes. The walk runs <em>through</em> already-revoked rows rather than stopping at the first one, because
    /// that is the only shape the signal ever arrives in: an honest client has consumed every link behind the
    /// stolen copy, so the row the replay points at is revoked before the replay is ever seen, and the live
    /// credential sits at the far end of that run of consumed rows. Stopping at a revoked link would burn nothing
    /// on the case the finding is about. A row's original <c>RevokedAtUtc</c> is left alone — overwriting it would
    /// destroy the record of when the honest client spent it.
    ///
    /// One link is one redemption, and a redemption hands out roughly <c>Jwt:ExpirationMinutes</c> of access, so
    /// seven days of one honest client is on the order of 168 links; the bound is not a policy threshold, it is
    /// the guard against a cycle the data cannot form — a link points at a row created after it, and the digest
    /// column is unique-indexed.
    /// </summary>
    private async Task BurnChainAsync(RefreshToken spent, CancellationToken cancellationToken)
    {
        const int MaxChainLinks = 500;

        var revoked = 0;
        var next = spent.ReplacedByToken;

        // Counted in steps, not in revocations: the run behind a replayed link is mostly already-revoked rows, and
        // a bound that only fires on newly revoked ones would let an exhausted chain be walked without limit.
        for (var steps = 0; next != null && steps < MaxChainLinks; steps++)
        {
            var link = next;
            var row = await _db.RefreshTokens
                .FirstOrDefaultAsync(rt => rt.Token == link, cancellationToken);

            if (row == null) break;

            if (!row.IsRevoked)
            {
                row.RevokedAtUtc = _clock.UtcNow;
                revoked++;
            }

            next = row.ReplacedByToken;
        }

        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogWarning(
            "Refresh token reuse detected for user {UserId}: {RevokedLinks} later credential(s) burned with it. Correlation-Id {CorrelationId}",
            spent.UserId,
            revoked,
            HttpContext.Response.Headers[CorrelationIdMiddleware.CorrelationIdHeader]);
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

        var nowUtc = _clock.UtcNow;

        // The password is verified on every path that reaches a known address, including one that is locked out: a
        // refusal that skips the hash answers in about a millisecond instead of the fifty-odd one costs, and that
        // gap would be a cheaper question than the body already permits.
        var passwordMatches = user != null && _passwordHasher.VerifyPassword(request.Password, user.PasswordHash);

        // One answer for four reasons: no such user, wrong password, inactive account, account inside its lockout
        // window (`SEC-04(b)`). A 423 or a distinct detail would announce that this address exists *and* that
        // somebody is being kept out of it — the oracle the rest of SEC-04 was spent closing.
        if (user == null
            || !passwordMatches
            || !user.IsActive
            || LoginLockoutPolicy.IsLockedOut(user.LockoutUntilUtc, nowUtc))
        {
            if (user != null && !passwordMatches)
                await RecordFailedLoginAsync(user, nowUtc, "auth:login", cancellationToken);

            return Refusal(InvalidCredentials);
        }

        await ResetLoginLockoutAsync(user, nowUtc, cancellationToken);

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

        await _db.SaveChangesAsync(cancellationToken);

        var userDto = new UserDto(user.Id, user.Email, user.FirstName, user.LastName, user.TenantId, roles, permissions);
        return Ok(new AuthResponse(accessToken, refreshTokenString, _clock.UtcNow.AddMinutes(60), userDto));
    }

    /// <summary>
    /// SEC-04(b): writes the streak forward and opens a lockout when the threshold is reached, as one statement that
    /// touches only the columns the auth path owns. The guard is a compare-and-swap on the count this request read:
    /// a losing swap writes nothing, because whoever moved that count first reported a real event this refusal never
    /// saw. Without it, a fifth failure decided on a four-deep streak could land after its owner signed in elsewhere
    /// and lock out the very account the rule exists to protect.
    ///
    /// PW-01: <c>source</c> names the door the wrong password came in, because a guesser who splits attempts between
    /// signing in and changing a password is still one guesser — the same counter, the same threshold, the same
    /// deadline, and only the audit trail tells the two apart.
    /// </summary>
    private Task RecordFailedLoginAsync(User user, DateTime nowUtc, string source, CancellationToken cancellationToken)
    {
        var next = LoginLockoutPolicy.NextFailedCount(user.FailedLoginCount, user.LastFailedLoginAtUtc, nowUtc);
        var lockoutUntil = LoginLockoutPolicy.LockoutUntil(next, user.LockoutUntilUtc, nowUtc);

        return _db.Users
            .Where(u => u.Id == user.Id && u.FailedLoginCount == user.FailedLoginCount)
            .ExecuteUpdateAsync(s => s
                .SetProperty(u => u.FailedLoginCount, next)
                .SetProperty(u => u.LastFailedLoginAtUtc, nowUtc)
                .SetProperty(u => u.LockoutUntilUtc, lockoutUntil)
                .SetProperty(u => u.LastModifiedAtUtc, nowUtc)
                .SetProperty(u => u.LastModifiedBy, source), cancellationToken);
    }

    /// <summary>
    /// A sign-in that got in says the account is not being guessed, so the streak and any deadline go with it.
    /// Set-based for the same reason as the failure write, and it carries <c>LastLoginAtUtc</c> because that is now
    /// the only place the auth path records a successful entry.
    /// </summary>
    private Task ResetLoginLockoutAsync(User user, DateTime nowUtc, CancellationToken cancellationToken) =>
        _db.Users
            .Where(u => u.Id == user.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(u => u.FailedLoginCount, 0)
                .SetProperty(u => u.LastFailedLoginAtUtc, (DateTime?)null)
                .SetProperty(u => u.LockoutUntilUtc, (DateTime?)null)
                .SetProperty(u => u.LastLoginAtUtc, nowUtc)
                .SetProperty(u => u.LastModifiedAtUtc, nowUtc)
                .SetProperty(u => u.LastModifiedBy, "auth:login"), cancellationToken);

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
            // SEC-05b: a credential that was already spent being offered again is the one signal this table can
            // give, and a refusal alone throws it away — the thief keeps the rest of the chain and the owner sees
            // nothing. Burn the chain first, then refuse in exactly the same words as any other refusal, so the
            // signal stays in the data and not in the response.
            if (tokenRecord is { IsRevoked: true, ReplacedByToken: not null })
                await BurnChainAsync(tokenRecord, cancellationToken);

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

    /// <summary>
    /// SEC-05c: ends the session whose refresh credential the caller holds.
    ///
    /// Anonymous and driven by the body rather than <c>[Authorize]</c>, on purpose: the thing being revoked is a
    /// refresh credential, and a client whose access token has already expired is exactly the client that wants to
    /// sign out. The reach is bounded by what the credential already buys — a caller can revoke a row only by
    /// presenting the string that authorizes it, and whoever holds that string can already mint an access token
    /// with it, so this adds no capability beyond ending the session being used.
    ///
    /// It revokes the presented row and nothing else. It is not a reuse signal, so it burns no chain, and it does
    /// not touch the other live sessions the same account holds (`FAN-01`). The answer is always <c>204</c>: a
    /// string that was never issued and a string that is the live tip of a session get the same response, so the
    /// endpoint tells a caller nothing about which credentials exist. It is idempotent for the same reason.
    ///
    /// What it does not do: stop an access token this session already minted. That runs out on
    /// <c>Jwt:ExpirationMinutes</c> like every other refusal here.
    /// </summary>
    [HttpPost("logout")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> Logout([FromBody] LogoutRequest request, CancellationToken cancellationToken)
    {
        var tokenRecord = await FindPresentedTokenAsync(request.RefreshToken, cancellationToken);

        if (tokenRecord is { IsRevoked: false })
            tokenRecord.RevokedAtUtc = _clock.UtcNow;

        // Reached even when nothing was revoked: a row written before SEC-05a carries its credential in plaintext,
        // and touching it here is one more chance to rewrite it as a digest on the way out.
        await _db.SaveChangesAsync(cancellationToken);

        return NoContent();
    }

    /// <summary>
    /// PW-01: lets the person who already holds a session take the account's secret back. Until this existed, a
    /// password could only be replaced by someone with database access, which made a leaked password unfixable by
    /// the one party who can tell it was leaked, and made the shared operator password in the seeding configuration
    /// permanent.
    ///
    /// The refusal is <see cref="InvalidCredentials"/> for every reason this path can say no — no row behind the
    /// token, wrong current password, deactivated account, account inside its lockout window — for the same reason
    /// login says one thing: a route that answers 404 for a vanished user and 401 for a wrong password is an oracle
    /// over who exists, and SEC-04(c) spent its budget closing exactly that.
    ///
    /// Wrong current passwords feed the <see cref="LoginLockoutPolicy"/> streak login uses, not a second one. A
    /// separate counter would be a second door with a looser rule behind it, and the threshold is what stops the
    /// guessing, not the door it comes through.
    ///
    /// The new password is judged <em>after</em> the current one. A shape check first would hand a caller who does
    /// not know the secret a 400-versus-401 distinction to read the account's existence from, and would let the
    /// malformed-password probes pass without ever touching the streak the rule exists to build.
    ///
    /// Success revokes every session the account holds, including the one making the call, and the count goes in
    /// the log because "the stolen device is still signed in" is the claim this endpoint exists to settle. What it
    /// cannot do is reach back into an access token already minted: that runs out on <c>Jwt:ExpirationMinutes</c>,
    /// like every other refusal here. The client is expected to re-login; the response says nothing about it
    /// because <c>204</c> has no body.
    /// </summary>
    [HttpPost("change-password")]
    [Authorize]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request, CancellationToken cancellationToken)
    {
        var userId = _currentUser.UserId;

        var user = userId == null
            ? null
            : await _db.Users
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(u => u.Id == userId.Value && !u.IsDeleted, cancellationToken);

        var nowUtc = _clock.UtcNow;

        // Verified on every path that reaches a row, a locked-out one included, for the reason login gives: a
        // refusal that skips the hash answers in about a millisecond instead of the fifty-odd one costs.
        var currentMatches = user != null
            && !string.IsNullOrEmpty(request.CurrentPassword)
            && _passwordHasher.VerifyPassword(request.CurrentPassword, user.PasswordHash);

        if (user == null
            || !currentMatches
            || !user.IsActive
            || LoginLockoutPolicy.IsLockedOut(user.LockoutUntilUtc, nowUtc))
        {
            if (user != null && !currentMatches)
                await RecordFailedLoginAsync(user, nowUtc, "auth:change-password", cancellationToken);

            return Refusal(InvalidCredentials);
        }

        var shapeRefusal = PasswordPolicy.RefusalFor(request.NewPassword);
        if (shapeRefusal != null) return InvalidRequest(shapeRefusal);

        // Captured before the statement, because a method call inside SetProperty is not translatable and EF would
        // answer by trying to push the whole entity through the database.
        var passwordHash = _passwordHasher.HashPassword(request.NewPassword);
        var id = user.Id;

        // One transaction, because these two writes are one promise. A new password that leaves the old sessions
        // alive has taken nothing back from whoever is using them, and a revocation that runs without the hash
        // change tells the caller they rotated a secret they still hold.
        var revokedSessions = await _db.ExecuteInTransactionAsync(async () =>
        {
            var revoked = await _db.RefreshTokens
                .Where(t => t.UserId == id && t.RevokedAtUtc == null)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAtUtc, nowUtc), cancellationToken);

            await _db.Users
                .Where(u => u.Id == id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(u => u.PasswordHash, passwordHash)
                    .SetProperty(u => u.FailedLoginCount, 0)
                    .SetProperty(u => u.LastFailedLoginAtUtc, (DateTime?)null)
                    .SetProperty(u => u.LockoutUntilUtc, (DateTime?)null)
                    .SetProperty(u => u.LastModifiedAtUtc, nowUtc)
                    .SetProperty(u => u.LastModifiedBy, "auth:change-password"), cancellationToken);

            return revoked;
        }, cancellationToken);

        _logger.LogInformation(
            "Password changed for user {UserId}; {RevokedSessions} active session(s) revoked. Correlation-Id {CorrelationId}",
            user.Id,
            revokedSessions,
            HttpContext.Response.Headers[CorrelationIdMiddleware.CorrelationIdHeader]);

        return NoContent();
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
