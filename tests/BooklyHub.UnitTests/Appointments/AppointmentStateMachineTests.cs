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
        return Appointment.Create(
            tenantId: Guid.NewGuid(),
            locationId: Guid.NewGuid(),
            serviceId: Guid.NewGuid(),
            staffId: Guid.NewGuid(),
            customerId: Guid.NewGuid(),
            startAtUtc: new DateTime(2026, 11, 10, 10, 0, 0, DateTimeKind.Utc),
            endAtUtc: new DateTime(2026, 11, 10, 10, 45, 0, DateTimeKind.Utc),
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
        appt.TransitionTo(AppointmentStatus.Confirmed, "Payment authorized", "StaffUser");
        appt.Status.Should().Be(AppointmentStatus.Confirmed);

        // Confirmed -> CheckedIn
        appt.TransitionTo(AppointmentStatus.CheckedIn, "Customer arrived at desk", "Receptionist");
        appt.Status.Should().Be(AppointmentStatus.CheckedIn);

        // CheckedIn -> InProgress
        appt.TransitionTo(AppointmentStatus.InProgress, "Treatment started", "StaffUser");
        appt.Status.Should().Be(AppointmentStatus.InProgress);

        // InProgress -> Completed
        appt.TransitionTo(AppointmentStatus.Completed, "Treatment finished successfully", "StaffUser");
        appt.Status.Should().Be(AppointmentStatus.Completed);

        // Total histories: Initial + 4 transitions = 5
        appt.StatusHistories.Should().HaveCount(5);
        appt.DomainEvents.Should().Contain(e => e is AppointmentCompletedEvent);
    }

    [Fact]
    public void InvalidTransition_FromCompletedToCancelled_ShouldThrowException()
    {
        var appt = CreateSampleAppointment();
        appt.TransitionTo(AppointmentStatus.Confirmed);
        appt.TransitionTo(AppointmentStatus.CheckedIn);
        appt.TransitionTo(AppointmentStatus.InProgress);
        appt.TransitionTo(AppointmentStatus.Completed);

        var act = () => appt.TransitionTo(AppointmentStatus.Cancelled, "Customer wants refund");

        act.Should().Throw<InvalidStateTransitionException>()
            .WithMessage("*not permitted*");
    }

    [Fact]
    public void InvalidTransition_FromCancelledToConfirmed_ShouldThrowException()
    {
        var appt = CreateSampleAppointment();
        appt.TransitionTo(AppointmentStatus.Cancelled, "Customer cancelled");

        var act = () => appt.TransitionTo(AppointmentStatus.Confirmed);

        act.Should().Throw<InvalidStateTransitionException>()
            .WithMessage("*not permitted*");
    }

    [Fact]
    public void Reschedule_ValidNewTimes_ShouldUpdateAndRecordHistory()
    {
        var appt = CreateSampleAppointment();
        appt.TransitionTo(AppointmentStatus.Confirmed);

        var newStart = new DateTime(2026, 11, 12, 14, 0, 0, DateTimeKind.Utc);
        var newEnd = new DateTime(2026, 11, 12, 14, 45, 0, DateTimeKind.Utc);

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
        appt.TransitionTo(AppointmentStatus.Confirmed);

        var newStart = new DateTime(2026, 11, 12, 14, 0, 0, DateTimeKind.Utc);
        var newEnd = new DateTime(2026, 11, 12, 13, 0, 0, DateTimeKind.Utc); // Earlier than start!

        var act = () => appt.Reschedule(newStart, newEnd);

        act.Should().Throw<BusinessRuleValidationException>();
    }
}
