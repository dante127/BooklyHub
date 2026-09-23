using System.Security.Cryptography;
using System.Text;
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

    public async Task InvokeAsync(HttpContext context, IIdempotencyService idempotencyService)
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

        var key = idempotencyKey.ToString().Trim();

        // Check if existing entry exists
        var existingEntry = await idempotencyService.GetEntryAsync(key, context.RequestAborted);
        if (existingEntry != null)
        {
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
                var requestHash = ComputeHash($"{context.Request.Method}:{context.Request.Path}");
                await idempotencyService.SaveEntryAsync(
                    key,
                    null,
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
