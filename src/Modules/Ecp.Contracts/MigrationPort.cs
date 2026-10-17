using System.Globalization;
using Ecp.Audit;
using Ecp.Contracts.Domain;
using Ecp.Contracts.Persistence;
using Ecp.Metadata;
using Ecp.Persistence;
using Ecp.SharedKernel;
using Ecp.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Ecp.Contracts;

public sealed record SupplierRef(Guid Id, string Name, string NormalisedName, string? VendorNumber, string? SourceSystem, Guid? MigrationBatchId);
public sealed record CategoryRef(Guid Id, string Code, string Name, string NormalisedName, Guid? ParentCategoryId, bool IsActive);
public sealed record ReferenceSnapshot(IReadOnlyList<SupplierRef> Suppliers, IReadOnlyList<CategoryRef> Categories);

/// <summary>An existing target contract as a migration needs to see it when deciding between create, update and skip.</summary>
public sealed record ExistingContractRef(
    Guid Id, string AgreementReference, string? SourceSystem, string? SourceRecordId, bool RecordLocked, bool IsLegacyLoad, bool IsArchived, string Status,
    Guid? MigrationBatchId, DateTime CreatedUtc, DateTime UpdatedUtc);

/// <summary>A contract to create from a migration. <see cref="LockAsLegacy"/> creates a locked legacy record under a legacy-load authorisation; otherwise an editable record in the initial state.</summary>
public sealed record MigratedContract(
    Guid OrganisationId, Guid BatchId, string AgreementReference, string? SourceAgreementReference, string Title, string? Description, Guid SupplierId, Guid CategoryId,
    string StatusCode, bool LockAsLegacy, string SourceSystem, string SourceRecordId, string? SourceStatus, DateTime? SourceExtractedUtc, string Classification,
    string? CurrencyCode, decimal? EstimatedValue, string? ExtensionType, bool LegalReviewRequired, DateOnly? StartDate, DateOnly? EffectiveDate, DateOnly? ReviewDate,
    DateOnly? EndDate, DateTime? RegisteredUtc, string? RegisteredSourceValue, bool ImportRegisteredDate, string ActingAdministrator,
    string? ContractType = null, string? AgreementType = null, string? RiskLevel = null, string? FunctionCode = null, string? SapOutlineAgreementNumber = null, int? NoticePeriodValue = null,
    string? NoticePeriodUnit = null, bool CriticalNotice = false,
    IReadOnlyDictionary<string, string?>? CustomValues = null, IReadOnlyDictionary<string, string>? CustomSourceValues = null);

public sealed record ContractFieldUpdate(IReadOnlyDictionary<string, string?> Before, DateTime AfterStampUtc);

public sealed record BatchContractSummary(
    int Contracts, IReadOnlyDictionary<string, int> ByStatus, IReadOnlyDictionary<string, int> ByCategoryCode, IReadOnlyDictionary<string, decimal> ValueByCurrency);

/// <summary>
/// The trusted operations a migration needs from the Contracts module. There is no per-user authorisation here: the migration module authorises the
/// caller (migration.* permissions) before using it, and nothing in request input can reach it directly. Every write records an audit event that carries the batch id.
/// </summary>
public interface IContractMigrationPort
{
    /// <summary>The workflow state a new editable record of the organisation starts in: the initial state of the organisation's own lifecycle definition. Refused (<c>no_solution_pack</c>) when the organisation has none.</summary>
    Task<string> InitialStatusAsync(Guid organisationId, CancellationToken ct = default);
    Task<ReferenceSnapshot> SnapshotReferenceDataAsync(Guid organisationId, CancellationToken ct = default);
    /// <summary>Contracts that match any of the given source record ids (within the organisation and source system) or agreement references (tenant-wide, normalised).</summary>
    Task<IReadOnlyList<ExistingContractRef>> FindContractsAsync(Guid organisationId, string sourceSystem, IReadOnlyCollection<string> sourceRecordIds,
        IReadOnlyCollection<string> agreementReferences, CancellationToken ct = default);
    Task<string> AllocateReferenceAsync(Guid organisationId, CancellationToken ct = default);
    Task<Guid> CreateSupplierAsync(Guid organisationId, Guid batchId, string name, string? vendorNumber, string sourceSystem, string sourceRecordId, CancellationToken ct = default);
    Task<Guid> CreateCategoryAsync(Guid organisationId, Guid batchId, string code, string name, Guid? parentId, string sourceSystem, string sourceRecordId, CancellationToken ct = default);
    Task<Guid> CreateContractAsync(MigratedContract contract, CancellationToken ct = default);
    /// <summary>Updates only the named fields and returns their previous values. A locked record needs <paramref name="lockedOverwriteAuthorised"/>.</summary>
    Task<ContractFieldUpdate> UpdateContractFieldsAsync(Guid contractId, Guid batchId, IReadOnlyDictionary<string, string?> fields, bool lockedOverwriteAuthorised,
        string reason, CancellationToken ct = default);
    /// <summary>Checks configured field values against the organisation's published definitions. Built once for a batch; no database work per row.</summary>
    Task<IMigrationCustomChecker> CustomFieldCheckerAsync(Guid organisationId, CancellationToken ct = default);
    Task<IReadOnlyDictionary<string, string?>> CaptureFieldsAsync(Guid contractId, IEnumerable<string> fieldNames, CancellationToken ct = default);
    /// <summary>The named fields of many contracts at once (for comparing a source row with what is already in the target).</summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, string?>>> CaptureFieldsManyAsync(IReadOnlyCollection<Guid> contractIds, IReadOnlyCollection<string> fieldNames, CancellationToken ct = default);
    Task<ExistingContractRef?> GetContractAsync(Guid contractId, CancellationToken ct = default);
    Task RemoveContractAsync(Guid contractId, Guid batchId, string reason, CancellationToken ct = default);
    Task ArchiveContractAsync(Guid contractId, Guid batchId, string reason, CancellationToken ct = default);
    /// <summary>Contracts that refer to the supplier, not counting the ones a rollback preview has already decided to remove.</summary>
    Task<int> CountContractsOfSupplierAsync(Guid supplierId, IReadOnlyCollection<Guid>? excluding = null, CancellationToken ct = default);
    Task<int> CountContractsOfCategoryAsync(Guid categoryId, IReadOnlyCollection<Guid>? excluding = null, CancellationToken ct = default);
    /// <summary>Child categories, not counting those a rollback has already decided to remove.</summary>
    Task<int> CountChildCategoriesAsync(Guid categoryId, IReadOnlyCollection<Guid>? excluding = null, CancellationToken ct = default);
    Task RemoveSupplierAsync(Guid supplierId, Guid batchId, string reason, CancellationToken ct = default);
    Task RemoveCategoryAsync(Guid categoryId, Guid batchId, string reason, CancellationToken ct = default);
    Task<BatchContractSummary> SummariseBatchAsync(Guid batchId, CancellationToken ct = default);
    /// <summary>Forgets entities the context is tracking, so a long migration does not keep every record it wrote in memory. Call only between committed chunks.</summary>
    void ReleaseTracked();
}

internal sealed class ContractMigrationPort(
    ContractsDbContext db, IUnitOfWork uow, IAuditWriter audit, IActorContext actor, IClock clock, ITenantContext tenant, ReferenceAllocator allocator,
    ITenantDirectory tenants, IOptions<ContractsOptions> options, IEnumerable<IContractCreationPolicy> policies, IContractCustomFieldWriter customFields, ICustomFieldCatalogue fieldCatalogue) : IContractMigrationPort
{
    // The same source the contract pages use: the lifecycle definition in force for the organisation. Only a host with no lifecycle module falls back to the configured code.
    public async Task<string> InitialStatusAsync(Guid organisationId, CancellationToken ct = default)
    {
        var policy = policies.FirstOrDefault();
        var initial = policy is null ? options.Value.InitialStatusCode : (await policy.ResolveAsync(organisationId, ct)).InitialStatus;
        if (string.IsNullOrWhiteSpace(initial))
            throw new InvalidOperationException("The organisation has no initial contract state (it comes from its lifecycle definition).");
        return initial;
    }

    public async Task<ReferenceSnapshot> SnapshotReferenceDataAsync(Guid organisationId, CancellationToken ct = default)
    {
        var suppliers = await db.Suppliers.AsNoTracking().Where(s => s.OrganisationId == organisationId)
            .Select(s => new SupplierRef(s.Id, s.Name, s.NormalisedName, s.VendorNumber, s.SourceSystem, s.MigrationBatchId)).ToListAsync(ct);
        var categories = (await db.Categories.AsNoTracking().Where(c => c.OrganisationId == organisationId)
            .Select(c => new { c.Id, c.Code, c.Name, c.ParentCategoryId, c.IsActive }).ToListAsync(ct))
            .Select(c => new CategoryRef(c.Id, c.Code, c.Name, Supplier.NormaliseName(c.Name), c.ParentCategoryId, c.IsActive)).ToList();
        return new ReferenceSnapshot(suppliers, categories);
    }

    public async Task<IReadOnlyList<ExistingContractRef>> FindContractsAsync(Guid organisationId, string sourceSystem, IReadOnlyCollection<string> sourceRecordIds,
        IReadOnlyCollection<string> agreementReferences, CancellationToken ct = default)
    {
        var found = new Dictionary<Guid, ExistingContractRef>();
        foreach (var chunk in sourceRecordIds.Distinct().Chunk(400))
        {
            var ids = chunk.ToList();
            var rows = await db.Contracts.AsNoTracking().Where(c => c.OrganisationId == organisationId && c.SourceSystem == sourceSystem && ids.Contains(c.SourceRecordId!))
                .Select(c => ToRef(c)).ToListAsync(ct);
            foreach (var r in rows) found[r.Id] = r;
        }
        foreach (var chunk in agreementReferences.Select(Contract.NormaliseReference).Distinct().Chunk(400))
        {
            var refs = chunk.ToList();
            var rows = await db.Contracts.AsNoTracking().Where(c => refs.Contains(c.AgreementReference)).Select(c => ToRef(c)).ToListAsync(ct);
            foreach (var r in rows) found[r.Id] = r;
        }
        return found.Values.OrderBy(r => r.AgreementReference, StringComparer.Ordinal).ToList();
    }

    private static ExistingContractRef ToRef(Contract c) => new(c.Id, c.AgreementReference, c.SourceSystem, c.SourceRecordId, c.RecordLocked, c.IsLegacyLoad, c.IsArchived, c.Status,
        c.MigrationBatchId, c.CreatedUtc, c.UpdatedUtc);

    public Task<string> AllocateReferenceAsync(Guid organisationId, CancellationToken ct = default) => allocator.AllocateAsync(organisationId, consume: true, ct);

    public async Task<Guid> CreateSupplierAsync(Guid organisationId, Guid batchId, string name, string? vendorNumber, string sourceSystem, string sourceRecordId, CancellationToken ct = default)
    {
        await RequireOrganisationAsync(organisationId, ct);
        var supplier = Supplier.Create(tenant.RequireTenantId(), organisationId, name, null, vendorNumber, clock.UtcNow);
        supplier.MarkMigrated(sourceSystem, sourceRecordId, batchId);
        await uow.ExecuteAsync(async c =>
        {
            db.Suppliers.Add(supplier);
            try { await db.SaveChangesAsync(c); }
            catch (DbUpdateException ex) when (DbExceptions.IsUniqueViolation(ex)) { throw new DomainException("vendor_number_in_use", "That [[supplier.number]] is already in use."); }
            audit.Record(new AuditEntry("migration.supplier.created", nameof(Supplier), supplier.Id.ToString(), OrganisationId: organisationId, MigrationBatchId: batchId,
                NewValuesJson: AuditJson.Of(new { supplier.Name, supplier.VendorNumber, sourceSystem, sourceRecordId })));
        }, ct);
        return supplier.Id;
    }

    public async Task<Guid> CreateCategoryAsync(Guid organisationId, Guid batchId, string code, string name, Guid? parentId, string sourceSystem, string sourceRecordId, CancellationToken ct = default)
    {
        await RequireOrganisationAsync(organisationId, ct);
        if (parentId is { } p && !await db.Categories.AnyAsync(x => x.Id == p && x.OrganisationId == organisationId, ct))
            throw new DomainException("category_not_found", "The parent [[category|lower]] was not found.");
        var category = Category.Create(tenant.RequireTenantId(), organisationId, code, name, parentId);
        category.MarkMigrated(sourceSystem, sourceRecordId, batchId);
        await uow.ExecuteAsync(async c =>
        {
            db.Categories.Add(category);
            try { await db.SaveChangesAsync(c); }
            catch (DbUpdateException ex) when (DbExceptions.IsUniqueViolation(ex)) { throw new DomainException("category_code_in_use", "That [[category|lower]] code is already in use."); }
            audit.Record(new AuditEntry("migration.category.created", nameof(Category), category.Id.ToString(), OrganisationId: organisationId, MigrationBatchId: batchId,
                NewValuesJson: AuditJson.Of(new { category.Code, category.Name, sourceSystem, sourceRecordId })));
        }, ct);
        return category.Id;
    }

    public async Task<Guid> CreateContractAsync(MigratedContract m, CancellationToken ct = default)
    {
        var tenantId = tenant.RequireTenantId();
        await RequireOrganisationAsync(m.OrganisationId, ct);
        // Supplier and category must belong to this tenant and organisation (other tenants' rows are invisible through the filter): INV-48.
        if (!await db.Suppliers.AnyAsync(s => s.Id == m.SupplierId && s.OrganisationId == m.OrganisationId, ct)) throw new DomainException("supplier_not_found", "The [[supplier|lower]] was not found.");
        if (!await db.Categories.AnyAsync(s => s.Id == m.CategoryId && s.OrganisationId == m.OrganisationId, ct)) throw new DomainException("category_not_found", "The [[category|lower]] was not found.");
        var by = actor.ActorId ?? "system";
        var n = new NewContract(tenantId, m.OrganisationId, m.AgreementReference, m.Title, m.SupplierId, m.CategoryId, await InitialStatusAsync(m.OrganisationId, ct), by, clock.UtcNow, m.Description,
            m.Classification, m.CurrencyCode, m.EstimatedValue, m.ExtensionType, m.LegalReviewRequired, m.StartDate, m.EffectiveDate, m.ReviewDate, m.EndDate,
            ContractType: m.ContractType, AgreementType: m.AgreementType, RiskLevel: m.RiskLevel, FunctionCode: m.FunctionCode, SapOutlineAgreementNumber: m.SapOutlineAgreementNumber,
            NoticePeriodValue: m.NoticePeriodValue, NoticePeriodUnit: m.NoticePeriodUnit, CriticalNotice: m.CriticalNotice);
        var source = new SourceProvenance(m.SourceSystem, m.SourceRecordId, m.SourceAgreementReference, m.SourceStatus, m.SourceExtractedUtc);
        var contract = m.LockAsLegacy
            ? Contract.CreateLegacyLoad(n, m.StatusCode, source, new LegacyLoadAuthorisation(m.BatchId, m.ActingAdministrator, m.ImportRegisteredDate), m.RegisteredUtc, m.RegisteredSourceValue)
            : Contract.CreateMigrated(n, source, m.BatchId);
        await uow.ExecuteAsync(async c =>
        {
            db.Contracts.Add(contract);
            try { await db.SaveChangesAsync(c); }
            catch (DbUpdateException ex) when (DbExceptions.IsUniqueViolation(ex)) { throw new DomainException("agreement_reference_in_use", "That [[contract.reference|lower]] or source record is already in use."); }
            // Configured field values are stored with the batch and the value as the source gave it. The record's last-changed stamp is left alone, so an untouched migrated record can still be rolled back by removal.
            if (m.CustomValues is { Count: > 0 })
                await customFields.ApplyMigratedAsync(contract.Id, m.CustomValues, m.BatchId, m.SourceSystem, m.CustomSourceValues ?? new Dictionary<string, string>(), by, touch: false, c);
            // The batch id, source record id, source value and acting administrator are on the audit event (decision record, Registered Date exception).
            audit.Record(new AuditEntry(m.LockAsLegacy ? "migration.contract.legacy_loaded" : "migration.contract.created", nameof(Contract), contract.Id.ToString(),
                OrganisationId: m.OrganisationId, Classification: contract.Classification, MigrationBatchId: m.BatchId,
                NewValuesJson: AuditJson.Of(new
                {
                    contract.AgreementReference, contract.SourceAgreementReference, contract.Status, contract.SourceStatus, contract.SourceSystem, contract.SourceRecordId, contract.IsLegacyLoad,
                    RegisteredImported = contract.RegisteredDateProvenance == RegisteredDateProvenance.Migration, SourceRegisteredValue = contract.SourceRegisteredValue,
                    ActingAdministrator = m.LockAsLegacy ? m.ActingAdministrator : null
                })));
        }, ct);
        return contract.Id;
    }

    private const string CustomPrefix = "cf.";

    public async Task<IMigrationCustomChecker> CustomFieldCheckerAsync(Guid organisationId, CancellationToken ct = default)
    {
        await RequireOrganisationAsync(organisationId, ct);
        return new MigrationCustomChecker(await fieldCatalogue.PublishedAsync(organisationId, ct), DateOnly.FromDateTime(clock.UtcNow));
    }

    public async Task<ContractFieldUpdate> UpdateContractFieldsAsync(Guid contractId, Guid batchId, IReadOnlyDictionary<string, string?> fields, bool lockedOverwriteAuthorised,
        string reason, CancellationToken ct = default)
    {
        return await uow.ExecuteAsync(async c =>
        {
            var contract = await db.Contracts.FirstOrDefaultAsync(x => x.Id == contractId, c) ?? throw new DomainException("contract_not_found", "The [[contract|lower]] was not found.");
            var by = actor.ActorId ?? "system";
            // Configured fields travel under the "cf." prefix in the same dictionary, so the before-image a rollback restores covers them too.
            var core = fields.Where(kv => !kv.Key.StartsWith(CustomPrefix, StringComparison.Ordinal)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
            var custom = fields.Where(kv => kv.Key.StartsWith(CustomPrefix, StringComparison.Ordinal)).ToDictionary(kv => kv.Key[CustomPrefix.Length..], kv => kv.Value, StringComparer.OrdinalIgnoreCase);
            var before = new Dictionary<string, string?>(contract.CaptureFields(core.Keys), StringComparer.Ordinal);
            if (core.Count > 0) contract.ApplyMigratedFields(core, lockedOverwriteAuthorised, by, clock.UtcNow);
            else if (contract.RecordLocked && !lockedOverwriteAuthorised) throw new DomainException("record_locked", "This record is locked. A migration may overwrite it only under an explicit legacy-load policy.");
            if (custom.Count > 0)
            {
                foreach (var (key, value) in await customFields.CurrentAsync(contractId, custom.Keys.ToList(), c)) before[CustomPrefix + key] = value;
                foreach (var key in custom.Keys) before.TryAdd(CustomPrefix + key, null);
            }
            try { await db.SaveChangesAsync(c); }
            catch (DbUpdateConcurrencyException) { throw new DomainException("concurrency_conflict", "The record was changed by someone else. Reload and try again."); }
            if (custom.Count > 0)
                await customFields.ApplyMigratedAsync(contractId, custom, batchId, "migration", custom.ToDictionary(kv => kv.Key, kv => kv.Value ?? ""), by, touch: true, c);
            var after = new Dictionary<string, string?>(contract.CaptureFields(core.Keys), StringComparer.Ordinal);
            foreach (var (key, value) in custom) after[CustomPrefix + key] = value;
            audit.Record(new AuditEntry("migration.contract.updated", nameof(Contract), contract.Id.ToString(), OrganisationId: contract.OrganisationId, Classification: contract.Classification, Reason: reason, MigrationBatchId: batchId,
                OldValuesJson: AuditJson.Of(before), NewValuesJson: AuditJson.Of(after)));
            return new ContractFieldUpdate(before, contract.UpdatedUtc);
        }, ct);
    }

    public async Task<IReadOnlyDictionary<string, string?>> CaptureFieldsAsync(Guid contractId, IEnumerable<string> fieldNames, CancellationToken ct = default)
    {
        var contract = await db.Contracts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == contractId, ct) ?? throw new DomainException("contract_not_found", "The [[contract|lower]] was not found.");
        var names = fieldNames.ToList();
        var result = new Dictionary<string, string?>(contract.CaptureFields(names.Where(n => !n.StartsWith(CustomPrefix, StringComparison.Ordinal))), StringComparer.Ordinal);
        var custom = names.Where(n => n.StartsWith(CustomPrefix, StringComparison.Ordinal)).Select(n => n[CustomPrefix.Length..]).ToList();
        if (custom.Count > 0) foreach (var (key, value) in await customFields.CurrentAsync(contractId, custom, ct)) result[CustomPrefix + key] = value;
        return result;
    }

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, string?>>> CaptureFieldsManyAsync(IReadOnlyCollection<Guid> contractIds, IReadOnlyCollection<string> fieldNames, CancellationToken ct = default)
    {
        var result = new Dictionary<Guid, IReadOnlyDictionary<string, string?>>();
        var core = fieldNames.Where(n => !n.StartsWith(CustomPrefix, StringComparison.Ordinal)).ToList();
        var customKeys = fieldNames.Where(n => n.StartsWith(CustomPrefix, StringComparison.Ordinal)).Select(n => n[CustomPrefix.Length..]).ToList();
        foreach (var chunk in contractIds.Distinct().Chunk(400))
        {
            var ids = chunk.ToList();
            foreach (var c in await db.Contracts.AsNoTracking().Where(x => ids.Contains(x.Id)).ToListAsync(ct)) result[c.Id] = new Dictionary<string, string?>(c.CaptureFields(core), StringComparer.Ordinal);
            if (customKeys.Count > 0)
            {
                // One query per chunk for all configured values; nothing per contract.
                var rows = await db.CustomValues.AsNoTracking().Where(v => ids.Contains(v.ContractId) && customKeys.Contains(v.FieldKey)).OrderBy(v => v.ContractId).ThenBy(v => v.FieldKey).ThenBy(v => v.Ordinal).ToListAsync(ct);
                var set = result.Count == 0 ? null : await fieldCatalogue.PublishedAsync(await db.Contracts.AsNoTracking().Where(x => x.Id == ids[0]).Select(x => x.OrganisationId).FirstAsync(ct), ct);
                foreach (var group in rows.GroupBy(r => (r.ContractId, r.FieldKey)))
                {
                    var d = set?.Find(group.Key.FieldKey);
                    if (d is null || !result.TryGetValue(group.Key.ContractId, out var map)) continue;
                    ((Dictionary<string, string?>)map)[CustomPrefix + d.Key] = string.Join("; ", ValueCodec.ToRaw(d, group.Select(g => ContractCustomFieldService.ToTyped(g, FieldTypes.StorageOf(d.Type))).ToList()));
                }
            }
        }
        return result;
    }

    public async Task<ExistingContractRef?> GetContractAsync(Guid contractId, CancellationToken ct = default)
    {
        var c = await db.Contracts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == contractId, ct);
        return c is null ? null : ToRef(c);
    }

    public async Task RemoveContractAsync(Guid contractId, Guid batchId, string reason, CancellationToken ct = default)
        => await uow.ExecuteAsync(async c =>
        {
            var contract = await db.Contracts.FirstOrDefaultAsync(x => x.Id == contractId && x.MigrationBatchId == batchId, c);
            if (contract is null) return; // already gone: rollback is idempotent. A record of another batch is never selected.
            await db.CustomValues.Where(v => v.ContractId == contractId).ExecuteDeleteAsync(c);       // the configured values this batch loaded go with the record
            db.Contracts.Remove(contract);
            await db.SaveChangesAsync(c);
            audit.Record(new AuditEntry("migration.contract.removed", nameof(Contract), contractId.ToString(), OrganisationId: contract.OrganisationId, Classification: contract.Classification, Reason: reason, MigrationBatchId: batchId,
                OldValuesJson: AuditJson.Of(new { contract.AgreementReference, contract.SourceRecordId, contract.Status })));
        }, ct);

    public async Task ArchiveContractAsync(Guid contractId, Guid batchId, string reason, CancellationToken ct = default)
        => await uow.ExecuteAsync(async c =>
        {
            var contract = await db.Contracts.FirstOrDefaultAsync(x => x.Id == contractId && x.MigrationBatchId == batchId, c);
            if (contract is null || contract.IsArchived) return;
            contract.Archive(reason, batchId, actor.ActorId ?? "system", clock.UtcNow);
            await db.SaveChangesAsync(c);
            audit.Record(new AuditEntry("migration.contract.archived", nameof(Contract), contractId.ToString(), OrganisationId: contract.OrganisationId, Classification: contract.Classification, Reason: reason, MigrationBatchId: batchId));
        }, ct);

    public Task<int> CountContractsOfSupplierAsync(Guid supplierId, IReadOnlyCollection<Guid>? excluding = null, CancellationToken ct = default)
        => excluding is { Count: > 0 } ? db.Contracts.AsNoTracking().CountAsync(c => c.SupplierId == supplierId && !excluding.Contains(c.Id), ct) : db.Contracts.AsNoTracking().CountAsync(c => c.SupplierId == supplierId, ct);
    public Task<int> CountContractsOfCategoryAsync(Guid categoryId, IReadOnlyCollection<Guid>? excluding = null, CancellationToken ct = default)
        => excluding is { Count: > 0 } ? db.Contracts.AsNoTracking().CountAsync(c => c.CategoryId == categoryId && !excluding.Contains(c.Id), ct) : db.Contracts.AsNoTracking().CountAsync(c => c.CategoryId == categoryId, ct);
    public Task<int> CountChildCategoriesAsync(Guid categoryId, IReadOnlyCollection<Guid>? excluding = null, CancellationToken ct = default)
        => excluding is { Count: > 0 } ? db.Categories.AsNoTracking().CountAsync(c => c.ParentCategoryId == categoryId && !excluding.Contains(c.Id), ct) : db.Categories.AsNoTracking().CountAsync(c => c.ParentCategoryId == categoryId, ct);

    public async Task RemoveSupplierAsync(Guid supplierId, Guid batchId, string reason, CancellationToken ct = default)
        => await uow.ExecuteAsync(async c =>
        {
            var supplier = await db.Suppliers.FirstOrDefaultAsync(x => x.Id == supplierId && x.MigrationBatchId == batchId, c);
            if (supplier is null) return;
            db.Suppliers.Remove(supplier);
            await db.SaveChangesAsync(c);
            audit.Record(new AuditEntry("migration.supplier.removed", nameof(Supplier), supplierId.ToString(), OrganisationId: supplier.OrganisationId, Reason: reason, MigrationBatchId: batchId,
                OldValuesJson: AuditJson.Of(new { supplier.Name, supplier.VendorNumber })));
        }, ct);

    public async Task RemoveCategoryAsync(Guid categoryId, Guid batchId, string reason, CancellationToken ct = default)
        => await uow.ExecuteAsync(async c =>
        {
            var category = await db.Categories.FirstOrDefaultAsync(x => x.Id == categoryId && x.MigrationBatchId == batchId, c);
            if (category is null) return;
            db.Categories.Remove(category);
            await db.SaveChangesAsync(c);
            audit.Record(new AuditEntry("migration.category.removed", nameof(Category), categoryId.ToString(), OrganisationId: category.OrganisationId, Reason: reason, MigrationBatchId: batchId,
                OldValuesJson: AuditJson.Of(new { category.Code, category.Name })));
        }, ct);

    public async Task<BatchContractSummary> SummariseBatchAsync(Guid batchId, CancellationToken ct = default)
    {
        var rows = await (from c in db.Contracts.AsNoTracking()
                          where c.MigrationBatchId == batchId
                          join g in db.Categories.AsNoTracking() on new { c.TenantId, Id = c.CategoryId } equals new { g.TenantId, g.Id }
                          select new { c.Status, g.Code, c.CurrencyCode, c.EstimatedValue }).ToListAsync(ct);
        var byStatus = rows.GroupBy(r => r.Status).ToDictionary(g => g.Key, g => g.Count());
        var byCategory = rows.GroupBy(r => r.Code).ToDictionary(g => g.Key, g => g.Count());
        var value = rows.Where(r => r.CurrencyCode != null && r.EstimatedValue != null).GroupBy(r => r.CurrencyCode!).ToDictionary(g => g.Key, g => g.Sum(r => r.EstimatedValue!.Value));
        return new BatchContractSummary(rows.Count, byStatus, byCategory, value);
    }

    public void ReleaseTracked() => db.ChangeTracker.Clear();

    private async Task RequireOrganisationAsync(Guid organisationId, CancellationToken ct)
    {
        if (!await tenants.OrganisationExistsAsync(organisationId, ct)) throw new DomainException("organisation_not_found", "The [[organisation|lower]] was not found.");
    }
}

/// <summary>
/// Cutover freeze (MIGRATION_SUPPLEMENT section 5.6). The Migration module supplies the real policy; until then nothing is frozen.
/// Contracts asks before creating a contract for a person: migration itself never goes through this check.
/// </summary>
public interface ICutoverFreeze
{
    /// <summary>A message when contract creation in the organisation is frozen at this moment, otherwise null.</summary>
    Task<string?> CreationBlockedAsync(Guid organisationId, CancellationToken ct = default);
}

internal sealed class NoCutoverFreeze : ICutoverFreeze
{
    public Task<string?> CreationBlockedAsync(Guid organisationId, CancellationToken ct = default) => Task.FromResult<string?>(null);
}
