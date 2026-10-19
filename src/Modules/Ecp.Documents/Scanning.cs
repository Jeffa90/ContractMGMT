using System.Text.Json;
using Ecp.Audit;
using Ecp.Documents.Domain;
using Ecp.Documents.Persistence;
using Ecp.Documents.Storage;
using Ecp.Identity;
using Ecp.Persistence;
using Ecp.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Ecp.Documents;

public sealed record ScanOverview(
    string Provider, bool ScannerConnected, bool DemonstrationOnly, bool AllowSkippedScan, int Clean, int Pending, int Skipped, int Error, int Infected, DateTime? OldestHeldUtc, int MaxAttempts)
{
    public int Held => Pending + Skipped + Error + Infected;
}

public sealed record ScanItem(Guid DocumentId, Guid VersionId, Guid OrganisationId, Guid? ContractId, string Title, string FileName, ScanStatus Status, DateTime UploadedUtc, int Attempts, DateTime? LastAttemptUtc, string? LastDetail, bool GivenUp);

public sealed record RescanResult(int Examined, int Clean, int Infected, int Error, int Skipped, int GivenUp);

/// <summary>What an administrator sees and does about the malware gate. Looking and retrying need <c>notifications.administer</c> (background-job status and retry), for the organisations the person administers; nothing outside them is shown.</summary>
public interface IDocumentScanning
{
    Task<ScanOverview> OverviewAsync(CancellationToken ct = default);
    /// <summary>Versions that are not yet released: waiting, skipped, failed, or infected (kept in quarantine), most recent first.</summary>
    Task<IReadOnlyList<ScanItem>> HeldAsync(int max = 200, CancellationToken ct = default);
    /// <summary>Scans one held version again now (a failed or waiting scan). An infected version is never released this way.</summary>
    Task<ScanStatus> RescanOneAsync(Guid versionId, CancellationToken ct = default);
}

/// <summary>The scheduled retry. Not reachable from request input; it holds no user and takes only what is held.</summary>
public interface IDocumentScanJob
{
    Task<RescanResult> RunAsync(CancellationToken ct = default);
}

/// <summary>
/// The gate between an uploaded file and everything that relies on it. A version is released only when a connected scanner calls it clean; it is held while the scan waits, was skipped, or failed (fail-closed: a failure is never
/// read as clean), and an infected version is kept in quarantine, never downloadable, copied or counted as evidence. A held version is tried again by a scheduled job, a bounded number of times, and every attempt is recorded and
/// audited. The job is safe to repeat and safe to run twice: it re-reads each version inside the transaction and records nothing if another run already settled it.
/// </summary>
internal sealed class DocumentScanning(
    DocumentsDbContext db, IDocumentStorageProvider storage, IMalwareScanner scanner, IUnitOfWork uow, IAuditWriter audit, ITenantContext tenant, IClock clock, IOptions<DocumentsOptions> options, IAccessService access) : IDocumentScanning, IDocumentScanJob
{
    private const string Permission = "notifications.administer";

    /// <summary>The organisations the person may administer, or null for all of them. Refuses a person who holds the permission nowhere.</summary>
    private async Task<HashSet<Guid>?> ScopeAsync(CancellationToken ct)
    {
        var grants = await access.GrantsAsync(Permission, ct);
        if (grants.Count == 0) throw new ForbiddenException("You cannot view or retry document scanning.");
        return grants.Any(g => g.OrganisationIds is null) ? null : grants.SelectMany(g => g.OrganisationIds!).ToHashSet();
    }

    private IQueryable<DocumentVersion> VersionsIn(HashSet<Guid>? scope)
        => scope is null ? db.Versions.AsNoTracking()
            : from v in db.Versions.AsNoTracking() join d in db.Documents.AsNoTracking() on v.DocumentId equals d.Id where scope.Contains(d.OrganisationId) select v;

    public static ScanStatus StatusOf(ScanVerdict verdict) => verdict switch { ScanVerdict.Clean => ScanStatus.Clean, ScanVerdict.Infected => ScanStatus.Infected, ScanVerdict.Skipped => ScanStatus.Skipped, _ => ScanStatus.Error };

    public async Task<ScanOverview> OverviewAsync(CancellationToken ct = default)
    {
        var scope = await ScopeAsync(ct);
        var counts = await VersionsIn(scope).GroupBy(v => v.ScanStatus).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        int Of(ScanStatus s) => counts.FirstOrDefault(c => c.Key == s)?.Count ?? 0;
        var oldest = await VersionsIn(scope).Where(v => v.ScanStatus != ScanStatus.Clean).OrderBy(v => v.UploadedUtc).Select(v => (DateTime?)v.UploadedUtc).FirstOrDefaultAsync(ct);
        return new ScanOverview(scanner.Name, scanner.IsConnected, scanner.IsDemonstrationOnly, options.Value.AllowSkippedScan, Of(ScanStatus.Clean), Of(ScanStatus.Pending), Of(ScanStatus.Skipped), Of(ScanStatus.Error), Of(ScanStatus.Infected), oldest, options.Value.MaxScanAttempts);
    }

    public async Task<IReadOnlyList<ScanItem>> HeldAsync(int max = 200, CancellationToken ct = default)
    {
        var scope = await ScopeAsync(ct);
        var rows = await (from v in db.Versions.AsNoTracking()
                          join d in db.Documents.AsNoTracking() on v.DocumentId equals d.Id
                          where v.ScanStatus != ScanStatus.Clean && (scope == null || scope.Contains(d.OrganisationId))
                          orderby v.UploadedUtc descending
                          select new { v.Id, v.DocumentId, d.OrganisationId, d.ContractId, d.Title, v.FileName, v.ScanStatus, v.UploadedUtc }).Take(Math.Clamp(max, 1, 500)).ToListAsync(ct);
        var ids = rows.Select(r => r.Id).ToList();
        var attempts = await db.ScanAttempts.AsNoTracking().Where(a => ids.Contains(a.VersionId)).Select(a => new { a.VersionId, a.AttemptedUtc, a.Outcome, a.Detail }).ToListAsync(ct);
        return rows.Select(r =>
        {
            var mine = attempts.Where(a => a.VersionId == r.Id).OrderBy(a => a.AttemptedUtc).ToList();
            var errors = mine.Count(a => a.Outcome == ScanStatus.Error);
            return new ScanItem(r.DocumentId, r.Id, r.OrganisationId, r.ContractId, r.Title, r.FileName, r.ScanStatus, r.UploadedUtc, mine.Count, mine.LastOrDefault()?.AttemptedUtc, mine.LastOrDefault()?.Detail,
                r.ScanStatus == ScanStatus.Error && errors >= options.Value.MaxScanAttempts);
        }).ToList();
    }

    /// <summary>Scans a stored version. A failure of any kind is an error verdict with a short note; it is never clean.</summary>
    private async Task<(ScanStatus Status, string? Detail)> ScanAsync(DocumentVersion v, CancellationToken ct)
    {
        try
        {
            await using var read = await storage.OpenReadAsync(tenant.RequireTenantId(), v.StorageKey, ct);
            var verdict = await scanner.ScanAsync(read, ct);
            return (StatusOf(verdict), verdict == ScanVerdict.Error ? "The scanner could not complete the scan." : null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return (ScanStatus.Error, "The scan could not be completed (" + ex.GetType().Name + ")."); }
    }

    private static bool NeedsScan(ScanStatus s) => s is ScanStatus.Pending or ScanStatus.Error or ScanStatus.Skipped;

    /// <summary>Scans one version and records the result, only if it is still waiting to be settled when the transaction reads it.</summary>
    private async Task<ScanStatus?> SettleAsync(Guid versionId, string trigger, CancellationToken ct)
    {
        var version = await db.Versions.AsNoTracking().FirstOrDefaultAsync(v => v.Id == versionId, ct);
        if (version is null || !NeedsScan(version.ScanStatus)) return null;
        var (status, detail) = await ScanAsync(version, ct);
        ScanStatus? settled = null;
        await uow.ExecuteAsync(async c =>
        {
            var current = await db.Versions.FirstOrDefaultAsync(v => v.Id == versionId, c);
            if (current is null || !NeedsScan(current.ScanStatus)) return;                 // another run settled it first: record nothing twice
            var document = await db.Documents.AsNoTracking().FirstAsync(d => d.Id == current.DocumentId, c);
            current.RecordScan(status);
            db.ScanAttempts.Add(DocumentScanAttempt.Create(current.TenantId, document.OrganisationId, current.DocumentId, current.Id, status, scanner.Name, trigger, detail, clock.UtcNow));
            await db.SaveChangesAsync(c);
            audit.Record(new AuditEntry(status switch { ScanStatus.Infected => "document.quarantined", ScanStatus.Error => "document.scan.failed", _ => "document.scan.completed" }, nameof(DocumentVersion), current.Id.ToString(),
                OrganisationId: document.OrganisationId, Classification: document.Classification, NewValuesJson: JsonSerializer.Serialize(new { outcome = status.ToString(), provider = scanner.Name, trigger, document = document.Id })));
            settled = status;
            if (status == ScanStatus.Error)
            {
                // The scanner could not give a verdict. When the bounded retries are used up the file stays held and a person has to act, so that is its own event (and notification).
                var failures = await db.ScanAttempts.AsNoTracking().CountAsync(a => a.VersionId == current.Id && a.Outcome == ScanStatus.Error, c);
                if (failures >= options.Value.MaxScanAttempts)
                    audit.Record(new AuditEntry("document.scan.retry_exhausted", nameof(DocumentVersion), current.Id.ToString(), OrganisationId: document.OrganisationId, Classification: document.Classification,
                        NewValuesJson: JsonSerializer.Serialize(new { provider = scanner.Name, attempts = failures, document = document.Id })));
            }
        }, ct);
        return settled;
    }

    public async Task<ScanStatus> RescanOneAsync(Guid versionId, CancellationToken ct = default)
    {
        var scope = await ScopeAsync(ct);
        if (!scanner.IsConnected) throw new DomainException("scanner_not_connected", "No malware scanner is connected, so nothing can be scanned. The file stays held until one is connected.");
        var version = await VersionsIn(scope).FirstOrDefaultAsync(v => v.Id == versionId, ct) ?? throw new ResourceNotFoundException();      // outside the person's organisations it is simply not found
        if (version.ScanStatus == ScanStatus.Infected) throw new DomainException("quarantined", "That file was found to be infected. It stays in quarantine and is never released by scanning it again.");
        if (version.ScanStatus == ScanStatus.Clean) return ScanStatus.Clean;
        return await SettleAsync(versionId, "manual", ct) ?? (await db.Versions.AsNoTracking().FirstAsync(v => v.Id == versionId, ct)).ScanStatus;
    }

    public async Task<RescanResult> RunAsync(CancellationToken ct = default)
    {
        // With no scanner connected nothing can change: versions stay held (fail-closed) and the readiness check says so.
        if (!scanner.IsConnected) return new RescanResult(0, 0, 0, 0, 0, 0);
        var limit = Math.Max(1, options.Value.ScanBatchSize);
        var candidates = await db.Versions.AsNoTracking().Where(v => v.ScanStatus == ScanStatus.Pending || v.ScanStatus == ScanStatus.Error || v.ScanStatus == ScanStatus.Skipped)
            .OrderBy(v => v.UploadedUtc).Select(v => new { v.Id, v.ScanStatus }).Take(limit * 4).ToListAsync(ct);
        int examined = 0, clean = 0, infected = 0, error = 0, skipped = 0, gaveUp = 0;
        foreach (var candidate in candidates)
        {
            if (examined >= limit) break;
            if (candidate.ScanStatus == ScanStatus.Error)
            {
                var failures = await db.ScanAttempts.AsNoTracking().CountAsync(a => a.VersionId == candidate.Id && a.Outcome == ScanStatus.Error, ct);
                if (failures >= options.Value.MaxScanAttempts) { gaveUp++; continue; }
            }
            examined++;
            switch (await SettleAsync(candidate.Id, "rescan", ct))
            {
                case ScanStatus.Clean: clean++; break;
                case ScanStatus.Infected: infected++; break;
                case ScanStatus.Error: error++; break;
                case ScanStatus.Skipped: skipped++; break;
            }
        }
        return new RescanResult(examined, clean, infected, error, skipped, gaveUp);
    }
}
