using System.Text;
using System.Text.Json;
using Ecp.Formats;
using Ecp.SharedKernel;
using Microsoft.Extensions.Options;

namespace Ecp.Recovery;

/// <summary>
/// The evidence of an upgrade, a rehearsal or a rollback (docs/UPGRADES.md, "Evidence"). Sanitised by construction: versions, hashes, counts, check codes and plain-language findings; never a secret, a connection string, a
/// personal name, record content or document content. JSON is refused if a scan finds a secret; CSV neutralises every cell a spreadsheet could read as a formula.
/// </summary>
public static class UpgradeEvidence
{
    public const string Notice = "This record is evidence of what ran. It is not an approval for production use. Rolling back by restore discards everything created after the backup it restores.";

    public static object Document(UpgradeResult r, UpgradeRequest request, PreflightReport? preflight) => new
    {
        kind = r.Kind == Ecp.Operations.Domain.OperationKinds.Upgrade ? "upgrade-evidence" : "upgrade-rehearsal-evidence", subject = Redaction.Clean(r.Subject), r.RunId, fromVersion = r.FromVersion, toVersion = r.ToVersion,
        manifestSha256 = r.ManifestSha256, backupId = r.BackupId, @operator = Redaction.Clean(request.Operator.Id), reason = Redaction.Clean(request.Reason), acceptedWarnings = request.AcceptedWarningCodes.Order(StringComparer.Ordinal),
        startedUtc = r.StartedUtc, completedUtc = r.CompletedUtc, outcome = r.Outcome, failure = Redaction.Clean(r.Failure), correlationId = r.CorrelationId, maintenanceReleased = r.MaintenanceReleased,
        preflight = preflight is null ? null : Preflight(preflight),
        quiescence = r.Quiescence,
        steps = r.Steps.Select(s => new { s.Module, s.From, s.To, s.Migrations, s.Milliseconds, s.Outcome, failure = Redaction.Clean(s.Failure) }),
        validation = r.Validation is null ? null : Validation(r.Validation),
        rollback = new { method = "restore", backupId = r.BackupId, note = "Restore the verified pre-upgrade backup into a clean database and start the matching application version. Never run an older application against an upgraded database." },
        notice = Notice,
    };

    public static object Preflight(PreflightReport p) => new
    {
        outcome = p.Outcome.ToString(), phase = p.Phase.ToString(), state = p.State.ToString(), source = p.SourceVersion, target = p.TargetVersion, p.ManifestSha256, p.BackupId, p.StartedUtc, p.CompletedUtc, p.CorrelationId,
        pending = p.PendingMigrations,
        counts = new { passed = p.Count(CheckOutcome.Passed), warnings = p.Count(CheckOutcome.Warning), failed = p.Count(CheckOutcome.Failed), notVerified = p.Count(CheckOutcome.NotVerified) },
        checks = p.Checks.Select(c => new { c.Code, c.Title, outcome = c.Outcome.ToString(), severity = c.Severity.ToString(), c.Description, detail = Redaction.Clean(c.TechnicalDetail), c.Remediation }),
    };

    public static object Validation(ValidationReport v) => new
    {
        outcome = v.Outcome.ToString(), v.StartedUtc, v.CompletedUtc, v.CorrelationId,
        counts = new { passed = v.Count(CheckOutcome.Passed), warnings = v.Count(CheckOutcome.Warning), failed = v.Count(CheckOutcome.Failed), notVerified = v.Count(CheckOutcome.NotVerified) },
        checks = v.Checks.Select(c => new { c.Code, c.Title, outcome = c.Outcome.ToString(), severity = c.Severity.ToString(), c.Description, detail = Redaction.Clean(c.TechnicalDetail), c.Remediation }),
    };

    public static byte[] ToJson(object document)
    {
        var text = JsonSerializer.Serialize(document, ManifestJson.Options) + "\n";
        if (Redaction.ContainsSecret(text)) throw new DomainException("secret_in_report", "The report was not produced because it appears to contain a secret.");
        return Encoding.UTF8.GetBytes(text);
    }

    public static byte[] PreflightCsv(PreflightReport p)
    {
        var rows = new List<IReadOnlyList<object?>> { new object?[] { "Code", "Title", "Outcome", "Severity", "Explanation", "Technical detail", "Remediation", "Correlation" } };
        rows.Add(["REPORT", $"Upgrade pre-flight {p.SourceVersion} to {p.TargetVersion}", p.Outcome.ToString(), p.Phase.ToString(), $"state {p.State}; manifest {p.ManifestSha256}", "", "", p.CorrelationId]);
        foreach (var c in p.Checks) rows.Add([c.Code, c.Title, c.Outcome.ToString(), c.Severity.ToString(), c.Description, Redaction.Clean(c.TechnicalDetail), c.Remediation, c.CorrelationId]);
        return CsvWriter.Write(rows);     // formula-looking cells are neutralised
    }

    public static byte[] ValidationCsv(ValidationReport v)
    {
        var rows = new List<IReadOnlyList<object?>> { new object?[] { "Code", "Title", "Outcome", "Severity", "Explanation", "Technical detail", "Remediation", "Correlation" } };
        rows.Add(["REPORT", v.Subject, v.Outcome.ToString(), "", "", "", "", v.CorrelationId]);
        foreach (var c in v.Checks) rows.Add([c.Code, c.Title, c.Outcome.ToString(), c.Severity.ToString(), c.Description, Redaction.Clean(c.TechnicalDetail), c.Remediation, c.CorrelationId]);
        return CsvWriter.Write(rows);
    }

    public static byte[] ResultCsv(UpgradeResult r)
    {
        var rows = new List<IReadOnlyList<object?>> { new object?[] { "Section", "Item", "Outcome", "Detail", "Utc" } };
        rows.Add(["Upgrade", $"{r.FromVersion} to {r.ToVersion}", r.Outcome, Redaction.Clean(r.Failure), r.StartedUtc.ToString("O")]);
        rows.Add(["Upgrade", "Manifest SHA-256", r.ManifestSha256, "", ""]);
        rows.Add(["Upgrade", "Backup", r.BackupId, "", ""]);
        if (r.Quiescence is { } q) rows.Add(["Quiescence", q.Clear ? "Clear" : "Not clear", $"{q.WaitedMilliseconds} ms", q.Note, ""]);
        foreach (var s in r.Steps) rows.Add(["Migration", s.Module, s.Outcome, $"{s.Migrations} migration(s) to {s.To}; {s.Milliseconds} ms {Redaction.Clean(s.Failure)}", ""]);
        if (r.Validation is { } v) foreach (var c in v.Checks) rows.Add(["Validation", c.Code + " " + c.Title, c.Outcome.ToString(), c.Description, ""]);
        rows.Add(["Notice", "", "", Notice, ""]);
        return CsvWriter.Write(rows);
    }
}

internal static class EvidenceWriterUpgradeExtensions
{
    public static async Task<string?> WriteUpgradeAsync(this EvidenceWriter writer, UpgradeResult result, UpgradeRequest request, PreflightReport? preflight, CancellationToken ct)
        => await writer.WriteDocumentAsync($"{(result.Kind == Ecp.Operations.Domain.OperationKinds.Upgrade ? "upgrade" : "rehearsal")}-{result.StartedUtc:yyyyMMddTHHmmss}Z-{result.BackupId}.json", UpgradeEvidence.Document(result, request, preflight), ct);
}
