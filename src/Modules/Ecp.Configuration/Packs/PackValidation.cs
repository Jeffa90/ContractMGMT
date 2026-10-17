using System.Text.Json;
using Ecp.Configuration.Domain;
using Ecp.Configuration.Persistence;
using Ecp.Packs;
using Ecp.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Ecp.Configuration;

public enum PackValidationOutcome { Valid, ValidWithWarnings, Invalid, Unsupported, Untrusted, Duplicate, Conflict }

/// <summary>What an administrator may be shown about a package: nothing from the server, no key material, no signature bytes.</summary>
public sealed record PackSummary(
    string PackKey, string Version, string PackageId, string DisplayName, string Description, string PublisherId, string PublisherName, bool Synthetic, IReadOnlyList<string> Capabilities,
    IReadOnlyList<PackContentSummary> Content, IReadOnlyList<string> Dependencies, string SignatureStatus, string? SignatureKeyId, string? SignatureAlgorithm, string? SignatureKeySource,
    bool ProductionEligible, string PackageSha256, string ContentDigest, DateTime CreatedUtc, string MinPlatformVersion, string? MaxPlatformVersion);

public sealed record PackValidationReport(PackValidationOutcome Outcome, IReadOnlyList<PackIssue> Issues, PackSummary? Summary, string? PackageSha256)
{
    /// <summary>A new package that may be installed after explicit confirmation.</summary>
    public bool CanInstall => Outcome is PackValidationOutcome.Valid or PackValidationOutcome.ValidWithWarnings;
    public IEnumerable<PackIssue> Errors => Issues.Where(i => i.IsError);
    public IEnumerable<PackIssue> Warnings => Issues.Where(i => i.Severity == PackSeverity.Warning);
}

/// <summary>
/// Dry-run validation of a package before anything is stored or installed: archive safety, manifest, hashes, compatibility, signature and trust, the pack's own documents through the owning modules'
/// validators, dependencies, and duplicates. It writes nothing. The configuration validators stay the authority on what a document may say.
/// </summary>
internal sealed class PackValidationService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    /// <summary>Validator messages that depend on the organisation the document is applied to. Without an organisation they are warnings, and become errors when the draft is validated for one.</summary>
    private static readonly HashSet<string> ContextDependent = new(StringComparer.Ordinal) { "matrix_missing" };

    private readonly PackKindRegistry _kinds;
    private readonly PackTrustEvaluator _trust;
    private readonly ShippedPackSource _shipped;
    private readonly ConfigurationDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly IOptions<PackOptions> _options;
    private readonly IPackLoader _loader;

    public PackValidationService(PackKindRegistry kinds, PackTrustEvaluator trust, ShippedPackSource shipped, ConfigurationDbContext db, ITenantContext tenant, IOptions<PackOptions> options, IPackLoader loader)
    { _kinds = kinds; _trust = trust; _shipped = shipped; _db = db; _tenant = tenant; _options = options; _loader = loader; }

    public async Task<PackValidationReport> ValidateAsync(byte[] bytes, CancellationToken ct = default)
    {
        var opened = PackReader.Open(bytes, _options.Value.Limits);
        var issues = new List<PackIssue>(opened.Issues.Where(i => i.Severity != PackSeverity.Info || i.Code == PackCodes.ManifestNotCanonical));
        if (!opened.IsStructurallyValid)
        {
            var unsupported = issues.Any(i => i.Code == PackCodes.FormatUnsupported);
            return new PackValidationReport(unsupported ? PackValidationOutcome.Unsupported : PackValidationOutcome.Invalid, issues, null, opened.Sha256);
        }
        var manifest = opened.Manifest!;

        // Compatibility.
        var compatibility = PackReader.CheckCompatibility(opened, new PackCompatibilityContext(PlatformVersion.Current, _kinds.Known));
        issues.AddRange(compatibility);
        if (SemVer.Parse(manifest.Version).PreRelease is not null && _options.Value.IsProduction)
            issues.Add(new PackIssue(PackCodes.VersionRule, PackSeverity.Error, "A pre-release version cannot be installed in Production.", PackPaths.Manifest, "Use a released version of the pack."));

        // Signature and trust.
        var trust = await _trust.EvaluateAsync(opened, ct);
        issues.AddRange(trust.Issues);

        // The pack's own catalogue entry.
        issues.AddRange(CheckSolutionPackDocument(opened));
        issues.AddRange(PackBuilder.CrossCheckSolutionPack(opened, manifest));

        // Each document through the module that owns its kind.
        if (!issues.Any(i => i.IsError && i.Code is PackCodes.KindUnknownRequired or PackCodes.SchemaUnsupported))
            foreach (var entry in manifest.Content)
            {
                if (entry.Kind == PackReader.SolutionPackKind || !_kinds.Known.Contains(entry.Kind)) continue;
                issues.AddRange(await ValidateDocumentAsync(entry, opened.Document(entry.Kind)!, ct));
            }

        // Dependencies.
        foreach (var d in manifest.Dependencies)
        {
            var found = await _loader.FindAsync(d.PackKey, null, usableOnly: true, ct);
            if (found is null || !SatisfiesRange(found.Version, d))
                issues.Add(new PackIssue(PackCodes.DependencyMissing, PackSeverity.Error, $"The package needs the pack '{d.PackKey}'{(d.MinVersion is null ? "" : $" version {d.MinVersion} or later")}, which is not installed here.", PackPaths.Manifest, "Install the required pack first."));
        }

        // Duplicates and reserved keys.
        var duplicate = false; var conflict = false;
        foreach (var shipped in _shipped.Packs.Where(p => p.Package.Manifest!.PackKey == manifest.PackKey))
        {
            if (shipped.Package.Manifest!.Version == manifest.Version)
            {
                if (shipped.Package.Sha256 == opened.Sha256) { duplicate = true; issues.Add(new PackIssue(PackCodes.DuplicateIdentical, PackSeverity.Info, "This exact package already ships with the application.", null)); }
                else { conflict = true; issues.Add(new PackIssue(PackCodes.DuplicateDifferent, PackSeverity.Error, "A pack with this key and version already ships with the application, and this package is different. Pack versions never change.", null, "Publish the changed pack under a new version number.")); }
            }
            else if (shipped.Package.Manifest.Publisher.Id != manifest.Publisher.Id)
            {
                conflict = true; issues.Add(new PackIssue(PackCodes.Conflict, PackSeverity.Error, $"The pack key '{manifest.PackKey}' belongs to a pack that ships with the application, from a different publisher.", null, "Use your own pack key."));
            }
        }
        if (_tenant.TenantId is not null)
        {
            var existing = await _db.InstalledPacks.AsNoTracking().Where(p => p.PackKey == manifest.PackKey).ToListAsync(ct);
            var same = existing.FirstOrDefault(p => p.Version == manifest.Version);
            if (same is not null)
            {
                if (same.PackageSha256 == opened.Sha256) { duplicate = true; issues.Add(new PackIssue(PackCodes.DuplicateIdentical, PackSeverity.Info, "This exact package is already installed.", null)); }
                else { conflict = true; issues.Add(new PackIssue(PackCodes.DuplicateDifferent, PackSeverity.Error, "A package with this key and version is already installed, and this one is different. Pack versions never change.", null, "Publish the changed pack under a new version number.")); }
            }
            if (existing.Any(p => p.PackageId != manifest.PackageId))
            { conflict = true; issues.Add(new PackIssue(PackCodes.Conflict, PackSeverity.Error, $"The pack key '{manifest.PackKey}' is already used by a different package identity.", null, "Use your own pack key.")); }
        }

        var summary = Summarise(opened, trust);
        var outcome = Classify(issues, trust, duplicate, conflict);
        return new PackValidationReport(outcome, issues, summary, opened.Sha256);
    }

    private static PackValidationOutcome Classify(List<PackIssue> issues, TrustResult trust, bool duplicate, bool conflict)
    {
        var errors = issues.Where(i => i.IsError).ToList();
        if (errors.Any(e => e.Code.StartsWith("PACK-A", StringComparison.Ordinal) || e.Code.StartsWith("PACK-M", StringComparison.Ordinal) && e.Code != PackCodes.FormatUnsupported
            || e.Code is PackCodes.HashMismatch or PackCodes.ContentMissing or PackCodes.ContentInvalid or PackCodes.ContentUnsafe or PackCodes.ContentMismatch)) return PackValidationOutcome.Invalid;
        if (errors.Any(e => e.Code is PackCodes.FormatUnsupported or PackCodes.PlatformTooOld or PackCodes.PlatformTooNew or PackCodes.SchemaUnsupported or PackCodes.KindUnknownRequired or PackCodes.KindUnknownOptional or PackCodes.VersionRule)) return PackValidationOutcome.Unsupported;
        if (!trust.IsAcceptable) return PackValidationOutcome.Untrusted;
        if (conflict || errors.Any(e => e.Code is PackCodes.DependencyMissing or PackCodes.Conflict or PackCodes.DuplicateDifferent)) return PackValidationOutcome.Conflict;
        if (errors.Count > 0) return PackValidationOutcome.Invalid;
        if (duplicate) return PackValidationOutcome.Duplicate;
        return issues.Any(i => i.Severity == PackSeverity.Warning) ? PackValidationOutcome.ValidWithWarnings : PackValidationOutcome.Valid;
    }

    private static bool SatisfiesRange(string version, PackDependency d)
        => SemVer.TryParse(version, out var v) && (d.MinVersion is null || v >= SemVer.Parse(d.MinVersion)) && (d.MaxVersion is null || v <= SemVer.Parse(d.MaxVersion));

    private IEnumerable<PackIssue> CheckSolutionPackDocument(OpenedPackage opened)
    {
        var json = opened.Document(PackReader.SolutionPackKind);
        if (json is null) yield break;                // CrossCheckSolutionPack reports the absence
        SolutionPackInfo? info = null; string? problem = null;
        try { info = JsonSerializer.Deserialize<SolutionPackInfo>(json, Json); }
        catch (JsonException) { problem = "The pack's catalogue entry is not valid."; }
        var classification = info?.Defaults?.Classification;
        if (problem is null && (info is null || classification is null || string.IsNullOrWhiteSpace(info.Name))) problem = "The pack's catalogue entry is incomplete (it needs a name and default classification levels).";
        if (problem is not null) yield return new PackIssue(PackCodes.ContentInvalid, PackSeverity.Error, problem, "content/solution-pack.json", "Correct the SolutionPack document in the pack source.");
        else if (classification!.Levels.Count == 0 || !classification.Levels.Contains(classification.DefaultLevel))
            yield return new PackIssue(PackCodes.ContentInvalid, PackSeverity.Error, "The pack's default classification level is not one of its levels.", "content/solution-pack.json", "Correct the classification defaults.");
    }

    private async Task<IEnumerable<PackIssue>> ValidateDocumentAsync(PackContentEntry entry, string json, CancellationToken ct)
    {
        var found = new List<PackIssue>();
        var kind = _kinds.Find(entry.Kind);
        if (kind is null) return found;
        try
        {
            var report = await kind.ValidateAsync(Guid.Empty, json, ct);
            foreach (var m in report.Messages)
            {
                var error = m.Severity == ValidationSeverity.Error && !ContextDependent.Contains(m.Code);
                found.Add(new PackIssue(error ? PackCodes.ContentInvalid : PackCodes.ContentWarning, error ? PackSeverity.Error : PackSeverity.Warning,
                    $"{entry.Kind}: {m.Path}: {m.Message}", entry.Path, error ? "Correct the document in the pack source (the same rule applies in the Configuration Studio)." : null));
            }
        }
        catch (Exception ex) when (ex is DomainException or JsonException or InvalidOperationException or ForbiddenException)
        {
            found.Add(new PackIssue(PackCodes.ContentInvalid, PackSeverity.Error, $"{entry.Kind} could not be checked: {(ex is DomainException ? ex.Message : "the document is not in a form this platform can read.")}", entry.Path, "Correct the document in the pack source."));
        }
        return found;
    }

    internal Task<TrustResult> TrustForAsync(OpenedPackage opened, CancellationToken ct) => _trust.EvaluateAsync(opened, ct);

    internal PackSummary Summarise(OpenedPackage opened, TrustResult trust)
    {
        var m = opened.Manifest!;
        return new PackSummary(m.PackKey, m.Version, m.PackageId, m.DisplayName.Resolve("en"), m.Description.Resolve("en"), m.Publisher.Id, m.Publisher.Name, m.Synthetic, m.Capabilities,
            m.Content.Select(c => new PackContentSummary(c.Kind, c.Scope, c.Required, c.SchemaVersion, c.Path)).ToList(), m.Dependencies.Select(d => d.PackKey + (d.MinVersion is null ? "" : " >= " + d.MinVersion)).ToList(),
            trust.Status.ToString(), trust.KeyId, trust.Algorithm, trust.KeySource, trust.IsTrusted, opened.Sha256, opened.ContentDigest, m.CreatedUtc, m.Compatibility.MinPlatformVersion, m.Compatibility.MaxPlatformVersion);
    }
}
