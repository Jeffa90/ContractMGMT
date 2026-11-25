using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;

namespace JbsContracts.Api.Infrastructure;

public static class WebSecurity
{
    /// <summary>The header the front end sends with every change. Browsers can't add it to a request from another site.</summary>
    public const string RequestHeader = "X-JBS-Contracts";

    /// <summary>
    /// Browsers send Windows sign-in automatically, even on requests another website triggers. So every request that changes
    /// something must (1) carry <see cref="RequestHeader"/>, which a cross-site form or image can't add and which needs a CORS
    /// preflight the app never grants, and (2) not be marked cross-site by the browser (Sec-Fetch-Site).
    /// </summary>
    public static IApplicationBuilder UseRequestForgeryGuard(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        var request = context.Request;
        var changes = !(HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method) || HttpMethods.IsOptions(request.Method));
        if (request.Path.StartsWithSegments("/api"))
        {
            var site = request.Headers["Sec-Fetch-Site"].ToString();
            var crossSite = site.Length > 0 && site is not ("same-origin" or "none");
            // Reads too: another website must not start a download (the full export, the documents ZIP) with someone's Windows sign-in.
            // Typing the address or following a link from an email is "none", so that still works.
            if (crossSite || (changes && request.Headers[RequestHeader].ToString() != "1"))
            {
                await ApiResults.Problem(StatusCodes.Status403Forbidden, "blocked",
                    changes ? "This change was blocked to keep your account safe. Refresh the page and try again." : "This was blocked to keep your account safe. Open JBS Contracts and try again from there.").ExecuteAsync(context);
                return;
            }
        }
        await next();
    });

    /// <summary>
    /// If SQL Server cancels a request as a deadlock victim (error 1205, usually a page read meeting a change), run the request again,
    /// up to 3 tries. Safe because every change is one all-or-nothing transaction: a cancelled one leaves nothing behind.
    /// </summary>
    public static IApplicationBuilder UseDeadlockRetry(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        if (!context.Request.Path.StartsWithSegments("/api")) { await next(); return; }
        context.Request.EnableBuffering();
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await next();
                return;
            }
            catch (Exception ex) when ((ex is BusyException || ex is Microsoft.Data.SqlClient.SqlException { Number: -2 }) && !context.Response.HasStarted)
            {
                // Waited too long for another change to finish (or the database didn't answer in time): say so plainly; nothing was saved.
                context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Busy").LogWarning(ex, "Request {Path} timed out waiting for the database.", context.Request.Path);
                await ApiResults.Problem(StatusCodes.Status503ServiceUnavailable, "busy",
                    "JBS Contracts is busy finishing another change, such as a large import. Nothing was saved. Wait a minute and try again.").ExecuteAsync(context);
                return;
            }
            catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 1205 && attempt < 3 && !context.Response.HasStarted)
            {
                context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Deadlock").LogWarning("Request {Path} was a deadlock victim; trying again ({Attempt}).", context.Request.Path, attempt);
                context.Request.Body.Position = 0;
                await Task.Delay(25 * attempt);
            }
        }
    });

    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        context.Response.OnStarting(() =>
        {
            var h = context.Response.Headers;
            // Everything is served from this site: no CDNs, analytics or web fonts from elsewhere (JBS data stays on JBS servers).
            h.ContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; font-src 'self' data:; connect-src 'self'; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";
            h.XContentTypeOptions = "nosniff";
            h["Referrer-Policy"] = "same-origin";
            h.XFrameOptions = "DENY";
            h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
            if (context.Request.Path.StartsWithSegments("/api")) h.CacheControl = "no-store";
            return Task.CompletedTask;
        });
        await next();
    });

    /// <summary>
    /// Serves JBS's own fonts from the server's data folder (DataDirectory\fonts). They are never in the repository
    /// (decision D-34). When the folder is missing the front end falls back to Inter.
    /// </summary>
    public static IApplicationBuilder UseBrandFonts(this IApplicationBuilder app, AppSettings settings)
    {
        if (settings.FontsDirectory is not { } folder || !Directory.Exists(folder)) return app;
        var types = new FileExtensionContentTypeProvider();
        types.Mappings.Clear();
        types.Mappings[".woff2"] = "font/woff2";
        types.Mappings[".woff"] = "font/woff";
        return app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(folder),
            RequestPath = "/fonts",
            ContentTypeProvider = types,
            OnPrepareResponse = c => c.Context.Response.Headers.CacheControl = "private, max-age=604800",
        });
    }
}
