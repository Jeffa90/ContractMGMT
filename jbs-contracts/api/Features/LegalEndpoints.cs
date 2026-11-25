using System.Text.Json;
using Dapper;
using JbsContracts.Api.Infrastructure;
using Microsoft.Data.SqlClient;

namespace JbsContracts.Api.Features;

/// <summary>The body of a contract action: the page's row version plus whatever that action uses.</summary>
public sealed record LegalAction(string? RowVersion, string? Note = null, int[]? DocumentVersionIds = null, int? PersonId = null, string? Status = null, DateOnly? EffectiveDate = null);

/// <summary>
/// Sending a contract on from Draft, and the Legal review back-and-forth (product-spec sections 4 and 5; D-09, D-55 to D-58).
/// Every action checks who may act, the stage and whose turn on the server, refuses a stale page (row version), writes a numbered
/// entry in the Legal conversation where it is part of the review, writes an audit event, and queues the emails in the same transaction.
/// </summary>
public static class LegalEndpoints
{
    public static readonly Dictionary<string, string> StatusNames = new()
    {
        ["NotStarted"] = "Not started", ["Reviewing"] = "Reviewing", ["WaitingOnSupplier"] = "Waiting on supplier", ["WaitingOnOwner"] = "Waiting on owner",
    };

    internal const int MaxNote = 2000;
    private const int MaxStatusNote = 500;

    // ---------------------------------------------------------------- What the contract page shows

    public static object View(ContractRow r) => new
    {
        r.WhoseTurn, r.LegalPersonId, r.LegalPersonName, Status = r.LegalStatus, StatusName = r.LegalStatus is { } s ? StatusNames[s] : null,
        Note = r.LegalNote, TurnSinceUtc = ContractEndpoints.Utc(r.TurnSinceUtc), Outcome = r.LegalOutcome, ClearedByName = r.LegalClearedByName,
        ClearedUtc = ContractEndpoints.Utc(r.LegalClearedUtc),
    };

    /// <summary>The actions this person can take on the contract now, by name (the page shows a button for each; the server checks again).</summary>
    public static List<string> Actions(Person me, ContractRow c)
    {
        var actions = new List<string>();
        if (c.IsLocked) return actions;
        if (c.Stage == "Draft" && me.IsContractOwner) actions.Add(c.NeedsLegalReview ? "sendToLegal" : "readyForSigning");
        if (c.Stage == "LegalReview")
        {
            if (c.WhoseTurn == "Owner" && me.IsContractOwner) actions.Add("sendToLegal");
            if (me.IsLegal)
            {
                if (c.WhoseTurn == "Legal")
                {
                    if (c.LegalPersonId != me.Id) actions.Add("take");
                    actions.AddRange(["sendToOwner", "clear", "updateStatus"]);
                }
                actions.Add("assign");
            }
        }
        if (c.Stage == "Signing" && me.IsContractOwner)
            actions.AddRange(["sendForApproval", c.LegalClearedUtc is not null ? "sendBackToLegal" : "backToDraft"]);
        if (c.Stage == "Approval" && me.IsApprover && !ApprovalEndpoints.IsInvolved(me, c)) actions.AddRange(["approve", "sendBack"]);
        return actions;
    }

    public sealed class Entry
    {
        public int Id { get; set; }
        public int Number { get; set; }
        public string Action { get; set; } = "";
        public string ByName { get; set; } = "";
        public DateTime AtUtc { get; set; }
        public string? WhoseTurn { get; set; }
        public string? LegalPersonName { get; set; }
        public string? Status { get; set; }
        public string? Note { get; set; }
    }

    private sealed class EntryDocument
    {
        public int LegalTurnId { get; set; }
        public int DocumentId { get; set; }
        public int VersionId { get; set; }
        public int VersionNumber { get; set; }
        public string Title { get; set; } = "";
        public string FileName { get; set; } = "";
    }

    /// <summary>The Legal conversation, oldest first, with the documents attached to each entry as they were then.</summary>
    public static async Task<IEnumerable<object>> ConversationAsync(SqlConnection c, int contractId)
    {
        var entries = (await c.QueryAsync<Entry>("""
            SELECT t.Id, t.Number, t.Action, by_.Name AS ByName, t.AtUtc, t.WhoseTurn, lp.Name AS LegalPersonName, t.Status, t.Note
            FROM contracts.LegalTurn t JOIN contracts.Person by_ ON by_.Id = t.ById LEFT JOIN contracts.Person lp ON lp.Id = t.LegalPersonId
            WHERE t.ContractId = @contractId ORDER BY t.Number
            """, new { contractId })).ToList();
        var documents = (await c.QueryAsync<EntryDocument>("""
            SELECT td.LegalTurnId, d.Id AS DocumentId, v.Id AS VersionId, v.VersionNumber, d.Title, v.FileName
            FROM contracts.LegalTurnDocument td JOIN contracts.LegalTurn t ON t.Id = td.LegalTurnId
            JOIN contracts.DocumentVersion v ON v.Id = td.DocumentVersionId JOIN contracts.Document d ON d.Id = v.DocumentId
            WHERE t.ContractId = @contractId
            """, new { contractId })).ToLookup(d => d.LegalTurnId);
        return entries.Select(e => (object)new
        {
            e.Number, e.Action, e.ByName, AtUtc = DateTime.SpecifyKind(e.AtUtc, DateTimeKind.Utc), e.WhoseTurn, e.LegalPersonName, e.Status,
            StatusName = e.Status is { } s ? StatusNames[s] : null, e.Note,
            Documents = documents[e.Id].Select(d => new { d.DocumentId, d.VersionId, d.VersionNumber, d.Title, d.FileName }),
        });
    }

    /// <summary>Required fields (Admin, Contract fields) that are still empty. Checked when a contract leaves Draft (D-17).</summary>
    public static async Task<List<(string Key, string Label)>> MissingRequiredAsync(SqlConnection c, SqlTransaction? tx, ContractRow r, List<FieldRow> definitions)
    {
        var required = definitions.Where(f => f.IsRequired && !f.IsHidden).ToList();
        var added = required.Where(f => !f.IsBuiltIn).ToList();
        var filled = added.Count == 0 ? [] : (await c.QueryAsync<int>(
            "SELECT FieldId FROM contracts.ContractFieldValue WHERE ContractId = @id", new { id = r.Id }, tx)).ToHashSet();
        var missing = new List<(string, string)>();
        foreach (var f in required)
        {
            var has = f.IsBuiltIn ? f.Key switch
            {
                "title" => !string.IsNullOrWhiteSpace(r.Title),
                "supplier" => r.SupplierId is not null,
                "category" => r.CategoryId is not null,
                "agreementType" => r.AgreementTypeId is not null,
                "description" => !string.IsNullOrWhiteSpace(r.Description),
                "backupOwner" => r.BackupOwnerId is not null,
                "businessUnit" => r.BusinessUnitId is not null,
                "site" => r.SiteId is not null,
                "value" => r.Value is not null,
                "sapOutlineAgreement" => !string.IsNullOrWhiteSpace(r.SapOutlineAgreement),
                "startDate" => r.StartDate is not null,
                "effectiveDate" => r.EffectiveDate is not null,
                "endDate" => r.EndDate is not null || r.IsEvergreen,
                "noticePeriodDays" => r.NoticePeriodDays is not null,
                "reviewDate" => r.ReviewDate is not null,
                _ => true, // owner is always set; yes/no fields always have a value
            } : filled.Contains(f.Id);
            if (!has) missing.Add((f.Key, f.Label));
        }
        return missing;
    }

    // ---------------------------------------------------------------- Actions

    public static void MapLegalEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/contracts/{id:int}");

        // Draft (needs Legal) or the owner's turn: hand it to Legal.
        group.MapPost("/send-to-legal", (int id, LegalAction body, CurrentUser current, Db db, AuditLog audit, AppSettings settings) =>
            ActAsync(id, body, current, db, async x =>
            {
                var c = x.Contract;
                if (!x.Me.IsContractOwner) return ApiResults.NotAllowed("Only contract owners can send a contract to Legal.");
                var fromDraft = c.Stage == "Draft";
                if (fromDraft && !c.NeedsLegalReview)
                    return ApiResults.NotAllowed("This contract doesn't need a Legal review (it's unticked). Use Ready for signing, or tick Needs Legal review first.");
                if (!fromDraft && c is not { Stage: "LegalReview", WhoseTurn: "Owner" })
                    return ApiResults.NotAllowed(c.Stage == "LegalReview" ? "It's already with Legal." : $"You can't send it to Legal while it's in {ContractEndpoints.StageNames[c.Stage]}.");
                var note = Clean(body.Note);
                if (note is { Length: > MaxNote }) return TooLong("note", MaxNote);
                if (fromDraft && await RequiredProblemAsync(x, "sending it to Legal") is { } missing) return missing;
                var docs = await CheckDocumentsAsync(x, body.DocumentVersionIds);
                if (docs.Problem is not null) return docs.Problem;

                var status = c.LegalPersonId is null ? "NotStarted" : "Reviewing";
                await x.C.ExecuteAsync($"""
                    UPDATE contracts.Contract SET Stage = N'LegalReview', {(fromDraft ? "StageSinceUtc = SYSUTCDATETIME()," : "")} WhoseTurn = N'Legal',
                        TurnSinceUtc = SYSUTCDATETIME(), LegalStatus = @status, LegalNote = NULL, UpdatedUtc = SYSUTCDATETIME(), UpdatedById = @me
                    WHERE Id = @id
                    """, new { status, me = x.Me.Id, id }, x.Tx);
                var number = await AddEntryAsync(x, "SentToLegal", "Legal", c.LegalPersonId, status, note, docs.Ids);
                await audit.WriteAsync(x.C, x.Tx, x.Me.AsActor(), "contract.sent-to-legal", "Contract", id,
                    $"{x.Me.Name} sent {c.Reference} to Legal" + (c.LegalPersonName is { } lp && !fromDraft ? $" ({lp})" : "") + Quote(note) + Stop(note),
                    new { note, documents = docs.Ids }, contractId: id);

                await EmailLegalAsync(x, number, note, settings);
                return null;
            }));

        // A draft that doesn't need Legal goes straight to Signing.
        group.MapPost("/ready-for-signing", (int id, LegalAction body, CurrentUser current, Db db, AuditLog audit) =>
            ActAsync(id, body, current, db, async x =>
            {
                var c = x.Contract;
                if (!x.Me.IsContractOwner) return ApiResults.NotAllowed("Only contract owners can send a contract on.");
                if (c.Stage != "Draft") return ApiResults.NotAllowed($"It's already in {ContractEndpoints.StageNames[c.Stage]}.");
                if (c.NeedsLegalReview) return ApiResults.NotAllowed("This contract needs a Legal review first. Use Send to Legal, or untick Needs Legal review if Legal has agreed.");
                if (await RequiredProblemAsync(x, "it goes to signing") is { } missing) return missing;
                await x.C.ExecuteAsync("UPDATE contracts.Contract SET Stage = N'Signing', StageSinceUtc = SYSUTCDATETIME(), UpdatedUtc = SYSUTCDATETIME(), UpdatedById = @me WHERE Id = @id",
                    new { me = x.Me.Id, id }, x.Tx);
                await audit.WriteAsync(x.C, x.Tx, x.Me.AsActor(), "contract.ready-for-signing", "Contract", id,
                    $"{x.Me.Name} sent {c.Reference} for signing without a Legal review (Needs Legal review was unticked).", contractId: id);
                return null;
            }));

        // Legal: "I'll take it".
        group.MapPost("/legal/take", (int id, LegalAction body, CurrentUser current, Db db, AuditLog audit) =>
            ActAsync(id, body, current, db, async x =>
            {
                var c = x.Contract;
                if (LegalOnly(x) is { } refusal) return refusal;
                if (c.WhoseTurn != "Legal") return ApiResults.NotAllowed("It's the owner's turn. You can take it when it comes back to Legal.");
                if (c.LegalPersonId == x.Me.Id) return ApiResults.NotAllowed("You already have it.");
                await SetLegalPersonAsync(x, x.Me.Id, "Reviewing");
                await AddEntryAsync(x, "Taken", "Legal", x.Me.Id, "Reviewing", null, []);
                await audit.WriteAsync(x.C, x.Tx, x.Me.AsActor(), "legal.taken", "Contract", id,
                    $"{x.Me.Name} took the Legal review of {c.Reference}" + (c.LegalPersonName is { } before ? $" over from {before}" : "") + ".", contractId: id);
                return null;
            }));

        // Legal: give it to a Legal colleague (or yourself).
        group.MapPost("/legal/assign", (int id, LegalAction body, CurrentUser current, Db db, AuditLog audit, AppSettings settings) =>
            ActAsync(id, body, current, db, async x =>
            {
                var c = x.Contract;
                if (LegalOnly(x) is { } refusal) return refusal;
                var person = body.PersonId is { } pid
                    ? await x.C.QuerySingleOrDefaultAsync<Person>("SELECT * FROM contracts.Person WHERE Id = @pid AND IsActive = 1 AND IsLegal = 1", new { pid }, x.Tx)
                    : null;
                if (person is null) return ApiResults.Invalid("Choose someone in Legal.", new() { ["personId"] = "Choose someone in Legal." });
                if (person.Id == c.LegalPersonId) return ApiResults.Invalid($"{person.Name} already has it.", new() { ["personId"] = $"{person.Name} already has it." });
                var status = c.WhoseTurn == "Legal" ? "Reviewing" : c.LegalStatus;
                await SetLegalPersonAsync(x, person.Id, status);
                var number = await AddEntryAsync(x, "Assigned", c.WhoseTurn, person.Id, status, null, []);
                await audit.WriteAsync(x.C, x.Tx, x.Me.AsActor(), "legal.assigned", "Contract", id,
                    person.Id == x.Me.Id ? $"{x.Me.Name} took the Legal review of {c.Reference}." : $"{x.Me.Name} gave the Legal review of {c.Reference} to {person.Name}.",
                    new { personId = person.Id }, contractId: id);
                if (person.Id != x.Me.Id)
                    await EmailOutbox.QueueAsync(x.C, x.Tx, "legal-assigned", person.Email, person.Name,
                        EmailTemplates.LegalAssigned(First(person.Name), x.Me.Name, c.Reference, c.Title, c.WhoseTurn == "Legal", Link(settings, id)),
                        personId: person.Id, contractId: id, dedupeKey: $"legal:{id}:{number}:{person.Id}");
                return null;
            }));

        // Legal, on Legal's turn: where it's up to (Reviewing or Waiting on supplier), with a short note. Doesn't change whose turn.
        group.MapPost("/legal/status", (int id, LegalAction body, CurrentUser current, Db db, AuditLog audit) =>
            ActAsync(id, body, current, db, async x =>
            {
                var c = x.Contract;
                if (LegalOnly(x) is { } refusal) return refusal;
                if (c.WhoseTurn != "Legal") return ApiResults.NotAllowed("It's the owner's turn, so its status is Waiting on owner.");
                if (body.Status is not ("Reviewing" or "WaitingOnSupplier"))
                    return ApiResults.Invalid("Choose a status.", new() { ["status"] = "Choose Reviewing or Waiting on supplier." });
                var note = Clean(body.Note);
                if (note is { Length: > MaxStatusNote }) return TooLong("note", MaxStatusNote);
                var legalPersonId = c.LegalPersonId ?? x.Me.Id; // updating it means you're working on it
                await x.C.ExecuteAsync("""
                    UPDATE contracts.Contract SET LegalPersonId = @legalPersonId, LegalStatus = @status, LegalNote = @note, UpdatedUtc = SYSUTCDATETIME(), UpdatedById = @me
                    WHERE Id = @id
                    """, new { legalPersonId, status = body.Status, note, me = x.Me.Id, id }, x.Tx);
                await AddEntryAsync(x, "StatusChanged", "Legal", legalPersonId, body.Status, note, []);
                await audit.WriteAsync(x.C, x.Tx, x.Me.AsActor(), "legal.status", "Contract", id,
                    $"{x.Me.Name} set the Legal status of {c.Reference} to {StatusNames[body.Status]}" + Quote(note) + Stop(note), new { status = body.Status, note }, contractId: id);
                return null;
            }));

        // Legal, on Legal's turn: hand it to the owner, with a note saying what's needed.
        group.MapPost("/legal/send-to-owner", (int id, LegalAction body, CurrentUser current, Db db, AuditLog audit, AppSettings settings) =>
            ActAsync(id, body, current, db, async x =>
            {
                var c = x.Contract;
                if (LegalOnly(x) is { } refusal) return refusal;
                if (c.WhoseTurn != "Legal") return ApiResults.NotAllowed("It's already with the owner.");
                var note = Clean(body.Note);
                if (note is null) return ApiResults.Invalid("Say what the owner needs to do.", new() { ["note"] = "Say what's needed, for example \"Supplier won't accept clause 12, please negotiate\"." });
                if (note.Length > MaxNote) return TooLong("note", MaxNote);
                var docs = await CheckDocumentsAsync(x, body.DocumentVersionIds);
                if (docs.Problem is not null) return docs.Problem;
                var legalPersonId = c.LegalPersonId ?? x.Me.Id;
                await x.C.ExecuteAsync("""
                    UPDATE contracts.Contract SET WhoseTurn = N'Owner', TurnSinceUtc = SYSUTCDATETIME(), LegalPersonId = @legalPersonId, LegalStatus = N'WaitingOnOwner',
                        LegalNote = NULL, UpdatedUtc = SYSUTCDATETIME(), UpdatedById = @me
                    WHERE Id = @id
                    """, new { legalPersonId, me = x.Me.Id, id }, x.Tx);
                var number = await AddEntryAsync(x, "SentToOwner", "Owner", legalPersonId, "WaitingOnOwner", note, docs.Ids);
                await audit.WriteAsync(x.C, x.Tx, x.Me.AsActor(), "legal.sent-to-owner", "Contract", id,
                    $"{x.Me.Name} sent {c.Reference} back to the owner" + Quote(note) + Stop(note), new { note, documents = docs.Ids }, contractId: id);
                await EmailOwnersAsync(x, $"legal:{id}:{number}", "legal-back", p => EmailTemplates.BackFromLegal(First(p.Name), x.Me.Name, c.Reference, c.Title, note, Link(settings, id)));
                return null;
            }));

        // Legal, on Legal's turn: cleared, with an outcome note. The contract moves to Signing.
        group.MapPost("/legal/clear", (int id, LegalAction body, CurrentUser current, Db db, AuditLog audit, AppSettings settings) =>
            ActAsync(id, body, current, db, async x =>
            {
                var c = x.Contract;
                if (LegalOnly(x) is { } refusal) return refusal;
                if (c.WhoseTurn != "Legal") return ApiResults.NotAllowed("It's the owner's turn. It can be cleared once it's back with Legal.");
                var note = Clean(body.Note);
                if (note is null) return ApiResults.Invalid("Add a short outcome.", new() { ["note"] = "Add a short outcome, for example \"Cleared. Liability cap agreed at $2m.\" The approver sees it." });
                if (note.Length > MaxNote) return TooLong("note", MaxNote);
                var legalPersonId = c.LegalPersonId ?? x.Me.Id;
                await x.C.ExecuteAsync("""
                    UPDATE contracts.Contract SET Stage = N'Signing', StageSinceUtc = SYSUTCDATETIME(), WhoseTurn = NULL, TurnSinceUtc = NULL, LegalStatus = NULL,
                        LegalNote = NULL, LegalPersonId = @legalPersonId, LegalOutcome = @note, LegalClearedUtc = SYSUTCDATETIME(), LegalClearedById = @me,
                        UpdatedUtc = SYSUTCDATETIME(), UpdatedById = @me
                    WHERE Id = @id
                    """, new { legalPersonId, note, me = x.Me.Id, id }, x.Tx);
                var number = await AddEntryAsync(x, "Cleared", null, legalPersonId, null, note, []);
                await audit.WriteAsync(x.C, x.Tx, x.Me.AsActor(), "legal.cleared", "Contract", id,
                    $"{x.Me.Name} cleared {c.Reference} (Legal)" + Quote(note) + Stop(note) + " It moves to Signing.", new { note }, contractId: id);
                await EmailOwnersAsync(x, $"legal:{id}:{number}", "legal-cleared", p => EmailTemplates.LegalCleared(First(p.Name), x.Me.Name, c.Reference, c.Title, note, Link(settings, id)));
                return null;
            }));
    }

    // ---------------------------------------------------------------- Helpers

    internal sealed record Ctx(SqlConnection C, SqlTransaction Tx, Person Me, ContractRow Contract);

    /// <summary>Loads and locks the contract, checks it can be seen and the page isn't stale, runs the action, and commits when it succeeds.</summary>
    internal static async Task<IResult> ActAsync(int id, LegalAction body, CurrentUser current, Db db, Func<Ctx, Task<IResult?>> action)
    {
        var (me, refusal) = await ApiResults.RequirePersonAsync(current, db);
        if (refusal is not null) return refusal;
        await using var c = await db.OpenAsync();
        await using var tx = await Db.BeginWriteAsync(c);
        var contract = await ContractEndpoints.LoadForUpdateAsync(c, tx, id);
        if (contract is null || !ContractAccess.CanSee(me!, contract.IsConfidential, contract.OwnerId, contract.BackupOwnerId))
            return ApiResults.NotFound("We couldn't find that contract. It may have been removed, or you may not have access.");
        if (Convert.ToBase64String(contract.RowVersion) != body.RowVersion)
            return ApiResults.Changed("Someone else changed this contract a moment ago, so nothing was done. Reload it to see the latest, then try again.");
        var problem = await action(new Ctx(c, tx, me!, contract));
        if (problem is not null) return problem;
        await tx.CommitAsync();
        var rowVersion = await c.ExecuteScalarAsync<byte[]>("SELECT RowVersion FROM contracts.Contract WHERE Id = @id", new { id });
        return Results.Ok(new { id, rowVersion = Convert.ToBase64String(rowVersion!) });
    }

    private static IResult? LegalOnly(Ctx x) =>
        !x.Me.IsLegal ? ApiResults.NotAllowed("Only Legal can do that.")
        : x.Contract.Stage != "LegalReview" ? ApiResults.NotAllowed($"It isn't in Legal review (it's in {ContractEndpoints.StageNames[x.Contract.Stage]}).")
        : null;

    internal static async Task<IResult?> RequiredProblemAsync(Ctx x, string when)
    {
        var missing = await MissingRequiredAsync(x.C, x.Tx, x.Contract, await Fields.LoadAsync(x.C, x.Tx));
        if (missing.Count == 0) return null;
        var labels = missing.Select(m => m.Label).ToList();
        var list = labels.Count == 1 ? labels[0] : $"{string.Join(", ", labels[..^1])} and {labels[^1]}";
        return ApiResults.Invalid($"Fill in {list} before {when}.", missing.ToDictionary(m => $"required.{m.Key}", m => $"{m.Label} is required before {when}."));
    }

    /// <summary>Documents attached to a hand-over must be versions on this contract (uploaded first, then linked).</summary>
    private static async Task<(int[] Ids, IResult? Problem)> CheckDocumentsAsync(Ctx x, int[]? ids)
    {
        var distinct = (ids ?? []).Distinct().ToArray();
        if (distinct.Length == 0) return ([], null);
        if (distinct.Length > 20) return ([], ApiResults.Invalid("Attach up to 20 documents.", new() { ["documents"] = "Attach up to 20 documents." }));
        var found = await x.C.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM contracts.DocumentVersion v JOIN contracts.Document d ON d.Id = v.DocumentId
            WHERE d.ContractId = @contractId AND d.IsRemoved = 0 AND v.Id IN (SELECT value FROM OPENJSON(@ids))
            """, new { contractId = x.Contract.Id, ids = JsonSerializer.Serialize(distinct) }, x.Tx);
        return found == distinct.Length ? (distinct, null)
            : ([], ApiResults.Invalid("One of the attached documents isn't on this contract.", new() { ["documents"] = "Reload the page and attach the documents again." }));
    }

    private static async Task SetLegalPersonAsync(Ctx x, int personId, string? status) =>
        await x.C.ExecuteAsync("UPDATE contracts.Contract SET LegalPersonId = @personId, LegalStatus = @status, UpdatedUtc = SYSUTCDATETIME(), UpdatedById = @me WHERE Id = @id",
            new { personId, status, me = x.Me.Id, id = x.Contract.Id }, x.Tx);

    internal static async Task<int> AddEntryAsync(Ctx x, string action, string? whoseTurn, int? legalPersonId, string? status, string? note, int[] documentVersionIds)
    {
        var number = await x.C.ExecuteScalarAsync<int>("SELECT ISNULL(MAX(Number), 0) + 1 FROM contracts.LegalTurn WHERE ContractId = @id", new { id = x.Contract.Id }, x.Tx);
        var turnId = await x.C.ExecuteScalarAsync<int>("""
            INSERT INTO contracts.LegalTurn (ContractId, Number, Action, ById, WhoseTurn, LegalPersonId, Status, Note)
            OUTPUT INSERTED.Id VALUES (@contractId, @number, @action, @me, @whoseTurn, @legalPersonId, @status, @note)
            """, new { contractId = x.Contract.Id, number, action, me = x.Me.Id, whoseTurn, legalPersonId, status, note }, x.Tx);
        foreach (var versionId in documentVersionIds)
            await x.C.ExecuteAsync("INSERT INTO contracts.LegalTurnDocument (LegalTurnId, DocumentVersionId) VALUES (@turnId, @versionId)", new { turnId, versionId }, x.Tx);
        return number;
    }

    /// <summary>To the Legal person who has it, or to everyone in Legal while nobody has picked it up (D-57); never to whoever acted.</summary>
    internal static async Task EmailLegalAsync(Ctx x, int number, string? note, AppSettings settings)
    {
        var c = x.Contract;
        var recipients = await x.C.QueryAsync<Person>(
            "SELECT * FROM contracts.Person WHERE IsActive = 1 AND IsLegal = 1 AND (@legalPersonId IS NULL OR Id = @legalPersonId) AND Id <> @me",
            new { legalPersonId = c.LegalPersonId, me = x.Me.Id }, x.Tx);
        foreach (var p in recipients)
            await EmailOutbox.QueueAsync(x.C, x.Tx, "legal-sent", p.Email, p.Name,
                EmailTemplates.SentToLegal(First(p.Name), x.Me.Name, c.Reference, c.Title, note, isNew: c.LegalPersonId is null, Link(settings, c.Id)),
                personId: p.Id, contractId: c.Id, dedupeKey: $"legal:{c.Id}:{number}:{p.Id}");
    }

    /// <summary>The owner and backup (D-57), except whoever acted.</summary>
    internal static async Task EmailOwnersAsync(Ctx x, string dedupe, string kind, Func<Person, (string, string, string)> content)
    {
        var people = await x.C.QueryAsync<Person>("SELECT * FROM contracts.Person WHERE IsActive = 1 AND Id IN (@owner, @backup) AND Id <> @me",
            new { owner = x.Contract.OwnerId, backup = x.Contract.BackupOwnerId ?? 0, me = x.Me.Id }, x.Tx);
        foreach (var p in people)
            await EmailOutbox.QueueAsync(x.C, x.Tx, kind, p.Email, p.Name, content(p), personId: p.Id, contractId: x.Contract.Id, dedupeKey: $"{dedupe}:{p.Id}");
    }

    internal static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    internal static string Quote(string? note) => note is null ? "" : $": \"{(note.Length > 200 ? note[..200] + "…" : note)}\"";
    /// <summary>Ends the sentence, unless the quoted note already ended it (no doubled full stop).</summary>
    internal static string Stop(string? note) => note is not null && ".!?".Contains(note[^1]) ? "" : ".";
    internal static string First(string name) => name.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? name;
    internal static string? Link(AppSettings s, int id) => s.SiteUrl is null ? null : $"{s.SiteUrl}/contracts/{id}";
    internal static IResult TooLong(string field, int max) => ApiResults.Invalid("That note is too long.", new() { [field] = $"Keep it under {max:N0} characters." });
}
