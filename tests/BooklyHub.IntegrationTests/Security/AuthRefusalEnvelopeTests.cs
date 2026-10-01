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
/// Two findings meet on these three responses. ENV-01: a refused sign-in answered <c>application/json</c> with a
/// single lowercase <c>message</c> while every other 401 in the product answers with the problem envelope, so a
/// client has two documents for one status code and the refusal a user reports carries no correlation id.
/// SEC-04(c): the login box used to say "User account has been deactivated." where everyone else says "Invalid
/// email or password.", which tells a caller which addresses exist. Both are fixed by answering once, in the
/// shape the client already parses — and these tests are what makes "once" mean byte-identical, not similar.
/// </summary>
public class AuthRefusalEnvelopeTests : IClassFixture<BooklyHubWebApplicationFactory>
{
    private const string Password = "correct horse battery staple";
    private const string InvalidCredentials = "Invalid email or password.";

    private readonly BooklyHubWebApplicationFactory _factory;

    public AuthRefusalEnvelopeTests(BooklyHubWebApplicationFactory factory) => _factory = factory;

    private async Task SeedAsync(string email, bool active)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

        db.Users.Add(new User
        {
            Id = Guid.NewGuid(),
            Email = email,
            PasswordHash = hasher.HashPassword(Password),
            FirstName = "Refusal",
            LastName = "Probe",
            TenantId = null,
            IsActive = active
        });

        await db.SaveChangesAsync();
    }

    private Task<HttpResponseMessage> LoginAsync(string email, string password) =>
        _factory.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { Email = email, Password = password });

    [Fact]
    public async Task FailedLogin_MustCarryEveryFieldTheChallengePathCarries()
    {
        await SeedAsync("envelope.live@example.test", active: true);

        var response = await LoginAsync("envelope.live@example.test", "wrong password");

        var problem = await ProblemEnvelope.ReadAsync(response, StatusCodes.Status401Unauthorized);
        problem.GetProperty("status").GetInt32().Should().Be(StatusCodes.Status401Unauthorized);
        ProblemEnvelope.Field(problem, "title").Should().Be("Unauthorized");
        ProblemEnvelope.Field(problem, "instance").Should().Be("/api/v1/auth/login");
        ProblemEnvelope.Field(problem, "detail").Should().Be(InvalidCredentials);
        ProblemEnvelope.Field(problem, "correlationId").Should().Be(ProblemEnvelope.CorrelationIdOf(response),
            "a refusal the user reads on screen has to be findable in the logs by the same handle");

        // These two are the writer's fingerprint. `API.md` §1.1 lists them against the pipeline path, and an
        // auth refusal has them too, which is what tells a client it is the same document rather than an
        // imitation of it.
        problem.TryGetProperty("type", out _).Should().BeTrue();
        problem.TryGetProperty("traceId", out _).Should().BeTrue();
    }

    /// <summary>
    /// The fields that carry the refusal. Measured on the wire: two 401s for the same reason differ only in
    /// <c>traceId</c> and <c>correlationId</c>, which are per-request by design, so comparing the whole body
    /// could never be the assertion — comparing every field except those two is the strongest form available.
    /// </summary>
    private static (string Type, string Title, int Status, string Detail, string Instance) RefusalShape(string body)
    {
        using var document = JsonDocument.Parse(body);
        var problem = document.RootElement;

        return (Text(problem, "type", body),
            Text(problem, "title", body),
            int.Parse(Text(problem, "status", body)),
            Text(problem, "detail", body),
            Text(problem, "instance", body));
    }

    /// <summary>Names the missing field and quotes the body, because a control's value is its failure text.</summary>
    private static string Text(JsonElement problem, string field, string body)
    {
        problem.TryGetProperty(field, out var value).Should().BeTrue(
            $"the refusal envelope must carry {field}, and it does not:\n{body}");

        // GetRawText keeps a number readable as itself; GetString() throws on the JSON number a status is.
        return field == "status" ? value.GetRawText() : value.GetString()!;
    }

    [Fact]
    public async Task UnknownAddress_WrongPassword_AndInactiveAccount_MustAnswerTheSameFields()
    {
        await SeedAsync("enquiry.live@example.test", active: true);
        await SeedAsync("enquiry.off@example.test", active: false);

        var responses = new[]
        {
            await LoginAsync("enquiry.never-issued@example.test", Password),
            await LoginAsync("enquiry.live@example.test", "wrong password"),
            await LoginAsync("enquiry.off@example.test", Password),
        };

        var shapes = new List<(string Type, string Title, int Status, string Detail, string Instance)>();
        var bodies = new List<string>();
        foreach (var response in responses)
        {
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

            var body = await response.Content.ReadAsStringAsync();
            bodies.Add(body);
            shapes.Add(RefusalShape(body));
        }

        // Field-for-field equality is the assertion: "all three say roughly no" is exactly the leak, and a
        // message that merely stops naming the account as deactivated still distinguishes it by wording.
        shapes.Should().AllBeEquivalentTo(shapes[0]);
        shapes[0].Detail.Should().Be(InvalidCredentials);
        shapes[0].Title.Should().Be("Unauthorized");
        bodies[0].Should().NotContain("deactivated",
            "the inactive branch was the whole of the existence oracle left in the login body");
    }

    [Fact]
    public async Task InactiveAccount_WithTheCorrectPassword_MustStillBeRefused()
    {
        await SeedAsync("gate.off@example.test", active: false);

        var response = await LoginAsync("gate.off@example.test", Password);

        // The collapse must not have traded one leak for a lock. The password is correct here, so this 401 is
        // the activity check and nothing else.
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "a deactivated account buying a token would turn the shared message into a real outage");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        // IgnoreQueryFilters because a scope with no tenant bound sees zero rows of everything, which would make
        // this assertion pass whether the gate held or not.
        db.RefreshTokens.IgnoreQueryFilters()
            .Any(t => t.User!.Email == "gate.off@example.test").Should().BeFalse(
                "refusal is only non-distinguishing if it also mints nothing");
    }

    [Fact]
    public async Task RefreshToken_NeverIssued_MustAnswerInTheSameEnvelope()
    {
        var response = await _factory.CreateClient()
            .PostAsJsonAsync("/api/v1/auth/refresh-token", new { RefreshToken = "a-string-nothing-issued" });

        var problem = await ProblemEnvelope.ReadAsync(response, StatusCodes.Status401Unauthorized);
        ProblemEnvelope.Field(problem, "title").Should().Be("Unauthorized");
        ProblemEnvelope.Field(problem, "instance").Should().Be("/api/v1/auth/refresh-token");
        ProblemEnvelope.Field(problem, "detail").Should().Contain("expired",
            "one body already covers never-issued, expired and revoked; the wording stays useful to a client");
    }
}
