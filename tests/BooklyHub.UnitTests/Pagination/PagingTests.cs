using BooklyHub.Application.Common.Models;
using FluentAssertions;
using Xunit;

namespace BooklyHub.UnitTests.Pagination;

/// <summary>
/// PAG-01: four routes page over live tables and each had its own idea of what a caller may ask for — two of them
/// had none at all. <see cref="Paging"/> is now the single rule; these facts pin the rule itself, because a shared
/// helper that no test reads can be quietly reintroduced as four different helpers again.
/// </summary>
public class PagingTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    [InlineData(1, 1)]
    [InlineData(int.MaxValue, int.MaxValue)]
    public void ANonPositivePageIsTheFirstPage(int requested, int expected) =>
        Paging.NormalizePage(requested).Should().Be(expected);

    [Theory]
    [InlineData(0, Paging.DefaultPageSize)]
    [InlineData(-5, Paging.DefaultPageSize)]
    [InlineData(101, Paging.DefaultPageSize)]
    [InlineData(int.MaxValue, Paging.DefaultPageSize)]
    [InlineData(20, 20)]
    [InlineData(100, 100)]
    public void ASizeOutsideTheAllowedRangeIsTheDefaultRatherThanTheAsk(int requested, int expected) =>
        Paging.NormalizePageSize(requested).Should().Be(expected);

    [Fact]
    public void TheFirstPageOfAnythingStartsAtTheFirstRow() => Paging.Offset(1, 20).Should().Be(0);

    [Fact]
    public void TheOffsetIsTheClampedPageTimesTheClampedSize()
    {
        Paging.Offset(3, 20).Should().Be(40);

        // Both asks are illegal, so this is the composition the routes actually hand to SQL: page 1, size 20.
        Paging.Offset(0, -5).Should().Be(0);
    }

    [Fact]
    public void AnOffsetBeyondTheRangeOfAnIntIsNotANegativeOffset()
    {
        // The defect this pins: (page - 1) * pageSize is an int product, so a legal-looking page two wraps to a
        // negative skip and SQL Server answers a 500 for a request whose only sin is arithmetic.
        Paging.Offset(int.MaxValue, 100).Should().Be(int.MaxValue);
        Paging.Offset(2147483647, 20).Should().Be(int.MaxValue);
    }

    [Theory]
    [InlineData(int.MaxValue, 1)]
    [InlineData(int.MaxValue, 20)]
    [InlineData(int.MaxValue, 100)]
    [InlineData(1000000, 100)]
    [InlineData(-7, 3)]
    public void EveryClampedAskMustProduceANonNegativeOffset(int page, int pageSize) =>
        Paging.Offset(page, pageSize).Should().BeGreaterThanOrEqualTo(0);
}
