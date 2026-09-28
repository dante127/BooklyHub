using BooklyHub.Application.Appointments;
using BooklyHub.Application.Common.Interfaces;
using BooklyHub.Application.Security;
using BooklyHub.Domain.Entities.Tenancy;
using BooklyHub.Domain.Exceptions;
using FluentAssertions;
using NSubstitute;

namespace BooklyHub.UnitTests.Appointments;

/// <summary>
/// The cutoff rules cancel, cancel-by-transition and reschedule all ask the same policy, so the policy is
/// pinned on its own: which roles may override, which setting each verb reads, and where the boundary lies.
/// </summary>
public class AppointmentCutoffPolicyTests
{
    private static readonly DateTime Now = new(2026, 3, 5, 10, 0, 0, DateTimeKind.Utc);

    private static ICurrentUser ActorIn(string? role = null)
    {
        var currentUser = Substitute.For<ICurrentUser>();
        if (role is not null)
        {
            currentUser.IsInRole(role).Returns(true);
        }

        return currentUser;
    }

    private static TenantSetting Settings(int cancellationHours, int reschedulingHours) =>
        new(Guid.NewGuid())
        {
            CancellationCutoffHours = cancellationHours,
            ReschedulingCutoffHours = reschedulingHours
        };

    [Theory]
    [InlineData(Roles.PlatformAdmin)]
    [InlineData(Roles.TenantOwner)]
    [InlineData(Roles.TenantAdmin)]
    [InlineData(Roles.Manager)]
    public void ARoleThatMayOverrideCutoffs_MustPassBothVerbsAtTheSameTime(string role)
    {
        var startAtUtc = Now.AddHours(1);
        var settings = Settings(cancellationHours: 24, reschedulingHours: 12);

        var act = () =>
        {
            AppointmentCutoffPolicy.EnsureCancellable(settings, startAtUtc, Now, ActorIn(role));
            AppointmentCutoffPolicy.EnsureReschedulable(settings, startAtUtc, Now, ActorIn(role));
        };

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(Roles.Receptionist)]
    [InlineData(Roles.Staff)]
    [InlineData(Roles.Accountant)]
    public void AWorkingRole_MustBeRefusedByBothVerbsInsideTheCutoff(string role)
    {
        var startAtUtc = Now.AddHours(3);
        var settings = Settings(cancellationHours: 24, reschedulingHours: 12);

        var cancel = () => AppointmentCutoffPolicy.EnsureCancellable(settings, startAtUtc, Now, ActorIn(role));
        var reschedule = () => AppointmentCutoffPolicy.EnsureReschedulable(settings, startAtUtc, Now, ActorIn(role));

        cancel.Should().Throw<BusinessRuleValidationException>().Which.RuleName.Should().Be("CancellationCutoffExceeded");
        reschedule.Should().Throw<BusinessRuleValidationException>().Which.RuleName.Should().Be("RescheduleCutoffExceeded");
    }

    [Fact]
    public void EachVerbMustReadItsOwnCutoffAndNotTheOtherOne()
    {
        // Three hours out: inside the 6 hour cancellation cutoff, outside the 2 hour rescheduling one.
        var startAtUtc = Now.AddHours(3);
        var settings = Settings(cancellationHours: 6, reschedulingHours: 2);
        var receptionist = ActorIn(Roles.Receptionist);

        var cancel = () => AppointmentCutoffPolicy.EnsureCancellable(settings, startAtUtc, Now, receptionist);
        var reschedule = () => AppointmentCutoffPolicy.EnsureReschedulable(settings, startAtUtc, Now, receptionist);

        cancel.Should().Throw<BusinessRuleValidationException>()
            .WithMessage("*within 6 hours*");
        reschedule.Should().NotThrow("the tenant's 2 hour rescheduling cutoff does not cover this lead time");
    }

    [Fact]
    public void TheCutoffBoundaryItselfMustStillBeAllowed()
    {
        var settings = Settings(cancellationHours: 6, reschedulingHours: 6);
        var receptionist = ActorIn(Roles.Receptionist);

        // Exactly six hours of lead is still cancellable; only strictly less than the cutoff refuses.
        Action atTheBoundary = () => AppointmentCutoffPolicy.EnsureCancellable(settings, Now.AddHours(6), Now, receptionist);
        atTheBoundary.Should().NotThrow();

        Action oneMinuteInside = () => AppointmentCutoffPolicy.EnsureCancellable(settings, Now.AddHours(6).AddMinutes(-1), Now, receptionist);
        oneMinuteInside.Should().Throw<BusinessRuleValidationException>();
    }

    [Fact]
    public void ATenantWithoutASettingsRow_MustStillGetTheDocumentedDefaults()
    {
        var startAtUtc = Now.AddHours(1);
        var receptionist = ActorIn(Roles.Receptionist);

        var cancel = () => AppointmentCutoffPolicy.EnsureCancellable(null, startAtUtc, Now, receptionist);
        var reschedule = () => AppointmentCutoffPolicy.EnsureReschedulable(null, startAtUtc, Now, receptionist);

        cancel.Should().Throw<BusinessRuleValidationException>().WithMessage("*within 24 hours*");
        reschedule.Should().Throw<BusinessRuleValidationException>().WithMessage("*within 12 hours*");
    }

    [Fact]
    public void AnAppointmentThatAlreadyStarted_MustStillBeRefusedForAWorkingRole()
    {
        var settings = Settings(cancellationHours: 24, reschedulingHours: 12);

        var act = () => AppointmentCutoffPolicy.EnsureCancellable(settings, Now.AddHours(-2), Now, ActorIn(Roles.Staff));

        act.Should().Throw<BusinessRuleValidationException>().Which.RuleName.Should().Be("CancellationCutoffExceeded");
    }
}
