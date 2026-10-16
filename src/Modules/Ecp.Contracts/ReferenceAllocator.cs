using Ecp.Contracts.Persistence;
using Ecp.SharedKernel;
using Ecp.Tenancy;
using Ecp.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecp.Contracts;

// Reviewed raw SQL (ARC-005): fixed statement text with parameters only; atomic increment of the per-organisation reference counter.
internal sealed class ReferenceAllocator(ContractsDbContext db, IOrganisationStructureService organisations)
{
    /// <summary>Takes the next number atomically inside the caller's unit of work and formats it with the organisation's rule.</summary>
    public async Task<string> AllocateAsync(Guid organisationId, bool consume, CancellationToken ct)
    {
        var org = await organisations.GetOrganisationAsync(organisationId, ct) ?? throw new DomainException("organisation_not_found", "The organisation was not found.");
        var rule = Ecp.Tenancy.Domain.ReferenceNumberingRule.Create(org.ReferencePrefix);
        var next = consume ? await TakeAsync(organisationId, ct) : (await db.ReferenceCounters.AsNoTracking().Where(r => r.OrganisationId == organisationId).Select(r => (long?)r.LastValue).FirstOrDefaultAsync(ct) ?? 0) + 1;
        return rule.Format(next);
    }

    private async Task<long> TakeAsync(Guid organisationId, CancellationToken ct)
    {
        await db.EnlistAsync(ct);
        var tenantId = db.CurrentTenantId;
        if (db.Database.IsSqlServer())
        {
            await db.Database.ExecuteSqlRawAsync(
                "IF NOT EXISTS (SELECT 1 FROM contracts.ReferenceCounter WHERE TenantId = {0} AND OrganisationId = {1}) INSERT INTO contracts.ReferenceCounter (TenantId, OrganisationId, LastValue) VALUES ({0}, {1}, 0);",
                [tenantId, organisationId], ct);
            var rows = await db.Database.SqlQueryRaw<long>(
                "UPDATE contracts.ReferenceCounter SET LastValue = LastValue + 1 OUTPUT inserted.LastValue AS [Value] WHERE TenantId = {0} AND OrganisationId = {1}",
                tenantId, organisationId).ToListAsync(ct);
            return rows.Single();
        }
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO ReferenceCounter (TenantId, OrganisationId, LastValue) SELECT {0}, {1}, 0 WHERE NOT EXISTS (SELECT 1 FROM ReferenceCounter WHERE TenantId = {0} AND OrganisationId = {1})",
            [tenantId, organisationId], ct);
        var sqlite = await db.Database.SqlQueryRaw<long>(
            "UPDATE ReferenceCounter SET LastValue = LastValue + 1 WHERE TenantId = {0} AND OrganisationId = {1} RETURNING LastValue AS Value",
            tenantId, organisationId).ToListAsync(ct);
        return sqlite.Single();
    }
}
