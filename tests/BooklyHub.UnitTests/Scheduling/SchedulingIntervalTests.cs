using BooklyHub.Application.Common.Helpers;
using BooklyHub.Application.Scheduling;
using FluentAssertions;
using Xunit;

namespace BooklyHub.UnitTests.Scheduling;

public class SchedulingIntervalTests
{
    [Fact]
    public void Subtract_NoOverlap_ShouldReturnOriginalInterval()
    {
        var baseInterval = new TimeInterval(
            new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 10, 1, 17, 0, 0, DateTimeKind.Utc));

        var outside = new TimeInterval(
            new DateTime(2026, 10, 1, 18, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 10, 1, 19, 0, 0, DateTimeKind.Utc));

        var result = baseInterval.Subtract(outside);

        result.Should().HaveCount(1);
        result[0].StartUtc.Should().Be(baseInterval.StartUtc);
        result[0].EndUtc.Should().Be(baseInterval.EndUtc);
    }

    [Fact]
    public void Subtract_CompleteCoverage_ShouldReturnEmpty()
    {
        var baseInterval = new TimeInterval(
            new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc));

        var covering = new TimeInterval(
            new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 10, 1, 13, 0, 0, DateTimeKind.Utc));

        var result = baseInterval.Subtract(covering);

        result.Should().BeEmpty();
    }

    [Fact]
    public void Subtract_MiddleBreak_ShouldSplitIntoTwoIntervals()
    {
        // Working 09:00 - 17:00, Lunch break 12:00 - 13:00
        var shift = new TimeInterval(
            new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 10, 1, 17, 0, 0, DateTimeKind.Utc));

        var lunchBreak = new TimeInterval(
            new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 10, 1, 13, 0, 0, DateTimeKind.Utc));

        var result = shift.Subtract(lunchBreak);

        result.Should().HaveCount(2);

        // Morning interval: 09:00 - 12:00
        result[0].StartUtc.Should().Be(shift.StartUtc);
        result[0].EndUtc.Should().Be(lunchBreak.StartUtc);

        // Afternoon interval: 13:00 - 17:00
        result[1].StartUtc.Should().Be(lunchBreak.EndUtc);
        result[1].EndUtc.Should().Be(shift.EndUtc);
    }

    [Fact]
    public void SubtractMany_MultipleBreaksAndAppointments_ShouldLeaveCorrectFreeWindows()
    {
        var shift = new TimeInterval(
            new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 10, 1, 17, 0, 0, DateTimeKind.Utc));

        var blocked = new[]
        {
            new TimeInterval(new DateTime(2026, 10, 1, 10, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 1, 11, 0, 0, DateTimeKind.Utc)), // Appointment 1
            new TimeInterval(new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 1, 13, 0, 0, DateTimeKind.Utc)), // Lunch break
            new TimeInterval(new DateTime(2026, 10, 1, 14, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 1, 15, 30, 0, DateTimeKind.Utc))  // Appointment 2
        };

        var available = TimeInterval.SubtractMany([shift], blocked);

        available.Should().HaveCount(4);
        available[0].StartUtc.Should().Be(new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc));
        available[0].EndUtc.Should().Be(new DateTime(2026, 10, 1, 10, 0, 0, DateTimeKind.Utc));

        available[1].StartUtc.Should().Be(new DateTime(2026, 10, 1, 11, 0, 0, DateTimeKind.Utc));
        available[1].EndUtc.Should().Be(new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc));

        available[2].StartUtc.Should().Be(new DateTime(2026, 10, 1, 13, 0, 0, DateTimeKind.Utc));
        available[2].EndUtc.Should().Be(new DateTime(2026, 10, 1, 14, 0, 0, DateTimeKind.Utc));

        available[3].StartUtc.Should().Be(new DateTime(2026, 10, 1, 15, 30, 0, DateTimeKind.Utc));
        available[3].EndUtc.Should().Be(new DateTime(2026, 10, 1, 17, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void TimeZoneHelper_DSTConversion_ShouldBeDeterministic()
    {
        // US Eastern Standard Time
        var tz = TimeZoneHelper.ResolveTimeZone("America/New_York");
        tz.Should().NotBeNull();

        // Summer time (EDT, UTC-4): July 1st, 2026 10:00 AM local
        var summerLocal = new DateTime(2026, 7, 1, 10, 0, 0);
        var summerUtc = TimeZoneHelper.ToUtc(summerLocal, tz);
        summerUtc.Hour.Should().Be(14); // 10:00 + 4 = 14:00 UTC

        // Winter time (EST, UTC-5): Jan 15th, 2026 10:00 AM local
        var winterLocal = new DateTime(2026, 1, 15, 10, 0, 0);
        var winterUtc = TimeZoneHelper.ToUtc(winterLocal, tz);
        winterUtc.Hour.Should().Be(15); // 10:00 + 5 = 15:00 UTC
    }
}
