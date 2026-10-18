using Ecp.Operations.Domain;
using Ecp.Operations.Persistence;
using Ecp.Persistence;
using Ecp.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Ecp.Operations;

public sealed record MigrationRecordDto(string Module, string MigrationId, string Checksum, string Generator, string PlatformVersion, string Origin, Guid? UpgradeRunId, DateTime RecordedUtc);

public sealed record NewMigrationRecord(string Module, string MigrationId, string Checksum, string Generator, string PlatformVersion, string Origin, Guid? UpgradeRunId);

/// <summary>
/// The upgrade tooling's record of the migrations it has verified or applied, with the checksum of each (docs/UPGRADES.md). Rows are added, never changed. An installation that has not been through the upgrade tooling yet has no
/// rows (or, before the table's own migration has run, no table), which <see cref="ListAsync"/> reports as null so the caller can say "not recorded" instead of failing.
/// </summary>
public interface IMigrationRecords
{
    Task<IReadOnlyList<MigrationRecordDto>?> ListAsync(CancellationToken ct = default);
    Task AddAsync(IEnumerable<NewMigrationRecord> records, CancellationToken ct = default);
}

internal sealed class MigrationRecords(OperationsDbContext db, IUnitOfWork uow, TenantContext tenant, IClock clock) : IMigrationRecords
{
    public async Task<IReadOnlyList<MigrationRecordDto>?> ListAsync(CancellationToken ct = default)
    {
        using var _ = tenant.UseInstallationScope();
        try
        {
            return (await db.MigrationRecords.AsNoTracking().OrderBy(r => r.Module).ThenBy(r => r.MigrationId).ToListAsync(ct))
                .Select(r => new MigrationRecordDto(r.Module, r.MigrationId, r.Checksum, r.Generator, r.PlatformVersion, r.Origin, r.UpgradeRunId, r.RecordedUtc)).ToList();
        }
        catch (Exception ex) when (ex is Microsoft.Data.SqlClient.SqlException { Number: 208 } or Microsoft.Data.Sqlite.SqliteException) { return null; }   // 208: invalid object name (the table has not been created yet)
    }

    public async Task AddAsync(IEnumerable<NewMigrationRecord> records, CancellationToken ct = default)
    {
        using var _ = tenant.UseInstallationScope();
        var list = records.ToList();
        if (list.Count == 0) return;
        await uow.ExecuteAsync(async c =>
        {
            var existing = (await db.MigrationRecords.AsNoTracking().Select(r => r.Module + "/" + r.MigrationId).ToListAsync(c)).ToHashSet(StringComparer.Ordinal);
            foreach (var r in list.Where(r => !existing.Contains(r.Module + "/" + r.MigrationId)))
                db.MigrationRecords.Add(MigrationRecord.Create(r.Module, r.MigrationId, r.Checksum, r.Generator, r.PlatformVersion, r.Origin, r.UpgradeRunId, clock.UtcNow));
            await db.SaveChangesAsync(c);
        }, ct);
    }
}
