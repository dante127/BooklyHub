using BooklyHub.Application.Appointments.Commands;
using BooklyHub.Application.Common.Interfaces;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace BooklyHub.UnitTests.Appointments;

/// <summary>
/// Both "must be in the future" rules decide whether a request is acceptable, so both have to be judged
/// on the application clock like every other time rule. While they read the system clock their own
/// boundary was unreachable from a test, and the booking floor disagreed with the handler's notice rule
/// about which clock the request lived on.
/// </summary>
public class FutureStartRuleTests
{
    private static readonly DateTime Now = new(2026, 3, 5, 14, 0, 0, DateTimeKind.Utc);

    private static readonly Guid TenantId = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid LocationId = new("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ServiceId = new("33333333-3333-3333-3333-333333333333");
    private static readonly Guid StaffId = new("44444444-4444-4444-4444-444444444444");
    private static readonly Guid CustomerId = new("55555555-5555-5555-5555-555555555555");
    private static readonly Guid AppointmentId = new("66666666-6666-6666-6666-666666666666");

    private static IClock ClockAt(DateTime utc)
    {
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(utc);
        return clock;
    }

    private static BookAppointmentCommand Booking(DateTime startAtUtc) =>
        new(TenantId, LocationId, ServiceId, StaffId, CustomerId, startAtUtc);

    private static RescheduleAppointmentCommand Reschedule(DateTime newStartAtUtc) =>
        new(TenantId, AppointmentId, newStartAtUtc);

    private static void ShouldBeAccepted(FluentValidation.Results.ValidationResult result) =>
        result.IsValid.Should().BeTrue(string.Join("; ", result.Errors.Select(e => e.ErrorMessage)));

    private static void ShouldBeRefused(FluentValidation.Results.ValidationResult result, string message)
    {
        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.ErrorMessage == message);
    }

    [Fact]
    public void Booking_JustPastTheFiveMinuteFloor_IsAccepted()
    {
        var validator = new BookAppointmentCommandValidator(ClockAt(Now));

        ShouldBeAccepted(validator.Validate(Booking(Now.AddMinutes(5).AddSeconds(1))));
    }

    [Fact]
    public void Booking_ExactlyOnTheFiveMinuteFloor_IsRefused()
    {
        // The rule is a strict inequality, so the floor minute itself is not bookable.
        var validator = new BookAppointmentCommandValidator(ClockAt(Now));

        ShouldBeRefused(validator.Validate(Booking(Now.AddMinutes(5))),
            "Appointment start time must be in the future.");
    }

    [Fact]
    public void Booking_JustBeforeTheFiveMinuteFloor_IsRefused()
    {
        var validator = new BookAppointmentCommandValidator(ClockAt(Now));

        ShouldBeRefused(validator.Validate(Booking(Now.AddMinutes(5).AddSeconds(-1))),
            "Appointment start time must be in the future.");
    }

    [Fact]
    public void Reschedule_JustAfterTheClock_IsAccepted()
    {
        var validator = new RescheduleAppointmentCommandValidator(ClockAt(Now));

        ShouldBeAccepted(validator.Validate(Reschedule(Now.AddSeconds(1))));
    }

    [Fact]
    public void Reschedule_ExactlyAtTheClock_IsRefused()
    {
        var validator = new RescheduleAppointmentCommandValidator(ClockAt(Now));

        ShouldBeRefused(validator.Validate(Reschedule(Now)), "Rescheduled time must be in the future.");
    }

    [Fact]
    public void Reschedule_JustBeforeTheClock_IsRefused()
    {
        var validator = new RescheduleAppointmentCommandValidator(ClockAt(Now));

        ShouldBeRefused(validator.Validate(Reschedule(Now.AddSeconds(-1))),
            "Rescheduled time must be in the future.");
    }

    [Fact]
    public void TheBookingFloor_MustAnswerToTheSuppliedClockNotToTheSystem()
    {
        // A start time comfortably ahead of the real machine, but already past the clock this request is
        // judged on. Refusing it is only possible if the rule reads the supplied instant.
        var aheadOfTheSuppliedClock = DateTime.UtcNow.AddHours(3);
        var validator = new BookAppointmentCommandValidator(ClockAt(aheadOfTheSuppliedClock.AddMinutes(6)));

        ShouldBeRefused(validator.Validate(Booking(aheadOfTheSuppliedClock)),
            "Appointment start time must be in the future.");
    }

    [Fact]
    public void TheRescheduleFloor_MustAnswerToTheSuppliedClockNotToTheSystem()
    {
        var aheadOfTheSuppliedClock = DateTime.UtcNow.AddHours(3);
        var validator = new RescheduleAppointmentCommandValidator(ClockAt(aheadOfTheSuppliedClock.AddMinutes(1)));

        ShouldBeRefused(validator.Validate(Reschedule(aheadOfTheSuppliedClock)),
            "Rescheduled time must be in the future.");
    }

    [Fact]
    public void AStartBehindTheSystemClockButAheadOfTheSuppliedOne_IsAccepted()
    {
        // The other direction: the machine says this instant already happened, the clock under test says
        // it is still ahead. Only a rule reading the supplied clock can accept it.
        var behindTheSystemClock = DateTime.UtcNow.AddMinutes(-30);
        var validator = new RescheduleAppointmentCommandValidator(ClockAt(behindTheSystemClock.AddMinutes(-5)));

        ShouldBeAccepted(validator.Validate(Reschedule(behindTheSystemClock)));
    }

    [Fact]
    public void TheFloor_MustBeReReadForEveryValidation()
    {
        // The clock is captured as a dependency, not as a value, or a validator reused across requests
        // would keep judging against the instant it was built at.
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(Now, Now.AddMinutes(30));
        var validator = new BookAppointmentCommandValidator(clock);

        ShouldBeAccepted(validator.Validate(Booking(Now.AddMinutes(10))));
        ShouldBeRefused(validator.Validate(Booking(Now.AddMinutes(10))),
            "Appointment start time must be in the future.");
    }
}
