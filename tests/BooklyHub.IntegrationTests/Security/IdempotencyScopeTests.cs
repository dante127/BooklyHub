using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BooklyHub.Api.Controllers;
using BooklyHub.Application.Payments;
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
/// An idempotency key is a promise a tenant makes to itself. These tests pin the ways that promise was broken:
/// a key stored without its tenant let one tenant replay another tenant's cached response, a key checked without
/// its payload let a caller reuse it for a different request and get the old answer, a key whose window had closed
/// was still refused to the response that replaced it (<c>IDEM-01</c>), and a save that cleared the key's row
/// without asking whether that row was still alive would have let the loser of a concurrent pair overwrite the
/// winner.
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

    /// <summary>
    /// IDEM-01: the window closing stopped the row from being *read* but did not stop it being *there*, and the
    /// primary key is the same string either way. Every retry that arrived after the window re-ran its request and
    /// then silently failed to record what it had answered, so the key stopped being replayable for as long as the
    /// dead row stayed. The stored deadline moving forward is the observable half of that; the re-run is not
    /// allowed to leave the caller with an answer nobody kept.
    /// </summary>
    [Fact]
    public async Task AKeyReusedAfterItsWindow_MustStoreTheAnswerThatReplacedTheExpiredRow()
    {
        var graph = await SeedGraphAsync("reused-window-tenant");
        var key = Guid.NewGuid().ToString("N");

        try
        {
            var first = await BookAsync(graph, Slot(10), key);
            Assert.True(first.StatusCode == HttpStatusCode.Created,
                $"expected 201, got {(int)first.StatusCode}: {await first.Content.ReadAsStringAsync()}");

            var original = await ReadRecordAsync(graph, key);
            original.Should().NotBeNull();
            var originalExpiry = original!.ExpiresAtUtc;

            _factory.Clock.AdvanceBy(TimeSpan.FromHours(25));

            var retry = await BookAsync(graph, Slot(10), key);
            var retryBody = await retry.Content.ReadAsStringAsync();
            Assert.True(retry.StatusCode == HttpStatusCode.Created,
                $"the re-run must answer on its own, got {(int)retry.StatusCode}: {retryBody}");
            retry.Headers.Contains("X-Idempotent-Replay").Should().BeFalse(
                "the window had closed, so this request really executed");

            var replaced = await ReadRecordAsync(graph, key);
            replaced.Should().NotBeNull("a key that was used again is a key with a response to keep");
            replaced!.ExpiresAtUtc.Should().BeAfter(originalExpiry,
                "the expired row is replaced rather than collided with, so what is stored is what the caller was answered");
            replaced.ResponseBody.Should().Be(retryBody,
                "the body a replay hands back is the body this re-run produced, byte for byte");
            (replaced.ExpiresAtUtc - replaced.CreatedAtUtc).Should().Be(RetentionPolicy.IdempotencyWindow,
                "the replacement carries the same window the middleware promises, not the dead row's");

            var replay = await BookAsync(graph, Slot(10), key);
            replay.Headers.Contains("X-Idempotent-Replay").Should().BeTrue(
                "one re-run buys one execution: the retry after it must be served from what it stored");
            (await CountAppointmentsAsync(graph.TenantId)).Should().Be(1,
                "and neither layer has booked the patient twice");
        }
        finally
        {
            _factory.Clock.Release();
        }
    }

    /// <summary>
    /// The other half of the same write, and the half that a fix could easily destroy while fixing the first:
    /// the row being cleared has to be the dead one. Two callers holding one key concurrently both miss the
    /// read, both run, and the loser arrives at the store while the winner's row is still inside its window.
    /// Deleting on key alone would hand the winner's response to the loser's failure, so a later retry would
    /// replay an answer that is not the one that booked the appointment.
    /// </summary>
    [Fact]
    public async Task ASave_MustNotClearALiveRowThatSharesItsKey()
    {
        var graph = await SeedGraphAsync("live-row-tenant");
        var scopedKey = $"{graph.TenantId:N}:{Guid.NewGuid():N}";

        var winnerBody = "{\"appointmentId\":\"00000000-0000-0000-0000-000000000001\",\"winner\":true}";
        // One clock reading for both the row and the expectation: the unpinned test clock drifts by the
        // microsecond, and a `datetime2` column compared against a second reading would fail on the drift.
        var seededAt = _factory.Clock.UtcNow;
        var winnerExpiry = seededAt.Add(RetentionPolicy.IdempotencyWindow);

        using (var scope = _factory.Services.CreateScope())
        {
            var seedDb = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            seedDb.IdempotencyRecords.Add(new IdempotencyRecord(
                scopedKey, graph.TenantId, "hash-of-the-winner", 201, winnerBody,
                RetentionPolicy.IdempotencyWindow, seededAt));
            await seedDb.SaveChangesAsync();
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<IIdempotencyService>();
            await service.SaveEntryAsync(scopedKey, graph.TenantId, "hash-of-the-loser", 409,
                "{\"detail\":\"this is not the response that booked it\"}",
                RetentionPolicy.IdempotencyWindow, CancellationToken.None);
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var readDb = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var stored = await readDb.IdempotencyRecords.AsNoTracking().FirstOrDefaultAsync(r => r.Id == scopedKey);

            stored.Should().NotBeNull("a key that was answered once is a key with a stored answer");
            stored!.ResponseBody.Should().Be(winnerBody,
                "the loser of a concurrent pair may not overwrite the response a replay owes");
            stored.RequestHash.Should().Be("hash-of-the-winner",
                "and the payload check on the next retry must still be measured against the winner's request");
            stored.ExpiresAtUtc.Should().Be(winnerExpiry,
                "the row that survives is the row whose window is still open");
        }
    }
}
