using Ecp.Audit;
using Ecp.Configuration;
using Ecp.Contracts.Domain;
using Ecp.Contracts.Persistence;
using Ecp.Identity;
using Ecp.Identity.Access;
using Ecp.Persistence;
using Ecp.SharedKernel;
using Ecp.SharedKernel.Localisation;
using Ecp.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Ecp.Contracts;

/// <summary>An identifier type an organisation uses, as a form needs it: its label, whether it is required or unique, and a plain-language hint about its structure.</summary>
public sealed record IdentifierTypeInfo(string Key, string Label, string? Country, bool Required, bool Unique, string Classification, string Hint, bool IsPrimary);

/// <summary>One recorded identifier as a reader may see it. <see cref="Verification"/> is a structural status only; no identifier is verified against any registry.</summary>
public sealed record SupplierIdentifierItem(Guid Id, Guid SupplierId, string TypeKey, string Label, string Value, string Verification, string Source, string? SourceSystem, string Classification, bool IsPrimary);

/// <summary>
/// The counterparty identifiers of the organisation's identifier profile (docs/TAX_IDENTIFIERS.md): add, end and list, each checked against the profile's structural rule, the right to manage or read suppliers,
/// and the identifier's classification. A reader without the classification never sees the value, and is never told that one exists.
/// </summary>
public interface ISupplierIdentifiers
{
    /// <summary>The identifier types the organisation records, primary first. Empty when the organisation has no identifier profile (the Blank pack's starting position).</summary>
    Task<IReadOnlyList<IdentifierTypeInfo>> TypesAsync(Guid organisationId, CancellationToken ct = default);
    Task<IReadOnlyList<SupplierIdentifierItem>> ListAsync(Guid supplierId, CancellationToken ct = default);
    Task<SupplierIdentifierItem> AddAsync(Guid supplierId, string typeKey, string value, CancellationToken ct = default);
    Task EndAsync(Guid identifierId, CancellationToken ct = default);
    /// <summary>Checks a value against a type's structural rule without storing anything.</summary>
    Task<IdentifierCheck> CheckAsync(Guid organisationId, string typeKey, string value, CancellationToken ct = default);
}

/// <summary>Records an identifier on behalf of a trusted process (supplier creation, a migration batch). The caller has authorised the operation; the structural rule and the uniqueness policy still apply.</summary>
public interface ISupplierIdentifierWriter
{
    Task<Guid> RecordTrustedAsync(Guid organisationId, Guid supplierId, string typeKey, string value, string sourceKind, string? sourceSystem, Guid? migrationBatchId, CancellationToken ct = default);
    /// <summary>Checks a value against a type and reports why it would be refused, without storing anything and without an access check (a dry run of a migration batch).</summary>
    Task<IdentifierCheck> CheckTrustedAsync(Guid organisationId, string typeKey, string value, CancellationToken ct = default);
}

internal sealed class SupplierIdentifierService(ContractsDbContext db, IAccessService access, IUnitOfWork uow, IAuditWriter audit, ITenantContext tenant, IClock clock, IActorContext actor,
    IRegionalSettingsResolver regional, ITerminologyResolver terminology) : ISupplierIdentifiers, ISupplierIdentifierWriter
{
    private ResourceDescriptor Resource(Guid organisationId, Guid? supplierId = null, string? classification = null)
        => new("Supplier", tenant.RequireTenantId(), organisationId, Id: supplierId?.ToString() ?? "new", Classification: classification, Dimensions: ScopeDimensions.OrganisationOnly);

    private static string Hint(IdentifierTypeProfile p) => p.Rule switch
    {
        IdentifierRuleKinds.Digits => p.Length is { } l && !l.Contains('-') ? $"{l} digits" : $"{p.Length} digits",
        IdentifierRuleKinds.Alphanumeric => $"{p.Length} letters or digits",
        IdentifierRuleKinds.Mask => $"pattern {p.Mask}",
        _ => "letters, digits, spaces, hyphens, dots or slashes"
    };

    public async Task<IReadOnlyList<IdentifierTypeInfo>> TypesAsync(Guid organisationId, CancellationToken ct = default)
    {
        await access.RequireAsync("suppliers.read", Resource(organisationId), ct);
        var settings = await regional.GetAsync(organisationId, ct);
        var primary = settings.PrimaryIdentifier?.Key;
        return settings.Identifiers.OrderBy(i => i.Order).ThenBy(i => i.Key, StringComparer.Ordinal)
            .Select(i => new IdentifierTypeInfo(i.Key, i.Label, i.Country, i.Required, i.Unique, i.Classification, Hint(i), i.Key == primary)).ToList();
    }

    public async Task<IdentifierCheck> CheckAsync(Guid organisationId, string typeKey, string value, CancellationToken ct = default)
    {
        await access.RequireAsync("suppliers.manage", Resource(organisationId), ct);
        return await CheckTrustedAsync(organisationId, typeKey, value, ct);
    }

    public async Task<IdentifierCheck> CheckTrustedAsync(Guid organisationId, string typeKey, string value, CancellationToken ct = default)
    {
        var settings = await regional.GetAsync(organisationId, ct);
        var profile = settings.Identifier(typeKey);
        return profile is null ? new IdentifierCheck(false, "", "identifier_type_unknown", "That identifier type is not set up for this organisation.") : IdentifierRules.Check(profile, value);
    }

    /// <summary>Which classification levels the caller may read for suppliers in the organisation (the union over their grants).</summary>
    private async Task<IReadOnlySet<string>> ReadableAsync(Guid organisationId, CancellationToken ct)
        => (await access.GrantsAsync("suppliers.read", ct)).Where(g => g.OrganisationIds is null || g.OrganisationIds.Contains(organisationId)).SelectMany(g => g.AllowedClassifications).ToHashSet(StringComparer.Ordinal);

    private async Task<(Supplier Supplier, RegionalSettings Settings)> LoadAsync(Guid supplierId, CancellationToken ct)
    {
        var supplier = await db.Suppliers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == supplierId, ct) ?? throw new ResourceNotFoundException();
        return (supplier, await regional.GetAsync(supplier.OrganisationId, ct));
    }

    public async Task<IReadOnlyList<SupplierIdentifierItem>> ListAsync(Guid supplierId, CancellationToken ct = default)
    {
        var (supplier, settings) = await LoadAsync(supplierId, ct);
        await access.RequireAsync("suppliers.read", Resource(supplier.OrganisationId, supplierId), ct);
        var readable = await ReadableAsync(supplier.OrganisationId, ct);
        var rows = await db.SupplierIdentifiers.AsNoTracking().Where(i => i.SupplierId == supplierId && i.EffectiveToUtc == null).OrderBy(i => i.TypeKey).ThenBy(i => i.CreatedUtc).ToListAsync(ct);
        var words = await terminology.GetAsync(supplier.OrganisationId, null, ct);
        var primary = settings.PrimaryIdentifier?.Key;
        var items = new List<SupplierIdentifierItem>();
        foreach (var r in rows.Where(r => readable.Contains(r.Classification)).OrderBy(r => settings.Identifier(r.TypeKey)?.Order ?? int.MaxValue))
        {
            var profile = settings.Identifier(r.TypeKey);
            var label = profile?.Label ?? words.Expand("[[supplier.tax.id]]");
            var shown = profile is { Rule: IdentifierRuleKinds.Mask } ? IdentifierRules.Display(profile, r.NormalisedValue) : r.Value;
            items.Add(new SupplierIdentifierItem(r.Id, r.SupplierId, r.TypeKey, label, shown, r.VerificationStatus, r.SourceKind, r.SourceSystem, r.Classification, r.TypeKey == primary));
        }
        return items;
    }

    public async Task<SupplierIdentifierItem> AddAsync(Guid supplierId, string typeKey, string value, CancellationToken ct = default)
    {
        var (supplier, settings) = await LoadAsync(supplierId, ct);
        await access.RequireAsync("suppliers.manage", Resource(supplier.OrganisationId, supplierId), ct);
        var profile = settings.Identifier(typeKey) ?? throw new DomainException("identifier_type_unknown", "That identifier type is not set up for this [[organisation|lower]].");
        // A person records only what they may read: the same classification gate that hides the value from readers applies to the writer, so a duplicate is never reported across a classification line.
        await access.RequireAsync("suppliers.read", Resource(supplier.OrganisationId, supplierId, profile.Classification), ct);
        var id = await RecordAsync(supplier.OrganisationId, supplierId, settings, profile, value, IdentifierSources.Manual, null, null, ct);
        return (await ListAsync(supplierId, ct)).First(i => i.Id == id);
    }

    public async Task<Guid> RecordTrustedAsync(Guid organisationId, Guid supplierId, string typeKey, string value, string sourceKind, string? sourceSystem, Guid? migrationBatchId, CancellationToken ct = default)
    {
        var settings = await regional.GetAsync(organisationId, ct);
        var profile = settings.Identifier(typeKey) ?? throw new DomainException("identifier_type_unknown", "That identifier type is not set up for this [[organisation|lower]].");
        return await RecordAsync(organisationId, supplierId, settings, profile, value, sourceKind, sourceSystem, migrationBatchId, ct);
    }

    private async Task<Guid> RecordAsync(Guid organisationId, Guid supplierId, RegionalSettings settings, IdentifierTypeProfile profile, string value, string sourceKind, string? sourceSystem, Guid? batch, CancellationToken ct)
    {
        var check = IdentifierRules.Check(profile, value);
        if (!check.Valid) throw new DomainException(check.Code ?? "identifier_invalid", $"[[supplier.tax.id]] '{profile.Label}': {check.Message}");
        var tenantId = tenant.RequireTenantId();
        if (await db.SupplierIdentifiers.AnyAsync(i => i.SupplierId == supplierId && i.TypeKey == profile.Key && i.NormalisedValue == check.Normalised && i.EffectiveToUtc == null, ct))
            throw new DomainException("identifier_already_recorded", "That value is already recorded for this [[supplier|lower]].");
        if (profile.Unique && await db.SupplierIdentifiers.AnyAsync(i => i.OrganisationId == organisationId && i.TypeKey == profile.Key && i.NormalisedValue == check.Normalised && i.EffectiveToUtc == null && i.SupplierId != supplierId, ct))
            throw new DomainException("identifier_in_use", "That [[supplier.tax.id]] is already in use by another [[supplier|lower]].");
        var created = SupplierIdentifier.Create(tenantId, organisationId, supplierId, profile.Key, value.Trim(), check.Normalised, IdentifierVerification.FormatValid, profile.Classification, sourceKind, sourceSystem, batch, actor.ActorId ?? "system", clock.UtcNow);
        await uow.ExecuteAsync(async c =>
        {
            db.SupplierIdentifiers.Add(created);
            await db.SaveChangesAsync(c);
            await ProjectAsync(supplierId, settings, c);
            audit.Record(new AuditEntry("supplier.identifier.added", nameof(SupplierIdentifier), created.Id.ToString(), OrganisationId: organisationId, MigrationBatchId: batch, Classification: profile.Classification,
                NewValuesJson: AuditJson.Of(new { supplierId, type = profile.Key, value = created.Value, status = created.VerificationStatus, source = sourceKind })));
        }, ct);
        return created.Id;
    }

    public async Task EndAsync(Guid identifierId, CancellationToken ct = default)
    {
        var row = await db.SupplierIdentifiers.AsNoTracking().FirstOrDefaultAsync(i => i.Id == identifierId, ct) ?? throw new ResourceNotFoundException();
        var settings = await regional.GetAsync(row.OrganisationId, ct);
        await access.RequireAsync("suppliers.manage", Resource(row.OrganisationId, row.SupplierId), ct);
        // The same answer as for a missing row when the identifier is above the caller's classification.
        if (!(await ReadableAsync(row.OrganisationId, ct)).Contains(row.Classification)) throw new ResourceNotFoundException();
        await uow.ExecuteAsync(async c =>
        {
            var tracked = await db.SupplierIdentifiers.FirstAsync(i => i.Id == identifierId, c);
            tracked.End(clock.UtcNow);
            await db.SaveChangesAsync(c);
            await ProjectAsync(row.SupplierId, settings, c);
            audit.Record(new AuditEntry("supplier.identifier.ended", nameof(SupplierIdentifier), identifierId.ToString(), OrganisationId: row.OrganisationId, Classification: row.Classification,
                OldValuesJson: AuditJson.Of(new { supplierId = row.SupplierId, type = row.TypeKey, value = row.Value })));
        }, ct);
    }

    /// <summary>Keeps the supplier's single projected value (list, search, export) equal to its newest active identifier of the organisation's primary type.</summary>
    private async Task ProjectAsync(Guid supplierId, RegionalSettings settings, CancellationToken ct)
    {
        var supplier = await db.Suppliers.FirstAsync(s => s.Id == supplierId, ct);
        var primary = settings.PrimaryIdentifier?.Key;
        if (primary is null) return;
        var newest = await db.SupplierIdentifiers.AsNoTracking().Where(i => i.SupplierId == supplierId && i.TypeKey == primary && i.EffectiveToUtc == null).OrderByDescending(i => i.CreatedUtc).Select(i => i.NormalisedValue).FirstOrDefaultAsync(ct);
        supplier.SetTaxIdentifier(newest);
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>
/// Decides, once per query, in which organisations the caller may read the primary tax identifier that lists, searches and exports show. An organisation with no identifier profile has nothing to hide;
/// otherwise the primary type's classification must be one the caller's grants allow (docs/TAX_IDENTIFIERS.md, access).
/// </summary>
internal sealed class TaxIdentifierGate(IRegionalSettingsResolver regional)
{
    public async Task<IReadOnlySet<Guid>> ReadableOrganisationsAsync(IReadOnlyList<AccessGrant> grants, IEnumerable<Guid> organisationIds, CancellationToken ct)
    {
        var readable = new HashSet<Guid>();
        foreach (var org in organisationIds.Distinct())
        {
            var classification = (await regional.GetAsync(org, ct)).PrimaryIdentifier?.Classification;
            if (classification is null || grants.Any(g => (g.OrganisationIds is null || g.OrganisationIds.Contains(org)) && g.AllowedClassifications.Contains(classification, StringComparer.Ordinal))) readable.Add(org);
        }
        return readable;
    }
}
