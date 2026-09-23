namespace BooklyHub.Application.Scheduling;

public record TimeInterval
{
    public DateTime StartUtc { get; }
    public DateTime EndUtc { get; }

    public TimeSpan Duration => EndUtc - StartUtc;

    public TimeInterval(DateTime startUtc, DateTime endUtc)
    {
        if (endUtc < startUtc)
        {
            throw new ArgumentException("EndUtc cannot be earlier than StartUtc.", nameof(endUtc));
        }

        StartUtc = startUtc;
        EndUtc = endUtc;
    }

    public bool Overlaps(TimeInterval other)
    {
        return StartUtc < other.EndUtc && EndUtc > other.StartUtc;
    }

    public bool Overlaps(DateTime start, DateTime end)
    {
        return StartUtc < end && EndUtc > start;
    }

    public TimeInterval? Intersect(TimeInterval other)
    {
        var start = StartUtc > other.StartUtc ? StartUtc : other.StartUtc;
        var end = EndUtc < other.EndUtc ? EndUtc : other.EndUtc;

        return start < end ? new TimeInterval(start, end) : null;
    }

    public IReadOnlyList<TimeInterval> Subtract(TimeInterval toSubtract)
    {
        // No overlap
        if (!Overlaps(toSubtract))
        {
            return [this];
        }

        // Completely covered
        if (toSubtract.StartUtc <= StartUtc && toSubtract.EndUtc >= EndUtc)
        {
            return [];
        }

        var results = new List<TimeInterval>();

        // Segment before toSubtract
        if (toSubtract.StartUtc > StartUtc)
        {
            results.Add(new TimeInterval(StartUtc, toSubtract.StartUtc));
        }

        // Segment after toSubtract
        if (toSubtract.EndUtc < EndUtc)
        {
            results.Add(new TimeInterval(toSubtract.EndUtc, EndUtc));
        }

        return results;
    }

    public static List<TimeInterval> SubtractMany(IEnumerable<TimeInterval> baseIntervals, IEnumerable<TimeInterval> subtractions)
    {
        var current = baseIntervals.ToList();

        foreach (var sub in subtractions)
        {
            var next = new List<TimeInterval>();
            foreach (var interval in current)
            {
                next.AddRange(interval.Subtract(sub));
            }
            current = next;
        }

        return current.Where(i => i.Duration > TimeSpan.Zero).ToList();
    }
}
