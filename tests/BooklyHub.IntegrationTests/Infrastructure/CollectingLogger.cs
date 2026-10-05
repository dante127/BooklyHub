using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace BooklyHub.IntegrationTests.Infrastructure;

/// <summary>
/// Everything the application logged through <c>ILogger&lt;T&gt;</c>, kept in memory so a fact can read the log
/// instead of reading the source. SEC-09 needed it: "a dispatch writes no customer address" and "a refusal says
/// which of its four causes it was" are claims about what the host wrote, and neither is visible from a response.
/// </summary>
/// <remarks>
/// <para>
/// This sits at the <see cref="ILogger"/> seam rather than at an <see cref="ILoggerProvider"/>, and the reason is
/// measured rather than chosen: <c>Program.cs</c> swaps Serilog in with <c>UseSerilog()</c>, and the
/// <see cref="ILoggerFactory"/> that comes back out of the built container is Serilog's own factory, which writes
/// to the static Serilog logger and never enumerates the registered providers. A collector registered as a
/// provider was in the container, was listed by <c>GetServices&lt;ILoggerProvider&gt;()</c>, and received nothing
/// at all — <c>TheCollector_MustSeeALineTheHostWrites</c> is the fact that says so, and it is the fact every
/// absence-assertion in SEC-09 depends on.
/// </para>
/// <para>
/// What that placement deliberately does <em>not</em> see: anything a component logs through a logger it built
/// itself from <see cref="ILoggerFactory"/> rather than through a resolved <c>ILogger&lt;T&gt;</c> — which in this
/// host is everything reaching Serilog's factory. Facts about the address a customer typed are therefore facts
/// about the application's own lines, and say so; the framework's lines are kept out of every sink by the
/// <c>Serilog:MinimumLevel:Override:Microsoft</c> setting in <c>appsettings.json</c>, which is a configuration
/// line rather than something this file proves.
/// </para>
/// </remarks>
public sealed class CollectingLogger
{
    private readonly ConcurrentQueue<LogLine> _lines = new();

    public sealed record LogLine(string Category, LogLevel Level, string Message);

    public IReadOnlyList<LogLine> Lines => _lines.ToArray();

    /// <summary>Lines whose rendered message carries <paramref name="marker"/>, oldest first.</summary>
    public IReadOnlyList<LogLine> Containing(string marker) =>
        Lines.Where(l => l.Message.Contains(marker, StringComparison.Ordinal)).ToList();

    public IReadOnlyList<LogLine> From(string category) => Lines.Where(l => l.Category == category).ToList();

    internal void Add(string category, LogLevel level, string message) =>
        _lines.Enqueue(new LogLine(category, level, message));
}

/// <summary>
/// The <see cref="ILogger{T}"/> the test host resolves for every category in the application. It records and
/// accepts everything it is handed: no level is filtered here, so the only reason a fact sees no line is that
/// nobody wrote one.
/// </summary>
public sealed class CollectingLogger<T> : ILogger<T>
{
    private readonly CollectingLogger _sink;
    private readonly string _category = typeof(T).FullName ?? typeof(T).Name;

    public CollectingLogger(CollectingLogger sink) => _sink = sink;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;

        _sink.Add(_category, logLevel, formatter(state, exception));
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }
}
