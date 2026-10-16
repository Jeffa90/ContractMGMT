using System.Text.RegularExpressions;
using Ecp.SharedKernel;
using Ecp.Tenancy;

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

/// <summary>
/// Demo/Test only: resolves the single demonstration tenant so the Demo runs without an identity provider. It is enabled only when
/// <c>Ecp:DemoTenantResolution</c> is true, which the Production guard refuses (R-ID-01). Real tenant resolution arrives with identity (M2).
/// </summary>
public sealed class DemoTenantMiddleware(RequestDelegate next, EcpHostOptions options, DemoTenantCache cache)
{
    public async Task InvokeAsync(HttpContext context, TenantContext tenant, ITenantDirectory directory, ActorContext actor)
    {
        var path = context.Request.Path;
        var needsTenant = !(path.StartsWithSegments("/health") || path.StartsWithSegments("/api/v1/openapi.json"));
        if (needsTenant)
        {
            var info = cache.Value ??= await directory.FindByNameAsync(options.DemoTenantName, context.RequestAborted);
            if (info is { Status: Ecp.Tenancy.Domain.TenantStatus.Active })
            {
                actor.ActorId = "demo:anonymous";
                actor.Type = ActorType.System;
                using (tenant.Use(info.Id)) { await next(context); }
                return;
            }
        }
        await next(context);
    }
}

public sealed class DemoTenantCache { public TenantInfo? Value { get; set; } }
