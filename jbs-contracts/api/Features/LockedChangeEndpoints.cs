using Dapper;
using JbsContracts.Api.Infrastructure;
using static JbsContracts.Api.Features.LegalEndpoints;

namespace JbsContracts.Api.Features;

public sealed record HandOverAction(string? RowVersion, int? OwnerId, int? BackupOwnerId);
public sealed record ConfidentialAction(string? RowVersion, bool IsConfidential, string? Note);
public sealed record AnyTimeFieldsAction(string? RowVersion, Dictionary<string, System.Text.Json.JsonElement>? Fields);
public sealed record UnlockAction(string? RowVersion, string? Note, int? Hours);

/// <summary>
/// What can change on a locked contract without a Variation (product-spec section 7; A-05, D-17, D-76 to D-79): handing it over, the
/// Confidential tick (admins, with a reason), added fields an admin marked "any time", and an admin's emergency unlock.
/// </summary>
public static class LockedChangeEndpoints
{
    public static List<string> Actions(Person me, ContractRow c, bool hasAnyTimeFields)
    {
        var actions = new List<string>();
        var open = c.Stage is not ("Withdrawn" or "Archived");
        if (me.IsContractOwner && open && c.IsLocked) actions.Add("handOver");
        if (me.IsAdmin && open && c.IsLocked) actions.Add("confidential");
        if (me.IsContractOwner && c.Stage == "Effective" && hasAnyTimeFields) actions.Add("anyTimeFields");
        if (me.IsAdmin && c.Stage is "Effective" or "Ended") actions.Add(c.UnlockId is null ? "unlock" : "closeUnlock");
        if (me.IsContractOwner && c.Stage == "Effective" && c.OpenVariationId is null) actions.Add("startVariation");
        return actions;
    }

    public static void MapLockedChangeEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/contracts/{id:int}");

        // Any contract owner hands a contract over: owner and backup, any time, even locked (A-05). The new people are emailed.
        group.MapPost("/hand-over", (int id, HandOverAction body, CurrentUser current, Db db, AuditLog audit, AppSettings settings) =>
            ActAsync(id, body.RowVersion, current, db, async x =>
            {
                var c = x.Contract;
                if (!x.Me.IsContractOwner) return ApiResults.NotAllowed("Only contract owners can hand a contract over.");
                if (c.Stage is "Withdrawn" or "Archived") return ApiResults.NotAllowed($"It's {ContractEndpoints.StageNames[c.Stage]}, so it can't be handed over.");
                var problems = new Dictionary<string, string>();
                async Task<Person?> OwnerAsync(int? pid) => pid is null ? null
                    : await x.C.QuerySingleOrDefaultAsync<Person>("SELECT * FROM contracts.Person WHERE Id = @pid AND IsActive = 1 AND IsContractOwner = 1", new { pid }, x.Tx);
                var owner = await OwnerAsync(body.OwnerId);
                if (owner is null) problems["ownerId"] = "Choose an active contract owner.";
                var backup = await OwnerAsync(body.BackupOwnerId);
                if (body.BackupOwnerId is not null && backup is null) problems["backupOwnerId"] = "The backup must be an active contract owner.";
                if (owner is not null && backup?.Id == owner.Id) problems["backupOwnerId"] = "The backup must be someone other than the owner.";
                if (problems.Count > 0) return ApiResults.Invalid(problems.Values.First(), problems);
                if (owner!.Id == c.OwnerId && backup?.Id == c.BackupOwnerId) return ApiResults.Invalid("Nothing has changed.", new() { ["ownerId"] = "Choose a different owner or backup." });
                await x.C.ExecuteAsync("UPDATE contracts.Contract SET OwnerId = @owner, BackupOwnerId = @backup, UpdatedUtc = SYSUTCDATETIME(), UpdatedById = @me WHERE Id = @id",
                    new { owner = owner.Id, backup = backup?.Id, me = x.Me.Id, id }, x.Tx);
                var said = new List<string>();
                if (owner.Id != c.OwnerId) said.Add($"owner from {c.OwnerName} to {owner.Name}");
                if (backup?.Id != c.BackupOwnerId) said.Add(backup is null ? $"cleared the backup ({c.BackupOwnerName})" : $"backup {(c.BackupOwnerName is null ? "set to" : $"from {c.BackupOwnerName} to")} {backup.Name}");
                await x.Audit("contract.handed-over", $"{x.Me.Name} handed over {c.Reference}: {string.Join("; ", said)}.",
                    new { owner = new { before = c.OwnerId, after = owner.Id }, backup = new { before = c.BackupOwnerId, after = backup?.Id } });
                var stamp = Convert.ToBase64String(c.RowVersion);
                foreach (var (p, role) in new[] { (owner, "owner"), (backup, "backup") })
                {
                    if (p is null || p.Id == x.Me.Id || (role == "owner" ? p.Id == c.OwnerId : p.Id == c.BackupOwnerId)) continue;
                    await EmailOutbox.QueueAsync(x.C, x.Tx, "handed-over", p.Email, p.Name,
                        await EmailWording.RenderAsync(x.C, x.Tx, "HandedOver", With(With(EmailWording.For(p.Name, c), "Handed over by", x.Me.Name), "Role", role), Link(settings, id)),
                        personId: p.Id, contractId: id, dedupeKey: $"handed-over:{id}:{stamp}:{p.Id}");
                }
                return null;
            }, audit));

        // Admins only, with a reason, on a locked contract (drafts change it on the form).
        group.MapPost("/confidential", (int id, ConfidentialAction body, CurrentUser current, Db db, AuditLog audit) =>
            ActAsync(id, body.RowVersion, current, db, async x =>
            {
                var c = x.Contract;
                if (!x.Me.IsAdmin) return ApiResults.NotAllowed("Only admins can change Confidential on a locked contract.");
                if (!c.IsLocked || c.Stage is "Withdrawn" or "Archived") return ApiResults.NotAllowed("Change Confidential on the contract's form instead.");
                var reason = Clean(body.Note);
                if (reason is null) return ApiResults.Invalid("Say why.", new() { ["note"] = "Say why it's changing." });
                if (reason.Length > 1000) return TooLong("note", 1000);
                if (body.IsConfidential == c.IsConfidential) return ApiResults.Invalid("Nothing has changed.", new() { ["isConfidential"] = "It's already set that way." });
                await x.C.ExecuteAsync("UPDATE contracts.Contract SET IsConfidential = @v, UpdatedUtc = SYSUTCDATETIME(), UpdatedById = @me WHERE Id = @id",
                    new { v = body.IsConfidential, me = x.Me.Id, id }, x.Tx);
                await x.Audit("contract.confidential", $"{x.Me.Name} {(body.IsConfidential ? "made" : "stopped")} {c.Reference} {(body.IsConfidential ? "Confidential" : "being Confidential")}" + Quote(reason) + Stop(reason),
                    new { before = c.IsConfidential, after = body.IsConfidential, reason });
                return null;
            }, audit));

        // Added fields an admin marked "any time" (D-17) change directly on an Effective contract.
        group.MapPost("/any-time-fields", (int id, AnyTimeFieldsAction body, CurrentUser current, Db db, AuditLog audit) =>
            ActAsync(id, body.RowVersion, current, db, async x =>
            {
                var c = x.Contract;
                if (!x.Me.IsContractOwner) return ApiResults.NotAllowed("Only contract owners can change it.");
                if (c.Stage != "Effective") return ApiResults.NotAllowed("Only Effective contracts use this; drafts change on the form.");
                var definitions = await Fields.LoadAsync(x.C, x.Tx);
                var allowed = definitions.Where(f => !f.IsBuiltIn && !f.IsHidden && f.AfterEffective == "AnyTime").Select(f => f.Key).ToHashSet();
                var wrong = (body.Fields ?? []).Keys.Where(k => !allowed.Contains(k)).ToList();
                if (wrong.Count > 0) return ApiResults.Invalid("Some of these can only change through a Variation.", wrong.ToDictionary(k => $"field.{k}", _ => "This changes through a Variation."));
                var (values, errors) = await Fields.ParseAsync(x.C, x.Tx, body.Fields, definitions);
                if (errors.Count > 0) return ApiResults.Invalid("Check the highlighted details.", errors);
                var changes = await Fields.SaveAsync(x.C, x.Tx, id, values, definitions);
                if (changes.Count > 0)
                {
                    await x.C.ExecuteAsync("UPDATE contracts.Contract SET UpdatedUtc = SYSUTCDATETIME(), UpdatedById = @me WHERE Id = @id", new { me = x.Me.Id, id }, x.Tx);
                    await x.Audit("contract.changed", $"{x.Me.Name} changed {string.Join("; ", changes)} (any-time fields).", new { added = changes });
                }
                return null;
            }, audit));

        // Admin emergency unlock: reason, up to 4 hours, a banner while open; only admins can edit meanwhile (D-79). Never changes the stage.
        group.MapPost("/unlock", (int id, UnlockAction body, CurrentUser current, Db db, AuditLog audit) =>
            ActAsync(id, body.RowVersion, current, db, async x =>
            {
                var c = x.Contract;
                if (!x.Me.IsAdmin) return ApiResults.NotAllowed("Only admins can unlock a contract.");
                if (c.Stage is not ("Effective" or "Ended")) return ApiResults.NotAllowed("Only Effective or Ended contracts are unlocked this way.");
                if (c.UnlockId is not null) return ApiResults.NotAllowed("It's already unlocked.");
                var reason = Clean(body.Note);
                var problems = new Dictionary<string, string>();
                if (reason is null) problems["note"] = "Say why it needs unlocking, for example \"Supplier name was mistyped at registration\".";
                else if (reason.Length > 1000) problems["note"] = "Keep it under 1,000 characters.";
                if (body.Hours is not (>= 1 and <= 4)) problems["hours"] = "Choose 1 to 4 hours.";
                if (problems.Count > 0) return ApiResults.Invalid(problems.Values.First(), problems);
                await x.C.ExecuteAsync("INSERT INTO contracts.Unlock (ContractId, ById, Reason, ExpiresUtc) VALUES (@id, @me, @reason, DATEADD(HOUR, @hours, SYSUTCDATETIME()))",
                    new { id, me = x.Me.Id, reason, hours = body.Hours }, x.Tx);
                await x.C.ExecuteAsync("UPDATE contracts.Contract SET UpdatedUtc = SYSUTCDATETIME() WHERE Id = @id", new { id }, x.Tx);
                await x.Audit("contract.unlocked", $"{x.Me.Name} unlocked {c.Reference} for up to {body.Hours} hour{(body.Hours == 1 ? "" : "s")}" + Quote(reason) + Stop(reason), new { reason, hours = body.Hours });
                return null;
            }, audit));

        group.MapPost("/unlock/close", (int id, UnlockAction body, CurrentUser current, Db db, AuditLog audit) =>
            ActAsync(id, body.RowVersion, current, db, async x =>
            {
                var c = x.Contract;
                if (!x.Me.IsAdmin) return ApiResults.NotAllowed("Only admins can lock a contract again.");
                if (c.UnlockId is null) return ApiResults.NotAllowed("It isn't unlocked.");
                await x.C.ExecuteAsync("UPDATE contracts.Unlock SET ClosedUtc = SYSUTCDATETIME(), ClosedById = @me WHERE Id = @unlock", new { me = x.Me.Id, unlock = c.UnlockId }, x.Tx);
                await x.C.ExecuteAsync("UPDATE contracts.Contract SET UpdatedUtc = SYSUTCDATETIME() WHERE Id = @id", new { id }, x.Tx);
                await x.Audit("contract.relocked", $"{x.Me.Name} locked {c.Reference} again.", new { unlock = c.UnlockId });
                return null;
            }, audit));
    }
}
