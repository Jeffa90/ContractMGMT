using Ecp.Operations;
using Ecp.Persistence;
using Ecp.Recovery;
using Ecp.SharedKernel;

namespace Ecp.Host;

/// <summary>
/// Command-line upgrade tooling (docs/UPGRADE_RUNBOOK.md). Like the other operations commands it runs on the server for an operator who already has access to it; the same engines, confirmations, leases, maintenance mode,
/// verified-backup requirement and audit apply as in the web interface. Nothing prints a secret.
/// </summary>
public static class UpgradeCli
{
    public static readonly string[] Commands = ["upgrade-manifest-build", "upgrade-manifest-verify", "upgrade-preflight", "upgrade-plan", "upgrade-script", "upgrade-apply", "upgrade-rehearse", "upgrade-rollback-evidence", "upgrade-history", "migration-report"];

    public const string Usage = """
        Upgrade commands:
          upgrade-preflight [--backup <backup id>] [--apply-phase] [--json <file>] [--csv <file>]      (read-only; exit code 0 only when Ready or Ready with warnings)
          upgrade-plan [--backup <backup id>]                                                          (what would run, and the words to type)
          migration-report                                                                             (migrations by module: applied, pending, categories, review state)
          upgrade-script <module> [<file>]                                                             (the SQL the pending migrations of a module would run, for review)
          upgrade-rehearse <backup id> <new target database> --reason "<why>" --confirm "REHEARSE UPGRADE FROM <backup id>" [--accept <codes>]
          upgrade-apply <backup id> --reason "<why>" --confirm "UPGRADE <from> TO <to>" [--accept <codes>] [--expect-sha256 <manifest hash>]      (maintenance mode must be on)
          upgrade-rollback-evidence <backup id> <new target database> --reason "<why>" --confirm "PROVE ROLLBACK TO <backup id>"
          upgrade-history
          upgrade-manifest-verify                                                                      (check the manifest this build carries)
          upgrade-manifest-build <release notes file> <output file>                                    (release engineering)
        """;

    private static string? Option(string[] args, string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
    private static bool Flag(string[] args, string name) => Array.IndexOf(args, name) >= 0;
    private static IReadOnlyList<string> Codes(string[] args) => (Option(args, "--accept") ?? "").Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// The `migrate` command applies migrations. In Production it is for a first installation only: on a database that already holds an installation it refuses and points to the upgrade commands, because they enforce the verified
    /// backup, maintenance mode and pre-flight that a bare migrate does not. Returns null when migrate may proceed.
    /// </summary>
    public static async Task<string?> GuardMigrateAsync(IServiceProvider services, EcpMode mode, CancellationToken ct = default)
    {
        if (mode != EcpMode.Production) return null;
        return await UpgradeInstallation.HasInstallationAsync(services, ct)
            ? "This database already holds an installation. In Production, migrations are applied only by the upgrade procedure, after a verified backup, in maintenance mode: run upgrade-preflight, then upgrade-apply (docs/UPGRADE_RUNBOOK.md)."
            : null;
    }

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
                case "upgrade-manifest-build" when args.Length > 2:
                {
                    var catalogue = UpgradeCatalogue.Embedded();
                    var notes = UpgradeJson.Parse<ReleaseNotes>(await File.ReadAllBytesAsync(args[1], ct));
                    var baseline = catalogue.Baseline(notes.From) ?? throw new DomainException("baseline_missing", $"There is no frozen baseline for {notes.From}.");
                    var ledger = MigrationInventory.Build(provider.GetServices<ModuleDescriptor>());
                    var manifest = UpgradeManifestBuilder.Build(notes, baseline, ledger, catalogue.Reviews);
                    var problems = UpgradeManifestValidator.Validate(manifest, catalogue, ledger);
                    foreach (var p in problems) Console.WriteLine($"  {(p.Critical ? "CRITICAL" : "warning "),-8} {p.Code} {p.Message}");
                    if (problems.Any(p => p.Critical)) { Console.Error.WriteLine("The manifest was not written because it has critical problems."); return 2; }
                    await File.WriteAllBytesAsync(args[2], UpgradeJson.Serialise(manifest), ct);
                    Console.WriteLine($"Manifest {manifest.ReleaseId} written ({manifest.NewMigrations.Count} new migration(s)); SHA-256 {manifest.ManifestSha256}");
                    return 0;
                }
                case "upgrade-manifest-verify":
                {
                    var catalogue = provider.GetRequiredService<UpgradeCatalogue>();
                    var manifest = catalogue.ManifestTo(UpgradeVersions.Current);
                    if (manifest is null) { Console.Error.WriteLine($"This build (platform {UpgradeVersions.Current}) carries no upgrade manifest."); return 2; }
                    var problems = UpgradeManifestValidator.Validate(manifest, catalogue, MigrationInventory.Build(provider.GetServices<ModuleDescriptor>()));
                    Console.WriteLine($"Manifest {manifest.ReleaseId}: {manifest.SourcePlatformVersion} to {manifest.TargetPlatformVersion}, SHA-256 {manifest.ManifestSha256}");
                    foreach (var p in problems) Console.WriteLine($"  {(p.Critical ? "CRITICAL" : "warning "),-8} {p.Code} {p.Message}");
                    Console.WriteLine(problems.Any(p => p.Critical) ? "The manifest is NOT valid for this build." : "The manifest is valid for this build.");
                    return problems.Any(p => p.Critical) ? 2 : 0;
                }
                case "upgrade-preflight":
                {
                    var preflight = provider.GetRequiredService<IUpgradePreflight>();
                    var options = provider.GetRequiredService<DatabaseOptions>();
                    var database = provider.GetRequiredService<IDatabaseBackupProvider>();
                    var report = await preflight.RunAsync(new UpgradeTarget(services, options.ConnectionString, database.Supported ? database.ActiveDatabaseName : "", true, "running installation"),
                        new PreflightOptions(Option(args, "--backup"), Flag(args, "--apply-phase") ? PreflightPhase.Apply : PreflightPhase.Advisory), ct);
                    Console.WriteLine($"Upgrade pre-flight {report.SourceVersion} to {report.TargetVersion}: {report.Outcome} (state {report.State}; {report.Count(CheckOutcome.Passed)} passed, {report.Count(CheckOutcome.Warning)} warning(s), {report.Count(CheckOutcome.Failed)} blocker(s), {report.Count(CheckOutcome.NotVerified)} not verified)");
                    foreach (var c in report.Checks.Where(c => c.Outcome != CheckOutcome.Passed)) Console.WriteLine($"  {c.Outcome,-11} {c.Code} {c.Title}: {c.Description}{(c.Remediation.Length > 0 ? " → " + c.Remediation : "")}");
                    if (Option(args, "--json") is { } json) await File.WriteAllBytesAsync(json, UpgradeEvidence.ToJson(UpgradeEvidence.Preflight(report)), ct);
                    if (Option(args, "--csv") is { } csv) await File.WriteAllBytesAsync(csv, UpgradeEvidence.PreflightCsv(report), ct);
                    return report.Outcome is PreflightOutcome.Ready or PreflightOutcome.ReadyWithWarnings ? 0 : 2;
                }
                case "upgrade-plan":
                {
                    var plan = await provider.GetRequiredService<IUpgradeService>().PlanAsync(Option(args, "--backup"), ct);
                    Console.WriteLine($"Upgrade {plan.FromVersion} to {plan.ToVersion}: pre-flight {plan.Preflight.Outcome}; {plan.Migrations.Count} migration(s) to apply.");
                    foreach (var m in plan.Migrations) Console.WriteLine($"  {m.Module}/{m.Id}  [{string.Join(", ", m.Categories)}]{(m.ReviewRequired ? (m.Reviewed ? " reviewed" : " REVIEW MISSING") : "")}");
                    Console.WriteLine($"To apply, type exactly: {plan.ConfirmationPhrase}");
                    return plan.Preflight.Outcome is PreflightOutcome.Ready or PreflightOutcome.ReadyWithWarnings ? 0 : 2;
                }
                case "migration-report":
                {
                    var report = await provider.GetRequiredService<IUpgradeService>().MigrationReportAsync(ct);
                    Console.WriteLine($"Platform {report.TargetVersion}; installation {report.State}{(report.State == UpgradeStateKind.AtSource ? " at " + report.DetectedSourceVersion : "")}; manifest {report.ManifestSha256}");
                    foreach (var p in report.ManifestProblems) Console.WriteLine($"  {(p.Critical ? "CRITICAL" : "warning "),-8} {p.Code} {p.Message}");
                    foreach (var m in report.Modules)
                    {
                        Console.WriteLine($"{m.Module}: {m.Applied} applied, {m.Pending} pending (schema version {m.SchemaVersion})");
                        foreach (var row in m.Migrations.Where(r => r.Status == "Pending" || r.Categories.Any(Ecp.Persistence.MigrationCategories.NeedReview.Contains)))
                            Console.WriteLine($"    {row.Status,-8} {row.Id}  [{string.Join(", ", row.Categories)}]{(row.ReviewRequired ? (row.Reviewed ? " review recorded" : " REVIEW MISSING") : "")}");
                    }
                    if (report.Drift is { Count: > 0 } drift) foreach (var d in drift) Console.WriteLine($"  DRIFT {d.Object}: {d.Problem}");
                    return 0;
                }
                case "upgrade-script" when args.Length > 1:
                {
                    var text = await provider.GetRequiredService<IUpgradeService>().ScriptAsync(args[1], ct);
                    if (args.Length > 2 && !args[2].StartsWith('-')) { await File.WriteAllTextAsync(args[2], text, ct); Console.WriteLine("Script written for review."); }
                    else Console.WriteLine(text);
                    return 0;
                }
                case "upgrade-apply" when args.Length > 1:
                {
                    var result = await provider.GetRequiredService<IUpgradeService>().ApplyAsync(new UpgradeRequest(args[1], reason, Option(args, "--confirm") ?? "", who, Codes(args), Option(args, "--expect-sha256")), ct);
                    PrintUpgrade(result);
                    return result.Succeeded ? 0 : 2;
                }
                case "upgrade-rehearse" when args.Length > 2:
                {
                    var result = await provider.GetRequiredService<IUpgradeRehearsal>().RehearseAsync(new RehearsalRequest(args[1], args[2], reason, Option(args, "--confirm") ?? "", who, Codes(args)), ct);
                    Console.WriteLine($"Rehearsal from {result.BackupId} on {result.TargetDatabase}: {result.Outcome}.");
                    if (result.Upgrade is { } up) PrintUpgrade(up);
                    if (result.Failure is not null) Console.Error.WriteLine(Redaction.Clean(result.Failure));
                    return result.Succeeded ? 0 : 2;
                }
                case "upgrade-rollback-evidence" when args.Length > 2:
                {
                    var result = await provider.GetRequiredService<IRollbackEvidence>().ProveAsync(new RollbackEvidenceRequest(args[1], args[2], reason, Option(args, "--confirm") ?? "", who), ct);
                    Console.WriteLine($"Rollback evidence for {result.BackupId} on {result.TargetDatabase}: {result.Outcome}. Required application version {result.RequiredApplicationVersion}; running {result.RunningApplicationVersion}.");
                    foreach (var c in result.Checks) Console.WriteLine($"  {c.Outcome,-11} {c.Code} {c.Title}: {c.Description}");
                    Console.WriteLine($"Rolling back would discard {result.EventsDiscarded} audited event(s) recorded after the backup.");
                    if (result.Failure is not null) Console.Error.WriteLine(Redaction.Clean(result.Failure));
                    if (result.EvidenceFile is not null) Console.WriteLine("Evidence: " + result.EvidenceFile);
                    return result.Succeeded ? 0 : 2;
                }
                case "upgrade-history":
                    foreach (var r in await provider.GetRequiredService<IOperationHistory>().ListAsync(null, 100, ct))
                        if (r.Kind is "Upgrade" or "UpgradeRehearsal" or "RollbackEvidence" or "UpgradePreflight")
                            Console.WriteLine($"{r.StartedUtc:yyyy-MM-dd HH:mm}Z  {r.Kind,-16} {r.Status,-22} {Redaction.Clean(r.Summary)}");
                    return 0;
                default:
                    Console.Error.WriteLine(Usage);
                    return 64;
            }
        }
        catch (DomainException ex) { Console.Error.WriteLine(Redaction.Clean(ex.Message)); return 2; }
    }

    private static void PrintUpgrade(UpgradeResult result)
    {
        Console.WriteLine($"Upgrade {result.FromVersion} to {result.ToVersion} ({result.Kind}): {result.Outcome}.");
        if (result.Quiescence is { } q) Console.WriteLine($"Quiescence: {(q.Clear ? "clear" : "NOT clear")} — {q.Note}");
        foreach (var s in result.Steps) Console.WriteLine($"  {s.Outcome,-8} {s.Module}: {s.Migrations} migration(s) to {s.To} in {s.Milliseconds} ms{(s.Failure is null ? "" : " — " + s.Failure)}");
        if (result.Validation is { } v)
        {
            Console.WriteLine($"Post-upgrade validation: {v.Outcome} ({v.Count(CheckOutcome.Passed)} passed, {v.Count(CheckOutcome.Warning)} warning(s), {v.Count(CheckOutcome.Failed)} failed, {v.Count(CheckOutcome.NotVerified)} not verified)");
            foreach (var c in v.Checks.Where(c => c.Outcome != CheckOutcome.Passed)) Console.WriteLine($"  {c.Outcome,-11} {c.Code} {c.Title}: {c.Description}");
        }
        if (result.Failure is not null) Console.Error.WriteLine(Redaction.Clean(result.Failure));
        if (result.EvidenceFile is not null) Console.WriteLine("Evidence: " + result.EvidenceFile);
        Console.WriteLine(result.MaintenanceReleased ? "The upgrade passed validation and maintenance mode was released." : "The installation is held in maintenance mode. It is not released.");
    }
}
