using System.Text.Json;
using Ecp.Audit;
using Ecp.Contracts;
using Ecp.Contracts.Domain;
using Ecp.Documents;
using Ecp.Identity;
using Ecp.Lifecycle.Definitions;
using Ecp.Lifecycle.Domain;
using Ecp.Lifecycle.Persistence;
using Ecp.Persistence;
using Ecp.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Ecp.Lifecycle;

public sealed record VariationInput(string Title, string? Description, string Reason, DateOnly? ProposedDate, bool LegalReviewRequired, decimal? FinancialImpactAmount, string? FinancialImpactCurrency,
    string? FinancialImpactNote, IReadOnlyDictionary<string, string?> ProposedChanges);

public sealed record VariationListItem(
    Guid Id, Guid ContractId, string AgreementReference, string ContractTitle, string SupplierName, string CategoryName, string Reference, string Title, string Status, string StatusDisplay, bool IsOpen,
    VariationOutcome? Outcome, DateOnly? ProposedDate, decimal? ImpactAmount, string? ImpactCurrency, string CreatedBy, DateTime CreatedUtc, DateTime UpdatedUtc, DateTime? CompletedUtc);

public sealed record FieldComparison(string Field, string Label, string? Before, string? After, string? Current, bool Changed);

public sealed record VariationDocumentItem(Guid DocumentId, string Title, string DocumentType, string Role, string? FileName, bool IsExecutedCopy, bool CanDownload);

public sealed record VariationDetail(
    VariationListItem Summary, string? Description, string Reason, bool LegalReviewRequired, string? ImpactNote, string? OutcomeReason, string ParentStatusBefore, bool ParentChangedSinceDrafted,
    IReadOnlyList<FieldComparison> Comparison, IReadOnlyList<VariationDocumentItem> Documents, IReadOnlyList<AvailableTransition> Steps, bool CanEdit, bool CanAddDocuments, IReadOnlyList<string> ChangeableFields,
    IReadOnlyDictionary<string, string>? FieldLabels = null, Guid OrganisationId = default);

public sealed record AddVariationDocumentRequest(Guid VariationId, string Role, string Title, string FileName, string MimeType, Stream Content, string? Classification = null);

/// <summary>
/// Variations: the only ordinary way an Effective agreement changes. The parent stays locked while one is open; cancelling leaves the parent exactly as it was; applying changes only the fields the
/// Variation workflow names, in one audited step that preserves the before and after values.
/// </summary>
public interface IVariationService
{
    Task<Guid> CreateAsync(Guid contractId, VariationInput input, CancellationToken ct = default);
    Task UpdateAsync(Guid variationId, VariationInput input, CancellationToken ct = default);
    Task<VariationDetail> GetAsync(Guid variationId, CancellationToken ct = default);
    Task<IReadOnlyList<VariationListItem>> ListForContractAsync(Guid contractId, CancellationToken ct = default);
    Task<PagedResult<VariationListItem>> InProgressAsync(int page = 1, int pageSize = 25, CancellationToken ct = default);
    Task<VariationDocumentItem> AddDocumentAsync(AddVariationDocumentRequest request, CancellationToken ct = default);
    /// <summary>Takes a step that has no data of its own (prepare for signature, return to drafting).</summary>
    Task<TransitionResult> TransitionAsync(Guid variationId, string transitionCode, string? comment, CancellationToken ct = default);
    Task CancelAsync(Guid variationId, string reason, CancellationToken ct = default);
    /// <summary>The fields a Variation may change and the longest the form needs: from the Variation workflow in force.</summary>
    Task<IReadOnlyList<string>> ChangeableFieldsAsync(Guid contractId, CancellationToken ct = default);
    /// <summary>The configured fields (not core fields) the signed-in person may propose to change: those the workflow allows, that apply to the contract now, and that they may see.</summary>
    Task<IReadOnlyList<Ecp.Metadata.FieldDefinition>> ChangeableCustomFieldsAsync(Guid contractId, CancellationToken ct = default);
}

/// <summary>Completes a Variation from the approval path (approved or rejected), inside the approval's transaction.</summary>
internal interface IVariationOutcomes
{
    Task ApplyApprovedAsync(TransitionScope variationScope, ApprovalRequest request, string? comment, CancellationToken ct);
    Task RejectAsync(TransitionScope variationScope, ApprovalRequest request, string comment, CancellationToken ct);
}

internal sealed class VariationService(
    LifecycleDbContext db, LifecycleEngine engine, IUnitOfWork uow, IAuditWriter audit, ITenantContext tenant, IClock clock, IAccessService access, IContractLifecyclePort port,
    IWorkflowDefinitions definitions, ISecuredContractService contracts, ISecuredContractDocuments secureDocuments, IDocumentService documentService,
    LifecycleEvents events, VisibleContracts visible, LegalReviewService legalReviews, IContractCustomFields customFieldForms) : IVariationService, IVariationOutcomes
{
    private static readonly Dictionary<string, string> Labels = new()
    {
        ["currencyCode"] = "[[field.currency]]", ["estimatedValue"] = "[[field.estimated.value]]", ["extensionType"] = "[[field.extension.type]]", ["noticePeriodDays"] = "[[field.notice.period]] (days)", ["criticalNotice"] = "[[field.critical.notice]]",
        ["reviewDate"] = "[[field.review.date]]", ["endDate"] = "[[field.end.date]]", ["businessUnitId"] = "[[business.unit]]", ["siteId"] = "[[site]]"
    };

    private static string LabelOf(string field) => Labels.TryGetValue(field, out var l) ? l : field;

    public async Task<IReadOnlyList<string>> ChangeableFieldsAsync(Guid contractId, CancellationToken ct = default)
    {
        var facts = await port.FindAsync(contractId, ct) ?? throw new ResourceNotFoundException();
        await engine.RequireReadAsync(await contracts.DescribeAsync(contractId, ct), ct);
        return (await definitions.CurrentAsync(WorkflowKinds.Variation, facts.OrganisationId, ct)).Definition.Settings.Variation.ChangeableFields;
    }

    public async Task<IReadOnlyList<Ecp.Metadata.FieldDefinition>> ChangeableCustomFieldsAsync(Guid contractId, CancellationToken ct = default)
    {
        var allowed = await ChangeableFieldsAsync(contractId, ct);
        return (await customFieldForms.ProposableAsync(contractId, ct)).Where(d => AllowsCustom(allowed, d.Key)).ToList();
    }

    private static bool AllowsCustom(IReadOnlyCollection<string> allowed, string key)
        => allowed.Contains("cf." + key, StringComparer.OrdinalIgnoreCase) || allowed.Contains("cf.*", StringComparer.Ordinal);

    // ---- create and edit ------------------------------------------------------------------------------------------------------------------------

    private async Task<(string ProposedJson, string BeforeJson)> ValidateAsync(Guid contractId, VariationInput input, WorkflowDefinition variationDef, CancellationToken ct)
    {
        var allowed = variationDef.Settings.Variation.ChangeableFields;
        if (input.ProposedChanges.Count == 0) throw new DomainException("changes_required", "Name at least one change the [[variation]] proposes.");
        var outside = input.ProposedChanges.Keys.Where(k => !allowed.Contains(k) && !(k.StartsWith("cf.", StringComparison.Ordinal) && AllowsCustom(allowed, k[3..]))).ToList();
        if (outside.Count > 0) throw new DomainException("field_not_changeable", $"[[variation|a|cap]] cannot change: {string.Join(", ", outside)}.");
        // A configured field is also judged by what this person may see and propose: a restricted or role-limited field cannot be changed by someone who cannot see it.
        if (input.ProposedChanges.Keys.Any(k => k.StartsWith("cf.", StringComparison.Ordinal)))
        {
            var mine = (await customFieldForms.ProposableAsync(contractId, ct)).Select(d => d.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var refused = input.ProposedChanges.Keys.Where(k => k.StartsWith("cf.", StringComparison.Ordinal) && !mine.Contains(k[3..])).ToList();
            if (refused.Count > 0) throw new DomainException("field_not_changeable", $"[[variation|a|cap]] cannot change: {string.Join(", ", refused)}.");
        }
        // The proposed values are checked against the same rules that will apply them, so an invalid Variation is refused when it is drafted rather than when it is approved.
        await port.PreviewControlledChangesAsync(contractId, input.ProposedChanges, allowed, ct);
        var before = await port.CurrentValuesAsync(contractId, input.ProposedChanges.Keys.ToList(), ct);
        return (JsonSerializer.Serialize(input.ProposedChanges), JsonSerializer.Serialize(before));
    }

    private bool RequiredLegal(WorkflowDefinition def, bool requested) => def.Settings.Variation.LegalReview switch { "Required" => true, "None" => false, _ => requested };

    public async Task<Guid> CreateAsync(Guid contractId, VariationInput input, CancellationToken ct = default)
    {
        var subject = await engine.LoadSubjectAsync(SubjectKinds.Contract, contractId, ct);
        var contractDef = await engine.DefinitionForAsync(subject, ct);
        var transition = contractDef.Definition.From(subject.Status).FirstOrDefault(t => t.Role == "openVariation")
            ?? throw new DomainException("invalid_transition", $"[[variation|a|cap]] can be started only on an Effective [[agreement|lower]]; this one is {contractDef.Definition.DisplayOf(subject.Status)}.");
        var variationDef = await definitions.CurrentAsync(WorkflowKinds.Variation, subject.OrganisationId, ct);
        var (proposedJson, beforeJson) = await ValidateAsync(contractId, input, variationDef.Definition, ct);
        Guid id = Guid.Empty;
        await engine.ExecuteAsync(new TransitionCommand(SubjectKinds.Contract, contractId, transition.Code, input.Reason, null, subject.Status, AllowManaged: true), async (scope, c) =>
        {
            var sequence = (await db.Variations.Where(v => v.ContractId == contractId).Select(v => (int?)v.Sequence).MaxAsync(c) ?? 0) + 1;
            var reference = $"{scope.Subject.Contract.AgreementReference}-VAR{sequence:00}";
            var user = engine.ActorUserId ?? throw new ForbiddenException("A signed-in person is required.");
            var variation = Variation.Raise(tenant.RequireTenantId(), scope.Subject.OrganisationId, contractId, reference, sequence, variationDef.Definition.Initial.Code, input.Title, input.Description, input.Reason,
                input.ProposedDate, RequiredLegal(variationDef.Definition, input.LegalReviewRequired), input.FinancialImpactAmount, input.FinancialImpactCurrency, input.FinancialImpactNote,
                proposedJson, beforeJson, variationDef.WorkflowRef, variationDef.ReleaseId, scope.Subject.Status, engine.ActorLabel, user, scope.NowUtc);
            db.Variations.Add(variation);
            db.History.Add(StatusHistory.Record(tenant.RequireTenantId(), scope.Subject.OrganisationId, SubjectKinds.Variation, variation.Id, contractId, variationDef.Definition.Initial.Code, variationDef.Definition.Initial.Code,
                "Raised", engine.ActorLabel, user, input.Reason, variationDef.WorkflowRef, variationDef.ReleaseId, "-", null, null, scope.NowUtc, await engine.NextHistorySequenceAsync(contractId, c)));
            await engine.SaveAsync(c);
            id = variation.Id;
            events.Raise(LifecycleEventTypes.VariationRaised, scope.Subject.OrganisationId, SubjectKinds.Variation, variation.Id, contractId, scope.Subject.Classification, new { variationId = variation.Id, reference, submitterUserId = user });
            audit.Record(new AuditEntry("variation.raised", nameof(Variation), variation.Id.ToString(), OrganisationId: scope.Subject.OrganisationId, Reason: input.Reason, Classification: scope.Subject.Classification,
                NewValuesJson: AuditJson.Of(new { reference, contractId, fields = input.ProposedChanges.Keys, parentStatus = scope.Subject.Status })));
            await db.SaveChangesAsync(c);
        }, ct);
        return id;
    }

    private static readonly string[] EditableRoles = ["initial", "legalRework"];

    public async Task UpdateAsync(Guid variationId, VariationInput input, CancellationToken ct = default)
    {
        var subject = await engine.LoadSubjectAsync(SubjectKinds.Variation, variationId, ct);
        var resolved = await engine.DefinitionForAsync(subject, ct);
        await access.RequireAsync("variations.create", await engine.DescribeAsync(subject, null, ct), ct);
        if (!subject.Variation!.IsOpen || !EditableRoles.Contains(resolved.Definition.State(subject.Status)?.Role ?? ""))
            throw new DomainException("variation_not_editable", "[[variation|a|cap]] can be edited while it is a draft or after [[reviewer]] returns it for rework.");
        var (proposedJson, beforeJson) = await ValidateAsync(subject.ContractId, input, resolved.Definition, ct);
        await uow.ExecuteAsync(async c =>
        {
            var v = await db.Variations.FirstAsync(x => x.Id == variationId, c);
            v.Edit(input.Title, input.Description, input.Reason, input.ProposedDate, RequiredLegal(resolved.Definition, input.LegalReviewRequired), input.FinancialImpactAmount, input.FinancialImpactCurrency,
                input.FinancialImpactNote, proposedJson, beforeJson, clock.UtcNow);
            await engine.SaveAsync(c);
            audit.Record(new AuditEntry("variation.edited", nameof(Variation), variationId.ToString(), OrganisationId: subject.OrganisationId, Classification: subject.Classification,
                NewValuesJson: AuditJson.Of(new { fields = input.ProposedChanges.Keys })));
            await db.SaveChangesAsync(c);
        }, ct);
    }

    // ---- reading --------------------------------------------------------------------------------------------------------------------------------

    private static VariationListItem ToItem(Variation v, ContractListItem c, WorkflowDefinition? def)
        => new(v.Id, v.ContractId, c.AgreementReference, c.Title, c.SupplierName, c.CategoryName, v.Reference, v.Title, v.Status, def?.DisplayOf(v.Status) ?? v.Status, v.IsOpen, v.Outcome, v.ProposedDate,
            v.FinancialImpactAmount, v.FinancialImpactCurrency, v.CreatedBy, v.CreatedUtc, v.UpdatedUtc, v.CompletedUtc);

    public async Task<IReadOnlyList<VariationListItem>> ListForContractAsync(Guid contractId, CancellationToken ct = default)
    {
        var subject = await engine.LoadSubjectAsync(SubjectKinds.Contract, contractId, ct);
        await engine.RequireReadAsync(await engine.DescribeAsync(subject, null, ct), ct);
        var rows = await db.Variations.AsNoTracking().Where(v => v.ContractId == contractId).OrderByDescending(v => v.Sequence).ToListAsync(ct);
        var listing = new ContractListItem(subject.ContractId, subject.OrganisationId, subject.Contract.AgreementReference, subject.Contract.Title, "", "", subject.Status, subject.Classification, subject.Contract.RecordLocked);
        var defs = new Dictionary<Guid, WorkflowDefinition>();
        var items = new List<VariationListItem>();
        foreach (var v in rows)
            items.Add(ToItem(v, listing, await DefinitionOfAsync(v, defs, ct)));
        return items;
    }

    private async Task<WorkflowDefinition?> DefinitionOfAsync(Variation v, Dictionary<Guid, WorkflowDefinition> cache, CancellationToken ct)
    {
        if (v.ConfigReleaseId is not { } id) return null;
        if (cache.TryGetValue(id, out var def)) return def;
        return cache[id] = (await definitions.ResolveAsync(WorkflowKinds.Variation, v.OrganisationId, id, ct)).Definition;
    }

    public async Task<PagedResult<VariationListItem>> InProgressAsync(int page = 1, int pageSize = 25, CancellationToken ct = default)
    {
        var rows = await db.Variations.AsNoTracking().Where(v => v.IsOpen).OrderBy(v => v.CreatedUtc).Take(2000).ToListAsync(ct);
        var seen = await visible.GetAsync(rows.Select(r => r.ContractId), null, ct);
        var defs = new Dictionary<Guid, WorkflowDefinition>();
        var items = new List<VariationListItem>();
        foreach (var v in rows)
            if (seen.TryGetValue(v.ContractId, out var c)) items.Add(ToItem(v, c, await DefinitionOfAsync(v, defs, ct)));
        var p = Math.Max(1, page); var size = Math.Clamp(pageSize, 1, 200);
        return new PagedResult<VariationListItem>(items.Skip((p - 1) * size).Take(size).ToList(), items.Count, p, size);
    }

    public async Task<VariationDetail> GetAsync(Guid variationId, CancellationToken ct = default)
    {
        var subject = await engine.LoadSubjectAsync(SubjectKinds.Variation, variationId, ct);
        var descriptor = await engine.DescribeAsync(subject, null, ct);
        await engine.RequireReadAsync(descriptor, ct);
        var resolved = await engine.DefinitionForAsync(subject, ct);
        var v = subject.Variation!;
        var proposed = JsonSerializer.Deserialize<Dictionary<string, string?>>(v.ProposedChangesJson) ?? [];
        var before = JsonSerializer.Deserialize<Dictionary<string, string?>>(v.BeforeJson) ?? [];
        var current = await port.CurrentValuesAsync(v.ContractId, proposed.Keys.ToList(), ct);
        var rows = new List<FieldComparison>();
        // A configured field is shown only to people who may see it on the contract; for anyone else the row says only that a restricted detail changes, never its name or values.
        var visibleCustom = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (proposed.Keys.Any(k => k.StartsWith("cf.", StringComparison.Ordinal)))
            foreach (var e in (await customFieldForms.ForContractAsync(v.ContractId, ct: ct)).Fields) visibleCustom[e.Definition.Key] = e.Definition.Label;
        string Label(string field) => !field.StartsWith("cf.", StringComparison.Ordinal) ? LabelOf(field) : visibleCustom.TryGetValue(field[3..], out var l) ? l : "Restricted detail";
        bool Shown(string field) => !field.StartsWith("cf.", StringComparison.Ordinal) || visibleCustom.ContainsKey(field[3..]);
        string? Cur(string field) => Shown(field) ? current.GetValueOrDefault(field) : null;
        if (v.Outcome == VariationOutcome.Applied && v.AppliedBeforeJson is not null)
        {
            // What was actually in force and what replaced it, preserved at the moment of application.
            var ab = JsonSerializer.Deserialize<Dictionary<string, string?>>(v.AppliedBeforeJson) ?? [];
            var aa = JsonSerializer.Deserialize<Dictionary<string, string?>>(v.AppliedAfterJson ?? "{}") ?? [];
            foreach (var field in proposed.Keys) rows.Add(Shown(field) ? new FieldComparison(field, Label(field), ab.GetValueOrDefault(field), aa.GetValueOrDefault(field), Cur(field), ab.GetValueOrDefault(field) != aa.GetValueOrDefault(field)) : new FieldComparison("cf.restricted", Label(field), null, null, null, true));
        }
        else
            foreach (var field in proposed.Keys) rows.Add(Shown(field) ? new FieldComparison(field, Label(field), before.GetValueOrDefault(field), proposed[field], Cur(field), before.GetValueOrDefault(field) != proposed[field]) : new FieldComparison("cf.restricted", Label(field), null, null, null, true));
        var changedSince = v.IsOpen && before.Any(kv => current.GetValueOrDefault(kv.Key) != kv.Value);

        var docs = new List<VariationDocumentItem>();
        var links = await db.SubjectDocuments.AsNoTracking().Where(l => l.SubjectKind == SubjectKinds.Variation && l.SubjectId == variationId).ToListAsync(ct);
        foreach (var d in await secureDocuments.ListAsync(v.ContractId, ct)) // hidden above the caller's classification, as in the contract's own list
            if (links.FirstOrDefault(l => l.DocumentId == d.DocumentId) is { } link) docs.Add(new VariationDocumentItem(d.DocumentId, d.Title, d.DocumentType, link.Role, d.FileName, d.IsExecutedCopy, d.CanDownload));

        var steps = await engine.AvailableAsync(subject, resolved, ct);
        var role = resolved.Definition.State(subject.Status)?.Role ?? "";
        var mayEdit = v.IsOpen && EditableRoles.Contains(role) && (await access.CheckSilentAsync("variations.create", descriptor, ct)).Allowed;
        var mayDocs = v.IsOpen && DocumentRoles.Contains(role) && (await access.CheckSilentAsync("variations.create", descriptor, ct)).Allowed;
        var parent = await contracts.GetAsync(v.ContractId, ct);
        var summary = new VariationListItem(v.Id, v.ContractId, parent.AgreementReference, parent.Title, parent.SupplierName, parent.CategoryName, v.Reference, v.Title, v.Status, resolved.Definition.DisplayOf(v.Status), v.IsOpen, v.Outcome,
            v.ProposedDate, v.FinancialImpactAmount, v.FinancialImpactCurrency, v.CreatedBy, v.CreatedUtc, v.UpdatedUtc, v.CompletedUtc);
        // The workflow names the core fields; the configured fields offered are those this person may see and the workflow allows.
        var custom = mayEdit ? await ChangeableCustomFieldsAsync(v.ContractId, ct) : [];
        var changeable = resolved.Definition.Settings.Variation.ChangeableFields.Where(f => !f.StartsWith("cf.", StringComparison.Ordinal)).Concat(custom.Select(d => "cf." + d.Key)).ToList();
        return new VariationDetail(summary, v.Description, v.Reason, v.LegalReviewRequired, v.FinancialImpactNote, v.OutcomeReason, v.ParentStatusBefore, changedSince, rows, docs, steps, mayEdit, mayDocs,
            changeable, custom.ToDictionary(d => "cf." + d.Key, d => d.Label), v.OrganisationId);
    }

    private static readonly string[] DocumentRoles = ["initial", "legalRework", "legalCleared", "drafting"];

    // ---- documents and plain steps --------------------------------------------------------------------------------------------------------------

    public async Task<VariationDocumentItem> AddDocumentAsync(AddVariationDocumentRequest r, CancellationToken ct = default)
    {
        var subject = await engine.LoadSubjectAsync(SubjectKinds.Variation, r.VariationId, ct);
        var resolved = await engine.DefinitionForAsync(subject, ct);
        var descriptor = await engine.DescribeAsync(subject, null, ct);
        await access.RequireAsync("variations.create", descriptor, ct);
        if (!subject.Variation!.IsOpen || !DocumentRoles.Contains(resolved.Definition.State(subject.Status)?.Role ?? ""))
            throw new DomainException("documents_closed", "[[document|plural]] are added to [[variation|a]] while it is being prepared.");
        if (r.Role is not ("Final" or "Supporting")) throw new DomainException("invalid_role", "[[document|lower|a|cap]] is Final or Supporting.");
        var scale = await access.ClassificationScaleAsync(ct);
        var requested = scale.Canonical(r.Classification) ?? (string.IsNullOrWhiteSpace(r.Classification) ? null : throw new DomainException("invalid_classification", $"Classification '{r.Classification}' is not recognised."));
        var level = requested is not null && scale.Rank(requested) >= scale.Rank(subject.Classification) ? requested : subject.Classification;
        if (!(await access.CheckSilentAsync("contracts.read", descriptor with { Classification = level }, ct)).Allowed) throw new ForbiddenException("You cannot classify a document above your own access.");
        var type = r.Role == "Final" ? resolved.Definition.Settings.Documents.FinalTypes[0] : "SupportingDocument";
        var dto = await documentService.AddAsync(new AddDocumentRequest(subject.OrganisationId, subject.ContractId, type, r.Title, r.FileName, r.MimeType, r.Content, level), ct);
        await uow.ExecuteAsync(async c =>
        {
            db.SubjectDocuments.Add(SubjectDocument.Link(tenant.RequireTenantId(), SubjectKinds.Variation, subject.Id, subject.ContractId, dto.DocumentId, r.Role, engine.ActorLabel, clock.UtcNow));
            await engine.SaveAsync(c);
            audit.Record(new AuditEntry("variation.document_added", nameof(Variation), subject.Id.ToString(), OrganisationId: subject.OrganisationId, Classification: level,
                NewValuesJson: AuditJson.Of(new { dto.DocumentId, role = r.Role, dto.Sha256 })));
            await db.SaveChangesAsync(c);
        }, ct);
        return new VariationDocumentItem(dto.DocumentId, r.Title, type, r.Role, dto.FileName, false, dto.Downloadable);
    }

    public Task<TransitionResult> TransitionAsync(Guid variationId, string transitionCode, string? comment, CancellationToken ct = default)
        => engine.ExecuteAsync(new TransitionCommand(SubjectKinds.Variation, variationId, transitionCode, comment, comment), null, ct);

    public async Task CancelAsync(Guid variationId, string reason, CancellationToken ct = default)
    {
        var subject = await engine.LoadSubjectAsync(SubjectKinds.Variation, variationId, ct);
        var resolved = await engine.DefinitionForAsync(subject, ct);
        var transition = resolved.Definition.From(subject.Status).FirstOrDefault(t => t.Role == "cancel")
            ?? throw new DomainException("invalid_transition", $"This [[variation]] cannot be cancelled while it is {resolved.Definition.DisplayOf(subject.Status)}. Recall it from [[reviewer]] first, or have it rejected.");
        await engine.ExecuteAsync(new TransitionCommand(SubjectKinds.Variation, variationId, transition.Code, reason, reason, subject.Status, AllowManaged: true), async (scope, c) =>
        {
            await legalReviews.WithdrawOpenAsync(scope, reason, c); // a review still open is withdrawn with it (and kept)
            await CloseAsync(scope, VariationOutcome.Cancelled, reason, null, c);
        }, ct);
    }

    // ---- outcomes -------------------------------------------------------------------------------------------------------------------------------

    public Task ApplyApprovedAsync(TransitionScope s, ApprovalRequest request, string? comment, CancellationToken ct)
        => CloseAsync(s, VariationOutcome.Applied, comment, request, ct);

    public Task RejectAsync(TransitionScope s, ApprovalRequest request, string comment, CancellationToken ct)
        => CloseAsync(s, VariationOutcome.Rejected, comment, request, ct);

    /// <summary>
    /// Finishes the Variation and returns the parent. The parent's own transition runs through the same engine (its guards, permission and history), and an applied Variation changes the parent's fields
    /// in that same step, so either everything happens and is recorded, or nothing does.
    /// </summary>
    private async Task CloseAsync(TransitionScope variationScope, VariationOutcome outcome, string? reason, ApprovalRequest? approval, CancellationToken c)
    {
        var variation = await db.Variations.FirstAsync(v => v.Id == variationScope.Subject.Id, c);
        var parent = await engine.LoadSubjectAsync(SubjectKinds.Contract, variation.ContractId, c);
        var parentDef = await engine.DefinitionForAsync(parent, c);
        var role = outcome switch { VariationOutcome.Applied => "applyVariation", VariationOutcome.Rejected => "rejectVariation", _ => "cancelVariation" };
        var parentTransition = parentDef.Definition.From(parent.Status).FirstOrDefault(t => t.Role == role)
            ?? throw new DomainException("invalid_transition", $"The [[agreement|lower]] is {parentDef.Definition.DisplayOf(parent.Status)}, so this [[variation]] cannot be completed. Reload the [[agreement|lower]] to see where it stands.");
        string? appliedBefore = null, appliedAfter = null;

        await engine.ExecuteAsync(new TransitionCommand(SubjectKinds.Contract, parent.Id, parentTransition.Code, reason, reason, parent.Status, AllowManaged: true, Inputs: new GuardInputs(outcome), Approval: approval), async (scope, cc) =>
        {
            if (outcome != VariationOutcome.Applied) return;
            var proposed = JsonSerializer.Deserialize<Dictionary<string, string?>>(variation.ProposedChangesJson) ?? [];
            var variationDef = await definitions.ResolveAsync(WorkflowKinds.Variation, variation.OrganisationId, variation.ConfigReleaseId, cc);
            IReadOnlyList<FieldChange> applied;
            try { applied = await port.ApplyControlledChangesAsync(parent.Id, proposed, variationDef.Definition.Settings.Variation.ChangeableFields, engine.ActorLabel, "variation", variation.Id, $"Variation {variation.Reference}", cc); }
            catch (DomainException ex) when (ex.Code is not ("concurrency_conflict" or "state_changed"))
            { throw new DomainException("variation_cannot_apply", $"The [[variation]] cannot be applied to the [[agreement|lower]] as it stands: {ex.Message}"); }
            appliedBefore = JsonSerializer.Serialize(applied.ToDictionary(f => f.Field, f => f.Before));
            appliedAfter = JsonSerializer.Serialize(applied.ToDictionary(f => f.Field, f => f.After));
        }, c);

        variation.Complete(outcome, variationScope.Transition.To, reason, appliedBefore, appliedAfter, variationScope.NowUtc);
        await db.SaveChangesAsync(c);
        var type = outcome switch { VariationOutcome.Applied => LifecycleEventTypes.VariationApplied, VariationOutcome.Rejected => LifecycleEventTypes.VariationRejected, _ => LifecycleEventTypes.VariationCancelled };
        events.Raise(type, variationScope.Subject.OrganisationId, SubjectKinds.Variation, variation.Id, variation.ContractId, variationScope.Subject.Classification, new { variationId = variation.Id, reference = variation.Reference, outcome = outcome.ToString(), submitterUserId = variation.CreatedByUserId });
        audit.Record(new AuditEntry("variation." + outcome.ToString().ToLowerInvariant(), nameof(Variation), variation.Id.ToString(), OrganisationId: variation.OrganisationId, Reason: reason, Classification: variationScope.Subject.Classification,
            OldValuesJson: appliedBefore, NewValuesJson: AuditJson.Of(new { variation.Reference, contractId = variation.ContractId, outcome = outcome.ToString(), after = appliedAfter is null ? null : JsonSerializer.Deserialize<Dictionary<string, string?>>(appliedAfter) })));
    }
}
