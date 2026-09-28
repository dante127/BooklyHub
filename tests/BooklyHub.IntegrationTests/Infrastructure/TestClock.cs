using BooklyHub.Application.Common.Interfaces;

namespace BooklyHub.IntegrationTests.Infrastructure;

/// <summary>
/// The test host's <see cref="IClock"/>. Reports real UTC until a test pins it, so the suites that schedule
/// against <see cref="DateTime.UtcNow"/> see exactly the clock they had before, and the ones that need a
/// deadline to pass can pass it instead of waiting.
/// </summary>
public sealed class TestClock : IClock
{
    private DateTime? _pinnedUtc;

    public DateTime UtcNow => _pinnedUtc ?? DateTime.UtcNow;

    public void AdvanceBy(TimeSpan by) => _pinnedUtc = UtcNow + by;

    public void Pin(DateTime utc) => _pinnedUtc = utc;

    public void Release() => _pinnedUtc = null;
}
