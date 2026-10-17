using Ecp.Audit;
using Ecp.Contracts.Domain;
using Ecp.Contracts.Persistence;
using Ecp.Persistence;
using Ecp.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Ecp.Contracts;

/// <summary>A contract as the lifecycle engine needs to see it. No authorisation: the caller (the lifecycle module) has already authorised the actor.</summary>
public sealed record ContractFacts(
    Guid Id, Guid TenantId, Guid OrganisationId, string AgreementReference, string Title, string Status, bool RecordLocked, bool ContentFrozen, string DocumentPolicy, string Classification,
    Guid SupplierId, Guid CategoryId, Guid? BusinessUnitId, Guid? SiteId, bool LegalReviewRequired, string? CurrencyCode, decimal? EstimatedValue, string? ExtensionType,
    DateOnly? StartDate, DateOnly? EffectiveDate, DateOnly? ReviewDate, DateOnly? EndDate, int? NoticePeriodDays, bool CriticalNotice, int? ReminderLeadDays,
    DateTime? RegisteredDateUtc, string? RegisteredBy, string? WorkflowRef, Guid? ConfigReleaseId, Guid? ActiveUnlockId, DateTime? UnlockExpiresUtc,
    Guid? PredecessorContractId, Guid? SuccessorContractId, bool IsArchived, bool IsLegacyLoad, string CreatedBy, DateTime CreatedUtc, DateTime UpdatedUtc, string? Description,
    string? LockReason, string? LockedBy, DateTime? LockedUtc,
    Guid? LegalEntityId = null, string? ContractType = null, string? AgreementType = null, string? RiskLevel = null, string? FunctionCode = null, string? SapOutlineAgreementNumber = null,
    int? NoticePeriodValue = null, string? NoticePeriodUnit = null, string? SupplierType = null);

/// <summary>What a new contract starts as: its initial state and protection, and the workflow definition it is pinned to. Supplied by the lifecycle module when it is registered.</summary>
public sealed record ContractCreationSetup(string InitialStatus, StateEffects Effects, string? WorkflowRef, Guid? ConfigReleaseId);

/// <summary>Lets the lifecycle module decide the initial state of a new contract and pin it to the workflow in force. Without it, <see cref="ContractsOptions.InitialStatusCode"/> is used.</summary>
public interface IContractCreationPolicy
{
    Task<ContractCreationSetup> ResolveAsync(Guid organisationId, CancellationToken ct = default);
}

/// <summary>
/// Tells the contract access rules which contracts have a register-authorisation request waiting, and for which approver group. An Approver or Backup Approver can read a contract only while one is
/// waiting for their group; the lifecycle module supplies the facts, so the rule stays in the access evaluator and the facts stay with their owner.
/// </summary>
public interface IPendingApprovalSource
{
    /// <summary>Contract id to the approver groups with a request waiting on it (a Variation's request counts for its parent contract). Null asks for all.</summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlySet<string>>> PendingAsync(IReadOnlyCollection<Guid>? contractIds, CancellationToken ct = default);
}

/// <summary>A contract's dates and reminder attributes, for the reminder scan.</summary>
public sealed record ReminderCandidate(Guid ContractId, Guid OrganisationId, string AgreementReference, string Title, string Status, string Classification, Guid CategoryId,
    DateOnly? ReviewDate, DateOnly? EndDate, string? ExtensionType, int? NoticePeriodDays, bool CriticalNotice, int? ReminderLeadDays, bool IsArchived);

public sealed record SuccessorSeed(string AgreementReference, string Title, string InitialStatus, StateEffects Effects, string? WorkflowRef, Guid? ConfigReleaseId, string? Description,
    DateOnly? StartDate, DateOnly? EffectiveDate, DateOnly? ReviewDate, DateOnly? EndDate, string? CurrencyCode, decimal? EstimatedValue, string? ExtensionType,
    int? NoticePeriodDays, bool CriticalNotice, int? ReminderLeadDays, bool LegalReviewRequired);

/// <summary>
/// The trusted operations the lifecycle engine needs from Contracts. Every write joins the caller's unit of work (so the lifecycle's own records commit atomically with the change),
/// is concurrency-checked, and records an audit event that carries the record's classification.
/// </summary>
public interface IContractLifecyclePort
{
    Task<ContractFacts?> FindAsync(Guid contractId, CancellationToken ct = default);
    Task<IReadOnlyList<ContractFacts>> FindManyAsync(IReadOnlyCollection<Guid> contractIds, CancellationToken ct = default);

    /// <summary>
    /// Moves the contract from <paramref name="expectedStatus"/> to <paramref name="newStatus"/>. A second writer that read the same state fails with <c>state_changed</c>;
    /// a concurrent save fails with <c>concurrency_conflict</c>. Entering a locking state locks the record; no transition unlocks it.
    /// </summary>
    Task ApplyTransitionAsync(Guid contractId, string expectedStatus, string newStatus, StateEffects effects, string actor, string transitionCode, string? comment, CancellationToken ct = default);

    Task PinWorkflowAsync(Guid contractId, string workflowRef, Guid? configReleaseId, CancellationToken ct = default);
    Task StampRegistrationAsync(Guid contractId, string registeredBy, CancellationToken ct = default);
    Task OpenUnlockAsync(Guid contractId, Guid unlockId, DateTime expiresUtc, string actor, CancellationToken ct = default);
    Task CloseUnlockAsync(Guid contractId, string actor, Guid? unlockId, CancellationToken ct = default);

    /// <summary>Applies named changes through a controlled path (an applied Variation, or an emergency unlock) and returns what changed, before and after. The audit event carries <paramref name="authorityId"/>.</summary>
    Task<IReadOnlyList<FieldChange>> ApplyControlledChangesAsync(Guid contractId, IReadOnlyDictionary<string, string?> changes, IReadOnlyCollection<string> allowed, string actor,
        string authorityKind, Guid authorityId, string? reason, CancellationToken ct = default);

    /// <summary>Checks named changes against the same rules without saving anything, and returns what would change. Used to validate and preview a Variation's proposed changes.</summary>
    Task<IReadOnlyList<FieldChange>> PreviewControlledChangesAsync(Guid contractId, IReadOnlyDictionary<string, string?> changes, IReadOnlyCollection<string> allowed, CancellationToken ct = default);
    /// <summary>The current values of the named fields, as text, for a before/after comparison.</summary>
    Task<IReadOnlyDictionary<string, string?>> CurrentValuesAsync(Guid contractId, IReadOnlyCollection<string> fields, CancellationToken ct = default);

    Task SetReminderSettingsAsync(Guid contractId, int? leadDays, int? noticePeriodDays, bool criticalNotice, string actor, CancellationToken ct = default);
    /// <summary>Changes only the reminder lead time, in any workflow state: it is a preference, not contract content. Audited.</summary>
    Task SetReminderLeadAsync(Guid contractId, int? leadDays, string actor, CancellationToken ct = default);
    /// <summary>Creates the successor Draft of a renewal and links both ways. Returns the new contract id. Nothing of the predecessor's history is copied.</summary>
    Task<Guid> CreateSuccessorAsync(Guid predecessorId, SuccessorSeed seed, string actor, CancellationToken ct = default);

    /// <summary>Contracts with a Review Date or End Date, for the reminder scan (archived ones excluded).</summary>
    Task<IReadOnlyList<ReminderCandidate>> ReminderCandidatesAsync(CancellationToken ct = default);
    /// <summary>The users who currently own the contract's category (the category and its ancestors), most specific first.</summary>
    Task<IReadOnlyList<Guid>> CurrentOwnersAsync(Guid contractId, CancellationToken ct = default);
}

internal sealed class ContractLifecyclePort(
    ContractsDbContext db, IUnitOfWork uow, IAuditWriter audit, IClock clock, ITenantContext tenant, ContractAccessContext context, ReferenceAllocator allocator, Ecp.Identity.IClassificationScaleProvider scales,
    IContractCustomFieldWriter customFields) : IContractLifecyclePort
{
    /// <summary>A configured field is named <c>cf.</c> plus its key wherever it travels with the core fields (a Variation's proposed changes, comparisons, audit).</summary>
    public const string CustomPrefix = "cf.";

    /// <summary>Splits named changes into core and configured fields. A configured field must be allowed by name (<c>cf.key</c>) or by <c>cf.*</c>.</summary>
    private static (Dictionary<string, string?> Core, Dictionary<string, string?> Custom) Split(IReadOnlyDictionary<string, string?> changes, IReadOnlyCollection<string> allowed)
    {
        var core = new Dictionary<string, string?>(StringComparer.Ordinal); var custom = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in changes)
        {
            if (!name.StartsWith(CustomPrefix, StringComparison.Ordinal)) { core[name] = value; continue; }
            if (!allowed.Contains(name, StringComparer.OrdinalIgnoreCase) && !allowed.Contains(CustomPrefix + "*", StringComparer.Ordinal)) throw new DomainException("field_not_changeable", $"'{name}' cannot be changed this way.");
            custom[name[CustomPrefix.Length..]] = value;
        }
        return (core, custom);
    }

    private static IReadOnlyList<FieldChange> Prefixed(IEnumerable<FieldChange> changes) => changes.Select(f => f with { Field = CustomPrefix + f.Field }).ToList();

    private static ContractFacts ToFacts(Contract c, string? supplierType = null) => new(c.Id, c.TenantId, c.OrganisationId, c.AgreementReference, c.ContractTitle, c.Status, c.RecordLocked, c.ContentFrozen, c.DocumentPolicy,
        c.Classification, c.SupplierId, c.CategoryId, c.BusinessUnitId, c.SiteId, c.LegalReviewRequired, c.CurrencyCode, c.EstimatedValue, c.ExtensionType, c.StartDate, c.EffectiveDate,
        c.ReviewDate, c.EndDate, c.NoticePeriodDays, c.CriticalNotice, c.ReminderLeadDays, c.RegisteredDateUtc, c.RegisteredBy, c.WorkflowRef, c.ConfigReleaseId, c.ActiveUnlockId,
        c.UnlockExpiresUtc, c.PredecessorContractId, c.SuccessorContractId, c.IsArchived, c.IsLegacyLoad, c.CreatedBy, c.CreatedUtc, c.UpdatedUtc, c.Description, c.LockReason, c.LockedBy, c.LockedUtc,
        c.LegalEntityId, c.ContractType, c.AgreementType, c.RiskLevel, c.FunctionCode, c.SapOutlineAgreementNumber, c.NoticePeriodValue, c.NoticePeriodUnit, supplierType);

    public async Task<ContractFacts?> FindAsync(Guid contractId, CancellationToken ct = default)
    {
        var c = await db.Contracts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == contractId, ct);
        if (c is null) return null;
        var type = await db.Suppliers.AsNoTracking().Where(x => x.Id == c.SupplierId).Select(x => x.SupplierType).FirstOrDefaultAsync(ct);
        return ToFacts(c, type);
    }

    public async Task<IReadOnlyList<ContractFacts>> FindManyAsync(IReadOnlyCollection<Guid> contractIds, CancellationToken ct = default)
    {
        var rows = new List<Contract>();
        foreach (var chunk in contractIds.Distinct().Chunk(500))
        {
            var ids = chunk.ToList();
            rows.AddRange(await db.Contracts.AsNoTracking().Where(c => ids.Contains(c.Id)).ToListAsync(ct));
        }
        var supplierIds = rows.Select(r => r.SupplierId).Distinct().ToList();
        var types = new Dictionary<Guid, string?>();
        foreach (var chunk in supplierIds.Chunk(500)) { var ids = chunk.ToList(); foreach (var x in await db.Suppliers.AsNoTracking().Where(x => ids.Contains(x.Id)).Select(x => new { x.Id, x.SupplierType }).ToListAsync(ct)) types[x.Id] = x.SupplierType; }
        return rows.Select(r => ToFacts(r, types.GetValueOrDefault(r.SupplierId))).ToList();
    }

    private async Task<Contract> LoadTrackedAsync(Guid id, CancellationToken ct)
        => await db.Contracts.FirstOrDefaultAsync(c => c.Id == id, ct) ?? throw new DomainException("contract_not_found", "The [[contract|lower]] was not found.");

    private static async Task SaveAsync(ContractsDbContext db, CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw new DomainException("concurrency_conflict", "The record was changed by someone else at the same time. Reload it and try again."); }
    }

    public Task ApplyTransitionAsync(Guid contractId, string expectedStatus, string newStatus, StateEffects effects, string actor, string transitionCode, string? comment, CancellationToken ct = default)
        => uow.ExecuteAsync(async c =>
        {
            var contract = await LoadTrackedAsync(contractId, c);
            if (!string.Equals(contract.Status, expectedStatus, StringComparison.Ordinal))
                throw new DomainException("state_changed", "The [[contract|lower]] has moved on since you opened it. Reload it to see its current state.");
            var wasLocked = contract.RecordLocked;
            contract.ApplyWorkflowState(newStatus, effects, actor, clock.UtcNow);
            await SaveAsync(db, c);
            audit.Record(new AuditEntry("contract.transitioned", nameof(Contract), contractId.ToString(), OrganisationId: contract.OrganisationId, Reason: comment, Classification: contract.Classification,
                OldValuesJson: AuditJson.Of(new { status = expectedStatus }), NewValuesJson: AuditJson.Of(new { status = newStatus, transition = transitionCode, locked = contract.RecordLocked, lockedNow = contract.RecordLocked && !wasLocked, frozen = contract.ContentFrozen })));
        }, ct);

    public Task PinWorkflowAsync(Guid contractId, string workflowRef, Guid? configReleaseId, CancellationToken ct = default)
        => uow.ExecuteAsync(async c =>
        {
            var contract = await LoadTrackedAsync(contractId, c);
            if (contract.WorkflowRef is not null) return;
            contract.PinWorkflow(workflowRef, configReleaseId);
            await SaveAsync(db, c);
            audit.Record(new AuditEntry("contract.workflow_pinned", nameof(Contract), contractId.ToString(), OrganisationId: contract.OrganisationId, Classification: contract.Classification,
                ConfigReleaseId: configReleaseId, NewValuesJson: AuditJson.Of(new { workflowRef })));
        }, ct);

    public Task StampRegistrationAsync(Guid contractId, string registeredBy, CancellationToken ct = default)
        => uow.ExecuteAsync(async c =>
        {
            var contract = await LoadTrackedAsync(contractId, c);
            contract.StampRegistration(registeredBy, clock.UtcNow);
            await SaveAsync(db, c);
            audit.Record(new AuditEntry("contract.registered", nameof(Contract), contractId.ToString(), OrganisationId: contract.OrganisationId, Classification: contract.Classification,
                NewValuesJson: AuditJson.Of(new { registeredDateUtc = contract.RegisteredDateUtc, registeredBy })));
        }, ct);

    public Task OpenUnlockAsync(Guid contractId, Guid unlockId, DateTime expiresUtc, string actor, CancellationToken ct = default)
        => uow.ExecuteAsync(async c =>
        {
            var contract = await LoadTrackedAsync(contractId, c);
            contract.OpenUnlock(unlockId, expiresUtc, actor, clock.UtcNow);
            await SaveAsync(db, c);
            audit.Record(new AuditEntry("contract.unlock_opened", nameof(Contract), contractId.ToString(), OrganisationId: contract.OrganisationId, Classification: contract.Classification,
                ElevationId: unlockId, NewValuesJson: AuditJson.Of(new { unlockId, expiresUtc })));
        }, ct);

    public Task CloseUnlockAsync(Guid contractId, string actor, Guid? unlockId, CancellationToken ct = default)
        => uow.ExecuteAsync(async c =>
        {
            var contract = await LoadTrackedAsync(contractId, c);
            if (contract.ActiveUnlockId is null) return;
            if (unlockId is { } expected && contract.ActiveUnlockId != expected) return;
            var closed = contract.ActiveUnlockId;
            contract.CloseUnlock(actor, clock.UtcNow);
            await SaveAsync(db, c);
            audit.Record(new AuditEntry("contract.relocked", nameof(Contract), contractId.ToString(), OrganisationId: contract.OrganisationId, Classification: contract.Classification,
                ElevationId: closed, NewValuesJson: AuditJson.Of(new { unlockId = closed })));
        }, ct);

    public Task<IReadOnlyList<FieldChange>> ApplyControlledChangesAsync(Guid contractId, IReadOnlyDictionary<string, string?> changes, IReadOnlyCollection<string> allowed, string actor,
        string authorityKind, Guid authorityId, string? reason, CancellationToken ct = default)
        => uow.ExecuteAsync<IReadOnlyList<FieldChange>>(async c =>
        {
            var (coreChanges, customChanges) = Split(changes, allowed);
            var applied = new List<FieldChange>();
            if (coreChanges.Count > 0)
            {
                var contract = await LoadTrackedAsync(contractId, c);
                var before = contract.Classification;
                var scale = await scales.GetAsync(c);
                var core = contract.ApplyControlledFields(coreChanges, allowed.Where(a => !a.StartsWith(CustomPrefix, StringComparison.Ordinal)).ToList(), actor, clock.UtcNow);
                await SaveAsync(db, c);
                // The event is classified at the higher of the old and new level, so a change away from a restricted level is never visible to people who could not see the record before it.
                audit.Record(new AuditEntry("contract.fields_changed", nameof(Contract), contractId.ToString(), OrganisationId: contract.OrganisationId, Reason: reason, ElevationId: authorityKind == "unlock" ? authorityId : null,
                    Classification: scale.Rank(contract.Classification) >= scale.Rank(before) ? contract.Classification : before,
                    OldValuesJson: AuditJson.Of(core.ToDictionary(f => f.Field, f => f.Before)), NewValuesJson: AuditJson.Of(new { authority = authorityKind, authorityId, changes = core.ToDictionary(f => f.Field, f => f.After) })));
                applied.AddRange(core);
            }
            // The configured fields are written in the same unit of work, with their own audit event at the most sensitive level among the fields changed.
            if (customChanges.Count > 0) applied.AddRange(Prefixed(await customFields.ApplyAsync(contractId, customChanges, actor, authorityKind, authorityId, reason, c)));
            return applied;
        }, ct);

    public async Task<IReadOnlyList<FieldChange>> PreviewControlledChangesAsync(Guid contractId, IReadOnlyDictionary<string, string?> changes, IReadOnlyCollection<string> allowed, CancellationToken ct = default)
    {
        var (coreChanges, customChanges) = Split(changes, allowed);
        var result = new List<FieldChange>();
        if (coreChanges.Count > 0)
        {
            // An untracked copy takes the change, so the rules run exactly as they would on apply but nothing is stored.
            var copy = await db.Contracts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == contractId, ct) ?? throw new DomainException("contract_not_found", "The [[contract|lower]] was not found.");
            try { result.AddRange(copy.ApplyControlledFields(coreChanges, allowed.Where(a => !a.StartsWith(CustomPrefix, StringComparison.Ordinal)).ToList(), "preview", clock.UtcNow)); }
            catch (FormatException) { throw new DomainException("invalid_value", "One of the values is not in a recognised format."); }
            catch (OverflowException) { throw new DomainException("invalid_value", "One of the values is out of range."); }
        }
        if (customChanges.Count > 0) result.AddRange(Prefixed(await customFields.PreviewAsync(contractId, customChanges, ct)));
        return result;
    }

    public async Task<IReadOnlyDictionary<string, string?>> CurrentValuesAsync(Guid contractId, IReadOnlyCollection<string> fields, CancellationToken ct = default)
    {
        var c = await db.Contracts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == contractId, ct) ?? throw new DomainException("contract_not_found", "The [[contract|lower]] was not found.");
        var result = new Dictionary<string, string?>(c.CaptureFields(fields.Where(f => !f.StartsWith(CustomPrefix, StringComparison.Ordinal))), StringComparer.Ordinal);
        var customKeys = fields.Where(f => f.StartsWith(CustomPrefix, StringComparison.Ordinal)).Select(f => f[CustomPrefix.Length..]).ToList();
        if (customKeys.Count > 0)
            foreach (var (key, value) in await customFields.CurrentAsync(contractId, customKeys, ct)) result[CustomPrefix + key] = value;
        return result;
    }

    public Task SetReminderSettingsAsync(Guid contractId, int? leadDays, int? noticePeriodDays, bool criticalNotice, string actor, CancellationToken ct = default)
        => uow.ExecuteAsync(async c =>
        {
            var contract = await LoadTrackedAsync(contractId, c);
            var old = AuditJson.Of(new { contract.ReminderLeadDays, contract.NoticePeriodDays, contract.CriticalNotice });
            contract.SetReminderSettings(leadDays, noticePeriodDays, criticalNotice, actor, clock.UtcNow);
            await SaveAsync(db, c);
            audit.Record(new AuditEntry("contract.reminder_settings_changed", nameof(Contract), contractId.ToString(), OrganisationId: contract.OrganisationId, Classification: contract.Classification,
                OldValuesJson: old, NewValuesJson: AuditJson.Of(new { leadDays, noticePeriodDays, criticalNotice })));
        }, ct);

    public Task SetReminderLeadAsync(Guid contractId, int? leadDays, string actor, CancellationToken ct = default)
        => uow.ExecuteAsync(async c =>
        {
            var contract = await LoadTrackedAsync(contractId, c);
            var old = contract.ReminderLeadDays;
            contract.SetReminderLead(leadDays, actor, clock.UtcNow);
            await SaveAsync(db, c);
            audit.Record(new AuditEntry("contract.reminder_lead_changed", nameof(Contract), contractId.ToString(), OrganisationId: contract.OrganisationId, Classification: contract.Classification,
                OldValuesJson: AuditJson.Of(new { leadDays = old }), NewValuesJson: AuditJson.Of(new { leadDays })));
        }, ct);

    public Task<Guid> CreateSuccessorAsync(Guid predecessorId, SuccessorSeed seed, string actor, CancellationToken ct = default)
        => uow.ExecuteAsync(async c =>
        {
            var predecessor = await LoadTrackedAsync(predecessorId, c);
            var now = clock.UtcNow;
            var reference = string.IsNullOrWhiteSpace(seed.AgreementReference) ? await allocator.AllocateAsync(predecessor.OrganisationId, consume: true, c) : seed.AgreementReference;
            var successor = Contract.CreateSuccessor(new NewContract(
                tenant.RequireTenantId(), predecessor.OrganisationId, reference, seed.Title, predecessor.SupplierId, predecessor.CategoryId, seed.InitialStatus, actor, now, seed.Description,
                predecessor.Classification, seed.CurrencyCode, seed.EstimatedValue, seed.ExtensionType, seed.LegalReviewRequired, seed.StartDate, seed.EffectiveDate, seed.ReviewDate, seed.EndDate,
                predecessor.BusinessUnitId, predecessor.SiteId), predecessorId, seed.NoticePeriodDays, seed.CriticalNotice, seed.ReminderLeadDays);
            successor.ApplyWorkflowState(seed.InitialStatus, seed.Effects, actor, now);
            if (seed.WorkflowRef is not null) successor.PinWorkflow(seed.WorkflowRef, seed.ConfigReleaseId);
            db.Contracts.Add(successor);
            predecessor.LinkSuccessor(successor.Id, actor, now);
            try { await db.SaveChangesAsync(c); }
            catch (DbUpdateConcurrencyException) { throw new DomainException("concurrency_conflict", "The record was changed by someone else at the same time. Reload it and try again."); }
            catch (DbUpdateException ex) when (DbExceptions.IsUniqueViolation(ex)) { throw new DomainException("agreement_reference_in_use", "That [[contract.reference|lower]] is already in use."); }
            audit.Record(new AuditEntry("contract.created", nameof(Contract), successor.Id.ToString(), OrganisationId: successor.OrganisationId, Classification: successor.Classification,
                NewValuesJson: AuditJson.Of(new { successor.AgreementReference, successor.ContractTitle, successor.Status, successor.Classification, renewalOf = predecessor.AgreementReference })));
            audit.Record(new AuditEntry("contract.successor_linked", nameof(Contract), predecessorId.ToString(), OrganisationId: predecessor.OrganisationId, Classification: predecessor.Classification,
                NewValuesJson: AuditJson.Of(new { successor = successor.AgreementReference })));
            return successor.Id;
        }, ct);

    public async Task<IReadOnlyList<ReminderCandidate>> ReminderCandidatesAsync(CancellationToken ct = default)
        => await db.Contracts.AsNoTracking().Where(c => !c.IsArchived && (c.EndDate != null || c.ReviewDate != null))
            .Select(c => new ReminderCandidate(c.Id, c.OrganisationId, c.AgreementReference, c.ContractTitle, c.Status, c.Classification, c.CategoryId, c.ReviewDate, c.EndDate, c.ExtensionType,
                c.NoticePeriodDays, c.CriticalNotice, c.ReminderLeadDays, c.IsArchived)).ToListAsync(ct);

    public async Task<IReadOnlyList<Guid>> CurrentOwnersAsync(Guid contractId, CancellationToken ct = default)
    {
        var contract = await db.Contracts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == contractId, ct);
        if (contract is null) return [];
        var (_, _, ancestry) = await context.CategoryTreeAsync(ct);
        var path = ancestry.TryGetValue(contract.CategoryId, out var p) ? p : [contract.CategoryId];
        var now = clock.UtcNow;
        var assignments = (await db.CategoryAssignments.AsNoTracking().Where(a => path.Contains(a.CategoryId)).ToListAsync(ct)).Where(a => a.IsActiveAt(now)).ToList();
        // The most specific category's owners come first; the primary owner leads within a category.
        return path.SelectMany(cat => assignments.Where(a => a.CategoryId == cat).OrderByDescending(a => a.IsPrimary).ThenBy(a => a.CreatedUtc).Select(a => a.UserId)).Distinct().ToList();
    }
}
