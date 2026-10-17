using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ecp.Audit;
using Ecp.Configuration.Domain;
using Ecp.Configuration.Persistence;
using Ecp.Identity;
using Ecp.Identity.Access;
using Ecp.Packs;
using Ecp.Persistence;
using Ecp.SharedKernel;
using Ecp.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Ecp.Configuration;

public sealed record PackUploadResult(Guid Token, PackValidationReport Report);
public sealed record OrganisationUse(Guid OrganisationId, string Name, string? PackKey, string? Version);
public sealed record PackListItem(PackCatalogueEntry Entry, int OrganisationsUsing);
public sealed record PackDetails(PackCatalogueEntry Entry, IReadOnlyList<OrganisationUse> Organisations, IReadOnlyList<PackCatalogueEntry> OtherVersions);
public sealed record PackInstallPreview(
    PackValidationReport Report, IReadOnlyList<PackContentSummary> Kinds, IReadOnlyList<OrganisationUse> Organisations, IReadOnlyList<string> CapabilityChanges, IReadOnlyList<string> MissingDependencies,
    IReadOnlyList<string> NextSteps, string Statement);
public sealed record PackInstallResult(PackCatalogueEntry? Entry, bool AlreadyAvailable, IReadOnlyList<string> NextSteps);
public sealed record PackAuditItem(DateTime OccurredUtc, string Action, string? Actor, string Outcome, string Subject, string? Reason, string? Details);

/// <summary>
/// Upload, validate, preview, install, retire and read solution packs. Every method authorises first with the pack permissions; an upload is held in quarantine, belongs to the person who made it, and nothing is stored
/// as an installed pack, or becomes available, until that person confirms an install that is verified again. Installing stores the package and makes it available to assign: it does not assign it to any organisation
/// and changes no configuration.
/// </summary>
public interface IPackManager
{
    Task<PackUploadResult> UploadAsync(Stream content, string? fileName, CancellationToken ct = default);
    Task<PackValidationReport?> GetUploadReportAsync(Guid token, CancellationToken ct = default);
    Task<PackInstallPreview?> PreviewAsync(Guid token, CancellationToken ct = default);
    Task<PackInstallResult> InstallAsync(Guid token, CancellationToken ct = default);
    Task CancelUploadAsync(Guid token, CancellationToken ct = default);
    Task<byte[]?> ReportCsvAsync(Guid token, CancellationToken ct = default);
    Task<IReadOnlyList<PackListItem>> ListAsync(CancellationToken ct = default);
    Task<PackDetails?> GetAsync(string packKey, string version, CancellationToken ct = default);
    Task RetireAsync(string packKey, string version, string? reason, CancellationToken ct = default);
    Task ReinstateAsync(string packKey, string version, CancellationToken ct = default);
    Task<PagedResult<PackAuditItem>> AuditAsync(int page = 1, CancellationToken ct = default);
}

internal sealed class PackManager(
    PackValidationService validation, IPackLoader loader, IPackageStorage storage, ConfigurationDbContext db, IUnitOfWork uow, IAuditWriter audit, ITenantContext tenant,
    IAccessService access, CurrentUser currentUser, ActorContext actor, IClock clock, IOrganisationStructureService organisations, IOptions<PackOptions> options, IAuditEventSource auditEvents) : IPackManager
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() }, WriteIndented = false };
    private const int AuditPageSize = 50;

    private sealed record QuarantineRecord(Guid Token, string UploadedBy, DateTime UploadedUtc, string FileName, PackValidationReport Report);

    internal static ResourceDescriptor TenantResource(Guid tenantId) => new("SolutionPack", tenantId, IsTenantLevelDefinition: true, Dimensions: ScopeDimensions.None);
    private string Who() => currentUser.UserId?.ToString() ?? actor.ActorId ?? "system";

    public async Task<PackUploadResult> UploadAsync(Stream content, string? fileName, CancellationToken ct = default)
    {
        var tenantId = tenant.RequireTenantId();
        await access.RequireAsync("packs.upload", TenantResource(tenantId), ct);
        await storage.PurgeQuarantineAsync(tenantId, options.Value.QuarantineLifetime, ct);

        var (bytes, tooLarge) = await ReadBoundedAsync(content, options.Value.MaxUploadBytes, ct);
        var report = tooLarge
            ? new PackValidationReport(PackValidationOutcome.Invalid, [new PackIssue(PackCodes.TooLarge, PackSeverity.Error, $"The file is larger than the {options.Value.MaxUploadBytes / (1024 * 1024)} MB limit for a package.", null, "Check that this is a solution pack (.ecpkg).")], null, null)
            : await validation.ValidateAsync(bytes, ct);

        var token = Guid.NewGuid();
        var record = new QuarantineRecord(token, Who(), clock.UtcNow, SafeFileName(fileName), report);
        await storage.SaveQuarantineAsync(tenantId, token, ".report.json", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(record, Json)), ct);
        if (report.CanInstall || report.Outcome == PackValidationOutcome.Duplicate) await storage.SaveQuarantineAsync(tenantId, token, ".ecpkg", bytes, ct);

        await uow.ExecuteAsync(c =>
        {
            audit.Record(new AuditEntry(report.CanInstall || report.Outcome == PackValidationOutcome.Duplicate ? "pack.upload.verified" : "pack.upload.rejected", "SolutionPack", report.Summary?.PackKey ?? token.ToString("N"),
                report.CanInstall || report.Outcome == PackValidationOutcome.Duplicate ? AuditOutcome.Success : AuditOutcome.Failed, Reason: report.Outcome.ToString(), NewValuesJson: Details(report)));
            return Task.CompletedTask;
        }, ct);
        return new PackUploadResult(token, report);
    }

    private static string Details(PackValidationReport report) => JsonSerializer.Serialize(new
    {
        outcome = report.Outcome.ToString(), sha256 = report.PackageSha256, packKey = report.Summary?.PackKey, version = report.Summary?.Version, publisher = report.Summary?.PublisherId,
        signature = report.Summary?.SignatureStatus, keyId = report.Summary?.SignatureKeyId, errors = report.Errors.Select(i => i.Code).Distinct().ToArray(), warnings = report.Warnings.Select(i => i.Code).Distinct().ToArray()
    });

    private static string SafeFileName(string? name)
    {
        var n = Path.GetFileName(name ?? "") ;
        n = new string(n.Where(ch => !char.IsControl(ch)).ToArray());
        return n.Length > 100 ? n[..100] : n;
    }

    private static async Task<(byte[] Bytes, bool TooLarge)> ReadBoundedAsync(Stream stream, long max, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int n;
        while ((n = await stream.ReadAsync(buffer, ct)) > 0)
        {
            ms.Write(buffer, 0, n);
            if (ms.Length > max) return ([], true);
        }
        return (ms.ToArray(), false);
    }

    private async Task<QuarantineRecord?> LoadRecordAsync(Guid token, CancellationToken ct)
    {
        var tenantId = tenant.RequireTenantId();
        var bytes = await storage.ReadQuarantineAsync(tenantId, token, ".report.json", ct);
        if (bytes is null) return null;
        var record = JsonSerializer.Deserialize<QuarantineRecord>(bytes, Json);
        // An upload belongs to the person who made it, and expires.
        if (record is null || record.UploadedBy != Who() || clock.UtcNow - record.UploadedUtc > options.Value.QuarantineLifetime) return null;
        return record;
    }

    public async Task<PackValidationReport?> GetUploadReportAsync(Guid token, CancellationToken ct = default)
    {
        await access.RequireAsync("packs.read", TenantResource(tenant.RequireTenantId()), ct);
        return (await LoadRecordAsync(token, ct))?.Report;
    }

    public async Task<byte[]?> ReportCsvAsync(Guid token, CancellationToken ct = default)
    {
        var report = await GetUploadReportAsync(token, ct);
        return report is null ? null : PackReportCsv.Build(report);
    }

    public async Task<PackInstallPreview?> PreviewAsync(Guid token, CancellationToken ct = default)
    {
        var tenantId = tenant.RequireTenantId();
        await access.RequireAsync("packs.validate", TenantResource(tenantId), ct);
        var record = await LoadRecordAsync(token, ct);
        if (record is null) return null;
        var report = record.Report;
        var orgs = await OrganisationsAsync(ct);
        var summary = report.Summary;
        var capabilityChanges = new List<string>();
        var missing = report.Issues.Where(i => i.Code == PackCodes.DependencyMissing).Select(i => i.Message).ToList();
        if (summary is not null)
        {
            var previous = (await loader.ListAsync(ct)).Where(e => e.PackKey == summary.PackKey && e.Version != summary.Version).OrderByDescending(e => SemVer.TryParse(e.Version, out var v) ? v : new SemVer(0, 0, 0, null)).FirstOrDefault();
            if (previous is null) capabilityChanges.AddRange(summary.Capabilities.Select(c => $"Adds the capability '{c}'."));
            else
            {
                capabilityChanges.AddRange(summary.Capabilities.Except(previous.Capabilities).Select(c => $"Adds the capability '{c}' (not in version {previous.Version})."));
                capabilityChanges.AddRange(previous.Capabilities.Except(summary.Capabilities).Select(c => $"No longer declares the capability '{c}' (declared by version {previous.Version})."));
            }
            if (capabilityChanges.Count == 0) capabilityChanges.Add("No change in declared capabilities.");
        }
        var next = new List<string>
        {
            "Install stores the verified package so it can be chosen. It does not assign the pack to any organisation and does not change any configuration.",
            "To use it, assign it to an organisation (Administration, Solution packs, Organisation packs). Assigning creates configuration drafts; nothing is published.",
            "Review each draft, validate it, and publish it through Configuration. Publishing follows your normal rules, including second-person approval where it is required."
        };
        return new PackInstallPreview(report, summary?.Content ?? [], orgs, capabilityChanges, missing, next,
            "Installing this pack will not publish any configuration, will not change any organisation's existing configuration, and will not change any contract.");
    }

    public async Task<PackInstallResult> InstallAsync(Guid token, CancellationToken ct = default)
    {
        var tenantId = tenant.RequireTenantId();
        await access.RequireAsync("packs.install", TenantResource(tenantId), ct);
        var record = await LoadRecordAsync(token, ct) ?? throw new DomainException("upload_not_found", "That upload was not found or has expired. Upload the package again.");
        var bytes = await storage.ReadQuarantineAsync(tenantId, token, ".ecpkg", ct) ?? throw new DomainException("pack_not_installable", "That upload cannot be installed: it did not pass validation.");

        // Verify again from the quarantined bytes: what was shown is not trusted to still be true.
        var report = await validation.ValidateAsync(bytes, ct);
        if (report.Outcome == PackValidationOutcome.Duplicate)
        {
            var sha = report.PackageSha256!;
            var existing = await db.InstalledPacks.AsNoTracking().FirstOrDefaultAsync(p => p.PackageSha256 == sha, ct);
            var entry = existing is null ? null : await loader.FindAsync(existing.PackKey, existing.Version, usableOnly: false, ct);
            await storage.DeleteQuarantineAsync(tenantId, token, ct);
            return new PackInstallResult(entry, true, ["This exact package is already available. Assign it to an organisation to use it."]);
        }
        if (!report.CanInstall) throw new DomainException("pack_not_installable", $"The package cannot be installed ({report.Outcome}). Review the validation results.");

        var opened = PackReader.Open(bytes, options.Value.Limits);
        var m = opened.Manifest!;
        var trust = await TrustAsync(opened, ct);
        var reference = await storage.SaveAsync(tenantId, opened.Sha256, bytes, ct);
        var correlation = actor.CorrelationId;
        InstalledPack? row = null;
        try
        {
            await uow.ExecuteAsync(async c =>
            {
                row = InstalledPack.Create(tenantId, m.PackKey, m.Version, m.PackageId, m.Publisher.Id, m.Publisher.Name, m.DisplayName.Resolve("en"), m.Synthetic, opened.Sha256, opened.ContentDigest,
                    Encoding.UTF8.GetString(opened.CanonicalManifest), trust.Status.ToString(), trust.Algorithm, trust.KeyId, trust.IsTrusted ? "Verified" : "UnsignedSynthetic", reference, report.Outcome.ToString(),
                    JsonSerializer.Serialize(report.Issues.Where(i => i.Severity != PackSeverity.Info), Json), "[]", record.UploadedBy, record.UploadedUtc, Who(), clock.UtcNow, correlation);
                db.InstalledPacks.Add(row);
                await db.SaveChangesAsync(c);
                audit.Record(new AuditEntry("pack.installed", "SolutionPack", $"{m.PackKey}@{m.Version}", Reason: report.Outcome.ToString(), NewValuesJson: Details(report)));
            }, ct);
        }
        catch (DbUpdateException ex) when (DbExceptions.IsUniqueViolation(ex))
        {
            // A concurrent install of the same pack won. The same bytes make this call a harmless repeat; different bytes are a conflict.
            db.ChangeTracker.Clear();
            var existing = await db.InstalledPacks.AsNoTracking().FirstOrDefaultAsync(p => p.PackKey == m.PackKey && p.Version == m.Version, ct);
            if (existing is null || existing.PackageSha256 != opened.Sha256)
                throw new DomainException("pack_version_exists", "A different package with this pack key and version is already installed. Pack versions never change; publish the changes under a new version.");
        }
        await storage.DeleteQuarantineAsync(tenantId, token, ct);
        var installed = await loader.FindAsync(m.PackKey, m.Version, usableOnly: false, ct);
        return new PackInstallResult(installed, false,
            ["The pack is installed and available to assign.", "Assign it to an organisation to create configuration drafts from it. Nothing has been published or changed."]);
    }

    private Task<TrustResult> TrustAsync(OpenedPackage opened, CancellationToken ct) => validation.TrustForAsync(opened, ct);

    public async Task CancelUploadAsync(Guid token, CancellationToken ct = default)
    {
        var tenantId = tenant.RequireTenantId();
        await access.RequireAsync("packs.upload", TenantResource(tenantId), ct);
        if (await LoadRecordAsync(token, ct) is not null) await storage.DeleteQuarantineAsync(tenantId, token, ct);
    }

    public async Task<IReadOnlyList<PackListItem>> ListAsync(CancellationToken ct = default)
    {
        await access.RequireAsync("packs.read", TenantResource(tenant.RequireTenantId()), ct);
        var entries = await loader.ListAsync(ct);
        var orgs = await organisations.ListOrganisationsAsync(ct);
        return entries.OrderBy(e => e.PackKey, StringComparer.Ordinal).ThenByDescending(e => SemVer.TryParse(e.Version, out var v) ? v : new SemVer(0, 0, 0, null))
            .Select(e => new PackListItem(e, orgs.Count(o => o.SolutionPackKey == e.PackKey))).ToList();
    }

    public async Task<PackDetails?> GetAsync(string packKey, string version, CancellationToken ct = default)
    {
        await access.RequireAsync("packs.read", TenantResource(tenant.RequireTenantId()), ct);
        var entry = await loader.FindAsync(packKey, version, usableOnly: false, ct);
        if (entry is null) return null;
        var others = (await loader.ListAsync(ct)).Where(e => e.PackKey == packKey && e.Version != version).ToList();
        return new PackDetails(entry, (await OrganisationsAsync(ct)).Where(o => o.PackKey == packKey).ToList(), others);
    }

    private async Task<IReadOnlyList<OrganisationUse>> OrganisationsAsync(CancellationToken ct)
    {
        var orgs = await organisations.ListOrganisationsAsync(ct);
        var active = await db.PackAssignments.AsNoTracking().Where(a => a.Status == PackAssignment.Active).ToListAsync(ct);
        return orgs.Select(o => new OrganisationUse(o.Id, o.Name, o.SolutionPackKey, active.FirstOrDefault(a => a.OrganisationId == o.Id && a.PackKey == o.SolutionPackKey)?.Version)).ToList();
    }

    public async Task RetireAsync(string packKey, string version, string? reason, CancellationToken ct = default)
    {
        var tenantId = tenant.RequireTenantId();
        await access.RequireAsync("packs.retire", TenantResource(tenantId), ct);
        var row = await db.InstalledPacks.FirstOrDefaultAsync(p => p.PackKey == packKey && p.Version == version, ct);
        if (row is null)
        {
            if ((await loader.FindAsync(packKey, version, usableOnly: false, ct))?.IsShipped == true) throw new DomainException("shipped_pack_read_only", "A pack that ships with the application is read-only and cannot be retired here.");
            throw new ResourceNotFoundException();
        }
        await uow.ExecuteAsync(async c =>
        {
            row.Retire(Who(), reason, clock.UtcNow);
            await db.SaveChangesAsync(c);
            audit.Record(new AuditEntry("pack.retired", "SolutionPack", $"{packKey}@{version}", Reason: reason));
        }, ct);
    }

    public async Task ReinstateAsync(string packKey, string version, CancellationToken ct = default)
    {
        await access.RequireAsync("packs.retire", TenantResource(tenant.RequireTenantId()), ct);
        var row = await db.InstalledPacks.FirstOrDefaultAsync(p => p.PackKey == packKey && p.Version == version, ct) ?? throw new ResourceNotFoundException();
        await uow.ExecuteAsync(async c =>
        {
            row.Reinstate();
            await db.SaveChangesAsync(c);
            audit.Record(new AuditEntry("pack.reinstated", "SolutionPack", $"{packKey}@{version}"));
        }, ct);
    }

    public async Task<PagedResult<PackAuditItem>> AuditAsync(int page = 1, CancellationToken ct = default)
    {
        await access.RequireAsync("packs.audit.read", TenantResource(tenant.RequireTenantId()), ct);
        var query = auditEvents.Events.Where(e => e.Action.StartsWith("pack.") || e.Action == "organisation.solution_pack.set");
        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(e => e.Sequence).Skip((Math.Max(1, page) - 1) * AuditPageSize).Take(AuditPageSize).ToListAsync(ct);
        return new PagedResult<PackAuditItem>(rows.Select(e => new PackAuditItem(e.OccurredUtc, e.Action, e.ActorId, e.Outcome, e.EntityId, e.Reason, e.NewValuesJson)).ToList(), total, Math.Max(1, page), AuditPageSize);
    }
}

/// <summary>The validation report as a CSV a person can open in a spreadsheet. A cell that begins like a formula is defused, so a hostile package cannot turn its own messages into one.</summary>
public static class PackReportCsv
{
    public static byte[] Build(PackValidationReport report)
    {
        var sb = new StringBuilder();
        sb.Append("﻿");
        sb.AppendLine("Severity,Code,Entry,Message,Suggested fix");
        sb.AppendLine(string.Join(",", Cell("Outcome"), Cell(report.Outcome.ToString()), Cell(report.Summary?.PackKey), Cell(report.Summary?.Version), Cell(report.PackageSha256)));
        foreach (var i in report.Issues)
            sb.AppendLine(string.Join(",", Cell(i.Severity.ToString()), Cell(i.Code), Cell(i.Entry), Cell(i.Message), Cell(i.Remediation)));
        return new UTF8Encoding(false).GetBytes(sb.ToString());
    }

    public static string Cell(string? value)
    {
        value ??= "";
        value = new string(value.Where(c => !char.IsControl(c) || c == '\t').ToArray());
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r') value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
