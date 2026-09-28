using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BooklyHub.Api.Controllers;
using BooklyHub.Application.Appointments.Commands;
using BooklyHub.Application.Security;
using BooklyHub.Domain.Entities.Customers;
using BooklyHub.Domain.Entities.Organizations;
using BooklyHub.Domain.Entities.Scheduling;
using BooklyHub.Domain.Entities.Services;
using BooklyHub.Domain.Entities.StaffMembers;
using BooklyHub.Domain.Entities.Tenancy;
using BooklyHub.Domain.Enums;
using BooklyHub.Infrastructure.Data;
using BooklyHub.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BooklyHub.IntegrationTests.Appointments;

/// <summary>
/// The recurring endpoint used to resolve the requested range into a count of its own choosing: a series
/// bounded only by EndDate stopped at 12 occurrences, an explicit MaxOccurrences above 52 stopped at 52,
/// and a pattern it did not recognise repeated weekly. The caller could not tell any of that from a series
/// that had been booked as asked, because the response reported the shorter list as the request. Every
/// bound is now honoured or refused with the rule that refused it (V14).
/// </summary>
public class RecurringSeriesTests : IClassFixture<BooklyHubWebApplicationFactory>
{
    private readonly BooklyHubWebApplicationFactory _factory;

    public RecurringSeriesTests(BooklyHubWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private sealed record Graph(Guid TenantId, Guid LocationId, Guid ServiceId, Guid StaffId, Guid CustomerId);

    private async Task<Graph> SeedGraphAsync(DateOnly seriesStart, int maxAdvanceBookingDays)
    {
        var graph = new Graph(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var tenant = new Tenant(graph.TenantId, "Series Clinic", $"series-{graph.TenantId:N}", "UTC");
        tenant.Settings = new TenantSetting(graph.TenantId)
        {
            MinBookingNoticeMinutes = 10,
            MaxAdvanceBookingDays = maxAdvanceBookingDays,
            SlotIntervalMinutes = 30
        };
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
            FirstName = "Ava",
            LastName = "Doc",
            Email = $"ava-{graph.StaffId:N}@series.test"
        };
        staff.StaffServices.Add(new StaffService { TenantId = graph.TenantId, StaffId = graph.StaffId, ServiceId = graph.ServiceId });

        // A weekly series lands on the same weekday every time, so one working-hour row covers it.
        var workingHour = new WorkingHour
        {
            Id = Guid.NewGuid(),
            TenantId = graph.TenantId,
            StaffId = graph.StaffId,
            LocationId = graph.LocationId,
            DayOfWeek = seriesStart.DayOfWeek,
            IsWorkingDay = true
        };
        workingHour.Intervals.Add(new WorkingHourInterval(new TimeSpan(8, 0, 0), new TimeSpan(18, 0, 0), false));
        staff.WorkingHours.Add(workingHour);
        db.StaffMembers.Add(staff);

        db.Customers.Add(new Customer
        {
            Id = graph.CustomerId,
            TenantId = graph.TenantId,
            FirstName = "Cam",
            LastName = "Patient",
            Email = $"cam-{graph.CustomerId:N}@series.test"
        });

        await db.SaveChangesAsync();
        return graph;
    }

    private HttpClient StaffClient(Graph graph) => _factory.CreateClientForTenant(graph.TenantId, Roles.Staff);

    private Task<HttpResponseMessage> PostSeriesAsync(
        Graph graph,
        DateOnly startDate,
        DateOnly? endDate,
        int? maxOccurrences,
        RecurrencePattern pattern = RecurrencePattern.Weekly,
        RecurrenceConflictPolicy conflictPolicy = RecurrenceConflictPolicy.SkipConflicts) =>
        StaffClient(graph).PostAsJsonAsync("/api/v1/appointments/recurring", new
        {
            graph.TenantId,
            graph.LocationId,
            graph.ServiceId,
            graph.StaffId,
            graph.CustomerId,
            StartTimeOfDay = new TimeOnly(9, 0),
            Pattern = (int)pattern,
            Interval = 1,
            StartDate = startDate,
            EndDate = endDate,
            MaxOccurrences = maxOccurrences,
            ConflictPolicy = (int)conflictPolicy,
            Notes = "series test"
        });

    private async Task<(int Appointments, int Series)> ReadCountsAsync(Graph graph)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var appointments = await db.Appointments.IgnoreQueryFilters().CountAsync(a => a.TenantId == graph.TenantId);
        var series = await db.RecurringAppointments.IgnoreQueryFilters().CountAsync(r => r.TenantId == graph.TenantId);
        return (appointments, series);
    }

    private static async Task<string> ReadBodyAsync(HttpResponseMessage response) =>
        await response.Content.ReadAsStringAsync();

    private static string ReadRule(string body) =>
        JsonDocument.Parse(body).RootElement.GetProperty("rule").GetString()!;

    [Fact]
    public async Task TwoYearWeeklySeries_MustBeRefusedWithTheBookingHorizon()
    {
        var start = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(2));
        var graph = await SeedGraphAsync(start, maxAdvanceBookingDays: 60);

        var response = await PostSeriesAsync(graph, start, endDate: start.AddDays(730), maxOccurrences: null);
        var body = await ReadBodyAsync(response);

        Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity,
            $"expected 422, got {(int)response.StatusCode}: {body}");
        ReadRule(body).Should().Be("RecurrenceBeyondBookingWindow");

        // Refusing is only honest if nothing was written: the request that used to come back as twelve
        // appointments had those twelve appointments in the database.
        var (appointments, series) = await ReadCountsAsync(graph);
        (appointments, series).Should().Be((0, 0), body);
    }

    [Fact]
    public async Task SixtyOccurrenceSeries_MustCreateEveryRequestedOccurrence()
    {
        var start = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(2));
        var graph = await SeedGraphAsync(start, maxAdvanceBookingDays: 500);

        var response = await PostSeriesAsync(graph, start, endDate: null, maxOccurrences: 60);
        var body = await ReadBodyAsync(response);

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"expected 200, got {(int)response.StatusCode}: {body}");

        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("totalOccurrencesRequested").GetInt32().Should().Be(60,
            "60 was what was asked for; the cap used to answer 52 and call that the request");
        doc.RootElement.GetProperty("bookedCount").GetInt32().Should().Be(60);
        doc.RootElement.GetProperty("skippedCount").GetInt32().Should().Be(0);

        (await ReadCountsAsync(graph)).Appointments.Should().Be(60);
    }

    [Fact]
    public async Task SeriesWithoutAnyRangeBound_MustBeRefused()
    {
        var start = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(2));
        var graph = await SeedGraphAsync(start, maxAdvanceBookingDays: 500);

        var response = await PostSeriesAsync(graph, start, endDate: null, maxOccurrences: null);
        var body = await ReadBodyAsync(response);

        Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity,
            $"expected 422, got {(int)response.StatusCode}: {body}");
        ReadRule(body).Should().Be("RecurrenceRangeRequired");
        (await ReadCountsAsync(graph)).Should().Be((0, 0), body);
    }

    [Fact]
    public async Task SeriesRunningPastTheHorizon_MustBeRefusedRatherThanPartiallyBooked()
    {
        var start = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(2));
        var graph = await SeedGraphAsync(start, maxAdvanceBookingDays: 60);

        // 200 weekly occurrences run four years out; the tenant only books 60 days ahead, so the horizon
        // is what refuses this request rather than a silent count built into the endpoint.
        var response = await PostSeriesAsync(graph, start, endDate: null, maxOccurrences: 200);
        var body = await ReadBodyAsync(response);

        Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity,
            $"expected 422, got {(int)response.StatusCode}: {body}");
        ReadRule(body).Should().Be("RecurrenceBeyondBookingWindow");
        (await ReadCountsAsync(graph)).Should().Be((0, 0), body);
    }

    [Fact]
    public async Task SeriesWithOneOccupiedOccurrence_MustReportRequestedAsBookedPlusSkipped()
    {
        var start = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(2));
        var graph = await SeedGraphAsync(start, maxAdvanceBookingDays: 500);

        // The third occurrence is taken by another patient, so the series must say so in its own numbers.
        var otherCustomer = await SeedSecondCustomerAsync(graph);
        var thirdSlot = start.ToDateTime(new TimeOnly(9, 0)).AddDays(14);
        var occupied = await StaffClient(graph).PostAsJsonAsync("/api/v1/appointments",
            new AppointmentsController.BookAppointmentRequest(
                graph.LocationId, graph.ServiceId, graph.StaffId, otherCustomer,
                thirdSlot,
                "takes the third occurrence"));
        var occupiedBody = await ReadBodyAsync(occupied);
        Assert.True(occupied.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK,
            $"expected 201, got {(int)occupied.StatusCode}: {occupiedBody}");

        var response = await PostSeriesAsync(graph, start, endDate: null, maxOccurrences: 4);
        var body = await ReadBodyAsync(response);

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"expected 200, got {(int)response.StatusCode}: {body}");

        using var doc = JsonDocument.Parse(body);
        var requested = doc.RootElement.GetProperty("totalOccurrencesRequested").GetInt32();
        var booked = doc.RootElement.GetProperty("bookedCount").GetInt32();
        var skipped = doc.RootElement.GetProperty("skippedCount").GetInt32();

        requested.Should().Be(4);
        (booked + skipped).Should().Be(requested, "occurrences that were neither booked nor refused must not vanish from the counts");
        booked.Should().Be(3);
        skipped.Should().Be(1);
    }

    [Fact]
    public async Task UnrecognisedPattern_MustNotBeBookedAsWeekly()
    {
        var start = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(2));
        var graph = await SeedGraphAsync(start, maxAdvanceBookingDays: 500);

        // The switch used to fall through to a weekly cadence, so a pattern the server does not know
        // became a week-after-week series instead of a refusal.
        var response = await StaffClient(graph).PostAsync("/api/v1/appointments/recurring",
            new StringContent(
                JsonSerializer.Serialize(new
                {
                    graph.TenantId,
                    graph.LocationId,
                    graph.ServiceId,
                    graph.StaffId,
                    graph.CustomerId,
                    StartTimeOfDay = new TimeOnly(9, 0),
                    Pattern = 99,
                    Interval = 1,
                    StartDate = start,
                    EndDate = (DateOnly?)null,
                    MaxOccurrences = 4,
                    ConflictPolicy = 1,
                    Notes = "unknown pattern"
                }),
                Encoding.UTF8,
                "application/json"));
        var body = await ReadBodyAsync(response);

        Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity,
            $"expected 422, got {(int)response.StatusCode}: {body}");
        ReadRule(body).Should().Be("RecurrencePatternUnsupported");
        (await ReadCountsAsync(graph)).Should().Be((0, 0), body);
    }

    private async Task<Guid> SeedSecondCustomerAsync(Graph graph)
    {
        var customerId = Guid.NewGuid();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.Customers.Add(new Customer
        {
            Id = customerId,
            TenantId = graph.TenantId,
            FirstName = "Di",
            LastName = "Patient",
            Email = $"di-{customerId:N}@series.test"
        });
        await db.SaveChangesAsync();
        return customerId;
    }
}
