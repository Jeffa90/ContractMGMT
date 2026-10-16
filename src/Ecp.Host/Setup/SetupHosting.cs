using Ecp.Configuration;
using Ecp.Host.Security;
using Ecp.Setup;
using Ecp.SharedKernel;
using Ecp.Tenancy;
using Microsoft.AspNetCore.DataProtection;

namespace Ecp.Host.Setup;

/// <summary>Protects the bootstrap setup state with ASP.NET Data Protection. The key ring lives in the data directory, like the session keys.</summary>
internal sealed class DataProtectionSetupProtector(IDataProtectionProvider provider) : ISetupProtector
{
    private readonly IDataProtector _protector = provider.CreateProtector("Ecp.Setup.Bootstrap.v1");
    public string Protect(string plain) => _protector.Protect(plain);
    public string Unprotect(string protectedText) => _protector.Unprotect(protectedText);
}

/// <summary>Applies the synthetic demonstration data on request from the Setup wizard (Demo and Test only; the seeder itself refuses Production).</summary>
internal sealed class DemoDataApplier(DemoSeeder seeder) : IDemoDataApplier
{
    public Task ApplyAsync(CancellationToken ct) => seeder.SeedAsync(ct);
}

/// <summary>
/// Closes the application while installation is incomplete and closes the setup pages once it is complete.
/// Incomplete: browsers are redirected to /Setup; the API answers 503 with a problem document; health, static files and the setup pages stay reachable.
/// Complete: every /Setup path is a 404 (the signed-in administrator's /Admin/Setup is a different, authorised page).
/// </summary>
public sealed class SetupGateMiddleware(RequestDelegate next)
{
    public const string SetupPath = "/Setup";
    public const string SessionCookie = "ecp.setup";

    public async Task InvokeAsync(HttpContext context, ISetupService setup)
    {
        var path = context.Request.Path;
        var isSetup = path.StartsWithSegments(SetupPath, StringComparison.OrdinalIgnoreCase);
        var complete = await setup.IsCompleteAsync(context.RequestAborted);
        if (complete)
        {
            if (isSetup) { context.Response.StatusCode = StatusCodes.Status404NotFound; return; }
            await next(context);
            return;
        }
        if (isSetup || path.StartsWithSegments("/health") || path.StartsWithSegments("/css") || path.StartsWithSegments("/js") || path == "/favicon.ico"
            || path.StartsWithSegments("/api/v1/info") || path.StartsWithSegments("/Error") || path.StartsWithSegments("/Account/Challenge") || path.StartsWithSegments("/auth/callback"))
        { await next(context); return; }
        if (path.StartsWithSegments("/api"))
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.Headers.RetryAfter = "60";
            await context.Response.WriteAsJsonAsync(new { type = "about:blank", title = "Installation is not complete", status = 503, code = "setup_required" });
            return;
        }
        context.Response.Redirect(SetupPath);
    }
}

/// <summary>Branding for pages that may render before sign-in: the signed-in user's tenant, else the only tenant, else the platform default. Never throws.</summary>
public sealed class BrandingAccessor(IBrandingProvider branding, ITenantDirectory tenants, TenantContext tenant)
{
    public async Task<Branding> GetAsync(CancellationToken ct = default)
    {
        try
        {
            if (tenant.TenantId is not null) return await branding.GetAsync(ct);
            var all = await tenants.ListAsync(ct);
            if (all.Count != 1) return Branding.Default;
            using var _ = tenant.Use(all[0].Id);
            return await branding.GetAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Branding.Default; // the database may not exist yet (setup, or Production before migration)
        }
    }
}
