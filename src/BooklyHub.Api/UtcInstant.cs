namespace BooklyHub.Api;

/// <summary>
/// BL-05: an instant a caller sends is read as the instant it names, and every value that leaves this layer is
/// labeled UTC. The columns are <c>datetime2</c>, which stores the ticks it is handed and converts nothing, so a
/// value that arrives carrying an offset but is labeled for the machine's zone lands in the UTC column shifted by
/// that zone's offset — measured on a host at UTC+3, a body of <c>09:00:00+00:00</c> was stored as 12:00.
///
/// The three shapes a caller can actually produce, and what each one means:
/// <list type="bullet">
/// <item><description><c>Kind=Utc</c> — an ISO-8601 value ending in <c>Z</c>. The instant is stated; kept.</description></item>
/// <item><description><c>Kind=Unspecified</c> — no designator. The field is named <c>…AtUtc</c>, so the value is
/// read as UTC and the ticks are untouched; only the label is added, which is what the downstream comparisons and
/// the DST conversions in <c>TimeZoneHelper</c> already assumed silently.</description></item>
/// <item><description><c>Kind=Local</c> — an explicit offset, which the serializer re-bases onto the server's zone
/// while preserving the instant. Converted back with <c>ToUniversalTime</c>, which is exact for the same reason the
/// re-basing was: both halves happen in this process.</description></item>
/// </list>
///
/// Refusing <c>Kind != Utc</c> was the other candidate and is the one this does not take: an offset-bearing value
/// states one instant unambiguously, and 400-ing it would break a client that is not wrong, while the naked form is
/// what the field name already promises to read as UTC. Refusing buys nothing that converting does not.
///
/// Only the two POST bodies and the dashboard window go through here. The appointment list's <c>fromUtc</c> and
/// <c>toUtc</c> deliberately do not: measured on a host at UTC+3, MVC's query binder hands back a UTC-labeled
/// instant for <c>Z</c> and for any offset (so no shift is possible) and the same ticks for a naked bound that the
/// UTC column already holds, which is what its SQL comparison reads. Passing those through would change no answer,
/// and a rule nobody can observe is not a rule a test can hold.
/// </summary>
public static class UtcInstant
{
    public static DateTime Resolve(DateTime value) => value.Kind switch
    {
        DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => value
    };

    public static DateTime? Resolve(DateTime? value) => value is null ? null : Resolve(value.Value);
}
