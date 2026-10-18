using System.Text;
using System.Text.Json;
using Ecp.Formats;
using Ecp.Operations;
using Ecp.SharedKernel;
using Microsoft.Extensions.Options;

namespace Ecp.Recovery;

/// <summary>What is and is not verified for each way of running the platform. Kept next to the code so the operator interface cannot claim more than deploy/STATUS.md does.</summary>
public sealed record DeploymentModelStatus(string Model, string Status, string Notes);

public static class DeploymentModels
{
    public static readonly IReadOnlyList<DeploymentModelStatus> All =
    [
        new("Container + external SQL Server + persistent document storage", "Verified", "Primary operational model. Container build and smoke test, and SQL Server backup, verification and restore, run in CI."),
        new("Container + SQL Server, documents on a shared persistent volume", "Verified", "The local file system document provider on a persistent volume, as above."),
        new("IIS on Windows Server", "Scaffolded / Not Verified", "Guidance exists; no executed evidence of backup or restore on this model."),
        new("Azure App Service with Azure SQL and Blob storage", "Scaffolded / Not Verified", "Azure SQL is not implemented as a backup provider (its backups are managed by the service); Blob storage has no document provider. Nothing here is verified."),
        new("SharePoint or Azure Blob document storage", "Not implemented", "No provider exists. A future provider must supply object enumeration or its own verified backup (IDocumentStoreInventory)."),
        new("SQLite", "Demonstration and tests only", "Not a supported production database. Backup and restore are refused."),
    ];
}

public sealed record EvidenceRun(string Kind, string Status, string Reference, string Summary, DateTime StartedUtc, DateTime? CompletedUtc, string OperatorId);
public sealed record EvidenceBackup(string BackupId, string State, DateTime? CreatedUtc, string? Outcome, long Bytes, int Warnings, string? ManifestSha256, DateTime? VerifiedUtc);
public sealed record EvidenceLease(string Name, string Scope, string OwnerDescription, DateTime AcquiredUtc, DateTime ExpiresUtc, bool Active, int Generation);

/// <summary>A sanitised statement of the installation's recovery posture (docs/BACKUP_AND_RESTORE.md, "Evidence"). It never holds secrets, connection strings, personal data, document content or contract content.</summary>
public sealed record EvidenceReport(DateTime GeneratedUtc, string PlatformVersion, string EnvironmentMode, bool BackupConfigured, bool EncryptionRequested, bool StorageProtectionStated, string MaintenanceMode,
    IReadOnlyList<EvidenceBackup> Backups, IReadOnlyList<EvidenceRun> Runs, IReadOnlyList<EvidenceLease> Leases, IReadOnlyList<DeploymentModelStatus> DeploymentModels, IReadOnlyList<string> Notices);

public interface IEvidenceReports
{
    Task<EvidenceReport> BuildAsync(CancellationToken ct = default);
    byte[] ToJson(EvidenceReport report);
    byte[] ToCsv(EvidenceReport report);
}

internal sealed class EvidenceReports(IBackupCatalogue catalogue, IOperationHistory history, IOperationLeases leases, IMaintenanceService maintenance, IOptions<RecoveryOptions> recovery, StartupFacts facts, IClock clock) : IEvidenceReports
{
    public async Task<EvidenceReport> BuildAsync(CancellationToken ct = default)
    {
        var backups = (await catalogue.ListAsync(ct)).Take(50).Select(b => new EvidenceBackup(b.BackupId, b.State.ToString(), b.CreatedUtc, b.Outcome, b.Bytes, b.Warnings, b.ManifestSha256, b.VerifiedUtc)).ToList();
        var runs = (await history.ListAsync(null, 50, ct)).Select(r => new EvidenceRun(r.Kind, r.Status, Clean(r.Reference), Clean(r.Summary), r.StartedUtc, r.CompletedUtc, Clean(r.OperatorId))).ToList();
        var held = (await leases.ListAsync(ct)).Select(l => new EvidenceLease(l.Name, l.ScopeKey, Clean(l.OwnerDescription), l.AcquiredUtc, l.ExpiresUtc, l.IsActive, l.Generation)).ToList();
        var m = await maintenance.GetAsync(ct);
        var o = recovery.Value;
        return new EvidenceReport(clock.UtcNow, typeof(EvidenceReports).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false).Cast<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "0.0.0",
            facts.Mode.ToString(), o.BackupConfigured, o.EncryptionRequested, !string.IsNullOrWhiteSpace(o.StorageProtection), m.Enabled ? $"On ({m.Cause})" : "Off", backups, runs, held, DeploymentModels.All,
            ["Tooling does not replace organisational backup governance: retention, frequency, RPO/RTO, rehearsal and sign-off remain the organisation's responsibility.", "This report is not an approval for production use."]);
    }

    private static string Clean(string s) => Redaction.Clean(s);

    public byte[] ToJson(EvidenceReport report) => EvidenceFormats.ToJson(report);
    public byte[] ToCsv(EvidenceReport report) => EvidenceFormats.ToCsv(report);
}

/// <summary>The two download formats of an evidence report. JSON is refused if it appears to hold a secret; CSV neutralises every cell a spreadsheet could read as a formula.</summary>
public static class EvidenceFormats
{
    public static byte[] ToJson(EvidenceReport report)
    {
        var text = JsonSerializer.Serialize(report, ManifestJson.Options) + "\n";
        if (Redaction.ContainsSecret(text)) throw new DomainException("secret_in_report", "The evidence report was not produced because it appears to contain a secret.");
        return Encoding.UTF8.GetBytes(text);
    }

    public static byte[] ToCsv(EvidenceReport report)
    {
        var rows = new List<IReadOnlyList<object?>> { new object?[] { "Section", "Item", "Status", "Detail", "Utc" } };
        rows.Add(["Installation", "Platform version", report.PlatformVersion, report.EnvironmentMode, report.GeneratedUtc.ToString("O")]);
        rows.Add(["Installation", "Maintenance mode", report.MaintenanceMode, "", ""]);
        rows.Add(["Installation", "Backup configured", report.BackupConfigured ? "Yes" : "No", report.StorageProtectionStated ? "Storage protection stated" : "No storage protection statement", ""]);
        foreach (var b in report.Backups) rows.Add(["Backup", b.BackupId, b.State, $"{b.Outcome}; {b.Bytes} bytes; {b.Warnings} warning(s); manifest {b.ManifestSha256}", b.CreatedUtc?.ToString("O")]);
        foreach (var r in report.Runs) rows.Add([r.Kind, r.Reference, r.Status, r.Summary, r.StartedUtc.ToString("O")]);
        foreach (var l in report.Leases) rows.Add(["Lease", $"{l.Name}/{l.Scope}", l.Active ? "Active" : "Inactive", l.OwnerDescription, l.AcquiredUtc.ToString("O")]);
        foreach (var d in report.DeploymentModels) rows.Add(["Deployment model", d.Model, d.Status, d.Notes, ""]);
        foreach (var n in report.Notices) rows.Add(["Notice", "", "", n, ""]);
        return CsvWriter.Write(rows);      // formula-looking cells are neutralised
    }
}

/// <summary>Writes the evidence file of one restore next to the backups (not inside the database, which a restore may replace).</summary>
internal sealed class EvidenceWriter(IOptions<RecoveryOptions> recovery)
{
    public async Task<string?> WriteRestoreAsync(RestoreRequest request, RestorePreview preview, ValidationReport? report, string outcome, string? failure, DateTime startedUtc, DateTime completedUtc, string correlationId, CancellationToken ct)
    {
        try
        {
            var directory = Path.Combine(Path.GetFullPath(recovery.Value.BackupDirectory), "_evidence");
            Directory.CreateDirectory(directory);
            var name = $"restore-{startedUtc:yyyyMMddTHHmmss}Z-{request.BackupId}.json";
            var document = new
            {
                kind = "restore-evidence", backupId = request.BackupId, manifestSha256 = preview.Verification.ManifestSha256, target = preview.TargetDatabase, inPlaceDisasterRecovery = request.InPlaceDisasterRecovery,
                @operator = Redaction.Clean(request.Operator.Id), reason = Redaction.Clean(request.Reason), startedUtc, completedUtc, outcome, failure = Redaction.Clean(failure), correlationId,
                validation = report is null ? null : new
                {
                    outcome = report.Outcome.ToString(), report.StartedUtc, report.CompletedUtc,
                    checks = report.Checks.Select(c => new { c.Code, c.Title, outcome = c.Outcome.ToString(), severity = c.Severity.ToString(), c.Description, detail = Redaction.Clean(c.TechnicalDetail), c.Remediation }),
                },
                notice = "This record is evidence of what ran. It is not an approval for production use."
            };
            var text = JsonSerializer.Serialize(document, ManifestJson.Options) + "\n";
            if (Redaction.ContainsSecret(text)) return null;
            await File.WriteAllTextAsync(Path.Combine(directory, name), text, ct);
            return "_evidence/" + name;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}
