using Ecp.Configuration.Domain;
using Ecp.Configuration.Persistence;
using Ecp.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Ecp.Configuration;

/// <summary>
/// Operations dashboard facts for configuration and packs (docs/OPERATIONS_DASHBOARD.md): how much is published, whether any installed pack would be refused today, and when the trusted pack-signing keys expire.
/// Counts and times only: no configuration content, no key material and no key identifier leave this class.
/// </summary>
internal sealed class ConfigurationOperationalProbe(ConfigurationDbContext db, PackUpgradeProbe packs) : IOperationalProbe
{
    public string Area => "Configuration";

    public async Task<IReadOnlyList<OperationalFact>> ProbeAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        var facts = new List<OperationalFact>();
        var releases = await db.Releases.AsNoTracking().GroupBy(r => new { r.OrganisationId, r.Kind }).Select(g => g.Key).CountAsync(ct);
        facts.Add(new(Area, "Releases.InForce", releases));
        facts.Add(new(Area, "Drafts.Open", await db.Drafts.AsNoTracking().CountAsync(d => d.Status == DraftStatus.Draft || d.Status == DraftStatus.Validated, ct)));

        var found = await packs.ProbeAsync(ct);
        long Count(string code) => found.Where(f => f.Code == code).Sum(f => (long)f.Count);
        facts.Add(new("Packs", "Incompatible.InUse", Count("UPG-PCK-001")));
        facts.Add(new("Packs", "Incompatible.Idle", Count("UPG-PCK-002")));
        facts.Add(new("Packs", "Untrusted.InUse", Count("UPG-PCK-003")));
        facts.Add(new("Packs", "Unreadable.InUse", Count("UPG-PCK-004")));
        facts.Add(new("Packs", "Installed", await db.InstalledPacks.AsNoTracking().CountAsync(ct)));

        // Pack-signing keys: only active keys matter. A key with no end date never expires.
        var keys = await db.TrustedKeys.AsNoTracking().Where(k => k.Status == TrustedPublisherKey.Active).Select(k => k.NotAfterUtc).ToListAsync(ct);
        var dated = keys.Where(k => k.HasValue).Select(k => k!.Value).ToList();
        facts.Add(new("Certificates", "PackKeys.Active", keys.Count));
        facts.Add(new("Certificates", "PackKeys.Expired", dated.Count(d => d <= nowUtc)));
        facts.Add(new("Certificates", "PackKeys.Expiring30d", dated.Count(d => d > nowUtc && d <= nowUtc.AddDays(30))));
        var soonest = dated.Where(d => d > nowUtc).DefaultIfEmpty().Min();
        facts.Add(new("Certificates", "PackKeys.NextExpiryUtc", soonest == default ? 0 : 1, soonest == default ? null : soonest, FactCombine.Earliest));
        return facts;
    }
}
