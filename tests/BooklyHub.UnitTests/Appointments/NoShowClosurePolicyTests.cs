using BooklyHub.Domain.Entities.Appointments;
using BooklyHub.Domain.Enums;
using FluentAssertions;
using Xunit;

namespace BooklyHub.UnitTests.Appointments;

/// <summary>
/// The sweep decides with these numbers alone, so every bound here is a decision that has to break loudly
/// when the constant moves: what counts as absent, how long the clinic waits, and how far back it reaches.
/// </summary>
public class NoShowClosurePolicyTests
{
    private static readonly DateTime Now = new(2026, 3, 5, 14, 0, 0, DateTimeKind.Utc);

    // now - 6h: the visit window has to be closed before the absence is evidence of anything.
    private static readonly DateTime Deadline = new(2026, 3, 5, 8, 0, 0, DateTimeKind.Utc);

    // deadline - 14d: how far one sweep may reach back.
    private static readonly DateTime Earliest = new(2026, 2, 19, 8, 0, 0, DateTimeKind.Utc);

    private static bool Due(AppointmentStatus status, DateTime endAtUtc) =>
        NoShowClosurePolicy.IsUnattendedAndPastItsWindow(status, endAtUtc, Now);

    [Fact]
    public void TheBounds_MustBeTheDocumentedGraceAndLookback()
    {
        NoShowClosurePolicy.GraceHours.Should().Be(6);
        NoShowClosurePolicy.LookbackDays.Should().Be(14);

        NoShowClosurePolicy.DeadlineUtc(Now).Should().Be(Deadline);
        NoShowClosurePolicy.EarliestEndUtc(Now).Should().Be(Earliest);
    }

    [Theory]
    [InlineData(AppointmentStatus.Pending)]
    [InlineData(AppointmentStatus.Confirmed)]
    [InlineData(AppointmentStatus.CheckedIn)]
    [InlineData(AppointmentStatus.InProgress)]
    [InlineData(AppointmentStatus.Completed)]
    [InlineData(AppointmentStatus.Cancelled)]
    [InlineData(AppointmentStatus.NoShow)]
    [InlineData(AppointmentStatus.Rescheduled)]
    public void OnlyAConfirmedBooking_IsAnAbsenceTheSweepMayClose(AppointmentStatus status)
    {
        var due = Due(status, Deadline.AddMinutes(-1));

        if (status == AppointmentStatus.Confirmed)
        {
            due.Should().BeTrue("the visit window has closed and the customer was never recorded as present");
        }
        else
        {
            due.Should().BeFalse($"{status} is not this sweep's evidence of a no-show");
        }
    }

    [Fact]
    public void TheGraceBound_MustSitOnTheEndOfTheVisitNotItsStart()
    {
        // A booking ending at the deadline is already past the window; a second later it is not.
        Due(AppointmentStatus.Confirmed, Deadline).Should().BeTrue();
        Due(AppointmentStatus.Confirmed, Deadline.AddSeconds(1)).Should().BeFalse(
            "grace is what a late arrival still gets; closing inside it would blame a customer for being on time");
    }

    [Fact]
    public void TheLookbackBound_MustStopTheFirstRunFromRestatingHistory()
    {
        Due(AppointmentStatus.Confirmed, Earliest).Should().BeTrue();
        Due(AppointmentStatus.Confirmed, Earliest.AddSeconds(-1)).Should().BeFalse(
            "anything older than the lookback is a deliberate backfill, not a housekeeping tick");
    }

    [Fact]
    public void EveryStatusThatIsNotClosingTheVisit_MustBeTheOneTheDashboardReportsAsStale()
    {
        // The sweep refuses these, so the dashboard counter is the only thing that surfaces them. If this
        // set and the sweep ever drift, a stuck visit becomes invisible in both places.
        AppointmentStatusSet.AwaitingOutcome.Should().BeEquivalentTo(
            new[] { AppointmentStatus.CheckedIn, AppointmentStatus.InProgress });

        AppointmentStatusSet.AwaitingOutcome
            .Select(status => Due(status, Deadline.AddHours(-1)))
            .Should().AllBeEquivalentTo(false);
    }
}
