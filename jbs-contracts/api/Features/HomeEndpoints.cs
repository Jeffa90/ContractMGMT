using System.Text.Json;
using Dapper;
using JbsContracts.Api.Infrastructure;

namespace JbsContracts.Api.Features;

public sealed record HomePreferences(string[]? HiddenTiles, string? FirstGroup);

/// <summary>
/// Home: each person's to-do tiles (shown only while something is waiting), the Urgent list, and "Choose what I see"
/// (product-spec section 12; D-63 to D-65). Also the Ctrl+K search. Tiles count only contracts the person can see.
/// </summary>
public static class HomeEndpoints
{
    /// <summary>A tile: who sees it, its group ("turn", "coming" or "admin") and the SQL condition for its contracts (alias k).</summary>
    public sealed record Tile(string Key, string Group, string Label, string Help, Func<Person, bool> For, string Sql);

    private const string Mine = "(k.OwnerId = @meId OR k.BackupOwnerId = @meId)";
    private const string NoticeDeadline = "DATEADD(DAY, -k.NoticePeriodDays, CASE WHEN k.IsEvergreen = 1 THEN k.ReviewDate ELSE k.EndDate END)";
    private const string LatestSentBack = """
        EXISTS (SELECT 1 FROM contracts.Approval a WHERE a.ContractId = k.Id AND a.Decision = N'SentBack' AND a.AtUtc >= DATEADD(SECOND, -5, k.StageSinceUtc)
                AND a.Id = (SELECT MAX(a2.Id) FROM contracts.Approval a2 WHERE a2.ContractId = k.Id))
        """;
    private const string NotInvolved = "(k.CreatedById <> @meId AND k.OwnerId <> @meId AND ISNULL(k.BackupOwnerId, 0) <> @meId)";

    public static readonly Tile[] Tiles =
    [
        // Contract owners: contracts they own or back up.
        new("back-from-legal", "turn", "Back from Legal", "Legal has sent these back to you", p => p.IsContractOwner,
            $"k.Stage = N'LegalReview' AND k.WhoseTurn = N'Owner' AND {Mine}"),
        new("sent-back", "turn", "Sent back from approval", "An approver sent these back with a reason", p => p.IsContractOwner,
            $"k.Stage = N'Signing' AND {Mine} AND {LatestSentBack}"),
        new("signed-copy-due", "turn", "Signed copy due", "In Signing: send for approval once signed", p => p.IsContractOwner,
            $"k.Stage = N'Signing' AND {Mine} AND NOT {LatestSentBack}"),
        new("drafts", "turn", "Your drafts", "Not sent on yet", p => p.IsContractOwner,
            $"k.Stage = N'Draft' AND {Mine}"),
        // Legal.
        new("legal-your-turn", "turn", "Your turn (Legal)", "Legal reviews you have, waiting on you", p => p.IsLegal,
            "k.Stage = N'LegalReview' AND k.WhoseTurn = N'Legal' AND k.LegalPersonId = @meId"),
        new("legal-new", "turn", "New to pick up", "Sent to Legal; nobody has taken them yet", p => p.IsLegal,
            "k.Stage = N'LegalReview' AND k.WhoseTurn = N'Legal' AND k.LegalPersonId IS NULL"),
        // Approvers.
        new("to-approve", "turn", "To approve", "Signed and waiting for your confirmation", p => p.IsApprover,
            $"k.Stage = N'Approval' AND {NotInvolved}"),
        // Coming up.
        new("notice-30", "coming", "Notice deadline in 30 days", "Your Effective contracts: the last day to give notice is close", p => p.IsContractOwner,
            $"k.Stage = N'Effective' AND {Mine} AND k.NoticePeriodDays IS NOT NULL AND {NoticeDeadline} BETWEEN @today AND DATEADD(DAY, 30, @today)"),
        new("ending-90", "coming", "Ending in 90 days", "Your Effective contracts with an end date soon", p => p.IsContractOwner,
            $"k.Stage = N'Effective' AND {Mine} AND k.EndDate BETWEEN @today AND DATEADD(DAY, 90, @today)"),
        new("legal-waiting-owner", "coming", "Waiting on the owner", "Your Legal reviews that are with the owner", p => p.IsLegal,
            "k.Stage = N'LegalReview' AND k.WhoseTurn = N'Owner' AND k.LegalPersonId = @meId"),
        // Admins.
        new("owner-inactive", "admin", "Owner no longer active", "Their owner was switched off or is no longer a contract owner: give them a new owner", p => p.IsAdmin,
            "k.Stage NOT IN (N'Withdrawn', N'Archived', N'Ended') AND NOT EXISTS (SELECT 1 FROM contracts.Person o WHERE o.Id = k.OwnerId AND o.IsActive = 1 AND o.IsContractOwner = 1)"),
    ];

    /// <summary>The SQL condition for a tile the person can use, for the Contracts list (tile=...). Null when it isn't theirs.</summary>
    public static string? TileSql(string? key, Person me) => Tiles.FirstOrDefault(t => t.Key == key && t.For(me))?.Sql;

    public static DateTime Today(TimeProvider clock) =>
        TimeZoneInfo.ConvertTime(clock.GetUtcNow(), TimeZoneInfo.FindSystemTimeZoneById("Australia/Brisbane")).Date;

    private sealed class UrgentRow
    {
        public int Id { get; set; }
        public string Reference { get; set; } = "";
        public string Title { get; set; } = "";
        public string? SupplierName { get; set; }
        public DateTime Deadline { get; set; }
    }

    private sealed class PreferenceRow
    {
        public string HiddenTiles { get; set; } = "[]";
        public string FirstGroup { get; set; } = "turn";
    }

    public static void MapHomeEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/home", async (CurrentUser current, Db db, TimeProvider clock) =>
        {
            var (me, refusal) = await ApiResults.RequirePersonAsync(current, db);
            if (refusal is not null) return refusal;
            var mine = Tiles.Where(t => t.For(me!)).ToList();
            await using var c = await db.OpenAsync();
            var parameters = ContractAccess.Params(me!, new { today = Today(clock) });
            var counts = new Dictionary<string, int>();
            if (mine.Count > 0)
            {
                // One pass over the contracts this person can see: a yes/no per tile for each contract, then a count per tile.
                // (SQL Server can't SUM over an EXISTS directly, so the flags are worked out first.)
                var row = (IDictionary<string, object>)await c.QuerySingleAsync($"""
                    SELECT {string.Join(", ", mine.Select((_, i) => $"ISNULL(SUM(t{i}), 0) AS t{i}"))}
                    FROM (SELECT {string.Join(", ", mine.Select((t, i) => $"CASE WHEN {t.Sql} THEN 1 ELSE 0 END AS t{i}"))}
                          FROM contracts.Contract k WHERE {ContractAccess.VisibleSql("k")}) flags
                    """, parameters);
                for (var i = 0; i < mine.Count; i++) counts[mine[i].Key] = row[$"t{i}"] is int n ? n : 0;
            }

            // Urgent: notice deadlines on their Effective contracts that have passed (while the contract hasn't ended) or are within 7 days.
            var urgent = me!.IsContractOwner
                ? (await c.QueryAsync<UrgentRow>($"""
                    SELECT k.Id, k.Reference, k.Title, s.Name AS SupplierName, {NoticeDeadline} AS Deadline
                    FROM contracts.Contract k LEFT JOIN contracts.Supplier s ON s.Id = k.SupplierId
                    WHERE {ContractAccess.VisibleSql("k")} AND k.Stage = N'Effective' AND {Mine} AND k.NoticePeriodDays IS NOT NULL
                      AND {NoticeDeadline} <= DATEADD(DAY, 7, @today) AND (k.IsEvergreen = 1 OR k.EndDate >= @today)
                    ORDER BY Deadline
                    """, parameters)).ToList()
                : [];

            var pref = await c.QuerySingleOrDefaultAsync<PreferenceRow>("SELECT HiddenTiles, FirstGroup FROM contracts.HomePreference WHERE PersonId = @id", new { id = me.Id })
                ?? new PreferenceRow();
            var today = DateOnly.FromDateTime(Today(clock));
            return Results.Ok(new
            {
                tiles = mine.Select(t => new { t.Key, t.Group, t.Label, t.Help, Count = counts.GetValueOrDefault(t.Key) }),
                urgent = urgent.Select(u =>
                {
                    var deadline = DateOnly.FromDateTime(u.Deadline);
                    return new { u.Id, u.Reference, u.Title, u.SupplierName, Deadline = deadline, Days = deadline.DayNumber - today.DayNumber };
                }),
                preferences = new { hiddenTiles = JsonSerializer.Deserialize<string[]>(pref.HiddenTiles) ?? [], firstGroup = pref.FirstGroup },
            });
        });

        app.MapPut("/api/home/preferences", async (HomePreferences body, CurrentUser current, Db db) =>
        {
            var (me, refusal) = await ApiResults.RequirePersonAsync(current, db);
            if (refusal is not null) return refusal;
            var known = Tiles.Select(t => t.Key).ToHashSet();
            var hidden = (body.HiddenTiles ?? []).Where(known.Contains).Distinct().ToArray();
            var first = body.FirstGroup is "coming" ? "coming" : "turn";
            await using var c = await db.OpenAsync();
            // Only this person's own view: not a change to any contract, so not audited.
            await c.ExecuteAsync("""
                MERGE contracts.HomePreference AS t USING (SELECT @id AS PersonId) AS s ON t.PersonId = s.PersonId
                WHEN MATCHED THEN UPDATE SET HiddenTiles = @hidden, FirstGroup = @first, UpdatedUtc = SYSUTCDATETIME()
                WHEN NOT MATCHED THEN INSERT (PersonId, HiddenTiles, FirstGroup) VALUES (@id, @hidden, @first);
                """, new { id = me!.Id, hidden = JsonSerializer.Serialize(hidden), first });
            return Results.Ok(new { hiddenTiles = hidden, firstGroup = first });
        });

        // Ctrl+K: contracts (that the person can see), suppliers, and people (admins only, as only admins have the People page).
        app.MapGet("/api/search", async (string? q, CurrentUser current, Db db) =>
        {
            var (me, refusal) = await ApiResults.RequirePersonAsync(current, db);
            if (refusal is not null) return refusal;
            var text = (q ?? "").Trim();
            if (text.Length == 0) return Results.Ok(new { contracts = Array.Empty<object>(), suppliers = Array.Empty<object>(), people = Array.Empty<object>() });
            if (text.Length > 100) text = text[..100];
            var like = $"%{text.Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]")}%";
            await using var c = await db.OpenAsync();
            var contracts = await c.QueryAsync($"""
                SELECT TOP (8) k.Id, k.Reference, k.Title, k.Stage, s.Name AS SupplierName FROM contracts.Contract k
                LEFT JOIN contracts.Supplier s ON s.Id = k.SupplierId
                WHERE {ContractAccess.VisibleSql("k")} AND (k.Reference LIKE @like OR k.Title LIKE @like OR s.Name LIKE @like OR k.SapOutlineAgreement LIKE @like)
                ORDER BY CASE WHEN k.Reference = @text THEN 0 WHEN k.Stage IN (N'Withdrawn', N'Archived') THEN 2 ELSE 1 END, k.UpdatedUtc DESC
                """, ContractAccess.Params(me!, new { like, text }));
            var suppliers = await c.QueryAsync("SELECT TOP (5) Id, Name FROM contracts.Supplier WHERE IsActive = 1 AND (Name LIKE @like OR VendorNumber LIKE @like) ORDER BY Name", new { like });
            var people = me!.IsAdmin
                ? await c.QueryAsync("SELECT TOP (5) Id, Name, WindowsAccount FROM contracts.Person WHERE Name LIKE @like OR WindowsAccount LIKE @like ORDER BY IsActive DESC, Name", new { like })
                : [];
            return Results.Ok(new
            {
                contracts = contracts.Select(k => new { k.Id, k.Reference, k.Title, k.Stage, StageName = ContractEndpoints.StageNames[(string)k.Stage], k.SupplierName }),
                suppliers = suppliers.Select(x => new { id = (int)x.Id, name = (string)x.Name }),
                people = people.Select(x => new { id = (int)x.Id, name = (string)x.Name, windowsAccount = (string)x.WindowsAccount }),
            });
        });
    }
}
