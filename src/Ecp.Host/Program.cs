using System.Reflection;
using System.Threading.RateLimiting;
using Ecp.Audit;
using Ecp.Configuration;
using Ecp.Contracts;
using Ecp.DataMigration;
using Ecp.Lifecycle;
using Ecp.Notifications;
using Ecp.Documents;
using Ecp.Documents.Storage;
using Ecp.Host;
using Ecp.Host.Security;
using Ecp.Host.Setup;
using Ecp.Identity;
using Ecp.Operations;
using Ecp.Persistence;
using Ecp.Setup;
using Ecp.SharedKernel;
using Ecp.SharedKernel.Localisation;
using Ecp.SharedKernel.Secrets;
using Ecp.Tenancy;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

var secrets = new EnvironmentAndFileSecretProvider();
var resolved = HostConfiguration.Resolve(builder.Configuration, secrets, builder.Environment.ContentRootPath);
var production = resolved.Host.Mode == EcpMode.Production;
Directory.CreateDirectory(resolved.Host.DataDirectory);

var localisation = new LocalisationOptions();
builder.Configuration.GetSection("Ecp:Localisation").Bind(localisation);
localisation.Validate();
builder.Services.AddSingleton(localisation);
builder.Services.AddLocalization(o => o.ResourcesPath = "Resources");

builder.Services.AddSingleton<ISecretProvider>(secrets);
builder.Services.AddSingleton(resolved.Host);
builder.Services.AddSingleton(resolved.Facts);
var demoIdentity = new DemoIdentityOptions();
builder.Configuration.GetSection("Identity:Demo").Bind(demoIdentity);
builder.Services.AddSingleton(demoIdentity);

builder.Services.AddEcpPersistence(resolved.Database);
builder.Services.AddAuditModule();
builder.Services.AddTenancyModule();
builder.Services.AddIdentityModule(o => { o.Mode = resolved.Host.Mode; o.RequireEmergencyConfirmation = production; });
builder.Services.AddConfigurationModule(o => o.RequireSecondPersonPublish = production);
builder.Services.AddContractsModule(o => builder.Configuration.GetSection("Contracts").Bind(o));
builder.Services.AddDocumentsModule(o => builder.Configuration.GetSection("Documents").Bind(o),
    new LocalFileSystemOptions { RootPath = Path.Combine(resolved.Host.DataDirectory, "documents") });
builder.Services.AddMigrationModule(o =>
{
    builder.Configuration.GetSection("Migration").Bind(o);
    if (production) o.RequireMalwareScan = true; // no scanner adapter exists yet, so Production holds attachments rather than importing them unscanned (MIG-F014)
});
// The mocked scanner stands in for a provider adapter in Demonstration and tests only; Production never gets it.
if (resolved.Host.Mode != EcpMode.Production) builder.Services.AddSingleton<Ecp.Documents.IMalwareScanner, Ecp.Documents.MockMalwareScanner>();
// The installation default solution pack applies only to an organisation that has none of its own; it is unset unless the installer sets it (docs/SETUP_AND_ONBOARDING.md).
var installationDefaultPack = builder.Configuration["Ecp:DefaultPackId"];
builder.Services.AddLifecycleModule(o => { o.Mode = resolved.Host.Mode; o.DefaultPackId = installationDefaultPack; });
builder.Services.AddNotificationsModule(o =>
{
    builder.Configuration.GetSection("Notifications").Bind(o);
    o.Mode = resolved.Host.Mode;
    o.DefaultPackId = installationDefaultPack;
});
builder.Services.AddOperationsModule(resolved.Host.DataDirectory);
builder.Services.AddHostedService<BackgroundJobService>();
builder.Services.AddSingleton<DemoSeeder>();
var hostVersion = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
builder.Services.AddSetupModule(new SetupEnvironment(resolved.Host.Mode, resolved.Host.DataDirectory,
    DemoIdentityEnabled: resolved.Host.Mode != EcpMode.Production && resolved.Host.DemoTenantResolution, DemoSeedEnabled: resolved.Host.DemoSeed,
    ConnectionFromSecretReference: resolved.Facts.ConnectionFromSecretReference, DatabaseProvider: resolved.Database.Provider.ToString(), Version: hostVersion,
    AllowRemoteSetup: builder.Configuration.GetValue("Ecp:Setup:AllowRemote", false)));
builder.Services.AddSingleton<ISetupProtector, DataProtectionSetupProtector>();
builder.Services.AddSingleton<IDemoDataApplier, DemoDataApplier>();
builder.Services.AddScoped<BrandingAccessor>();

// Keys for cookies, antiforgery and temp data live in the data directory (persistent volume) so sessions survive restarts (R-SEC-04).
var dataProtection = builder.Services.AddDataProtection().SetApplicationName("EnterpriseContractPlatform")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(resolved.Host.DataDirectory, "keyring")));
if (OperatingSystem.IsWindows()) dataProtection.ProtectKeysWithDpapi();

// Sessions: a signed cookie issued after a verified sign-in. Organisation identity providers register their own schemes at run time.
builder.Services.AddSingleton<SessionIssuer>();
builder.Services.AddSingleton<DemoIdentityService>();
builder.Services.AddSingleton<OidcSchemeManager>();
builder.Services.AddSingleton<OidcEvents>();
builder.Services.AddSingleton<IIdentityProviderListener, OidcSchemeListener>();
builder.Services.AddTransient<OpenIdConnectHandler>();
builder.Services.AddSingleton<IConfigureOptions<OpenIdConnectOptions>, OidcOptionsConfigurator>();
builder.Services.AddSingleton<IPostConfigureOptions<OpenIdConnectOptions>, OpenIdConnectPostConfigureOptions>();
builder.Services.AddAuthentication(EcpSchemes.Session).AddCookie(EcpSchemes.Session, o =>
{
    o.Cookie.Name = "ecp.session";
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Lax;
    o.Cookie.SecurePolicy = production ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
    o.ExpireTimeSpan = TimeSpan.FromHours(8);
    o.SlidingExpiration = true;
    o.LoginPath = "/SignIn";
    o.AccessDeniedPath = "/Error?code=403";
    o.ReturnUrlParameter = "returnUrl";
    o.Events.OnRedirectToLogin = ctx =>
    {
        if (ctx.Request.Path.StartsWithSegments("/api")) ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; else ctx.Response.Redirect(ctx.RedirectUri);
        return Task.CompletedTask;
    };
    o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
});
builder.Services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
builder.Services.AddScoped<IAuthorizationHandler, PermissionHandler>();
builder.Services.AddScoped<IAuthorizationHandler, RecentAuthenticationHandler>();
builder.Services.AddAuthorization(o =>
{
    // Every endpoint requires a signed-in user unless it opts out with [AllowAnonymous] (ARC-008).
    o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
    o.AddPolicy(Policies.RecentAuthentication, p => p.RequireAuthenticatedUser().AddRequirements(new RecentAuthenticationRequirement(TimeSpan.FromMinutes(10))));
});

builder.Services.AddAntiforgery(o =>
{
    o.Cookie.Name = "ecp.csrf";
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Strict;
    o.Cookie.SecurePolicy = production ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
    o.HeaderName = "X-CSRF-TOKEN";
});
builder.Services.AddRazorPages().AddViewLocalization();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ProblemExceptionHandler>();
builder.Services.AddOpenApi();
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 600, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    // Credential-style endpoints get a much tighter budget.
    o.AddPolicy("signin", ctx => RateLimitPartition.GetFixedWindowLimiter("signin:" + (ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown"),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

var app = builder.Build();

// Command-line operations: `migrate` and `verify-audit <tenant name>` run and exit (Production applies migrations explicitly).
// Option-style arguments (--urls, --applicationName from test hosts) are configuration, not commands.
if (args.Length > 0 && !args[0].StartsWith('-'))
{
    await using var scope = app.Services.CreateAsyncScope();
    switch (args[0])
    {
        case "migrate":
            await app.Services.GetRequiredService<MigrationRunner>().RunAsync();
            Console.WriteLine("Migrations applied.");
            return 0;
        case "verify-audit" when args.Length > 1:
            var dir = scope.ServiceProvider.GetRequiredService<ITenantDirectory>();
            var tenantInfo = await dir.FindByNameAsync(args[1]);
            if (tenantInfo is null) { Console.Error.WriteLine("Tenant not found."); return 1; }
            using (scope.ServiceProvider.GetRequiredService<TenantContext>().Use(tenantInfo.Id))
            {
                var result = await scope.ServiceProvider.GetRequiredService<AuditChainVerifier>().VerifyAsync();
                Console.WriteLine(result.IsValid ? $"Audit chain valid ({result.EventsChecked} events)." : $"Audit chain BROKEN at sequence {result.FirstBrokenSequence}: {result.Reason}");
                return result.IsValid ? 0 : 2;
            }
        case "setup-token":
            // Issues a single-use token for the setup wizard (24 hours). Printed to this terminal only; it is never written to a log or a file.
            try
            {
                var token = await app.Services.GetRequiredService<ISetupService>().IssueTokenAsync();
                Console.WriteLine("Setup token (single use, valid for 24 hours, shown once):");
                Console.WriteLine(token);
                return 0;
            }
            catch (DomainException ex) { Console.Error.WriteLine(ex.Message); return 1; }
        default:
            Console.Error.WriteLine("Unknown command. Use: migrate | verify-audit <tenant name> | setup-token");
            return 64;
    }
}

if (resolved.Database.AutoMigrate && resolved.Host.Mode != EcpMode.Production)
    await app.Services.GetRequiredService<MigrationRunner>().RunAsync();
var setupService = app.Services.GetRequiredService<ISetupService>();
if (resolved.Host.DemoSeed && resolved.Host.Mode != EcpMode.Production)
{
    await app.Services.GetRequiredService<DemoSeeder>().SeedAsync();
    await setupService.AutoCompleteForDemoAsync("synthetic demonstration data seeded at start-up");
}
else
{
    await setupService.EnsureInitialisedAsync();
    if (!await setupService.IsCompleteAsync())
        app.Logger.LogWarning("Installation is not complete. Run `setup-token` on the server to obtain a one-time token, then open /Setup.");
}
try { await app.Services.GetRequiredService<OidcSchemeManager>().ReloadAsync(app.Services); }
catch (Exception ex) when (ex is not OperationCanceledException)
{
    // The database may not be migrated yet (Production before `migrate`); readiness reports that. No provider is registered until it is.
    app.Logger.LogWarning("Identity providers were not loaded at start-up: {Reason}", ex.GetType().Name);
}

app.UseMiddleware<CorrelationMiddleware>();
// Error pages: status-code re-execution wraps the exception handler, so a handled exception with no body becomes an HTML error page for browsers.
app.UseWhen(c => !c.Request.Path.StartsWithSegments("/api") && !c.Request.Path.StartsWithSegments("/health"),
    b => b.UseStatusCodePagesWithReExecute("/Error", "?code={0}"));
// API and health callers get RFC 9457 problem details for bare status codes (401, 404, 429).
app.UseWhen(c => c.Request.Path.StartsWithSegments("/api") || c.Request.Path.StartsWithSegments("/health"), b => b.UseStatusCodePages());
app.UseExceptionHandler();
app.UseMiddleware<SecurityHeadersMiddleware>();
if (production) app.UseHsts();
app.UseStaticFiles();
app.UseMiddleware<SetupGateMiddleware>();
app.UseRequestLocalization(o =>
{
    o.SetDefaultCulture(localisation.DefaultCulture).AddSupportedCultures([.. localisation.SupportedCultures]).AddSupportedUICultures([.. localisation.SupportedCultures]);
    // Culture comes from the organisation default or an explicit cookie, not from browser Accept-Language (an en-US browser must not
    // silently switch an Australian organisation to month-first dates).
    o.RequestCultureProviders = [new Microsoft.AspNetCore.Localization.CookieRequestCultureProvider()];
    o.ApplyCurrentCultureToResponseHeaders = true;
});
app.UseRateLimiter();
app.UseRouting();
app.UseAuthentication();
app.UseMiddleware<SessionMiddleware>();
app.UseAuthorization();

var version = hostVersion;

app.MapGet("/health/live", () => Results.Json(new { status = "Live" })).AllowAnonymous();
app.MapGet("/health/startup", async (MigrationRunner runner) =>
{
    var status = await runner.GetStatusAsync();
    return status.IsCurrent ? Results.Json(new { status = "Started" }) : Results.Json(new { status = "Starting" }, statusCode: StatusCodes.Status503ServiceUnavailable);
}).AllowAnonymous();
app.MapGet("/health/ready", async (ReadinessService readiness, EcpHostOptions options) =>
{
    var report = await readiness.RunAsync();
    // Unauthenticated callers never receive dependency detail in Production.
    object body = options.Mode == EcpMode.Production
        ? new { status = report.Overall.ToString() }
        : new { status = report.Overall.ToString(), checks = report.Results.Select(r => new { id = r.Id, status = r.Status.ToString(), message = r.Message }) };
    return Results.Json(body, statusCode: report.Overall == ReadinessStatus.Unhealthy ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status200OK);
}).AllowAnonymous();
app.MapGet("/api/v1/info", (EcpHostOptions options) => Results.Json(new { name = "Enterprise Contract Platform", version, mode = options.Mode.ToString() })).AllowAnonymous();
if (resolved.Host.Mode != EcpMode.Production) app.MapOpenApi("/api/v1/openapi.json").AllowAnonymous();
// Accent colour from the published branding, served as a stylesheet because the content security policy forbids inline styles.
app.MapGet("/css/branding.css", async (BrandingAccessor brand, CancellationToken ct) =>
{
    var accent = (await brand.GetAsync(ct)).AccentColour;
    var safe = System.Text.RegularExpressions.Regex.IsMatch(accent, "^#[0-9a-fA-F]{6}$") ? accent : Ecp.Configuration.Branding.Default.AccentColour;
    return Results.Text($":root {{ --accent: {safe}; }}\n", "text/css");
}).AllowAnonymous();
app.MapRazorPages();

await app.RunAsync();
return 0;

/// <summary>Entry point type, public so integration tests can host the application.</summary>
public partial class Program;
