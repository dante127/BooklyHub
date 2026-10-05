using System.Globalization;
using BooklyHub.Domain.Entities.Appointments;
using BooklyHub.Domain.Exceptions;
using FluentAssertions;
using Xunit;

namespace BooklyHub.UnitTests.Appointments;

/// <summary>
/// BL-05's other half: the API layer resolves an inbound instant to UTC, and these two writers are where the
/// invariant is stated as a rule instead of as a convention. <c>datetime2</c> stores the ticks it is handed, so a
/// value still labeled for a machine's zone is not a formatting preference — it is an appointment booked at a time
/// nobody chose, and the shift depends on where the process happens to run. Only <c>Kind=Local</c> is refused: it is
/// the one label that says "these ticks belong to a zone", while an Unspecified value claims nothing about a zone and
/// is what the availability engine has always built from a local calendar.
/// </summary>
public class UtcInstantWriterTests
{
    private static readonly DateTime Slot = new(2027, 6, 1, 9, 0, 0, DateTimeKind.Utc);

    private static DateTime InMachineZone(DateTime utcInstant) =>
        TimeZoneInfo.ConvertTimeFromUtc(utcInstant, TimeZoneInfo.Local);

    private static Appointment BookedUtc() => Appointment.Create(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        Slot, Slot.AddMinutes(30), 30, 100.00m);

    [Fact]
    public void ACreate_MustRefuseAnInstantLabeledForTheMachineZone()
    {
        var local = InMachineZone(Slot);
        local.Kind.Should().Be(DateTimeKind.Local, "a value this machine built for itself is the exact shape a caller can send");

        var act = () => Appointment.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            local, local.AddMinutes(30), 30, 100.00m);

        act.Should().Throw<BusinessRuleValidationException>().Which.RuleName
            .Should().Be("InvalidDateKind");
    }

    [Fact]
    public void ACreate_MustStillAcceptAnInstantThatClaimsNoZone()
    {
        // The availability engine builds candidate times from a staff member's local calendar and hands them over as
        // Unspecified; refusing that here would refuse every booking, so the rule stays exactly as wide as the defect.
        var noZone = DateTime.Parse("2027-06-01T09:00:00", CultureInfo.InvariantCulture);
        noZone.Kind.Should().Be(DateTimeKind.Unspecified);

        var appointment = Appointment.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            noZone, noZone.AddMinutes(30), 30, 100.00m);

        appointment.StartAtUtc.Should().Be(Slot, "the ticks are kept untouched, which is what reading it as UTC means");
    }

    [Fact]
    public void AReschedule_MustRefuseAnInstantLabeledForTheMachineZone()
    {
        var appointment = BookedUtc();
        var local = InMachineZone(Slot.AddHours(2));

        var act = () => appointment.Reschedule(local, local.AddMinutes(30), "moved by staff");

        act.Should().Throw<BusinessRuleValidationException>().Which.RuleName
            .Should().Be("InvalidDateKind");

        appointment.StartAtUtc.Should().Be(Slot, "a refused move must not have shifted the row at all");
    }
}
