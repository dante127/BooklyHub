using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace BooklyHub.Api.Health;

/// <summary>
/// The three probe routes, and the only shape any of them answer in.
///
/// A readiness gate is read by a load balancer, so what it says has to be what the instance can promise:
/// <c>/health/ready</c> runs the checks tagged <c>ready</c> and nothing else. <c>/health</c> is the operator's
/// route and runs every registered check, which is how a dependency that is degrading without stopping
/// service still gets seen. <c>/health/live</c> runs none — a process that is up answers it, and nothing a
/// dependency can do should make a orchestrator restart a working instance.
///
/// The body names each check and how long it took, and deliberately omits each check's description and
/// exception text: these routes are anonymous, and the failure message of the database check is written by
/// the SQL client and carries the server and database the deployment is talking to.
/// </summary>
public static class HealthEndpoints
{
    public const string ReadinessTag = "ready";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static HealthCheckOptions Liveness { get; } = ForPredicate(_ => false);

    public static HealthCheckOptions Readiness { get; } = ForPredicate(check => check.Tags.Contains(ReadinessTag));

    public static HealthCheckOptions All { get; } = ForPredicate(_ => true);

    private static HealthCheckOptions ForPredicate(Func<HealthCheckRegistration, bool> predicate) => new()
    {
        Predicate = predicate,
        ResponseWriter = WriteAsync
    };

    private static async Task WriteAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json; charset=utf-8";

        var payload = new HealthResponse(
            report.Status.ToString(),
            Math.Round(report.TotalDuration.TotalMilliseconds, 1),
            report.Entries
                .Select(entry => new HealthCheckResponse(
                    entry.Key,
                    entry.Value.Status.ToString(),
                    Math.Round(entry.Value.Duration.TotalMilliseconds, 1)))
                .OrderBy(check => check.Name, StringComparer.Ordinal));

        await context.Response.WriteAsync(JsonSerializer.Serialize(payload, JsonOptions));
    }

    private sealed record HealthResponse(string Status, double TotalDurationMs, IEnumerable<HealthCheckResponse> Checks);

    private sealed record HealthCheckResponse(string Name, string Status, double DurationMs);
}
