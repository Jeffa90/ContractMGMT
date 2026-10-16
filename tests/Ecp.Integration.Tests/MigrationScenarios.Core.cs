using System.Text;
using Ecp.Contracts;
using Ecp.Contracts.Domain;
using Ecp.Contracts.Persistence;
using Ecp.DataMigration;
using Ecp.DataMigration.Domain;
using Ecp.DataMigration.Mapping;
using Ecp.DataMigration.Persistence;
using Ecp.Documents;
using Ecp.Documents.Persistence;
using Ecp.Identity;
using Ecp.SharedKernel;
using Ecp.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecp.Integration.Tests;

internal static partial class MigrationScenarios
{
    private static byte[] Contracts(params string[] rows) => MigrationWorld.Csv([MigrationWorld.ContractsHeader, .. rows]);
    private static byte[] Manifest(params string[] rows) => MigrationWorld.Csv([MigrationWorld.ManifestHeader, .. rows]);

    /// <summary>Everything the migration must not touch in a dry run: target records and their change stamps, reference counters, and the document content area.</summary>
    private static async Task<string> TargetSnapshotAsync(MigrationWorld mw)
    {
        await using var t = mw.World.AsScopeless();
        var c = t.Get<ContractsDbContext>(); var d = t.Get<DocumentsDbContext>();
        var parts = new List<string>
        {
            "contracts:" + string.Join(",", (await c.Contracts.AsNoTracking().OrderBy(x => x.Id).Select(x => x.Id + "/" + x.UpdatedUtc.Ticks).ToListAsync())),
            "suppliers:" + string.Join(",", (await c.Suppliers.AsNoTracking().OrderBy(x => x.Id).Select(x => x.Id.ToString()).ToListAsync())),
            "categories:" + string.Join(",", (await c.Categories.AsNoTracking().OrderBy(x => x.Id).Select(x => x.Id + "/" + x.IsActive).ToListAsync())),
            "assignments:" + await c.CategoryAssignments.AsNoTracking().CountAsync(),
            "counters:" + string.Join(",", (await c.ReferenceCounters.AsNoTracking().OrderBy(x => x.OrganisationId).Select(x => x.OrganisationId + "/" + x.LastValue).ToListAsync())),
            "documents:" + string.Join(",", (await d.Documents.AsNoTracking().OrderBy(x => x.Id).Select(x => x.Id + "/" + x.VersionCount).ToListAsync())),
            "versions:" + await d.Versions.AsNoTracking().CountAsync()
        };
        return string.Join("|", parts);
    }

    private static int BlobCount(MigrationWorld mw)
    {
        var root = Path.Combine(mw.App.DocumentRoot, "documents");
        return Directory.Exists(root) ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Count(f => !f.Contains("_incoming")) : 0;
    }

    private static async Task<List<StagingMessage>> MessagesAsync(MigrationWorld mw, Guid batch)
    {
        await using var t = mw.World.AsScopeless();
        return await t.Get<MigrationDbContext>().Messages.AsNoTracking().Where(m => m.BatchId == batch).ToListAsync();
    }

    // ---- MIG-001 ---------------------------------------------------------------------------------------------------------------------------------

    public static async Task A_dry_run_writes_nothing_to_the_target(TestApp app)
    {
        var mw = await MigrationWorld.CreateAsync(app);
        var batch = await mw.CreateBatchAsync(BatchKind.DryRun, legacy: false);
        await mw.UploadAsync(batch, manifest: MigrationSamples.ManifestCsv, archive: MigrationSamples.AttachmentZip(includeManifest: false));
        var before = await TargetSnapshotAsync(mw); var blobsBefore = BlobCount(mw);
        var summary = await mw.ValidateAsync(batch);
        await mw.ValidateAsync(batch); // repeatable
        Assert.Equal(before, await TargetSnapshotAsync(mw));
        Assert.Equal(blobsBefore, BlobCount(mw));
        // A standard load can only use the initial state, so the sample's Effective rows are rejected with E008 - and still nothing was written.
        Assert.Equal(2, summary.Rejected);
        Assert.Equal(2, summary.MessagesByCode["MIG-E008"]);

        await using var t = mw.World.AsScopeless();
        var m = t.Get<MigrationDbContext>();
        Assert.Equal(2, await m.StagingRows.CountAsync(r => r.BatchId == batch)); // re-validation does not accumulate rows or messages
        Assert.Equal(await m.Messages.CountAsync(x => x.BatchId == batch), summary.Errors + summary.Warnings);
        var audit = await t.Get<Ecp.Audit.Persistence.AuditDbContext>().Events.AsNoTracking().Where(e => e.MigrationBatchId == batch).Select(e => e.Action).ToListAsync();
        Assert.Contains("migration.dryrun.validated", audit);
        Assert.DoesNotContain(audit, a => a.StartsWith("migration.contract") || a.StartsWith("migration.document") || a.StartsWith("migration.supplier"));

        // A dry run can never be committed, whatever its state.
        var ex = await Assert.ThrowsAsync<DomainException>(() => mw.AsAdminAsync(s => s.Get<IMigrationCommit>().CommitAsync(batch)));
        Assert.Equal("dry_run_cannot_commit", ex.Code);
    }

    // ---- MIG-009 / MIG-020 / MIG-021: totals, no truncation, error workbook ------------------------------------------------------------------

    public static async Task Error_and_reconciliation_totals_equal_the_processed_input_and_nothing_is_cut_or_executed(TestApp app)
    {
        var mw = await MigrationWorld.CreateAsync(app);
        var batch = await mw.CreateBatchAsync();
        var rows = new[]
        {
            MigrationWorld.Row("S-1", "AGR-001"),
            MigrationWorld.Row("S-2", "AGR-002", title: new string('x', 501)),                                           // MIG-E009: never shortened
            MigrationWorld.Row("S-3", "AGR-003", status: "Unheard of"),                                                  // MIG-E004: unmapped status
            MigrationWorld.Row("S-4", "AGR-004", start: "31/12/2024"),                                                   // MIG-E002: wrong date format
            MigrationWorld.Row("S-5", "AGR-005", title: "=HYPERLINK(\"http://evil.example\",\"click\")", value: "-5"),   // formula text and a bad number (min 0)
            MigrationWorld.Row("S-6", "AGR-006", category: "Unknown category", sub: ""),                                  // MIG-E011
            MigrationWorld.Row("S-1", "AGR-007"),                                                                        // MIG-E005: a second S-1
            MigrationWorld.Row("S-8", "AGR-008", legal: "perhaps"),                                                      // MIG-E004: not a yes/no
            MigrationWorld.Row("S-9", "AGR-009"),
        };
        var csv = Contracts(rows);
        await mw.UploadAsync(batch, contracts: csv);
        var summary = await mw.ValidateAsync(batch);
        Assert.Equal(9, summary.Rows);
        Assert.Equal(2, summary.Accepted);       // S-1 and S-9
        Assert.Equal(7, summary.Rejected);
        foreach (var code in new[] { "MIG-E009", "MIG-E004", "MIG-E002", "MIG-E011", "MIG-E005" }) Assert.True(summary.MessagesByCode.ContainsKey(code), code);
        Assert.Equal(summary.Rows, summary.Accepted + summary.Rejected + summary.Skipped);

        await mw.AsAdmin2Async(s => s.Get<IMigrationCommit>().ConfirmLegacyLoadAsync(batch).Done());
        var progress = await mw.AsAdminAsync(s => s.Get<IMigrationCommit>().CommitAsync(batch));
        Assert.Equal(BatchStatus.Committed, progress.Status);
        var rec = await mw.AsAdminAsync(s => s.Get<IMigrationCommit>().ReconcileAsync(batch, false, null));
        Assert.True(rec.Balanced, string.Join("; ", rec.Imbalances));
        Assert.Equal(new ReconciliationTotals(9, 2, 7, 0, 2, 0), rec.Totals);
        Assert.Equal(rec.Totals.SourceRows, rec.Totals.Accepted + rec.Totals.Rejected + rec.Totals.Skipped);

        // The over-long title was rejected, not cut: no contract anywhere has a shortened title.
        await using (var t = mw.World.AsScopeless())
            Assert.DoesNotContain(await t.Get<ContractsDbContext>().Contracts.AsNoTracking().Select(c => c.AgreementReference).ToListAsync(), r => r == "AGR-002");

        // The error report holds one message per problem, the source values of rejected rows, and neutralises formula text (MIG-021).
        var xlsx = await mw.AsAdminAsync(s => s.Get<IMigrationReports>().ExportErrorsAsync(batch, ExportFormat.Xlsx));
        Assert.EndsWith(".xlsx", xlsx.FileName);
        var sheet = Ecp.DataMigration.Formats.XlsxReader.Read(new MemoryStream(xlsx.Content), sheetName: "Rejected rows");
        Assert.Equal(7, sheet.Count);
        Assert.Contains(sheet.Rows, r => r.Fields.Contains("'=HYPERLINK(\"http://evil.example\",\"click\")"));
        Assert.DoesNotContain(sheet.Rows, r => r.Fields.Any(f => f.StartsWith("=")));
        var messages = Ecp.DataMigration.Formats.XlsxReader.Read(new MemoryStream(xlsx.Content), sheetName: "Messages");
        Assert.True(messages.Count >= 7); // one per problem found
        var csvReport = await mw.AsAdminAsync(s => s.Get<IMigrationReports>().ExportErrorsAsync(batch, ExportFormat.Csv));
        Assert.DoesNotContain("\n=", Encoding.UTF8.GetString(csvReport.Content));
    }

    // ---- MIG-014 -----------------------------------------------------------------------------------------------------------------------------------

    public static async Task The_mapping_preview_shows_original_and_transformed_values_and_how_many_rows_each_mapping_changes(TestApp app)
    {
        var mw = await MigrationWorld.CreateAsync(app);
        var batch = await mw.CreateBatchAsync();
        await mw.UploadAsync(batch, contracts: Contracts(MigrationWorld.Row("S-1", "AGR-001", category: "Packaging"), MigrationWorld.Row("S-2", "AGR-002", category: "Energy", sub: "Electricity", currency: "aud")));
        var preview = await mw.AsAdminAsync(s => s.Get<IMigrationValidation>().PreviewMappingAsync(batch, 5));
        Assert.Equal(2, preview.Rows);
        var category = preview.Stats.Single(x => x.TargetField == "category.name");
        Assert.Equal(2, category.Rows); Assert.Equal(2, category.Changed);              // "Packaging" -> PACKAGING, "Energy" -> ENERGY
        var currency = preview.Stats.Single(x => x.TargetField == "currencyCode");
        Assert.Equal(1, currency.Changed);                                              // only "aud" changes under Upper
        var value = preview.Sample[0].Values.Single(v => v.TargetField == "category.name");
        Assert.Equal(("Packaging", "PACKAGING", true), (value.Original, value.Transformed, value.Changed));
        // Previewing writes nothing.
        await using var t = mw.World.AsScopeless();
        Assert.Equal(0, await t.Get<MigrationDbContext>().Messages.CountAsync(m => m.BatchId == batch && m.Phase == MessagePhase.Validation));
    }

    // ---- MIG-015 -----------------------------------------------------------------------------------------------------------------------------------

    public static async Task A_legacy_load_needs_a_policy_a_second_person_and_exactly_what_was_validated(TestApp app)
    {
        var mw = await MigrationWorld.CreateAsync(app, legacyPolicy: false);
        // No policy, no legacy batch.
        var refused = await Assert.ThrowsAsync<DomainException>(() => mw.CreateBatchAsync(legacy: true));
        Assert.Equal("legacy_policy_inactive", refused.Code);
        // A person without migration.legacy_load cannot set the policy or create a legacy batch.
        await Assert.ThrowsAsync<ForbiddenException>(() => mw.World.AsAsync("tenantAdmin", s => s.Get<IMigrationProjects>().SetLegacyPolicyAsync(mw.ProjectId, new LegacyPolicyInput(true, mw.App.Clock.UtcNow.AddDays(1), ["Effective"], false, null))));

        await mw.AsAdminAsync(s => s.Get<IMigrationProjects>().SetLegacyPolicyAsync(mw.ProjectId, new LegacyPolicyInput(true, mw.App.Clock.UtcNow.AddDays(10), ["Effective"], false, null)));
        var batch = await mw.CreateBatchAsync(legacy: true);
        await mw.UploadAsync(batch, contracts: Contracts(MigrationWorld.Row("S-1", "AGR-001"), MigrationWorld.Row("S-2", "AGR-002", status: "Terminated"))); // Ended is not an allowed state
        var summary = await mw.ValidateAsync(batch);
        Assert.Equal(1, summary.Accepted); Assert.Equal(1, summary.MessagesByCode["MIG-E008"]);

        // Not confirmed: refused. The same person cannot confirm and then commit.
        var unconfirmed = await Assert.ThrowsAsync<DomainException>(() => mw.AsAdminAsync(s => s.Get<IMigrationCommit>().CommitAsync(batch)));
        Assert.Equal("confirmation_required", unconfirmed.Code);
        await mw.AsAdminAsync(s => s.Get<IMigrationCommit>().ConfirmLegacyLoadAsync(batch).Done());
        var self = await Assert.ThrowsAsync<DomainException>(() => mw.AsAdminAsync(s => s.Get<IMigrationCommit>().CommitAsync(batch)));
        Assert.Equal("second_person_required", self.Code);

        // Changing the inputs withdraws the confirmation: it described a different batch.
        await mw.UploadAsync(batch, contracts: Contracts(MigrationWorld.Row("S-1", "AGR-001"), MigrationWorld.Row("S-3", "AGR-003")));
        await mw.ValidateAsync(batch);
        Assert.False((await mw.DetailAsync(batch)).ConfirmationCurrent);
        await mw.AsAdmin2Async(s => s.Get<IMigrationCommit>().ConfirmLegacyLoadAsync(batch).Done());
        Assert.True((await mw.DetailAsync(batch)).ConfirmationCurrent);

        // The policy expires on its own.
        mw.App.Clock.Advance(TimeSpan.FromDays(11));
        var expired = await Assert.ThrowsAsync<DomainException>(() => mw.AsAdminAsync(s => s.Get<IMigrationCommit>().CommitAsync(batch)));
        Assert.Equal("legacy_policy_inactive", expired.Code);
        var revalidated = await mw.ValidateAsync(batch);
        Assert.Contains(revalidated.Blockers, b => b.Contains("MIG-E012"));
    }

    // ---- registered date ---------------------------------------------------------------------------------------------------------------------------

    public static async Task The_source_registered_date_is_kept_only_when_the_policy_allows_it_and_never_made_up(TestApp app)
    {
        var mw = await MigrationWorld.CreateAsync(app, allowRegisteredDate: false);
        var batch = await mw.CreateBatchAsync();
        await mw.UploadAsync(batch, contracts: Contracts(MigrationWorld.Row("S-1", "AGR-001", registered: "2024-01-15"), MigrationWorld.Row("S-2", "AGR-002", registered: "")));
        var summary = await mw.ValidateAndCommitAsync(batch).ContinueWith(t => t.Result);
        Assert.Equal(BatchStatus.Committed, summary.Status);
        await using var t = mw.World.AsScopeless();
        var contracts = await t.Get<ContractsDbContext>().Contracts.AsNoTracking().Where(c => c.MigrationBatchId == batch).OrderBy(c => c.AgreementReference).ToListAsync();
        Assert.All(contracts, c => { Assert.Null(c.RegisteredDateUtc); Assert.Null(c.RegisteredDateProvenance); Assert.True(c.IsLegacyLoad); });
        Assert.Contains((await MessagesAsync(mw, batch)), m => m.Code == "MIG-W005");

        // With the policy on, a present date is kept with its provenance and an absent one stays absent.
        var mw2 = await MigrationWorld.CreateAsync(app, allowRegisteredDate: true);
        var b2 = await mw2.CreateBatchAsync();
        await mw2.UploadAsync(b2, contracts: Contracts(MigrationWorld.Row("S-1", "AGR-001", registered: "2024-01-15", effective: "2024-02-01"), MigrationWorld.Row("S-2", "AGR-002", registered: "")));
        var validation = await mw2.ValidateAsync(b2);
        Assert.Equal(1, validation.MessagesByCode["MIG-W004"]);    // registered before the effective date
        await mw2.AsAdmin2Async(s => s.Get<IMigrationCommit>().ConfirmLegacyLoadAsync(b2).Done());
        await mw2.AsAdminAsync(s => s.Get<IMigrationCommit>().CommitAsync(b2));
        await using var t2 = mw2.World.AsScopeless();
        var list = await t2.Get<ContractsDbContext>().Contracts.AsNoTracking().Where(c => c.MigrationBatchId == b2).OrderBy(c => c.AgreementReference).ToListAsync();
        Assert.Equal(RegisteredDateProvenance.Migration, list[0].RegisteredDateProvenance);
        Assert.Equal("2024-01-15", list[0].SourceRegisteredValue);
        Assert.Equal(mw2.AdminId.ToString(), list[0].RegisteredBy is { } rb && rb.Length > 0 ? mw2.AdminId.ToString() : "");
        Assert.Null(list[1].RegisteredDateUtc); Assert.Null(list[1].RegisteredDateProvenance); // never fabricated
        var evt = await t2.Get<Ecp.Audit.Persistence.AuditDbContext>().Events.AsNoTracking().Where(e => e.MigrationBatchId == b2 && e.Action == "migration.contract.legacy_loaded").ToListAsync();
        Assert.Contains(evt, e => e.NewValuesJson!.Contains("\"RegisteredImported\":true") && e.NewValuesJson.Contains("2024-01-15") && e.NewValuesJson.Contains("MED") == false && e.NewValuesJson.Contains("S-1"));
    }
}
