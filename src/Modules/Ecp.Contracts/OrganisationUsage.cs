using Ecp.Contracts.Persistence;
using Ecp.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Ecp.Contracts;

/// <summary>Says how many agreements and counterparties an organisation holds, so setup that has gone live cannot be reset from under them.</summary>
internal sealed class ContractsOrganisationUsage(ContractsDbContext db) : IOrganisationUsageSource
{
    public async Task<IReadOnlyList<OrganisationUsageLine>> UsageAsync(Guid organisationId, CancellationToken ct = default)
    {
        var contracts = await db.Contracts.AsNoTracking().CountAsync(c => c.OrganisationId == organisationId, ct);
        return contracts == 0 ? [] : [new OrganisationUsageLine("[[contract|plural|sentence]]", contracts)];
    }
}
