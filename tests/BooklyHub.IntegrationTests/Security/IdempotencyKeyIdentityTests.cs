using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using BooklyHub.Application.Common.Interfaces;
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
/// KEY-02: an idempotency key is an identity, and the database's default collation folded it. Three doors decided
/// "is this the request I already answered" with a SQL equality over a case-insensitive column, so
/// <c>AB12…</c> and <c>ab12…</c> were one key: measured on this tree before the fix, a case-flipped key carrying a
/// different booking was refused <c>409</c> and never ran, and — the door that costs money — a case-flipped key
/// carrying a <c>70.00</c> charge was answered <c>200</c> with the receipt of an earlier <c>40.00</c> payment, with
/// one row in the table and the second capture never attempted. One key, three doors, so the family is pinned here
/// door by door: a fix at the replay store alone turns the <c>409</c> into the booking door's <c>422</c> and changes
/// no outcome for the caller.
/// </summary>
/// <remarks>
/// The two doors that read a domain row are reached by deleting the replay row, which is exactly what the window
/// does to it an hour later: the retention sweep and the replay window make that state ordinary, not exotic.
/// </remarks>
public class IdempotencyKeyIdentityTests : IClassFixture<BooklyHubWebApplicationFactory>
{
    private readonly BooklyHubWebApplicationFactory _factory;

    public IdempotencyKeyIdentityTests(BooklyHubWebApplicationFactory factory) => _factory = factory;

    private sealed record Graph(Guid TenantId, Guid LocationId, Guid ServiceId, Guid StaffId, Guid CustomerId);

    [Fact]
    public async Task ACaseFlippedKeyForADifferentBooking_MustRunAndStoreItsOwnAnswer()
    {
        var graph = await SeedGraphAsync("key02-different");
        var key = Guid.NewGuid().ToString("N");

        var first = await BookAsync(graph, Slot(10), key);
        var second = await BookAsync(graph, Slot(11), Flip(key));

        first.StatusCode.Should().Be(HttpStatusCode.Created, await second.Content.ReadAsStringAsync());
        second.StatusCode.Should().Be(HttpStatusCode.Created,
            $"two keys that differ by case are two promises from two requests, and the second was refused for the first one's body: {await second.Content.ReadAsStringAsync()}");

        (await CountAppointmentsAsync(graph)).Should().Be(2, "the second request had its own slot and had to be executed");
        (await ReplayHeaderAsync(second)).Should().Be("no", "a replay of the first answer would be the fold wearing a different hat");

        var stored = await StoredIdsAsync(graph);
        stored.Should().HaveCount(2, "one primary key per promise: the store cannot hold two rows the collation calls equal");
        stored.Should().OnlyContain(id => Regex.IsMatch(id, "^[0-9a-f]{64}$"),
            "a lowercase digest leaves the collation nothing to fold, and 64 characters still fit the nvarchar(256) key");
    }

    [Fact]
    public async Task ACaseFlippedKeyForTheSameSlot_MustReachTheBookingGuardInsteadOfTheReplayStore()
    {
        var graph = await SeedGraphAsync("key02-sameslot");
        var key = Guid.NewGuid().ToString("N");

        await BookAsync(graph, Slot(10), key);
        var second = await BookAsync(graph, Slot(10), Flip(key));
        var body = await second.Content.ReadAsStringAsync();

        // Before the fix this call was answered 201 with the first booking's own body and no sign it had not run.
        // Now the request is executed, and what refuses it is the slot, which is the honest answer to a second
        // booking attempt — a conflict the client caused, not a key it shares with somebody else.
        second.StatusCode.Should().Be(HttpStatusCode.Conflict, body);
        body.Should().Contain("Booking Conflict", "the refusal must come from the occupancy guard, not from the replay store");
        body.Should().NotContain("Idempotency-Key was already used", body);
        (await CountAppointmentsAsync(graph)).Should().Be(1);
    }

    [Fact]
    public async Task TheBookingDoor_MustNotFoldAKeyAfterItsReplayRowIsGone()
    {
        var graph = await SeedGraphAsync("key02-door2");
        var key = Guid.NewGuid().ToString("N");

        await BookAsync(graph, Slot(10), key);
        await DropReplayRowsAsync(graph);

        var second = await BookAsync(graph, Slot(11), Flip(key));
        var body = await second.Content.ReadAsStringAsync();

        second.StatusCode.Should().Be(HttpStatusCode.Created,
            $"""
             the appointment row outlives the replay window, so this door decides on its own: {body}
             """);
        body.Should().NotContain("IdempotencyKeyReused", body);
        (await CountAppointmentsAsync(graph)).Should().Be(2);
    }

    [Fact]
    public async Task ThePaymentDoor_MustNotAnswerACaseFlippedKeyWithSomebodyElsesReceipt()
    {
        var graph = await SeedGraphAsync("key02-door3");
        var key = Guid.NewGuid().ToString("N");
        var appointmentId = await BookedIdAsync(graph, Slot(13), Guid.NewGuid().ToString("N"));

        var first = await ChargeAsync(graph, appointmentId, key, 40.00m);
        first.StatusCode.Should().Be(HttpStatusCode.OK, await first.Content.ReadAsStringAsync());

        await DropReplayRowsAsync(graph);

        var second = await ChargeAsync(graph, appointmentId, Flip(key), 30.00m);
        var secondBody = await second.Content.ReadAsStringAsync();

        // Measured before the fix: 200 and the 40.00 payment, one row in the table, 70.00 never captured. The
        // client was handed a receipt for money the clinic never collected.
        second.StatusCode.Should().Be(HttpStatusCode.OK, secondBody);
        ReadAmount(secondBody).Should().Be(30.00m, "the response must describe the charge this request asked for");

        (await CountPaymentsAsync(graph)).Should().Be(2);
    }

    [Fact]
    public async Task ARetryWithTheSameBytes_MustStillReplayTheBookingItAlreadyBought()
    {
        var graph = await SeedGraphAsync("key02-honest");
        var key = Guid.NewGuid().ToString("N");

        var first = await BookAsync(graph, Slot(10), key);
        var retry = await BookAsync(graph, Slot(10), key);

        (await CountAppointmentsAsync(graph)).Should().Be(1);
        ReadId(await retry.Content.ReadAsStringAsync()).Should().Be(ReadId(await first.Content.ReadAsStringAsync()));
        (await ReplayHeaderAsync(retry)).Should().Be("true",
            "byte-exact identity must not become 'nothing matches': a real retry is the case this store exists for");
        (await StoredIdsAsync(graph)).Should().HaveCount(1);
    }

    [Fact]
    public async Task ARowWrittenBeforeTheDigestExisted_MustStillAnswerTheRetryThatWroteIt()
    {
        var graph = await SeedGraphAsync("key02-legacy");
        var key = Guid.NewGuid().ToString("N");
        var booked = await BookAsync(graph, Slot(10), key);
        var bookedId = ReadId(await booked.Content.ReadAsStringAsync());

        // The deploy state, built by hand: the replay row the old code left behind, addressed by the verbatim
        // scoped key. The digest read cannot see it, so the request runs again — and must be answered by the
        // booking door, which still holds the caller's own string. That is why the row doors keep the key verbatim.
        using (var seedScope = _factory.Services.CreateScope())
        {
            var db = seedScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.IdempotencyRecords.IgnoreQueryFilters()
                .Where(r => r.TenantId == graph.TenantId)
                .ExecuteDeleteAsync();
            db.IdempotencyRecords.Add(new IdempotencyRecord(
                $"{graph.TenantId:N}:{key}", graph.TenantId, "x", 201, "{}",
                RetentionPolicy.IdempotencyWindow, _factory.Clock.UtcNow));
            await db.SaveChangesAsync();

            // The cached copy has to go, because inside one running host it is what answers a retry and the row
            // would never be read — the fact would prove the cache instead of the deploy. A release restarts the
            // process, so the durable row is all that is left. This is the one place the test has to name the
            // service's private cache format to simulate something the service cannot do: RemoveByPrefixAsync is
            // a stub that returns without invalidating anything.
            var cache = seedScope.ServiceProvider.GetRequiredService<ICacheService>();
            await cache.RemoveAsync($"idemp:{IdempotencyIdentity.StorageId($"{graph.TenantId:N}:{key}")}");
        }

        var retry = await BookAsync(graph, Slot(10), key);
        var body = await retry.Content.ReadAsStringAsync();

        retry.StatusCode.Should().Be(HttpStatusCode.Created, body);
        ReadId(body).Should().Be(bookedId, "a retry across the release must not buy a second appointment");
        (await CountAppointmentsAsync(graph)).Should().Be(1);
    }

    private static string Flip(string key) => key.ToUpperInvariant();

    private static Guid ReadId(string body) => JsonDocument.Parse(body).RootElement.GetProperty("id").GetGuid();

    private static decimal ReadAmount(string body) => JsonDocument.Parse(body).RootElement.GetProperty("amount").GetDecimal();

    private static DateTime Slot(int hour) => DateTime.UtcNow.Date.AddDays(2).AddHours(hour);

    private async Task<string> ReplayHeaderAsync(HttpResponseMessage response) =>
        response.Headers.TryGetValues("X-Idempotent-Replay", out var values) ? string.Join(",", values) : "no";

    private async Task<Guid> BookedIdAsync(Graph graph, DateTime start, string key) =>
        ReadId(await (await BookAsync(graph, start, key)).Content.ReadAsStringAsync());

    private async Task<HttpResponseMessage> BookAsync(Graph graph, DateTime startAtUtc, string idempotencyKey)
    {
        var client = _factory.CreateClientForTenant(graph.TenantId, Roles.Manager);
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/appointments")
        {
            Content = JsonContent.Create(new
            {
                graph.LocationId,
                graph.ServiceId,
                graph.StaffId,
                graph.CustomerId,
                StartAtUtc = startAtUtc,
                Notes = "key02 identity"
            })
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        return await client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> ChargeAsync(Graph graph, Guid appointmentId, string idempotencyKey, decimal amount)
    {
        var client = _factory.CreateClientForTenant(graph.TenantId, Roles.Manager);
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/payments/charge")
        {
            Content = JsonContent.Create(new { AppointmentId = appointmentId, Amount = amount, Currency = "USD", Method = 0 })
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        return await client.SendAsync(request);
    }

    private async Task<int> CountAppointmentsAsync(Graph graph)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.Appointments.IgnoreQueryFilters().CountAsync(a => a.TenantId == graph.TenantId);
    }

    private async Task<int> CountPaymentsAsync(Graph graph)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.Payments.IgnoreQueryFilters().CountAsync(p => p.TenantId == graph.TenantId);
    }

    private async Task<IReadOnlyList<string>> StoredIdsAsync(Graph graph)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.IdempotencyRecords.IgnoreQueryFilters()
            .Where(r => r.TenantId == graph.TenantId)
            .Select(r => r.Id)
            .ToListAsync();
    }

    /// <summary>What the replay window does on its own an hour later, done now so the row doors are what answers.</summary>
    private async Task DropReplayRowsAsync(Graph graph)
    {
        var removed = await StoredIdsAsync(graph);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.IdempotencyRecords.IgnoreQueryFilters()
            .Where(r => r.TenantId == graph.TenantId)
            .ExecuteDeleteAsync();
        removed.Should().NotBeEmpty("a door reached with nothing to bypass would prove the replay store, not itself");
    }

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
            Email = $"doc-{graph.StaffId:N}@key02.test"
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
            Email = $"ann-{graph.CustomerId:N}@key02.test"
        });

        await db.SaveChangesAsync();
        return graph;
    }
}
