using BooklyHub.Domain.Entities.System;
using FluentAssertions;
using Xunit;

namespace BooklyHub.UnitTests.Security;

/// <summary>
/// A replay window is a promise about time, and it used to be written from two separate wall-clock readings
/// and read back from a third — none of which a test could move. The window now opens from the clock the
/// caller supplies, so it can be opened and closed inside a test.
/// </summary>
public class IdempotencyWindowTests
{
    private static readonly DateTime WindowStart = new(2026, 3, 5, 10, 0, 0, DateTimeKind.Utc);

    private static IdempotencyRecord Open(TimeSpan ttl) =>
        new("tenant:key", Guid.NewGuid(), "request-hash", 201, "{}", ttl, WindowStart);

    [Fact]
    public void TheWindowOpensFromTheSuppliedClock()
    {
        var record = Open(TimeSpan.FromHours(24));

        record.CreatedAtUtc.Should().Be(WindowStart);
        record.ExpiresAtUtc.Should().Be(WindowStart.AddHours(24));
    }

    [Fact]
    public void ABackdatedWindow_MustNotBePatchedUpToThePresent()
    {
        // If any wall-clock reading survives in the constructor, the deadline lands near now instead of in
        // March, and the window can only be observed by waiting for it.
        var record = Open(TimeSpan.FromMinutes(30));

        record.ExpiresAtUtc.Should().Be(WindowStart.AddMinutes(30));
        record.ExpiresAtUtc.Should().BeBefore(DateTime.UtcNow);
    }

    [Fact]
    public void TheDeadlineMustSitExactlyOneTtlAfterTheStamp()
    {
        var record = Open(TimeSpan.FromHours(24));

        (record.ExpiresAtUtc - record.CreatedAtUtc).Should().Be(TimeSpan.FromHours(24),
            "both ends come from one clock reading; two readings drift and store a window longer than the one that was asked for");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AWindowThatStartsAlreadyClosed_MustStayClosed(int dayOffset)
    {
        // A zero or negative ttl is a caller saying "this may never replay"; clamping it open would turn the
        // parameter into a suggestion.
        var ttl = TimeSpan.FromDays(dayOffset);
        var record = Open(ttl);

        record.ExpiresAtUtc.Should().Be(WindowStart.Add(ttl));
        (record.ExpiresAtUtc > WindowStart).Should().BeFalse(
            "a non-positive ttl is the caller saying this may never replay, so it must not be clamped open");
    }
}
