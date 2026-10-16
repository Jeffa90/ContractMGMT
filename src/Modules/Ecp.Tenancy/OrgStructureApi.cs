using Ecp.Audit;
using Ecp.Persistence;
using Ecp.SharedKernel;
using Ecp.Tenancy.Domain;
using Ecp.Tenancy.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecp.Tenancy;

public enum StructureKind { LegalEntity, Division, BusinessUnit, Site }

public sealed record LegalEntityDto(Guid Id, string Key, string Name, bool IsActive, string? RegistrationNumber);
public sealed record DivisionDto(Guid Id, string Key, string Name, bool IsActive);
public sealed record BusinessUnitDto(Guid Id, string Key, string Name, bool IsActive, Guid? DivisionId);
public sealed record SiteDto(Guid Id, string Key, string Name, bool IsActive, string? TimeZoneId);

/// <summary>A snapshot of one organisation's structure. Division membership is derived from business units.</summary>
public sealed record OrganisationStructure(
    Guid OrganisationId, IReadOnlyList<LegalEntityDto> LegalEntities, IReadOnlyList<DivisionDto> Divisions,
    IReadOnlyList<BusinessUnitDto> BusinessUnits, IReadOnlyList<SiteDto> Sites)
{
    public Guid? DivisionOfBusinessUnit(Guid? businessUnitId)
        => businessUnitId is null ? null : BusinessUnits.FirstOrDefault(b => b.Id == businessUnitId)?.DivisionId;
}

public sealed record OrganisationSummary(Guid Id, string Name, string TimeZoneId, string CultureCode, string DefaultCurrencyCode, string ReferencePrefix, string? SolutionPackKey);

public interface IOrganisationStructureService
{
    Task<IReadOnlyList<OrganisationSummary>> ListOrganisationsAsync(CancellationToken ct = default);
    Task<OrganisationSummary?> GetOrganisationAsync(Guid organisationId, CancellationToken ct = default);
    Task<OrganisationStructure> GetAsync(Guid organisationId, CancellationToken ct = default);

    /// <summary>Creates or updates the item with this key in the organisation (idempotent; reactivates an inactive item).</summary>
    Task<Guid> UpsertLegalEntityAsync(Guid organisationId, string key, string name, string? registrationNumber, CancellationToken ct = default);
    Task<Guid> UpsertDivisionAsync(Guid organisationId, string key, string name, CancellationToken ct = default);
    Task<Guid> UpsertBusinessUnitAsync(Guid organisationId, string key, string name, string? divisionKey, CancellationToken ct = default);
    Task<Guid> UpsertSiteAsync(Guid organisationId, string code, string name, string? timeZoneId, CancellationToken ct = default);

    /// <summary>Deactivates an item. Items are never hard-deleted (contracts and assignments may refer to them).</summary>
    Task DeactivateAsync(StructureKind kind, Guid organisationId, string key, CancellationToken ct = default);
}

internal sealed class OrganisationStructureService(TenancyDbContext db, IUnitOfWork uow, IAuditWriter audit, ITenantContext tenant, IClock clock) : IOrganisationStructureService
{
    public async Task<IReadOnlyList<OrganisationSummary>> ListOrganisationsAsync(CancellationToken ct = default)
        => await db.Organisations.AsNoTracking().OrderBy(o => o.Name)
            .Select(o => new OrganisationSummary(o.Id, o.Name, o.TimeZoneId, o.CultureCode, o.DefaultCurrencyCode, o.ReferencePrefix, o.SolutionPackKey)).ToListAsync(ct);

    public Task<OrganisationSummary?> GetOrganisationAsync(Guid organisationId, CancellationToken ct = default)
        => db.Organisations.AsNoTracking().Where(o => o.Id == organisationId)
            .Select(o => new OrganisationSummary(o.Id, o.Name, o.TimeZoneId, o.CultureCode, o.DefaultCurrencyCode, o.ReferencePrefix, o.SolutionPackKey)).FirstOrDefaultAsync(ct);

    public async Task<OrganisationStructure> GetAsync(Guid organisationId, CancellationToken ct = default)
    {
        await EnsureOrganisationAsync(organisationId, ct);
        return new OrganisationStructure(organisationId,
            await db.LegalEntities.AsNoTracking().Where(x => x.OrganisationId == organisationId).OrderBy(x => x.Key).Select(x => new LegalEntityDto(x.Id, x.Key, x.Name, x.IsActive, x.RegistrationNumber)).ToListAsync(ct),
            await db.Divisions.AsNoTracking().Where(x => x.OrganisationId == organisationId).OrderBy(x => x.Key).Select(x => new DivisionDto(x.Id, x.Key, x.Name, x.IsActive)).ToListAsync(ct),
            await db.BusinessUnits.AsNoTracking().Where(x => x.OrganisationId == organisationId).OrderBy(x => x.Key).Select(x => new BusinessUnitDto(x.Id, x.Key, x.Name, x.IsActive, x.DivisionId)).ToListAsync(ct),
            await db.Sites.AsNoTracking().Where(x => x.OrganisationId == organisationId).OrderBy(x => x.Key).Select(x => new SiteDto(x.Id, x.Key, x.Name, x.IsActive, x.TimeZoneId)).ToListAsync(ct));
    }

    public Task<Guid> UpsertLegalEntityAsync(Guid organisationId, string key, string name, string? registrationNumber, CancellationToken ct = default)
        => UpsertAsync(organisationId, StructureKind.LegalEntity, key, async (k, tenantId, c) =>
        {
            var existing = await db.LegalEntities.FirstOrDefaultAsync(x => x.OrganisationId == organisationId && x.Key == k, c);
            if (existing is null) { var created = LegalEntity.Create(tenantId, organisationId, k, name, registrationNumber, clock.UtcNow); db.LegalEntities.Add(created); return (created.Id, true); }
            existing.Update(name, registrationNumber); existing.Activate(); return (existing.Id, false);
        }, ct);

    public Task<Guid> UpsertDivisionAsync(Guid organisationId, string key, string name, CancellationToken ct = default)
        => UpsertAsync(organisationId, StructureKind.Division, key, async (k, tenantId, c) =>
        {
            var existing = await db.Divisions.FirstOrDefaultAsync(x => x.OrganisationId == organisationId && x.Key == k, c);
            if (existing is null) { var created = Division.Create(tenantId, organisationId, k, name, clock.UtcNow); db.Divisions.Add(created); return (created.Id, true); }
            existing.Rename(name); existing.Activate(); return (existing.Id, false);
        }, ct);

    public Task<Guid> UpsertBusinessUnitAsync(Guid organisationId, string key, string name, string? divisionKey, CancellationToken ct = default)
        => UpsertAsync(organisationId, StructureKind.BusinessUnit, key, async (k, tenantId, c) =>
        {
            Guid? divisionId = null;
            if (!string.IsNullOrWhiteSpace(divisionKey))
            {
                var dk = StructureKey.Normalise(divisionKey, "Division key");
                divisionId = await db.Divisions.Where(d => d.OrganisationId == organisationId && d.Key == dk).Select(d => (Guid?)d.Id).FirstOrDefaultAsync(c)
                    ?? throw new DomainException("division_not_found", $"Division '{dk}' was not found in this organisation.");
            }
            var existing = await db.BusinessUnits.FirstOrDefaultAsync(x => x.OrganisationId == organisationId && x.Key == k, c);
            if (existing is null) { var created = BusinessUnit.Create(tenantId, organisationId, k, name, divisionId, clock.UtcNow); db.BusinessUnits.Add(created); return (created.Id, true); }
            existing.Update(name, divisionId); existing.Activate(); return (existing.Id, false);
        }, ct);

    public Task<Guid> UpsertSiteAsync(Guid organisationId, string code, string name, string? timeZoneId, CancellationToken ct = default)
        => UpsertAsync(organisationId, StructureKind.Site, code, async (k, tenantId, c) =>
        {
            var existing = await db.Sites.FirstOrDefaultAsync(x => x.OrganisationId == organisationId && x.Key == k, c);
            if (existing is null) { var created = Site.Create(tenantId, organisationId, k, name, timeZoneId, clock.UtcNow); db.Sites.Add(created); return (created.Id, true); }
            existing.Update(name, timeZoneId); existing.Activate(); return (existing.Id, false);
        }, ct);

    public async Task DeactivateAsync(StructureKind kind, Guid organisationId, string key, CancellationToken ct = default)
    {
        await EnsureOrganisationAsync(organisationId, ct);
        var k = StructureKey.Normalise(key);
        await uow.ExecuteAsync(async c =>
        {
            StructureItem? item = kind switch
            {
                StructureKind.LegalEntity => await db.LegalEntities.FirstOrDefaultAsync(x => x.OrganisationId == organisationId && x.Key == k, c),
                StructureKind.Division => await db.Divisions.FirstOrDefaultAsync(x => x.OrganisationId == organisationId && x.Key == k, c),
                StructureKind.BusinessUnit => await db.BusinessUnits.FirstOrDefaultAsync(x => x.OrganisationId == organisationId && x.Key == k, c),
                _ => await db.Sites.FirstOrDefaultAsync(x => x.OrganisationId == organisationId && x.Key == k, c)
            };
            if (item is null) throw new DomainException("structure_item_not_found", "The item was not found.");
            item.Deactivate();
            await db.SaveChangesAsync(c);
            audit.Record(new AuditEntry($"structure.{kind.ToString().ToLowerInvariant()}.deactivated", kind.ToString(), item.Id.ToString(), OrganisationId: organisationId));
        }, ct);
    }

    private async Task<Guid> UpsertAsync(Guid organisationId, StructureKind kind, string rawKey,
        Func<string, Guid, CancellationToken, Task<(Guid Id, bool Created)>> apply, CancellationToken ct)
    {
        var tenantId = tenant.RequireTenantId();
        await EnsureOrganisationAsync(organisationId, ct);
        var key = StructureKey.Normalise(rawKey);
        return await uow.ExecuteAsync(async c =>
        {
            var (id, created) = await apply(key, tenantId, c);
            try { await db.SaveChangesAsync(c); }
            catch (DbUpdateException ex) when (DbExceptions.IsUniqueViolation(ex))
            { throw new DomainException("structure_key_in_use", $"The key '{key}' is already in use in this organisation."); }
            audit.Record(new AuditEntry($"structure.{kind.ToString().ToLowerInvariant()}.{(created ? "created" : "updated")}", kind.ToString(), id.ToString(),
                OrganisationId: organisationId, NewValuesJson: System.Text.Json.JsonSerializer.Serialize(new { key })));
            return id;
        }, ct);
    }

    private async Task EnsureOrganisationAsync(Guid organisationId, CancellationToken ct)
    {
        if (!await db.Organisations.AsNoTracking().AnyAsync(o => o.Id == organisationId, ct))
            throw new DomainException("organisation_not_found", "The organisation was not found.");
    }
}
