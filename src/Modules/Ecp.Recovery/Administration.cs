using System.Text.Json;
using Ecp.Identity;
using Ecp.Operations;
using Ecp.Operations.Domain;
using Ecp.Persistence;
using Ecp.SharedKernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Ecp.Recovery;

public static class OperationsPermissions
{
    public const string Read = "operations.read";
    public const string Backup = "operations.backup";
    public const string Restore = "operations.restore";
    public const string Validate = "operations.validate";
    public const string Maintenance = "operations.maintenance";
    public const string EvidenceExport = "operations.evidence.export";
    public const string Upgrade = "operations.upgrade";
}

public sealed record OperationsOverview(bool BackupConfigured, bool RestoreConfigured, bool DatabaseSupported, string DatabaseEngine, string UnsupportedReason, bool EncryptionRequested, bool StorageProtectionStated,
    bool AdminAccountConfigured, MaintenanceStatus Maintenance, IReadOnlyList<BackupSummary> Backups, IReadOnlyList<OperationRunDto> Runs, IReadOnlyList<LeaseInfo> Leases, BackupSummary? LastVerified, int StaleBackupDays);

/// <summary>
/// The operator-facing entry point (Administration, Operations). Each action checks the permission in the service, not only in the page; destructive or state-changing actions also need a recent sign-in. The engines it
/// calls (<see cref="IBackupService"/>, <see cref="IRestoreService"/>) are also used by the command line, which has no signed-in user and relies on access to the server instead.
/// </summary>
public interface IOperationsAdministration
{
    Task<OperationsOverview> OverviewAsync(CancellationToken ct = default);
    Task<IReadOnlyList<BackupSummary>> BackupsAsync(CancellationToken ct = default);
    Task<BackupSummary?> BackupAsync(string backupId, CancellationToken ct = default);
    Task<BackupManifest> ManifestAsync(string backupId, CancellationToken ct = default);
    Task<IReadOnlyList<OperationRunDto>> RunsAsync(string? kind, CancellationToken ct = default);
    Task<IReadOnlyList<LeaseInfo>> LeasesAsync(CancellationToken ct = default);
    Task<BackupResult> StartBackupAsync(string reason, bool holdMaintenance, CancellationToken ct = default);
    Task<BackupVerification> VerifyBackupAsync(string backupId, CancellationToken ct = default);
    Task<RestorePreview> PreviewRestoreAsync(string backupId, string targetDatabase, CancellationToken ct = default);
    Task<RestoreResult> StartRestoreAsync(string backupId, string targetDatabase, string reason, string confirmation, bool allowDifferentEnvironment, string? expectedManifestSha256, CancellationToken ct = default);
    Task<ValidationReport> ValidateInstallationAsync(CancellationToken ct = default);
    Task<ValidationReport?> LastValidationAsync(CancellationToken ct = default);
    Task<MaintenanceStatus> MaintenanceAsync(CancellationToken ct = default);
    Task<MaintenanceStatus> EnterMaintenanceAsync(string reason, CancellationToken ct = default);
    Task<MaintenanceStatus> LeaveMaintenanceAsync(string reason, bool acknowledgeRestoreState, CancellationToken ct = default);
    Task<LeaseGrant> TakeOverLeaseAsync(string name, string scope, string reason, CancellationToken ct = default);
    Task<EvidenceReport> EvidenceAsync(CancellationToken ct = default);
    Task<byte[]> EvidenceJsonAsync(CancellationToken ct = default);
    Task<byte[]> EvidenceCsvAsync(CancellationToken ct = default);
    Task<byte[]> SupportBundleAsync(CancellationToken ct = default);
}

internal sealed class OperationsAdministration(
    IAccessService access, CurrentUser currentUser, ReauthenticationContext reauth, IClock clock, IOptions<RecoveryOptions> recovery, IDatabaseBackupProvider database, IBackupCatalogue catalogue, IBackupVerifier verifier,
    IBackupService backups, IRestoreService restores, IInstallationValidator validator, IOperationHistory history, IOperationLeases leases, IMaintenanceService maintenance, IEvidenceReports evidence,
    ISupportBundle bundle, OperationAudit audit, DatabaseOptions databaseOptions, IServiceProvider root, IServiceScopeFactory scopes, ActorContext actor, ITenantContext tenant) : IOperationsAdministration
{
    private RecoveryOptions Options => recovery.Value;
    private TimeSpan Window => TimeSpan.FromMinutes(Math.Max(1, Options.ReauthenticationMinutes));

    private async Task RequireAsync(string permission, bool recentSignIn = false)
    {
        // Operations belong to the installation. Whatever a person holds inside a customer tenant, these actions are only ever available from the installation's own tenant.
        if (tenant.TenantId != SystemTenant.Id) throw new ForbiddenException("You do not have permission to do this.");
        if (!await access.HoldsAnywhereAsync(permission)) throw new ForbiddenException("You do not have permission to do this.");
        if (recentSignIn) reauth.Require(clock.UtcNow, Window);
    }

    private OperatorIdentity Operator()
    {
        var id = currentUser.UserId?.ToString("N") ?? actor.ActorId ?? "unknown";
        return new OperatorIdentity(id, id);
    }

    public async Task<OperationsOverview> OverviewAsync(CancellationToken ct = default)
    {
        await RequireAsync(OperationsPermissions.Read);
        var list = await catalogue.ListAsync(ct);
        return new OperationsOverview(Options.BackupConfigured, Options.RestoreConfigured, database.Supported, database.Engine, database.UnsupportedReason, Options.EncryptionRequested, !string.IsNullOrWhiteSpace(Options.StorageProtection),
            !string.IsNullOrWhiteSpace(Options.AdminConnectionString), await maintenance.GetAsync(ct), list.Take(10).ToList(), await history.ListAsync(null, 10, ct), await leases.ListAsync(ct),
            list.FirstOrDefault(b => b.State is BackupState.Verified or BackupState.VerifiedWithWarnings), Options.StaleBackupDays);
    }

    public async Task<IReadOnlyList<BackupSummary>> BackupsAsync(CancellationToken ct = default) { await RequireAsync(OperationsPermissions.Read); return await catalogue.ListAsync(ct); }
    public async Task<BackupSummary?> BackupAsync(string backupId, CancellationToken ct = default) { await RequireAsync(OperationsPermissions.Read); return await catalogue.FindAsync(backupId, ct); }
    public async Task<BackupManifest> ManifestAsync(string backupId, CancellationToken ct = default) { await RequireAsync(OperationsPermissions.Read); return await verifier.LoadManifestAsync(backupId, ct); }
    public async Task<IReadOnlyList<OperationRunDto>> RunsAsync(string? kind, CancellationToken ct = default) { await RequireAsync(OperationsPermissions.Read); return await history.ListAsync(kind, 100, ct); }
    public async Task<IReadOnlyList<LeaseInfo>> LeasesAsync(CancellationToken ct = default) { await RequireAsync(OperationsPermissions.Read); return await leases.ListAsync(ct); }

    public async Task<BackupResult> StartBackupAsync(string reason, bool holdMaintenance, CancellationToken ct = default)
    {
        await RequireAsync(OperationsPermissions.Backup, recentSignIn: true);
        if (holdMaintenance) await RequireAsync(OperationsPermissions.Maintenance);
        return await backups.CreateAsync(new BackupRequest(reason, Operator(), holdMaintenance), ct);
    }

    public async Task<BackupVerification> VerifyBackupAsync(string backupId, CancellationToken ct = default)
    {
        await RequireAsync(OperationsPermissions.Backup);
        var result = await verifier.VerifyAsync(backupId, persist: true, ct);
        await audit.RecordAsync(new Ecp.Audit.AuditEntry("operations.backup.verified", "Backup", backupId, result.Outcome == VerificationOutcome.Failed ? Ecp.Audit.AuditOutcome.Failed : Ecp.Audit.AuditOutcome.Success,
            NewValuesJson: JsonSerializer.Serialize(new { backupId, outcome = result.Outcome.ToString(), manifestSha256 = result.ManifestSha256 })), ct);
        return result;
    }

    public async Task<RestorePreview> PreviewRestoreAsync(string backupId, string targetDatabase, CancellationToken ct = default)
    {
        await RequireAsync(OperationsPermissions.Restore);
        return await restores.PreviewAsync(backupId, targetDatabase, inPlace: false, ct);
    }

    public async Task<RestoreResult> StartRestoreAsync(string backupId, string targetDatabase, string reason, string confirmation, bool allowDifferentEnvironment, string? expectedManifestSha256, CancellationToken ct = default)
    {
        await RequireAsync(OperationsPermissions.Restore, recentSignIn: true);
        // The disaster-recovery path (restoring over the active database) is deliberately not reachable from the web interface: it is run from the server's command line with the application stopped.
        return await restores.RestoreAsync(new RestoreRequest(backupId, targetDatabase, reason, confirmation, Operator(), InPlaceDisasterRecovery: false, ReplaceExistingTarget: false, allowDifferentEnvironment, expectedManifestSha256), ct);
    }

    public async Task<ValidationReport> ValidateInstallationAsync(CancellationToken ct = default)
    {
        await RequireAsync(OperationsPermissions.Validate);
        var owner = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";
        await using var lease = await LeaseHolder.TryAcquireAsync(scopes, RecoveryLeases.Exclusive, IOperationLeases.InstallationScope, owner, "installation validation", TimeSpan.FromMinutes(Math.Max(2, Options.LeaseMinutes)), ct)
            ?? throw new DomainException("operation_in_progress", await RecoveryLeases.DescribeBusyAsync(scopes, ct));
        var run = await history.StartAsync(OperationKinds.Validation, "installation", "Operator-requested validation of the running installation", Operator(), ct);
        var report = await validator.ValidateAsync(new ValidationTarget(root, databaseOptions.ConnectionString, database.Supported ? database.ActiveDatabaseName : "", Restored: false, Manifest: null, HashDocumentContent: true, Subject: "running installation"), ct);
        var status = report.Outcome switch { ValidationOutcome.Passed => OperationStatuses.Completed, ValidationOutcome.PassedWithWarnings => OperationStatuses.CompletedWithWarnings, ValidationOutcome.NotVerified => OperationStatuses.NotVerified, _ => OperationStatuses.Failed };
        await history.FinishAsync(run, status, $"Validation of the running installation: {report.Outcome} ({report.Count(CheckOutcome.Failed)} failed, {report.Count(CheckOutcome.Warning)} warnings, {report.Count(CheckOutcome.NotVerified)} not verified).",
            JsonSerializer.Serialize(new { report.Outcome, checks = report.Checks.Select(c => new { c.Code, c.Title, outcome = c.Outcome.ToString(), c.Description, detail = Redaction.Clean(c.TechnicalDetail), c.Remediation }) }, ManifestJson.Options), ct);
        await audit.RecordAsync(new Ecp.Audit.AuditEntry("operations.validation.run", "Validation", report.CorrelationId, report.AllowsRelease ? Ecp.Audit.AuditOutcome.Success : Ecp.Audit.AuditOutcome.Failed,
            NewValuesJson: JsonSerializer.Serialize(new { outcome = report.Outcome.ToString() })), ct);
        return report;
    }

    public async Task<ValidationReport?> LastValidationAsync(CancellationToken ct = default)
    {
        await RequireAsync(OperationsPermissions.Read);
        var run = (await history.ListAsync(OperationKinds.Validation, 1, ct)).FirstOrDefault();
        if (run is null) return null;
        var canSeeDetail = await access.HoldsAnywhereAsync(OperationsPermissions.Validate);
        try
        {
            using var doc = JsonDocument.Parse(run.DetailJson);
            var checks = doc.RootElement.GetProperty("checks").EnumerateArray().Select(c => new ValidationCheck(c.GetProperty("Code").GetString() ?? "", c.GetProperty("Title").GetString() ?? "",
                Enum.Parse<CheckOutcome>(c.GetProperty("outcome").GetString() ?? "NotVerified"), CheckSeverity.Info, c.GetProperty("Description").GetString() ?? "",
                canSeeDetail ? c.GetProperty("detail").GetString() ?? "" : "", c.GetProperty("Remediation").GetString() ?? "", run.CorrelationId)).ToList();
            return new ValidationReport("running installation", run.StartedUtc, run.CompletedUtc ?? run.StartedUtc, ValidationReport.Summarise(checks), checks, run.CorrelationId);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or ArgumentException) { return null; }
    }

    public async Task<MaintenanceStatus> MaintenanceAsync(CancellationToken ct = default) { await RequireAsync(OperationsPermissions.Read); return await maintenance.GetAsync(ct); }

    public async Task<MaintenanceStatus> EnterMaintenanceAsync(string reason, CancellationToken ct = default)
    {
        await RequireAsync(OperationsPermissions.Maintenance, recentSignIn: true);
        return await maintenance.EnableAsync(reason, Operator(), MaintenanceCauses.Manual, ct);
    }

    public async Task<MaintenanceStatus> LeaveMaintenanceAsync(string reason, bool acknowledgeRestoreState, CancellationToken ct = default)
    {
        await RequireAsync(OperationsPermissions.Maintenance, recentSignIn: true);
        return await maintenance.DisableAsync(reason, Operator(), acknowledgeRestoreState, ct);
    }

    public async Task<LeaseGrant> TakeOverLeaseAsync(string name, string scope, string reason, CancellationToken ct = default)
    {
        await RequireAsync(OperationsPermissions.Maintenance, recentSignIn: true);
        var operatorId = Operator();
        // The operator takes the lease only to stop the old owner; it is released at once, so the work can be started again.
        var grant = await leases.StealAsync(name, scope, "operator:" + operatorId.Id, "taken over by an operator", reason, TimeSpan.FromMinutes(1), ct);
        await leases.ReleaseAsync(grant, ct);
        return grant;
    }

    public async Task<EvidenceReport> EvidenceAsync(CancellationToken ct = default) { await RequireAsync(OperationsPermissions.Read); return await evidence.BuildAsync(ct); }

    public async Task<byte[]> EvidenceJsonAsync(CancellationToken ct = default)
    {
        await RequireAsync(OperationsPermissions.EvidenceExport);
        var bytes = evidence.ToJson(await evidence.BuildAsync(ct));
        await audit.RecordAsync(new Ecp.Audit.AuditEntry("operations.evidence.exported", "Evidence", "json", NewValuesJson: JsonSerializer.Serialize(new { format = "json", bytes = bytes.Length })), ct);
        return bytes;
    }

    public async Task<byte[]> EvidenceCsvAsync(CancellationToken ct = default)
    {
        await RequireAsync(OperationsPermissions.EvidenceExport);
        var bytes = evidence.ToCsv(await evidence.BuildAsync(ct));
        await audit.RecordAsync(new Ecp.Audit.AuditEntry("operations.evidence.exported", "Evidence", "csv", NewValuesJson: JsonSerializer.Serialize(new { format = "csv", bytes = bytes.Length })), ct);
        return bytes;
    }

    public async Task<byte[]> SupportBundleAsync(CancellationToken ct = default)
    {
        await RequireAsync(OperationsPermissions.EvidenceExport);
        var bytes = await bundle.BuildAsync(ct);
        await audit.RecordAsync(new Ecp.Audit.AuditEntry("operations.supportbundle.exported", "SupportBundle", "zip", NewValuesJson: JsonSerializer.Serialize(new { bytes = bytes.Length })), ct);
        return bytes;
    }
}
