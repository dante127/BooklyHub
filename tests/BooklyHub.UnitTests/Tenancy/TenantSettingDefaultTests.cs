using BooklyHub.Domain.Entities.Tenancy;
using FluentAssertions;
using Xunit;

namespace BooklyHub.UnitTests.Tenancy;

/// <summary>
/// The numbers a tenant that never configured anything is judged by are documented in
/// <c>docs/MULTI-TENANCY.md</c>, and they were written down in five separate places in the code: the
/// initializer on each property and the fallback of each reader. Those copies are now one constant each, which is
/// what the wire facts in <c>TenantDefaultsWithoutSettingsRowTests</c> rely on. A fact here that names a literal is
/// therefore a documentation pin, not a proof of behaviour — it fails when somebody moves a number without moving
/// the documentation, and the behaviour itself is proved against real SQL Server over there.
/// </summary>
public class TenantSettingDefaultTests
{
    [Theory]
    // The name is part of each row because two of these defaults are the same number: without it xUnit collapses the
    // two 24s into one case, and a failure would not say which rule moved.
    [InlineData("MinBookingNoticeMinutes", TenantSetting.DefaultMinBookingNoticeMinutes, 120)]
    [InlineData("MaxAdvanceBookingDays", TenantSetting.DefaultMaxAdvanceBookingDays, 60)]
    [InlineData("CancellationCutoffHours", TenantSetting.DefaultCancellationCutoffHours, 24)]
    [InlineData("ReschedulingCutoffHours", TenantSetting.DefaultReschedulingCutoffHours, 12)]
    [InlineData("SlotIntervalMinutes", TenantSetting.DefaultSlotIntervalMinutes, 15)]
    [InlineData("ReminderNoticeHours", TenantSetting.DefaultReminderNoticeHours, 24)]
    public void DocumentedDefault_MustStayTheNumberTheDocumentationNames(string setting, int resolved, int documented)
    {
        resolved.Should().Be(documented,
            $"{setting} is the value a tenant with no settings row is judged by, and the same value the entity hands a row that was created and never edited");
    }

    [Fact]
    public void FreshSettingsRow_MustCarryTheSameNumbersTheFallbacksUse()
    {
        var settings = new TenantSetting(Guid.NewGuid());

        // The point of naming the constants: the two ways a tenant can end up unconfigured — no row, or a row nobody
        // touched — must not be able to disagree. Before this they were two literals in two files.
        settings.MinBookingNoticeMinutes.Should().Be(TenantSetting.DefaultMinBookingNoticeMinutes);
        settings.MaxAdvanceBookingDays.Should().Be(TenantSetting.DefaultMaxAdvanceBookingDays);
        settings.CancellationCutoffHours.Should().Be(TenantSetting.DefaultCancellationCutoffHours);
        settings.ReschedulingCutoffHours.Should().Be(TenantSetting.DefaultReschedulingCutoffHours);
        settings.SlotIntervalMinutes.Should().Be(TenantSetting.DefaultSlotIntervalMinutes);
        settings.ReminderNoticeHours.Should().Be(TenantSetting.DefaultReminderNoticeHours);
    }
}
