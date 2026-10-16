using System.Globalization;
using System.Text.Json;
using Ecp.Audit;
using Ecp.Contracts;
using Ecp.Contracts.Domain;
using Ecp.Documents;
using Ecp.Documents.Domain;
using Ecp.Identity;
using Ecp.Lifecycle.Definitions;
using Ecp.Lifecycle.Domain;
using Ecp.Lifecycle.Persistence;
using Ecp.Persistence;
using Ecp.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Ecp.Lifecycle;

public sealed record CopiedDocument(Guid SourceDocumentId, Guid NewDocumentId, string DocumentType, string Title, string Sha256);

public sealed record RenewalInfo(
    Guid Id, Guid PredecessorContractId, string PredecessorReference, string PredecessorTitle, Guid SuccessorContractId, string? SuccessorReference, string? SuccessorStatus, RenewalStatus Status, string StartedBy,
    DateTime StartedUtc, string? Note, IReadOnlyList<string> CopiedFields, IReadOnlyList<CopiedDocument> CopiedDocuments, string? ClosedBy, DateTime? ClosedUtc, string? CloseReason, string? CategoryName, string? SupplierName);

/// <summary>
/// Renewals and successors. Starting a renewal creates a new agreement from only the fields and document types the workflow names, links the two both ways and leaves the predecessor's content,
/// dates and history alone. The predecessor changes only by its own explicit steps; completing the renewal never ends it silently.
/// </summary>
public interface IRenewalService
{
    Task<RenewalInfo> StartAsync(Guid contractId, string? note, CancellationToken ct = default);
    Task<IReadOnlyList<RenewalInfo>> ListForContractAsync(Guid contractId, CancellationToken ct = default);
    Task<PagedResult<RenewalInfo>> InProgressAsync(int page = 1, int pageSize = 25, CancellationToken ct = default);
    /// <summary>Records that the successor is registered and returns the predecessor to Effective, unchanged. Ending the predecessor is its owner's separate, explicit step.</summary>
    Task CompleteAsync(Guid contractId, string? note, CancellationToken ct = default);
    /// <summary>Abandons the renewal: the predecessor returns to Effective unchanged and the successor, if it can still be withdrawn, is withdrawn.</summary>
    Task CancelAsync(Guid contractId, string reason, CancellationToken ct = default);
}

internal sealed class RenewalService(
    LifecycleDbContext db, LifecycleEngine engine, IAuditWriter audit, ITenantContext tenant, IContractLifecyclePort port, IWorkflowDefinitions definitions,
    IDocumentService documentService, IDocumentQuery documentQuery, LifecycleEvents events, VisibleContracts visible) : IRenewalService
{
    public async Task<RenewalInfo> StartAsync(Guid contractId, string? note, CancellationToken ct = default)
    {
        var subject = await engine.LoadSubjectAsync(SubjectKinds.Contract, contractId, ct);
        var resolved = await engine.DefinitionForAsync(subject, ct);
        var transition = resolved.Definition.From(subject.Status).FirstOrDefault(t => t.Role == "startRenewal")
            ?? throw new DomainException("invalid_transition", $"A renewal can be started only on an Effective agreement; this one is {resolved.Definition.DisplayOf(subject.Status)}.");
        if (subject.Contract.SuccessorContractId is not null) throw new DomainException("successor_exists", "This agreement already has a successor.");
        var settings = resolved.Definition.Settings.Renewal;
        var current = await definitions.CurrentAsync(WorkflowKinds.ContractLifecycle, subject.OrganisationId, ct);
        var startState = current.Definition.State(settings.SuccessorInitialState)
            ?? throw new DomainException("renewal_state", "The renewal's starting state is not part of the workflow in force. An administrator must correct the workflow definition.");
        Renewal renewal = null!;
        await engine.ExecuteAsync(new TransitionCommand(SubjectKinds.Contract, contractId, transition.Code, note, null, subject.Status, AllowManaged: true), async (scope, c) =>
        {
            var p = scope.Subject.Contract;
            var copied = settings.CopyFields;
            // Only the configured fields are carried across; dates are the renewal's own, and nothing of the predecessor's audit, history or provenance goes with it.
            var seed = new SuccessorSeed("", p.Title + " (renewal)", startState.Code, new StateEffects(startState.Locked, startState.FreezeContent, startState.DocumentPolicy, startState.LockReason),
                current.WorkflowRef, current.ReleaseId, copied.Contains("description") ? p.Description : null,
                settings.StartDateRule == "DayAfterPredecessorEnd" && p.EndDate is { } end ? end.AddDays(1) : null, null, null, null,
                copied.Contains("currencyCode") ? p.CurrencyCode : null, copied.Contains("estimatedValue") ? p.EstimatedValue : null, copied.Contains("extensionType") ? p.ExtensionType : null,
                copied.Contains("noticePeriodDays") ? p.NoticePeriodDays : null, copied.Contains("criticalNotice") && p.CriticalNotice, copied.Contains("reminderLeadDays") ? p.ReminderLeadDays : null,
                copied.Contains("legalReviewRequired") && p.LegalReviewRequired);
            var successorId = await port.CreateSuccessorAsync(contractId, seed, scope.Actor, c);

            var docs = new List<CopiedDocument>();
            foreach (var d in await documentQuery.ListForContractAsync(contractId, c))
            {
                if (d.IsExecutedCopy || d.Status != DocumentStatus.Active || !d.Downloadable || !settings.CopyDocumentTypes.Contains(d.DocumentTypeCode)) continue;
                var copy = await documentService.CopyAsync(new CopyDocumentRequest(d.DocumentId, successorId, scope.Subject.OrganisationId, d.Classification), c);
                docs.Add(new CopiedDocument(d.DocumentId, copy.DocumentId, d.DocumentTypeCode, d.Title, copy.Sha256));
            }
            renewal = Renewal.Start(tenant.RequireTenantId(), scope.Subject.OrganisationId, contractId, successorId, scope.Actor, note, JsonSerializer.Serialize(copied), JsonSerializer.Serialize(docs), scope.NowUtc);
            db.Renewals.Add(renewal);
            db.History.Add(StatusHistory.Record(tenant.RequireTenantId(), scope.Subject.OrganisationId, SubjectKinds.Contract, successorId, successorId, startState.Code, startState.Code, "CreatedAsRenewal", scope.Actor,
                scope.ActorUserId, $"Renewal of {p.AgreementReference}", current.WorkflowRef, current.ReleaseId, "-", null, null, scope.NowUtc, await engine.NextHistorySequenceAsync(successorId, c)));
            await engine.SaveAsync(c);
            events.Raise(LifecycleEventTypes.RenewalStarted, scope.Subject, new { renewalId = renewal.Id, successorId });
            audit.Record(new AuditEntry("renewal.started", nameof(Renewal), renewal.Id.ToString(), OrganisationId: scope.Subject.OrganisationId, Reason: note, Classification: scope.Subject.Classification,
                NewValuesJson: AuditJson.Of(new { predecessor = contractId, successor = successorId, fields = copied, documents = docs.Count })));
            await db.SaveChangesAsync(c);
        }, ct);
        return (await InfoAsync([renewal], ct))[0];
    }

    public async Task<IReadOnlyList<RenewalInfo>> ListForContractAsync(Guid contractId, CancellationToken ct = default)
    {
        var subject = await engine.LoadSubjectAsync(SubjectKinds.Contract, contractId, ct);
        await engine.RequireReadAsync(await engine.DescribeAsync(subject, null, ct), ct);
        var rows = await db.Renewals.AsNoTracking().Where(r => r.PredecessorContractId == contractId || r.SuccessorContractId == contractId).OrderByDescending(r => r.StartedUtc).ToListAsync(ct);
        return await InfoAsync(rows, ct);
    }

    public async Task<PagedResult<RenewalInfo>> InProgressAsync(int page = 1, int pageSize = 25, CancellationToken ct = default)
    {
        var rows = await db.Renewals.AsNoTracking().Where(r => r.Status == RenewalStatus.InProgress).OrderBy(r => r.StartedUtc).Take(2000).ToListAsync(ct);
        var seen = await visible.GetAsync(rows.Select(r => r.PredecessorContractId), null, ct);
        var items = await InfoAsync(rows.Where(r => seen.ContainsKey(r.PredecessorContractId)).ToList(), ct);
        var p = Math.Max(1, page); var size = Math.Clamp(pageSize, 1, 200);
        return new PagedResult<RenewalInfo>(items.Skip((p - 1) * size).Take(size).ToList(), items.Count, p, size);
    }

    /// <summary>Renewal facts for display. A linked agreement is named only when the caller may read it; otherwise only that a link exists.</summary>
    private async Task<IReadOnlyList<RenewalInfo>> InfoAsync(IReadOnlyList<Renewal> rows, CancellationToken ct)
    {
        var ids = rows.SelectMany(r => new[] { r.PredecessorContractId, r.SuccessorContractId }).Distinct().ToList();
        var seen = await visible.GetAsync(ids, null, ct);
        return rows.Select(r =>
        {
            seen.TryGetValue(r.PredecessorContractId, out var pre);
            seen.TryGetValue(r.SuccessorContractId, out var suc);
            return new RenewalInfo(r.Id, r.PredecessorContractId, pre?.AgreementReference ?? "(not visible)", pre?.Title ?? "", r.SuccessorContractId, suc?.AgreementReference, suc?.Status, r.Status, r.StartedBy, r.StartedUtc, r.Note,
                JsonSerializer.Deserialize<List<string>>(r.CopiedFieldsJson) ?? [], JsonSerializer.Deserialize<List<CopiedDocument>>(r.CopiedDocumentsJson) ?? [], r.ClosedBy, r.ClosedUtc, r.CloseReason, pre?.CategoryName, pre?.SupplierName);
        }).ToList();
    }

    public async Task CompleteAsync(Guid contractId, string? note, CancellationToken ct = default)
    {
        var subject = await engine.LoadSubjectAsync(SubjectKinds.Contract, contractId, ct);
        var resolved = await engine.DefinitionForAsync(subject, ct);
        var transition = resolved.Definition.From(subject.Status).FirstOrDefault(t => t.Role == "completeRenewal")
            ?? throw new DomainException("invalid_transition", $"There is no renewal to complete while the agreement is {resolved.Definition.DisplayOf(subject.Status)}.");
        await engine.ExecuteAsync(new TransitionCommand(SubjectKinds.Contract, contractId, transition.Code, note, null, subject.Status, AllowManaged: true), async (scope, c) =>
        {
            var renewal = await db.Renewals.FirstAsync(r => r.PredecessorContractId == contractId && r.Status == RenewalStatus.InProgress, c);
            renewal.Close(RenewalStatus.Completed, scope.Actor, note, scope.NowUtc);
            await engine.SaveAsync(c);
            events.Raise(LifecycleEventTypes.RenewalCompleted, scope.Subject, new { renewalId = renewal.Id, successorId = renewal.SuccessorContractId });
            audit.Record(new AuditEntry("renewal.completed", nameof(Renewal), renewal.Id.ToString(), OrganisationId: scope.Subject.OrganisationId, Reason: note, Classification: scope.Subject.Classification,
                NewValuesJson: AuditJson.Of(new { predecessor = contractId, successor = renewal.SuccessorContractId, note = "The predecessor is returned unchanged; ending it is a separate step" })));
            await db.SaveChangesAsync(c);
        }, ct);
    }

    public async Task CancelAsync(Guid contractId, string reason, CancellationToken ct = default)
    {
        var subject = await engine.LoadSubjectAsync(SubjectKinds.Contract, contractId, ct);
        var resolved = await engine.DefinitionForAsync(subject, ct);
        var transition = resolved.Definition.From(subject.Status).FirstOrDefault(t => t.Role == "cancelRenewal")
            ?? throw new DomainException("invalid_transition", $"There is no renewal to cancel while the agreement is {resolved.Definition.DisplayOf(subject.Status)}.");
        var renewalRow = await db.Renewals.AsNoTracking().FirstOrDefaultAsync(r => r.PredecessorContractId == contractId && r.Status == RenewalStatus.InProgress, ct)
            ?? throw new DomainException("no_renewal", "There is no renewal in progress.");
        var successor = await engine.LoadSubjectAsync(SubjectKinds.Contract, renewalRow.SuccessorContractId, ct);
        var successorDef = await engine.DefinitionForAsync(successor, ct);
        var withdraw = successorDef.Definition.From(successor.Status).FirstOrDefault(t => t.Role == "withdraw");
        if (successor.Contract.RegisteredDateUtc is null && withdraw is null)
            throw new DomainException("successor_in_flight", $"The successor ({successor.Contract.AgreementReference}) is {successorDef.Definition.DisplayOf(successor.Status)}. Recall it or let it finish before cancelling the renewal.");
        if (successor.Contract.RegisteredDateUtc is not null)
            throw new DomainException("successor_registered", "The successor is already registered, so the renewal cannot be cancelled. Complete it instead.");
        await engine.ExecuteAsync(new TransitionCommand(SubjectKinds.Contract, contractId, transition.Code, reason, reason, subject.Status, AllowManaged: true), async (scope, c) =>
        {
            var renewal = await db.Renewals.FirstAsync(r => r.Id == renewalRow.Id, c);
            // The successor is withdrawn through its own workflow step, so its history and rules apply as for any withdrawal.
            await engine.ExecuteAsync(new TransitionCommand(SubjectKinds.Contract, renewal.SuccessorContractId, withdraw!.Code, reason, reason, successor.Status, AllowManaged: true), null, c);
            renewal.Close(RenewalStatus.Cancelled, scope.Actor, reason, scope.NowUtc);
            await engine.SaveAsync(c);
            events.Raise(LifecycleEventTypes.RenewalCancelled, scope.Subject, new { renewalId = renewal.Id });
            audit.Record(new AuditEntry("renewal.cancelled", nameof(Renewal), renewal.Id.ToString(), OrganisationId: scope.Subject.OrganisationId, Reason: reason, Classification: scope.Subject.Classification,
                NewValuesJson: AuditJson.Of(new { predecessor = contractId, successor = renewal.SuccessorContractId })));
            await db.SaveChangesAsync(c);
        }, ct);
    }
}
