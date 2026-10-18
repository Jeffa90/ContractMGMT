using System.Text;
using Ecp.Operations.Domain;
using Ecp.Persistence;
using Ecp.Recovery;
using Ecp.SharedKernel;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Xunit;

namespace Ecp.UnitTests;

/// <summary>The building blocks of upgrade safety (docs/UPGRADES.md): the manifest and its validation, where an installation stands, what a migration does, and the evidence. The end-to-end proof on a real SQL Server is in <c>UpgradeSqlServerTests</c>.</summary>
[Trait("Category", "Unit")]
public sealed class UpgradeTests
{
    // ---- fixtures: a two-module installation at 1.0.0 and the 1.1.0 that adds one migration ----

    private static LedgerMigration M(string id, string sum, params string[] categories) => new(id, sum, categories, [], 1);

    private static LedgerModule Module(string name, int tier, params LedgerMigration[] migrations) => new(name, tier, name.ToLowerInvariant(), migrations.LastOrDefault()?.Id ?? "", migrations);

    private static ReleaseLedger Baseline100()
    {
        var modules = new[] { Module("Alpha", 1, M("2026_01_Initial", "aa"), M("2026_02_More", "ab")), Module("Beta", 2, M("2026_01_Initial", "ba")) };
        return new ReleaseLedger(1, "1.0.0", "10.0.0", "test baseline", modules, UpgradeJson.LedgerHash(modules));
    }

    private static MigrationLedger Target(Func<IReadOnlyList<LedgerModule>, IReadOnlyList<LedgerModule>>? change = null)
    {
        IReadOnlyList<LedgerModule> modules = [Module("Alpha", 1, M("2026_01_Initial", "aa"), M("2026_02_More", "ab")), Module("Beta", 2, M("2026_01_Initial", "ba"), M("2026_03_Added", "bb", MigrationCategories.IndexOrConstraint))];
        modules = change?.Invoke(modules) ?? modules;
        return new MigrationLedger("10.0.0", modules.Select(m => new ModuleMigrations(m.Module, m.Tier, m.Schema, m.SchemaVersion, m.Migrations.Select(x => new MigrationEntry(x.Id, x.Checksum, x.Categories, x.Objects, x.Operations)).ToList())).ToList());
    }

    private static ReleaseNotes Notes() => new(1, "ecp-1.1.0", "1.0.0", "1.1.0", "1.0.0", ["1.0.0"], 24, [], [], [], [], ["VAL-002"], new RollbackPlan("restore", ["Restore the verified backup."], "Rolling back discards everything created after the backup."));

    private static (UpgradeManifest Manifest, UpgradeCatalogue Catalogue, MigrationLedger Compiled) Release(Func<IReadOnlyList<LedgerModule>, IReadOnlyList<LedgerModule>>? change = null, MigrationReviewRegister? reviews = null)
    {
        var baseline = Baseline100();
        var compiled = Target(change);
        var register = reviews ?? new MigrationReviewRegister(1, []);
        var manifest = UpgradeManifestBuilder.Build(Notes(), baseline, compiled, register);
        return (manifest, new UpgradeCatalogue([baseline], [manifest], register, [Notes()]), compiled);
    }

    private static IEnumerable<string> Codes(IEnumerable<UpgradeProblem> problems) => problems.Where(p => p.Critical).Select(p => p.Code);

    // ---- the manifest ----

    [Fact]
    public void A_manifest_is_deterministic_hashed_and_valid_for_the_build_it_describes()
    {
        var (manifest, catalogue, compiled) = Release();
        var again = UpgradeManifestBuilder.Build(Notes(), Baseline100(), compiled, new MigrationReviewRegister(1, []));
        Assert.Equal(UpgradeJson.Serialise(manifest), UpgradeJson.Serialise(again));                    // the same bytes every time
        Assert.Equal(64, manifest.ManifestSha256.Length);
        Assert.Equal(UpgradeJson.Serialise(manifest), UpgradeJson.Serialise(UpgradeJson.Parse<UpgradeManifest>(UpgradeJson.Serialise(manifest))));   // reads back equal
        Assert.Empty(Codes(UpgradeManifestValidator.Validate(manifest, catalogue, compiled)));
        Assert.Equal(["Beta/2026_03_Added"], manifest.NewMigrations.Select(n => n.Module + "/" + n.Id));
        Assert.Equal(["Alpha", "Beta"], manifest.ModuleOrder);
        Assert.True(manifest.Requirements.VerifiedBackupRequired && manifest.Requirements.MaintenanceRequired);
    }

    [Fact]
    public void A_manifest_that_was_changed_after_it_was_produced_is_refused()
    {
        var (manifest, catalogue, compiled) = Release();
        var changed = manifest with { ReleaseId = "ecp-other" };
        Assert.Contains("UPM-002", Codes(UpgradeManifestValidator.Validate(changed, catalogue, compiled)));
        // Even a change with a recomputed hash is refused if it no longer describes the build.
        var forged = manifest with { BreakingChanges = [new("X", "y")] };
        forged = forged with { ManifestSha256 = UpgradeJson.ManifestHash(forged) };
        Assert.Empty(Codes(UpgradeManifestValidator.Validate(forged, catalogue, compiled)));            // notes are the author's to write...
        var lies = manifest with { TargetModules = manifest.TargetModules.Select(m => m with { Migrations = m.Migrations.Select(x => x with { Checksum = "ff" }).ToList() }).ToList() };
        lies = lies with { ManifestSha256 = UpgradeJson.ManifestHash(lies) };
        Assert.Contains("UPM-007", Codes(UpgradeManifestValidator.Validate(lies, catalogue, compiled)));     // ...but the inventory must be the inventory
    }

    [Fact]
    public void A_historical_migration_that_was_modified_or_removed_is_refused_wherever_it_is_noticed()
    {
        // Modified: the checksum of an already released migration differs from the frozen baseline.
        var (modified, catalogue, compiled) = Release(m => m.Select(x => x.Module == "Alpha" ? x with { Migrations = [x.Migrations[0] with { Checksum = "tampered" }, x.Migrations[1]] } : x).ToList());
        Assert.Contains("UPM-012", Codes(UpgradeManifestValidator.Validate(modified, catalogue, compiled)));
        // Removed: a migration the baseline had is gone from the target.
        var (removed, c2, comp2) = Release(m => m.Select(x => x.Module == "Alpha" ? Module("Alpha", 1, x.Migrations[0]) : x).ToList());
        Assert.Contains("UPM-011", Codes(UpgradeManifestValidator.Validate(removed, c2, comp2)));
        // The build and its manifest must belong together.
        var (manifest, c3, _) = Release();
        Assert.Contains("UPM-050", Codes(UpgradeManifestValidator.Validate(manifest, c3, Target(m => m.Select(x => x.Module == "Beta" ? Module("Beta", 2, x.Migrations[0], M("2026_03_Added", "different")) : x).ToList()))));
    }

    [Fact]
    public void A_migration_that_drops_or_rewrites_needs_a_recorded_review_and_a_manifest_cannot_waive_the_backup_or_maintenance()
    {
        Func<IReadOnlyList<LedgerModule>, IReadOnlyList<LedgerModule>> destructive = m => m.Select(x => x.Module == "Beta" ? Module("Beta", 2, x.Migrations[0], M("2026_03_Added", "bb", MigrationCategories.Destructive)) : x).ToList();
        var (unreviewed, catalogue, compiled) = Release(destructive);
        Assert.Contains("UPM-019", Codes(UpgradeManifestValidator.Validate(unreviewed, catalogue, compiled)));
        Assert.True(unreviewed.NewMigrations.Single().ReviewRequired);
        Assert.False(unreviewed.NewMigrations.Single().Reviewed);
        var register = new MigrationReviewRegister(1, [new("Beta", "2026_03_Added", [MigrationCategories.Destructive], "release", "Drops a column that no code reads.", "Backup rehearsal CI run")]);
        var (reviewed, c2, comp2) = Release(destructive, register);
        Assert.True(reviewed.NewMigrations.Single().Reviewed);
        Assert.Empty(Codes(UpgradeManifestValidator.Validate(reviewed, c2, comp2)));
        // A review for a different category does not cover it.
        var wrong = new MigrationReviewRegister(1, [new("Beta", "2026_03_Added", [MigrationCategories.DataRewrite], "release", "x", "y")]);
        Assert.False(Release(destructive, wrong).Manifest.NewMigrations.Single().Reviewed);
        // Nothing in a manifest may waive the verified backup, maintenance mode, or the rollback method.
        var (manifest, c3, comp3) = Release();
        var waived = manifest with { Requirements = manifest.Requirements with { VerifiedBackupRequired = false } };
        Assert.Contains("UPM-021", Codes(UpgradeManifestValidator.Validate(waived with { ManifestSha256 = UpgradeJson.ManifestHash(waived) }, c3, comp3)));
        var downgrade = manifest with { Rollback = manifest.Rollback with { Method = "downgrade" } };
        Assert.Contains("UPM-022", Codes(UpgradeManifestValidator.Validate(downgrade with { ManifestSha256 = UpgradeJson.ManifestHash(downgrade) }, c3, comp3)));
    }

    [Fact]
    public void A_manifest_with_a_secret_unsupported_source_or_unknown_version_is_refused()
    {
        var (manifest, catalogue, compiled) = Release();
        var secret = manifest with { OperationalActions = [new("OPA", "Set Password=Sup3rS3cret! before you start")] };
        Assert.Contains("UPM-040", Codes(UpgradeManifestValidator.Validate(secret with { ManifestSha256 = UpgradeJson.ManifestHash(secret) }, catalogue, compiled)));
        var unsupported = manifest with { SupportedSourceVersions = [] };
        Assert.Contains("UPM-006", Codes(UpgradeManifestValidator.Validate(unsupported with { ManifestSha256 = UpgradeJson.ManifestHash(unsupported) }, catalogue, compiled)));
        var notNewer = manifest with { TargetPlatformVersion = "0.9.0" };
        Assert.Contains("UPM-004", Codes(UpgradeManifestValidator.Validate(notNewer with { ManifestSha256 = UpgradeJson.ManifestHash(notNewer) }, catalogue, compiled)));
        var below = manifest with { MinimumSupportedSourceVersion = "1.0.1" };
        Assert.Contains("UPM-005", Codes(UpgradeManifestValidator.Validate(below with { ManifestSha256 = UpgradeJson.ManifestHash(below) }, catalogue, compiled)));
        var garbage = manifest with { SourcePlatformVersion = "not-a-version" };
        Assert.Contains("UPM-003", Codes(UpgradeManifestValidator.Validate(garbage with { ManifestSha256 = UpgradeJson.ManifestHash(garbage) }, catalogue, compiled)));
        Assert.Contains("UPM-008", Codes(UpgradeManifestValidator.Validate(manifest, new UpgradeCatalogue([], [manifest], new MigrationReviewRegister(1, []), []), compiled)));
        Assert.Throws<DomainException>(() => UpgradeJson.Parse<UpgradeManifest>(Encoding.UTF8.GetBytes("{\"formatVersion\":1,\"surprise\":true}")));      // unknown members are refused, not ignored
        Assert.Throws<DomainException>(() => UpgradeJson.Parse<UpgradeManifest>([]));
    }

    // ---- where an installation stands ----

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> Applied(params (string Module, string[] Ids)[] modules) => modules.ToDictionary(m => m.Module, m => (IReadOnlyList<string>)m.Ids);

    [Fact]
    public void An_installation_is_classified_as_empty_at_source_at_target_part_way_ahead_or_inconsistent_and_never_guessed_safe()
    {
        var (_, catalogue, compiled) = Release();
        string[] sources = ["1.0.0"];
        UpgradeState Detect(params (string, string[])[] a) => UpgradeStateDetector.Detect(Applied(a), compiled, catalogue, sources);
        Assert.Equal(UpgradeStateKind.Empty, Detect(("Alpha", []), ("Beta", [])).Kind);
        var atSource = Detect(("Alpha", ["2026_01_Initial", "2026_02_More"]), ("Beta", ["2026_01_Initial"]));
        Assert.Equal(UpgradeStateKind.AtSource, atSource.Kind);
        Assert.Equal("1.0.0", atSource.SourceVersion);
        Assert.Equal(["Beta/2026_03_Added"], atSource.Pending);
        Assert.Equal(UpgradeStateKind.AtTarget, Detect(("Alpha", ["2026_01_Initial", "2026_02_More"]), ("Beta", ["2026_01_Initial", "2026_03_Added"])).Kind);
        // Part-way: a consistent prefix that is no released version.
        var partial = Detect(("Alpha", ["2026_01_Initial"]), ("Beta", ["2026_01_Initial"]));
        Assert.Equal(UpgradeStateKind.Partial, partial.Kind);
        Assert.Equal(["Alpha", "Beta"], partial.PartialModules);
        // Ahead: a migration this build has never heard of means a newer version wrote the database.
        var ahead = Detect(("Alpha", ["2026_01_Initial", "2026_02_More", "2099_Future"]), ("Beta", ["2026_01_Initial"]));
        Assert.Equal(UpgradeStateKind.Ahead, ahead.Kind);
        Assert.Equal(["Alpha/2099_Future"], ahead.Unknown);
        Assert.Equal(UpgradeStateKind.Ahead, Detect(("Alpha", ["2026_01_Initial", "2026_02_More"]), ("Beta", ["2026_01_Initial"]), ("Gamma", ["x"])).Kind);   // a module this build does not have
        // A missing predecessor leaves a gap.
        var gap = Detect(("Alpha", ["2026_02_More"]), ("Beta", ["2026_01_Initial"]));
        Assert.Equal(UpgradeStateKind.Inconsistent, gap.Kind);
        Assert.Equal(["Alpha"], gap.Gaps);
        // The same state with no supported source listed is not "at source": an unknown path is not called safe.
        Assert.Equal(UpgradeStateKind.Partial, UpgradeStateDetector.Detect(Applied(("Alpha", ["2026_01_Initial", "2026_02_More"]), ("Beta", ["2026_01_Initial"])), compiled, catalogue, []).Kind);
    }

    [Fact]
    public void A_backup_is_judged_by_the_schema_it_holds_not_by_the_text_of_the_version_that_wrote_it()
    {
        var (_, catalogue, compiled) = Release();
        BackupManifest Backup(params (string Module, string[] Ids)[] modules) => new(1, "bk-20261016T010000Z-aaaaaaaa", "installation", DateTime.UtcNow, DateTime.UtcNow, "9.9.9", "Test", new("o", "o"), "r", "c", null!, new SchemaSection(modules.Select(m => new ModuleSchema(m.Module, m.Module.ToLowerInvariant(), m.Ids)).ToList(), 0, []), null!, null!, null!, null!, true, null!, [], "Completed", []);
        Assert.Equal("1.0.0", UpgradeBackups.SchemaVersionOf(Backup(("Alpha", ["2026_01_Initial", "2026_02_More"]), ("Beta", ["2026_01_Initial"])), catalogue, compiled, "1.1.0"));
        Assert.Equal("1.1.0", UpgradeBackups.SchemaVersionOf(Backup(("Alpha", ["2026_01_Initial", "2026_02_More"]), ("Beta", ["2026_01_Initial", "2026_03_Added"])), catalogue, compiled, "1.1.0"));
        Assert.Null(UpgradeBackups.SchemaVersionOf(Backup(("Alpha", ["2026_01_Initial"]), ("Beta", ["2026_01_Initial"])), catalogue, compiled, "1.1.0"));
    }

    // ---- what a migration does ----

    [Fact]
    public void Migrations_are_classified_by_what_they_do_to_existing_structure_data_security_and_constraints()
    {
        static (IReadOnlyList<string> Categories, IReadOnlyList<string> Objects) Of(params MigrationOperation[] ops) => MigrationInventory.Classify(ops);
        Assert.Equal([], Of(new CreateTableOperation { Name = "T", Schema = "s" }, new AddColumnOperation { Name = "C", Table = "T", Schema = "s", ClrType = typeof(int) }).Categories);
        Assert.Contains(MigrationCategories.Destructive, Of(new DropTableOperation { Name = "T", Schema = "s" }).Categories);
        Assert.Contains(MigrationCategories.Destructive, Of(new DropColumnOperation { Name = "C", Table = "T", Schema = "s" }).Categories);
        Assert.Contains(MigrationCategories.SchemaAlteration, Of(new AlterColumnOperation { Name = "C", Table = "T", Schema = "s", ClrType = typeof(string) }).Categories);
        Assert.Contains(MigrationCategories.SchemaAlteration, Of(new RenameTableOperation { Name = "T", NewName = "U", Schema = "s" }).Categories);
        Assert.Contains(MigrationCategories.IndexOrConstraint, Of(new CreateIndexOperation { Name = "IX", Table = "T", Schema = "s", Columns = ["C"] }).Categories);
        Assert.Contains(MigrationCategories.IndexOrConstraint, Of(new DropForeignKeyOperation { Name = "FK", Table = "T", Schema = "s" }).Categories);
        Assert.Contains(MigrationCategories.DataRewrite, Of(new UpdateDataOperation { Table = "T", Schema = "s", KeyColumns = ["Id"], KeyValues = new object[,] { { 1 } }, Columns = ["C"], Values = new object[,] { { 2 } } }).Categories);
        Assert.Contains(MigrationCategories.DataRewrite, Of(new SqlOperation { Sql = "UPDATE [contracts].[Contract] SET [A] = [B];" }).Categories);
        Assert.Contains(MigrationCategories.DataRewrite, Of(new SqlOperation { Sql = "INSERT INTO [contracts].[X] ([Id]) SELECT [Id] FROM [contracts].[Y];" }).Categories);
        Assert.Contains(MigrationCategories.Destructive, Of(new SqlOperation { Sql = "DROP TABLE [contracts].[Old];" }).Categories);
        Assert.Contains(MigrationCategories.RowLevelSecurity, Of(new SqlOperation { Sql = "CREATE SECURITY POLICY security.[P] ADD FILTER PREDICATE security.fn_TenantPredicate(TenantId) ON s.T;" }).Categories);
        // Defining a trigger or function is not rewriting data, even though its body mentions UPDATE and DELETE.
        var trigger = Of(new SqlOperation { Sql = "CREATE TRIGGER s.TR ON s.T INSTEAD OF UPDATE, DELETE AS BEGIN THROW 51000, 'append only', 1; END" });
        Assert.DoesNotContain(MigrationCategories.DataRewrite, trigger.Categories);
        Assert.Contains(MigrationCategories.RawSql, trigger.Categories);
        Assert.Contains("create table s.T", Of(new CreateTableOperation { Name = "T", Schema = "s" }).Objects);
        Assert.All(MigrationCategories.NeedReview, c => Assert.Contains(c, MigrationCategories.All));
        Assert.DoesNotContain(MigrationCategories.IndexOrConstraint, MigrationCategories.NeedReview);
    }

    // ---- the report, the phrases and the evidence ----

    private static ValidationCheck Check(string code, CheckOutcome outcome) => new(code, "t", outcome, CheckSeverity.Info, "d", "", "", "c");

    [Fact]
    public void The_pre_flight_outcome_fails_closed_blockers_win_then_not_verified_then_warnings_and_only_an_installation_at_source_can_apply()
    {
        Assert.Equal(PreflightOutcome.Ready, PreflightReport.Summarise([Check("a", CheckOutcome.Passed)]));
        Assert.Equal(PreflightOutcome.ReadyWithWarnings, PreflightReport.Summarise([Check("a", CheckOutcome.Passed), Check("b", CheckOutcome.Warning)]));
        Assert.Equal(PreflightOutcome.NotVerified, PreflightReport.Summarise([Check("a", CheckOutcome.Warning), Check("b", CheckOutcome.NotVerified)]));
        Assert.Equal(PreflightOutcome.Blocked, PreflightReport.Summarise([Check("a", CheckOutcome.NotVerified), Check("b", CheckOutcome.Failed)]));
        PreflightReport Report(PreflightOutcome o, UpgradeStateKind s) => new("x", "1.0.0", "1.1.0", null, null, s, DateTime.UtcNow, DateTime.UtcNow, o, [], [], "c", PreflightPhase.Apply);
        Assert.True(Report(PreflightOutcome.Ready, UpgradeStateKind.AtSource).CanApply);
        Assert.True(Report(PreflightOutcome.ReadyWithWarnings, UpgradeStateKind.AtSource).CanApply);
        Assert.False(Report(PreflightOutcome.Blocked, UpgradeStateKind.AtSource).CanApply);
        Assert.False(Report(PreflightOutcome.NotVerified, UpgradeStateKind.AtSource).CanApply);
        Assert.False(Report(PreflightOutcome.Ready, UpgradeStateKind.AtTarget).CanApply);
        Assert.False(Report(PreflightOutcome.Ready, UpgradeStateKind.Partial).CanApply);
    }

    [Fact]
    public void The_confirmation_phrases_name_the_versions_and_the_backup_so_they_cannot_be_typed_by_habit()
    {
        Assert.Equal("UPGRADE 1.0.0 TO 1.1.0", UpgradePhrases.For("1.0.0", "1.1.0"));
        Assert.Equal("REHEARSE UPGRADE FROM bk-20261016T010000Z-aaaaaaaa", RehearsalPhrases.For("bk-20261016T010000Z-aaaaaaaa"));
        Assert.Equal("PROVE ROLLBACK TO bk-20261016T010000Z-aaaaaaaa", RollbackPhrases.For("bk-20261016T010000Z-aaaaaaaa"));
        Assert.NotEqual(UpgradePhrases.For("1.0.0", "1.1.0"), UpgradePhrases.For("1.0.0", "1.2.0"));
    }

    [Fact]
    public void Upgrade_reports_neutralise_spreadsheet_formulas_hold_no_secret_and_are_refused_if_one_slips_in()
    {
        var evil = new ValidationCheck("UPF-999", "=HYPERLINK(\"http://evil\")", CheckOutcome.Warning, CheckSeverity.Low, "+cmd|' /C calc'!A1", "@SUM(1+1)", "-2+3", "c");
        var report = new PreflightReport("running installation", "1.0.0", "1.1.0", "abc", null, UpgradeStateKind.AtSource, DateTime.UtcNow, DateTime.UtcNow, PreflightOutcome.ReadyWithWarnings, [evil], [], "c", PreflightPhase.Advisory);
        var csv = Encoding.UTF8.GetString(UpgradeEvidence.PreflightCsv(report));
        foreach (var line in csv.Split('\n')) foreach (var cell in line.Split(',')) Assert.False(cell.TrimStart('"') is ['=', ..] or ['+', ..] or ['@', ..] or ['-', ..] && cell.Length > 1 && !char.IsDigit(cell.TrimStart('"')[1]), $"a cell could run as a formula: {cell}");
        // A secret typed into a technical detail is masked before it is written, and a document that still holds one is refused.
        var secretive = evil with { TechnicalDetail = "Server=db;Database=x;Password=Sup3rS3cret!;" };
        var json = Encoding.UTF8.GetString(UpgradeEvidence.ToJson(UpgradeEvidence.Preflight(report with { Checks = [secretive] })));
        Assert.DoesNotContain("Sup3rS3cret", json);
        Assert.Throws<DomainException>(() => UpgradeEvidence.ToJson(new { text = "Password=Sup3rS3cret!" }));
        Assert.Contains("not an approval for production use", UpgradeEvidence.Notice);
        Assert.Contains("Rolling back", UpgradeEvidence.Notice);
    }

    [Fact]
    public void Maintenance_causes_for_an_upgrade_cannot_be_left_by_accident_like_those_of_a_restore()
    {
        Assert.True(MaintenanceCauses.IsGuarded(MaintenanceCauses.UpgradeInProgress));
        Assert.True(MaintenanceCauses.IsGuarded(MaintenanceCauses.UpgradeFailed));
        Assert.True(MaintenanceCauses.IsGuarded(MaintenanceCauses.RestoreFailed));
        Assert.False(MaintenanceCauses.IsGuarded(MaintenanceCauses.Manual));
        Assert.True(new Ecp.Operations.MaintenanceStatus(true, MaintenanceCauses.UpgradeFailed, "x", DateTime.UtcNow, "op").RestoreRelated);
    }
}
