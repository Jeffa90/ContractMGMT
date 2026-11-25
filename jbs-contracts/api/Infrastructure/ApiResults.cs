using Dapper;

namespace JbsContracts.Api.Infrastructure;

/// <summary>Every error the API returns has a plain-words message saying what happened and what to do next.</summary>
public static class ApiResults
{
    public static IResult Problem(int status, string code, string message, object? extra = null) =>
        Results.Json(new { code, message, extra }, statusCode: status);

    public static IResult Invalid(string message, Dictionary<string, string> fields) =>
        Results.Json(new { code = "invalid", message, fields }, statusCode: StatusCodes.Status400BadRequest);

    public static IResult NotAllowed(string message) => Problem(StatusCodes.Status403Forbidden, "not-allowed", message);

    public static IResult NotFound(string message) => Problem(StatusCodes.Status404NotFound, "not-found", message);

    public static IResult Changed(string message) => Problem(StatusCodes.Status409Conflict, "changed", message);

    /// <summary>
    /// Resolves the signed-in person for an /api endpoint. Someone not added (or deactivated) gets "no access", with the names
    /// of the admins to ask; nothing else about the app is revealed.
    /// </summary>
    public static async Task<(Person? Person, IResult? Refusal)> RequirePersonAsync(CurrentUser current, Db db)
    {
        var person = await current.GetAsync();
        if (person is not null) return (person, null);
        if (current.Account is null)
            return (null, Problem(StatusCodes.Status401Unauthorized, "not-signed-in", "We couldn't tell who you are. Open JBS Contracts from a JBS computer signed in with your Windows account."));
        await using var connection = await db.OpenAsync();
        var admins = (await connection.QueryAsync<string>("SELECT Name FROM contracts.Person WHERE IsAdmin = 1 AND IsActive = 1 ORDER BY Name")).ToList();
        return (null, Problem(StatusCodes.Status403Forbidden, "no-access",
            "You don't have access to JBS Contracts yet. Ask your admin to add you.", new { account = current.Account, admins }));
    }
}
