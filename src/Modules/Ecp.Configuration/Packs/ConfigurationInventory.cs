using Ecp.Configuration.Domain;
using Ecp.Configuration.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecp.Configuration;

/// <summary>A published release as backup and restore see it: which one, its recorded hash, and whether its content still produces that hash.</summary>
public sealed record ReleaseFingerprint(Guid ReleaseId, Guid OrganisationId, string Kind, int Number, string StoredHash, bool HashMatchesContent);
/// <summary>An installed package record: what was installed, the hash of its bytes, and what was concluded about its signature when it was installed.</summary>
public sealed record PackRecordFingerprint(string PackKey, string Version, string PackageSha256, string SignatureStatus, string? TrustKeyId, string Status, string StorageReference, string SourceType);
public sealed record PackAssignmentFingerprint(Guid OrganisationId, string PackKey, string Version, string PackageSha256, string Status);
/// <summary>Public trust-store metadata only. The key material itself is never copied into a manifest or an evidence report.</summary>
public sealed record TrustKeyFingerprint(string KeyId, string Fingerprint, string Status, string Algorithm);

/// <summary>
/// Trusted, read-only inventory of the current tenant's configuration and pack records for backup manifests and post-restore validation (docs/BACKUP_AND_RESTORE.md). No content other than hashes and keys is returned,
/// and no per-user authorisation is applied: it is not reachable from request input.
/// </summary>
public interface IConfigurationInventory
{
    Task<IReadOnlyList<ReleaseFingerprint>> ReleasesAsync(CancellationToken ct = default);
    Task<int> OpenDraftCountAsync(CancellationToken ct = default);
    Task<IReadOnlyList<PackRecordFingerprint>> InstalledPacksAsync(CancellationToken ct = default);
    Task<IReadOnlyList<PackAssignmentFingerprint>> AssignmentsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<TrustKeyFingerprint>> TrustKeysAsync(CancellationToken ct = default);
}

internal sealed class ConfigurationInventory(ConfigurationDbContext db) : IConfigurationInventory
{
    public async Task<IReadOnlyList<ReleaseFingerprint>> ReleasesAsync(CancellationToken ct = default)
    {
        var rows = await db.Releases.AsNoTracking().OrderBy(r => r.Kind).ThenBy(r => r.OrganisationId).ThenBy(r => r.Number).ToListAsync(ct);
        return rows.Select(r => new ReleaseFingerprint(r.Id, r.OrganisationId, r.Kind, r.Number, r.ContentHash, string.Equals(ConfigHash.Of(r.ContentJson), r.ContentHash, StringComparison.Ordinal))).ToList();
    }

    public Task<int> OpenDraftCountAsync(CancellationToken ct = default)
        => db.Drafts.AsNoTracking().CountAsync(d => d.Status == DraftStatus.Draft || d.Status == DraftStatus.Validated, ct);

    public async Task<IReadOnlyList<PackRecordFingerprint>> InstalledPacksAsync(CancellationToken ct = default)
        => (await db.InstalledPacks.AsNoTracking().OrderBy(p => p.PackKey).ThenBy(p => p.Version).ToListAsync(ct))
            .Select(p => new PackRecordFingerprint(p.PackKey, p.Version, p.PackageSha256, p.SignatureStatus, p.TrustKeyId, p.Status, p.StorageReference, p.SourceType)).ToList();

    public async Task<IReadOnlyList<PackAssignmentFingerprint>> AssignmentsAsync(CancellationToken ct = default)
        => (await db.PackAssignments.AsNoTracking().OrderBy(a => a.OrganisationId).ThenBy(a => a.AssignedUtc).ToListAsync(ct))
            .Select(a => new PackAssignmentFingerprint(a.OrganisationId, a.PackKey, a.Version, a.PackageSha256, a.Status)).ToList();

    public async Task<IReadOnlyList<TrustKeyFingerprint>> TrustKeysAsync(CancellationToken ct = default)
        => (await db.TrustedKeys.AsNoTracking().OrderBy(k => k.KeyId).ToListAsync(ct)).Select(k => new TrustKeyFingerprint(k.KeyId, k.Fingerprint, k.Status, k.Algorithm)).ToList();
}
