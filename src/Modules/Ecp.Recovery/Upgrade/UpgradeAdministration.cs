using System.Text.Json;
using System.Text.RegularExpressions;
using Ecp.Identity;
using Ecp.Operations;
using Ecp.Operations.Domain;
using Ecp.Persistence;
using Ecp.SharedKernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Ecp.Recovery;

public sealed record MigrationReportModule(string Module, string SchemaVersion, int Applied, int Pending, IReadOnlyList<MigrationReportRow> Migrations);

public sealed record MigrationReportRow(string Id, string Status, IReadOnlyList<string> Categories, IReadOnlyList<string> Objects, bool ReviewRequired, bool Reviewed, string? RecordedChecksumState);

public sealed record MigrationReport(string TargetVersion, string DetectedSourceVersion, UpgradeStateKind State, string? ManifestSha256, IReadOnlyList<UpgradeProblem> ManifestProblems, IReadOnlyList<MigrationReportModule> Modules,
    IReadOnlyList<DriftItem>? Drift, IReadOnlyList<UpgradeNotice> BreakingChanges, IReadOnlyList<UpgradeNotice> OperationalActions, IReadOnlyList<UpgradeNotice> CompatibilityWarnings, RollbackPlan? Rollback);

public sealed record UpgradeHistoryEntry(Guid Id, string Kind, string Status, string Reference, string Summary, DateTime StartedUtc, DateTime? CompletedUtc, string OperatorId, string CorrelationId);

public sealed record PostUpgradeValidation(Guid RunId, string Status, DateTime StartedUtc, DateTime? CompletedUtc, string Summary, IReadOnlyList<ValidationCheck> Checks, string? Evidence);

/// <summary>
/// The operator-facing upgrade services (Administration, Operations, Upgrade pages). Reading the pre-flight, the plan, the migration report and the history needs <c>operations.read</c>; technical detail and migration scripts need
/// <c>operations.validate</c>; downloads need <c>operations.evidence.export</c>; applying an upgrade needs <c>operations.upgrade</c> and a recent sign-in; a rehearsal and rollback evidence restore a copy and need
/// <c>operations.restore</c> and a recent sign-in. Every call re-checks, in the service, that the caller is in the installation's own tenant.
/// </summary>
public interface IUpgradeAdministration
{
    Task<PreflightReport> PreflightAsync(string? backupId, CancellationToken ct = default);
    Task<UpgradePlan> PlanAsync(string? backupId, CancellationToken ct = default);
    Task<MigrationReport> MigrationReportAsync(CancellationToken ct = default);
    Task<IReadOnlyList<UpgradeHistoryEntry>> HistoryAsync(CancellationToken ct = default);
    Task<PostUpgradeValidation?> LastPostUpgradeValidationAsync(CancellationToken ct = default);
    Task<RollbackEvidenceSummary?> LastRollbackEvidenceAsync(CancellationToken ct = default);
    Task<string> ScriptAsync(string module, CancellationToken ct = default);
    Task<UpgradeResult> ApplyAsync(string backupId, string reason, string confirmation, IReadOnlyList<string> acceptedWarningCodes, string? expectedManifestSha256, CancellationToken ct = default);
    Task<RehearsalResult> RehearseAsync(string backupId, string targetDatabase, string reason, string confirmation, IReadOnlyList<string> acceptedWarningCodes, CancellationToken ct = default);
    Task<RollbackEvidenceResult> ProveRollbackAsync(string backupId, string targetDatabase, string reason, string confirmation, CancellationToken ct = default);
    Task<byte[]> PreflightJsonAsync(string? backupId, CancellationToken ct = default);
    Task<byte[]> PreflightCsvAsync(string? backupId, CancellationToken ct = default);
    Task<byte[]> EvidenceFileAsync(string name, CancellationToken ct = default);
}

public sealed record RollbackEvidenceSummary(Guid RunId, string Status, string BackupId, string Summary, DateTime StartedUtc, DateTime? CompletedUtc, IReadOnlyList<ValidationCheck> Checks, string? Evidence);

internal sealed partial class UpgradeAdministration(
    IAccessService access, CurrentUser currentUser, ReauthenticationContext reauth, IClock clock, IOptions<RecoveryOptions> recovery, IUpgradePreflight preflight, UpgradeService upgrades, IUpgradeRehearsal rehearsal,
    IRollbackEvidence rollback, IOperationHistory history, OperationAudit audit, IServiceProvider root, DatabaseOptions databaseOptions, IDatabaseBackupProvider database,
    ActorContext actor, ITenantContext tenant) : IUpgradeAdministration
{
    private RecoveryOptions Options => recovery.Value;
    private TimeSpan Window => TimeSpan.FromMinutes(Math.Max(1, Options.ReauthenticationMinutes));

    private async Task RequireAsync(string permission, bool recentSignIn = false)
    {
        if (tenant.TenantId != SystemTenant.Id) throw new ForbiddenException("You do not have permission to do this.");
        if (!await access.HoldsAnywhereAsync(permission)) throw new ForbiddenException("You do not have permission to do this.");
        if (recentSignIn) reauth.Require(clock.UtcNow, Window);
    }

    private OperatorIdentity Operator()
    {
        var id = currentUser.UserId?.ToString("N") ?? actor.ActorId ?? "unknown";
        return new OperatorIdentity(id, id);
    }

    private UpgradeTarget Active() => new(root, databaseOptions.ConnectionString, database.Supported ? database.ActiveDatabaseName : "", true, "running installation");

    /// <summary>People who may read the pre-flight but not run validation see the explanation and the remediation, not the technical detail.</summary>
    private async Task<PreflightReport> ForReaderAsync(PreflightReport report)
    {
        if (await access.HoldsAnywhereAsync(OperationsPermissions.Validate)) return report;
        return report with { Checks = report.Checks.Select(c => c with { TechnicalDetail = "" }).ToList() };
    }

    public async Task<PreflightReport> PreflightAsync(string? backupId, CancellationToken ct = default)
    {
        await RequireAsync(OperationsPermissions.Read);
        var owner = Guid.NewGuid().ToString("N");
        var run = await history.StartAsync(OperationKinds.UpgradePreflight, "installation", "Operator-requested upgrade pre-flight", Operator(), ct);
        var report = await preflight.RunAsync(Active(), new PreflightOptions(backupId, PreflightPhase.Advisory), ct);
        await history.FinishAsync(run, report.Outcome switch { PreflightOutcome.Ready => OperationStatuses.Completed, PreflightOutcome.ReadyWithWarnings => OperationStatuses.CompletedWithWarnings, PreflightOutcome.NotVerified => OperationStatuses.NotVerified, _ => OperationStatuses.Failed },
            $"Upgrade pre-flight {report.SourceVersion} to {report.TargetVersion}: {report.Outcome} ({report.Count(CheckOutcome.Failed)} blocker(s), {report.Count(CheckOutcome.Warning)} warning(s), {report.Count(CheckOutcome.NotVerified)} not verified).",
            JsonSerializer.Serialize(UpgradeEvidence.Preflight(report), ManifestJson.Options), ct);
        _ = owner;
        return await ForReaderAsync(report);
    }

    public async Task<UpgradePlan> PlanAsync(string? backupId, CancellationToken ct = default)
    {
        await RequireAsync(OperationsPermissions.Read);
        var plan = await upgrades.PlanAsync(backupId, ct);
        return plan with { Preflight = await ForReaderAsync(plan.Preflight) };
    }

    public async Task<MigrationReport> MigrationReportAsync(CancellationToken ct = default)
    {
        await RequireAsync(OperationsPermissions.Read);
        return await upgrades.MigrationReportAsync(ct);
    }

    public async Task<IReadOnlyList<UpgradeHistoryEntry>> HistoryAsync(CancellationToken ct = default)
    {
        await RequireAsync(OperationsPermissions.Read);
        var all = new List<OperationRunDto>();
        foreach (var kind in new[] { OperationKinds.Upgrade, OperationKinds.UpgradeRehearsal, OperationKinds.RollbackEvidence, OperationKinds.UpgradePreflight })
            all.AddRange(await history.ListAsync(kind, 50, ct));
        return all.OrderByDescending(r => r.StartedUtc).Take(100).Select(r => new UpgradeHistoryEntry(r.Id, r.Kind, r.Status, Redaction.Clean(r.Reference), Redaction.Clean(r.Summary), r.StartedUtc, r.CompletedUtc, Redaction.Clean(r.OperatorId), r.CorrelationId)).ToList();
    }

    public async Task<PostUpgradeValidation?> LastPostUpgradeValidationAsync(CancellationToken ct = default)
    {
        await RequireAsync(OperationsPermissions.Read);
        var canSeeDetail = await access.HoldsAnywhereAsync(OperationsPermissions.Validate);
        var run = (await history.ListAsync(OperationKinds.Upgrade, 5, ct)).FirstOrDefault(r => r.CompletedUtc is not null);
        if (run is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(run.DetailJson);
            if (!doc.RootElement.TryGetProperty("validationChecks", out var array) || array.ValueKind != JsonValueKind.Array) return new PostUpgradeValidation(run.Id, run.Status, run.StartedUtc, run.CompletedUtc, run.Summary, [], null);
            var checks = array.EnumerateArray().Select(c => new ValidationCheck(c.GetProperty("Code").GetString() ?? "", c.GetProperty("Title").GetString() ?? "", Enum.Parse<CheckOutcome>(c.GetProperty("outcome").GetString() ?? "NotVerified"),
                Enum.TryParse<CheckSeverity>(c.GetProperty("severity").GetString(), out var sev) ? sev : CheckSeverity.Info, c.GetProperty("Description").GetString() ?? "", canSeeDetail ? c.GetProperty("detail").GetString() ?? "" : "", c.GetProperty("Remediation").GetString() ?? "", run.CorrelationId)).ToList();
            return new PostUpgradeValidation(run.Id, run.Status, run.StartedUtc, run.CompletedUtc, run.Summary, checks, doc.RootElement.TryGetProperty("evidence", out var e) ? e.GetString() : null);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or ArgumentException or InvalidOperationException) { return null; }
    }

    public async Task<RollbackEvidenceSummary?> LastRollbackEvidenceAsync(CancellationToken ct = default)
    {
        await RequireAsync(OperationsPermissions.Read);
        var run = (await history.ListAsync(OperationKinds.RollbackEvidence, 5, ct)).FirstOrDefault(r => r.CompletedUtc is not null);
        return run is null ? null : new RollbackEvidenceSummary(run.Id, run.Status, run.Reference, Redaction.Clean(run.Summary), run.StartedUtc, run.CompletedUtc, [], null);
    }

    public async Task<string> ScriptAsync(string module, CancellationToken ct = default)
    {
        await RequireAsync(OperationsPermissions.Validate);
        var text = await upgrades.ScriptAsync(module, ct);
        await audit.RecordAsync(new Ecp.Audit.AuditEntry("operations.upgrade.script.read", "Upgrade", module, NewValuesJson: JsonSerializer.Serialize(new { module, bytes = text.Length })), ct);
        return text;
    }

    public async Task<UpgradeResult> ApplyAsync(string backupId, string reason, string confirmation, IReadOnlyList<string> acceptedWarningCodes, string? expectedManifestSha256, CancellationToken ct = default)
    {
        await RequireAsync(OperationsPermissions.Upgrade, recentSignIn: true);
        return await upgrades.ApplyAsync(new UpgradeRequest(backupId, reason, confirmation, Operator(), acceptedWarningCodes, expectedManifestSha256), ct);
    }

    public async Task<RehearsalResult> RehearseAsync(string backupId, string targetDatabase, string reason, string confirmation, IReadOnlyList<string> acceptedWarningCodes, CancellationToken ct = default)
    {
        await RequireAsync(OperationsPermissions.Restore, recentSignIn: true);
        return await rehearsal.RehearseAsync(new RehearsalRequest(backupId, targetDatabase, reason, confirmation, Operator(), acceptedWarningCodes), ct);
    }

    public async Task<RollbackEvidenceResult> ProveRollbackAsync(string backupId, string targetDatabase, string reason, string confirmation, CancellationToken ct = default)
    {
        await RequireAsync(OperationsPermissions.Restore, recentSignIn: true);
        return await rollback.ProveAsync(new RollbackEvidenceRequest(backupId, targetDatabase, reason, confirmation, Operator()), ct);
    }

    public async Task<byte[]> PreflightJsonAsync(string? backupId, CancellationToken ct = default)
    {
        await RequireAsync(OperationsPermissions.EvidenceExport);
        var report = await preflight.RunAsync(Active(), new PreflightOptions(backupId, PreflightPhase.Advisory), ct);
        var bytes = UpgradeEvidence.ToJson(UpgradeEvidence.Preflight(report));
        await audit.RecordAsync(new Ecp.Audit.AuditEntry("operations.upgrade.preflight.exported", "Upgrade", "json", NewValuesJson: JsonSerializer.Serialize(new { format = "json", outcome = report.Outcome.ToString() })), ct);
        return bytes;
    }

    public async Task<byte[]> PreflightCsvAsync(string? backupId, CancellationToken ct = default)
    {
        await RequireAsync(OperationsPermissions.EvidenceExport);
        var report = await preflight.RunAsync(Active(), new PreflightOptions(backupId, PreflightPhase.Advisory), ct);
        await audit.RecordAsync(new Ecp.Audit.AuditEntry("operations.upgrade.preflight.exported", "Upgrade", "csv", NewValuesJson: JsonSerializer.Serialize(new { format = "csv", outcome = report.Outcome.ToString() })), ct);
        return UpgradeEvidence.PreflightCsv(report);
    }

    [GeneratedRegex(@"^(upgrade|rehearsal|rollback)-[0-9]{8}T[0-9]{6}Z-bk-[0-9]{8}T[0-9]{6}Z-[0-9a-f]{8}\.json$", RegexOptions.NonBacktracking)] private static partial Regex EvidenceName();

    public async Task<byte[]> EvidenceFileAsync(string name, CancellationToken ct = default)
    {
        await RequireAsync(OperationsPermissions.EvidenceExport);
        if (!EvidenceName().IsMatch(name ?? "")) throw new ResourceNotFoundException();
        var path = SafeFiles.Resolve(Path.Combine(Path.GetFullPath(Options.BackupDirectory), "_evidence"), name!);
        if (!File.Exists(path)) throw new ResourceNotFoundException();
        var bytes = await File.ReadAllBytesAsync(path, ct);
        await audit.RecordAsync(new Ecp.Audit.AuditEntry("operations.upgrade.evidence.exported", "Upgrade", name!, NewValuesJson: JsonSerializer.Serialize(new { name, bytes = bytes.Length })), ct);
        return bytes;
    }
}
