using JbsContracts.Api.Features;
using JbsContracts.Api.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
var settings = AppSettings.From(builder.Configuration, builder.Environment);

builder.AddAppLogging(settings);
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<Db>();
builder.Services.AddSingleton<AuditLog>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<CurrentUser>();
builder.Services.AddAppAuthentication(settings);

var app = builder.Build();

if (settings.ApplyDatabaseScripts)
{
    var ran = await DatabaseScripts.ApplyAsync(settings.ConnectionString);
    if (ran.Count > 0) app.Logger.LogInformation("Applied database scripts: {Scripts}", string.Join(", ", ran));
}
try
{
    await FirstAdmins.EnsureAsync(app.Services.GetRequiredService<Db>(), app.Services.GetRequiredService<AuditLog>(), settings, app.Logger);
}
catch (Exception ex)
{
    // Keep running so /health/live can report the problem (it fails the deploy's smoke test) and the log says why.
    app.Logger.LogError(ex, "Could not check the first admins at start-up (is the database reachable and are its scripts applied?).");
}

app.UseSecurityHeaders();
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    // index.html must always be fetched fresh so a new deploy shows straight away; hashed assets can be cached.
    OnPrepareResponse = c => c.Context.Response.Headers.CacheControl =
        c.File.Name == "index.html" ? "no-cache" : "public, max-age=31536000, immutable",
});
app.UseBrandFonts(settings);
app.UseAuthentication();
app.UseAuthorization();
app.UseRequestForgeryGuard();

app.MapSystemEndpoints();
app.MapPeopleEndpoints();
app.MapListsEndpoints();
app.MapSupplierEndpoints();
app.MapContractEndpoints();
app.MapDocumentEndpoints();
app.MapExcelEndpoints();

// Unknown /api paths are a plain 404; every other path is a screen of the front end.
app.Map("/api/{**rest}", () => ApiResults.NotFound("That isn't something JBS Contracts can do."));
app.MapFallbackToFile("index.html", new StaticFileOptions
{
    OnPrepareResponse = c => c.Context.Response.Headers.CacheControl = "no-cache",
});

app.Run();

public partial class Program;
