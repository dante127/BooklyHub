using System.Net;
using System.Net.Http.Json;
using BooklyHub.Domain.Entities.Identity;
using BooklyHub.Domain.Entities.System;
using BooklyHub.Infrastructure.BackgroundJobs;
using BooklyHub.Infrastructure.Data;
using BooklyHub.IntegrationTests.Infrastructure;
using BooklyHub.IntegrationTests.Security;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BooklyHub.IntegrationTests.Operations;

/// <summary>
/// DB-04 / SEC-08: four append-only tables grew one row per rotation, per replay, per dispatch and per reminder,
/// and nothing in the codebase had ever removed one. These facts pin both halves of the answer — that a row past
/// its stated horizon is gone, and that a row still answering a question is not.
/// </summary>
internal static class Retention
{
    /// <summary>
    /// Every horizon in this file is measured against a pinned clock: the sweep reads <see cref="Application.Common.Interfaces.IClock"/>,
    /// so an unpinned run would compare seeded ages against a moving "now" and a kept row would prove nothing about
    /// the boundary it was supposed to sit on.
    /// </summary>
    public static readonly DateTime Now = new(2026, 10, 4, 9, 0, 0, DateTimeKind.Utc);

    public static async Task<RetentionSweepResult> SweepAsync(
        BooklyHubWebApplicationFactory factory, int batchSize = 2_000)
    {
        var sweep = new RetentionSweepBackgroundService(
            factory.Services.GetRequiredService<IServiceScopeFactory>(),
            factory.Services.GetRequiredService<ILogger<RetentionSweepBackgroundService>>());

        return await sweep.SweepAsync(CancellationToken.None, batchSize);
    }

    public static Task SeedIdempotencyAsync(
        BooklyHubWebApplicationFactory factory, string key, DateTime expiresAtUtc, string body = "{\"ok\":true}") =>
        WithDbAsync(factory, db =>
        {
            db.IdempotencyRecords.Add(new IdempotencyRecord
            {
                Id = key,
                TenantId = null,
                RequestHash = "hash-of-the-original-request",
                ResponseStatusCode = 200,
                ResponseBody = body,
                CreatedAtUtc = expiresAtUtc - RetentionPolicy.IdempotencyWindow,
                ExpiresAtUtc = expiresAtUtc
            });
            return Task.CompletedTask;
        });

    public static Task SeedTokenAsync(
        BooklyHubWebApplicationFactory factory, Guid userId, DateTime expiresAtUtc) =>
        WithDbAsync(factory, db =>
        {
            db.RefreshTokens.Add(new RefreshToken
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Token = Guid.NewGuid().ToString("N"),
                ExpiresAtUtc = expiresAtUtc,
                CreatedAtUtc = expiresAtUtc.AddDays(-7),
                RevokedAtUtc = expiresAtUtc < Now ? expiresAtUtc.AddDays(-7).AddMinutes(30) : null
            });
            return Task.CompletedTask;
        });

    public static Task SeedOutboxAsync(
        BooklyHubWebApplicationFactory factory, DateTime occurredOnUtc, DateTime? processedOnUtc, string? error) =>
        WithDbAsync(factory, db =>
        {
            db.OutboxMessages.Add(new OutboxMessage
            {
                Id = Guid.NewGuid(),
                OccurredOnUtc = occurredOnUtc,
                Type = "AppointmentCreatedEvent",
                Content = "{}",
                ProcessedOnUtc = processedOnUtc,
                Error = error
            });
            return Task.CompletedTask;
        });

    public static Task SeedNotificationAsync(
        BooklyHubWebApplicationFactory factory, Guid tenantId, DateTime? sentAtUtc, bool isSent, string? error = null) =>
        WithDbAsync(factory, db =>
        {
            db.NotificationRecords.Add(new NotificationRecord
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                Recipient = "patient@example.test",
                Channel = "Email",
                Subject = "Reminder",
                Body = "Your appointment is tomorrow.",
                IsSent = isSent,
                SentAtUtc = sentAtUtc,
                Error = error,
                CreatedAtUtc = sentAtUtc ?? Now.AddDays(-91)
            });
            return Task.CompletedTask;
        });

    public static Task<List<string>> IdempotencyKeysAsync(BooklyHubWebApplicationFactory factory) =>
        QueryAsync(factory, db => db.IdempotencyRecords.IgnoreQueryFilters()
            .OrderBy(r => r.Id)
            .Select(r => r.Id)
            .ToListAsync());

    public static Task<int> CountAsync<TEntity>(
        BooklyHubWebApplicationFactory factory, Func<ApplicationDbContext, IQueryable<TEntity>> rows)
        where TEntity : class =>
        QueryAsync(factory, db => rows(db).IgnoreQueryFilters().CountAsync());

    private static async Task WithDbAsync(BooklyHubWebApplicationFactory factory, Func<ApplicationDbContext, Task> action)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await action(db);
        await db.SaveChangesAsync();
    }

    private static async Task<T> QueryAsync<T>(BooklyHubWebApplicationFactory factory, Func<ApplicationDbContext, Task<T>> query)
    {
        using var scope = factory.Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>());
    }
}

public class RetentionIdempotencyTests : IClassFixture<BooklyHubWebApplicationFactory>
{
    private readonly BooklyHubWebApplicationFactory _factory;

    public RetentionIdempotencyTests(BooklyHubWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task APastItsReplayWindow_MustGo_WithTheBodyItStored()
    {
        await Retention.SeedIdempotencyAsync(_factory, "aged:one-hour", Retention.Now.AddSeconds(-1),
            "{\"customerName\":\"Halima\",\"phone\":\"0999111222\"}");
        await Retention.SeedIdempotencyAsync(_factory, "aged:three-days", Retention.Now.AddDays(-3));
        await Retention.SeedIdempotencyAsync(_factory, "live:one-minute", Retention.Now.AddMinutes(1));

        _factory.Clock.Pin(Retention.Now);
        try
        {
            var result = await Retention.SweepAsync(_factory);
            result.IdempotencyRecords.Should().Be(2);

            (await Retention.IdempotencyKeysAsync(_factory))
                .Should().ContainSingle()
                .Which.Should().Be("live:one-minute",
                    "a key inside its replay window still owes its caller the exact response it stored");
        }
        finally
        {
            _factory.Clock.Release();
        }
    }
}

public class RetentionSweepBoundaryTests : IClassFixture<BooklyHubWebApplicationFactory>
{
    private readonly BooklyHubWebApplicationFactory _factory;

    public RetentionSweepBoundaryTests(BooklyHubWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task AKeyStoredThroughTheWire_MustBeKeptForTheWindowThatMintedIt_AndGonePastIt()
    {
        var (_, token) = await TokenReuse.LoginAsync(_factory, "retention.wire@example.test");

        _factory.Clock.Pin(Retention.Now);
        try
        {
            var client = _factory.CreateClient();
            client.DefaultRequestHeaders.Add("Idempotency-Key", "retention:wire");
            var response = await client.PostAsJsonAsync("/api/v1/auth/refresh-token", new { RefreshToken = token });
            response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

            // The middleware stores the caller's key behind the tenant that issued it, so the row is found by the
            // suffix the client sent rather than by a prefix this file would have to guess.
            // The deadline is the middleware's own, so this is the two ends of one rule meeting: a sweep horizon
            // copied from a second constant would delete the row while the key still promises a replay.
            (await Retention.IdempotencyKeysAsync(_factory))
                .Should().Contain(key => key.EndsWith(":retention:wire"));

            _factory.Clock.AdvanceBy(RetentionPolicy.IdempotencyWindow - TimeSpan.FromHours(1));
            var stillLive = await Retention.SweepAsync(_factory);
            stillLive.IdempotencyRecords.Should().Be(0, "an hour before the deadline the response is still owed");
            (await Retention.IdempotencyKeysAsync(_factory))
                .Should().Contain(key => key.EndsWith(":retention:wire"));

            // One hour past the deadline, not a day past it: a horizon that deleted only grossly-aged rows would
            // pass a test that overshot.
            _factory.Clock.AdvanceBy(TimeSpan.FromHours(2));
            (await Retention.SweepAsync(_factory)).IdempotencyRecords.Should().Be(1);
            (await Retention.IdempotencyKeysAsync(_factory))
                .Should().NotContain(key => key.EndsWith(":retention:wire"),
                    "past the window the stored body is a copy of somebody's booking in a table nobody reads");
        }
        finally
        {
            _factory.Clock.Release();
        }
    }
}

public class RetentionRefreshTokenTests : IClassFixture<BooklyHubWebApplicationFactory>
{
    private readonly BooklyHubWebApplicationFactory _factory;

    public RetentionRefreshTokenTests(BooklyHubWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task ATokenPastItsGrace_MustGo_WithoutTouchingTheSessionThatIsLive()
    {
        var (userId, token) = await TokenReuse.LoginAsync(_factory, "retention.tokens@example.test");
        await Retention.SeedTokenAsync(_factory, userId, Retention.Now.AddDays(-31));

        _factory.Clock.Pin(Retention.Now);
        try
        {
            (await Retention.SweepAsync(_factory)).RefreshTokens.Should().Be(1);

            var refreshed = await TokenReuse.Refresh(_factory, token);
            refreshed.StatusCode.Should().Be(HttpStatusCode.OK, await refreshed.Content.ReadAsStringAsync());
        }
        finally
        {
            _factory.Clock.Release();
        }
    }

    [Fact]
    public async Task ATokenInsideItsGrace_MustStay_BecauseItIsStillTheReplaySignal()
    {
        var (userId, _) = await TokenReuse.LoginAsync(_factory, "retention.grace@example.test");
        await Retention.SeedTokenAsync(_factory, userId, Retention.Now.AddDays(-29));

        _factory.Clock.Pin(Retention.Now);
        try
        {
            (await Retention.SweepAsync(_factory)).RefreshTokens.Should().Be(0);

            // 29 days past expiry is still one day of evidence: SEC-05b's chain walk reads spent rows, and a sweep
            // that prunes to the expiry instant would erase the record of a theft while its successors were live.
            (await Retention.CountAsync(_factory, db => db.RefreshTokens.Where(t => t.UserId == userId)))
                .Should().Be(2);
        }
        finally
        {
            _factory.Clock.Release();
        }
    }
}

public class RetentionOutboxTests : IClassFixture<BooklyHubWebApplicationFactory>
{
    private readonly BooklyHubWebApplicationFactory _factory;

    public RetentionOutboxTests(BooklyHubWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task ADeadLetter_MustSurviveTheSweepThatRemovesItsDeliveredNeighbours()
    {
        var aged = Retention.Now - RetentionPolicy.OutboxRetention - TimeSpan.FromDays(1);

        await Retention.SeedOutboxAsync(_factory, aged, aged.AddMinutes(1), null);
        await Retention.SeedOutboxAsync(_factory, aged, aged.AddMinutes(2), "SMTP gave up after 5 retries");
        await Retention.SeedOutboxAsync(_factory, aged, null, null);
        await Retention.SeedOutboxAsync(_factory, Retention.Now.AddDays(-1), Retention.Now.AddDays(-1), null);

        _factory.Clock.Pin(Retention.Now);
        try
        {
            (await Retention.SweepAsync(_factory)).OutboxMessages.Should().Be(1,
                "only the row that went out clean and stopped being anything but a receipt is owed to the horizon");

            (await Retention.CountAsync(_factory, db => db.OutboxMessages.Where(m => m.Error != null)))
                .Should().Be(1, "a delivery that never happened is the only record that it was owed");
            (await Retention.CountAsync(_factory, db => db.OutboxMessages.Where(m => m.ProcessedOnUtc == null)))
                .Should().Be(1, "a pending message is not a dead row, whatever its age");
            (await Retention.CountAsync(_factory, db => db.OutboxMessages.Where(m => m.OccurredOnUtc > Retention.Now.AddDays(-7))))
                .Should().Be(1);
        }
        finally
        {
            _factory.Clock.Release();
        }
    }
}

public class RetentionNotificationTests : IClassFixture<BooklyHubWebApplicationFactory>
{
    private readonly BooklyHubWebApplicationFactory _factory;

    public RetentionNotificationTests(BooklyHubWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task ASentNotificationPastTheHorizon_MustGoInEveryTenant()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var aged = Retention.Now - RetentionPolicy.NotificationRetention - TimeSpan.FromDays(1);

        await Retention.SeedNotificationAsync(_factory, tenantA, aged, true);
        await Retention.SeedNotificationAsync(_factory, tenantB, aged, true);
        await Retention.SeedNotificationAsync(_factory, tenantA, null, false, "Line busy");
        await Retention.SeedNotificationAsync(_factory, tenantA, Retention.Now.AddHours(-1), true);

        // The reminder worker's F-1 lesson, in its second half: a sweep running in no tenant's scope would otherwise
        // be a no-op that still logged a number. Rows from two tenants both going is that evidence.
        _factory.Clock.Pin(Retention.Now);
        try
        {
            (await Retention.SweepAsync(_factory)).NotificationRecords.Should().Be(2);

            (await Retention.CountAsync(_factory, db => db.NotificationRecords.Where(n => n.TenantId == tenantB)))
                .Should().Be(0, "a platform-wide purge does not stop at the tenant the scope happens to name");
            (await Retention.CountAsync(_factory, db => db.NotificationRecords.Where(n => !n.IsSent)))
                .Should().Be(1, "an undelivered reminder is still owed a retry, not a delete");
            (await Retention.CountAsync(_factory, db => db.NotificationRecords.Where(n => n.IsSent)))
                .Should().Be(1);
        }
        finally
        {
            _factory.Clock.Release();
        }
    }
}

public class RetentionBatchTests : IClassFixture<BooklyHubWebApplicationFactory>
{
    private readonly BooklyHubWebApplicationFactory _factory;

    public RetentionBatchTests(BooklyHubWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task ABacklogBiggerThanOneBatch_MustBeEmptied_RatherThanLeftAtTheFirstPage()
    {
        for (var i = 1; i <= 5; i++)
            await Retention.SeedIdempotencyAsync(_factory, $"backlog:{i}", Retention.Now.AddSeconds(-i));

        // batchSize 2 is the point, not a shortcut: a purge that reads one page and stops would report 2 and leave 3
        // rows of somebody's patient data in the table forever, and the number it logs would be the lie.
        _factory.Clock.Pin(Retention.Now);
        try
        {
            (await Retention.SweepAsync(_factory, batchSize: 2)).IdempotencyRecords.Should().Be(5);

            (await Retention.IdempotencyKeysAsync(_factory)).Should().BeEmpty();
        }
        finally
        {
            _factory.Clock.Release();
        }
    }
}
