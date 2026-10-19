using Ecp.SharedKernel;
using Ecp.Tenancy.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecp.Tenancy;

/// <summary>Implementation evidence facts for the tenant: how many organisations it holds. A count only; no organisation name leaves this class.</summary>
internal sealed class TenancyOperationalProbe(TenancyDbContext db) : IOperationalProbe
{
    public string Area => "Tenancy";

    public async Task<IReadOnlyList<OperationalFact>> ProbeAsync(DateTime nowUtc, CancellationToken ct = default)
        => [new(Area, "Organisations.Active", await db.Organisations.AsNoTracking().CountAsync(ct))];
}
