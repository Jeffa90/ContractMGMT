using Ecp.DataMigration.Domain;
using Ecp.DataMigration.Persistence;
using Ecp.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Ecp.DataMigration;

/// <summary>Upgrade pre-flight for data-migration batches: a batch that is committing must finish (or be rolled back) first; staged work is kept as it is.</summary>
internal sealed class MigrationUpgradeProbe(MigrationDbContext db) : IUpgradeProbe
{
    public string Area => "DataMigration";

    public async Task<IReadOnlyList<UpgradeFinding>> ProbeAsync(CancellationToken ct = default)
    {
        var findings = new List<UpgradeFinding>();
        var committing = await db.Batches.AsNoTracking().CountAsync(b => b.Status == BatchStatus.Committing, ct);
        if (committing > 0)
            findings.Add(new("DataMigration", "UPG-MIG-001", UpgradeFindingSeverity.Blocker, "A migration batch is committing",
                "A data migration is part-way through writing records. Upgrading now could interrupt it. It can be resumed or rolled back from the migration pages, but not while the installation is being upgraded.",
                $"{committing} committing batch(es).", "Let the batch finish, or resume or roll it back, then run the pre-flight again.", committing));
        var staged = await db.Batches.AsNoTracking().CountAsync(b => b.Status == BatchStatus.Created || b.Status == BatchStatus.Profiled || b.Status == BatchStatus.Validated, ct);
        if (staged > 0)
            findings.Add(new("DataMigration", "UPG-MIG-002", UpgradeFindingSeverity.Info, "Migration batches are staged",
                "Some batches are staged but not committed. They are kept; no commit can start while the installation is in maintenance mode.", $"{staged} staged batch(es).", "", staged));
        return findings;
    }
}
