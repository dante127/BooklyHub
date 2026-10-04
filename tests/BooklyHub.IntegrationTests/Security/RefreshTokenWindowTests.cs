using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BooklyHub.Application.Security;
using BooklyHub.Domain.Entities.Identity;
using BooklyHub.Infrastructure.Data;
using BooklyHub.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BooklyHub.IntegrationTests.Security;

/// <summary>
/// A revoked or expired refresh token is the last thing standing between a stolen credential string and a
/// fresh access token, and its life was counted against the system clock while every other deadline in the
/// product is counted against <see cref="BooklyHub.Application.Common.Interfaces.IClock"/>. These tests move
/// that clock across the token's own deadline and require the endpoint to change its answer.
/// </summary>
public sealed class RefreshTokenWindowTests : IAsyncLifetime
{
    private readonly BooklyHubWebApplicationFactory _factory = new();

    public Task InitializeAsync() => _factory.InitializeAsync();

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private async Task SeedUserAsync(string email)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

        db.Users.Add(new User
        {
            Id = Guid.NewGuid(),
            Email = email,
            PasswordHash = hasher.HashPassword("correct horse battery staple"),
            FirstName = "Nadia",
            LastName = "Khoury",
            TenantId = null
        });

        await db.SaveChangesAsync();
    }

    private async Task<string> LoginAsync(string email)
    {
        var response = await _factory.CreateClient()
            .PostAsJsonAsync("/api/v1/auth/login", new { Email = email, Password = "correct horse battery staple" });

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("refreshToken").GetString()!;
    }

    private Task<HttpResponseMessage> RefreshAsync(string refreshToken) =>
        _factory.CreateClient().PostAsJsonAsync("/api/v1/auth/refresh-token", new { RefreshToken = refreshToken });

    [Fact]
    public async Task RefreshToken_UsedPastItsSevenDayLife_MustBeRefused()
    {
        await SeedUserAsync("expiry.past@example.test");
        var token = await LoginAsync("expiry.past@example.test");

        try
        {
            _factory.Clock.AdvanceBy(TimeSpan.FromDays(7) + TimeSpan.FromMinutes(1));

            var response = await RefreshAsync(token);

            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
                "the token outlived its deadline, so it buys nothing however valid its string is");
            (await response.Content.ReadAsStringAsync()).Should().Contain("expired");
        }
        finally
        {
            _factory.Clock.Release();
        }
    }

    [Fact]
    public async Task RefreshToken_UsedJustInsideItsLife_MustStillRotate()
    {
        await SeedUserAsync("expiry.inside@example.test");
        var token = await LoginAsync("expiry.inside@example.test");

        try
        {
            _factory.Clock.AdvanceBy(TimeSpan.FromDays(7) - TimeSpan.FromHours(1));

            var response = await RefreshAsync(token);

            response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            json.RootElement.GetProperty("refreshToken").GetString().Should().NotBe(token,
                "a live refresh token is consumed and replaced, not reused");
        }
        finally
        {
            _factory.Clock.Release();
        }
    }

    [Fact]
    public async Task RefreshToken_ConsumedByRotation_MustBeRefusedWithTheClockUnmoved()
    {
        await SeedUserAsync("expiry.rotated@example.test");
        var original = await LoginAsync("expiry.rotated@example.test");

        (await RefreshAsync(original)).StatusCode.Should().Be(HttpStatusCode.OK);

        var replay = await RefreshAsync(original);

        replay.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "revocation is a separate rule from expiry and must hold seconds after the token worked");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var protector = scope.ServiceProvider.GetRequiredService<IRefreshTokenProtector>();

        // SEC-05: the row is keyed by the digest of the string the client presented, not by that string.
        var rows = await db.RefreshTokens.IgnoreQueryFilters()
            .Where(t => t.Token == protector.Protect(original)).ToListAsync();
        rows.Should().ContainSingle();
        rows[0].RevokedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task RefreshToken_ThatWasNeverIssued_MustBeRefused()
    {
        await SeedUserAsync("expiry.unknown@example.test");
        await LoginAsync("expiry.unknown@example.test");

        var response = await RefreshAsync("a-string-nothing-issued");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
