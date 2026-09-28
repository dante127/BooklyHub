using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BooklyHub.Application.Common.Interfaces;
using BooklyHub.Application.Payments;

namespace BooklyHub.Api.Middlewares;

public class IdempotencyMiddleware
{
    private const string IdempotencyHeader = "Idempotency-Key";
    private readonly RequestDelegate _next;

    public IdempotencyMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, IIdempotencyService idempotencyService, ITenantContext tenantContext)
    {
        // Only apply idempotency to state-mutating requests (POST, PUT, PATCH, DELETE)
        if (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
        {
            await _next(context);
            return;
        }

        if (!context.Request.Headers.TryGetValue(IdempotencyHeader, out var idempotencyKey) ||
            string.IsNullOrWhiteSpace(idempotencyKey))
        {
            await _next(context);
            return;
        }

        // A key only means something inside the tenant that issued it: scoping the stored record by tenant
        // keeps one tenant's key from replaying another tenant's cached response, and requests without a
        // tenant share one bucket where the payload check below is the only thing standing between a caller
        // and someone else's response.
        var tenantScope = tenantContext.TenantId?.ToString("N") ?? "global";
        var scopedKey = $"{tenantScope}:{idempotencyKey.ToString().Trim()}";

        context.Request.EnableBuffering();
        var requestBody = await new StreamReader(context.Request.Body, leaveOpen: true).ReadToEndAsync(context.RequestAborted);
        context.Request.Body.Position = 0;

        // The hash is what a key promises to repeat. Without comparing it, the same key reused with a
        // different payload would silently return the old response and the new request would never run.
        var requestHash = ComputeHash($"{context.Request.Method}\n{context.Request.Path}\n{requestBody}");

        var existingEntry = await idempotencyService.GetEntryAsync(scopedKey, context.RequestAborted);
        if (existingEntry != null)
        {
            if (existingEntry.RequestHash != requestHash)
            {
                context.Response.ContentType = "application/problem+json";
                context.Response.StatusCode = StatusCodes.Status409Conflict;
                await context.Response.WriteAsync(JsonSerializer.Serialize(new
                {
                    title = "Idempotency Key Conflict",
                    status = StatusCodes.Status409Conflict,
                    detail = "This Idempotency-Key was already used for a different request.",
                    instance = context.Request.Path.Value
                }));
                return;
            }

            context.Response.ContentType = "application/json";
            context.Response.StatusCode = existingEntry.StatusCode;
            context.Response.Headers["X-Idempotent-Replay"] = "true";
            await context.Response.WriteAsync(existingEntry.ResponseBody);
            return;
        }

        // Capture response
        var originalBodyStream = context.Response.Body;
        using var responseBodyMemoryStream = new MemoryStream();
        context.Response.Body = responseBodyMemoryStream;

        try
        {
            await _next(context);

            responseBodyMemoryStream.Seek(0, SeekOrigin.Begin);
            var responseBody = await new StreamReader(responseBodyMemoryStream).ReadToEndAsync(context.RequestAborted);

            // Persist successful or client error responses (don't cache 500 server errors)
            if (context.Response.StatusCode < 500)
            {
                await idempotencyService.SaveEntryAsync(
                    scopedKey,
                    tenantContext.TenantId is { } tenantId && tenantId != Guid.Empty ? tenantId : null,
                    requestHash,
                    context.Response.StatusCode,
                    responseBody,
                    TimeSpan.FromHours(24),
                    context.RequestAborted);
            }

            responseBodyMemoryStream.Seek(0, SeekOrigin.Begin);
            await responseBodyMemoryStream.CopyToAsync(originalBodyStream);
        }
        finally
        {
            context.Response.Body = originalBodyStream;
        }
    }

    private static string ComputeHash(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes);
    }
}
