using System.Net;
using BooklyHub.Api.Middlewares;
using BooklyHub.Domain.Exceptions;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BooklyHub.IntegrationTests.Security;

/// <summary>
/// The two boundaries an HTTP round trip cannot reach from a test: a request the client abandons
/// mid-flight, and an exception raised after the response is already on the wire. Both are driven
/// against the middleware directly so the branch, not a side effect of it, is what fails a control.
/// </summary>
public class ErrorHandlingBoundaryTests
{
    private sealed class RecordingLogger : ILogger<ExceptionHandlingMiddleware>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception)));
    }

    /// <summary>Mirrors what Kestrel does to a handler that keeps writing after the first flush.</summary>
    private sealed class WriteAfterStartStream : Stream
    {
        public int WriteAttempts { get; private set; }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => 0;
        public override long Position { get; set; }

        public override void Flush() { }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            WriteAttempts++;
            throw new InvalidOperationException("The response has already started, the headers are not editable.");
        }

        public override void Write(byte[] buffer, int offset, int count) =>
            WriteAsync(buffer, offset, count).GetAwaiter().GetResult();

        public override long Seek(long offset, SeekOrigin origin) => 0;
        public override void SetLength(long value) { }
        public override int Read(byte[] buffer, int offset, int count) => 0;
    }

    private sealed class StartedResponseFeature : HttpResponseFeature
    {
        public override bool HasStarted => true;
    }

    private static async Task<(Exception? Thrown, RecordingLogger Logger)> RunAsync(
        DefaultHttpContext context, Func<Task> inner)
    {
        var logger = new RecordingLogger();
        var middleware = new ExceptionHandlingMiddleware(_ => inner(), logger);

        Exception? thrown = null;
        try
        {
            await middleware.InvokeAsync(context);
        }
        catch (Exception ex)
        {
            thrown = ex;
        }

        return (thrown, logger);
    }

    private static DefaultHttpContext AbortedContext(out CancellationTokenSource source)
    {
        source = new CancellationTokenSource();
        source.Cancel();

        return new DefaultHttpContext { RequestAborted = source.Token };
    }

    [Fact]
    public async Task ClientAbort_IsNotAnsweredAsAServerFailure()
    {
        var context = AbortedContext(out var cancelled);

        var (thrown, logger) = await RunAsync(context, () =>
            throw new OperationCanceledException("The operation was canceled.", cancelled.Token));

        thrown.Should().BeNull("an abandoned request must not escape the handler as an unhandled fault");
        context.Response.StatusCode.Should().Be(StatusCodes.Status499ClientClosedRequest,
            "the access log line is written from this status, and 499 keeps abandoned traffic out of the 5xx rate");
        context.Response.ContentType.Should().BeNull("there is nobody left to read a problem document");
        logger.Entries.Should().ContainSingle()
            .Which.Level.Should().Be(LogLevel.Warning,
                "a closed browser tab is not a server error and must not page anyone");
    }

    [Fact]
    public async Task CancellationTheClientDidNotCause_StillMapsToAServerError()
    {
        var context = new DefaultHttpContext { RequestAborted = CancellationToken.None };
        using var internalDeadline = new CancellationTokenSource();
        internalDeadline.Cancel();

        var (thrown, logger) = await RunAsync(context, () =>
            throw new OperationCanceledException("an internal timeout", internalDeadline.Token));

        thrown.Should().BeNull();
        context.Response.StatusCode.Should().Be((int)HttpStatusCode.InternalServerError,
            "a cancellation nobody at the client asked for is still a server fault and must be reported as one");
        logger.Entries.Should().ContainSingle().Which.Level.Should().Be(LogLevel.Error);
    }

    [Fact]
    public async Task FailureAfterTheResponseStarted_WritesNothingMoreButIsStillLogged()
    {
        var body = new WriteAfterStartStream();
        var context = new DefaultHttpContext();

        // Response.Body is served by IHttpResponseBodyFeature, not by IHttpResponseFeature, so a fixture
        // that replaces only the first silently keeps writing to the default null stream and the test
        // passes whether the guard is there or not. Both halves are set, and both are asserted.
        context.Features.Set<IHttpResponseFeature>(new StartedResponseFeature());
        context.Features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(body));

        context.Response.HasStarted.Should().BeTrue("the fixture has to present a started response");
        context.Response.Body.Should().BeSameAs(body,
            "the write the guard is supposed to prevent has to be aimed at the stream that would fail it");

        var (thrown, logger) = await RunAsync(context, () =>
            throw new NotFoundException("Appointment was not found"));

        thrown.Should().BeNull("the handler must not throw a second failure on top of the first");
        body.WriteAttempts.Should().Be(0,
            "appending a problem document to bytes the client is already parsing corrupts the first response");
        logger.Entries.Should().ContainSingle().Which.Level.Should().Be(LogLevel.Error,
            "truncating the response is still a server fault and losing the log line would hide it");
    }

    [Fact]
    public async Task OrdinaryFailureOnAFreshResponse_StillWritesTheEnvelope()
    {
        var body = new MemoryStream();
        var context = new DefaultHttpContext();
        context.Response.Body = body;

        var (thrown, _) = await RunAsync(context, () => throw new NotFoundException("Appointment was not found"));

        thrown.Should().BeNull();
        context.Response.StatusCode.Should().Be((int)HttpStatusCode.NotFound);
        body.Position = 0;
        (await new StreamReader(body).ReadToEndAsync()).Should().Contain("Resource Not Found",
            "the started-response guard must stay narrow enough to leave the normal path writing");
    }
}
