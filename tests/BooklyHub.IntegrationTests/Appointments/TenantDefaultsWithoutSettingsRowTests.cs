using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BooklyHub.Api.Controllers;
using BooklyHub.Domain.Entities.Appointments;
using BooklyHub.Domain.Entities.Customers;
using BooklyHub.Domain.Entities.Organizations;
using BooklyHub.Domain.Entities.Scheduling;
using BooklyHub.Domain.Entities.Services;
using BooklyHub.Domain.Entities.StaffMembers;
using BooklyHub.Domain.Entities.Tenancy;
using BooklyHub.Domain.Enums;
using BooklyHub.Application.Security;
using BooklyHub.Infrastructure.Data;
using BooklyHub.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BooklyHub.IntegrationTests.Appointments;

/// <summary>
/// A tenant that never configured anything is not a tenant with no rules: three readers answered that case with a
/// literal <c>?? 60</c>, <c>?? 120</c> or <c>?? 15</c>, <see cref="AppointmentCutoffPolicy"/> kept two more private
/// copies, and each number was also written a second time as the initializer on the property it falls back to. Five
/// places, one policy each, and nothing in the suite could see the two halves disagree — the tests all seeded
/// settings rows, so the fallback ran only in code. These facts put the no-settings-row tenant on the wire and pin
/// the numbers it is judged by, which is what makes the single constant load-bearing instead of decorative.
/// </summary>
/// <remarks>
/// The clock is pinned so that "13 hours ahead" is arithmetic rather than a race against when the suite runs; the
/// boundary facts sit a full hour away from the number they name, so the assertions are about which rule fires and
/// not about a second of skew.
/// </remarks>
public class TenantDefaultsWithoutSettingsRowTests : IClassFixture<BooklyHubWebApplicationFactory>
{
    private static readonly DateTime Now = new(2026, 10, 5, 9, 0, 0, DateTimeKind.Utc);

    private readonly BooklyHubWebApplicationFactory _factory;

    public TenantDefaultsWithoutSettingsRowTests(BooklyHubWebApplicationFactory factory) => _factory = factory;

    private sealed record Graph(Guid TenantId, Guid LocationId, Guid ServiceId, Guid StaffId, Guid CustomerId);

    /// <summary>Seeds a tenant and everything under it except a settings row — the state the fallbacks answer for.</summary>
    private async Task<Graph> SeedGraphWithoutSettingsAsync()
    {
        var graph = new Graph(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var tenant = new Tenant(graph.TenantId, "Defaults Clinic", $"defaults-{graph.TenantId:N}", "UTC");
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
            Email = $"ava-{graph.StaffId:N}@defaults.test"
        };
        staff.StaffServices.Add(new StaffService
        {
            TenantId = graph.TenantId,
            StaffId = graph.StaffId,
            ServiceId = graph.ServiceId
        });

        var workingHour = new WorkingHour
        {
            Id = Guid.NewGuid(),
            TenantId = graph.TenantId,
            StaffId = graph.StaffId,
            LocationId = graph.LocationId,
            DayOfWeek = DayOfWeek.Monday,
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
            Email = $"cam-{graph.CustomerId:N}@defaults.test"
        });

        await db.SaveChangesAsync();

        using var checkScope = _factory.Services.CreateScope();
        var checkDb = checkScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await checkDb.TenantSettings.IgnoreQueryFilters()
            .AnyAsync(s => s.TenantId == graph.TenantId)).Should().BeFalse(
                "every fact here is about the tenant that has no settings row at all");

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

    private Task<HttpResponseMessage> CancelAsync(Graph graph, Guid id) =>
        _factory.CreateClientForTenant(graph.TenantId, Roles.Receptionist)
            .PostAsJsonAsync($"/api/v1/appointments/{id}/cancel",
                new AppointmentsController.CancelAppointmentRequest("patient asked"));

    private Task<HttpResponseMessage> RescheduleAsync(Graph graph, Guid id, DateTime newStart) =>
        _factory.CreateClientForTenant(graph.TenantId, Roles.Receptionist)
            .PostAsJsonAsync($"/api/v1/appointments/{id}/reschedule",
                new AppointmentsController.RescheduleAppointmentRequest(newStart, "moved by staff"));

    private Task<HttpResponseMessage> BookAsync(Graph graph, DateTime startAtUtc) =>
        _factory.CreateClientForTenant(graph.TenantId, Roles.Staff)
            .PostAsJsonAsync("/api/v1/appointments", new AppointmentsController.BookAppointmentRequest(
                LocationId: graph.LocationId,
                ServiceId: graph.ServiceId,
                StaffId: graph.StaffId,
                CustomerId: graph.CustomerId,
                StartAtUtc: startAtUtc,
                Notes: "defaults probe"));

    private static async Task<string> BodyAsync(HttpResponseMessage response) => await response.Content.ReadAsStringAsync();

    private static string ReadRefusal(string body)
    {
        var root = JsonDocument.Parse(body).RootElement;
        return $"{root.GetProperty("rule").GetString()}|{root.GetProperty("detail").GetString()}";
    }

    [Fact]
    public async Task CancelWithinTwentyFourHours_MustBeRefusedByTheNamedDefault()
    {
        var graph = await SeedGraphWithoutSettingsAsync();
        var id = await SeedAppointmentAsync(graph, Now.AddHours(10));

        _factory.Clock.Pin(Now);
        try
        {
            var response = await CancelAsync(graph, id);
            var body = await BodyAsync(response);

            response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity, body);
            ReadRefusal(body).Should().Be(
                $"CancellationCutoffExceeded|Appointments cannot be cancelled within {TenantSetting.DefaultCancellationCutoffHours} hours of the start time.",
                "the refusal must name the constant, because a fallback the tests never reach could otherwise be any number");
        }
        finally
        {
            _factory.Clock.Release();
        }
    }

    [Fact]
    public async Task RescheduleWithinTwelveHours_MustBeRefusedByItsOwnDefaultAndNotByTheCancellationOne()
    {
        var graph = await SeedGraphWithoutSettingsAsync();
        var id = await SeedAppointmentAsync(graph, Now.AddHours(10));

        _factory.Clock.Pin(Now);
        try
        {
            var response = await RescheduleAsync(graph, id, Now.AddHours(14));
            var body = await BodyAsync(response);

            response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity, body);
            ReadRefusal(body).Should().Be(
                $"RescheduleCutoffExceeded|Appointments cannot be rescheduled within {TenantSetting.DefaultReschedulingCutoffHours} hours of the start time.",
                "the two cutoffs are two numbers, and the reschedule one is the smaller of them");
        }
        finally
        {
            _factory.Clock.Release();
        }
    }

    [Fact]
    public async Task AtThirteenHoursTheRescheduleRuleMustBeSilentWhileTheCancellationRuleStillBites()
    {
        var graph = await SeedGraphWithoutSettingsAsync();
        var id = await SeedAppointmentAsync(graph, Now.AddHours(13));

        _factory.Clock.Pin(Now);
        try
        {
            var refusal = await CancelAsync(graph, id);
            var refusalBody = await BodyAsync(refusal);
            refusal.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity, refusalBody);
            ReadRefusal(refusalBody).Should().StartWith("CancellationCutoffExceeded|");

            // This is the fact that keeps the two constants from collapsing into one: at 13 hours the appointment is
            // outside the reschedule window and inside the cancellation window, so a single shared number would
            // refuse both calls with the same rule name.
            var allowed = await RescheduleAsync(graph, id, Now.AddHours(17));
            var allowedBody = await BodyAsync(allowed);
            allowed.StatusCode.Should().NotBe(HttpStatusCode.UnprocessableEntity, allowedBody);
            allowedBody.Should().NotContain("RescheduleCutoffExceeded");
        }
        finally
        {
            _factory.Clock.Release();
        }
    }

    [Fact]
    public async Task TheSlotGrid_MustBeTheIntervalDefaultAndNotAnArbitraryStep()
    {
        var graph = await SeedGraphWithoutSettingsAsync();

        _factory.Clock.Pin(Now);
        try
        {
            var response = await _factory.CreateClient().GetAsync(
                $"/api/v1/availability?tenantId={graph.TenantId}&locationId={graph.LocationId}" +
                $"&serviceId={graph.ServiceId}&staffId={graph.StaffId}&date=2026-10-05");
            var body = await BodyAsync(response);

            response.StatusCode.Should().Be(HttpStatusCode.OK, body);
            var slots = JsonDocument.Parse(body).RootElement.GetProperty("slots");
            slots.EnumerateArray().Should().NotBeEmpty(
                "an empty page would answer the grid question by accident, so this fact needs slots to read");

            // The interval decides where the boundaries fall, so alignment is the observable: a reader that stopped
            // honouring the default would put a start time on a minute the number cannot produce.
            foreach (var slot in slots.EnumerateArray())
            {
                var start = slot.GetProperty("startAtUtc").GetDateTime();
                (start.Minute % TenantSetting.DefaultSlotIntervalMinutes).Should().Be(0,
                    $"the no-settings tenant is stepped by {TenantSetting.DefaultSlotIntervalMinutes} minutes, and {start:O} is not");
            }
        }
        finally
        {
            _factory.Clock.Release();
        }
    }

    [Fact]
    public async Task ASeriesBeyondSixtyDays_MustBeRefusedByTheSameHorizonTheSingleBookingUses()
    {
        var graph = await SeedGraphWithoutSettingsAsync();

        _factory.Clock.Pin(Now);
        try
        {
            // The recurring handler held its own copy of the same literal. Two readers of one policy that resolved
            // it differently would show up here as two different numbers in two refusals for one condition.
            var response = await _factory.CreateClientForTenant(graph.TenantId, Roles.Staff)
                .PostAsJsonAsync("/api/v1/appointments/recurring", new
                {
                    graph.TenantId,
                    graph.LocationId,
                    graph.ServiceId,
                    graph.StaffId,
                    graph.CustomerId,
                    StartTimeOfDay = new TimeOnly(9, 0),
                    Pattern = (int)RecurrencePattern.Weekly,
                    Interval = 1,
                    StartDate = new DateOnly(2026, 12, 10),
                    EndDate = (DateOnly?)null,
                    MaxOccurrences = 2,
                    ConflictPolicy = 0,
                    Notes = "defaults probe"
                });
            var body = await BodyAsync(response);

            response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity, body);
            body.Should().Contain($"({TenantSetting.DefaultMaxAdvanceBookingDays} days ahead)",
                "the refusal quotes the horizon it resolved, which is the only way to see which copy answered");
        }
        finally
        {
            _factory.Clock.Release();
        }
    }

    [Fact]
    public async Task BookingUnderTwoHoursAhead_MustBeRefusedByTheNoticeDefault()
    {
        var graph = await SeedGraphWithoutSettingsAsync();

        _factory.Clock.Pin(Now);
        try
        {
            var response = await BookAsync(graph, Now.AddMinutes(30));
            var body = await BodyAsync(response);

            response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity, body);
            ReadRefusal(body).Should().Be(
                $"MinimumNoticeViolation|Appointments must be booked at least {TenantSetting.DefaultMinBookingNoticeMinutes} minutes in advance.");
        }
        finally
        {
            _factory.Clock.Release();
        }
    }

    [Fact]
    public async Task BookingBeyondSixtyDays_MustBeRefusedByTheHorizonDefault()
    {
        var graph = await SeedGraphWithoutSettingsAsync();

        _factory.Clock.Pin(Now);
        try
        {
            var response = await BookAsync(graph, Now.AddDays(61));
            var body = await BodyAsync(response);

            response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity, body);
            ReadRefusal(body).Should().Be(
                $"MaxAdvanceViolation|Appointments cannot be booked more than {TenantSetting.DefaultMaxAdvanceBookingDays} days in advance.",
                "three readers used to hold this number as their own literal, so the refusal text is the only place all three can be checked at once");
        }
        finally
        {
            _factory.Clock.Release();
        }
    }
}
