using System.Net;
using System.Net.Http.Json;
using BooklyHub.Application.Common.Interfaces;
using BooklyHub.Application.Security;
using BooklyHub.Domain.Entities.Identity;
using BooklyHub.Infrastructure.Data;
using BooklyHub.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BooklyHub.IntegrationTests.Security;

/// <summary>
/// SEC-09, first half: the three outbound senders this build registers answered with a log line instead of a
/// delivery, and the line carried the payload — a patient's address and the subject of their appointment mail at
/// <c>NotificationAndCacheServices.cs:87</c>, their phone number and the sentence "your appointment is confirmed
/// for &lt;when&gt;" at <c>:103</c>. That is the level every environment runs at, so a support question about a
/// reminder bought a copy of the patient's schedule, and the copy travelled to whatever sink the console is
/// pointed at rather than staying in the row that already has it.
/// </summary>
/// <remarks>
/// These facts read the host's own log rather than the source text, because a claim about what a log carries is
/// not answerable from the file that writes it — an assertion over the message template would be a test comparing
/// its own diff. The dispatch is made through the container so the implementation named by the binding is the one
/// under test.
/// </remarks>
public class OutboundDispatchLogTests : IClassFixture<BooklyHubWebApplicationFactory>
{
    private readonly BooklyHubWebApplicationFactory _factory;

    public OutboundDispatchLogTests(BooklyHubWebApplicationFactory factory) => _factory = factory;

    /// <summary>
    /// The reason every fact below is allowed to pass by finding nothing. This one passes by finding something:
    /// <c>Program.cs</c> swaps Serilog in with <c>UseSerilog()</c>, and the <see cref="ILoggerFactory"/> that
    /// survives in the container is Serilog's own, which never enumerates the registered
    /// <see cref="ILoggerProvider"/> collection — a provider registered the obvious way is listed by
    /// <c>GetServices&lt;ILoggerProvider&gt;()</c> and receives nothing at all. The collector therefore hangs on
    /// the <see cref="ILogger{T}"/> seam instead, and this fact is what notices if that seam ever stops carrying
    /// the host's writing.
    /// </summary>
    [Fact]
    public void TheCollector_MustSeeALineTheHostWrites()
    {
        var marker = $"collector-alive-{Guid.NewGuid():N}";

        using var scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ILogger<OutboundDispatchLogTests>>()
            .LogInformation("harness {Marker}", marker);

        var line = _factory.Logs.Containing(marker).SingleOrDefault();
        line.Should().NotBeNull(
            "a collector the host never writes to is empty forever, and every absence this file asserts would read as privacy");
        line!.Level.Should().Be(LogLevel.Information);
        line.Category.Should().Be(typeof(OutboundDispatchLogTests).FullName);
    }

    [Fact]
    public async Task ADispatch_MustLeaveItsChannelAndItsSizeAndNoneOfTheCustomer()
    {
        var token = Guid.NewGuid().ToString("N");
        var address = $"ziad.{token}@privacy.test";
        var phone = "+9639" + token.Substring(0, 7);
        var subject = $"Appointment Confirmation - Dental Clean ({token})";
        var body = $"<p>Dear Ziad {token},</p><p>Your appointment has been booked.</p>";
        var sms = $"Your appointment is confirmed. Ref {token}";
        var recipientId = Guid.NewGuid().ToString();
        var pushTitle = $"Ziad {token}, your visit is tomorrow";
        var pushMessage = $"Body {token}";

        using var scope = _factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IEmailSender>()
            .SendEmailAsync(address, subject, body, CancellationToken.None);
        await scope.ServiceProvider.GetRequiredService<ISmsSender>()
            .SendSmsAsync(phone, sms, CancellationToken.None);
        await scope.ServiceProvider.GetRequiredService<IPushNotificationSender>()
            .SendPushAsync(recipientId, pushTitle, pushMessage, CancellationToken.None);

        // The line still has to be there. These senders report success by writing it and by nothing else, so a
        // redaction that removes the line with the PII has removed the only evidence a dispatch was asked for —
        // which is the claim OutboundProviderPolicy refuses in Production on the sender's behalf.
        foreach (var channel in new[] { "[EMAIL DISPATCHED]", "[SMS DISPATCHED]", "[PUSH DISPATCHED]" })
            _factory.Logs.Containing(channel).Should().NotBeEmpty($"{channel} is what says a dispatch happened");

        foreach (var value in new[] { address, phone, subject, body, sms, pushTitle, pushMessage })
            _factory.Logs.Lines.Should().NotContain(l => l.Message.Contains(value, StringComparison.Ordinal),
                "a dispatch line that carried the customer's own values is the copy that leaves the row and travels to every sink");

        // The boundary, drawn positively so a reader can see where it sits: a recipient *id* is this server's own
        // key and stays, in the same shape the auth path logs a user id. An address is the customer's, and goes.
        _factory.Logs.Containing("[PUSH DISPATCHED]").Last().Message.Should().Contain(recipientId,
            "an id this server minted is not the personal data this finding is about");
    }
}

/// <summary>
/// SEC-09, second half: a sign-in refusal answers one sentence for four causes, and until this change the log
/// answered it the same way — which is to say it did not answer at all. Three of the four causes wrote no row and
/// no line, so "why was this person turned away" could not be reconstructed from anything the server keeps, while
/// a wrong password left a streak nobody could tie to a cause.
/// </summary>
/// <remarks>
/// The cause goes to the log and stays out of the response, which is the whole shape of the finding: putting it
/// in the response is the oracle <c>SEC-04(c)</c> spent its budget closing. Facts here take one permit each from
/// the auth tier (ten a minute, partitioned per fixture host); the guessing run that needs six is its own class.
/// </remarks>
public class RefusedSignInLogTests : IClassFixture<BooklyHubWebApplicationFactory>
{
    private const string AuthCategory = "BooklyHub.Api.Controllers.AuthController";

    private readonly BooklyHubWebApplicationFactory _factory;

    public RefusedSignInLogTests(BooklyHubWebApplicationFactory factory) => _factory = factory;

    private async Task<string> TryAsync(string email, string password)
    {
        var response = await _factory.CreateClient()
            .PostAsJsonAsync("/api/v1/auth/login", new { Email = email, Password = password });

        var text = await response.Content.ReadAsStringAsync();

        // 429 would mean the address tier answered and no refusal line was ever written, so every fact that reads
        // the log would be reading a line some other request made.
        response.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests, text);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, text);

        return text;
    }

    private IReadOnlyList<CollectingLogger.LogLine> Refusals() =>
        _factory.Logs.Lines.Where(l => l.Category == AuthCategory && l.Message.Contains("Refused at auth:login")).ToList();

    [Fact]
    public async Task AWrongPassword_MustLeaveItsCauseAndItsAccountInTheLog()
    {
        var email = $"sec09.badpw-{Guid.NewGuid():N}@example.test";
        var userId = await LoginAttempt.SeedAsync(_factory, email);

        var text = await TryAsync(email, "wrong-once");

        text.Should().NotContain("bad-password",
            "the cause is the oracle SEC-04(c) was spent closing; it belongs in the log and not in the body");

        var lines = Refusals();
        lines.Should().Contain(l => l.Message.Contains(userId.ToString()),
            "a refusal that names no account is a count of refusals and nothing more");

        var line = lines.Single(l => l.Message.Contains(userId.ToString()));
        line.Message.Should().Contain("bad-password").And.Contain("auth:login");
        line.Level.Should().Be(LogLevel.Warning);
    }

    [Fact]
    public async Task AnUnknownAddress_MustBeRefusedByNameOfNothingItDoesNotHave()
    {
        var email = $"sec09.nobody-{Guid.NewGuid():N}@example.test";

        await TryAsync(email, LoginAttempt.Password);

        var lines = Refusals();
        lines.Should().Contain(l => l.Message.Contains("unknown-account"));

        var line = lines.Single(l => l.Message.Contains("unknown-account"));
        line.Message.Should().Contain("(no row)",
            "this is the one cause with no account to name, and the line says so rather than inventing one");

        // The address is what a reader might expect in this line, because it is what the caller sent. It is the
        // customer's claim about an account and the log is shared, so it is written nowhere: the burst these lines
        // count is the rate limiter's to answer, and the addresses in it are not this server's to keep.
        _factory.Logs.Lines.Should().NotContain(l => l.Message.Contains(email, StringComparison.Ordinal),
            "an unknown-address refusal that wrote the address would be a list of every address anyone has ever typed");
    }

    [Fact]
    public async Task AnInactiveAccount_MustNotReadLikeAWrongPasswordToTheLog()
    {
        var email = $"sec09.inactive-{Guid.NewGuid():N}@example.test";
        var userId = await LoginAttempt.SeedAsync(_factory, email);

        using (var scope = _factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
                .Users
                .Where(u => u.Id == userId)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false));
        }

        // The right password, so the only thing this request can be refused for is the flag. A wrong one would
        // have read as bad-password and left the deactivation unnamed.
        await TryAsync(email, LoginAttempt.Password);

        Refusals().Should().Contain(l =>
            l.Message.Contains(userId.ToString()) && l.Message.Contains("inactive-account"),
            "a deactivated account is an administrator's decision, and the line that says bad-password hides it");
    }
}

/// <summary>
/// The fourth cause, in its own fixture: this fact spends six of the auth tier's ten permits per minute, and the
/// tier's partition is the fixture's own host.
/// </summary>
public class LockedOutSignInLogTests : IClassFixture<BooklyHubWebApplicationFactory>
{
    private readonly BooklyHubWebApplicationFactory _factory;

    public LockedOutSignInLogTests(BooklyHubWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task ALockedOutAccount_MustSayLockedOutWhenThePasswordItWasGivenIsRight()
    {
        var email = $"sec09.lockout-{Guid.NewGuid():N}@example.test";
        var userId = await LoginAttempt.SeedAsync(_factory, email);

        for (var i = 1; i <= LoginLockoutPolicy.MaxFailedAttempts; i++)
            await LoginAttempt.TryAsync(_factory, email, "wrong-" + i);

        var response = await _factory.CreateClient()
            .PostAsJsonAsync("/api/v1/auth/login", new { Email = email, Password = LoginAttempt.Password });
        var text = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, text);
        text.Should().NotContain("locked-out", "a body that says so is an announcement that somebody is being kept out");

        var lines = _factory.Logs.Lines
            .Where(l => l.Category == "BooklyHub.Api.Controllers.AuthController" && l.Message.Contains(userId.ToString()))
            .ToList();

        // The five mistypes and the correct password that came after them are two different things, and the log is
        // the only place that still knows which is which: to the caller all six answered the same sentence.
        lines.Should().HaveCount(LoginLockoutPolicy.MaxFailedAttempts + 1,
            "each of the six refusals is its own event, and the last one is the one that was refused for a reason "
            + "the other five were not");

        lines.Last().Message.Should().Contain("locked-out");
        lines.Take(LoginLockoutPolicy.MaxFailedAttempts).Should().OnlyContain(l => l.Message.Contains("bad-password"));

        _factory.Logs.Lines.Should().NotContain(l => l.Message.Contains(email, StringComparison.Ordinal));
    }
}

/// <summary>
/// The second door that takes a password (<c>PW-01</c>) is logged by the same classifier as the first, so it is
/// pinned the same way: the cause for the log, the door in the line, and no address in either. Its client is
/// minted offline, which costs the auth tier only this one POST.
/// </summary>
public class RefusedPasswordChangeLogTests : IClassFixture<BooklyHubWebApplicationFactory>
{
    private readonly BooklyHubWebApplicationFactory _factory;

    public RefusedPasswordChangeLogTests(BooklyHubWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task ARefusedPasswordChange_MustNameTheDoorThatRefusedIt()
    {
        var email = $"sec09.rotate-{Guid.NewGuid():N}@example.test";
        var userId = await PasswordChange.SeedAsync(_factory, email);

        var response = await PasswordChange.SendAsync(
            PasswordChange.ClientFor(_factory, userId), "not the current one", PasswordChange.Replacement);
        var text = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, text);
        text.Should().NotContain("bad-password");

        var line = _factory.Logs.Lines
            .SingleOrDefault(l => l.Message.Contains("Refused at auth:change-password") && l.Message.Contains(userId.ToString()));

        line.Should().NotBeNull(
            "two doors take a password; a line that does not say which one was knocked on cannot answer for the streak that grew");
        line!.Message.Should().Contain("bad-password");

        _factory.Logs.Lines.Should().NotContain(l => l.Message.Contains(email, StringComparison.Ordinal));
    }
}
