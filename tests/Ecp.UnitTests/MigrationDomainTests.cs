using System.Text.RegularExpressions;
using Ecp.DataMigration.Domain;
using Ecp.SharedKernel;
using Ecp.Tests.Common;
using Xunit;

namespace Ecp.UnitTests;

[Trait("Category", "Migration")]
public class MigrationLifecycleTests
{
    private static readonly DateTime Now = new(2026, 10, 16, 1, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Alice = Guid.NewGuid(), Bob = Guid.NewGuid();

    private static MigrationProject Project(bool legacy = true)
    {
        var p = MigrationProject.Create(Guid.NewGuid(), Guid.NewGuid(), "Load", "Source", "1", "owner", Now.AddDays(-1), Now, "Alice");
        if (legacy) p.EnableLegacyLoad(true, Now.AddDays(30), Now, ["Ended", "Effective", "Effective"]);
        return p;
    }

    private static MigrationBatch Validated(MigrationProject p, bool legacy = true, BatchKind kind = BatchKind.Final)
    {
        var b = p.CreateBatch(kind, DuplicateStrategy.SkipExisting, DocumentDuplicateOption.AddVersion, legacy, Guid.NewGuid(), Guid.NewGuid(), Now, "Alice", Alice, "Batch", 3);
        b.InputsChanged(hasSources: true);
        b.MarkValidated("fp-1", "{}", Now);
        return b;
    }

    [Fact] // MIG-015
    public void A_legacy_policy_lists_its_allowed_states_sorted_without_duplicates_and_overwrite_needs_a_reason()
    {
        var p = Project();
        Assert.Equal(["Effective", "Ended"], p.LegacyAllowedStatuses);
        Assert.Equal("invalid_policy", Assert.Throws<DomainException>(() => p.EnableLegacyLoad(true, Now.AddDays(1), Now, ["Effective"], allowOverwriteLocked: true)).Code);
        p.EnableLegacyLoad(false, Now.AddDays(1), Now, ["Effective"], true, "Re-load after a data fix");
        Assert.True(p.LegacyAllowOverwriteLocked); Assert.Equal("Re-load after a data fix", p.LegacyOverwriteReason);
        p.Close();
        Assert.False(p.LegacyLoadEnabled); Assert.Equal("project_closed", Assert.Throws<DomainException>(() => p.SetReferencePolicy(ReferencePolicy.GenerateNew)).Code);
        Assert.Equal("project_closed", Assert.Throws<DomainException>(() => p.CreateBatch(BatchKind.Mock, DuplicateStrategy.Reject, DocumentDuplicateOption.AddVersion, false, null, null, Now)).Code);
    }

    [Fact] // MIG-026
    public void The_target_freeze_needs_a_window_and_is_in_force_only_inside_it()
    {
        var p = Project(legacy: false);
        Assert.Equal("invalid_freeze", Assert.Throws<DomainException>(() => p.SetTargetFreeze(true)).Code);
        p.SetFreezeWindow(Now, Now.AddHours(8));
        p.SetTargetFreeze(true);
        Assert.True(p.FreezeInForceAt(Now.AddHours(1)));
        Assert.False(p.FreezeInForceAt(Now.AddHours(8)));   // the end is exclusive
        Assert.False(p.FreezeInForceAt(Now.AddMinutes(-1)));
        p.SetTargetFreeze(false);
        Assert.False(p.FreezeInForceAt(Now.AddHours(1)));
    }

    [Fact] // MIG-015
    public void A_legacy_commit_needs_a_second_persons_confirmation_of_exactly_what_was_validated()
    {
        var p = Project(); var b = Validated(p);
        Assert.Equal("confirmation_required", Assert.Throws<DomainException>(() => b.StartCommit(Now, "run-1", TimeSpan.FromMinutes(2), "Alice", Alice)).Code);
        b.ConfirmLegacyLoad(Alice, "Alice", Now);
        Assert.Equal("second_person_required", Assert.Throws<DomainException>(() => b.StartCommit(Now, "run-1", TimeSpan.FromMinutes(2), "Alice", Alice)).Code); // the confirmer cannot also commit
        b.StartCommit(Now, "run-1", TimeSpan.FromMinutes(2), "Bob", Bob);
        Assert.Equal(BatchStatus.Committing, b.Status);

        // Changing what was validated withdraws the confirmation.
        var b2 = Validated(p); b2.ConfirmLegacyLoad(Alice, "Alice", Now);
        b2.MarkValidated("fp-2", "{}", Now.AddMinutes(1));
        Assert.False(b2.HasValidConfirmation);
        b2.InputsChanged(true); b2.MarkValidated("fp-2", "{}", Now);
        Assert.Equal("confirmation_required", Assert.Throws<DomainException>(() => b2.StartCommit(Now, "run-1", TimeSpan.FromMinutes(2), "Bob", Bob)).Code);
        Assert.Equal("not_legacy_load", Assert.Throws<DomainException>(() => Validated(p, legacy: false).ConfirmLegacyLoad(Alice, "Alice", Now)).Code);
    }

    [Fact] // MIG-002 / MIG-017
    public void A_commit_holds_a_lease_that_stops_a_second_run_until_it_expires_then_resume_is_allowed()
    {
        var b = Validated(Project(false), legacy: false);
        b.StartCommit(Now, "run-1", TimeSpan.FromMinutes(2), "Bob", Bob);
        Assert.Equal("commit_in_progress", Assert.Throws<DomainException>(() => b.StartCommit(Now.AddMinutes(1), "run-2", TimeSpan.FromMinutes(2), "Bob", Bob)).Code);
        b.RenewLease("run-1", Now.AddMinutes(1), TimeSpan.FromMinutes(2));
        Assert.Equal("lease_lost", Assert.Throws<DomainException>(() => b.RenewLease("run-2", Now, TimeSpan.FromMinutes(1))).Code);
        Assert.Equal("commit_in_progress", Assert.Throws<DomainException>(() => b.EnsureCanRollBack(Now.AddMinutes(2))).Code);
        // The process died: after the lease expires another run takes over from the checkpoints.
        b.StartCommit(Now.AddMinutes(10), "run-2", TimeSpan.FromMinutes(2), "Bob", Bob);
        Assert.Equal("run-2", b.LeaseOwner);
        b.CompleteCommit(Now.AddMinutes(11));
        Assert.Null(b.LeaseOwner); Assert.Equal(BatchStatus.Committed, b.Status);
    }

    [Fact]
    public void Inputs_freeze_once_a_commit_starts_and_a_validation_result_is_dropped_when_inputs_change()
    {
        var b = Validated(Project(false), legacy: false);
        Assert.Equal(BatchStatus.Validated, b.Status);
        b.InputsChanged(true);
        Assert.Equal(BatchStatus.Profiled, b.Status); Assert.Null(b.ValidationFingerprint);
        b.MarkValidated("fp", "{}", Now);
        b.StartCommit(Now, "run", TimeSpan.FromMinutes(1), "Bob", Bob);
        Assert.Equal("batch_locked", Assert.Throws<DomainException>(() => b.InputsChanged(true)).Code);
        Assert.Equal("batch_locked", Assert.Throws<DomainException>(() => b.CapturePinnedVersions(Guid.NewGuid(), null, null)).Code);
        Assert.Equal("batch_locked", Assert.Throws<DomainException>(() => b.SetStrategies(DuplicateStrategy.Reject, DocumentDuplicateOption.RejectDuplicateHash)).Code);
    }

    [Fact] // MIG-022
    public void Rollback_is_refused_after_acceptance_and_compensation_is_the_path_afterwards()
    {
        var b = Validated(Project(false), legacy: false);
        b.StartCommit(Now, "run", TimeSpan.FromMinutes(1), "Bob", Bob); b.CompleteCommit(Now);
        b.Reconcile(new ReconciliationTotals(2, 2, 0, 0, 2, 0), false);
        b.Accept("Owner", "Checked a sample of three records.", Now);
        Assert.Equal("rollback_after_acceptance", Assert.Throws<DomainException>(() => b.EnsureCanRollBack(Now)).Code);
        Assert.Equal("rollback_after_acceptance", Assert.Throws<DomainException>(() => b.CompleteRollback("why", "Bob", Now)).Code);
        b.CompleteCompensation("Supplier dispute", "Bob", Now);
        Assert.Equal(BatchStatus.Compensated, b.Status);
        b.CompleteCompensation("again", "Bob", Now); // idempotent
    }

    [Fact]
    public void File_resolutions_are_recorded_with_the_person_and_a_discard_needs_a_reason()
    {
        var f = StagedFile.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "a/b.pdf", "b.pdf", "pdf", new string('a', 64), 10, "t/x/aa/key", "application/pdf", FileScan.Clean, FileState.Staged, null);
        f.RecordMatch(FileState.Unmatched, QueueReason.NoMatch, null, null, null, null, "No match.");
        f.ResolveManually(Guid.NewGuid(), "Alice", Now);
        Assert.Equal((FileState.Matched, MatchMethod.Manual, "Alice"), (f.State, f.MatchedBy, f.ResolvedBy));
        Assert.Equal("required", Assert.Throws<DomainException>(() => f.Discard(" ", "Alice", Now)).Code);
        f.Discard("Duplicate scan of a letter", "Alice", Now);
        Assert.Equal(FileState.Discarded, f.State);
        f.RecordMatch(FileState.Matched, QueueReason.None, null, Guid.NewGuid(), MatchMethod.FolderName, null, null);
        Assert.Equal(FileState.Discarded, f.State); // a settled file is never moved by a later validation run
        f.MarkImported(Guid.NewGuid());
        Assert.Equal("file_imported", Assert.Throws<DomainException>(() => StagedFile.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "x.pdf", "x.pdf", "pdf", null, 1, null, null, FileScan.Clean, FileState.Imported, null).Discard("r", "A", Now)).Code);
    }

    [Fact]
    public void A_published_profile_version_is_immutable_and_review_decisions_survive_revalidation()
    {
        var profile = MappingProfile.Create(Guid.NewGuid(), Guid.NewGuid(), "P", "S", null, "Alice", Now);
        var v1 = profile.NewVersion("{}", "h1", "Alice", Now, null);
        v1.ReplaceDefinition("{\"a\":1}", "h2");
        v1.Publish("Alice", Now);
        Assert.Equal("profile_version_published", Assert.Throws<DomainException>(() => v1.ReplaceDefinition("{}", "h3")).Code);
        Assert.Equal(2, profile.NewVersion("{}", "h", "Alice", Now, null).VersionNumber);

        var row = StagingRow.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, 2, "{}", "hash");
        row.RecordValidation(null, "S1", "R1", "k", RowOutcome.Pending, PlannedAction.None, null, 0, 0, "dup", reviewApplicable: true);
        Assert.Equal(ReviewDecision.Required, row.ReviewDecision);
        row.Decide(ReviewDecision.Skip);
        row.RecordValidation(null, "S1", "R1", "k", RowOutcome.Skipped, PlannedAction.Skip, null, 0, 0, "skipped", reviewApplicable: true);
        Assert.Equal(ReviewDecision.Skip, row.ReviewDecision);          // kept while the question still applies
        row.RecordValidation(null, "S1", "R1", "k", RowOutcome.Accepted, PlannedAction.Create, null, 0, 0, "ok", reviewApplicable: false);
        Assert.Equal(ReviewDecision.None, row.ReviewDecision);          // dropped when it does not
        Assert.Equal("no_review_needed", Assert.Throws<DomainException>(() => row.Decide(ReviewDecision.Skip)).Code);
    }
}

[Trait("Category", "Migration")]
public class MigrationCatalogueTests
{
    [Fact]
    public void Every_message_code_the_code_can_produce_is_documented_in_the_message_catalogue()
    {
        var root = RepoRoot.Find();
        var catalogue = File.ReadAllText(Path.Combine(root, "docs", "MIGRATION.md")) + File.ReadAllText(Path.Combine(root, "requirements", "09-migration", "MIGRATION_SUPPLEMENT.md"));
        var used = Directory.EnumerateFiles(Path.Combine(root, "src", "Modules", "Ecp.DataMigration"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}"))
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), "MIG-[EWF][0-9]{3}").Select(m => m.Value)).Distinct().OrderBy(x => x).ToList();
        Assert.True(used.Count >= 30, "the migration code uses many catalogue codes");
        var missing = used.Where(code => !catalogue.Contains($"| {code} |", StringComparison.Ordinal)).ToList();
        Assert.True(missing.Count == 0, "Message codes used in code but absent from docs/MIGRATION.md: " + string.Join(", ", missing));
    }
}
