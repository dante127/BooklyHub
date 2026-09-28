using BooklyHub.Application.Appointments.Commands;
using BooklyHub.Domain.Enums;
using BooklyHub.Domain.Exceptions;
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

    [Fact]
    public void GenerateDates_WithOnlyEndDate_ShouldRunAllTheWayToTheEndDate()
    {
        var start = new DateOnly(2026, 10, 1);

        var dates = CreateRecurringAppointmentCommandHandler.GenerateDates(
            RecurrencePattern.Weekly,
            interval: 1,
            startDate: start,
            endDate: new DateOnly(2026, 12, 31),
            maxOccurrences: null);

        // Oct 1 through Dec 31 inclusive is 14 weekly occurrences. The cap used to stop at a default of
        // 12 and report that shorter list as what had been requested.
        dates.Should().HaveCount(14);
        dates.Last().Should().Be(new DateOnly(2026, 12, 31));
    }

    [Fact]
    public void GenerateDates_ShouldNotClampMaxOccurrencesAboveTheOldLimitOf52()
    {
        var start = new DateOnly(2026, 10, 1);

        var dates = CreateRecurringAppointmentCommandHandler.GenerateDates(
            RecurrencePattern.Weekly,
            interval: 1,
            startDate: start,
            endDate: null,
            maxOccurrences: 60);

        dates.Should().HaveCount(60);
        dates.Last().Should().Be(new DateOnly(2026, 10, 1).AddDays(7 * 59));
    }

    [Fact]
    public void GenerateDates_MonthlyWithInterval_ShouldSpaceByWholeIntervals()
    {
        var dates = CreateRecurringAppointmentCommandHandler.GenerateDates(
            RecurrencePattern.Monthly,
            interval: 3,
            startDate: new DateOnly(2026, 1, 15),
            endDate: null,
            maxOccurrences: 4);

        dates.Should().Equal(
            new DateOnly(2026, 1, 15),
            new DateOnly(2026, 4, 15),
            new DateOnly(2026, 7, 15),
            new DateOnly(2026, 10, 15));
    }

    [Fact]
    public void GenerateDates_WithoutAnyBound_ShouldRequireOne()
    {
        var act = () => CreateRecurringAppointmentCommandHandler.GenerateDates(
            RecurrencePattern.Weekly,
            interval: 1,
            startDate: new DateOnly(2026, 10, 1),
            endDate: null,
            maxOccurrences: null);

        act.Should().Throw<BusinessRuleValidationException>()
            .Which.RuleName.Should().Be("RecurrenceRangeRequired");
    }

    [Fact]
    public void GenerateDates_WithAnUndefinedPattern_ShouldRefuseInsteadOfRepeatingWeekly()
    {
        var act = () => CreateRecurringAppointmentCommandHandler.GenerateDates(
            (RecurrencePattern)99,
            interval: 1,
            startDate: new DateOnly(2026, 10, 1),
            endDate: null,
            maxOccurrences: 4);

        act.Should().Throw<BusinessRuleValidationException>()
            .Which.RuleName.Should().Be("RecurrencePatternUnsupported");
    }

    [Fact]
    public void GenerateDates_WithADailyRangePastTheCeiling_ShouldRefuseInsteadOfTruncating()
    {
        var act = () => CreateRecurringAppointmentCommandHandler.GenerateDates(
            RecurrencePattern.Daily,
            interval: 1,
            startDate: new DateOnly(2026, 10, 1),
            endDate: new DateOnly(2029, 10, 1),
            maxOccurrences: null);

        act.Should().Throw<BusinessRuleValidationException>()
            .Which.RuleName.Should().Be("RecurrenceRangeTooLarge");
    }

    [Fact]
    public void GenerateDates_WithMaxOccurrencesAboveTheCeiling_ShouldRefuse()
    {
        var act = () => CreateRecurringAppointmentCommandHandler.GenerateDates(
            RecurrencePattern.Weekly,
            interval: 1,
            startDate: new DateOnly(2026, 10, 1),
            endDate: null,
            maxOccurrences: CreateRecurringAppointmentCommandHandler.MaxOccurrencesCeiling + 1);

        act.Should().Throw<BusinessRuleValidationException>()
            .Which.RuleName.Should().Be("RecurrenceRangeTooLarge");
    }

    [Fact]
    public void GenerateDates_WithAnEmptyOrReversedRange_ShouldRefuse()
    {
        var start = new DateOnly(2026, 10, 1);

        foreach (var (endDate, maxOccurrences) in new (DateOnly?, int?)[]
        {
            (new DateOnly(2026, 9, 30), null),
            (null, 0),
            (null, -3)
        })
        {
            var act = () => CreateRecurringAppointmentCommandHandler.GenerateDates(
                RecurrencePattern.Weekly, interval: 1, startDate: start, endDate: endDate, maxOccurrences: maxOccurrences);

            act.Should().Throw<BusinessRuleValidationException>()
                .Which.RuleName.Should().Be("RecurrenceRangeInvalid");
        }
    }

    [Fact]
    public void GenerateDates_WithAnIntervalOutsideTheAllowedRange_ShouldRefuse()
    {
        foreach (var interval in new[] { 0, -1, CreateRecurringAppointmentCommandHandler.MaxInterval + 1 })
        {
            var act = () => CreateRecurringAppointmentCommandHandler.GenerateDates(
                RecurrencePattern.Monthly,
                interval: interval,
                startDate: new DateOnly(2026, 10, 1),
                endDate: null,
                maxOccurrences: 3);

            act.Should().Throw<BusinessRuleValidationException>()
                .Which.RuleName.Should().Be("RecurrenceRangeInvalid");
        }
    }
}
