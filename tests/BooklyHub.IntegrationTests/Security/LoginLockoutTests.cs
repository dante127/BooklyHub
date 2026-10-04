using System.Net;
using System.Net.Http.Json;
using BooklyHub.Application.Security;
using BooklyHub.Domain.Entities.Identity;
using BooklyHub.Infrastructure.Data;
using BooklyHub.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BooklyHub.IntegrationTests.Security;

/// <summary>
/// SEC-04(b): the address tier says how fast a machine may knock, and nothing said how many wrong passwords one
/// account would absorb. Measured before the change: ten wrong passwords at one known address all returned 401,
/// <c>Users</c> carried no failure column at all, and the owner's own correct attempt — eleventh in the minute —
/// was refused 429 by the tier. These facts pin the account rule that fires first, and pin that it stays a delay
/// rather than becoming a lock a guesser can impose on a clinic.
/// </summary>
/// <remarks>
/// One fact per class: the auth tier is ten requests per minute and its partition is the fixture's own host, so a
/// class holding two streaks would be sharing a permit budget with itself.
/// </remarks>
internal static class LoginAttempt
{
    public const string Password = "correct horse battery staple";

    /// <summary>
    /// The streak is a set of deadlines, so every fact here pins the clock and compares to exact instants:
    /// <see cref="TestClock.UtcNow"/> is the real system clock while unpinned, and an unpinned assertion on
    /// <c>LockoutUntilUtc</c> compares the request's read of it with a later one made by the test itself — a
    /// difference of microseconds that would have failed the fact without proving anything about the rule.
    /// </summary>
    public static readonly DateTime Now = new(2026, 10, 4, 9, 0, 0, DateTimeKind.Utc);

    public static async Task<Guid> SeedAsync(BooklyHubWebApplicationFactory factory, string email)
    {
        var id = Guid.NewGuid();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        db.Users.Add(new User
        {
            Id = id,
            Email = email,
            PasswordHash = scope.ServiceProvider.GetRequiredService<IPasswordHasher>().HashPassword(Password),
            FirstName = "Knock",
            LastName = "Count",
            TenantId = null
        });

        await db.SaveChangesAsync();
        return id;
    }

    public static async Task<HttpStatusCode> TryAsync(BooklyHubWebApplicationFactory factory, string email, string password)
    {
        var response = await factory.CreateClient()
            .PostAsJsonAsync("/api/v1/auth/login", new { Email = email, Password = password });

        // A 429 here would mean the address tier answered, not the account rule, and the fact below would prove
        // nothing about the rule it names — the threshold is five precisely so the tier cannot be the cause.
        response.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests, await response.Content.ReadAsStringAsync());
        return response.StatusCode;
    }

    public static async Task<User> ReadAsync(BooklyHubWebApplicationFactory factory, Guid userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.Users.IgnoreQueryFilters().FirstAsync(u => u.Id == userId);
    }

    /// <summary>
    /// What a successful sign-in from another device leaves behind: a cleared streak. Written through the same
    /// shape the auth path uses, so this is not the test asserting a behavior its own helper does not perform.
    /// </summary>
    public static async Task ClearStreakAsync(BooklyHubWebApplicationFactory factory, Guid userId, DateTime nowUtc)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.Users
            .Where(u => u.Id == userId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(u => u.FailedLoginCount, 0)
                .SetProperty(u => u.LastFailedLoginAtUtc, (DateTime?)null)
                .SetProperty(u => u.LockoutUntilUtc, (DateTime?)null)
                .SetProperty(u => u.LastLoginAtUtc, nowUtc));
    }
}

public class LoginLockoutThresholdTests : IClassFixture<BooklyHubWebApplicationFactory>
{
    private readonly BooklyHubWebApplicationFactory _factory;

    public LoginLockoutThresholdTests(BooklyHubWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task FiveWrongPasswords_MustStopTheRightPasswordFromSigningIn()
    {
        var email = "sec04b.threshold@example.test";
        var userId = await LoginAttempt.SeedAsync(_factory, email);

        _factory.Clock.Pin(LoginAttempt.Now);
        try
        {
            for (var i = 1; i <= LoginLockoutPolicy.MaxFailedAttempts; i++)
                (await LoginAttempt.TryAsync(_factory, email, "wrong-" + i))
                    .Should().Be(HttpStatusCode.Unauthorized);

            (await LoginAttempt.TryAsync(_factory, email, LoginAttempt.Password))
                .Should().Be(HttpStatusCode.Unauthorized,
                    "the fifth failure closed the door, so the correct password is not a way back in");

            var user = await LoginAttempt.ReadAsync(_factory, userId);
            user.FailedLoginCount.Should().Be(5);
            user.LastFailedLoginAtUtc.Should().Be(LoginAttempt.Now);
            user.LockoutUntilUtc.Should().Be(LoginAttempt.Now + LoginLockoutPolicy.LockoutDuration);
            user.LastLoginAtUtc.Should().BeNull("no attempt in this streak ever got in");
        }
        finally
        {
            _factory.Clock.Release();
        }
    }
}

public class LoginLockoutUnderThresholdTests : IClassFixture<BooklyHubWebApplicationFactory>
{
    private readonly BooklyHubWebApplicationFactory _factory;

    public LoginLockoutUnderThresholdTests(BooklyHubWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task ASignInThatGotIn_MustClearTheStreakRatherThanJoinIt()
    {
        var email = "sec04b.under@example.test";
        var userId = await LoginAttempt.SeedAsync(_factory, email);

        _factory.Clock.Pin(LoginAttempt.Now);
        try
        {
            for (var i = 1; i < LoginLockoutPolicy.MaxFailedAttempts; i++)
                (await LoginAttempt.TryAsync(_factory, email, "wrong-" + i)).Should().Be(HttpStatusCode.Unauthorized);

            (await LoginAttempt.TryAsync(_factory, email, LoginAttempt.Password))
                .Should().Be(HttpStatusCode.OK, "four mistypes are a person, not a guesser");

            var user = await LoginAttempt.ReadAsync(_factory, userId);
            user.FailedLoginCount.Should().Be(0, "a streak that ended in a correct password was not a guessing run");
            user.LastFailedLoginAtUtc.Should().BeNull();
            user.LockoutUntilUtc.Should().BeNull();
            user.LastLoginAtUtc.Should().Be(LoginAttempt.Now);

            // And the fifth failure after a success is the first of a new streak, not the fifth of the old one.
            (await LoginAttempt.TryAsync(_factory, email, "wrong-after")).Should().Be(HttpStatusCode.Unauthorized);
            (await LoginAttempt.ReadAsync(_factory, userId)).FailedLoginCount.Should().Be(1);
        }
        finally
        {
            _factory.Clock.Release();
        }
    }
}

public class LoginLockoutShapeTests : IClassFixture<BooklyHubWebApplicationFactory>
{
    private const string InvalidCredentials = "Invalid email or password.";

    private readonly BooklyHubWebApplicationFactory _factory;

    public LoginLockoutShapeTests(BooklyHubWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task ALockedOutAccount_MustAnswerExactlyLikeAWrongPassword()
    {
        var email = "sec04b.shape@example.test";
        await LoginAttempt.SeedAsync(_factory, email);

        _factory.Clock.Pin(LoginAttempt.Now);
        try
        {
            for (var i = 1; i <= LoginLockoutPolicy.MaxFailedAttempts; i++)
                await LoginAttempt.TryAsync(_factory, email, "wrong-" + i);

            var lockedResponse = await _factory.CreateClient().PostAsJsonAsync("/api/v1/auth/login",
                new { Email = email, Password = LoginAttempt.Password });
            var locked = await ProblemEnvelope.ReadAsync(lockedResponse, StatusCodes.Status401Unauthorized);

            var strangerResponse = await _factory.CreateClient().PostAsJsonAsync("/api/v1/auth/login",
                new { Email = "nobody.at.all@example.test", Password = LoginAttempt.Password });
            var stranger = await ProblemEnvelope.ReadAsync(strangerResponse, StatusCodes.Status401Unauthorized);

            // A 423 or a detail naming a lockout would tell the caller that this address exists and that somebody
            // is being kept out of it — the oracle the rest of SEC-04 was spent closing, in exchange for a nicer
            // status code. The status the wire carries is the assertion: ReadAsync above requires 401.
            lockedResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
                "a lockout is not a 423, because 423 is an answer");

            foreach (var field in new[] { "type", "title", "detail", "instance" })
            {
                ProblemEnvelope.Field(locked, field).Should().Be(ProblemEnvelope.Field(stranger, field),
                    $"a locked account and an unknown address must agree on {field}");
            }

            ProblemEnvelope.Field(locked, "detail").Should().Be(InvalidCredentials);
        }
        finally
        {
            _factory.Clock.Release();
        }
    }
}

public class LoginLockoutLiftTests : IClassFixture<BooklyHubWebApplicationFactory>
{
    private readonly BooklyHubWebApplicationFactory _factory;

    public LoginLockoutLiftTests(BooklyHubWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task TheLockout_MustLiftByItselfWhenItsWindowTurns()
    {
        var email = "sec04b.lift@example.test";
        var userId = await LoginAttempt.SeedAsync(_factory, email);

        _factory.Clock.Pin(LoginAttempt.Now);
        try
        {
            for (var i = 1; i <= LoginLockoutPolicy.MaxFailedAttempts; i++)
                await LoginAttempt.TryAsync(_factory, email, "wrong-" + i);

            (await LoginAttempt.ReadAsync(_factory, userId)).LockoutUntilUtc
                .Should().Be(LoginAttempt.Now + LoginLockoutPolicy.LockoutDuration);

            // Advanced to the deadline itself, not past it: the boundary is exclusive, so the account answers
            // again at the promised instant rather than a second later on someone's arithmetic.
            _factory.Clock.AdvanceBy(LoginLockoutPolicy.LockoutDuration);

            (await LoginAttempt.TryAsync(_factory, email, LoginAttempt.Password))
                .Should().Be(HttpStatusCode.OK,
                    "the penalty is a delay, not a sentence — nothing here needs an administrator to undo it");
        }
        finally
        {
            _factory.Clock.Release();
        }

        var user = await LoginAttempt.ReadAsync(_factory, userId);
        user.LockoutUntilUtc.Should().BeNull();
        user.FailedLoginCount.Should().Be(0);
    }
}

public class LoginLockoutWindowTests : IClassFixture<BooklyHubWebApplicationFactory>
{
    private readonly BooklyHubWebApplicationFactory _factory;

    public LoginLockoutWindowTests(BooklyHubWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task FailuresSeparatedByMoreThanTheWindow_MustNotStackIntoALockout()
    {
        var email = "sec04b.window@example.test";
        var userId = await LoginAttempt.SeedAsync(_factory, email);

        _factory.Clock.Pin(LoginAttempt.Now);
        try
        {
            for (var i = 1; i <= 3; i++)
                await LoginAttempt.TryAsync(_factory, email, "wrong-a-" + i);

            (await LoginAttempt.ReadAsync(_factory, userId)).FailedLoginCount.Should().Be(3,
                "the first run was live and simply had not reached the threshold");

            _factory.Clock.AdvanceBy(LoginLockoutPolicy.Window + TimeSpan.FromMinutes(1));

            for (var i = 1; i <= 3; i++)
                (await LoginAttempt.TryAsync(_factory, email, "wrong-b-" + i))
                    .Should().Be(HttpStatusCode.Unauthorized);

            (await LoginAttempt.TryAsync(_factory, email, LoginAttempt.Password))
                .Should().Be(HttpStatusCode.OK,
                    "six mistypes across two windows are two accidents, not one guessing run");
        }
        finally
        {
            _factory.Clock.Release();
        }

        var user = await LoginAttempt.ReadAsync(_factory, userId);
        user.FailedLoginCount.Should().Be(0);
        user.LastLoginAtUtc.Should().NotBeNull();
    }
}

/// <summary>
/// The refusal is decided on a read of the streak and then written back, so anything another device clears in
/// that gap is a fact this request never saw. This is the one place the compare-and-swap in
/// <c>RecordFailedLoginAsync</c> is load-bearing, so the write lands inside the request's own window through the
/// host's read interceptor rather than being raced with <c>Task.WhenAll</c>, which would prove nothing about
/// interleaving.
/// </summary>
public class LoginLockoutStaleFailureTests : IClassFixture<BooklyHubWebApplicationFactory>
{
    /// <summary>
    /// The sign-in's read of the account. Nothing else in this request selects from that table, so matching it is
    /// how the fact knows its clear landed between the read and the write rather than somewhere else.
    /// </summary>
    private const string AccountRead = "FROM [Users]";

    private readonly BooklyHubWebApplicationFactory _factory;

    public LoginLockoutStaleFailureTests(BooklyHubWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task AFailureDecidedOnAClearedStreak_MustNotLockTheAccountOut()
    {
        var email = "sec04b.stale@example.test";
        var userId = await LoginAttempt.SeedAsync(_factory, email);

        _factory.Clock.Pin(LoginAttempt.Now);
        try
        {
            for (var i = 1; i < LoginLockoutPolicy.MaxFailedAttempts; i++)
                await LoginAttempt.TryAsync(_factory, email, "wrong-" + i);

            (await LoginAttempt.ReadAsync(_factory, userId)).FailedLoginCount
                .Should().Be(4, "the streak is one failure short, so the window below is a real one");

            _factory.QueryInterceptor.DisarmAfterRead();
            _factory.QueryInterceptor.ArmAfterRead(AccountRead,
                _ => LoginAttempt.ClearStreakAsync(_factory, userId, LoginAttempt.Now));

            (await LoginAttempt.TryAsync(_factory, email, "wrong-while-away"))
                .Should().Be(HttpStatusCode.Unauthorized);

            _factory.QueryInterceptor.ArmHits.Should().Be(1,
                "the clearing write has to land between this refusal's read and its write, or the fact races nothing");

            var user = await LoginAttempt.ReadAsync(_factory, userId);
            user.FailedLoginCount.Should().Be(0,
                "the fifth failure was counted against a streak that had already been cleared");
            user.LockoutUntilUtc.Should().BeNull(
                "a stale failure must not lock out the person it exists to protect");
            user.LastLoginAtUtc.Should().Be(LoginAttempt.Now, "the sign-in elsewhere is not this refusal's to undo");
        }
        finally
        {
            _factory.QueryInterceptor.DisarmAfterRead();
            _factory.Clock.Release();
        }
    }
}

public class LoginLockoutRefreshTests : IClassFixture<BooklyHubWebApplicationFactory>
{
    private readonly BooklyHubWebApplicationFactory _factory;

    public LoginLockoutRefreshTests(BooklyHubWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task ALockedOutAccount_MustStillRefreshATokenItAlreadyHolds()
    {
        var email = "sec04b.refresh@example.test";
        var (userId, token) = await TokenReuse.LoginAsync(_factory, email);

        _factory.Clock.Pin(LoginAttempt.Now);
        try
        {
            for (var i = 1; i <= LoginLockoutPolicy.MaxFailedAttempts; i++)
                await LoginAttempt.TryAsync(_factory, email, "wrong-" + i);

            (await LoginAttempt.ReadAsync(_factory, userId)).LockoutUntilUtc
                .Should().Be(LoginAttempt.Now + LoginLockoutPolicy.LockoutDuration,
                    "the account is locked, so this fact is testing the right state");

            // The lockout answers the question "may a new session start". It is not a revocation, and making it one
            // would hand anyone who knows an address a way to log its owner out; SEC-05c's logout is the verb that
            // ends a session, and it belongs to whoever holds the credential.
            var response = await TokenReuse.Refresh(_factory, token);

            response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        }
        finally
        {
            _factory.Clock.Release();
        }
    }
}
