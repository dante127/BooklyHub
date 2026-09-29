using BooklyHub.Domain.Entities.Appointments;
using BooklyHub.Domain.Enums;
using BooklyHub.Domain.Events;
using BooklyHub.Domain.Exceptions;
using FluentAssertions;
using Xunit;

namespace BooklyHub.UnitTests.Appointments;

public class AppointmentStateMachineTests
{
    private Appointment CreateSampleAppointment()
    {
        // Started two hours ago: the execution statuses (CheckedIn/InProgress/Completed/NoShow) are
        // temporally gated, so a lifecycle test needs an appointment whose time has come.
        var start = DateTime.UtcNow.AddHours(-2);
        return Appointment.Create(
            tenantId: Guid.NewGuid(),
            locationId: Guid.NewGuid(),
            serviceId: Guid.NewGuid(),
            staffId: Guid.NewGuid(),
            customerId: Guid.NewGuid(),
            startAtUtc: start,
            endAtUtc: start.AddMinutes(45),
            durationMinutes: 45,
            price: 75.00m,
            notes: "Test booking");
    }

    [Fact]
    public void Create_ShouldInitializeAsPendingWithInitialHistory()
    {
        var appt = CreateSampleAppointment();

        appt.Status.Should().Be(AppointmentStatus.Pending);
        appt.DurationMinutes.Should().Be(45);
        appt.StatusHistories.Should().HaveCount(1);
        appt.StatusHistories.First().ToStatus.Should().Be(AppointmentStatus.Pending);

        appt.DomainEvents.Should().ContainSingle(e => e is AppointmentCreatedEvent);
    }

    [Fact]
    public void HappyPath_FullLifecycle_ShouldSucceed()
    {
        var appt = CreateSampleAppointment();

        // Pending -> Confirmed
        appt.TransitionTo(AppointmentStatus.Confirmed, DateTime.UtcNow, "Payment authorized", "StaffUser");
        appt.Status.Should().Be(AppointmentStatus.Confirmed);

        // Confirmed -> CheckedIn
        appt.TransitionTo(AppointmentStatus.CheckedIn, DateTime.UtcNow, "Customer arrived at desk", "Receptionist");
        appt.Status.Should().Be(AppointmentStatus.CheckedIn);

        // CheckedIn -> InProgress
        appt.TransitionTo(AppointmentStatus.InProgress, DateTime.UtcNow, "Treatment started", "StaffUser");
        appt.Status.Should().Be(AppointmentStatus.InProgress);

        // InProgress -> Completed
        appt.TransitionTo(AppointmentStatus.Completed, DateTime.UtcNow, "Treatment finished successfully", "StaffUser");
        appt.Status.Should().Be(AppointmentStatus.Completed);

        // Total histories: Initial + 4 transitions = 5
        appt.StatusHistories.Should().HaveCount(5);
        appt.DomainEvents.Should().Contain(e => e is AppointmentCompletedEvent);
    }

    [Fact]
    public void InvalidTransition_FromCompletedToCancelled_ShouldThrowException()
    {
        var appt = CreateSampleAppointment();
        appt.TransitionTo(AppointmentStatus.Confirmed, DateTime.UtcNow);
        appt.TransitionTo(AppointmentStatus.CheckedIn, DateTime.UtcNow);
        appt.TransitionTo(AppointmentStatus.InProgress, DateTime.UtcNow);
        appt.TransitionTo(AppointmentStatus.Completed, DateTime.UtcNow);

        var act = () => appt.TransitionTo(AppointmentStatus.Cancelled, DateTime.UtcNow, "Customer wants refund");

        act.Should().Throw<InvalidStateTransitionException>()
            .WithMessage("*not permitted*");
    }

    [Fact]
    public void InvalidTransition_FromCancelledToConfirmed_ShouldThrowException()
    {
        var appt = CreateSampleAppointment();
        appt.TransitionTo(AppointmentStatus.Cancelled, DateTime.UtcNow, "Customer cancelled");

        var act = () => appt.TransitionTo(AppointmentStatus.Confirmed, DateTime.UtcNow);

        act.Should().Throw<InvalidStateTransitionException>()
            .WithMessage("*not permitted*");
    }

    [Fact]
    public void Reschedule_ValidNewTimes_ShouldUpdateAndRecordHistory()
    {
        var appt = CreateSampleAppointment();
        appt.TransitionTo(AppointmentStatus.Confirmed, DateTime.UtcNow);

        var newStart = DateTime.UtcNow.AddDays(2).Date.AddHours(14);
        var newEnd = newStart.AddMinutes(45);

        appt.Reschedule(newStart, newEnd, "Customer requested new time", "Receptionist");

        appt.StartAtUtc.Should().Be(newStart);
        appt.EndAtUtc.Should().Be(newEnd);
        appt.DurationMinutes.Should().Be(45);

        appt.DomainEvents.Should().Contain(e => e is AppointmentRescheduledEvent);
        appt.StatusHistories.Should().Contain(h => h.Reason != null && h.Reason.Contains("Rescheduled from"));
    }

    [Fact]
    public void Reschedule_InvalidTimeRange_ShouldThrowException()
    {
        var appt = CreateSampleAppointment();
        appt.TransitionTo(AppointmentStatus.Confirmed, DateTime.UtcNow);

        var newStart = DateTime.UtcNow.AddDays(2).Date.AddHours(14);
        var newEnd = newStart.AddHours(-1); // Earlier than start!

        var act = () => appt.Reschedule(newStart, newEnd);

        act.Should().Throw<BusinessRuleValidationException>();
    }

    [Fact]
    public void Transition_ConfirmedToCompleted_AfterStart_ShouldSucceed()
    {
        var appt = CreateSampleAppointment();
        appt.TransitionTo(AppointmentStatus.Confirmed, DateTime.UtcNow);

        appt.TransitionTo(AppointmentStatus.Completed, DateTime.UtcNow, "Walk-in handled without check-in ceremony");

        appt.Status.Should().Be(AppointmentStatus.Completed);
        appt.DomainEvents.Should().Contain(e => e is AppointmentCompletedEvent);
    }

    [Fact]
    public void Transition_CheckedInToCompleted_AfterStart_ShouldSucceed()
    {
        var appt = CreateSampleAppointment();
        appt.TransitionTo(AppointmentStatus.Confirmed, DateTime.UtcNow);
        appt.TransitionTo(AppointmentStatus.CheckedIn, DateTime.UtcNow);

        appt.TransitionTo(AppointmentStatus.Completed, DateTime.UtcNow);

        appt.Status.Should().Be(AppointmentStatus.Completed);
    }

    [Fact]
    public void Transition_ToRescheduled_ShouldAlwaysThrow()
    {
        // Reschedule() moves the appointment in place and keeps it active, so nothing may ever set the
        // Rescheduled status: a row moved there by hand became non-cancellable and invisible to
        // availability while still being displayed as a live booking.
        var appt = CreateSampleAppointment();
        appt.TransitionTo(AppointmentStatus.Confirmed, DateTime.UtcNow);

        var act = () => appt.TransitionTo(AppointmentStatus.Rescheduled, DateTime.UtcNow);

        act.Should().Throw<InvalidStateTransitionException>()
            .WithMessage("*not permitted*");
    }

    [Fact]
    public void Transition_ToSameStatus_ShouldThrow()
    {
        var appt = CreateSampleAppointment();
        appt.TransitionTo(AppointmentStatus.Confirmed, DateTime.UtcNow);

        var act = () => appt.TransitionTo(AppointmentStatus.Confirmed, DateTime.UtcNow);

        act.Should().Throw<InvalidStateTransitionException>()
            .WithMessage("*not permitted*");
    }

    [Fact]
    public void Transition_CheckInLongBeforeStart_ShouldThrow()
    {
        var appt = CreateFutureAppointment();
        appt.TransitionTo(AppointmentStatus.Confirmed, DateTime.UtcNow);

        var act = () => appt.TransitionTo(AppointmentStatus.CheckedIn, DateTime.UtcNow);

        act.Should().Throw<BusinessRuleValidationException>()
            .Where(e => e.RuleName == "TransitionTooEarly");
    }

    [Fact]
    public void Transition_CompleteBeforeStart_ShouldThrow()
    {
        var appt = CreateFutureAppointment();
        appt.TransitionTo(AppointmentStatus.Confirmed, DateTime.UtcNow);

        var act = () => appt.TransitionTo(AppointmentStatus.Completed, DateTime.UtcNow);

        act.Should().Throw<BusinessRuleValidationException>()
            .Where(e => e.RuleName == "TransitionTooEarly");
    }

    [Fact]
    public void Transition_NoShowBeforeStart_ShouldThrow()
    {
        var appt = CreateFutureAppointment();
        appt.TransitionTo(AppointmentStatus.Confirmed, DateTime.UtcNow);

        var act = () => appt.TransitionTo(AppointmentStatus.NoShow, DateTime.UtcNow);

        act.Should().Throw<BusinessRuleValidationException>()
            .Where(e => e.RuleName == "TransitionTooEarly");
    }

    private static Appointment CreateFutureAppointment()
    {
        var start = DateTime.UtcNow.AddDays(2);
        return Appointment.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            start, start.AddMinutes(45), 45, 75.00m);
    }
}
