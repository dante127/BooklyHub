using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using BooklyHub.Domain.Exceptions;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Serilog.Context;

namespace BooklyHub.Api.Middlewares;

public class CorrelationIdMiddleware
{
    public const string CorrelationIdHeader = "X-Correlation-Id";

    /// <summary>
    /// SEC-13: this value is echoed on the response, reflected in every problem body, and spliced raw into the log line
    /// by Serilog's output template, so it is only safe as far as it is boring. A caller that labels its own request
    /// gets that label back; a caller that sends something else gets a server-made id, not a censored version of its own.
    /// </summary>
    private static readonly Regex SuppliedId = new(@"\A[A-Za-z0-9._-]{1,64}\z", RegexOptions.Compiled);

    private readonly RequestDelegate _next;

    public CorrelationIdMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    /// <summary>
    /// The id this request will be logged and answered under. Replaces rather than strips: stripping unsafe characters
    /// can fold two different caller ids into one string, which is the opposite of what a correlation id is for, and a
    /// value that is nothing but unsafe characters strips to an empty bracket. Nothing is padded or trimmed either —
    /// the echoed id is either exactly what the caller sent or exactly a server-made one, never a third string that
    /// neither side recognises.
    /// </summary>
    public static string Resolve(string? supplied)
    {
        // \z, not $: $ matches just before a trailing newline, so a value ending in one would pass a rule whose whole
        // point is that the id cannot reshape the text it is embedded in.
        return supplied is not null && SuppliedId.IsMatch(supplied)
            ? supplied
            : Guid.NewGuid().ToString("N");
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = Resolve(context.Request.Headers[CorrelationIdHeader].FirstOrDefault());

        context.Response.Headers[CorrelationIdHeader] = correlationId;

        using (LogContext.PushProperty("CorrelationId", correlationId))
        {
            await _next(context);
        }
    }
}

public class ExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;

    public ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (OperationCanceledException ex) when (context.RequestAborted.IsCancellationRequested)
        {
            // The caller hung up, which is not a server fault: the old path answered it 500 and wrote
            // an ERROR with a stack trace, so a closed tab looked exactly like a broken endpoint. There
            // is usually nobody left to read a response, but the status still has to be recorded because
            // the access log line is written from it, and 499 keeps aborted traffic out of the 5xx rate.
            _logger.LogWarning(ex, "Request aborted by the client: {Message}", ex.Message);

            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = StatusCodes.Status499ClientClosedRequest;
            }
        }
        catch (Exception ex)
        {
            await HandleExceptionAsync(context, ex);
        }
    }

    private async Task HandleExceptionAsync(HttpContext context, Exception exception)
    {
        // BadHttpRequestException is the framework's own "this request was bad" type and it carries the status it
        // means, so a refusal of that kind is a client's mistake rather than a server fault: a Warning line and no
        // stack trace, the same way an aborted request is handled above. The downgrade is deliberately limited to
        // that type - a thrown NotFoundException or ValidationException still writes an ERROR here, and whether a
        // mapped 4xx should stop paging anyone is a separate decision, not part of this one.
        if (exception is BadHttpRequestException { StatusCode: < StatusCodes.Status500InternalServerError })
        {
            _logger.LogWarning("Request refused: {Message}", exception.Message);
        }
        else
        {
            _logger.LogError(exception, "Unhandled exception occurred: {Message}", exception.Message);
        }

        // Headers and the first bytes are already committed, so the status, the content type and a
        // problem document cannot be added to what the client is already parsing. Stop here and leave
        // the truncated response rather than corrupting it with a second one.
        if (context.Response.HasStarted)
        {
            return;
        }

        var (statusCode, title, detail, errors) = exception switch
        {
            ValidationException validationEx => (
                (int)HttpStatusCode.BadRequest,
                "Validation Error",
                "One or more validation failures occurred.",
                (IDictionary<string, string[]>?)validationEx.Errors
                    .GroupBy(e => e.PropertyName)
                    .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray())
            ),
            NotFoundException notFoundEx => (
                (int)HttpStatusCode.NotFound,
                "Resource Not Found",
                notFoundEx.Message,
                null
            ),
            BookingConflictException conflictEx => (
                (int)HttpStatusCode.Conflict,
                "Booking Conflict",
                conflictEx.Message,
                null
            ),
            InvalidStateTransitionException transitionEx => (
                (int)HttpStatusCode.UnprocessableEntity,
                "Invalid State Transition",
                transitionEx.Message,
                null
            ),
            BusinessRuleValidationException ruleEx => (
                (int)HttpStatusCode.UnprocessableEntity,
                "Business Rule Violation",
                ruleEx.Message,
                null
            ),
            CrossTenantAccessViolationException tenantEx => (
                (int)HttpStatusCode.Forbidden,
                "Access Denied",
                tenantEx.Message,
                null
            ),
            UnauthorizedAccessException => (
                (int)HttpStatusCode.Unauthorized,
                "Unauthorized",
                "You are not authorized to perform this operation.",
                null
            ),
            // The status comes from the exception, not from this switch: the framework raises the same type for a
            // body it cannot accept, and that one already knows whether it means 400 or 413.
            BadHttpRequestException badRequestEx => (
                badRequestEx.StatusCode,
                ReasonPhrases.GetReasonPhrase(badRequestEx.StatusCode),
                badRequestEx.Message,
                null
            ),
            _ => (
                (int)HttpStatusCode.InternalServerError,
                "Internal Server Error",
                "An unexpected server error occurred. Please contact support with your Correlation ID.",
                null
            )
        };

        context.Response.ContentType = "application/problem+json";
        context.Response.StatusCode = statusCode;

        var problemDetails = new ProblemDetails
        {
            Status = statusCode,
            Title = title,
            Detail = detail,
            Instance = context.Request.Path
        };

        if (context.Response.Headers.TryGetValue(CorrelationIdMiddleware.CorrelationIdHeader, out var corrId))
        {
            problemDetails.Extensions["correlationId"] = corrId.ToString();
        }

        if (errors != null)
        {
            problemDetails.Extensions["errors"] = errors;
        }

        if (exception is BusinessRuleValidationException ruleViolation)
        {
            // Without the rule name a client can only string-match the prose to tell "already paid" from
            // "you asked for more than is owed", which are different things to do about it.
            problemDetails.Extensions["rule"] = ruleViolation.RuleName;
        }

        var json = JsonSerializer.Serialize(problemDetails, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        });

        await context.Response.WriteAsync(json);
    }
}
