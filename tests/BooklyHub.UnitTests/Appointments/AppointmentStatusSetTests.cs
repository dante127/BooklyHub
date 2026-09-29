using BooklyHub.Application.Payments;
using BooklyHub.Domain.Entities.Appointments;
using BooklyHub.Domain.Enums;
using FluentAssertions;
using Xunit;

namespace BooklyHub.UnitTests.Appointments;

/// <summary>
/// "What is still owed a visit" is one definition, and the booking engine, the reminder sweep and the
/// reporting counters all have to answer to it. These tests bind the set to the state machine itself: a
/// status is closed exactly when nothing can follow it, so adding a status to the enum cannot let it join
/// either bucket without a test saying so.
/// </summary>
public class AppointmentStatusSetTests
{
    private static readonly AppointmentStatus[] AllStatuses = Enum.GetValues<AppointmentStatus>();

    private static Appointment AppointmentIn(AppointmentStatus status)
    {
        // Start in the past so the temporal gate on the execution statuses does not interfere with a test
        // about which transitions exist at all.
        var start = DateTime.UtcNow.AddMinutes(-30);
        var appointment = Appointment.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            start, start.AddMinutes(45), 45, 75.00m);

        if (status == AppointmentStatus.Pending) return appointment;

        appointment.TransitionTo(AppointmentStatus.Confirmed, DateTime.UtcNow);
        if (status == AppointmentStatus.Confirmed) return appointment;

        appointment.TransitionTo(AppointmentStatus.CheckedIn, DateTime.UtcNow);
        if (status == AppointmentStatus.CheckedIn) return appointment;

        if (status == AppointmentStatus.InProgress)
        {
            appointment.TransitionTo(AppointmentStatus.InProgress, DateTime.UtcNow);
            return appointment;
        }

        if (status == AppointmentStatus.Completed)
        {
            appointment.TransitionTo(AppointmentStatus.InProgress, DateTime.UtcNow);
            appointment.TransitionTo(AppointmentStatus.Completed, DateTime.UtcNow);
            return appointment;
        }

        if (status == AppointmentStatus.Cancelled)
        {
            // Reached from Confirmed rather than from Pending, since the walk above is already past Pending.
            appointment.TransitionTo(AppointmentStatus.Cancelled, DateTime.UtcNow);
            return appointment;
        }

        appointment.TransitionTo(AppointmentStatus.NoShow, DateTime.UtcNow);
        return appointment;
    }

    private static bool HasOutgoingTransition(AppointmentStatus from) =>
        AllStatuses.Any(to => AppointmentIn(from).CanTransitionTo(to));

    [Fact]
    public void EveryClosedStatus_MustHaveNoTransitionOut()
    {
        foreach (var status in AppointmentStatusSet.Closed)
        {
            HasOutgoingTransition(status).Should().BeFalse(
                $"{status} is reported as closed, so nothing may follow it");
        }
    }

    [Fact]
    public void EveryOpenStatus_MustStillHaveSomewhereToGo()
    {
        // Rescheduled is the documented exception: unreachable for new rows, but a legacy row in it is a
        // live booking, so it belongs to neither bucket by transition.
        var open = AllStatuses
            .Where(s => !AppointmentStatusSet.IsClosed(s) && s != AppointmentStatus.Rescheduled)
            .ToList();

        open.Should().NotBeEmpty();

        foreach (var status in open)
        {
            HasOutgoingTransition(status).Should().BeTrue(
                $"{status} is not closed, so the state machine must still be able to move it");
        }
    }

    [Fact]
    public void TheOnlyStatusThatIsNeitherClosedNorAbleToTransition_MustBeRescheduled()
    {
        var unclassified = AllStatuses
            .Where(s => !AppointmentStatusSet.IsClosed(s) && !HasOutgoingTransition(s))
            .ToList();

        unclassified.Should().Equal(new[] { AppointmentStatus.Rescheduled },
            because: "a new status has to be declared closed or given an outgoing edge, or the upcoming counters silently stop covering it");
    }

    [Fact]
    public void IsClosed_MustAgreeWithTheSet()
    {
        foreach (var status in AllStatuses)
        {
            AppointmentStatusSet.IsClosed(status)
                .Should().Be(AppointmentStatusSet.Closed.Contains(status), $"for {status}");
        }
    }

    // The collection queue lists a booking by its status, so the set it reads has to be a set of statuses the
    // money door still opens for. Otherwise the queue invites a charge the ledger is going to refuse.
    [Fact]
    public void EveryCollectableStatus_MustBeOneTheChargeDoorStillOpensFor()
    {
        foreach (var status in AppointmentStatusSet.Collectable)
        {
            var action = () => PaymentLedger.ValidateCharge(
                PaymentLedger.From([]), 100.00m, "USD", "USD", status, 100.00m);

            action.Should().NotThrow($"{status} is listed as still owing money, so charging it must be permitted");
        }
    }

    [Fact]
    public void Collectable_MustExcludeEveryStatusWhoseDebtIsNotTheCustomers()
    {
        // Pending: the clinic never accepted the booking, so nothing is owed to it. CheckedIn and InProgress:
        // the customer appeared and the open row is the clinic's own reporting gap. Cancelled and NoShow: the
        // visit is over with nothing owed, and a NoShow cannot be charged at all.
        AppointmentStatusSet.Collectable.Should().BeEquivalentTo(
            [AppointmentStatus.Confirmed, AppointmentStatus.Completed]);
    }

    // Completed is the only status that both ends the visit and still owes for it, which is exactly why the
    // queue cannot reuse Closed: the same status answers "is it over" and "is it paid" differently.
    [Fact]
    public void ClosedAndCollectable_MustOverlapOnCompletedAlone()
    {
        AppointmentStatusSet.Closed.Intersect(AppointmentStatusSet.Collectable)
            .Should().Equal(AppointmentStatus.Completed);
    }
}
