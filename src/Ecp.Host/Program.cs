using System.Reflection;
using System.Threading.RateLimiting;
using Ecp.Audit;
using Ecp.Contracts;
using Ecp.Documents;
using Ecp.Documents.Storage;
using Ecp.Host;
using Ecp.Migration;
using Ecp.Operations;
using Ecp.Persistence;
using Ecp.SharedKernel;
using Ecp.SharedKernel.Secrets;
using Ecp.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

var secrets = new EnvironmentAndFileSecretProvider();
var resolved = HostConfiguration.Resolve(builder.Configuration, secrets, builder.Environment.ContentRootPath);
Directory.CreateDirectory(resolved.Host.DataDirectory);

builder.Services.AddSingleton<ISecretProvider>(secrets);
builder.Services.AddSingleton(resolved.Host);
builder.Services.AddSingleton(resolved.Facts);
builder.Services.AddSingleton<DemoTenantCache>();
builder.Services.AddEcpPersistence(resolved.Database);
builder.Services.AddAuditModule();
builder.Services.AddTenancyModule();
builder.Services.AddContractsModule(o => builder.Configuration.GetSection("Contracts").Bind(o));
builder.Services.AddDocumentsModule(o => builder.Configuration.GetSection("Documents").Bind(o),
    new LocalFileSystemOptions { RootPath = Path.Combine(resolved.Host.DataDirectory, "documents") });
builder.Services.AddMigrationModule();
builder.Services.AddOperationsModule(resolved.Host.DataDirectory);
builder.Services.AddScoped<DemoSeeder>();

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ProblemExceptionHandler>();
builder.Services.AddAuthorization();
builder.Services.AddOpenApi();
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 600, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
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
        default:
            Console.Error.WriteLine("Unknown command. Use: migrate | verify-audit <tenant name>");
            return 64;
    }
}

if (resolved.Database.AutoMigrate && resolved.Host.Mode != EcpMode.Production)
    await app.Services.GetRequiredService<MigrationRunner>().RunAsync();
if (resolved.Host.DemoSeed && resolved.Host.Mode != EcpMode.Production)
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<DemoSeeder>().SeedAsync();
}

app.UseMiddleware<CorrelationMiddleware>();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseMiddleware<SecurityHeadersMiddleware>();
if (resolved.Host.Mode == EcpMode.Production) app.UseHsts();
app.UseRateLimiter();
if (resolved.Host.DemoTenantResolution) app.UseMiddleware<DemoTenantMiddleware>();
app.UseAuthorization();

var version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

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
app.MapGet("/", () => Results.Content("<!doctype html><html lang=\"en-AU\"><head><meta charset=\"utf-8\"><title>Enterprise Contract Platform</title></head><body><main><h1>Enterprise Contract Platform</h1><p>Walking skeleton. Health: /health/ready</p></main></body></html>", "text/html")).AllowAnonymous();
if (resolved.Host.Mode != EcpMode.Production) app.MapOpenApi("/api/v1/openapi.json").AllowAnonymous();

await app.RunAsync();
return 0;

/// <summary>Entry point type, public so integration tests can host the application.</summary>
public partial class Program;
