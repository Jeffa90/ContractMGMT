using Dapper;
using JbsContracts.Api.Infrastructure;

namespace JbsContracts.Api.Features;

public static class SystemEndpoints
{
    public static void MapSystemEndpoints(this IEndpointRouteBuilder app)
    {
        // The deploy's smoke test. 200 only when the app is running AND can reach its database, so a broken connection fails the deploy.
        app.MapGet("/health/live", async (Db db, ILoggerFactory logs) =>
        {
            try
            {
                await using var connection = await db.OpenAsync();
                await connection.ExecuteScalarAsync<int>("SELECT 1");
                return Results.Text("Healthy", "text/plain");
            }
            catch (Exception ex)
            {
                logs.CreateLogger("Health").LogError(ex, "Health check could not reach the database.");
                return Results.Text("Unhealthy: the app can't reach its database. See the app log.", "text/plain", statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        }).AllowAnonymous();

        // IIS Application Initialization warms the app up here (Anonymous is allowed on /warmup only).
        app.MapGet("/warmup", () => Results.Text("OK", "text/plain")).AllowAnonymous();

        app.MapGet("/api/me", async (CurrentUser current, Db db, AppSettings settings) =>
        {
            var (person, refusal) = await ApiResults.RequirePersonAsync(current, db);
            if (refusal is not null) return refusal;
            return Results.Ok(new
            {
                person = PersonView.From(person!),
                defaultDomain = settings.DefaultDomain,
            });
        });
    }
}
