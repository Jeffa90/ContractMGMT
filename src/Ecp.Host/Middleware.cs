using System.Text.RegularExpressions;
using Ecp.SharedKernel;

namespace Ecp.Host;

/// <summary>Accepts a well-formed client correlation id or generates one; echoes it on the response; records it for audit.</summary>
public sealed partial class CorrelationMiddleware(RequestDelegate next)
{
    public const string Header = "X-Correlation-Id";

    public async Task InvokeAsync(HttpContext context, ActorContext actor)
    {
        var supplied = context.Request.Headers[Header].ToString();
        var id = Valid().IsMatch(supplied) ? supplied : Guid.NewGuid().ToString("N");
        actor.CorrelationId = id;
        actor.Source = "Web";
        context.Response.Headers[Header] = id;
        context.TraceIdentifier = id;
        await next(context);
    }

    [GeneratedRegex(@"^[A-Za-z0-9\-_.]{1,64}$", RegexOptions.NonBacktracking)]
    private static partial Regex Valid();
}

public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            var h = context.Response.Headers;
            h["X-Content-Type-Options"] = "nosniff";
            h["Referrer-Policy"] = "no-referrer";
            h["X-Frame-Options"] = "DENY";
            h["Cross-Origin-Resource-Policy"] = "same-origin";
            h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
            // No inline script or style anywhere: the policy needs no 'unsafe-inline'/'unsafe-eval'.
            h["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'";
            var path = context.Request.Path;
            if (path.StartsWithSegments("/api") || path.StartsWithSegments("/health"))
                h["Cache-Control"] = "no-store";
            return Task.CompletedTask;
        });
        return next(context);
    }
}
