using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using BooklyHub.IntegrationTests.Infrastructure;
using FluentAssertions;
using Xunit;

namespace BooklyHub.IntegrationTests.Security;

/// <summary>
/// SEC-13: the middleware handed whatever the client put in <c>X-Correlation-Id</c> straight to the response header,
/// to the failure body, and to the log line — where the Serilog output template interpolates it inside brackets, raw.
/// A caller could therefore put a few kilobytes of its own choosing into every log record of its request, including
/// text shaped like the template's own structure, and see it reflected back at it. The rule pinned here is small: an
/// id the server uses is either one the client sent that the server can vouch for, or one the server made.
/// </summary>
/// <remarks>
/// <para>
/// What "vouch for" means, and why it is replace-not-strip: a value is kept when it is 1..64 characters of
/// <c>[A-Za-z0-9._-]</c>, and is replaced with a fresh server-generated id otherwise. Stripping the unsafe characters
/// was the other candidate and loses on two counts — it can collapse two different caller ids into one (which is the
/// opposite of what a correlation id is for), and a caller that sends nothing but unsafe characters gets an empty id
/// into the log bracket. Replacing keeps the id meaningful to the operator and gives the client back exactly what was
/// actually used, which the header already does.
/// </para>
/// <para>
/// The 64 is a stated bound, not a transport one: the server's own header limit is far larger, so the difference this
/// rule makes is that the attacker-controlled span in a log line is now bounded by the application's choice.
/// </para>
/// <para>
/// The log side cannot be asserted from here, because <c>LogContext.PushProperty</c> is Serilog's own enrichment and
/// the test host's collector sits on the <c>ILogger&lt;T&gt;</c> seam: a fact that read the log scope would pass with
/// the middleware unchanged. The bound is therefore asserted at both places a caller can read — the response header and
/// the problem body — and the log consequence is documented in <c>docs/SECURITY.md</c> §3.4 rather than claimed here.
/// </para>
/// </remarks>
public class CorrelationIdBoundaryTests : IClassFixture<BooklyHubWebApplicationFactory>
{
    /// <summary>The shape a correlation id the server is willing to use has.</summary>
    private const string SafeIdPattern = "^[A-Za-z0-9._-]{1,64}$";

    private const int LengthCap = 64;

    private readonly BooklyHubWebApplicationFactory _factory;

    public CorrelationIdBoundaryTests(BooklyHubWebApplicationFactory factory) => _factory = factory;

    /// <summary>
    /// Sends the header on a request that needs no database and no credentials: an unauthenticated call to a
    /// protected route answers 401 with the problem envelope, which carries the correlation id in its body as well
    /// as in the header. One request, both reflection points.
    /// </summary>
    private async Task<(string Header, JsonElement Problem)> SendAsync(string? supplied)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/appointments");
        if (supplied is not null)
        {
            // WithoutValidation, because the point is to reach the middleware with the exact bytes a hostile
            // caller can put on the wire — the default validator would refuse some of them client-side and the
            // fact would then be measuring HttpClient.
            request.Headers.TryAddWithoutValidation("X-Correlation-Id", supplied);
        }

        using var response = await _factory.CreateClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        response.Headers.TryGetValues("X-Correlation-Id", out var values).Should().BeTrue(
            "the header is the handle support asks a caller for");
        var header = values!.Single();

        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        return (header, document.RootElement.Clone());
    }

    private static void AssertUsableId(string id) =>
        id.Should().MatchRegex(SafeIdPattern,
            "an id the server echoes, reflects in a failure body and splices raw into its own log line has to be " +
            "something the server vouches for: length-bounded and free of anything that can reshape the text around it");

    /// <summary>
    /// The half that separates "replace" from "clean up": an id the caller did not get is one the server made, so it
    /// parses as the server's own format. A censored or truncated version of the caller's value would satisfy the safe
    /// pattern above and still be a third string — one the caller cannot search its own logs for and the operator
    /// cannot tell apart from a real id.
    /// </summary>
    private static void AssertServerMadeId(string id) =>
        Guid.TryParse(id, out _).Should().BeTrue(
            "a value the rule refused has to be replaced by an id the server made, not by an edited version of what " +
            "was sent; the echoed id was the only thing a caller has to go on");

    [Fact]
    public async Task ASafeCorrelationIdFromTheClient_MustComeBackUnchangedInBothPlaces()
    {
        var supplied = "req-7F_2.k9";

        var (header, problem) = await SendAsync(supplied);

        header.Should().Be(supplied,
            "a caller that labels its own request must be able to search the logs for that label; a rule that " +
            "replaced every value would make the header useless and would be a bug wearing a fix");
        AssertUsableId(header);
        problem.GetProperty("correlationId").GetString().Should().Be(header,
            "the body copy is what a user pastes into a support ticket, so it cannot disagree with the header");
    }

    [Fact]
    public async Task ACorrelationIdExactlyAtTheLengthCap_MustBeKept()
    {
        var supplied = new string('a', LengthCap);

        var (header, _) = await SendAsync(supplied);

        header.Should().Be(supplied, "the cap is a bound, not a preference — a value inside it is honored");
    }

    [Fact]
    public async Task ACorrelationIdOneOverTheLengthCap_MustBeReplacedInBothPlaces()
    {
        var supplied = new string('a', LengthCap + 1);

        var (header, problem) = await SendAsync(supplied);

        header.Should().NotBe(supplied,
            "past the bound the value stops being an identifier and starts being caller-owned text the server " +
            "quotes back at the client and into its own log line");
        AssertUsableId(header);
        AssertServerMadeId(header);
        problem.GetProperty("correlationId").GetString().Should().Be(header);
    }

    [Fact]
    public async Task ACorrelationIdCarryingUnsafeCharacters_MustBeReplacedNotPassedThrough()
    {
        // Shaped like the log template's own brackets, because that is the text an operator reads: a value that
        // survives is a line the caller wrote, not a line the server logged.
        var supplied = "] [99:59:59 ERR] auth:login success for admin [";

        var (header, problem) = await SendAsync(supplied);

        header.Should().NotBe(supplied);
        AssertUsableId(header);
        AssertServerMadeId(header);
        header.Should().NotContain("[");
        problem.GetProperty("correlationId").GetString().Should().Be(header);
    }

    [Fact]
    public async Task ACorrelationIdOfNothingButUnsafeCharacters_MustStillYieldAUsableId()
    {
        // The fact that separates "replace what is unsafe" from "strip what is unsafe": stripping this leaves an
        // empty id, which renders as an empty bracket in the log and as a correlation id no caller can quote.
        var (header, problem) = await SendAsync("////");

        header.Should().NotBe("////");
        AssertUsableId(header);
        AssertServerMadeId(header);
        problem.GetProperty("correlationId").GetString().Should().Be(header);
    }

    [Fact]
    public async Task ANewlineBearingCorrelationId_MustNotReachTheEchoedId()
    {
        // A header value cannot carry CR/LF over a real socket, but the test host is in-memory, so this pins what
        // the rule does with the class of character a log record is built around: with a text template, a newline
        // in a spliced value is a second log line the caller wrote.
        var (header, _) = await SendAsync("ok\r\n[99:59:59 ERR] forged");

        AssertUsableId(header);
        AssertServerMadeId(header);
        header.Should().NotContain("\n");
        header.Should().NotContain("\r");
    }

    [Fact]
    public async Task ACorrelationIdWithATrailingNewline_MustBeReplaced()
    {
        // The input that makes the pattern's anchor load-bearing: an end-of-string match that treats a trailing
        // newline as the end would accept this, and the accepted value is what gets echoed back and logged.
        var (header, _) = await SendAsync("abc\n");

        header.Should().NotBe("abc\n");
        AssertUsableId(header);
        AssertServerMadeId(header);
        header.Should().NotContain("\n",
            "a spliced newline is a second log record with the server's own timestamp on it");
    }

    [Fact]
    public async Task ANoCorrelationIdSent_MustStillGetOneTheServerMade()
    {
        var (header, problem) = await SendAsync(null);

        AssertUsableId(header);
        Guid.TryParse(header, out _).Should().BeTrue(
            "a request the client did not label is labeled by the server, and the fallback shares the shape the " +
            "rule promises rather than being a second format nobody checks");
        problem.GetProperty("correlationId").GetString().Should().Be(header);
    }

    [Fact]
    public async Task TwoReplacementsInARow_MustNotCollide()
    {
        var (first, _) = await SendAsync("////");
        var (second, _) = await SendAsync(new string('b', 4096));

        first.Should().NotBe(second,
            "replaced ids are the only handle an operator has for those two requests, so the fallback cannot be " +
            "a constant stand-in for the caller's value");
    }
}
