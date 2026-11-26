using Dapper;
using JbsContracts.Api.Infrastructure;

namespace JbsContracts.Api.Features;

public sealed record TryAsRequest(int? PersonId);

/// <summary>
/// QA only (AllowDemoData; never Production): an admin tries the app as another person, to test every role (D-81). It's remembered in a
/// cookie for the browser session; stopping clears it. Starting and stopping are audited, and everything done meanwhile carries
/// "[tried by ...]" in its audit and messages.
/// </summary>
public static class TryAsEndpoints
{
    public static void MapTryAsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/qa/people", async (CurrentUser current, Db db, AppSettings settings) =>
        {
            var (_, refusal) = await ApiResults.RequirePersonAsync(current, db);
            if (refusal is not null) return refusal;
            if (current.Real is not { IsAdmin: true } || !settings.AllowDemoData) return ApiResults.NotAllowed("Only admins on QA can try the app as someone else.");
            await using var c = await db.OpenAsync();
            var people = await c.QueryAsync<Person>("SELECT * FROM contracts.Person WHERE IsActive = 1 ORDER BY Name");
            return Results.Ok(people.Select(p => new
            {
                p.Id, p.Name, p.WindowsAccount, IsYou = p.Id == current.Real.Id,
                Roles = string.Join(", ", new[] { p.IsContractOwner ? "Contract owner" : null, p.IsLegal ? "Legal" : null, p.IsGroupHead ? "Group Head" : p.IsApprover ? "Approver" : null, p.IsProcurement ? "Procurement" : null, p.IsReports ? "Reports" : null, p.IsAdmin ? "Admin" : null, p.IsAuditor ? "Auditor" : null }.OfType<string>()),
            }));
        });

        app.MapPost("/api/qa/try-as", async (TryAsRequest body, HttpContext http, CurrentUser current, Db db, AuditLog audit, AppSettings settings) =>
        {
            var (me, refusal) = await ApiResults.RequirePersonAsync(current, db);
            if (refusal is not null) return refusal;
            var real = current.Real!;
            if (!real.IsAdmin || !settings.AllowDemoData) return ApiResults.NotAllowed("Only admins on QA can try the app as someone else.");
            await using var c = await db.OpenAsync();
            await using var tx = await Db.BeginWriteAsync(c);
            if (body.PersonId is null || body.PersonId == real.Id)
            {
                http.Response.Cookies.Delete(CurrentUser.TryAsCookie, new CookieOptions { Path = "/" });
                if (me!.TriedById is not null)
                    await audit.WriteAsync(c, tx, real.AsActor(), "qa.try-as-stopped", "Person", me.Id, $"{real.Name} stopped trying JBS Contracts as {me.Name.Replace($" [tried by {real.Name}]", "")}.");
                await tx.CommitAsync();
                return Results.Ok(new { tryingAs = (string?)null });
            }
            var target = await c.QuerySingleOrDefaultAsync<Person>("SELECT * FROM contracts.Person WHERE Id = @id AND IsActive = 1", new { id = body.PersonId }, tx);
            if (target is null) return ApiResults.Invalid("Choose an active person.", new() { ["personId"] = "Choose an active person." });
            http.Response.Cookies.Append(CurrentUser.TryAsCookie, target.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), new CookieOptions
            {
                HttpOnly = true, Secure = http.Request.IsHttps, SameSite = SameSiteMode.Strict, Path = "/", IsEssential = true,
            });
            await audit.WriteAsync(c, tx, real.AsActor(), "qa.try-as", "Person", target.Id, $"{real.Name} started trying JBS Contracts as {target.Name} (QA).");
            await tx.CommitAsync();
            return Results.Ok(new { tryingAs = target.Name });
        });
    }
}
