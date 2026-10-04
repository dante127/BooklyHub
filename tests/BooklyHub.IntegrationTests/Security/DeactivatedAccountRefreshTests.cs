using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
/// ACT-01: the account's own state was a rule of the login box only. A user deactivated after signing in kept
/// an unexpired refresh token, and that token bought a fresh 60-minute access token for its full seven days —
/// so switching an account off was a delay, not a stop. These tests deactivate an account *after* it holds a
/// token, which is the only sequence that can tell the two rules apart.
/// </summary>
public class DeactivatedAccountRefreshTests : IClassFixture<BooklyHubWebApplicationFactory>
{
    private const string Password = "correct horse battery staple";
    private const string RefreshRefusal = "Invalid or expired refresh token.";

    private readonly BooklyHubWebApplicationFactory _factory;

    public DeactivatedAccountRefreshTests(BooklyHubWebApplicationFactory factory) => _factory = factory;

    private async Task SeedAsync(string email)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

        db.Users.Add(new User
        {
            Id = Guid.NewGuid(),
            Email = email,
            PasswordHash = hasher.HashPassword(Password),
            FirstName = "Rot",
            LastName = "Probe",
            TenantId = null
        });

        await db.SaveChangesAsync();
    }

    private async Task<string> LoginAndTakeTokenAsync(string email)
    {
        var login = await _factory.CreateClient().PostAsJsonAsync("/api/v1/auth/login",
            new { Email = email, Password });

        login.StatusCode.Should().Be(HttpStatusCode.OK, await login.Content.ReadAsStringAsync());

        using var json = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("refreshToken").GetString()!;
    }

    private Task<HttpResponseMessage> RefreshAsync(string refreshToken) =>
        _factory.CreateClient().PostAsJsonAsync("/api/v1/auth/refresh-token", new { RefreshToken = refreshToken });

    /// <summary>
    /// Writes the account's state the way an administrator's action would, bypassing the soft-delete filter so a
    /// deleted row can be reached at all.
    /// </summary>
    private async Task ChangeAccountAsync(string email, bool? active = null, bool? deleted = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var user = await db.Users.IgnoreQueryFilters().FirstAsync(u => u.Email == email);

        if (active.HasValue) user.IsActive = active.Value;
        if (deleted.HasValue) user.IsDeleted = deleted.Value;

        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task DeactivatingAnAccount_MustStopTheRefreshTokenItAlreadyIssued()
    {
        await SeedAsync("act01.deactivate@example.test");
        var token = await LoginAndTakeTokenAsync("act01.deactivate@example.test");

        // The token is live here: it has not been rotated, expired or revoked. Only the account changed.
        await ChangeAccountAsync("act01.deactivate@example.test", active: false);

        var refused = await RefreshAsync(token);

        refused.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "a credential the account no longer owns cannot be worth a token however valid its string is");

        var problem = await ProblemEnvelope.ReadAsync(refused, StatusCodes.Status401Unauthorized);
        ProblemEnvelope.Field(problem, "detail").Should().Be(RefreshRefusal,
            "the refusal must not become a second way to learn an account was switched off");

        // The same refusal-bearing fields a token nobody ever issued gets. Whole bodies cannot be compared:
        // traceId and correlationId are per-request by design, which is what made the identical claim on the
        // login tests fail for the wrong reason.
        var stranger = await ProblemEnvelope.ReadAsync(
            await RefreshAsync("a-string-nothing-issued"), StatusCodes.Status401Unauthorized);

        foreach (var field in new[] { "type", "title", "detail", "instance" })
        {
            ProblemEnvelope.Field(stranger, field).Should().Be(ProblemEnvelope.Field(problem, field),
                $"refusing a switched-off account and refusing a stranger must agree on {field}");
        }

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.RefreshTokens.IgnoreQueryFilters()
            .CountAsync(t => t.User!.Email == "act01.deactivate@example.test"))
            .Should().Be(1, "a refused refresh must mint nothing, not revoke and replace");
    }

    [Fact]
    public async Task SoftDeletedAccounts_Token_MustBeRefusedByTheFilterAlone()
    {
        await SeedAsync("act01.delete@example.test");
        var token = await LoginAndTakeTokenAsync("act01.delete@example.test");

        await ChangeAccountAsync("act01.delete@example.test", deleted: true);

        var refused = await RefreshAsync(token);

        // No `IsDeleted` term exists in the controller. This fact is the evidence that the global query filter
        // is what removes the row, so adding such a term would be dead validation rather than defence.
        refused.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "the deleted user must reach the action as no user at all");

        var problem = await ProblemEnvelope.ReadAsync(refused, StatusCodes.Status401Unauthorized);
        ProblemEnvelope.Field(problem, "detail").Should().Be(RefreshRefusal);
    }

    [Fact]
    public async Task AnActiveAccount_MustStillRotateItsToken()
    {
        await SeedAsync("act01.active@example.test");
        var original = await LoginAndTakeTokenAsync("act01.active@example.test");

        var response = await RefreshAsync(original);

        // The other half of the finding: a stop for one account cannot become a stop for all of them.
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("refreshToken").GetString().Should().NotBe(original);
    }
}
