using BooklyHub.Application.Appointments.Commands;
using BooklyHub.Domain.Enums;
using FluentAssertions;
using Xunit;

namespace BooklyHub.UnitTests.Appointments;

public class RecurringAppointmentTests
{
    [Fact]
    public void GenerateDates_Weekly_ShouldGenerateWeeklyCadence()
    {
        var start = new DateOnly(2026, 10, 1); // Thursday
        var dates = CreateRecurringAppointmentCommandHandler.GenerateDates(
            RecurrencePattern.Weekly,
            interval: 1,
            startDate: start,
            endDate: null,
            maxOccurrences: 4);

        dates.Should().HaveCount(4);
        dates[0].Should().Be(new DateOnly(2026, 10, 1));
        dates[1].Should().Be(new DateOnly(2026, 10, 8));
        dates[2].Should().Be(new DateOnly(2026, 10, 15));
        dates[3].Should().Be(new DateOnly(2026, 10, 22));
    }

    [Fact]
    public void GenerateDates_Biweekly_ShouldGenerateEveryTwoWeeks()
    {
        var start = new DateOnly(2026, 10, 1);
        var dates = CreateRecurringAppointmentCommandHandler.GenerateDates(
            RecurrencePattern.Biweekly,
            interval: 1,
            startDate: start,
            endDate: null,
            maxOccurrences: 3);

        dates.Should().HaveCount(3);
        dates[0].Should().Be(new DateOnly(2026, 10, 1));
        dates[1].Should().Be(new DateOnly(2026, 10, 15));
        dates[2].Should().Be(new DateOnly(2026, 10, 29));
    }

    [Fact]
    public void GenerateDates_Monthly_ShouldGenerateMonthlyCadence()
    {
        var start = new DateOnly(2026, 1, 15);
        var dates = CreateRecurringAppointmentCommandHandler.GenerateDates(
            RecurrencePattern.Monthly,
            interval: 1,
            startDate: start,
            endDate: null,
            maxOccurrences: 3);

        dates.Should().HaveCount(3);
        dates[0].Should().Be(new DateOnly(2026, 1, 15));
        dates[1].Should().Be(new DateOnly(2026, 2, 15));
        dates[2].Should().Be(new DateOnly(2026, 3, 15));
    }

    [Fact]
    public void GenerateDates_WithEndDateCutoff_ShouldStopAtEndDate()
    {
        var start = new DateOnly(2026, 10, 1);
        var end = new DateOnly(2026, 10, 16);

        var dates = CreateRecurringAppointmentCommandHandler.GenerateDates(
            RecurrencePattern.Weekly,
            interval: 1,
            startDate: start,
            endDate: end,
            maxOccurrences: 10);

        dates.Should().HaveCount(3); // Oct 1, Oct 8, Oct 15
        dates.Last().Should().Be(new DateOnly(2026, 10, 15));
    }
}
