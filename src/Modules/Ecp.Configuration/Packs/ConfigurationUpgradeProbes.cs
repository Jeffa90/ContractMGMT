using Ecp.Configuration.Domain;
using Ecp.Configuration.Persistence;
using Ecp.Packs;
using Ecp.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Ecp.Configuration;

/// <summary>
/// Upgrade pre-flight for published configuration (docs/UPGRADES.md, "Pack and configuration compatibility"): is everything this tenant has published, and everything it is still drafting, acceptable to the validators of the
/// version being upgraded to? It reads and validates; it never republishes, rewrites a release, or changes a draft.
/// </summary>
internal sealed class ConfigurationUpgradeProbe(ConfigurationDbContext db, IEnumerable<IConfigurationKind> kinds, ILogger<ConfigurationUpgradeProbe> log) : IUpgradeProbe
{
    public string Area => "Configuration";

    public async Task<IReadOnlyList<UpgradeFinding>> ProbeAsync(CancellationToken ct = default)
    {
        var findings = new List<UpgradeFinding>();
        var known = kinds.ToDictionary(k => k.Kind, StringComparer.Ordinal);
        var latest = await db.Releases.AsNoTracking().GroupBy(r => new { r.OrganisationId, r.Kind }).Select(g => new { g.Key.OrganisationId, g.Key.Kind, Number = g.Max(r => r.Number) }).ToListAsync(ct);
        var unknownKinds = new SortedSet<string>(StringComparer.Ordinal);
        var invalid = new List<string>(); var unjudged = new List<string>();
        foreach (var item in latest)
        {
            var release = await db.Releases.AsNoTracking().FirstAsync(r => r.OrganisationId == item.OrganisationId && r.Kind == item.Kind && r.Number == item.Number, ct);
            if (!known.TryGetValue(release.Kind, out var kind)) { unknownKinds.Add(release.Kind); continue; }
            try
            {
                var report = await kind.ValidateForUpgradeAsync(release.OrganisationId, release.ContentJson, ct);
                if (!report.IsValid) invalid.Add($"{release.Kind} release {release.Number}: " + string.Join("; ", report.Messages.Where(m => m.Severity == ValidationSeverity.Error).Take(3).Select(m => m.Code)));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Upgrade probe could not validate a published {Kind} release.", release.Kind);
                unjudged.Add($"{release.Kind} release {release.Number} ({ex.GetType().Name})");
            }
        }
        if (unknownKinds.Count > 0)
            findings.Add(new("Configuration", "UPG-CFG-001", UpgradeFindingSeverity.Warning, "Published configuration of a kind this version does not know",
                "Some published configuration belongs to a kind that the version being upgraded to does not recognise. It would be kept as it is but would have no effect.",
                "Kinds: " + string.Join(", ", unknownKinds), "Decide with the owner whether the configuration is still needed; do not delete releases. A newer pack or version may bring the kind back.", unknownKinds.Count));
        if (invalid.Count > 0)
            findings.Add(new("Configuration", "UPG-CFG-002", UpgradeFindingSeverity.Blocker, "A live configuration release would no longer pass validation",
                "At least one release that is in use today would be refused by the validators of the version being upgraded to. The release itself is immutable and is not changed, but the platform could not rely on it.",
                string.Join(" | ", invalid.Take(10)), "Do not upgrade yet. Obtain a version that accepts this configuration, or have the owner publish a corrected release first (which creates a new release; history is kept).", invalid.Count));
        if (unjudged.Count > 0)
            findings.Add(new("Configuration", "UPG-CFG-003", UpgradeFindingSeverity.Warning, "Some published configuration could not be checked",
                "A validator could not complete for some published releases, so their compatibility is not verified.", string.Join(" | ", unjudged.Take(10)), "Check the application log with the correlation id and run the pre-flight again.", unjudged.Count));

        var drafts = await db.Drafts.AsNoTracking().Where(d => d.Status == DraftStatus.Draft || d.Status == DraftStatus.Validated).ToListAsync(ct);
        var badDrafts = new List<string>();
        foreach (var draft in drafts)
        {
            if (!known.TryGetValue(draft.Kind, out var kind)) { badDrafts.Add(draft.Kind + " (unknown kind)"); continue; }
            try
            {
                var report = await kind.ValidateForUpgradeAsync(draft.OrganisationId, draft.ContentJson, ct);
                if (!report.IsValid) badDrafts.Add(draft.Kind);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { badDrafts.Add(draft.Kind + " (not checked)"); }
        }
        if (badDrafts.Count > 0)
            findings.Add(new("Configuration", "UPG-CFG-004", UpgradeFindingSeverity.Warning, "Open drafts that the new version would not accept",
                "Some drafts that people are still working on would fail validation in the version being upgraded to. They are kept untouched; they will need correcting before they can be published.",
                string.Join(", ", badDrafts.GroupBy(x => x).Select(g => $"{g.Key} ×{g.Count()}")), "Ask the authors to finish or discard these drafts, or accept that they will need editing after the upgrade.", badDrafts.Count));
        if (drafts.Count > 0)
            findings.Add(new("Configuration", "UPG-CFG-005", UpgradeFindingSeverity.Info, "Open configuration drafts", "Drafts are kept as they are by an upgrade. They are never published for anyone.", $"{drafts.Count} open draft(s).", "", drafts.Count));
        return findings;
    }
}

/// <summary>Installed and shipped packs against the version being upgraded to: platform range, format, kinds and schemas, signatures. A pack in use that fails is a blocker; one that nobody uses is a warning. No package bytes are touched.</summary>
internal sealed class PackUpgradeProbe(IPackLoader packs, IConfigurationInventory inventory) : IUpgradeProbe
{
    public string Area => "Packs";

    public async Task<IReadOnlyList<UpgradeFinding>> ProbeAsync(CancellationToken ct = default)
    {
        var findings = new List<UpgradeFinding>();
        var inUse = (await inventory.AssignmentsAsync(ct)).Select(a => (a.PackKey, a.Version)).ToHashSet();
        var incompatibleInUse = new List<string>(); var incompatibleIdle = new List<string>();
        var untrustedInUse = new List<string>(); var unreadableInUse = new List<string>();
        foreach (var entry in await packs.ListAsync(ct))
        {
            var label = $"{entry.PackKey}@{entry.Version}";
            var used = inUse.Contains((entry.PackKey, entry.Version));
            if (entry.Unavailable) { if (used) unreadableInUse.Add(label); continue; }
            if (!entry.IsCompatible)
            {
                var why = string.Join(",", entry.CompatibilityIssues.Where(i => i.IsError).Select(i => i.Code).Distinct().Take(3));
                (used ? incompatibleInUse : incompatibleIdle).Add($"{label} [{why}]");
            }
            if (used && !entry.Trust.IsAcceptable) untrustedInUse.Add($"{label} [{entry.Trust.Status}]");
        }
        if (incompatibleInUse.Count > 0)
            findings.Add(new("Packs", "UPG-PCK-001", UpgradeFindingSeverity.Blocker, "A solution pack in use is not compatible with the new version",
                "An organisation uses a pack whose platform range, format, content kinds or schema versions the version being upgraded to does not accept.", string.Join(" | ", incompatibleInUse.Distinct().Take(10)),
                "Do not upgrade yet. Obtain a pack version that supports the new platform version and install it (installing creates a new version; assigned organisations keep the old one until the owner moves them).", incompatibleInUse.Distinct().Count()));
        if (incompatibleIdle.Count > 0)
            findings.Add(new("Packs", "UPG-PCK-002", UpgradeFindingSeverity.Warning, "Installed packs that the new version cannot use",
                "Some installed pack versions that no organisation uses would not be usable after the upgrade. They are kept (signed bytes are never rewritten) but cannot be assigned.", string.Join(" | ", incompatibleIdle.Distinct().Take(10)),
                "Nothing is required. Retire them later if they are no longer wanted.", incompatibleIdle.Distinct().Count()));
        if (untrustedInUse.Count > 0)
            findings.Add(new("Packs", "UPG-PCK-003", UpgradeFindingSeverity.Blocker, "A pack in use fails the signature policy",
                "A pack that an organisation uses is not signed by a key the environment trusts, which this environment's policy does not allow.", string.Join(" | ", untrustedInUse.Distinct().Take(10)),
                "Fix the trust configuration (public keys only) or install a correctly signed version before upgrading.", untrustedInUse.Distinct().Count()));
        if (unreadableInUse.Count > 0)
            findings.Add(new("Packs", "UPG-PCK-004", UpgradeFindingSeverity.Blocker, "A pack in use cannot be read",
                "The package file for a pack that an organisation uses is missing or damaged.", string.Join(" | ", unreadableInUse.Distinct().Take(10)), "Restore the package files from a verified backup before upgrading.", unreadableInUse.Distinct().Count()));
        return findings;
    }
}
