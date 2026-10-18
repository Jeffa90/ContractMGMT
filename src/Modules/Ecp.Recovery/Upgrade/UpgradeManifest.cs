using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ecp.Packs;
using Ecp.Persistence;
using Ecp.SharedKernel;

namespace Ecp.Recovery;

// The application-upgrade manifest (docs/UPGRADES.md). A release carries, as data, what an upgrade to it changes, what it requires and how to go back. The platform never claims an upgrade path that
// is not written in a manifest it can verify, and it never edits a manifest: a manifest is produced at release time, committed, embedded and read-only.

public sealed record LedgerMigration(string Id, string Checksum, IReadOnlyList<string> Categories, IReadOnlyList<string> Objects, int Operations);

public sealed record LedgerModule(string Module, int Tier, string Schema, string SchemaVersion, IReadOnlyList<LedgerMigration> Migrations);

/// <summary>The migrations of one released version, frozen at release time. A baseline is the only truthful description of "what N-1 looked like": it is committed and never regenerated.</summary>
public sealed record ReleaseLedger(int FormatVersion, string PlatformVersion, string Generator, string Note, IReadOnlyList<LedgerModule> Modules, string LedgerSha256);

public sealed record UpgradeNotice(string Code, string Text);

public sealed record NewMigration(string Module, string Id, string Checksum, IReadOnlyList<string> Categories, IReadOnlyList<string> Objects, bool ReviewRequired, bool Reviewed);

public sealed record PackCompatibilityInfo(int PackageFormatVersion, IReadOnlyList<int> SupportedConfigurationSchemaVersions, string PlatformVersionRule);

public sealed record UpgradeRequirements(bool VerifiedBackupRequired, int MaximumBackupAgeHours, bool MaintenanceRequired, bool BackupMustMatchInstallation);

public sealed record RollbackPlan(string Method, IReadOnlyList<string> Steps, string DataLossStatement);

public sealed record UpgradeManifest(
    int FormatVersion, string ReleaseId, string SourcePlatformVersion, string TargetPlatformVersion, string MinimumSupportedSourceVersion, IReadOnlyList<string> SupportedSourceVersions,
    string SourceLedgerSha256, string TargetLedgerSha256, IReadOnlyList<string> ModuleOrder, IReadOnlyList<LedgerModule> TargetModules, IReadOnlyList<NewMigration> NewMigrations,
    PackCompatibilityInfo Packs, IReadOnlyList<UpgradeNotice> ConfigurationChanges, UpgradeRequirements Requirements, IReadOnlyList<UpgradeNotice> OperationalActions,
    IReadOnlyList<UpgradeNotice> CompatibilityWarnings, IReadOnlyList<UpgradeNotice> BreakingChanges, IReadOnlyList<string> PostUpgradeChecks, RollbackPlan Rollback, string ManifestSha256);

/// <summary>The hand-written part of a release: what people need to know that the migrations themselves cannot say.</summary>
public sealed record ReleaseNotes(
    int FormatVersion, string ReleaseId, string From, string To, string MinimumSupportedSourceVersion, IReadOnlyList<string> SupportedSourceVersions, int MaximumBackupAgeHours,
    IReadOnlyList<UpgradeNotice> ConfigurationChanges, IReadOnlyList<UpgradeNotice> OperationalActions, IReadOnlyList<UpgradeNotice> CompatibilityWarnings, IReadOnlyList<UpgradeNotice> BreakingChanges,
    IReadOnlyList<string> PostUpgradeChecks, RollbackPlan Rollback);

public sealed record MigrationReviewEntry(string Module, string Migration, IReadOnlyList<string> Categories, string Basis, string Summary, string Evidence);

public sealed record MigrationReviewRegister(int FormatVersion, IReadOnlyList<MigrationReviewEntry> Entries);

public static class UpgradeJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32,
        NumberHandling = JsonNumberHandling.Strict,
    };

    private static readonly JsonSerializerOptions Compact = new(Options) { WriteIndented = false };

    public static string Sha256Hex(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    public static string LedgerHash(IReadOnlyList<LedgerModule> modules) => Sha256Hex(JsonSerializer.Serialize(modules, Compact));

    public static string ManifestHash(UpgradeManifest manifest) => Sha256Hex(JsonSerializer.Serialize(manifest with { ManifestSha256 = "" }, Compact));

    public static byte[] Serialise<T>(T value) => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, Options) + "\n");

    public static T Parse<T>(ReadOnlySpan<byte> bytes, int maxBytes = 4_000_000) where T : class
    {
        if (bytes.Length == 0 || bytes.Length > maxBytes) throw new DomainException("upgrade_manifest_invalid", "The upgrade document is empty or larger than the limit.");
        try { return JsonSerializer.Deserialize<T>(bytes, Options) ?? throw new DomainException("upgrade_manifest_invalid", "The upgrade document is empty."); }
        catch (JsonException) { throw new DomainException("upgrade_manifest_invalid", "The upgrade document is not valid for this version of the platform."); }
    }

    public static IReadOnlyList<LedgerModule> ToLedger(MigrationLedger ledger)
        => ledger.Modules.Select(m => new LedgerModule(m.Module, m.Tier, m.Schema, m.SchemaVersion, m.Migrations.Select(e => new LedgerMigration(e.Id, e.Checksum, e.Categories.ToList(), e.Objects.ToList(), e.Operations)).ToList())).ToList();
}

/// <summary>A problem found in an upgrade document, with a stable code.</summary>
public sealed record UpgradeProblem(string Code, bool Critical, string Message);

/// <summary>Reads the baselines, release notes, review register and manifests that ship inside the platform (embedded at build time from the repository's upgrades folder).</summary>
public sealed class UpgradeCatalogue
{
    public IReadOnlyList<ReleaseLedger> Baselines { get; }
    public IReadOnlyList<UpgradeManifest> Manifests { get; }
    public MigrationReviewRegister Reviews { get; }
    public IReadOnlyList<ReleaseNotes> Notes { get; }

    public UpgradeCatalogue(IReadOnlyList<ReleaseLedger> baselines, IReadOnlyList<UpgradeManifest> manifests, MigrationReviewRegister reviews, IReadOnlyList<ReleaseNotes> notes)
    {
        Baselines = baselines; Manifests = manifests; Reviews = reviews; Notes = notes;
    }

    /// <summary>The catalogue embedded in this build.</summary>
    public static UpgradeCatalogue Embedded() => FromAssembly(typeof(UpgradeCatalogue).Assembly);

    public static UpgradeCatalogue FromAssembly(Assembly assembly)
    {
        var names = assembly.GetManifestResourceNames().Where(n => n.StartsWith("upgrades/", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList();
        byte[] Read(string name) { using var s = assembly.GetManifestResourceStream(name)!; using var m = new MemoryStream(); s.CopyTo(m); return m.ToArray(); }
        var baselines = names.Where(n => n.StartsWith("upgrades/baselines/", StringComparison.Ordinal)).Select(n => UpgradeJson.Parse<ReleaseLedger>(Read(n))).ToList();
        var manifests = names.Where(n => n.StartsWith("upgrades/manifests/", StringComparison.Ordinal)).Select(n => UpgradeJson.Parse<UpgradeManifest>(Read(n))).ToList();
        var notes = names.Where(n => n.StartsWith("upgrades/release-notes/", StringComparison.Ordinal)).Select(n => UpgradeJson.Parse<ReleaseNotes>(Read(n))).ToList();
        var review = names.FirstOrDefault(n => n == "upgrades/migration-reviews.json");
        var register = review is null ? new MigrationReviewRegister(1, []) : UpgradeJson.Parse<MigrationReviewRegister>(Read(review));
        return new UpgradeCatalogue(baselines, manifests, register, notes);
    }

    public ReleaseLedger? Baseline(string version) => Baselines.FirstOrDefault(b => b.PlatformVersion == version);
    public UpgradeManifest? ManifestTo(string targetVersion) => Manifests.FirstOrDefault(m => m.TargetPlatformVersion == targetVersion);
}

/// <summary>Builds the manifest of a release from its inputs, deterministically.</summary>
public static class UpgradeManifestBuilder
{
    public static UpgradeManifest Build(ReleaseNotes notes, ReleaseLedger source, MigrationLedger target, MigrationReviewRegister reviews)
    {
        var targetModules = UpgradeJson.ToLedger(target);
        var sourceIds = source.Modules.SelectMany(m => m.Migrations.Select(e => (m.Module, e.Id))).ToHashSet();
        var fresh = new List<NewMigration>();
        foreach (var module in targetModules)
            foreach (var migration in module.Migrations.Where(e => !sourceIds.Contains((module.Module, e.Id))))
            {
                var needs = migration.Categories.Any(MigrationCategories.NeedReview.Contains);
                var covered = reviews.Entries.Any(r => r.Module == module.Module && r.Migration == migration.Id && migration.Categories.Where(MigrationCategories.NeedReview.Contains).All(r.Categories.Contains));
                fresh.Add(new NewMigration(module.Module, migration.Id, migration.Checksum, migration.Categories, migration.Objects, needs, !needs || covered));
            }
        var manifest = new UpgradeManifest(1, notes.ReleaseId, source.PlatformVersion, notes.To, notes.MinimumSupportedSourceVersion, notes.SupportedSourceVersions.Order(StringComparer.Ordinal).ToList(),
            source.LedgerSha256, UpgradeJson.LedgerHash(targetModules), targetModules.Select(m => m.Module).ToList(), targetModules, fresh,
            new PackCompatibilityInfo(PackManifest.CurrentFormatVersion, [1], $"A pack is compatible when its platform range includes {notes.To}; checked for every installed pack by pre-flight."),
            notes.ConfigurationChanges, new UpgradeRequirements(true, notes.MaximumBackupAgeHours, true, true), notes.OperationalActions, notes.CompatibilityWarnings, notes.BreakingChanges,
            notes.PostUpgradeChecks, notes.Rollback, "");
        return manifest with { ManifestSha256 = UpgradeJson.ManifestHash(manifest) };
    }

    public static ReleaseLedger Baseline(string platformVersion, string note, MigrationLedger ledger)
    {
        var modules = UpgradeJson.ToLedger(ledger);
        return new ReleaseLedger(1, platformVersion, ledger.Generator, note, modules, UpgradeJson.LedgerHash(modules));
    }
}

/// <summary>Judges an upgrade manifest. Every problem is stable-coded; a critical problem means the manifest must not be used to upgrade anything.</summary>
public static class UpgradeManifestValidator
{
    public static IReadOnlyList<UpgradeProblem> Validate(UpgradeManifest m, UpgradeCatalogue catalogue, MigrationLedger? compiled)
    {
        var problems = new List<UpgradeProblem>();
        void Critical(string code, string message) => problems.Add(new(code, true, message));
        void Warn(string code, string message) => problems.Add(new(code, false, message));

        if (m.FormatVersion != 1) Critical("UPM-001", $"The manifest format version {m.FormatVersion} is not supported by this platform version.");
        if (!string.Equals(m.ManifestSha256, UpgradeJson.ManifestHash(m), StringComparison.Ordinal)) Critical("UPM-002", "The manifest's hash does not match its content: it has been changed since it was produced.");
        if (!SemVer.TryParse(m.SourcePlatformVersion, out var source) || !SemVer.TryParse(m.TargetPlatformVersion, out var target) || !SemVer.TryParse(m.MinimumSupportedSourceVersion, out var minimum))
        {
            Critical("UPM-003", "A version in the manifest is not a valid version number.");
            return problems;
        }
        if (target.CompareTo(source) <= 0) Critical("UPM-004", "The target version is not newer than the source version.");
        if (source.CompareTo(minimum) < 0) Critical("UPM-005", "The source version is below the minimum supported source version.");
        if (!m.SupportedSourceVersions.Contains(m.SourcePlatformVersion)) Critical("UPM-006", "The source version is not listed as a supported source.");
        if (!string.Equals(m.TargetLedgerSha256, UpgradeJson.LedgerHash(m.TargetModules), StringComparison.Ordinal)) Critical("UPM-007", "The target migration inventory does not match its recorded hash.");
        var baseline = catalogue.Baseline(m.SourcePlatformVersion);
        if (baseline is null) Critical("UPM-008", $"There is no frozen baseline for source version {m.SourcePlatformVersion}, so the source schema cannot be described.");
        else
        {
            if (!string.Equals(baseline.LedgerSha256, m.SourceLedgerSha256, StringComparison.Ordinal) || !string.Equals(baseline.LedgerSha256, UpgradeJson.LedgerHash(baseline.Modules), StringComparison.Ordinal))
                Critical("UPM-009", "The source baseline does not match the hash recorded in the manifest.");
            foreach (var module in baseline.Modules)
            {
                var now = m.TargetModules.FirstOrDefault(t => t.Module == module.Module);
                if (now is null) { Critical("UPM-010", $"Module {module.Module} existed in the source version but is missing from the target."); continue; }
                foreach (var old in module.Migrations)
                {
                    var same = now.Migrations.FirstOrDefault(e => e.Id == old.Id);
                    if (same is null) Critical("UPM-011", $"Migration {module.Module}/{old.Id} existed in the source version but is missing from the target (a historical migration was removed).");
                    else if (!string.Equals(same.Checksum, old.Checksum, StringComparison.Ordinal)) Critical("UPM-012", $"Migration {module.Module}/{old.Id} has changed since the source version was released (a historical migration was modified).");
                }
                if (!now.Migrations.Take(module.Migrations.Count).Select(e => e.Id).SequenceEqual(module.Migrations.Select(e => e.Id))) Critical("UPM-013", $"Module {module.Module}: the source migrations are not the first migrations of the target, in the same order.");
            }
        }
        foreach (var module in m.TargetModules)
        {
            var ids = module.Migrations.Select(e => e.Id).ToList();
            if (ids.Count != ids.Distinct(StringComparer.Ordinal).Count()) Critical("UPM-014", $"Module {module.Module} lists a migration twice.");
            if (!ids.SequenceEqual(ids.Order(StringComparer.Ordinal))) Critical("UPM-015", $"Module {module.Module}: migrations are not in identifier order.");
            if (module.SchemaVersion != (ids.LastOrDefault() ?? "")) Critical("UPM-016", $"Module {module.Module}: the declared schema version is not its last migration.");
        }
        var expectedOrder = m.TargetModules.OrderBy(x => x.Tier).ThenBy(x => x.Module, StringComparer.Ordinal).Select(x => x.Module).ToList();
        if (!m.ModuleOrder.SequenceEqual(expectedOrder)) Critical("UPM-017", "The module order is not the deterministic tier order.");
        foreach (var fresh in m.NewMigrations)
        {
            var module = m.TargetModules.FirstOrDefault(t => t.Module == fresh.Module);
            var entry = module?.Migrations.FirstOrDefault(e => e.Id == fresh.Id);
            if (entry is null || entry.Checksum != fresh.Checksum) Critical("UPM-018", $"New migration {fresh.Module}/{fresh.Id} is not in the target inventory with the same checksum.");
            if (fresh.ReviewRequired && !fresh.Reviewed) Critical("UPM-019", $"Migration {fresh.Module}/{fresh.Id} changes or removes existing structure or data ({string.Join(", ", fresh.Categories)}) and has no recorded review.");
        }
        if (baseline is not null)
        {
            var expectedNew = m.TargetModules.SelectMany(t => t.Migrations.Where(e => !baseline.Modules.Any(b => b.Module == t.Module && b.Migrations.Any(x => x.Id == e.Id))).Select(e => t.Module + "/" + e.Id)).Order(StringComparer.Ordinal).ToList();
            if (!expectedNew.SequenceEqual(m.NewMigrations.Select(n => n.Module + "/" + n.Id).Order(StringComparer.Ordinal))) Critical("UPM-020", "The list of new migrations is not the difference between the source baseline and the target inventory.");
        }
        if (!m.Requirements.VerifiedBackupRequired || !m.Requirements.MaintenanceRequired) Critical("UPM-021", "A manifest cannot waive the verified backup or maintenance mode.");
        if (m.Rollback.Method != "restore") Critical("UPM-022", "The only rollback method is restore of the verified pre-upgrade backup.");
        if (m.BreakingChanges.Count > 0) Warn("UPM-030", $"{m.BreakingChanges.Count} breaking change notice(s) must be read before upgrading.");
        var text = JsonSerializer.Serialize(m, UpgradeJson.Options);
        if (Redaction.ContainsSecret(text)) Critical("UPM-040", "The manifest appears to contain a secret.");
        if (compiled is not null)
        {
            var live = UpgradeJson.LedgerHash(UpgradeJson.ToLedger(compiled));
            if (!string.Equals(live, m.TargetLedgerSha256, StringComparison.Ordinal)) Critical("UPM-050", "This build's migrations are not the migrations the manifest describes: the build and its manifest do not belong together.");
        }
        return problems;
    }
}
