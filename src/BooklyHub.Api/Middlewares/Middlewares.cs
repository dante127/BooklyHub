using System.Net;
using System.Text.Json;
using BooklyHub.Domain.Exceptions;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using Serilog.Context;

namespace BooklyHub.Api.Middlewares;

public class CorrelationIdMiddleware
{
    private const string CorrelationIdHeader = "X-Correlation-Id";
    private readonly RequestDelegate _next;

    public CorrelationIdMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = context.Request.Headers[CorrelationIdHeader].FirstOrDefault() 
                            ?? Guid.NewGuid().ToString("N");

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
        catch (Exception ex)
        {
            await HandleExceptionAsync(context, ex);
        }
    }

    private async Task HandleExceptionAsync(HttpContext context, Exception exception)
    {
        _logger.LogError(exception, "Unhandled exception occurred: {Message}", exception.Message);

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

        if (context.Response.Headers.TryGetValue("X-Correlation-Id", out var corrId))
        {
            problemDetails.Extensions["correlationId"] = corrId.ToString();
        }

        if (errors != null)
        {
            problemDetails.Extensions["errors"] = errors;
        }

        var json = JsonSerializer.Serialize(problemDetails, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        });

        await context.Response.WriteAsync(json);
    }
}
