using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace BooklyHub.Infrastructure.Data;

/// <summary>
/// TIME-01: labels every instant this context materializes as UTC, at the moment it enters the CLR object.
///
/// SQL Server's <c>datetime2</c> stores ticks and no zone, so a value EF reads arrives
/// <c>Kind=Unspecified</c>, and System.Text.Json writes what the Kind says — a naked
/// <c>2026-10-08T12:30:00</c>. The instant in the column is right and the ticks never move; what was missing is the
/// label the field name already promises (<c>…AtUtc</c>), and a client that parses a naked ISO instant as its own
/// local time shifts every appointment it lists by its own offset. The write path did not show this because the
/// value the request boundary produced still carried <c>Kind=Utc</c>, so the create response said <c>Z</c> while the
/// read of the same row did not.
///
/// It is a model-wide convention rather than a per-DTO fix because every persisted <see cref="DateTime"/> in the
/// domain is a UTC instant — all 58 of them, across 13 entity files, are named with a <c>Utc</c> suffix — and the
/// local wall times this API does show are produced from those instants on the way out
/// (<c>AvailabilityService.cs:150-151</c>), never stored. Putting it here also means a read path added next month
/// cannot reintroduce the naked spelling.
/// </summary>
public sealed class UtcInstantConverter : ValueConverter<DateTime, DateTime>
{
    public UtcInstantConverter()
        : base(convertToProviderExpression: static value => value,
               convertFromProviderExpression: static value => DateTime.SpecifyKind(value, DateTimeKind.Utc))
    {
    }
}
