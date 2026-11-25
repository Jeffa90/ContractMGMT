using System.Globalization;
using Dapper;
using JbsContracts.Api.Infrastructure;
using Microsoft.Data.SqlClient;
using static JbsContracts.Api.Features.LegalEndpoints;

namespace JbsContracts.Api.Features;

/// <summary>
/// Signing, and the Group Head's confirmation that registers a contract (product-spec sections 4 and 6; D-59 to D-62).
/// The business approval and signatures happen in DocuSign; here the Group Head (or a backup approver) confirms the signed copy
/// and details are right, which makes the contract Effective and locks it. Every rule is checked on the server.
/// </summary>
public static class ApprovalEndpoints
{
    /// <summary>Nobody approves a contract they raised, own or back up.</summary>
    public static bool IsInvolved(Person me, ContractRow c) => c.CreatedById == me.Id || c.OwnerId == me.Id || c.BackupOwnerId == me.Id;

    private sealed class ApprovalRow
    {
        public string Decision { get; set; } = "";
        public string ByName { get; set; } = "";
        public string? AsBackupForName { get; set; }
        public DateTime AtUtc { get; set; }
        public string? Reason { get; set; }
    }

    private const string SelectApprovals = """
        SELECT a.Decision, p.Name AS ByName, gh.Name AS AsBackupForName, a.AtUtc, a.Reason
        FROM contracts.Approval a JOIN contracts.Person p ON p.Id = a.ById LEFT JOIN contracts.Person gh ON gh.Id = a.AsBackupFor
        WHERE a.ContractId = @id
        """;

    private static object View(ApprovalRow a) =>
        new { a.Decision, a.ByName, a.AsBackupForName, AtUtc = DateTime.SpecifyKind(a.AtUtc, DateTimeKind.Utc), a.Reason };

    /// <summary>Every decision on the contract, newest first.</summary>
    public static async Task<IEnumerable<object>> HistoryAsync(SqlConnection c, int id) =>
        (await c.QueryAsync<ApprovalRow>($"{SelectApprovals} ORDER BY a.Id DESC", new { id })).Select(View);

    /// <summary>While a contract is back in Signing because an approver sent it back, that decision (shown at the top of the page).</summary>
    public static async Task<object?> SentBackAsync(SqlConnection c, ContractRow r)
    {
        if (r.Stage != "Signing") return null;
        var last = await c.QuerySingleOrDefaultAsync<ApprovalRow>($"SELECT TOP (1) * FROM ({SelectApprovals}) x ORDER BY AtUtc DESC", new { id = r.Id });
        return last is { Decision: "SentBack" } && last.AtUtc >= r.StageSinceUtc.AddSeconds(-5) ? View(last) : null;
    }

    public static void MapApprovalEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/contracts/{id:int}");

        // Owner, in Signing: the signed copy is on the contract and the effective date is entered (required, never filled in for you: D-61).
        group.MapPost("/send-for-approval", (int id, LegalAction body, CurrentUser current, Db db, AuditLog audit, AppSettings settings) =>
            ActAsync(id, body, current, db, async x =>
            {
                var c = x.Contract;
                if (!x.Me.IsContractOwner) return ApiResults.NotAllowed("Only contract owners can send a contract for approval.");
                if (c.Stage != "Signing") return ApiResults.NotAllowed($"It isn't in Signing (it's in {ContractEndpoints.StageNames[c.Stage]}).");
                var problems = new Dictionary<string, string>();
                var signed = await x.C.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM contracts.Document WHERE ContractId = @id AND Kind = N'SignedCopy' AND IsRemoved = 0", new { id }, x.Tx);
                if (signed == 0) problems["signedCopy"] = "Add the signed copy first: under Documents, choose the kind Signed copy, then Add document.";
                if (body.EffectiveDate is not { } effective) problems["effectiveDate"] = "Enter the date the contract takes effect.";
                else if (c.EndDate is { } end && effective > DateOnly.FromDateTime(end)) problems["effectiveDate"] = $"The effective date is after the end date ({end.ToString("d MMM yyyy", CultureInfo.InvariantCulture)}).";
                if (problems.Count > 0) return ApiResults.Invalid(problems.Count == 2 ? "Add the signed copy and the effective date first." : problems.Values.First(), problems);

                var p = new DynamicParameters(new { me = x.Me.Id, id });
                p.Add("effective", body.EffectiveDate!.Value.ToDateTime(TimeOnly.MinValue), System.Data.DbType.Date);
                await x.C.ExecuteAsync("""
                    UPDATE contracts.Contract SET Stage = N'Approval', StageSinceUtc = SYSUTCDATETIME(), EffectiveDate = @effective, UpdatedUtc = SYSUTCDATETIME(), UpdatedById = @me
                    WHERE Id = @id
                    """, p, x.Tx);
                var date = body.EffectiveDate.Value.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
                await audit.WriteAsync(x.C, x.Tx, x.Me.AsActor(), "contract.sent-for-approval", "Contract", id,
                    $"{x.Me.Name} sent {c.Reference} for approval, effective {date}" + (c.EffectiveDate is { } was && DateOnly.FromDateTime(was) != body.EffectiveDate ? $" (was {was.ToString("d MMM yyyy", CultureInfo.InvariantCulture)})" : "") + ".",
                    new { effectiveDate = body.EffectiveDate }, contractId: id);

                // The Group Head only (D-60); everyone who can act if the Group Head can't, or there isn't one.
                var approvers = (await x.C.QueryAsync<Person>("SELECT * FROM contracts.Person WHERE IsActive = 1 AND IsApprover = 1", transaction: x.Tx))
                    .Where(a => !IsInvolved(a, c) && a.Id != x.Me.Id).ToList();
                var head = approvers.Where(a => a.IsGroupHead).ToList();
                var stamp = Convert.ToBase64String(c.RowVersion);
                foreach (var a in head.Count > 0 ? head : approvers)
                    await EmailOutbox.QueueAsync(x.C, x.Tx, "approval-waiting", a.Email, a.Name,
                        EmailTemplates.WaitingForApproval(First(a.Name), x.Me.Name, c.Reference, c.Title, ApproveLink(settings, id)),
                        personId: a.Id, contractId: id, dedupeKey: $"approval-waiting:{id}:{stamp}:{a.Id}");
                return null;
            }));

        // Approver: confirm, register and lock.
        group.MapPost("/approval/approve", (int id, LegalAction body, CurrentUser current, Db db, AuditLog audit, AppSettings settings) =>
            ActAsync(id, body, current, db, async x =>
            {
                var c = x.Contract;
                if (ApproverOnly(x) is { } refusal) return refusal;
                var groupHead = x.Me.IsGroupHead ? null : await x.C.QuerySingleOrDefaultAsync<Person>("SELECT * FROM contracts.Person WHERE IsGroupHead = 1 AND IsActive = 1", transaction: x.Tx);
                await x.C.ExecuteAsync("""
                    UPDATE contracts.Contract SET Stage = N'Effective', StageSinceUtc = SYSUTCDATETIME(), IsLocked = 1, RegisteredUtc = SYSUTCDATETIME(),
                        RegisteredById = @me, RegisteredAsBackup = @asBackup, UpdatedUtc = SYSUTCDATETIME(), UpdatedById = @me
                    WHERE Id = @id
                    """, new { me = x.Me.Id, asBackup = !x.Me.IsGroupHead, id }, x.Tx);
                var approvalId = await x.C.ExecuteScalarAsync<int>(
                    "INSERT INTO contracts.Approval (ContractId, Decision, ById, AsBackupFor) OUTPUT INSERTED.Id VALUES (@id, N'Approved', @me, @backupFor)",
                    new { id, me = x.Me.Id, backupFor = groupHead?.Id }, x.Tx);
                await audit.WriteAsync(x.C, x.Tx, x.Me.AsActor(), "contract.approved", "Contract", id,
                    $"{x.Me.Name} approved and registered {c.Reference}" + (groupHead is not null ? $" (as backup for {groupHead.Name})" : "") + ". It is Effective and locked.",
                    new { asBackupFor = groupHead?.Id }, contractId: id);
                await EmailOwnersAsync(x, $"approval:{id}:{approvalId}", "approved",
                    p => EmailTemplates.Approved(First(p.Name), x.Me.Name, c.Reference, c.Title, Link(settings, id)));
                return null;
            }));

        // Approver: something isn't right; back to Signing with the reason (D-62).
        group.MapPost("/approval/send-back", (int id, LegalAction body, CurrentUser current, Db db, AuditLog audit, AppSettings settings) =>
            ActAsync(id, body, current, db, async x =>
            {
                var c = x.Contract;
                if (ApproverOnly(x) is { } refusal) return refusal;
                var reason = Clean(body.Note);
                if (reason is null) return ApiResults.Invalid("Say what needs fixing.", new() { ["note"] = "Say what needs fixing, for example \"The signed copy is missing page 4.\"" });
                if (reason.Length > MaxNote) return TooLong("note", MaxNote);
                var groupHead = x.Me.IsGroupHead ? null : await x.C.QuerySingleOrDefaultAsync<Person>("SELECT * FROM contracts.Person WHERE IsGroupHead = 1 AND IsActive = 1", transaction: x.Tx);
                await x.C.ExecuteAsync("UPDATE contracts.Contract SET Stage = N'Signing', StageSinceUtc = SYSUTCDATETIME(), UpdatedUtc = SYSUTCDATETIME(), UpdatedById = @me WHERE Id = @id",
                    new { me = x.Me.Id, id }, x.Tx);
                var approvalId = await x.C.ExecuteScalarAsync<int>(
                    "INSERT INTO contracts.Approval (ContractId, Decision, ById, AsBackupFor, Reason) OUTPUT INSERTED.Id VALUES (@id, N'SentBack', @me, @backupFor, @reason)",
                    new { id, me = x.Me.Id, backupFor = groupHead?.Id, reason }, x.Tx);
                await audit.WriteAsync(x.C, x.Tx, x.Me.AsActor(), "contract.sent-back", "Contract", id,
                    $"{x.Me.Name} sent {c.Reference} back to Signing" + Quote(reason) + Stop(reason), new { reason }, contractId: id);
                await EmailOwnersAsync(x, $"approval:{id}:{approvalId}", "approval-sent-back",
                    p => EmailTemplates.SentBackByApprover(First(p.Name), x.Me.Name, c.Reference, c.Title, reason, Link(settings, id)));
                return null;
            }));

        // Owner, in Signing, when details must change: back to Legal (if Legal cleared it) or back to Draft (if it skipped Legal). Reason required.
        group.MapPost("/send-back-to-legal", (int id, LegalAction body, CurrentUser current, Db db, AuditLog audit, AppSettings settings) =>
            ActAsync(id, body, current, db, async x =>
            {
                var c = x.Contract;
                if (OwnerInSigning(x) is { } refusal) return refusal;
                if (c.LegalClearedUtc is null) return ApiResults.NotAllowed("This contract didn't go through Legal. Use Back to draft instead.");
                var reason = Clean(body.Note);
                if (reason is null) return ApiResults.Invalid("Say what needs to change.", new() { ["note"] = "Say what needs to change and why." });
                if (reason.Length > MaxNote) return TooLong("note", MaxNote);
                var status = c.LegalPersonId is null ? "NotStarted" : "Reviewing";
                await x.C.ExecuteAsync("""
                    UPDATE contracts.Contract SET Stage = N'LegalReview', StageSinceUtc = SYSUTCDATETIME(), WhoseTurn = N'Legal', TurnSinceUtc = SYSUTCDATETIME(),
                        LegalStatus = @status, LegalNote = NULL, LegalOutcome = NULL, LegalClearedUtc = NULL, LegalClearedById = NULL,
                        UpdatedUtc = SYSUTCDATETIME(), UpdatedById = @me
                    WHERE Id = @id
                    """, new { status, me = x.Me.Id, id }, x.Tx);
                var number = await AddEntryAsync(x, "SentToLegal", "Legal", c.LegalPersonId, status, reason, []);
                await audit.WriteAsync(x.C, x.Tx, x.Me.AsActor(), "contract.sent-back-to-legal", "Contract", id,
                    $"{x.Me.Name} sent {c.Reference} back from Signing to Legal" + Quote(reason) + Stop(reason), new { reason }, contractId: id);
                await EmailLegalAsync(x, number, reason, settings);
                return null;
            }));

        group.MapPost("/back-to-draft", (int id, LegalAction body, CurrentUser current, Db db, AuditLog audit) =>
            ActAsync(id, body, current, db, async x =>
            {
                var c = x.Contract;
                if (OwnerInSigning(x) is { } refusal) return refusal;
                if (c.LegalClearedUtc is not null) return ApiResults.NotAllowed("Legal cleared this contract, so changes go back through Legal. Use Send back to Legal.");
                var reason = Clean(body.Note);
                if (reason is null) return ApiResults.Invalid("Say what needs to change.", new() { ["note"] = "Say what needs to change and why." });
                if (reason.Length > MaxNote) return TooLong("note", MaxNote);
                await x.C.ExecuteAsync("UPDATE contracts.Contract SET Stage = N'Draft', StageSinceUtc = SYSUTCDATETIME(), UpdatedUtc = SYSUTCDATETIME(), UpdatedById = @me WHERE Id = @id",
                    new { me = x.Me.Id, id }, x.Tx);
                await audit.WriteAsync(x.C, x.Tx, x.Me.AsActor(), "contract.back-to-draft", "Contract", id,
                    $"{x.Me.Name} took {c.Reference} back to Draft from Signing" + Quote(reason) + Stop(reason), new { reason }, contractId: id);
                return null;
            }));
    }

    private static IResult? ApproverOnly(Ctx x) =>
        !x.Me.IsApprover ? ApiResults.NotAllowed("Only approvers can do that.")
        : x.Contract.Stage != "Approval" ? ApiResults.NotAllowed($"It isn't waiting for approval (it's in {ContractEndpoints.StageNames[x.Contract.Stage]}).")
        : IsInvolved(x.Me, x.Contract) ? ApiResults.NotAllowed("You can't approve this one because you raised it, own it or are its backup. Another approver can.")
        : null;

    private static IResult? OwnerInSigning(Ctx x) =>
        !x.Me.IsContractOwner ? ApiResults.NotAllowed("Only contract owners can do that.")
        : x.Contract.Stage != "Signing" ? ApiResults.NotAllowed($"It isn't in Signing (it's in {ContractEndpoints.StageNames[x.Contract.Stage]}).")
        : null;

    private static string? ApproveLink(AppSettings s, int id) => s.SiteUrl is null ? null : $"{s.SiteUrl}/contracts/{id}/approve";
}
