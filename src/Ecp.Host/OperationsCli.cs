using Ecp.Operations;
using Ecp.Recovery;
using Ecp.SharedKernel;

namespace Ecp.Host;

/// <summary>
/// Command-line operations for backup, restore and validation (docs/OPERATIONS_RUNBOOK.md). They run on the server, as an operator who already has access to the server; there is no signed-in person, so the web interface's
/// permission and sign-in checks do not apply. The same engines, confirmations, leases, maintenance mode and audit apply. Nothing prints a secret.
/// </summary>
public static class OperationsCli
{
    public static readonly string[] Commands = ["backup", "list-backups", "verify-backup", "restore", "validate", "maintenance", "support-bundle", "evidence", "upgrade-baseline"];

    public const string Usage = """
        Operations commands:
          backup --reason "<why>" [--hold-maintenance]
          list-backups
          verify-backup <backup id>
          restore <backup id> <new target database> --reason "<why>" --confirm "RESTORE <backup id>" [--expect-sha256 <manifest hash>] [--allow-different-environment] [--accept-missing-documents]
          restore <backup id> --in-place --reason "<why>" --confirm "RESTORE <backup id> OVER THE ACTIVE DATABASE"      (disaster recovery; stop the web application first)
          validate
          maintenance on|off --reason "<why>" [--acknowledge-restore-state]
          support-bundle <file>
          evidence <file> [--csv]
          upgrade-baseline <platform version> <file> --note "<what this baseline is>"      (release engineering: freeze the migration inventory of a release)
        """;

    private static string? Option(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static bool Flag(string[] args, string name) => Array.IndexOf(args, name) >= 0;

    public static async Task<int> RunAsync(string command, string[] args, IServiceProvider services, CancellationToken ct = default)
    {
        await using var scope = services.CreateAsyncScope();
        var provider = scope.ServiceProvider;
        var actor = provider.GetRequiredService<ActorContext>();
        actor.Type = ActorType.System; actor.Source = "Cli"; actor.ActorId = "cli:" + Environment.UserName;
        var who = new OperatorIdentity(actor.ActorId, actor.ActorId);
        var reason = Option(args, "--reason") ?? "";
        try
        {
            switch (command)
            {
                case "backup":
                {
                    var result = await provider.GetRequiredService<IBackupService>().CreateAsync(new BackupRequest(reason, who, Flag(args, "--hold-maintenance")), ct);
                    Console.WriteLine($"Backup {result.BackupId}: {result.Outcome}.");
                    if (result.ManifestSha256 is not null) Console.WriteLine($"Manifest SHA-256: {result.ManifestSha256}");
                    foreach (var w in result.Warnings) Console.WriteLine("  warning: " + Redaction.Clean(w));
                    if (result.Failure is not null) Console.Error.WriteLine(Redaction.Clean(result.Failure));
                    return result.Succeeded ? 0 : 2;
                }
                case "list-backups":
                    foreach (var b in await provider.GetRequiredService<IBackupCatalogue>().ListAsync(ct))
                        Console.WriteLine($"{b.BackupId}  {b.State,-20} {b.CreatedUtc:yyyy-MM-dd HH:mm}Z  {b.Bytes,14:N0} bytes  {b.Warnings} warning(s)  {b.Detail}");
                    return 0;
                case "verify-backup" when args.Length > 1:
                {
                    var v = await provider.GetRequiredService<IBackupVerifier>().VerifyAsync(args[1], persist: true, ct);
                    Console.WriteLine($"Backup {v.BackupId}: {v.Outcome} ({v.FilesChecked} files, {v.BytesChecked:N0} bytes checked).");
                    foreach (var f in v.Findings.Where(f => f.Severity != FindingSeverity.Info)) Console.WriteLine($"  {f.Severity,-8} {f.Code} {Redaction.Clean(f.Message)}");
                    return v.Outcome is VerificationOutcome.Passed or VerificationOutcome.PassedWithWarnings ? 0 : 2;
                }
                case "restore" when args.Length > 1:
                {
                    var inPlace = Flag(args, "--in-place");
                    var target = inPlace ? "" : args.Length > 2 && !args[2].StartsWith('-') ? args[2] : "";
                    var result = await provider.GetRequiredService<IRestoreService>().RestoreAsync(new RestoreRequest(args[1], target, reason, Option(args, "--confirm") ?? "", who, inPlace, ReplaceExistingTarget: false,
                        Flag(args, "--allow-different-environment"), Option(args, "--expect-sha256"), Flag(args, "--accept-missing-documents")), ct);
                    Console.WriteLine($"Restore of {result.BackupId} into {result.TargetDatabase}: {result.Outcome}.");
                    if (result.Validation is { } report)
                    {
                        Console.WriteLine($"Post-restore validation: {report.Outcome} ({report.Count(CheckOutcome.Passed)} passed, {report.Count(CheckOutcome.Warning)} warnings, {report.Count(CheckOutcome.Failed)} failed, {report.Count(CheckOutcome.NotVerified)} not verified).");
                        foreach (var c in report.Checks.Where(c => c.Outcome != CheckOutcome.Passed)) Console.WriteLine($"  {c.Outcome,-11} {c.Code} {c.Title}: {c.Description}");
                    }
                    if (result.Failure is not null) Console.Error.WriteLine(Redaction.Clean(result.Failure));
                    if (result.EvidenceFile is not null) Console.WriteLine("Evidence: " + result.EvidenceFile);
                    Console.WriteLine(result.Released ? "The restored copy passed validation and its maintenance mode was released." : "The restored copy is held in maintenance mode. It is not released.");
                    return result.Succeeded ? 0 : 2;
                }
                case "validate":
                {
                    var admin = provider.GetRequiredService<IInstallationValidator>();
                    var database = provider.GetRequiredService<IDatabaseBackupProvider>();
                    var options = provider.GetRequiredService<Ecp.Persistence.DatabaseOptions>();
                    var report = await admin.ValidateAsync(new ValidationTarget(services, options.ConnectionString, database.Supported ? database.ActiveDatabaseName : "", Restored: false, Manifest: null, HashDocumentContent: true, Subject: "running installation"), ct);
                    Console.WriteLine($"Validation: {report.Outcome}");
                    foreach (var c in report.Checks) Console.WriteLine($"  {c.Outcome,-11} {c.Code} {c.Title}: {c.Description}");
                    return report.AllowsRelease ? 0 : 2;
                }
                case "maintenance" when args.Length > 1 && args[1] is "on" or "off":
                {
                    var service = provider.GetRequiredService<IMaintenanceService>();
                    var status = args[1] == "on" ? await service.EnableAsync(reason, who, ct: ct) : await service.DisableAsync(reason, who, Flag(args, "--acknowledge-restore-state"), ct);
                    Console.WriteLine(status.Enabled ? "Maintenance mode is on." : "Maintenance mode is off.");
                    return 0;
                }
                case "support-bundle" when args.Length > 1:
                    await File.WriteAllBytesAsync(args[1], await provider.GetRequiredService<ISupportBundle>().BuildAsync(ct), ct);
                    Console.WriteLine("Support bundle written. Check it before you send it anywhere.");
                    return 0;
                case "evidence" when args.Length > 1:
                {
                    var reports = provider.GetRequiredService<IEvidenceReports>();
                    var report = await reports.BuildAsync(ct);
                    await File.WriteAllBytesAsync(args[1], Flag(args, "--csv") ? reports.ToCsv(report) : reports.ToJson(report), ct);
                    Console.WriteLine("Evidence report written.");
                    return 0;
                }
                case "upgrade-baseline" when args.Length > 2:
                {
                    var ledger = Ecp.Persistence.MigrationInventory.Build(provider.GetServices<Ecp.Persistence.ModuleDescriptor>());
                    await File.WriteAllBytesAsync(args[2], UpgradeJson.Serialise(UpgradeManifestBuilder.Baseline(args[1], Option(args, "--note") ?? "", ledger)), ct);
                    Console.WriteLine($"Baseline for {args[1]} written ({ledger.Modules.Sum(m => m.Migrations.Count)} migrations in {ledger.Modules.Count} modules).");
                    return 0;
                }
                default:
                    Console.Error.WriteLine(Usage);
                    return 64;
            }
        }
        catch (DomainException ex) { Console.Error.WriteLine(Redaction.Clean(ex.Message)); return 2; }
    }
}
