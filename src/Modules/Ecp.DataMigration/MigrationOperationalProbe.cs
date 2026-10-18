using Ecp.DataMigration.Domain;
using Ecp.DataMigration.Persistence;
using Ecp.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Ecp.DataMigration;

/// <summary>Operations dashboard facts for data-migration batches that are in progress: counts only, no batch name, file or record.</summary>
internal sealed class MigrationOperationalProbe(MigrationDbContext db) : IOperationalProbe
{
    public string Area => "DataMigration";

    public async Task<IReadOnlyList<OperationalFact>> ProbeAsync(DateTime nowUtc, CancellationToken ct = default)
        =>
        [
            new(Area, "Batches.Committing", await db.Batches.AsNoTracking().CountAsync(b => b.Status == BatchStatus.Committing, ct)),
            new(Area, "Batches.Staged", await db.Batches.AsNoTracking().CountAsync(b => b.Status == BatchStatus.Created || b.Status == BatchStatus.Profiled || b.Status == BatchStatus.Validated, ct)),
        ];
}
