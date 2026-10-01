using System.Diagnostics;
using System.Net;
using BooklyHub.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace BooklyHub.IntegrationTests.Security;

/// <summary>
/// How the limiter behaves once a caller is over budget. Measured before this existed: an anonymous burst of
/// a hundred requests parked four authenticated GETs past five seconds with no status at all, and answered
/// GET /health with a 429 in zero milliseconds — the queue protected nobody and the probe inherited the blame.
/// One walk per host because every fact here spends the same shared partition.
/// </summary>
public class LimiterFailureModeTests : IClassFixture<BooklyHubWebApplicationFactory>
{
    private readonly BooklyHubWebApplicationFactory _factory;

    public LimiterFailureModeTests(BooklyHubWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task AnOverBudgetRequest_IsRefusedImmediatelyAndDoesNotBlameTheProbe()
    {
        var client = _factory.CreateClient();

        var burned = new List<int>();
        for (var i = 0; i < 100; i++)
        {
            burned.Add((int)(await client.GetAsync("/api/v1/nothing-here")).StatusCode);
        }

        burned.Should().OnlyContain(s => s == StatusCodes.Status404NotFound,
            "the walk depends on a window that is exactly spent at the end of it, not over-spent partway");

        // The probe is in a partition of its own: it has to stay answerable while some caller burns user traffic.
        (await client.GetAsync("/health")).StatusCode.Should().Be(HttpStatusCode.OK,
            "a readiness probe that runs a database check is not traffic, and a 429 on it reads as an " +
            "unhealthy instance to whatever is watching");
        (await client.GetAsync("/health/live")).StatusCode.Should().Be(HttpStatusCode.OK,
            "both probes are watched by things that cannot retry with a deadline");

        var watch = Stopwatch.StartNew();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        HttpResponseMessage refused;
        try
        {
            refused = await client.GetAsync("/api/v1/nothing-here").WaitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            throw new Xunit.Sdk.XunitException(
                $"the 101st request was still parked in the queue after 5 s and had answered nothing — " +
                $"that wait, not the refusal, is what held four desk requests open in the measurement");
        }
        watch.Stop();

        refused.StatusCode.Should().Be(HttpStatusCode.TooManyRequests,
            "over budget has to be a refusal; a queue that eventually lets the request through is a delay, " +
            "not a limit");
        watch.ElapsedMilliseconds.Should().BeLessThan(2_000,
            "the answer is known the moment the permit is refused, so any real wait means the request was " +
            "queued instead of answered");

        refused.Headers.TryGetValues("Retry-After", out var values).Should().BeTrue(
            "the caller of an ordinary endpoint needs the deadline as much as a login caller does");
        int.Parse(values!.Single()).Should().BeInRange(1, 60);
    }
}
