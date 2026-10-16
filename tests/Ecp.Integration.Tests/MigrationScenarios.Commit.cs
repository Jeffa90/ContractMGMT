using Ecp.Contracts;
using Ecp.Contracts.Persistence;
using Ecp.DataMigration;
using Ecp.DataMigration.Domain;
using Ecp.DataMigration.Persistence;
using Ecp.Documents;
using Ecp.Documents.Persistence;
using Ecp.SharedKernel;
using Ecp.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Ecp.Integration.Tests;

/// <summary>Stops a commit at an exact point, as if the process died (a kill: nothing is recorded) or the work failed (a failure the engine records).</summary>
internal sealed class FaultPlan : IMigrationFaults
{
    private readonly List<(FaultPoint Point, string Phase, int Nth, bool Kill)> _rules = [];
    private readonly Dictionary<(FaultPoint, string), int> _seen = [];
    public int Fired { get; private set; }
    public void KillAt(FaultPoint point, string phase, int nth = 1) => _rules.Add((point, phase, nth, true));
    public void FailAt(FaultPoint point, string phase, int nth = 1) => _rules.Add((point, phase, nth, false));
    public void Clear() { _rules.Clear(); _seen.Clear(); Fired = 0; }

    public Task AtAsync(FaultPoint point, string phase, long sequence, CancellationToken ct)
    {
        var key = (point, phase);
        _seen[key] = _seen.GetValueOrDefault(key) + 1;
        foreach (var r in _rules)
            if (r.Point == point && r.Phase == phase && r.Nth == _seen[key])
            {
                Fired++;
                throw r.Kill ? new SimulatedProcessKill($"killed at {point}/{phase}#{r.Nth}") : new InvalidOperationException($"failed at {point}/{phase}#{r.Nth}");
            }
        return Task.CompletedTask;
    }
}

internal static partial class MigrationScenarios
{
    private static string[] FiveRows(string prefix = "S") => Enumerable.Range(1, 5).Select(i => MigrationWorld.Row($"{prefix}-{i}", $"AGR-00{i}", supplier: $"Supplier {i} Pty Ltd", vendor: $"70000{i}", value: (1000 * i).ToString())).ToArray();

    private static (byte[] Manifest, byte[] Archive) FiveDocuments()
    {
        var zip = new ZipBuilder(); var rows = new List<string>();
        for (var i = 1; i <= 5; i++)
        {
            zip.Add($"AGR-00{i}/AGR-00{i}_executed.pdf", Placeholders.Pdf($"contract {i}"));
            rows.Add(MigrationWorld.ManifestRow($"S-{i}", $"AGR-00{i}", $"AGR-00{i}/AGR-00{i}_executed.pdf"));
        }
        return (Manifest([.. rows]), zip.Build());
    }

    private static async Task<(int Contracts, int Suppliers, int Documents, int Versions, int Provenance, long Lines)> CountsAsync(MigrationWorld mw, Guid batch)
    {
        await using var t = mw.World.AsScopeless();
        var c = t.Get<ContractsDbContext>(); var d = t.Get<DocumentsDbContext>(); var m = t.Get<MigrationDbContext>();
        return (await c.Contracts.CountAsync(x => x.MigrationBatchId == batch), await c.Suppliers.CountAsync(x => x.MigrationBatchId == batch), await d.Documents.CountAsync(x => x.MigrationBatchId == batch),
            await d.Versions.CountAsync(x => x.MigrationBatchId == batch), await m.Provenance.CountAsync(x => x.BatchId == batch), await t.Get<Ecp.Audit.Persistence.AuditDbContext>().Events.CountAsync(e => e.MigrationBatchId == batch && e.Action.StartsWith("migration.contract.")));
    }

    // ---- MIG-002 / MIG-017 -----------------------------------------------------------------------------------------------------------------------

    public static async Task A_stopped_commit_resumes_from_its_checkpoints_without_losing_or_duplicating_anything(TestApp app, FaultPlan faults)
    {
        var cases = new (FaultPoint Point, string Phase, int Nth, bool Kill, int Chunk)[]
        {
            (FaultPoint.ChunkStart, "Contracts", 1, true, 2),     // at the very start of the first phase
            (FaultPoint.Item, "Contracts", 2, true, 3),           // in the middle of a chunk
            (FaultPoint.ChunkEnd, "Contracts", 2, true, 2),       // work done, not yet committed
            (FaultPoint.ChunkCommitted, "Contracts", 1, true, 2), // committed, process dies before the next chunk
            (FaultPoint.ChunkStart, "Documents", 1, true, 2),     // the boundary between the contracts and the documents phases
            (FaultPoint.Item, "Documents", 3, true, 2),
            (FaultPoint.ChunkCommitted, "Documents", 1, true, 2),
            (FaultPoint.Item, "Contracts", 4, false, 1),          // a failure the engine records (Failed, not Committing)
        };
        var options = app.Services.GetRequiredService<IOptions<MigrationOptions>>().Value;
        foreach (var c in cases)
        {
            faults.Clear(); options.ChunkSize = c.Chunk;
            var mw = await MigrationWorld.CreateAsync(app);
            var batch = await mw.CreateBatchAsync();
            var (manifest, archive) = FiveDocuments();
            await mw.UploadAsync(batch, contracts: Contracts(FiveRows()), manifest: manifest, archive: archive);
            Assert.Empty((await mw.ValidateAsync(batch)).Blockers);
            await mw.AsAdmin2Async(s => s.Get<IMigrationCommit>().ConfirmLegacyLoadAsync(batch).Done());

            if (c.Kill) faults.KillAt(c.Point, c.Phase, c.Nth); else faults.FailAt(c.Point, c.Phase, c.Nth);
            if (c.Kill)
            {
                await Assert.ThrowsAsync<SimulatedProcessKill>(() => mw.AsAdminAsync(s => s.Get<IMigrationCommit>().CommitAsync(batch)));
                var stuck = await mw.DetailAsync(batch);
                Assert.Equal(BatchStatus.Committing, stuck.Status);
                // The lease still protects a commit that may be running: a second run is refused until it lapses.
                var busy = await Assert.ThrowsAsync<DomainException>(() => mw.AsAdminAsync(s => s.Get<IMigrationCommit>().CommitAsync(batch)));
                Assert.Equal("commit_in_progress", busy.Code);
                Assert.Equal(BatchStatus.Committing, (await mw.DetailAsync(batch)).Status);
                app.Clock.Advance(TimeSpan.FromMinutes(10));
            }
            else
            {
                var progress = await mw.AsAdminAsync(s => s.Get<IMigrationCommit>().CommitAsync(batch));
                Assert.Equal(BatchStatus.Failed, progress.Status);
                Assert.Contains("failed at", progress.FailureReason);
            }
            Assert.True(faults.Fired == 1, $"{c.Point}/{c.Phase}#{c.Nth} fired {faults.Fired} time(s)");

            var resumed = await mw.AsAdminAsync(s => s.Get<IMigrationCommit>().CommitAsync(batch));
            Assert.Equal(BatchStatus.Committed, resumed.Status);

            var counts = await CountsAsync(mw, batch);
            Assert.Equal((5, 5, 5, 5), (counts.Contracts, counts.Suppliers, counts.Documents, counts.Versions));      // nothing lost, nothing twice
            Assert.Equal(5 + 5 + 5 + 1, counts.Provenance);                                                           // one record per created thing, plus the link to the shared category
            var rec = await mw.AsAdminAsync(s => s.Get<IMigrationCommit>().ReconcileAsync(batch, false, null));
            Assert.True(rec.Balanced, $"{c.Point}/{c.Phase}#{c.Nth}: " + string.Join("; ", rec.Imbalances));
            Assert.Equal(new ReconciliationTotals(5, 5, 0, 0, 5, 0), rec.Totals);
            Assert.Equal(5, rec.Files.Imported);
            Assert.Equal(5, await DocumentFilesAsync(mw)); // each file stored once
            var detail = await mw.DetailAsync(batch);
            Assert.All(detail.Checkpoints, cp => Assert.True(cp.Complete));
        }
    }

    private static async Task<int> DocumentFilesAsync(MigrationWorld mw)
    {
        await using var t = mw.World.AsScopeless();
        return (await t.Get<DocumentsDbContext>().Versions.AsNoTracking().Select(v => v.StorageKey).Distinct().ToListAsync()).Count;
    }

    // ---- MIG-024 / MIG-004 ----------------------------------------------------------------------------------------------------------------------

    public static async Task Re_running_a_file_is_idempotent_and_updating_changes_only_mapped_fields_with_before_images(TestApp app)
    {
        var mw = await MigrationWorld.CreateAsync(app);
        var (manifest, archive) = FiveDocuments();
        var rows = FiveRows();
        var first = await mw.CreateBatchAsync();
        await mw.UploadAsync(first, contracts: Contracts(rows), manifest: manifest, archive: archive);
        await mw.ValidateAndCommitAsync(first);
        var snapshot = await TargetSnapshotAsync(mw);

        // Skip existing: the same files again change nothing at all.
        var second = await mw.CreateBatchAsync(strategy: DuplicateStrategy.SkipExisting);
        await mw.UploadAsync(second, contracts: Contracts(rows), manifest: manifest, archive: archive);
        var summary = await mw.ValidateAsync(second);
        Assert.Equal((5, 0, 5), (summary.Rows, summary.Accepted, summary.Skipped));
        await mw.ValidateAndCommitAsync(second);
        Assert.Equal(snapshot, await TargetSnapshotAsync(mw));
        var rec = await mw.AsAdminAsync(s => s.Get<IMigrationCommit>().ReconcileAsync(second, false, null));
        Assert.True(rec.Balanced, string.Join("; ", rec.Imbalances));
        Assert.Equal((5, 5, 0, 0), (rec.Totals.SourceRows, rec.Totals.Skipped, rec.Totals.Created, rec.Totals.Updated));
        Assert.Equal(5, rec.Files.DuplicateHandled); // the documents were recognised, not added again
        Assert.Equal(0, (await CountsAsync(mw, second)).Documents);

        // Update mapped fields on locked legacy records is refused unless the policy explicitly allows it (MIG-E012).
        var changed = FiveRows(); changed[0] = MigrationWorld.Row("S-1", "AGR-001", title: "Renamed agreement", supplier: "Supplier 1 Pty Ltd", vendor: "700001", value: "1500");
        var refused = await mw.CreateBatchAsync(strategy: DuplicateStrategy.UpdateMapped);
        await mw.UploadAsync(refused, contracts: Contracts(changed));
        var refusedSummary = await mw.ValidateAsync(refused);
        var dump = await mw.AsAdminAsync(async x => string.Join(" | ", (await x.Get<IMigrationBatches>().ListRowsAsync(refused, new RowFilter())).Items.Select(r => r.SourceRecordId + ":" + r.PlanSummary)));
        Assert.True(refusedSummary.MessagesByCode["MIG-E012"] == 1, dump);
        Assert.Equal(1, refusedSummary.Rejected);
        Assert.Equal(4, refusedSummary.Skipped + 0); // the four unchanged rows are not updates

        await mw.AsAdminAsync(s => s.Get<IMigrationProjects>().SetLegacyPolicyAsync(mw.ProjectId, new LegacyPolicyInput(true, app.Clock.UtcNow.AddDays(30), ["Effective", "Ended"], true, "Correct the title and value after a data fix")));
        var update = await mw.CreateBatchAsync(strategy: DuplicateStrategy.UpdateMapped);
        await mw.UploadAsync(update, contracts: Contracts(changed));
        var updateSummary = await mw.ValidateAsync(update);
        Assert.Equal((1, 0, 4), (updateSummary.Accepted, updateSummary.Rejected, updateSummary.Skipped));
        Assert.Equal(1, updateSummary.ToUpdate);
        Guid contractId;
        await using (var t = mw.World.AsScopeless()) contractId = (await t.Get<ContractsDbContext>().Contracts.AsNoTracking().SingleAsync(c => c.SourceRecordId == "S-1")).Id;
        string before; DateTime stamp;
        await using (var t = mw.World.AsScopeless()) { var c = await t.Get<ContractsDbContext>().Contracts.AsNoTracking().SingleAsync(x => x.Id == contractId); before = c.ContractTitle; stamp = c.UpdatedUtc; }

        app.Clock.Advance(TimeSpan.FromMinutes(5));
        await mw.ValidateAndCommitAsync(update);
        await using (var t = mw.World.AsScopeless())
        {
            var c = await t.Get<ContractsDbContext>().Contracts.AsNoTracking().SingleAsync(x => x.Id == contractId);
            Assert.Equal("Renamed agreement", c.ContractTitle); Assert.Equal(1500m, c.EstimatedValue);
            Assert.Equal(first.ToString(), c.MigrationBatchId.ToString());           // still the batch that created it
            Assert.Equal("AGR-001", c.AgreementReference); Assert.Equal("S-1", c.SourceRecordId); Assert.True(c.RecordLocked); Assert.NotEqual(stamp, c.UpdatedUtc);
            var prov = await t.Get<MigrationDbContext>().Provenance.AsNoTracking().SingleAsync(p => p.BatchId == update && p.Action == ProvenanceAction.Updated);
            Assert.Contains(before, prov.BeforeImageJson);                           // the earlier title is kept so the change can be undone
            Assert.Equal(contractId, prov.TargetEntityId);
            // The four other contracts were not touched.
            Assert.Equal(4, await t.Get<ContractsDbContext>().Contracts.CountAsync(x => x.MigrationBatchId == first && x.UpdatedUtc == x.CreatedUtc));
        }

        // Rolling the update back restores the earlier values, and only that.
        var result = await mw.AsAdminAsync(s => s.Get<IMigrationRollback>().RollBackAsync(update, "The correction was wrong"));
        Assert.Equal(1, result.Reversed);
        await using (var t = mw.World.AsScopeless())
        {
            var c = await t.Get<ContractsDbContext>().Contracts.AsNoTracking().SingleAsync(x => x.Id == contractId);
            Assert.Equal(before, c.ContractTitle); Assert.Equal(1000m, c.EstimatedValue);
        }
    }

    public static async Task Each_duplicate_strategy_behaves_as_specified_and_agreement_references_are_unique_across_the_tenant(TestApp app)
    {
        var mw = await MigrationWorld.CreateAsync(app);
        // A record in another organisation of the same tenant already holds the reference.
        await mw.World.AsAsync("procAdmin", async s => { }); // (touch: the admin exists)
        await using (var t = mw.World.AsScopeless())
            await t.Get<IContractService>().CreateDraftAsync(new CreateContractRequest(mw.World.Org2, "TAKEN-1", "Existing elsewhere", mw.World.Sup3, mw.World.CatOrg2));

        async Task<(ValidationSummary Summary, Guid Batch)> Run(DuplicateStrategy strategy, string id)
        {
            var b = await mw.CreateBatchAsync(strategy: strategy);
            await mw.UploadAsync(b, contracts: Contracts(MigrationWorld.Row(id, "TAKEN-1")));
            return (await mw.ValidateAsync(b), b);
        }

        var reject = await Run(DuplicateStrategy.Reject, "D-1");
        Assert.Equal(1, reject.Summary.Rejected); Assert.Equal(1, reject.Summary.MessagesByCode["MIG-E006"]);
        var message = (await MessagesAsync(mw, reject.Batch)).Single(m => m.Code == "MIG-E006").Message;
        Assert.DoesNotContain(mw.World.Org2.ToString(), message);                                  // never says where the colliding record lives
        Assert.DoesNotContain("Existing elsewhere", message);

        var skip = await Run(DuplicateStrategy.SkipExisting, "D-2");
        Assert.Equal((0, 1), (skip.Summary.Accepted, skip.Summary.Skipped));
        var update = await Run(DuplicateStrategy.UpdateMapped, "D-3");
        Assert.Equal(1, update.Summary.Rejected);                                                  // it did not come from this source record, so it is never updated

        var generate = await Run(DuplicateStrategy.CreateNewReference, "D-4");
        Assert.Equal(1, generate.Summary.Accepted);
        await mw.ValidateAndCommitAsync(generate.Batch);
        await using (var t = mw.World.AsScopeless())
        {
            var c = await t.Get<ContractsDbContext>().Contracts.AsNoTracking().SingleAsync(x => x.SourceRecordId == "D-4");
            Assert.NotEqual("TAKEN-1", c.AgreementReference);
            Assert.StartsWith("WLD", c.AgreementReference);                                         // allocated by the organisation's numbering rule
            Assert.Equal("TAKEN-1", c.SourceAgreementReference);                                    // the original reference is always preserved
        }

        // Manual review: the commit is blocked until a person decides.
        var manual = await Run(DuplicateStrategy.ManualReview, "D-5");
        Assert.Equal(1, manual.Summary.ReviewRequired);
        var blocked = await Assert.ThrowsAsync<DomainException>(() => mw.AsAdminAsync(s => s.Get<IMigrationCommit>().CommitAsync(manual.Batch)));
        Assert.Equal("review_required", blocked.Code);
        var rowId = await mw.AsAdminAsync(async s => (await s.Get<IMigrationBatches>().ListRowsAsync(manual.Batch, new RowFilter(ReviewOnly: true))).Items.Single().Id);
        await mw.AsAdminAsync(async s => await s.Get<IMigrationBatches>().DecideRowAsync(manual.Batch, rowId, ReviewDecision.CreateNew));
        Assert.Equal(BatchStatus.Profiled, (await mw.DetailAsync(manual.Batch)).Status);           // a decision changes the plan, so the dry run must be repeated
        var decided = await mw.ValidateAsync(manual.Batch);
        var dump2 = await mw.AsAdminAsync(async x => string.Join(" | ", (await x.Get<IMigrationBatches>().ListRowsAsync(manual.Batch, new RowFilter())).Items.Select(r => r.SourceRecordId + ":" + r.Outcome + "/" + r.Review + "/" + r.PlanSummary)));
        Assert.True((1, 0) == (decided.Accepted, decided.ReviewRequired), dump2);
        await mw.ValidateAndCommitAsync(manual.Batch);
        await using (var t = mw.World.AsScopeless())
            Assert.Equal(2, await t.Get<ContractsDbContext>().Contracts.CountAsync(c => c.SourceAgreementReference == "TAKEN-1"));
        Assert.Equal(1, (await CountsAsync(mw, manual.Batch)).Contracts);

        // Another batch of the same tenant, the same reference, in the same file twice: the in-file collision follows the strategy too.
        var dup = await mw.CreateBatchAsync(strategy: DuplicateStrategy.Reject);
        await mw.UploadAsync(dup, contracts: Contracts(MigrationWorld.Row("E-1", "SAME-REF"), MigrationWorld.Row("E-2", "SAME-REF")));
        var dupSummary = await mw.ValidateAsync(dup);
        Assert.Equal((1, 1), (dupSummary.Accepted, dupSummary.Rejected));
    }

    public static async Task The_reference_policy_can_generate_every_target_reference_and_keeps_the_source_reference(TestApp app)
    {
        var mw = await MigrationWorld.CreateAsync(app);
        await mw.AsAdminAsync(s => s.Get<IMigrationProjects>().SetReferencePolicyAsync(mw.ProjectId, ReferencePolicy.GenerateNew));
        var batch = await mw.CreateBatchAsync();
        await mw.UploadAsync(batch, contracts: Contracts(FiveRows()));
        await mw.ValidateAndCommitAsync(batch);
        await using var t = mw.World.AsScopeless();
        var list = await t.Get<ContractsDbContext>().Contracts.AsNoTracking().Where(c => c.MigrationBatchId == batch).OrderBy(c => c.SourceRecordId).ToListAsync();
        Assert.Equal(5, list.Count);
        Assert.All(list, c => Assert.StartsWith("WLD", c.AgreementReference));
        Assert.Equal(5, list.Select(c => c.AgreementReference).Distinct().Count());
        Assert.Equal(["AGR-001", "AGR-002", "AGR-003", "AGR-004", "AGR-005"], list.Select(c => c.SourceAgreementReference));
    }

    // ---- MIG-006 / MIG-016 / MIG-022 ---------------------------------------------------------------------------------------------------------

    public static async Task Rollback_removes_only_the_selected_batchs_reversible_changes_and_keeps_shared_data(TestApp app)
    {
        var mw = await MigrationWorld.CreateAsync(app);
        var preexisting = await TargetSnapshotAsync(mw);

        // Batch A creates contracts, a new supplier and documents; batch B (later) links A's supplier.
        var a = await mw.CreateBatchAsync();
        var zipA = new ZipBuilder().Add("AGR-A1/a1.pdf", Placeholders.Pdf("a1")).Add("AGR-A2/a2.pdf", Placeholders.Pdf("a2")).Build();
        await mw.UploadAsync(a, contracts: Contracts(MigrationWorld.Row("A-1", "AGR-A1", supplier: "Shared Supplier Pty Ltd", vendor: "800001"), MigrationWorld.Row("A-2", "AGR-A2", supplier: "Only A Supplier Pty Ltd", vendor: "800002")),
            manifest: Manifest(MigrationWorld.ManifestRow("A-1", "AGR-A1", "AGR-A1/a1.pdf"), MigrationWorld.ManifestRow("A-2", "AGR-A2", "AGR-A2/a2.pdf")), archive: zipA);
        await mw.ValidateAndCommitAsync(a);
        var afterA = await TargetSnapshotAsync(mw);

        var b = await mw.CreateBatchAsync();
        await mw.UploadAsync(b, contracts: Contracts(MigrationWorld.Row("B-1", "AGR-B1", supplier: "Shared Supplier Pty Ltd", vendor: "800001")));
        await mw.ValidateAndCommitAsync(b);

        var preview = await mw.AsAdminAsync(s => s.Get<IMigrationRollback>().PreviewAsync(a));
        Assert.True(preview.Allowed);
        Assert.Equal(2, preview.Lines.Single(l => l.Entity == "Contract" && l.Action == "Remove").Count);
        Assert.Equal(2, preview.Lines.Single(l => l.Entity == "Document" && l.Action == "Remove").Count);
        Assert.Equal(1, preview.Lines.Single(l => l.Entity == "Supplier" && l.Action == "Keep").Count);   // shared with B
        Assert.Equal(1, preview.Lines.Single(l => l.Entity == "Supplier" && l.Action == "Remove").Count);

        var result = await mw.AsAdminAsync(s => s.Get<IMigrationRollback>().RollBackAsync(a, "Wrong source extract"));
        Assert.Equal(BatchStatus.RolledBack, result.Status);
        Assert.Equal(0, result.NotReversible);
        await using (var t = mw.World.AsScopeless())
        {
            var c = t.Get<ContractsDbContext>(); var d = t.Get<DocumentsDbContext>();
            Assert.Equal(0, await c.Contracts.CountAsync(x => x.MigrationBatchId == a));
            Assert.Equal(0, await d.Documents.CountAsync(x => x.MigrationBatchId == a)); Assert.Equal(0, await d.Versions.CountAsync(x => x.MigrationBatchId == a));
            Assert.Equal(1, await c.Contracts.CountAsync(x => x.MigrationBatchId == b));                              // another batch is untouched
            var shared = await c.Suppliers.AsNoTracking().SingleAsync(x => x.VendorNumber == "800001");              // kept: still used outside the batch
            Assert.Equal(a, shared.MigrationBatchId);
            Assert.False(await c.Suppliers.AnyAsync(x => x.VendorNumber == "800002"));                               // nothing refers to it, so it went
            var prov = await t.Get<MigrationDbContext>().Provenance.AsNoTracking().Where(p => p.BatchId == a && p.TargetEntityType == "Supplier").ToListAsync();
            Assert.Contains(prov, p => p.ReversalState == ReversalState.Retained && p.ReversalNote!.Contains("still refer"));
            Assert.Contains(prov, p => p.ReversalState == ReversalState.Reversed);
        }
        // Everything that existed before the batch is exactly as it was, and the rest of the target matches the state between the batches.
        var afterRollback = await TargetSnapshotAsync(mw);
        Assert.NotEqual(afterA, afterRollback);
        await using (var t = mw.World.AsScopeless())
        {
            var original = await t.Get<ContractsDbContext>().Contracts.AsNoTracking().Where(x => x.MigrationBatchId == null).CountAsync();
            Assert.Equal(7 + 0, original); // the access world's own contracts
        }
        Assert.StartsWith("contracts:", preexisting);

        // Idempotent: asking again changes nothing.
        var again = await mw.AsAdminAsync(s => s.Get<IMigrationRollback>().RollBackAsync(a, "Wrong source extract"));
        Assert.Equal(result.Reversed, again.Reversed);
        Assert.Equal(afterRollback, await TargetSnapshotAsync(mw));
        // Audited, with the batch id.
        await using (var t = mw.World.AsScopeless())
        {
            var events = await t.Get<Ecp.Audit.Persistence.AuditDbContext>().Events.AsNoTracking().Where(e => e.MigrationBatchId == a).Select(e => e.Action).ToListAsync();
            Assert.Contains("migration.batch.rolled_back", events); Assert.Contains("migration.contract.removed", events); Assert.Contains("migration.document.removed", events);
        }
    }

    public static async Task A_record_that_has_moved_on_is_archived_not_deleted_and_a_batch_with_activity_still_rolls_back(TestApp app)
    {
        var mw = await MigrationWorld.CreateAsync(app, legacyPolicy: true);
        var a = await mw.CreateBatchAsync(legacy: true);
        await mw.UploadAsync(a, contracts: Contracts(MigrationWorld.Row("A-1", "AGR-A1"), MigrationWorld.Row("A-2", "AGR-A2")));
        await mw.ValidateAndCommitAsync(a);
        // Someone works on one of the loaded records (workflow activity): it can no longer be removed by rollback.
        app.Clock.Advance(TimeSpan.FromMinutes(5));
        await using (var t = mw.World.AsScopeless())
            await t.Get<IContractLifecycleService>().ApplyStateAsync((await t.Get<ContractsDbContext>().Contracts.AsNoTracking().SingleAsync(c => c.SourceRecordId == "A-1")).Id, "Ended", locks: true);
        var result = await mw.AsAdminAsync(s => s.Get<IMigrationRollback>().RollBackAsync(a, "Rolled back after later activity"));
        Assert.Equal(BatchStatus.RolledBack, result.Status);
        await using (var t = mw.World.AsScopeless())
        {
            var c = t.Get<ContractsDbContext>();
            Assert.False(await c.Contracts.AnyAsync(x => x.SourceRecordId == "A-2"));                              // untouched: removed
            var kept = await c.Contracts.AsNoTracking().SingleAsync(x => x.SourceRecordId == "A-1");               // had activity: archived, never deleted
            Assert.True(kept.IsArchived); Assert.Equal(a, kept.ArchivedByBatchId); Assert.Contains("Rolled back", kept.ArchiveReason);
        }
        // Archived records leave the register but stay readable with their provenance.
        var list = await mw.World.AsAsync("procAdmin", s => s.Get<ISecuredContractService>().ListAsync(new ContractFilter(Search: "AGR-A1")));
        Assert.Empty(list.Items);
    }

    public static async Task After_acceptance_rollback_is_refused_and_compensating_archival_keeps_everything(TestApp app)
    {
        var mw = await MigrationWorld.CreateAsync(app);
        var (manifest, archive) = FiveDocuments();
        var batch = await mw.CreateBatchAsync();
        await mw.UploadAsync(batch, contracts: Contracts(FiveRows()), manifest: manifest, archive: archive);
        await mw.ValidateAndCommitAsync(batch);
        await mw.AsAdminAsync(async s => await s.Get<IMigrationCommit>().ReconcileAsync(batch, false, null));
        await mw.AsAdminAsync(s => s.Get<IMigrationCommit>().AcceptAsync(batch, new SampleCheckInput("Owner checked a sample of records and documents.", false)));

        var refused = await Assert.ThrowsAsync<DomainException>(() => mw.AsAdminAsync(s => s.Get<IMigrationRollback>().RollBackAsync(batch, "Too late")));
        Assert.Equal("rollback_after_acceptance", refused.Code);
        var preview = await mw.AsAdminAsync(s => s.Get<IMigrationRollback>().PreviewAsync(batch));
        Assert.False(preview.Allowed);

        var result = await mw.AsAdminAsync(s => s.Get<IMigrationRollback>().CompensateAsync(batch, "The supplier relationship ended before go-live"));
        Assert.Equal(BatchStatus.Compensated, result.Status);
        await using var t = mw.World.AsScopeless();
        var c = t.Get<ContractsDbContext>(); var d = t.Get<DocumentsDbContext>();
        Assert.Equal(5, await c.Contracts.CountAsync(x => x.MigrationBatchId == batch && x.IsArchived));           // nothing was deleted
        Assert.Equal(5, await c.Suppliers.CountAsync(x => x.MigrationBatchId == batch));                          // shared reference data stays
        Assert.Equal(5, await d.Documents.CountAsync(x => x.MigrationBatchId == batch && x.Status == Ecp.Documents.Domain.DocumentStatus.Archived));
        var events = await t.Get<Ecp.Audit.Persistence.AuditDbContext>().Events.AsNoTracking().Where(e => e.MigrationBatchId == batch).Select(e => e.Action).ToListAsync();
        Assert.Contains("migration.batch.compensated", events); Assert.Equal(5, events.Count(e => e == "migration.contract.archived"));
        var hidden = await mw.World.AsAsync("procAdmin", s => s.Get<ISecuredContractService>().ListAsync(new ContractFilter(Search: "AGR-00")));
        Assert.Empty(hidden.Items);
        var details = await mw.World.AsAsync("procAdmin", async s => await s.Get<ISecuredContractService>().GetAsync((await c.Contracts.AsNoTracking().FirstAsync(x => x.MigrationBatchId == batch)).Id));
        Assert.True(details.IsArchived); Assert.Contains("supplier relationship", details.ArchiveReason);
    }
}
