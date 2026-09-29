using System.Globalization;
using System.Net;
using System.Text.Json;
using BooklyHub.Application.Security;
using BooklyHub.Domain.Entities.Appointments;
using BooklyHub.Domain.Entities.Customers;
using BooklyHub.Domain.Entities.Organizations;
using BooklyHub.Domain.Entities.Scheduling;
using BooklyHub.Domain.Entities.Services;
using BooklyHub.Domain.Entities.StaffMembers;
using BooklyHub.Domain.Entities.Tenancy;
using BooklyHub.Infrastructure.Data;
using BooklyHub.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BooklyHub.IntegrationTests.Reports;

/// <summary>
/// "The last 30 days" on the dashboard was computed from the machine's clock while every other time rule in
/// the request - the booking floors, the transition gate, the cutoff policy - is judged on IClock. The
/// report therefore described a different period from the one the rest of the application lived in, and the
/// period could not be exercised from a test at all. The echoed window and the rows inside it are both
/// asserted here, because only together do they show the controller's clock reaches SQL.
/// </summary>
public class DashboardWindowClockTests : IClassFixture<BooklyHubWebApplicationFactory>
{
    private static readonly DateTime PinnedNow = new(2026, 3, 5, 14, 0, 0, DateTimeKind.Utc);

    private readonly BooklyHubWebApplicationFactory _factory;

    public DashboardWindowClockTests(BooklyHubWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private sealed record Graph(Guid TenantId, Guid LocationId, Guid ServiceId, Guid StaffId, Guid CustomerId);

    private async Task<Graph> SeedGraphAsync()
    {
        var graph = new Graph(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var tenant = new Tenant(graph.TenantId, "Report Clinic", $"report-{graph.TenantId:N}", "UTC");
        db.Tenants.Add(tenant);

        db.Locations.Add(new Location
        {
            Id = graph.LocationId,
            TenantId = graph.TenantId,
            Name = "Main",
            Address = "1 Main St",
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
            Price = 100.00m
        });

        var staff = new Staff
        {
            Id = graph.StaffId,
            TenantId = graph.TenantId,
            LocationId = graph.LocationId,
            FirstName = "Nadia",
            LastName = "Doc",
            Email = $"nadia-{graph.StaffId:N}@report.test"
        };
        staff.StaffServices.Add(new StaffService { TenantId = graph.TenantId, StaffId = graph.StaffId, ServiceId = graph.ServiceId });
        db.StaffMembers.Add(staff);

        db.Customers.Add(new Customer
        {
            Id = graph.CustomerId,
            TenantId = graph.TenantId,
            FirstName = "Eli",
            LastName = "Patient",
            Email = $"eli-{graph.CustomerId:N}@report.test"
        });

        await db.SaveChangesAsync();
        return graph;
    }

    private async Task<Guid> SeedAppointmentAsync(Graph graph, DateTime startAtUtc)
    {
        var appointment = Appointment.Create(
            graph.TenantId, graph.LocationId, graph.ServiceId, graph.StaffId, graph.CustomerId,
            startAtUtc, startAtUtc.AddMinutes(30), 30, 100.00m, "USD");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.Appointments.Add(appointment);
        await db.SaveChangesAsync();

        return appointment.Id;
    }

    private static DateTime ReadUtc(JsonElement root, string property) =>
        DateTime.Parse(root.GetProperty(property).GetString()!, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    private async Task<JsonElement> DashboardAsync(Graph graph)
    {
        var client = _factory.CreateClientForTenant(graph.TenantId, Roles.Manager);
        var response = await client.GetAsync("/api/v1/reports/dashboard");
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.StatusCode == HttpStatusCode.OK,
            $"expected 200, got {(int)response.StatusCode}: {body}");

        return JsonDocument.Parse(body).RootElement;
    }

    [Fact]
    public async Task TheDefaultWindow_MustBeAnchoredToTheApplicationClock()
    {
        var graph = await SeedGraphAsync();

        try
        {
            _factory.Clock.Pin(PinnedNow);
            var root = await DashboardAsync(graph);

            ReadUtc(root, "toUtc").Should().Be(PinnedNow);
            ReadUtc(root, "fromUtc").Should().Be(PinnedNow.AddDays(-30));
        }
        finally
        {
            _factory.Clock.Release();
        }
    }

    [Fact]
    public async Task TheDefaultWindow_MustSpanExactlyThirtyDays()
    {
        var graph = await SeedGraphAsync();

        try
        {
            _factory.Clock.Pin(PinnedNow);
            var root = await DashboardAsync(graph);

            // Both bounds come from one clock read, so the period is exactly 30 days rather than 30 days
            // plus however long the request took between two reads.
            (ReadUtc(root, "toUtc") - ReadUtc(root, "fromUtc")).Should().Be(TimeSpan.FromDays(30));
        }
        finally
        {
            _factory.Clock.Release();
        }
    }

    [Fact]
    public async Task ASuppliedWindow_MustBeUsedVerbatimUnderAPinnedClock()
    {
        var graph = await SeedGraphAsync();
        var from = new DateTime(2026, 1, 4, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 1, 31, 0, 0, 0, DateTimeKind.Utc);

        try
        {
            _factory.Clock.Pin(PinnedNow);

            var client = _factory.CreateClientForTenant(graph.TenantId, Roles.Manager);
            var response = await client.GetAsync($"/api/v1/reports/dashboard?fromUtc={from:O}&toUtc={to:O}");

            Assert.True(response.StatusCode == HttpStatusCode.OK,
                $"expected 200, got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

            var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
            ReadUtc(root, "fromUtc").Should().Be(from);
            ReadUtc(root, "toUtc").Should().Be(to);
        }
        finally
        {
            _factory.Clock.Release();
        }
    }

    [Fact]
    public async Task ARowThatOnlyTheMachineClockWouldStillCallRecent_MustLeaveTheDefaultReport()
    {
        // Five days behind the machine is inside the old default period, and ten days ahead of it once the
        // application clock is forty days on - so this row is counted only by a report on the machine clock.
        var graph = await SeedGraphAsync();
        await SeedAppointmentAsync(graph, DateTime.UtcNow.AddDays(-5));

        try
        {
            _factory.Clock.AdvanceBy(TimeSpan.FromDays(40));
            var root = await DashboardAsync(graph);

            root.GetProperty("totalAppointments").GetInt32().Should().Be(0);
        }
        finally
        {
            _factory.Clock.Release();
        }
    }

    [Fact]
    public async Task ARowTheApplicationClockCallsRecent_BelongsToTheDefaultReportEvenWhenTheMachineDisagrees()
    {
        // Forty-five days behind the machine looks ancient to the machine, but the pinned clock is forty
        // days in its past, so the period the dashboard reports really does cover it.
        var graph = await SeedGraphAsync();
        await SeedAppointmentAsync(graph, DateTime.UtcNow.AddDays(-45));

        try
        {
            _factory.Clock.AdvanceBy(TimeSpan.FromDays(-40));
            var root = await DashboardAsync(graph);

            root.GetProperty("totalAppointments").GetInt32().Should().Be(1);
        }
        finally
        {
            _factory.Clock.Release();
        }
    }
}
