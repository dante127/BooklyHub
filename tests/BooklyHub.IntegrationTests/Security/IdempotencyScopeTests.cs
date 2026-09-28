using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BooklyHub.Api.Controllers;
using BooklyHub.Application.Security;
using BooklyHub.Domain.Entities.Customers;
using BooklyHub.Domain.Entities.Organizations;
using BooklyHub.Domain.Entities.Scheduling;
using BooklyHub.Domain.Entities.Services;
using BooklyHub.Domain.Entities.StaffMembers;
using BooklyHub.Domain.Entities.System;
using BooklyHub.Domain.Entities.Tenancy;
using BooklyHub.Infrastructure.Data;
using BooklyHub.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BooklyHub.IntegrationTests.Security;

/// <summary>
/// An idempotency key is a promise a tenant makes to itself. These tests pin the two ways that promise was
/// broken: a key stored without its tenant let one tenant replay another tenant's cached response, and a
/// key checked without its payload let a caller reuse it for a different request and get the old answer.
/// </summary>
public class IdempotencyScopeTests : IClassFixture<BooklyHubWebApplicationFactory>
{
    private readonly BooklyHubWebApplicationFactory _factory;

    public IdempotencyScopeTests(BooklyHubWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private sealed record Graph(Guid TenantId, Guid LocationId, Guid ServiceId, Guid StaffId, Guid CustomerId);

    private async Task<Graph> SeedGraphAsync(string name)
    {
        var graph = new Graph(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var bookingDay = DateTime.UtcNow.Date.AddDays(2);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var tenant = new Tenant(graph.TenantId, name, $"{name}-{graph.TenantId:N}", "UTC");
        tenant.Settings = new TenantSetting(graph.TenantId)
        {
            MinBookingNoticeMinutes = 10,
            MaxAdvanceBookingDays = 30,
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
            Price = 100.00m,
            BufferBeforeMinutes = 0,
            BufferAfterMinutes = 0
        });

        var staff = new Staff
        {
            Id = graph.StaffId,
            TenantId = graph.TenantId,
            LocationId = graph.LocationId,
            FirstName = "Doc",
            LastName = "One",
            Email = $"doc-{graph.StaffId:N}@idem.test"
        };
        staff.StaffServices.Add(new StaffService { TenantId = graph.TenantId, StaffId = graph.StaffId, ServiceId = graph.ServiceId });

        var workingHour = new WorkingHour
        {
            Id = Guid.NewGuid(),
            TenantId = graph.TenantId,
            StaffId = graph.StaffId,
            LocationId = graph.LocationId,
            DayOfWeek = bookingDay.DayOfWeek,
            IsWorkingDay = true
        };
        workingHour.Intervals.Add(new WorkingHourInterval(new TimeSpan(8, 0, 0), new TimeSpan(18, 0, 0), false));
        staff.WorkingHours.Add(workingHour);
        db.StaffMembers.Add(staff);

        db.Customers.Add(new Customer
        {
            Id = graph.CustomerId,
            TenantId = graph.TenantId,
            FirstName = "Ann",
            LastName = "Patient",
            Email = $"ann-{graph.CustomerId:N}@idem.test"
        });

        await db.SaveChangesAsync();
        return graph;
    }

    private static DateTime Slot(int hour) => DateTime.UtcNow.Date.AddDays(2).AddHours(hour);

    private async Task<HttpResponseMessage> BookAsync(Graph graph, DateTime startAtUtc, string idempotencyKey)
    {
        var client = _factory.CreateClientForTenant(graph.TenantId, Roles.Staff);
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/appointments")
        {
            Content = JsonContent.Create(new AppointmentsController.BookAppointmentRequest(
                graph.LocationId, graph.ServiceId, graph.StaffId, graph.CustomerId, startAtUtc, "idempotency test"))
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        return await client.SendAsync(request);
    }

    private static Guid ReadAppointmentId(string body) =>
        JsonDocument.Parse(body).RootElement.GetProperty("id").GetGuid();

    private static async Task<Guid> ReadAppointmentIdAsync(HttpResponseMessage response) =>
        ReadAppointmentId(await response.Content.ReadAsStringAsync());

    private async Task<int> CountAppointmentsAsync(Guid tenantId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.Appointments.IgnoreQueryFilters().CountAsync(a => a.TenantId == tenantId);
    }

    private async Task<IdempotencyRecord?> ReadRecordAsync(Graph graph, string key)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.IdempotencyRecords
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == $"{graph.TenantId:N}:{key}");
    }

    [Fact]
    public async Task Replay_SameTenantSameKeySamePayload_MustReturnTheCachedBooking()
    {
        var graph = await SeedGraphAsync("replay-tenant");
        var key = Guid.NewGuid().ToString("N");

        var first = await BookAsync(graph, Slot(10), key);
        Assert.True(first.StatusCode == HttpStatusCode.Created,
            $"expected 201, got {(int)first.StatusCode}: {await first.Content.ReadAsStringAsync()}");
        var bookedId = await ReadAppointmentIdAsync(first);

        var replay = await BookAsync(graph, Slot(10), key);

        Assert.True(replay.StatusCode == HttpStatusCode.Created,
            $"a retry of the same request must replay the original 201, got {(int)replay.StatusCode}");
        replay.Headers.Contains("X-Idempotent-Replay").Should().BeTrue("the client must be told this is a replay");
        (await ReadAppointmentIdAsync(replay)).Should().Be(bookedId, "the replay returns the original booking, not a new one");
        (await CountAppointmentsAsync(graph.TenantId)).Should().Be(1);
    }

    [Fact]
    public async Task Reuse_SameKeyWithDifferentPayload_MustBeRefused()
    {
        var graph = await SeedGraphAsync("reuse-tenant");
        var key = Guid.NewGuid().ToString("N");

        var first = await BookAsync(graph, Slot(10), key);
        Assert.True(first.StatusCode == HttpStatusCode.Created,
            $"expected 201, got {(int)first.StatusCode}: {await first.Content.ReadAsStringAsync()}");

        var reused = await BookAsync(graph, Slot(14), key);

        Assert.True(reused.StatusCode == HttpStatusCode.Conflict,
            $"same key with a different payload must be refused, got {(int)reused.StatusCode}: {await reused.Content.ReadAsStringAsync()}");
        reused.Headers.Contains("X-Idempotent-Replay").Should().BeFalse("a refusal is not a replay");
        (await CountAppointmentsAsync(graph.TenantId)).Should().Be(1, "the refused request must not book anything");
    }

    [Fact]
    public async Task Replay_DifferentTenantSameKey_MustNotLeakTheOtherTenantsResponse()
    {
        var tenantA = await SeedGraphAsync("tenant-a");
        var tenantB = await SeedGraphAsync("tenant-b");
        var sharedKey = Guid.NewGuid().ToString("N");

        var responseA = await BookAsync(tenantA, Slot(10), sharedKey);
        Assert.True(responseA.StatusCode == HttpStatusCode.Created,
            $"expected 201, got {(int)responseA.StatusCode}: {await responseA.Content.ReadAsStringAsync()}");
        var appointmentA = await ReadAppointmentIdAsync(responseA);

        var responseB = await BookAsync(tenantB, Slot(10), sharedKey);

        Assert.True(responseB.StatusCode == HttpStatusCode.Created,
            $"tenant B's first use of the key must book normally, got {(int)responseB.StatusCode}: {await responseB.Content.ReadAsStringAsync()}");
        responseB.Headers.Contains("X-Idempotent-Replay").Should().BeFalse(
            "tenant B must never be handed tenant A's cached response");
        var appointmentB = await ReadAppointmentIdAsync(responseB);
        appointmentB.Should().NotBe(appointmentA, "tenant B's booking is its own row, not an echo of tenant A's");

        (await CountAppointmentsAsync(tenantA.TenantId)).Should().Be(1);
        (await CountAppointmentsAsync(tenantB.TenantId)).Should().Be(1);
    }

    [Fact]
    public async Task ConcurrentBooking_SameKeySameSlot_MustBookExactlyOnce()
    {
        var graph = await SeedGraphAsync("race-tenant");
        var key = Guid.NewGuid().ToString("N");

        var responses = await Task.WhenAll(
            BookAsync(graph, Slot(10), key),
            BookAsync(graph, Slot(10), key));

        foreach (var response in responses)
        {
            Assert.True(response.StatusCode == HttpStatusCode.Created,
                $"both retries own the same booking, got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        }

        var firstId = await ReadAppointmentIdAsync(responses[0]);
        var secondId = await ReadAppointmentIdAsync(responses[1]);
        secondId.Should().Be(firstId, "one key buys one booking, so both responses name the same appointment");
        (await CountAppointmentsAsync(graph.TenantId)).Should().Be(1);
    }

    [Fact]
    public async Task Replay_AfterTheWindowClosed_MustNotBeServedFromTheCachedCopy()
    {
        var graph = await SeedGraphAsync("window-tenant");
        var key = Guid.NewGuid().ToString("N");

        try
        {
            var first = await BookAsync(graph, Slot(10), key);
            Assert.True(first.StatusCode == HttpStatusCode.Created,
                $"expected 201, got {(int)first.StatusCode}: {await first.Content.ReadAsStringAsync()}");
            var bookedId = await ReadAppointmentIdAsync(first);

            var replay = await BookAsync(graph, Slot(10), key);
            Assert.True(replay.StatusCode == HttpStatusCode.Created,
                $"inside the window a retry must replay, got {(int)replay.StatusCode}");
            replay.Headers.Contains("X-Idempotent-Replay").Should().BeTrue();
            (await ReadAppointmentIdAsync(replay)).Should().Be(bookedId);

            var stored = await ReadRecordAsync(graph, key);
            stored.Should().NotBeNull("the middleware stores the response with the window it promised");
            (stored!.ExpiresAtUtc - stored.CreatedAtUtc).Should().Be(TimeSpan.FromHours(24),
                "the window the record holds is the window the caller asked for, measured from one clock reading");

            // The distributed cache and the JWT stack keep their own real-time timers, so nothing here can age
            // the cached copy out except the deadline travelling inside it. Passing the window on the
            // application's clock therefore asks the only question that matters: does the copy know it died?
            // The slot is two days out and the working hours were seeded for its weekday, so after 25 hours it
            // is still a bookable time and the only thing left to refuse on is the existing booking.
            _factory.Clock.AdvanceBy(TimeSpan.FromHours(25));

            var afterWindow = await BookAsync(graph, Slot(10), key);
            var body = await afterWindow.Content.ReadAsStringAsync();

            // The replay marker is what separates the two ways this can answer. Serving the stored copy stamps
            // the header; executing the request again lets the booking's own key recovery hand back the
            // appointment it already made — same booking, no replay, because the window really did close.
            Assert.True(afterWindow.StatusCode == HttpStatusCode.Created,
                $"the expired key must be re-executed and answered by the booking's own key recovery, got {(int)afterWindow.StatusCode}: {body}");
            afterWindow.Headers.Contains("X-Idempotent-Replay").Should().BeFalse(
                "an expired key may not be answered from the stored copy, which is the promise the window exists to keep");
            ReadAppointmentId(body).Should().Be(bookedId,
                "the re-run finds the appointment the key already made rather than booking a second one");
            (await CountAppointmentsAsync(graph.TenantId)).Should().Be(1,
                "one key buys one booking on either layer, so the re-run must not add a row");
        }
        finally
        {
            _factory.Clock.Release();
        }
    }
}
