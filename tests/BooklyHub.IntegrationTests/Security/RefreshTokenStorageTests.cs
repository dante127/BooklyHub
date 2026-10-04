using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
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
/// SEC-05: the table used to hold, in a plain <c>nvarchar</c> column, the exact string that buys a fresh access
/// token for seven days. Anyone who could read the table could be a user. These tests pin the stored value to a
/// digest of the credential, pin the wire credential to still work, and pin the one row shape that pre-dates the
/// change — a plaintext row — to being rewritten the moment it is redeemed rather than being abandoned.
/// </summary>
public class RefreshTokenStorageTests : IClassFixture<BooklyHubWebApplicationFactory>
{
    private const string Password = "correct horse battery staple";

    private readonly BooklyHubWebApplicationFactory _factory;

    public RefreshTokenStorageTests(BooklyHubWebApplicationFactory factory) => _factory = factory;

    private async Task<Guid> SeedUserAsync(string email)
    {
        var id = Guid.NewGuid();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

        db.Users.Add(new User
        {
            Id = id,
            Email = email,
            PasswordHash = hasher.HashPassword(Password),
            FirstName = "Hash",
            LastName = "Store",
            TenantId = null
        });

        await db.SaveChangesAsync();
        return id;
    }

    private async Task<string> LoginAsync(string email)
    {
        var response = await _factory.CreateClient()
            .PostAsJsonAsync("/api/v1/auth/login", new { Email = email, Password });

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("refreshToken").GetString()!;
    }

    private Task<HttpResponseMessage> RefreshAsync(string refreshToken) =>
        _factory.CreateClient().PostAsJsonAsync("/api/v1/auth/refresh-token", new { RefreshToken = refreshToken });

    private async Task<string> SuccessorFromResponseAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("refreshToken").GetString()!;
    }

    private async Task<List<RefreshToken>> RowsAsync(params string[] storedValues)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.RefreshTokens.IgnoreQueryFilters()
            .Where(t => storedValues.Contains(t.Token))
            .ToListAsync();
    }

    [Fact]
    public async Task TheStoredCredential_MustNotBeTheStringThatAuthorizesIt()
    {
        var email = "sec05a.stored@example.test";
        await SeedUserAsync(email);
        var wire = await LoginAsync(email);

        (await RowsAsync(wire)).Should().BeEmpty(
            "the exact string that buys an access token must not be recoverable from a read of the table");

        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(wire))).ToLowerInvariant();
        var rows = await RowsAsync(digest);

        rows.Should().ContainSingle(
            "the row is keyed by the digest of what the client presents, so a table read authorizes nothing");
        rows[0].Token.Should().Be(digest, "SHA-256 over the credential's UTF-8 bytes, lowercase hex");
        rows[0].Token.Length.Should().Be(64);
        rows[0].RevokedAtUtc.Should().BeNull("issuing a session is not revoking one");
    }

    [Fact]
    public async Task TheWireCredential_MustStillRedeemNowThatTheRowIsDigested()
    {
        var email = "sec05a.redeem@example.test";
        await SeedUserAsync(email);
        var wire = await LoginAsync(email);

        var response = await RefreshAsync(wire);

        // The write side digests and the read side must digest the same way, or every session in the product
        // stops working the day this ships while every unit of storage still looks correct.
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task APreChangePlaintextRow_MustStillWorkAndLeaveNoPlaintextBehind()
    {
        var email = "sec05a.legacy@example.test";
        var userId = await SeedUserAsync(email);

        // Exactly the row shape the previous code wrote: the credential itself, and no digest anywhere.
        var legacyRaw = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.RefreshTokens.Add(new RefreshToken
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Token = legacyRaw,
                ExpiresAtUtc = _factory.Clock.UtcNow.AddDays(7),
                CreatedAtUtc = _factory.Clock.UtcNow
            });
            await db.SaveChangesAsync();
        }

        (await RowsAsync(legacyRaw)).Should().ContainSingle("the legacy row was seeded and is stored as presented");

        var response = await RefreshAsync(legacyRaw);
        var successor = await SuccessorFromResponseAsync(response);

        // The redemption succeeded on the old shape and, as it left, rewrote it — this is what makes the change
        // cost nothing at deploy time instead of logging every user out.
        (await RowsAsync(legacyRaw)).Should().BeEmpty(
            "a redeemed legacy row must not keep the plaintext credential behind it");

        var legacyDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(legacyRaw))).ToLowerInvariant();
        (await RowsAsync(legacyDigest)).Should().ContainSingle(
            "the upgraded row keeps pointing at the same session through its digest");

        (await RowsAsync(successor)).Should().BeEmpty("the successor it minted is stored digested too");
    }

    [Fact]
    public async Task TheSuccessorLink_MustNotHandTheNextSessionSCredentialToWhoeverReadsTheRow()
    {
        var email = "sec05a.link@example.test";
        await SeedUserAsync(email);
        var wire = await LoginAsync(email);

        var successor = await SuccessorFromResponseAsync(await RefreshAsync(wire));

        var spentDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(wire))).ToLowerInvariant();
        var spentRows = await RowsAsync(spentDigest);
        spentRows.Should().ContainSingle("the presented credential is stored as its digest");
        var spent = spentRows.Single();

        // Recording the successor is what makes a replay recognisable at all, but recording it in plaintext would
        // put the *live* credential inside the spent row — the same leak, one column over.
        spent.ReplacedByToken.Should().NotBeNull();
        spent.ReplacedByToken.Should().NotBe(successor);
        spent.ReplacedByToken.Should().Be(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(successor))).ToLowerInvariant());
        spent.RevokedAtUtc.Should().NotBeNull();
    }
}
