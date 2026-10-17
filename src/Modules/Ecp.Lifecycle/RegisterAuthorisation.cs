using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ecp.Approvals;
using Ecp.Approvals.Domain;
using Ecp.Approvals.Resolution;
using Ecp.Audit;
using Ecp.Contracts;
using Ecp.Documents;
using Ecp.Documents.Domain;
using Ecp.Identity.Domain;
using Ecp.Identity;
using Ecp.Identity.Access;
using Ecp.Lifecycle.Definitions;
using Ecp.Lifecycle.Domain;
using Ecp.Lifecycle.Persistence;
using Ecp.Persistence;
using Ecp.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Ecp.Lifecycle;

public sealed record EligibleApprover(Guid UserId, string DisplayName, string RoleCode, bool IsBackup);

/// <summary>What the approver relied on, frozen when the request was submitted. It never changes afterwards, whatever happens to the contract.</summary>
public sealed record RegisterSnapshot(
    DateTime CapturedUtc, string SubjectKind, string AgreementReference, string Title, string? VariationReference, string Status, string Classification, string SupplierName, string CategoryName,
    string? CurrencyCode, decimal? EstimatedValue, string? StartDate, string? EffectiveDate, string? ReviewDate, string? EndDate, string? ExtensionType, int? NoticePeriodDays, bool CriticalNotice,
    bool LegalReviewRequired, string? LegalStatus, int LegalRounds, string? LegalClearedBy, DateTime? LegalClearedUtc, string? ExecutionProvider, string? ProviderRequestId, string? ExecutionStatus,
    Guid? ExecutedDocumentId, string? ExecutedFileName, string? ExecutedSha256, string WorkflowRef, string ApproverGroupKey, string SubmittedBy, IReadOnlyList<EligibleApprover> EligibleApprovers,
    IReadOnlyDictionary<string, string?>? ProposedChanges, IReadOnlyDictionary<string, string?>? CurrentValues)
{
    /// <summary>
    /// Snapshots frozen before the provider-neutral vocabulary carry the provider's request identifier under its old key. They are never rewritten (they are evidence); this property lets them be read, and
    /// <see cref="WithLegacyNames"/> folds it into <see cref="ProviderRequestId"/>. New snapshots do not write it.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("envelopeId"), System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyRequestId { get; init; }

    public RegisterSnapshot WithLegacyNames() => ProviderRequestId is null && LegacyRequestId is not null ? this with { ProviderRequestId = LegacyRequestId, LegacyRequestId = null } : this;
}

public sealed record ApprovalDecisionInfo(Guid RequestId, string SubjectKind, ApprovalStatus Status, string SubmittedByName, DateTime SubmittedUtc, string? DecidedByName, string? DecidedAsRole,
    string? OnBehalfOfName, string? Comment, DateTime? DecidedUtc);

public sealed record ExecutedDocumentInfo(Guid DocumentId, string Title, string? FileName, string? Sha256, bool CanDownload);

public sealed record ApprovalQueueItem(
    Guid RequestId, string SubjectKind, Guid SubjectId, Guid ContractId, string AgreementReference, string Title, string? VariationReference, string SupplierName, string CategoryName, string Classification,
    string SubmittedByName, DateTime SubmittedUtc, int AgeDays, string ApproverGroupKey, bool CanDecide, bool IsSubmitter, bool ActingAsBackup);

public sealed record ApprovalDetail(
    Guid RequestId, string SubjectKind, Guid SubjectId, Guid ContractId, string AgreementReference, string Title, string? VariationReference, ApprovalStatus Status, string ApproverGroupKey, string SubmittedByName,
    DateTime SubmittedUtc, RegisterSnapshot Snapshot, string SnapshotSha256, bool SnapshotIntact, ExecutedDocumentInfo? ExecutedDocument, IReadOnlyList<EligibleApprover> Eligible, ApprovalDecisionInfo? Decision,
    IReadOnlyList<ApprovalDecisionInfo> History, bool CanApprove, bool CanReject, string? CannotBecause, PlanView? Plan = null, Guid OrganisationId = default);

/// <summary>
/// Register authorisation: the configured primary approver (or a configured backup) approves or rejects an executed agreement. Whoever responds first decides; the actor is the signed-in person, never a
/// name supplied by a request or a callback; the submitter cannot decide their own submission; and an approval stamps Registered Date and locks the record in the same transaction.
/// </summary>
public interface IRegisterAuthorisationService
{
    Task<Guid> SubmitAsync(string subjectKind, Guid subjectId, string? note, string? clientRequestId = null, CancellationToken ct = default);
    Task<PagedResult<ApprovalQueueItem>> PendingAsync(bool onlyActionable = true, int page = 1, int pageSize = 25, CancellationToken ct = default);
    Task<ApprovalDetail> GetAsync(Guid requestId, CancellationToken ct = default);
    Task<ApprovalDetail?> CurrentForSubjectAsync(string subjectKind, Guid subjectId, CancellationToken ct = default);
    Task<IReadOnlyList<ApprovalDecisionInfo>> HistoryAsync(Guid contractId, CancellationToken ct = default);
    Task ApproveAsync(Guid requestId, string? comment, string? clientRequestId = null, CancellationToken ct = default);
    Task RejectAsync(Guid requestId, string comment, string? clientRequestId = null, CancellationToken ct = default);
    /// <summary>The primary approvers and backups for a group, so everyone can see who may act.</summary>
    Task<IReadOnlyList<EligibleApprover>> EligibleApproversAsync(string approverGroupKey, CancellationToken ct = default);
}

internal sealed partial class RegisterAuthorisationService(
    LifecycleDbContext db, LifecycleEngine engine, IAuditWriter audit, ITenantContext tenant, IClock clock, CurrentUser currentUser, IAccessService access, IAccessDirectory directory,
    IContractLifecyclePort port, ISecuredContractService contracts, ISecuredContractDocuments secureDocuments, IDocumentQuery documentQuery, IVariationOutcomes variationOutcomes, LifecycleEvents events,
    VisibleContracts visible, IApprovalResolver approvalResolver, ApprovalFactsBuilder factsBuilder, IUnitOfWork uow, IContractCustomFieldReader customReader) : IRegisterAuthorisationService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<EligibleApprover>> EligibleApproversAsync(string approverGroupKey, CancellationToken ct = default)
        => (await directory.ApproversAsync(approverGroupKey, ct)).Select(a => new EligibleApprover(a.UserId, a.DisplayName, a.RoleCode, a.RoleCode == RoleCodes.BackupApprover)).OrderBy(a => a.IsBackup).ThenBy(a => a.DisplayName).ToList();

    // ---- submit ---------------------------------------------------------------------------------------------------------------------------------

    public async Task<Guid> SubmitAsync(string subjectKind, Guid subjectId, string? note, string? clientRequestId = null, CancellationToken ct = default)
    {
        var subject = await engine.LoadSubjectAsync(subjectKind, subjectId, ct);
        var resolved = await engine.DefinitionForAsync(subject, ct);
        var transition = resolved.Definition.From(subject.Status).FirstOrDefault(t => t.Role == "submitRegister")
            ?? throw new DomainException("invalid_transition", $"This {subject.Noun} cannot be submitted for [[registration.approval|lower]] while it is {resolved.Definition.DisplayOf(subject.Status)}.");
        var group = resolved.Definition.Settings.Approval.ApproverGroupKey;
        var me = currentUser.UserId ?? throw new ForbiddenException("A signed-in person is required.");
        IReadOnlyList<EligibleApprover> eligible;
        ApprovalResolution? resolution = null;
        if (resolved.Definition.Settings.Approval.Matrix is { } matrixCode)
        {
            // A workflow that names an approval matrix gets its steps and people from the matrix. If nobody can approve, the submission is blocked with the reasons (fail closed); nothing is defaulted.
            var facts = await factsBuilder.BuildAsync(subject, ct);
            resolution = await approvalResolver.ResolveAsync(new ResolutionRequest(subject.OrganisationId, matrixCode, facts, await engine.DescribeAsync(subject, null, ct), me, clock.UtcNow, MatrixSource.Published), ct);
            if (!resolution.Resolved)
            {
                await uow.ExecuteAsync(c =>
                {
                    audit.Record(new AuditEntry("approval.resolution_blocked", nameof(ApprovalRequest), subjectId.ToString(), AuditOutcome.Denied, subject.OrganisationId, Reason: resolution.BlockMessage, Classification: subject.Classification,
                        NewValuesJson: AuditJson.Of(new { subjectKind, matrix = matrixCode, release = resolution.ReleaseNumber, code = resolution.BlockCode })));
                    return Task.CompletedTask;
                }, ct);
                throw new DomainException("approval_blocked", resolution.BlockMessage!);
            }
            var first = resolution.Plan!.Steps.Where(x => x.Stage == resolution.Plan.Steps.Min(y => y.Stage)).ToList();
            group = first[0].ApproverGroup;
            eligible = first.SelectMany(x => x.Approvers).GroupBy(a => a.UserId).Select(g => g.First()).Select(a => new EligibleApprover(a.UserId, a.Name, a.Role, a.Role != ParticipantRoles.Primary)).ToList();
        }
        else
        {
            eligible = await EligibleApproversAsync(group, ct);
            if (eligible.Count == 0)
                throw new DomainException("no_approver", "No approver is assigned to the register-authorisation group. An administrator must assign the approver and a backup before this can be submitted.");
            if (eligible.All(e => e.UserId == me))
                throw new DomainException("no_independent_approver", "You are the only person who could approve this, and nobody can approve their own submission. Another approver or a backup must be assigned.");
        }
        Guid id = Guid.Empty;
        await engine.ExecuteAsync(new TransitionCommand(subjectKind, subjectId, transition.Code, note, null, subject.Status, clientRequestId, AllowManaged: true), async (scope, c) =>
        {
            var snapshot = await BuildSnapshotAsync(scope, resolved, group, eligible, c);
            var json = JsonSerializer.Serialize(snapshot, Json);
            var sha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
            var name = await directory.DisplayNameAsync(me, c) ?? engine.ActorLabel;
            var request = ApprovalRequest.Submit(tenant.RequireTenantId(), scope.Subject.OrganisationId, subjectKind, subjectId, scope.Subject.ContractId, group, me, name, snapshot.ExecutedDocumentId,
                snapshot.ExecutedSha256, json, sha, JsonSerializer.Serialize(eligible, Json), scope.NowUtc);
            string? planSha = null;
            if (resolution is not null)
            {
                var stored = JsonSerializer.Serialize(new StoredPlan(resolution.Plan!, resolution.Trace, resolution.Explanations), Json);
                planSha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stored))).ToLowerInvariant();
                var state = ApprovalPlans.Evaluate(resolution.Plan!, []);
                request.AttachPlan(resolution.Plan!.MatrixCode, resolution.Plan.ReleaseNumber, stored, planSha, state.ActiveGroupKeys);
            }
            db.Approvals.Add(request);
            await engine.SaveAsync(c);
            id = request.Id;
            foreach (var g in request.PendingGroups) events.Raise(LifecycleEventTypes.ApprovalSubmitted, scope.Subject, new { requestId = request.Id, group = g, submitterUserId = me });
            audit.Record(new AuditEntry("approval.submitted", nameof(ApprovalRequest), request.Id.ToString(), OrganisationId: scope.Subject.OrganisationId, Reason: note, Classification: scope.Subject.Classification,
                NewValuesJson: resolution is null
                    ? AuditJson.Of(new { subjectKind, subjectId, group, snapshotSha256 = sha, executedDocumentId = snapshot.ExecutedDocumentId, eligible = eligible.Select(e => new { e.UserId, e.RoleCode }) })
                    : AuditJson.Of(new
                    {
                        subjectKind, subjectId, group, snapshotSha256 = sha, executedDocumentId = snapshot.ExecutedDocumentId, eligible = eligible.Select(e => new { e.UserId, e.RoleCode }),
                        matrix = resolution.MatrixCode, release = resolution.ReleaseNumber, planSha256 = planSha, rows = resolution.RowCodes, steps = resolution.Plan!.Steps.Select(x => new { x.Key, x.Stage, x.Mode, x.ApproverGroup })
                    })));
            await db.SaveChangesAsync(c);
        }, ct);
        return id;
    }

    private async Task<RegisterSnapshot> BuildSnapshotAsync(TransitionScope scope, ResolvedDefinition resolved, string group, IReadOnlyList<EligibleApprover> eligible, CancellationToken c)
    {
        var s = scope.Subject;
        var details = await contracts.GetAsync(s.ContractId, c);
        var review = await db.LegalReviews.AsNoTracking().FirstOrDefaultAsync(r => r.SubjectKind == s.Kind && r.SubjectId == s.Id, c);
        var exec = await db.SignatureRequests.AsNoTracking().FirstOrDefaultAsync(e => e.SubjectKind == s.Kind && e.SubjectId == s.Id && e.IsCurrent, c);
        var executed = await ExecutedDocumentAsync(s, exec?.ExecutedDocumentId, c);
        Dictionary<string, string?>? proposed = null, current = null;
        if (s.IsVariation)
        {
            proposed = JsonSerializer.Deserialize<Dictionary<string, string?>>(s.Variation!.ProposedChangesJson);
            // The snapshot is read by approvers, who may not be cleared for every configured field. A configured field appears only when an administrator has made it usable in approvals.
            if (proposed is not null && proposed.Keys.Any(k => k.StartsWith("cf.", StringComparison.Ordinal)))
            {
                var usable = (await customReader.ApprovalFactKeysAsync(s.OrganisationId, c)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                proposed = proposed.Where(kv => !kv.Key.StartsWith("cf.", StringComparison.Ordinal) || usable.Contains(kv.Key[3..])).ToDictionary(kv => kv.Key, kv => kv.Value);
            }
            current = proposed is null ? null : new Dictionary<string, string?>(await port.CurrentValuesAsync(s.ContractId, proposed.Keys.ToList(), c));
        }
        return new RegisterSnapshot(scope.NowUtc, s.Kind, details.AgreementReference, s.IsVariation ? s.Variation!.Title : details.Title, s.IsVariation ? s.Variation!.Reference : null, s.Status, s.Classification,
            details.SupplierName, details.CategoryName, details.CurrencyCode, details.EstimatedValue, details.StartDate?.ToString("yyyy-MM-dd"), details.EffectiveDate?.ToString("yyyy-MM-dd"),
            details.ReviewDate?.ToString("yyyy-MM-dd"), details.EndDate?.ToString("yyyy-MM-dd"), details.ExtensionType, s.Contract.NoticePeriodDays, s.Contract.CriticalNotice, s.LegalRequired,
            review?.Status.ToString(), review?.CurrentRound ?? 0, review?.ClearedBy, review?.ClearedUtc, exec?.Provider, exec?.ProviderRequestId, exec?.Status.ToString(), executed?.DocumentId, executed?.FileName, executed?.Sha256,
            resolved.WorkflowRef, group, scope.Actor, eligible, proposed, current);
    }

    private async Task<DocumentSummary?> ExecutedDocumentAsync(SubjectView s, Guid? preferred, CancellationToken c)
    {
        var docs = (await documentQuery.ListForContractAsync(s.ContractId, c)).Where(d => d.IsExecutedCopy && d.Status == DocumentStatus.Active && d.Downloadable).ToList();
        if (s.IsVariation)
        {
            var linked = await db.SubjectDocuments.AsNoTracking().Where(l => l.SubjectKind == SubjectKinds.Variation && l.SubjectId == s.Id).Select(l => l.DocumentId).ToListAsync(c);
            docs = docs.Where(d => linked.Contains(d.DocumentId)).ToList();
        }
        return docs.FirstOrDefault(d => d.DocumentId == preferred) ?? docs.FirstOrDefault();
    }

    // ---- reading --------------------------------------------------------------------------------------------------------------------------------

    private ResourceDescriptor Approving(ResourceDescriptor d, ApprovalRequest r)
        => d with { PendingApprovalGroupKeys = new HashSet<string>(r.PendingGroups), SubmittedByUserId = r.SubmittedByUserId };

    public async Task<PagedResult<ApprovalQueueItem>> PendingAsync(bool onlyActionable = true, int page = 1, int pageSize = 25, CancellationToken ct = default)
    {
        var me = currentUser.UserId;
        var rows = await db.Approvals.AsNoTracking().Where(a => a.Status == ApprovalStatus.Pending).OrderBy(a => a.SubmittedUtc).Take(2000).ToListAsync(ct);
        var seen = await visible.GetAsync(rows.Select(r => r.ContractId), null, ct);
        var variations = await db.Variations.AsNoTracking().Where(v => rows.Select(r => r.SubjectId).Contains(v.Id)).ToDictionaryAsync(v => v.Id, v => v.Reference, ct);
        var now = clock.UtcNow;
        var items = new List<ApprovalQueueItem>();
        foreach (var r in rows)
        {
            if (!seen.TryGetValue(r.ContractId, out var c)) continue;
            var descriptor = Approving(await contracts.DescribeAsync(r.ContractId, ct), r);
            var canDecide = (await access.CheckSilentAsync("approvals.action", descriptor, ct)).Allowed;
            var mine = me is { } id && r.SubmittedByUserId == id;
            if (onlyActionable && !canDecide && !mine) continue;
            var backup = canDecide && me is { } u && (await EligibleApproversAsync(r.ApproverGroupKey, ct)).Any(e => e.UserId == u && e.IsBackup);
            items.Add(new ApprovalQueueItem(r.Id, r.SubjectKind, r.SubjectId, r.ContractId, c.AgreementReference, c.Title, variations.GetValueOrDefault(r.SubjectId), c.SupplierName, c.CategoryName, c.Classification,
                r.SubmittedByName, r.SubmittedUtc, (int)Math.Max(0, (now - r.SubmittedUtc).TotalDays), r.ApproverGroupKey, canDecide, mine, backup));
        }
        var p = Math.Max(1, page); var size = Math.Clamp(pageSize, 1, 200);
        return new PagedResult<ApprovalQueueItem>(items.Skip((p - 1) * size).Take(size).ToList(), items.Count, p, size);
    }

    public async Task<ApprovalDetail> GetAsync(Guid requestId, CancellationToken ct = default)
    {
        var request = await db.Approvals.AsNoTracking().FirstOrDefaultAsync(a => a.Id == requestId, ct) ?? throw new ResourceNotFoundException();
        return await BuildDetailAsync(request, ct);
    }

    public async Task<ApprovalDetail?> CurrentForSubjectAsync(string subjectKind, Guid subjectId, CancellationToken ct = default)
    {
        var request = await db.Approvals.AsNoTracking().Where(a => a.SubjectKind == subjectKind && a.SubjectId == subjectId).OrderByDescending(a => a.SubmittedUtc).FirstOrDefaultAsync(ct);
        if (request is null) return null;
        // The subject's page is visible to anyone who can read the record; if they cannot, it does not exist for them.
        var subject = await engine.LoadSubjectAsync(subjectKind, subjectId, ct);
        await engine.RequireReadAsync(await engine.DescribeAsync(subject, null, ct), ct);
        return await BuildDetailAsync(request, ct, alreadyAuthorised: true);
    }

    private async Task<ApprovalDetail> BuildDetailAsync(ApprovalRequest request, CancellationToken ct, bool alreadyAuthorised = false)
    {
        var subject = await engine.LoadSubjectAsync(request.SubjectKind, request.SubjectId, ct);
        var descriptor = Approving(await engine.DescribeAsync(subject, null, ct), request);
        // A person who can act on the group sees the request; so does anyone who can read the record. Anyone else gets a 404.
        if (!alreadyAuthorised) await engine.RequireReadAsync(descriptor, ct);
        var snapshot = JsonSerializer.Deserialize<RegisterSnapshot>(request.SnapshotJson, Json)!.WithLegacyNames();
        var intact = string.Equals(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.SnapshotJson))).ToLowerInvariant(), request.SnapshotSha256, StringComparison.Ordinal);
        var eligible = JsonSerializer.Deserialize<List<EligibleApprover>>(request.EligibleApproversJson, Json) ?? [];
        var current = await EligibleApproversAsync(request.ApproverGroupKey, ct);

        ExecutedDocumentInfo? doc = null;
        if (request.ExecutedDocumentId is { } docId)
        {
            try
            {
                var visibleDocs = await secureDocuments.ListAsync(subject.ContractId, ct);
                if (visibleDocs.FirstOrDefault(d => d.DocumentId == docId) is { } d) doc = new ExecutedDocumentInfo(d.DocumentId, d.Title, d.FileName, d.Sha256, d.CanDownload);
            }
            catch (Exception ex) when (ex is ForbiddenException or ResourceNotFoundException)
            {
                // An approver reaches the record itself only while a request is waiting for their group; afterwards they still see the frozen request, with the document's identity but no download.
                doc = new ExecutedDocumentInfo(docId, snapshot.ExecutedFileName ?? "Executed copy", snapshot.ExecutedFileName, request.ExecutedDocumentSha256, false);
            }
        }

        var history = await HistoryOfAsync(request.ContractId, ct);
        var decision = request.Status is ApprovalStatus.Approved or ApprovalStatus.Rejected ? ToInfo(request) : null;
        bool canApprove = false, canReject = false; string? why = null;
        if (request.Status != ApprovalStatus.Pending) why = "This request has already been decided.";
        else
        {
            var allowed = await access.CheckSilentAsync("approvals.action", descriptor, ct);
            if (!allowed.Allowed)
                why = request.SubmittedByUserId == currentUser.UserId ? "You submitted this request, so someone else must decide it." : "Only the assigned approver or a backup can decide this request.";
            else { canApprove = true; canReject = true; }
        }
        var title = snapshot.VariationReference is null ? snapshot.Title : $"{snapshot.VariationReference}: {snapshot.Title}";
        PlanView? planView = null;
        if (request.HasPlan)
        {
            planView = await BuildPlanViewAsync(request, descriptor, ct);
            if (request.Status == ApprovalStatus.Pending && canApprove && planView.YourPart is null) { canApprove = false; canReject = false; why = planView.YourPartNote ?? "It is not your turn to respond."; }
        }
        return new ApprovalDetail(request.Id, request.SubjectKind, request.SubjectId, request.ContractId, snapshot.AgreementReference, title, snapshot.VariationReference, request.Status, request.ApproverGroupKey,
            request.SubmittedByName, request.SubmittedUtc, snapshot, request.SnapshotSha256, intact, doc, current.Count > 0 ? current : eligible, decision, history, canApprove, canReject, why, planView, request.OrganisationId);
    }

    private static ApprovalDecisionInfo ToInfo(ApprovalRequest r)
        => new(r.Id, r.SubjectKind, r.Status, r.SubmittedByName, r.SubmittedUtc, r.DecidedByName, r.DecidedAsRole, r.OnBehalfOfName, r.DecisionComment, r.DecidedUtc);

    private async Task<IReadOnlyList<ApprovalDecisionInfo>> HistoryOfAsync(Guid contractId, CancellationToken ct)
        => (await db.Approvals.AsNoTracking().Where(a => a.ContractId == contractId).OrderByDescending(a => a.SubmittedUtc).ToListAsync(ct)).Select(ToInfo).ToList();

    public async Task<IReadOnlyList<ApprovalDecisionInfo>> HistoryAsync(Guid contractId, CancellationToken ct = default)
    {
        var subject = await engine.LoadSubjectAsync(SubjectKinds.Contract, contractId, ct);
        await engine.RequireReadAsync(await engine.DescribeAsync(subject, null, ct), ct);
        return await HistoryOfAsync(contractId, ct);
    }

    // ---- decide ---------------------------------------------------------------------------------------------------------------------------------

    public Task ApproveAsync(Guid requestId, string? comment, string? clientRequestId = null, CancellationToken ct = default) => DecideAsync(requestId, true, comment, clientRequestId, ct);

    public Task RejectAsync(Guid requestId, string comment, string? clientRequestId = null, CancellationToken ct = default)
        => string.IsNullOrWhiteSpace(comment) ? throw new DomainException("comment_required", "A rejection needs a comment.") : DecideAsync(requestId, false, comment, clientRequestId, ct);

    private static DomainException AlreadyDecided(ApprovalRequest r)
        => new("already_decided", $"This request was already {(r.Status == ApprovalStatus.Approved ? "approved" : r.Status == ApprovalStatus.Rejected ? "rejected" : "withdrawn")}"
            + (r.DecidedByName is null ? "." : $" by {r.DecidedByName}") + (r.DecidedUtc is { } at ? $" at {at:yyyy-MM-dd HH:mm} UTC. The first response stands." : "."));

    private async Task DecideAsync(Guid requestId, bool approve, string? comment, string? clientRequestId, CancellationToken ct)
    {
        var me = currentUser.UserId ?? throw new ForbiddenException("A signed-in person is required.");
        var request = await db.Approvals.FirstOrDefaultAsync(a => a.Id == requestId, ct) ?? throw new ResourceNotFoundException();
        // The decision is made once. A repeat of the same call (same request id) is answered from history by the engine; anything else on a decided request is refused with who decided first.
        if (request.Status != ApprovalStatus.Pending)
        {
            if (!string.IsNullOrWhiteSpace(clientRequestId) && await db.History.AnyAsync(h => h.SubjectId == request.SubjectId && h.ClientRequestId == clientRequestId, ct)) return;
            throw AlreadyDecided(request);
        }
        // A request made under an approval matrix is decided step by step; the reference model below is unchanged.
        if (request.HasPlan) { await DecideWithPlanAsync(request, approve, comment, clientRequestId, ct); return; }
        var subject = await engine.LoadSubjectAsync(request.SubjectKind, request.SubjectId, ct);
        var resolved = await engine.DefinitionForAsync(subject, ct);
        var role = approve ? "approve" : "reject";
        var transition = resolved.Definition.From(subject.Status).FirstOrDefault(t => t.Role == role)
            ?? throw new DomainException("invalid_transition", $"This {subject.Noun} is {resolved.Definition.DisplayOf(subject.Status)}, so the request can no longer be decided. Reload it to see where it stands.");
        var eligible = await EligibleApproversAsync(request.ApproverGroupKey, ct);
        var mine = eligible.FirstOrDefault(e => e.UserId == me);

        try
        {
            await engine.ExecuteAsync(new TransitionCommand(request.SubjectKind, request.SubjectId, transition.Code, comment, null, subject.Status, clientRequestId, AllowManaged: true, Approval: request), async (scope, c) =>
            {
                // The actor is the signed-in person. A backup acts on behalf of the primary approver and that is recorded.
                var asRole = mine?.RoleCode ?? throw new ForbiddenException("Only the assigned approver or a backup can decide this request.");
                var primaries = eligible.Where(e => !e.IsBackup).ToList();
                var onBehalf = mine.IsBackup && primaries.Count == 1 ? primaries[0] : null;
                if (scope.Subject.IsVariation)
                {
                    // The Variation's own outcome (and the parent's return to Effective) is completed while the request is still pending, so the parent's checks see the approval as it stands.
                    if (approve) await variationOutcomes.ApplyApprovedAsync(scope, request, comment, c);
                    else await variationOutcomes.RejectAsync(scope, request, comment!, c);
                }
                else if (approve) await port.StampRegistrationAsync(scope.Subject.ContractId, scope.Actor, c);
                var name = await directory.DisplayNameAsync(me, c) ?? scope.Actor;
                request.Decide(approve ? ApprovalStatus.Approved : ApprovalStatus.Rejected, me, name, asRole, onBehalf?.UserId, onBehalf?.DisplayName, comment, null, scope.NowUtc);
                await engine.SaveAsync(c);
                events.Raise(approve ? LifecycleEventTypes.ApprovalApproved : LifecycleEventTypes.ApprovalRejected, scope.Subject, new { requestId = request.Id, decidedBy = me, asRole, submitterUserId = request.SubmittedByUserId, group = request.ApproverGroupKey });
                if (approve && !scope.Subject.IsVariation) events.Raise(LifecycleEventTypes.ContractRegistered, scope.Subject, new { requestId = request.Id, submitterUserId = request.SubmittedByUserId });
                audit.Record(new AuditEntry(approve ? "approval.approved" : "approval.rejected", nameof(ApprovalRequest), request.Id.ToString(), OrganisationId: scope.Subject.OrganisationId, Reason: comment,
                    Classification: scope.Subject.Classification, OldValuesJson: AuditJson.Of(new { status = "Pending" }),
                    NewValuesJson: AuditJson.Of(new { status = request.Status.ToString(), decidedBy = me, asRole, onBehalfOf = onBehalf?.UserId, snapshotSha256 = request.SnapshotSha256, subjectKind = request.SubjectKind })));
                await db.SaveChangesAsync(c);
            }, ct);
        }
        catch (DomainException ex) when (ex.Code is "concurrency_conflict" or "state_changed" or "duplicate_request")
        {
            // Someone else got there first. If the request is now decided, say who; otherwise the original conflict stands.
            db.ChangeTracker.Clear();
            var latest = await db.Approvals.AsNoTracking().FirstOrDefaultAsync(a => a.Id == requestId, ct);
            if (latest is not null && latest.Status != ApprovalStatus.Pending) throw AlreadyDecided(latest);
            throw;
        }
    }
}

/// <summary>
/// Lets the contract access rules see which contracts the approver group has something to decide on: a register-authorisation request waiting, or a contract flagged for disposition. Approvers reach a
/// contract only while one of those holds.
/// </summary>
internal sealed class PendingApprovalSource(LifecycleDbContext db, IWorkflowDefinitions definitions, Ecp.Tenancy.IOrganisationStructureService structure) : IPendingApprovalSource
{
    public async Task<IReadOnlyDictionary<Guid, IReadOnlySet<string>>> PendingAsync(IReadOnlyCollection<Guid>? contractIds, CancellationToken ct = default)
    {
        var ids = contractIds is { Count: > 0 } ? contractIds.ToArray() : null;
        var query = db.Approvals.AsNoTracking().Where(a => a.Status == ApprovalStatus.Pending);
        if (ids is not null) query = query.Where(a => ids.Contains(a.ContractId));
        // A plan-based request is waiting on the groups of its open steps; the reference model on its single approver group.
        var rows = (await query.Select(a => new { a.ContractId, a.ApproverGroupKey, a.ActiveGroupKeys }).ToListAsync(ct))
            .SelectMany(r => (string.IsNullOrEmpty(r.ActiveGroupKeys) ? new[] { r.ApproverGroupKey } : r.ActiveGroupKeys.Split(',', StringSplitOptions.RemoveEmptyEntries)).Select(g => (r.ContractId, Group: g))).ToList();

        // Contracts whose latest step landed in a state with the 'flagged' role are waiting for the disposition decision of the workflow's approver group.
        var flaggedByGroup = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var org in await structure.ListOrganisationsAsync(ct))
        {
            var def = (await definitions.CurrentAsync(WorkflowKinds.ContractLifecycle, org.Id, ct)).Definition;
            var group = def.Settings.Approval.ApproverGroupKey;
            if (!flaggedByGroup.TryGetValue(group, out var list)) flaggedByGroup[group] = list = [];
            list.AddRange(def.States.Where(s => s.Role == "flagged").Select(s => s.Code));
        }
        foreach (var (group, codes) in flaggedByGroup.Where(g => g.Value.Count > 0))
        {
            var codeArray = codes.Distinct().ToArray();
            var latest = db.History.AsNoTracking().Where(h => h.SubjectKind == SubjectKinds.Contract && codeArray.Contains(h.ToStatus)
                && !db.History.Any(h2 => h2.ContractId == h.ContractId && h2.SubjectKind == SubjectKinds.Contract && h2.Sequence > h.Sequence));
            if (ids is not null) latest = latest.Where(h => ids.Contains(h.ContractId));
            rows.AddRange((await latest.Select(h => h.ContractId).ToListAsync(ct)).Select(c => (c, group)));
        }
        return rows.GroupBy(r => r.ContractId).ToDictionary(g => g.Key, g => (IReadOnlySet<string>)g.Select(r => r.Group).ToHashSet(StringComparer.Ordinal));
    }
}
