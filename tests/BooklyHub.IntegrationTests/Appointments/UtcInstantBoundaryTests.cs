using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using BooklyHub.Api.Controllers;
using BooklyHub.Application.Security;
using BooklyHub.Domain.Entities.Appointments;
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
/// BL-05, the <c>Kind</c> half: a start time arriving over HTTP kept whatever ticks the serializer had put in it,
/// and those ticks mean different things depending on how the string was written and on which zone the process runs
/// in. Measured before the change, on a host at UTC+3: a body of <c>"09:00:00+00:00"</c> arrived as
/// <c>Kind=Local</c> carrying 12:00, and the column is <c>datetime2</c>, which stores the ticks it is handed — so
/// the appointment was written three hours from the instant its caller named, and the shift differs per deployment.
/// The same instant written as <c>09:00:00Z</c> was written correctly, and written with no designator it was read as
/// UTC by luck rather than by rule. These facts pin the instant, not the string: what a caller names is what the row
/// must hold, whichever of the three shapes it arrives in.
/// </summary>
/// <remarks>
/// The offset a fact sends is deliberately two hours away from the host's own zone, so a regression shows up as a
/// two-hour error on every machine instead of vanishing on one deployed at UTC — see <see cref="SendOffset"/>.
/// </remarks>
public class UtcInstantBoundaryTests : IClassFixture<BooklyHubWebApplicationFactory>
{
    private const string WireFormat = "yyyy-MM-ddTHH:mm:ss";

    private readonly BooklyHubWebApplicationFactory _factory;

    public UtcInstantBoundaryTests(BooklyHubWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private sealed record Graph(Guid TenantId, Guid LocationId, Guid ServiceId, Guid StaffId, Guid CustomerId);

    /// <summary>
    /// The offset the wire strings are written in: the host's own, moved by two hours. Sending the host's own offset
    /// would let the old bug pass on a UTC deployment (a zero-hour shift is invisible), which is the difference
    /// between a fact and a decoration.
    /// </summary>
    private static TimeSpan SendOffset
    {
        get
        {
            var shifted = TimeZoneInfo.Local.BaseUtcOffset + TimeSpan.FromHours(2);
            return shifted <= TimeSpan.FromHours(14) ? shifted : TimeSpan.FromHours(-2);
        }
    }

    private static string OffsetSuffix(TimeSpan offset) =>
        $"{(offset < TimeSpan.Zero ? '-' : '+')}{Math.Abs(offset.Hours):00}:{Math.Abs(offset.Minutes):00}";

    /// <summary>One instant, written in the zone <paramref name="offset"/> names — the same UTC time, different text.</summary>
    private static string WrittenIn(DateTime utcInstant, TimeSpan offset) =>
        (utcInstant + offset).ToString(WireFormat, CultureInfo.InvariantCulture) + OffsetSuffix(offset);

    private static string WrittenWithoutZone(DateTime utcInstant) =>
        utcInstant.ToString(WireFormat, CultureInfo.InvariantCulture);

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private async Task<Graph> SeedGraphAsync()
    {
        var graph = new Graph(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var tenant = new Tenant(graph.TenantId, "Zone Clinic", $"zone-{graph.TenantId:N}", "UTC");
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
            Price = 100.00m
        });

        var bookingDay = DateTime.UtcNow.Date.AddDays(2);
        var staff = new Staff
        {
            Id = graph.StaffId,
            TenantId = graph.TenantId,
            LocationId = graph.LocationId,
            FirstName = "Doc",
            LastName = "One",
            Email = $"doc-{graph.StaffId:N}@zone.test"
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
            Email = $"ann-{graph.CustomerId:N}@zone.test"
        });

        await db.SaveChangesAsync();
        return graph;
    }

    private async Task<Appointment> SeedConfirmedAppointmentAsync(Graph graph, DateTime startAtUtc)
    {
        var appointment = Appointment.Create(
            graph.TenantId, graph.LocationId, graph.ServiceId, graph.StaffId, graph.CustomerId,
            startAtUtc, startAtUtc.AddMinutes(30), 30, 100.00m, "USD");
        appointment.TransitionTo(AppointmentStatus.Confirmed, DateTime.UtcNow, "seeded for the zone facts");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.Appointments.Add(appointment);
        await db.SaveChangesAsync();
        return appointment;
    }

    /// <summary>Reads the row back through a fresh context, so the answer is what SQL holds, not what was tracked.
    /// The scope carries no tenant, which is exactly why the filter has to be stepped over: the fact is about the
    /// instant in the column, not about whether the row is visible to a request.</summary>
    private async Task<DateTime> ReadStoredStartAsync(Guid appointmentId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return (await db.Appointments.IgnoreQueryFilters().AsNoTracking().FirstAsync(a => a.Id == appointmentId)).StartAtUtc;
    }

    private HttpClient Client(Graph graph, string role) => _factory.CreateClientForTenant(graph.TenantId, role);

    private static string TextField(JsonElement root, string name) => root.GetProperty(name).GetString()!;

    private static DateTime ReadInstant(JsonElement root, string name) =>
        DateTime.Parse(TextField(root, name), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    [Fact]
    public async Task ABookingStartWrittenInAnotherZone_MustBookTheInstantItNames()
    {
        var graph = await SeedGraphAsync();
        var slot = DateTime.UtcNow.Date.AddDays(2).AddHours(9);

        var body = $"{{\"locationId\":\"{graph.LocationId}\",\"serviceId\":\"{graph.ServiceId}\"," +
                   $"\"staffId\":\"{graph.StaffId}\",\"customerId\":\"{graph.CustomerId}\"," +
                   $"\"startAtUtc\":\"{WrittenIn(slot, SendOffset)}\"}}";

        var response = await Client(graph, Roles.Staff).PostAsync("/api/v1/appointments", Json(body));
        var text = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Created, text);

        using var created = JsonDocument.Parse(text);
        var appointmentId = created.RootElement.GetProperty("id").GetGuid();

        (await ReadStoredStartAsync(appointmentId)).Should().Be(slot,
            $"the caller named {slot:O} by writing it {(SendOffset.Hours >= 0 ? "+" : "")}{SendOffset.Hours} hours " +
            "from UTC, and datetime2 stores the ticks it is handed, so an unrebased value would be off by the " +
            "host's own zone instead of the instant asked for");

        created.RootElement.GetProperty("startAtUtc").GetString().Should().EndWith("Z",
            "the row answers with the instant it holds, and an answer that carries no zone is a second guess");
    }

    [Fact]
    public async Task ABookingStartWithNoZoneDesignator_MustBeReadAsTheUtcTheFieldNamePromises()
    {
        var graph = await SeedGraphAsync();
        var slot = DateTime.UtcNow.Date.AddDays(2).AddHours(10);

        // The field is called startAtUtc, so a value with no designator is read as UTC — and, after this change,
        // says so on the way out instead of leaving the reader to assume it.
        var body = $"{{\"locationId\":\"{graph.LocationId}\",\"serviceId\":\"{graph.ServiceId}\"," +
                   $"\"staffId\":\"{graph.StaffId}\",\"customerId\":\"{graph.CustomerId}\"," +
                   $"\"startAtUtc\":\"{WrittenWithoutZone(slot)}\"}}";

        var response = await Client(graph, Roles.Staff).PostAsync("/api/v1/appointments", Json(body));
        var text = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Created, text);

        using var created = JsonDocument.Parse(text);
        var appointmentId = created.RootElement.GetProperty("id").GetGuid();

        (await ReadStoredStartAsync(appointmentId)).Should().Be(slot,
            "assume-UTC is what the name promises, and the ticks must not move on the way in");
        TextField(created.RootElement, "startAtUtc").Should().EndWith("Z",
            "the answer should carry the assumption it made rather than repeat the caller's ambiguity");
        ReadInstant(created.RootElement, "startAtUtc").Should().Be(slot);
    }

    [Fact]
    public async Task ARescheduleStartWrittenInAnotherZone_MustMoveToTheInstantItNames()
    {
        var graph = await SeedGraphAsync();
        var appointment = await SeedConfirmedAppointmentAsync(graph, DateTime.UtcNow.Date.AddDays(2).AddHours(9));
        var newSlot = DateTime.UtcNow.Date.AddDays(2).AddHours(11);

        var body = $"{{\"newStartAtUtc\":\"{WrittenIn(newSlot, SendOffset)}\",\"Reason\":\"moved by staff\"}}";

        var response = await Client(graph, Roles.Manager)
            .PostAsync($"/api/v1/appointments/{appointment.Id}/reschedule", Json(body));
        var text = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, text);

        (await ReadStoredStartAsync(appointment.Id)).Should().Be(newSlot,
            "a reschedule written in another zone moves the appointment to a time nobody chose if the offset is dropped");
    }

    [Fact]
    public async Task AWindowBoundWrittenInAnotherZone_MustBeEchoedAsTheUtcItWasReadAs()
    {
        var graph = await SeedGraphAsync();
        var from = new DateTime(2027, 6, 1, 9, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2027, 6, 2, 9, 0, 0, DateTimeKind.Utc);

        var response = await Client(graph, Roles.Manager).GetAsync(
            $"/api/v1/reports/dashboard?fromUtc={Uri.EscapeDataString(WrittenIn(from, SendOffset))}" +
            $"&toUtc={Uri.EscapeDataString(WrittenWithoutZone(to))}");
        var text = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, text);

        using var report = JsonDocument.Parse(text);
        var root = report.RootElement;

        // The binder already turns an offset-bearing bound into a UTC instant, so the window is right; what was not
        // right is that a bound written with no designator came back with no designator, leaving the report's own
        // reader to guess which zone the period was in.
        TextField(root, "fromUtc").Should().EndWith("Z", "a window that names no zone is a window two readers can differ on");
        TextField(root, "toUtc").Should().EndWith("Z");
        ReadInstant(root, "fromUtc").Should().Be(from);
        ReadInstant(root, "toUtc").Should().Be(to);
    }
}
