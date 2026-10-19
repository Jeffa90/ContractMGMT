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
    {
        var facts = new List<OperationalFact>
        {
            new(Area, "Batches.Committing", await db.Batches.AsNoTracking().CountAsync(b => b.Status == BatchStatus.Committing, ct)),
            new(Area, "Batches.Staged", await db.Batches.AsNoTracking().CountAsync(b => b.Status == BatchStatus.Created || b.Status == BatchStatus.Profiled || b.Status == BatchStatus.Validated, ct)),
        };
        // Evidence facts (counts only): where the loads stand, and what the data-handling checkpoint recorded.
        foreach (var status in new[] { BatchStatus.Committed, BatchStatus.Reconciled, BatchStatus.Accepted, BatchStatus.RolledBack, BatchStatus.Failed })
            facts.Add(new(Area, "Batches." + status, await db.Batches.AsNoTracking().CountAsync(b => b.Status == status, ct)));
        var loaded = db.Batches.AsNoTracking().Where(b => b.Kind != BatchKind.DryRun);
        facts.Add(new(Area, "Batches.MaskedCompanyData", await db.BatchClassifications.AsNoTracking().CountAsync(c => c.DataClass == BatchDataClasses.MaskedCompanyData, ct)));
        facts.Add(new(Area, "Batches.Synthetic", await db.BatchClassifications.AsNoTracking().CountAsync(c => c.DataClass == BatchDataClasses.Synthetic, ct)));
        facts.Add(new(Area, "Batches.Unclassified", await loaded.CountAsync(b => !db.BatchClassifications.Any(c => c.BatchId == b.Id), ct)));
        facts.Add(new(Area, "Masked.Declarations.Current", await db.MaskedDeclarations.AsNoTracking().CountAsync(d => d.RevokedUtc == null && d.ReviewByUtc > nowUtc, ct)));
        facts.Add(new(Area, "Masked.Declarations.Expired", await db.MaskedDeclarations.AsNoTracking().CountAsync(d => d.RevokedUtc == null && d.ReviewByUtc <= nowUtc, ct)));
        facts.Add(new(Area, "Masked.Declarations.Revoked", await db.MaskedDeclarations.AsNoTracking().CountAsync(d => d.RevokedUtc != null, ct)));
        facts.Add(new(Area, "Masked.Declarations.PersonalData", await db.MaskedDeclarations.AsNoTracking().CountAsync(d => d.RevokedUtc == null && d.PersonalDataIncluded, ct)));
        return facts;
    }
}
