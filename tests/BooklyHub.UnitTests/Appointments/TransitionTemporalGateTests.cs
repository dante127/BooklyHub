using BooklyHub.Domain.Entities.Appointments;
using BooklyHub.Domain.Enums;
using BooklyHub.Domain.Exceptions;
using FluentAssertions;
using Xunit;

namespace BooklyHub.UnitTests.Appointments;

/// <summary>
/// The execution statuses are gated on time, and a gate is only a rule if its instant can be stood on.
/// Every appointment here starts on a fixed date and the clock is handed to TransitionTo, so each pair of
/// tests below puts one call one second before the boundary and the other on it.
/// </summary>
public class TransitionTemporalGateTests
{
    private static readonly DateTime Start = new(2026, 3, 5, 14, 0, 0, DateTimeKind.Utc);

    private static Appointment AppointmentStartingAt(DateTime startAtUtc) => Appointment.Create(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        startAtUtc, startAtUtc.AddMinutes(45), 45, 75.00m);

    private static Appointment ConfirmedAt(DateTime nowUtc)
    {
        var appt = AppointmentStartingAt(Start);
        appt.TransitionTo(AppointmentStatus.Confirmed, nowUtc);
        return appt;
    }

    [Fact]
    public void CheckedIn_OnTheOneHourBoundary_MustBeAllowed()
    {
        var appt = ConfirmedAt(Start);

        appt.TransitionTo(AppointmentStatus.CheckedIn, Start - TimeSpan.FromHours(1));

        appt.Status.Should().Be(AppointmentStatus.CheckedIn);
    }

    [Fact]
    public void CheckedIn_OneSecondBeforeTheOneHourBoundary_MustBeRefused()
    {
        var appt = ConfirmedAt(Start);

        var act = () => appt.TransitionTo(AppointmentStatus.CheckedIn, Start - TimeSpan.FromHours(1) - TimeSpan.FromSeconds(1));

        act.Should().Throw<BusinessRuleValidationException>().Where(e => e.RuleName == "TransitionTooEarly");
    }

    [Fact]
    public void InProgress_OnTheFifteenMinuteBoundary_MustBeAllowed()
    {
        var appt = ConfirmedAt(Start);
        appt.TransitionTo(AppointmentStatus.CheckedIn, Start - TimeSpan.FromHours(1));

        appt.TransitionTo(AppointmentStatus.InProgress, Start - TimeSpan.FromMinutes(15));

        appt.Status.Should().Be(AppointmentStatus.InProgress);
    }

    [Fact]
    public void InProgress_OneSecondBeforeTheFifteenMinuteBoundary_MustBeRefused()
    {
        var appt = ConfirmedAt(Start);
        appt.TransitionTo(AppointmentStatus.CheckedIn, Start - TimeSpan.FromHours(1));

        var act = () => appt.TransitionTo(AppointmentStatus.InProgress, Start - TimeSpan.FromMinutes(15) - TimeSpan.FromSeconds(1));

        act.Should().Throw<BusinessRuleValidationException>().Where(e => e.RuleName == "TransitionTooEarly");
    }

    [Theory]
    [InlineData(AppointmentStatus.Completed)]
    [InlineData(AppointmentStatus.NoShow)]
    public void AnEndStatus_OnTheStartInstant_MustBeAllowed(AppointmentStatus status)
    {
        var appt = ConfirmedAt(Start);

        appt.TransitionTo(status, Start);

        appt.Status.Should().Be(status);
    }

    [Theory]
    [InlineData(AppointmentStatus.Completed)]
    [InlineData(AppointmentStatus.NoShow)]
    public void AnEndStatus_OneSecondBeforeStart_MustBeRefused(AppointmentStatus status)
    {
        var appt = ConfirmedAt(Start);

        var act = () => appt.TransitionTo(status, Start - TimeSpan.FromSeconds(1));

        act.Should().Throw<BusinessRuleValidationException>().Where(e => e.RuleName == "TransitionTooEarly");
    }

    [Fact]
    public void TheGate_MustAnswerToTheSuppliedClockNotToTheSystem()
    {
        // The appointment is three hours in the future, so the wall clock would refuse this. The supplied
        // instant says the time has come, and the supplied instant is what the rule reads.
        var appt = AppointmentStartingAt(DateTime.UtcNow.AddHours(3));
        var atStart = appt.StartAtUtc;
        appt.TransitionTo(AppointmentStatus.Confirmed, DateTime.UtcNow);

        appt.TransitionTo(AppointmentStatus.Completed, atStart);

        appt.Status.Should().Be(AppointmentStatus.Completed);
    }

    [Fact]
    public void TheGate_MustRefuseEvenWhenTheSystemClockHasPassedTheStart()
    {
        // The mirror of the test above: this appointment started an hour ago in wall-clock terms, so the
        // old rule would have allowed it. The supplied instant is before the start, and that is the answer.
        var appt = AppointmentStartingAt(DateTime.UtcNow.AddHours(-1));
        appt.TransitionTo(AppointmentStatus.Confirmed, DateTime.UtcNow);

        var act = () => appt.TransitionTo(AppointmentStatus.Completed, appt.StartAtUtc - TimeSpan.FromMinutes(30));

        act.Should().Throw<BusinessRuleValidationException>().Where(e => e.RuleName == "TransitionTooEarly");
    }

    [Theory]
    [InlineData(AppointmentStatus.Confirmed)]
    [InlineData(AppointmentStatus.Cancelled)]
    public void AStatusThatDescribesNoEvent_MustNotBeGated(AppointmentStatus status)
    {
        var appt = AppointmentStartingAt(DateTime.UtcNow.AddDays(30));

        appt.TransitionTo(status, DateTime.UtcNow);

        appt.Status.Should().Be(status);
    }
}
