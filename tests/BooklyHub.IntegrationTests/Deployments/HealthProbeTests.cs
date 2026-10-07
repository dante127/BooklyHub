using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BooklyHub.Api.Controllers;
using BooklyHub.Application.Common.Interfaces;
using BooklyHub.Application.Payments;
using BooklyHub.Application.Security;
using BooklyHub.Domain.Entities.Appointments;
using BooklyHub.Domain.Entities.Customers;
using BooklyHub.Domain.Entities.Organizations;
using BooklyHub.Domain.Entities.Services;
using BooklyHub.Domain.Entities.StaffMembers;
using BooklyHub.Domain.Entities.Tenancy;
using BooklyHub.Domain.Enums;
using BooklyHub.Infrastructure.Data;
using BooklyHub.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Xunit;

namespace BooklyHub.IntegrationTests.Deployments;

/// <summary>
/// What an orchestrator and an operator read before deciding whether this instance may serve traffic. The
/// route that answers "ready" has to run only the checks a request actually depends on, and the route that
/// answers "alive" has to run none: a dependency that goes away should stop an instance receiving work, not
/// get a working process restarted. Both are pinned here against a host carrying checks of each kind, plus a
/// cache that fails every call — the measured shape of a dead Redis.
/// </summary>
public sealed class HealthProbeTests : IAsyncLifetime
{
    /// <summary>
    /// Stands in for what a SQL client puts in a check's description when the database is unreachable: the
    /// server and catalog this deployment talks to. The probe routes are anonymous, so this string is the
    /// thing the response writer must not repeat.
    /// </summary>
    private const string DependencyCoordinates = "Server=tcp:prod-cluster.database.windows.net,1433;Database=BooklyHubDb";

    /// <summary>
    /// The two shapes a probe has to tell apart: a dependency that is degraded but not gating, and one that
    /// is tagged for the gate. Each carries a description so the writer has something it must not repeat.
    /// </summary>
    private sealed class DegradedDependencyCheck : IHealthCheck
    {
        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(new HealthCheckResult(HealthStatus.Degraded, DependencyCoordinates));
    }

    private sealed class ReadyDependencyCheck : IHealthCheck
    {
        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(new HealthCheckResult(HealthStatus.Healthy, "ready-dependency is healthy"));
    }

    private sealed class AlwaysFailingCache : IDistributedCache
    {
        private static Task<T> Fail<T>() => Task.FromException<T>(new InvalidOperationException("redis is down"));

        public byte[]? Get(string key) => throw new InvalidOperationException("redis is down");

        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => Fail<byte[]?>();

        public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
            => throw new InvalidOperationException("redis is down");

        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default) => Fail<object>();

        public void Refresh(string key) => throw new InvalidOperationException("redis is down");

        public Task RefreshAsync(string key, CancellationToken token = default) => Fail<object>();

        public void Remove(string key) => throw new InvalidOperationException("redis is down");

        public Task RemoveAsync(string key, CancellationToken token = default) => Fail<object>();
    }

    private sealed class ProbedFactory : BooklyHubWebApplicationFactory
    {
        protected override void ConfigureTestServices(IServiceCollection services)
        {
            // One check with no tag and one with the readiness tag, so the filter has two kinds of entry to
            // choose between. Before the fix the readiness predicate was `Tags.Contains("ready") || true`,
            // which answered with whatever the full health route answered and made the tag decoration.
            services.AddHealthChecks()
                .AddCheck<DegradedDependencyCheck>("degraded-dependency")
                .AddCheck<ReadyDependencyCheck>("ready-dependency", tags: new[] { "ready" });

            services.RemoveAll<IDistributedCache>();
            services.AddSingleton<IDistributedCache>(new AlwaysFailingCache());
        }
    }

    private readonly ProbedFactory _factory = new();

    public Task InitializeAsync() => _factory.InitializeAsync();

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private sealed record Graph(Guid TenantId, Guid LocationId, Guid ServiceId, Guid StaffId, Guid CustomerId, Guid AppointmentId);

    private async Task<Graph> SeedAppointmentAsync(decimal price = 120.00m)
    {
        var graph = new Graph(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.Empty);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        db.Tenants.Add(new Tenant(graph.TenantId, "Probe Clinic", $"probe-{graph.TenantId:N}", "UTC"));

        db.Locations.Add(new Location
        {
            Id = graph.LocationId,
            TenantId = graph.TenantId,
            Name = "Front",
            Address = "1 Front St",
            City = "Springfield",
            Country = "US",
            TimeZoneId = "UTC"
        });

        db.Services.Add(new Service
        {
            Id = graph.ServiceId,
            TenantId = graph.TenantId,
            Name = "Consult",
            DurationMinutes = 30,
            Price = price
        });

        db.StaffMembers.Add(new Staff
        {
            Id = graph.StaffId,
            TenantId = graph.TenantId,
            LocationId = graph.LocationId,
            FirstName = "Doc",
            LastName = "Probe",
            Email = $"doc-{graph.StaffId:N}@probe.test"
        });

        db.Customers.Add(new Customer
        {
            Id = graph.CustomerId,
            TenantId = graph.TenantId,
            FirstName = "Ann",
            LastName = "Patient",
            Email = $"ann-{graph.CustomerId:N}@probe.test"
        });

        var startAtUtc = DateTime.UtcNow.Date.AddDays(2).AddHours(9);
        var appointment = Appointment.Create(
            graph.TenantId, graph.LocationId, graph.ServiceId, graph.StaffId, graph.CustomerId,
            startAtUtc, startAtUtc.AddMinutes(30), 30, price);
        appointment.TransitionTo(AppointmentStatus.Confirmed, DateTime.UtcNow, "seeded for probe tests");
        db.Appointments.Add(appointment);

        await db.SaveChangesAsync();

        return graph with { AppointmentId = appointment.Id };
    }

    private async Task<JsonElement> ProbeAsync(string route)
    {
        var response = await _factory.CreateClient().GetAsync(route);
        var body = await response.Content.ReadAsStringAsync();

        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    private static string[] CheckNames(JsonElement report) =>
        report.GetProperty("checks").EnumerateArray().Select(c => c.GetProperty("name").GetString()!).ToArray();

    [Fact]
    public async Task TheReadinessRoute_MustRunOnlyTheChecksTaggedReady()
    {
        var ready = await ProbeAsync("/health/ready");

        ready.GetProperty("status").GetString().Should().Be("Healthy");
        // The gate runs what a request depends on and nothing that was not tagged for it.
        CheckNames(ready).Should().BeEquivalentTo(new[] { "Database", "ready-dependency" });
    }

    [Fact]
    public async Task TheOperatorRoute_MustRunEveryCheckIncludingOnesThatDoNotGateReadiness()
    {
        var response = await _factory.CreateClient().GetAsync("/health");
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);

        // Degraded stays a 200: this is the route an operator reads, and a dependency that is losing its
        // cache must not be reported as a reason to pull a serving instance out of rotation.
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        document.RootElement.GetProperty("status").GetString().Should().Be("Degraded");
        CheckNames(document.RootElement).Should().BeEquivalentTo(
            new[] { "Database", "ready-dependency", "degraded-dependency" });
    }

    [Fact]
    public async Task TheLivenessRoute_MustNotReadASingleDependency()
    {
        var live = await ProbeAsync("/health/live");

        live.GetProperty("status").GetString().Should().Be("Healthy");
        live.GetProperty("checks").GetArrayLength().Should().Be(0,
            "a dependency going away must never become a reason to restart a process that is serving");
    }

    [Fact]
    public async Task AProbeBody_MustNameTheDependencyAndStopThere()
    {
        // Both halves are the point. A host that answers these routes with no body at all passes the second
        // half trivially — measured, `ResponseWriter = null` makes every route return an empty 200 — so the
        // first half requires the prose an operator actually needs, and the second requires it to stop there.
        var health = await _factory.CreateClient().GetStringAsync("/health");
        health.Should().Contain("degraded-dependency");
        health.Should().Contain("Database");
        health.Should().NotContain(DependencyCoordinates, "the probe routes are anonymous and /health is not a configuration endpoint");
        health.Should().NotContain("database.windows.net");

        foreach (var route in new[] { "/health/live", "/health/ready" })
        {
            var body = await _factory.CreateClient().GetStringAsync(route);
            body.Should().NotBeEmpty($"{route} has to say which checks it ran");
            body.Should().NotContain(DependencyCoordinates);
            body.Should().NotContain("database.windows.net");
        }

        (await _factory.CreateClient().GetStringAsync("/health/ready")).Should().Contain("Database");
    }

    [Fact]
    public async Task ACacheThatFailsEveryCall_MustStillServeRequestsAndReplayTheChargeFromTheDatabase()
    {
        var graph = await SeedAppointmentAsync();
        var client = _factory.CreateClientForTenant(graph.TenantId, Roles.Accountant);
        const string idempotencyKey = "probe-charge-key";

        var first = await client.SendAsync(ChargeRequest(graph, idempotencyKey));
        var firstBody = await AssertOkAsync(first);
        var second = await client.SendAsync(ChargeRequest(graph, idempotencyKey));
        var secondBody = await AssertOkAsync(second);

        firstBody.GetProperty("id").GetGuid().Should().Be(secondBody.GetProperty("id").GetGuid(),
            "the idempotency record in the database is the authority and the cache holds only a copy of it");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.Payments.IgnoreQueryFilters().CountAsync(p => p.AppointmentId == graph.AppointmentId))
            .Should().Be(1, "a cache outage must not turn one charge into two");

        (await client.GetAsync("/api/v1/services")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static HttpRequestMessage ChargeRequest(Graph graph, string idempotencyKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/payments/charge")
        {
            Content = JsonContent.Create(new PaymentsController.ProcessPaymentApiRequest(graph.AppointmentId, 120.00m, "USD"))
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        return request;
    }

    private static async Task<JsonElement> AssertOkAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }
}
