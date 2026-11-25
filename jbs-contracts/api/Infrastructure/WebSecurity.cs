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
        if (changes && request.Path.StartsWithSegments("/api"))
        {
            var site = request.Headers["Sec-Fetch-Site"].ToString();
            var crossSite = site.Length > 0 && site is not ("same-origin" or "none");
            if (crossSite || request.Headers[RequestHeader].ToString() != "1")
            {
                await ApiResults.Problem(StatusCodes.Status403Forbidden, "blocked",
                    "This change was blocked to keep your account safe. Refresh the page and try again.").ExecuteAsync(context);
                return;
            }
        }
        await next();
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
