using System.Data.Common;
using Ecp.Operations;

namespace Ecp.Host;

/// <summary>
/// Maintenance mode (docs/MAINTENANCE_MODE.md): while the installation flag is on, a request that would change state is refused with 503 and a Retry-After. Reading stays possible, and so do health and readiness, sign-in and
/// sign-out, re-authentication and the Operations pages (which are how an operator turns maintenance off). The answer never says why the installation is in maintenance.
/// The check is on the path as the server normalised it; a path that only looks like an allowed one (for example with a doubled slash) does not match and is refused.
/// </summary>
public sealed class MaintenanceMiddleware(RequestDelegate next)
{
    private static readonly string[] AllowedPrefixes = ["/health", "/Admin/Operations", "/SignIn", "/SignOut", "/Reauth", "/Account/Challenge", "/auth/callback", "/Error"];

    public static bool IsAllowedDuringMaintenance(PathString path) => AllowedPrefixes.Any(p => path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase));

    public async Task InvokeAsync(HttpContext context, IMaintenanceService maintenance, ILogger<MaintenanceMiddleware> log)
    {
        var method = context.Request.Method;
        if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method) || IsAllowedDuringMaintenance(context.Request.Path)) { await next(context); return; }
        MaintenanceStatus status;
        try { status = await maintenance.GetAsync(context.RequestAborted); }
        catch (Exception ex) when (ex is DbException or InvalidOperationException)
        {
            // The flag cannot be read (for example the operational tables are not migrated yet). Readiness reports that; the request is not refused for a state that cannot be known.
            log.LogWarning("The maintenance flag could not be read ({Type}).", ex.GetType().Name);
            await next(context);
            return;
        }
        if (!status.Enabled) { await next(context); return; }
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.Headers.RetryAfter = "120";
        context.Response.Headers.CacheControl = "no-store";
        if (context.Request.Path.StartsWithSegments("/api"))
            await context.Response.WriteAsJsonAsync(new { type = "about:blank", title = "The system is in maintenance", status = 503, code = "maintenance_mode", correlationId = context.TraceIdentifier });
        else
        {
            context.Response.ContentType = "text/plain; charset=utf-8";
            await context.Response.WriteAsync("The system is in maintenance and changes are paused. Nothing you sent was saved. Please try again later.");
        }
    }
}
