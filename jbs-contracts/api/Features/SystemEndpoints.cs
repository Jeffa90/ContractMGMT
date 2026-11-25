using Dapper;
using JbsContracts.Api.Infrastructure;

namespace JbsContracts.Api.Features;

public sealed record ReminderDeliveryChoice(string? Delivery);

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
                demoData = person!.IsAdmin && settings.AllowDemoData ? await DemoData.IsLoadedAsync(db) ? "loaded" : "available" : null,
                // QA only: admins can try the app as someone else (D-81).
                canTryAs = current.Real is { IsAdmin: true } && settings.AllowDemoData,
                tryingAs = person.TriedById is null ? null : new { realName = person.TriedByName },
                // How their reminder emails come (D-99): their own choice, or the default.
                reminderDelivery = person.ReminderDelivery,
                defaultReminderDelivery = await ReminderAdminEndpoints.DefaultDeliveryAsync(db),
            });
        });

        // Each person chooses how their reminder emails come: as they happen, a daily summary or a weekly one; null goes back to the default.
        // Their own preference, not a change to any contract, so not audited (like Choose what I see).
        app.MapPut("/api/me/reminder-delivery", async (ReminderDeliveryChoice body, CurrentUser current, Db db) =>
        {
            var (person, refusal) = await ApiResults.RequirePersonAsync(current, db);
            if (refusal is not null) return refusal;
            if (body.Delivery is not (null or "Each" or "Daily" or "Weekly"))
                return ApiResults.Invalid("Choose how your reminders come.", new() { ["delivery"] = "Choose as they happen, daily or weekly." });
            await using var c = await db.OpenAsync();
            await c.ExecuteAsync("UPDATE contracts.Person SET ReminderDelivery = @delivery WHERE Id = @id", new { delivery = body.Delivery, id = person!.Id });
            return Results.Ok(new { delivery = body.Delivery });
        });
    }
}
