using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Flash.Shared.Http;

public static class CorrelationId
{
    public const string Header = "X-Correlation-Id";
}

/// <summary>Takes the caller's correlation id (or makes one), echoes it, and puts it on every log line of the request.</summary>
public sealed class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var incoming = context.Request.Headers[CorrelationId.Header].ToString();
        var id = IsAcceptable(incoming) ? incoming : Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
        context.Items[CorrelationId.Header] = id;
        context.Response.Headers[CorrelationId.Header] = id;
        using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = id }))
            await next(context);
    }

    /// <summary>Short, printable, no spaces: never echo arbitrary caller input into headers and logs.</summary>
    public static bool IsAcceptable(string value) =>
        value.Length is > 0 and <= 64 && value.All(c => char.IsLetterOrDigit(c) || c is '-' or '_');
}

public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        var headers = context.Response.Headers;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["X-Frame-Options"] = "DENY";
        headers["Referrer-Policy"] = "no-referrer";
        if (context.Request.Path.StartsWithSegments("/api"))
            headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";
        return next(context);
    }
}

public sealed class ExceptionMiddleware(RequestDelegate next, ILogger<ExceptionMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception ex)
        {
            if (context.Response.HasStarted)
                throw;

            var (status, title) = ex switch
            {
                DomainException d => (d.Status, d.Message),
                _ => (StatusCodes.Status500InternalServerError, "An error occurred while processing your request."),
            };
            if (status >= 500)
                logger.LogError(ex, "Unhandled exception processing {Method} {Path}", context.Request.Method, context.Request.Path);

            context.Response.StatusCode = status;
            context.Response.ContentType = "application/problem+json";
            var problem = new ProblemDetails { Status = status, Title = title };
            if (context.Items[CorrelationId.Header] is string id)
                problem.Extensions["correlationId"] = id;
            await context.Response.WriteAsJsonAsync(problem, options: null, contentType: "application/problem+json", cancellationToken: context.RequestAborted);
        }
    }
}
