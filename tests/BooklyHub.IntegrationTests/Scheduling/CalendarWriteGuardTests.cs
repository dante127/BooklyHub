using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BooklyHub.Application.Security;
using BooklyHub.Domain.Entities.Customers;
using BooklyHub.Domain.Entities.Organizations;
using BooklyHub.Domain.Entities.Scheduling;
using BooklyHub.Domain.Entities.Services;
using BooklyHub.Domain.Entities.StaffMembers;
using BooklyHub.Domain.Entities.Tenancy;
using BooklyHub.Infrastructure.Data;
using BooklyHub.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BooklyHub.IntegrationTests.Scheduling;

/// <summary>
/// <c>CAL-01</c>: the booking guard decides a day from <c>Holidays</c>, <c>BusinessHours</c> and
/// <c>AvailabilityExceptions</c> — <c>AvailabilityService.LoadCalendarAsync</c> reads all three before it looks at one
/// appointment — and until this file's routes existed, nothing in <c>src</c> wrote any of them. The consequence is not
/// cosmetic: <c>SlotUnavailableReason.Closed</c> had exactly one producer, <c>closedDates</c>, built from a table no
/// code path could fill, so on every deployed database the close-check was dead code and a clinic that needed to close
/// a day or send one staff member on sick leave had to do it with an <c>INSERT</c> — outside every permission, outside
/// every validation, and invisible to the portal that is still offering the slots.
/// </summary>
/// <remarks>
/// Each fact below writes through the route and then reads the consequence through a <em>different</em> surface — the
/// public availability preview, or the booking guard itself — because "the row is in the table" is a claim about the
/// writer only. The guard's own answer is the thing a client feels, so that is what is asserted, in both directions:
/// the write closes the day and the delete opens it again.
/// </remarks>
public class CalendarWriteGuardTests : IClassFixture<BooklyHubWebApplicationFactory>
{
    private readonly BooklyHubWebApplicationFactory _factory;

    public CalendarWriteGuardTests(BooklyHubWebApplicationFactory factory) => _factory = factory;

    private sealed record Graph(
        Guid TenantId, Guid LocationId, Guid ServiceId, Guid StaffId, Guid CustomerId,
        DateOnly Day, DateTime SlotStartUtc);

    /// <summary>What the staff member's roster row carries: a shift, or only a break — the case the guard reads BusinessHours for.</summary>
    private enum Roster
    {
        Shift,

        /// <summary>No working window on the row, so the day's bookable time comes from the location's opening hours.</summary>
        BreakOnly
    }

    private async Task<Graph> SeedGraphAsync(string slug, Roster roster = Roster.Shift)
    {
        var graph = new Graph(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(2)),
            DateTime.UtcNow.Date.AddDays(2).AddHours(9));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var tenant = new Tenant(graph.TenantId, $"{slug} Clinic", $"{slug}-{graph.TenantId:N}", "UTC");
        tenant.Settings = new TenantSetting(graph.TenantId)
        {
            MinBookingNoticeMinutes = 0,
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

        var staff = new Staff
        {
            Id = graph.StaffId,
            TenantId = graph.TenantId,
            LocationId = graph.LocationId,
            FirstName = "Ava",
            LastName = "Clinic",
            Email = $"ava-{slug}-{graph.StaffId:N}@calendar.test"
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
            DayOfWeek = graph.Day.DayOfWeek,
            IsWorkingDay = true
        };
        // A shift the preview can offer slots from, and — for the break-only fixture — its opposite: a row carrying
        // only a break has no working window, which is the guard's one condition for reading the location's opening
        // hours (AvailabilityService.cs:559-562).
        if (roster == Roster.Shift)
        {
            workingHour.Intervals.Add(new WorkingHourInterval(new TimeSpan(8, 0, 0), new TimeSpan(18, 0, 0), isBreak: false));
        }
        else
        {
            workingHour.Intervals.Add(new WorkingHourInterval(new TimeSpan(9, 30, 0), new TimeSpan(10, 0, 0), isBreak: true));
        }
        staff.WorkingHours.Add(workingHour);
        db.StaffMembers.Add(staff);

        db.Customers.Add(new Customer
        {
            Id = graph.CustomerId,
            TenantId = graph.TenantId,
            FirstName = "Ann",
            LastName = "Patient",
            Email = $"ann-{graph.CustomerId:N}@calendar.test"
        });

        await db.SaveChangesAsync();
        return graph;
    }

    private async Task<Guid> AddLocationAsync(Graph graph, string name)
    {
        var locationId = Guid.NewGuid();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.Locations.Add(new Location
        {
            Id = locationId,
            TenantId = graph.TenantId,
            Name = name,
            Address = $"2 {name} St",
            City = "Springfield",
            Country = "US",
            TimeZoneId = "UTC"
        });
        await db.SaveChangesAsync();
        return locationId;
    }

    private HttpClient Client(Graph graph, string role = Roles.Manager) =>
        _factory.CreateClientForTenant(graph.TenantId, role);

    private static async Task<JsonDocument> ReadAsync(HttpClient http, string url)
    {
        var response = await http.GetAsync(url);
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return JsonDocument.Parse(body);
    }

    /// <summary>The public preview, on the day the graph was seeded for.</summary>
    private async Task<JsonDocument> PreviewAsync(Graph graph, Guid? locationId = null, DateOnly? day = null) =>
        await ReadAsync(_factory.CreateClient(),
            $"/api/v1/availability?tenantId={graph.TenantId}&locationId={locationId ?? graph.LocationId}" +
            $"&serviceId={graph.ServiceId}&staffId={graph.StaffId}&date={day ?? graph.Day}");

    private static bool DayIsOpen(JsonDocument preview) => preview.RootElement.GetProperty("isOpen").GetBoolean();

    private static int SlotCount(JsonDocument preview) => preview.RootElement.GetProperty("slots").GetArrayLength();

    private async Task<(HttpStatusCode Status, string Body)> BookSlotAsync(Graph graph, Guid? locationId = null)
    {
        var response = await Client(graph, Roles.Receptionist).PostAsJsonAsync("/api/v1/appointments", new
        {
            LocationId = locationId ?? graph.LocationId,
            graph.ServiceId,
            graph.StaffId,
            graph.CustomerId,
            StartAtUtc = graph.SlotStartUtc
        });
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private async Task<int> CountAsync<T>(System.Linq.Expressions.Expression<Func<T, bool>> predicate)
        where T : class
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.Set<T>().IgnoreQueryFilters().CountAsync(predicate);
    }

    private async Task<JsonDocument> PostJsonAsync(HttpClient http, string url, object body)
    {
        var response = await http.PostAsJsonAsync(url, body);
        var text = await response.Content.ReadAsStringAsync();
        response.IsSuccessStatusCode.Should().Be(true, $"{url} answered {(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text);
    }

    private static string Rule(JsonDocument problem) => problem.RootElement.GetProperty("rule").GetString()!;

    // ---------------------------------------------------------------------------------------------
    // Holidays: the writer has to reach the guard, in both directions, and reach it where the guard reads it.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task AHolidayWrittenThroughTheRoute_MustCloseTheDayInThePreviewAndRefuseTheBooking()
    {
        var graph = await SeedGraphAsync("close");

        // Control run: without the holiday row the same day is bookable, so the refusal below is about this write.
        using (var before = await PreviewAsync(graph))
        {
            DayIsOpen(before).Should().BeTrue("the fixture's own day is open until the calendar says otherwise");
            SlotCount(before).Should().BeGreaterThan(0, "a preview this fact calls open has to have slots in it");
        }
        (await BookSlotAsync(graph)).Status.Should().Be(HttpStatusCode.Created);

        var created = await PostJsonAsync(Client(graph), "/api/v1/calendar/holidays",
            new { LocationId = (Guid?)null, Name = "Founders Day", Date = graph.Day, RecurringAnnually = false });

        created.RootElement.GetProperty("date").GetString().Should().Be(graph.Day.ToString("yyyy-MM-dd"));
        created.RootElement.GetProperty("locationId").ValueKind.Should().Be(JsonValueKind.Null);

        using var after = await PreviewAsync(graph);
        DayIsOpen(after).Should().BeFalse(
            "the day's own answer was isOpen:true with slots:[] — the preview had no way to say closed until this route existed");
        SlotCount(after).Should().Be(0);

        var (status, body) = await BookSlotAsync(graph);
        status.Should().Be(HttpStatusCode.Conflict, body);
        body.Should().Contain("closed for booking", "the guard's Closed reason is the refusal a caller has to be able to read");
    }

    [Fact]
    public async Task ATenantWideHoliday_MustCloseEveryLocationAndALocationHoliday_OnlyItsOwn()
    {
        var graph = await SeedGraphAsync("scope");
        var secondLocation = await AddLocationAsync(graph, "North");

        await PostJsonAsync(Client(graph), "/api/v1/calendar/holidays",
            new { LocationId = graph.LocationId, Name = "Front closed", Date = graph.Day, RecurringAnnually = false });

        using (var own = await PreviewAsync(graph))
        using (var other = await PreviewAsync(graph, secondLocation))
        {
            DayIsOpen(own).Should().BeFalse("the location the holiday names is closed");
            DayIsOpen(other).Should().BeTrue("a holiday that names a location says nothing about the other branch");
        }

        await PostJsonAsync(Client(graph), "/api/v1/calendar/holidays",
            new { LocationId = (Guid?)null, Name = "National day", Date = graph.Day.AddDays(1), RecurringAnnually = false });

        using var tomorrowOwn = await PreviewAsync(graph, day: graph.Day.AddDays(1));
        using var tomorrowOther = await PreviewAsync(graph, secondLocation, graph.Day.AddDays(1));

        DayIsOpen(tomorrowOwn).Should().BeFalse("a row with no location is the tenant's own closure, so it applies at both");
        DayIsOpen(tomorrowOther).Should().BeFalse("and that is the whole point of the null scope");
    }

    [Fact]
    public async Task ADeletedHoliday_MustGiveTheDayBack()
    {
        var graph = await SeedGraphAsync("reopen");

        var created = await PostJsonAsync(Client(graph), "/api/v1/calendar/holidays",
            new { LocationId = (Guid?)null, Name = "Mistyped closure", Date = graph.Day, RecurringAnnually = false });
        var holidayId = created.RootElement.GetProperty("id").GetGuid();

        using (var closed = await PreviewAsync(graph))
        {
            DayIsOpen(closed).Should().BeFalse("the write closed it");
        }

        var delete = await Client(graph).DeleteAsync($"/api/v1/calendar/holidays/{holidayId}");
        delete.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var reopened = await PreviewAsync(graph);
        DayIsOpen(reopened).Should().BeTrue("and the delete is the undo a manager needs when the date was wrong");
        SlotCount(reopened).Should().BeGreaterThan(0);

        (await BookSlotAsync(graph)).Status.Should().Be(HttpStatusCode.Created,
            "the guard has to stop refusing, not just the preview stop showing — the two read the same row");

        (await CountAsync<Holiday>(h => h.TenantId == graph.TenantId)).Should().Be(0);
    }

    [Fact]
    public async Task AnAnnuallyRecurringHoliday_MustCloseItsMonthAndDayInALaterYear()
    {
        var graph = await SeedGraphAsync("annual");

        // The anchor is deliberately a date last year: the guard matches month and day
        // (AvailabilityService.cs:486), so an anchor the tenant recorded when the holiday was first declared is the
        // normal shape, and a validator that demanded a future anchor would refuse it.
        await PostJsonAsync(Client(graph), "/api/v1/calendar/holidays",
            new
            {
                LocationId = (Guid?)null,
                Name = "Independence Day",
                Date = graph.Day.AddYears(-1),
                RecurringAnnually = true
            });

        using var preview = await PreviewAsync(graph);
        DayIsOpen(preview).Should().BeFalse("a recurring row anchored in a past year still closes this year's date");
        SlotCount(preview).Should().Be(0);
    }

    [Fact]
    public async Task ADuplicateHolidayInTheSameScope_MustBeRefusedAndADifferentScope_MustNot()
    {
        var graph = await SeedGraphAsync("dupe");

        await PostJsonAsync(Client(graph), "/api/v1/calendar/holidays",
            new { LocationId = (Guid?)null, Name = "Closure", Date = graph.Day, RecurringAnnually = false });

        var second = await Client(graph).PostAsJsonAsync("/api/v1/calendar/holidays",
            new { LocationId = (Guid?)null, Name = "Closure again", Date = graph.Day, RecurringAnnually = false });
        var secondBody = await second.Content.ReadAsStringAsync();
        second.StatusCode.Should().Be((HttpStatusCode)422, secondBody);
        using (var refused = JsonDocument.Parse(secondBody))
        {
            Rule(refused).Should().Be("HolidayAlreadyOnThisDate");
        }

        // Same date, different scope: the tenant-wide row and one branch's row are two different statements, and the
        // guard reads both — so the second one is not a duplicate and must not be refused.
        await PostJsonAsync(Client(graph), "/api/v1/calendar/holidays",
            new { LocationId = graph.LocationId, Name = "Branch-only closure", Date = graph.Day, RecurringAnnually = false });

        (await CountAsync<Holiday>(h => h.TenantId == graph.TenantId)).Should().Be(2);
        (await CountAsync<Holiday>(h => h.TenantId == graph.TenantId && h.LocationId == null)).Should().Be(1);
    }

    [Fact]
    public async Task ADatedHolidayInThePast_MustBeRefusedBecauseItClosesNothing()
    {
        var graph = await SeedGraphAsync("past");

        var response = await Client(graph).PostAsJsonAsync("/api/v1/calendar/holidays",
            new { LocationId = (Guid?)null, Name = "Yesterday", Date = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)), RecurringAnnually = false });
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain("close nothing",
            "closedDates is built from the loaded window, so a dated row behind it is a row the guard never meets");

        (await CountAsync<Holiday>(h => h.TenantId == graph.TenantId)).Should().Be(0);

        // And the recurring half of the same rule is not refused — the annual fact above anchors in a past year on purpose.
        await PostJsonAsync(Client(graph), "/api/v1/calendar/holidays",
            new
            {
                LocationId = (Guid?)null,
                Name = "Annual, anchored in the past",
                Date = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)),
                RecurringAnnually = true
            });
    }

    // ---------------------------------------------------------------------------------------------
    // AvailabilityExceptions: the absence the clinic actually has to record.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnAbsenceBlock_MustTakeAwayTheSlotsItCovers_AndADeleteMustGiveThemBack()
    {
        var graph = await SeedGraphAsync("leave");

        using (var before = await PreviewAsync(graph))
        {
            SlotCount(before).Should().BeGreaterThan(1, "this fact reads a slot away, so the day has to hold more than one");
        }

        var created = await PostJsonAsync(Client(graph), "/api/v1/calendar/availability-exceptions",
            new
            {
                StaffId = graph.StaffId,
                StartAtUtc = graph.SlotStartUtc,
                EndAtUtc = graph.SlotStartUtc.AddHours(4),
                IsAvailable = false,
                Reason = "Sick leave"
            });

        created.RootElement.GetProperty("startAtUtc").GetString().Should().EndWith("Z",
            "TIME-01 applies to a new route the moment it answers an instant");
        created.RootElement.GetProperty("isAvailable").GetBoolean().Should().BeFalse();

        var (conflicted, conflictBody) = await BookSlotAsync(graph);
        conflicted.Should().Be(HttpStatusCode.Conflict, conflictBody);
        conflictBody.Should().Contain("not available",
            "the reason is the guard's StaffNotAvailable, and a refusal that says only 'conflict' is not actionable");

        using var after = await PreviewAsync(graph);
        var starts = after.RootElement.GetProperty("slots").EnumerateArray()
            .Select(s => DateTime.Parse(s.GetProperty("startAtUtc").GetString()!,
                CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind))
            .ToList();

        var blockedUntil = graph.SlotStartUtc.AddHours(4);
        starts.Should().NotBeEmpty("the day is still open, only the covered hours are gone");
        starts.Should().NotContain(s => s >= graph.SlotStartUtc && s < blockedUntil,
            "the preview must not offer a slot the guard then refuses for the same reason — one rule, two readers");
        starts.Should().Contain(blockedUntil,
            "and the absence takes the hours it names, not the rest of the shift with them");

        var exceptionId = created.RootElement.GetProperty("id").GetGuid();
        (await Client(graph).DeleteAsync($"/api/v1/calendar/availability-exceptions/{exceptionId}"))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await BookSlotAsync(graph)).Status.Should().Be(HttpStatusCode.Created,
            "an absence recorded in error has to be liftable without a database round");
        (await CountAsync<AvailabilityException>(e => e.TenantId == graph.TenantId)).Should().Be(0);
    }

    [Fact]
    public async Task ARequestForTheHalfTheGuardDoesNotRead_MustBeRefusedRatherThanStoredAsADeadRow()
    {
        var graph = await SeedGraphAsync("shift");

        var response = await Client(graph).PostAsJsonAsync("/api/v1/calendar/availability-exceptions",
            new
            {
                StaffId = graph.StaffId,
                StartAtUtc = graph.SlotStartUtc,
                EndAtUtc = graph.SlotStartUtc.AddHours(2),
                IsAvailable = true,
                Reason = "Extra shift"
            });
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain("no reader",
            "LoadCalendarAsync builds its blocked list from !IsAvailable rows only (AvailabilityService.cs:566-568) " +
            "and never reads a true row as a shift, so a 201 here would promise a slot nobody can book");

        (await CountAsync<AvailabilityException>(e => e.TenantId == graph.TenantId)).Should().Be(0,
            "and nothing is stored on the way to the refusal");
    }

    [Fact]
    public async Task AnAbsenceThatHasAlreadyEnded_MustBeRefused()
    {
        var graph = await SeedGraphAsync("ended");

        var response = await Client(graph).PostAsJsonAsync("/api/v1/calendar/availability-exceptions",
            new
            {
                StaffId = graph.StaffId,
                StartAtUtc = DateTime.UtcNow.AddDays(-3),
                EndAtUtc = DateTime.UtcNow.AddDays(-1),
                IsAvailable = false,
                Reason = "Last week"
            });
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be((HttpStatusCode)422, body);
        using var refused = JsonDocument.Parse(body);
        Rule(refused).Should().Be("ExceptionAlreadyPast");
    }

    // ---------------------------------------------------------------------------------------------
    // BusinessHours: the one table whose reader is conditional, so the fact has to be read in that condition.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task ABreakOnlyRoster_MustBeBookableBecauseOfTheOpeningHoursAndOnlyBecauseOfThem()
    {
        var graph = await SeedGraphAsync("opening", Roster.BreakOnly);

        using (var noWeek = await PreviewAsync(graph))
        {
            SlotCount(noWeek).Should().Be(0,
                "a roster row carrying only a break has no shift of its own, which is the guard's one condition for " +
                "reading the location's opening hours (AvailabilityService.cs:559-562)");

            // The asymmetry these routes do not remove: `isOpen: false` is produced by the holiday path and by the
            // not-bookable path (AvailabilityService.cs:105, :117), while a day that simply has no shift reaches the
            // return at :176. A calendar write changes the slots, never that label — which is the one thing a client
            // reading this API has to know, because "closed" and "nobody works today" share a body except here.
            DayIsOpen(noWeek).Should().BeTrue(
                "an empty day is not a closed day, and the caller has to be able to tell them apart");
        }

        var week = await Client(graph).PutAsJsonAsync("/api/v1/calendar/business-hours", new
        {
            LocationId = graph.LocationId,
            Days = new object[]
            {
                new { DayOfWeek = graph.Day.DayOfWeek.ToString(), OpenTime = "08:00:00", CloseTime = "18:00:00", IsClosed = false }
            }
        });
        var weekBody = await week.Content.ReadAsStringAsync();
        week.StatusCode.Should().Be(HttpStatusCode.OK, weekBody);

        using var withWeek = await PreviewAsync(graph);
        SlotCount(withWeek).Should().BeGreaterThan(0,
            "the opening hours are what make this day bookable, so writing them has to show up as slots");

        // And the same row, closed, takes the day back — isClosed:true is the shape the reader filters out.
        var closed = await Client(graph).PutAsJsonAsync("/api/v1/calendar/business-hours", new
        {
            LocationId = graph.LocationId,
            Days = new object[]
            {
                new { DayOfWeek = graph.Day.DayOfWeek.ToString(), OpenTime = "08:00:00", CloseTime = "18:00:00", IsClosed = true }
            }
        });
        var closedBody = await closed.Content.ReadAsStringAsync();
        closed.StatusCode.Should().Be(HttpStatusCode.OK, closedBody);

        using var afterClosing = await PreviewAsync(graph);
        SlotCount(afterClosing).Should().Be(0,
            "the guard's business-hours read drops IsClosed rows, so a closed day has to stop feeding the fallback");
        DayIsOpen(afterClosing).Should().BeTrue(
            "a closed row in BusinessHours empties the day the way an absent shift does; this table is a fallback for " +
            "a roster with no shift, not a second way to say the clinic is shut — that answer belongs to a holiday");
    }

    [Fact]
    public async Task AWeekWrite_MustLeaveTheDaysItDidNotNameAlone()
    {
        var graph = await SeedGraphAsync("week");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.BusinessHours.Add(new BusinessHour
        {
            Id = Guid.NewGuid(),
            TenantId = graph.TenantId,
            LocationId = graph.LocationId,
            DayOfWeek = DayOfWeek.Saturday,
            OpenTime = new TimeSpan(9, 0, 0),
            CloseTime = new TimeSpan(13, 0, 0),
            IsClosed = false
        });
        await db.SaveChangesAsync();

        var response = await Client(graph).PutAsJsonAsync("/api/v1/calendar/business-hours", new
        {
            LocationId = graph.LocationId,
            Days = new object[]
            {
                new { DayOfWeek = "Monday", OpenTime = "09:00:00", CloseTime = "17:00:00", IsClosed = false }
            }
        });
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);

        using var week = JsonDocument.Parse(body);
        var days = week.RootElement.GetProperty("days").EnumerateArray()
            .Select(d => d.GetProperty("dayOfWeek").GetString()).ToList();

        days.Should().Contain("Monday", "the write named it");
        days.Should().Contain("Saturday",
            "a PUT that replaces the whole week would turn a client that sends Monday to Friday into a clinic that " +
            "closed its Saturday without being asked to");

        week.RootElement.GetProperty("timeZoneId").GetString().Should().Be("UTC");
    }

    [Fact]
    public async Task AClosingTimeThatWrapsPastMidnight_MustBeRefusedNotFoldedToZero()
    {
        var graph = await SeedGraphAsync("midnight");

        var response = await Client(graph).PutAsJsonAsync("/api/v1/calendar/business-hours", new
        {
            LocationId = graph.LocationId,
            Days = new object[]
            {
                // Written the way TimeSpan's own wire format spells twenty-four hours, because "24:00:00" is not a
                // TimeSpan the binder accepts — the 400 that would answer is about JSON, not about the calendar rule.
                new { DayOfWeek = "Monday", OpenTime = "09:00:00", CloseTime = "1.00:00:00", IsClosed = false }
            }
        });
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain("inside one day",
            "the guard turns this TimeSpan into a TimeOnly, which folds 24:00 back to 00:00 and would delete the " +
            "window the caller wrote without saying so");

        (await CountAsync<BusinessHour>(b => b.TenantId == graph.TenantId)).Should().Be(0);
    }

    // ---------------------------------------------------------------------------------------------
    // Who may write a calendar, and what a list route answers.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task ACallerWithoutSchedulesManage_MustNotWriteTheCalendarAndAnAnonymousCaller_MustNotReadIt()
    {
        var graph = await SeedGraphAsync("gate");

        // Receptionist and Staff hold schedules.read, not schedules.manage — they can look at the calendar and
        // cannot close a day.
        var receptionist = await Client(graph, Roles.Receptionist)
            .PostAsJsonAsync("/api/v1/calendar/holidays",
                new { LocationId = (Guid?)null, Name = "Not mine to declare", Date = graph.Day, RecurringAnnually = false });
        receptionist.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var accountant = await Client(graph, Roles.Accountant)
            .PutAsJsonAsync("/api/v1/calendar/business-hours", new
            {
                LocationId = graph.LocationId,
                Days = new object[] { new { DayOfWeek = "Monday", OpenTime = "09:00:00", CloseTime = "17:00:00", IsClosed = false } }
            });
        accountant.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        (await CountAsync<Holiday>(h => h.TenantId == graph.TenantId)).Should().Be(0);
        (await CountAsync<BusinessHour>(b => b.TenantId == graph.TenantId)).Should().Be(0);

        using var read = await ReadAsync(Client(graph, Roles.Receptionist), "/api/v1/calendar/holidays");
        read.RootElement.GetProperty("total").GetInt32().Should().Be(0, "and the read they do hold answers normally");

        var anonymous = await _factory.CreateClient().GetAsync("/api/v1/calendar/holidays");
        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "this is the clinic's own calendar, not the public catalog — it has no anonymous caller to serve");
    }

    [Fact]
    public async Task AHolidayOfAnotherTenant_MustBeNotFoundRatherThanForbidden()
    {
        var owner = await SeedGraphAsync("owner");
        var other = await SeedGraphAsync("other");

        var created = await PostJsonAsync(Client(owner), "/api/v1/calendar/holidays",
            new { LocationId = (Guid?)null, Name = "Owner closure", Date = owner.Day, RecurringAnnually = false });
        var holidayId = created.RootElement.GetProperty("id").GetGuid();

        var stolen = await Client(other).DeleteAsync($"/api/v1/calendar/holidays/{holidayId}");
        stolen.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "an id from another tenant is not a row this caller is allowed to learn the existence of");

        using var stillClosed = await PreviewAsync(owner);
        DayIsOpen(stillClosed).Should().BeFalse("and the refusal really left the other tenant's day closed");

        var otherGraph = other;
        var list = await ReadAsync(Client(otherGraph), "/api/v1/calendar/holidays");
        list.RootElement.GetProperty("total").GetInt32().Should().Be(0, "and the list reads one tenant's calendar");
    }

    [Fact]
    public async Task TheCalendarLists_MustAnswerAPageAndSurviveAnIllegalAsk()
    {
        var graph = await SeedGraphAsync("paging");

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var start = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1);
            for (var i = 0; i < 120; i++)
            {
                db.Holidays.Add(new Holiday
                {
                    Id = Guid.NewGuid(),
                    TenantId = graph.TenantId,
                    Name = $"Closure {i}",
                    Date = start.AddDays(i),
                    RecurringAnnually = false
                });
            }

            var from = DateTime.UtcNow.Date.AddDays(400);
            for (var i = 0; i < 40; i++)
            {
                db.AvailabilityExceptions.Add(new AvailabilityException
                {
                    Id = Guid.NewGuid(),
                    TenantId = graph.TenantId,
                    StaffId = graph.StaffId,
                    StartDateTimeUtc = from.AddHours(i),
                    EndDateTimeUtc = from.AddHours(i).AddMinutes(30),
                    IsAvailable = false,
                    Reason = "Training"
                });
            }

            // Rows a hand-written calendar can still leave behind, and the only way to see whether the list's default
            // window does anything: the route itself refuses to write them.
            for (var i = 0; i < 5; i++)
            {
                db.AvailabilityExceptions.Add(new AvailabilityException
                {
                    Id = Guid.NewGuid(),
                    TenantId = graph.TenantId,
                    StaffId = graph.StaffId,
                    StartDateTimeUtc = DateTime.UtcNow.AddDays(-(i + 2)),
                    EndDateTimeUtc = DateTime.UtcNow.AddDays(-(i + 1)),
                    IsAvailable = false,
                    Reason = "Long ago"
                });
            }

            await db.SaveChangesAsync();
        }

        using var http = Client(graph);

        var first = await ReadAsync(http, "/api/v1/calendar/holidays");
        first.RootElement.GetProperty("total").GetInt32().Should().Be(120);
        first.RootElement.GetProperty("items").GetArrayLength().Should().Be(20, "the default page size, not the whole table");

        // The same illegal asks PAG-01 and PERF-04 closed on every other list: they must not be a 500 or a
        // whole-table answer here either. A page of 0 or a negative size is not a request, so it becomes page 1 of 20;
        // a page past the end of the book IS a request, so it comes back as that page and an empty items array.
        foreach (var url in new[]
                 {
                     "/api/v1/calendar/holidays?page=0",
                     "/api/v1/calendar/holidays?pageSize=-5",
                     "/api/v1/calendar/holidays?pageSize=100000",
                     "/api/v1/calendar/holidays?page=0&pageSize=0",
                     "/api/v1/calendar/availability-exceptions?page=0&pageSize=0"
                 })
        {
            using var asked = await ReadAsync(http, url);
            asked.RootElement.GetProperty("page").GetInt32().Should().Be(1, $"{url} is not a page number this API reads");
            asked.RootElement.GetProperty("pageSize").GetInt32().Should().Be(20, $"{url} falls back to the default size");
            asked.RootElement.GetProperty("items").GetArrayLength().Should().BeLessThanOrEqualTo(100);
        }

        foreach (var url in new[]
                 {
                     "/api/v1/calendar/holidays?page=2147483647",
                     "/api/v1/calendar/availability-exceptions?page=2147483647"
                 })
        {
            using var farPage = await ReadAsync(http, url);
            farPage.RootElement.GetProperty("page").GetInt32().Should().Be(int.MaxValue,
                $"{url} is a real page number, and the offset it asks for is capped rather than wrapped");
            farPage.RootElement.GetProperty("items").GetArrayLength().Should().Be(0,
                "a page past the end of the book is an empty page, not a server fault");
        }

        var pastTheEnd = await ReadAsync(http, "/api/v1/calendar/holidays?page=999");
        pastTheEnd.RootElement.GetProperty("total").GetInt32().Should().Be(120);
        pastTheEnd.RootElement.GetProperty("items").GetArrayLength().Should().Be(0, "a page past the end is empty, not a fault");

        // The exceptions list defaults to what has not ended yet, because this API refuses to write an absence that
        // already did — a default that opened on the past would page through rows that block nothing.
        using var upcoming = await ReadAsync(http, "/api/v1/calendar/availability-exceptions?pageSize=100");
        upcoming.RootElement.GetProperty("total").GetInt32().Should().Be(40);

        using var since = await ReadAsync(http,
            "/api/v1/calendar/availability-exceptions?fromUtc=2000-01-01T00:00:00Z&pageSize=100");
        since.RootElement.GetProperty("total").GetInt32().Should().Be(45,
            "45 rows are in the table and the default answered 40, so it is the window dropping the five that already " +
            "ended — not the page size, and not a filter that was never applied");

        var back = DateTime.UtcNow.Date.AddDays(400);
        using var windowed = await ReadAsync(http,
            $"/api/v1/calendar/availability-exceptions?fromUtc={Uri.EscapeDataString(back.ToString("yyyy-MM-ddTHH:mm:ss"))}Z" +
            $"&toUtc={Uri.EscapeDataString(back.ToString("yyyy-MM-ddTHH:mm:ss"))}Z&page=2&pageSize=1");
        windowed.RootElement.GetProperty("total").GetInt32().Should().Be(1, "one row starts inside that second");
        windowed.RootElement.GetProperty("items").GetArrayLength().Should().Be(0, "and it is the first page's row");
    }

    [Fact]
    public async Task AWindowedHolidayList_MustIncludeTheRecurringRowThatCouldFallInsideIt()
    {
        var graph = await SeedGraphAsync("window");

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.Holidays.Add(new Holiday
            {
                Id = Guid.NewGuid(),
                TenantId = graph.TenantId,
                Name = "Dated, inside the window",
                Date = graph.Day,
                RecurringAnnually = false
            });
            db.Holidays.Add(new Holiday
            {
                Id = Guid.NewGuid(),
                TenantId = graph.TenantId,
                Name = "Annual, anchored two years back",
                Date = graph.Day.AddYears(-2),
                RecurringAnnually = true
            });
            await db.SaveChangesAsync();
        }

        // A window that does not contain the anchor's date still has to return the annual row: it is the row that
        // closes a day in that window, and the guard's own predicate is what decides that.
        using var list = await ReadAsync(Client(graph),
            $"/api/v1/calendar/holidays?from={graph.Day:yyyy-MM-dd}&to={graph.Day:yyyy-MM-dd}");

        var names = list.RootElement.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("name").GetString()).ToList();

        list.RootElement.GetProperty("total").GetInt32().Should().Be(2,
            "a list that filtered recurring rows by their anchor year would hide the closure the manager is asking about");
        names.Should().Contain("Annual, anchored two years back");
    }

    [Fact]
    public async Task AnIdempotentReplay_MustHandBackTheFirstHolidayInsteadOfADuplicateRefusal()
    {
        var graph = await SeedGraphAsync("idem");
        var key = Guid.NewGuid().ToString("N");

        var body = JsonSerializer.Serialize(new
        {
            LocationId = (Guid?)null,
            Name = "Declared once",
            Date = graph.Day,
            RecurringAnnually = false
        });

        using var first = new HttpRequestMessage(HttpMethod.Post, "/api/v1/calendar/holidays")
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
        };
        first.Headers.Add("Idempotency-Key", key);

        using var created = await Client(graph).SendAsync(first);
        var createdBody = await created.Content.ReadAsStringAsync();
        created.StatusCode.Should().Be(HttpStatusCode.Created, createdBody);
        created.Headers.Contains("X-Idempotent-Replay").Should().BeFalse("the first call is not a replay");

        using var retry = new HttpRequestMessage(HttpMethod.Post, "/api/v1/calendar/holidays")
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
        };
        retry.Headers.Add("Idempotency-Key", key);

        using var replayed = await Client(graph).SendAsync(retry);
        var replayBody = await replayed.Content.ReadAsStringAsync();
        replayed.StatusCode.Should().Be(HttpStatusCode.Created, replayBody);

        replayed.Headers.TryGetValues("X-Idempotent-Replay", out var markers).Should().BeTrue(
            "the middleware owns the key for every state-changing verb, so a calendar POST gets it without the route asking");
        markers!.Single().Should().Be("true");

        using (var handBack = JsonDocument.Parse(replayBody))
        {
            handBack.RootElement.GetProperty("id").GetGuid().Should().Be(
                Guid.Parse(JsonDocument.Parse(createdBody).RootElement.GetProperty("id").GetString()!),
                "a retry has to return the row it already declared, not a second answer");
        }

        (await CountAsync<Holiday>(h => h.TenantId == graph.TenantId)).Should().Be(1,
            "and the duplicate rule never runs, which is the point of the key");
    }
}
