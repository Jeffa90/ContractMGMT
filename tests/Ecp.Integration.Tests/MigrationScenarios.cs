using Ecp.Audit.Persistence;
using Ecp.Contracts;
using Ecp.Contracts.Domain;
using Ecp.Contracts.Persistence;
using Ecp.DataMigration.Persistence;
using Ecp.DataMigration;
using Ecp.DataMigration.Domain;
using Ecp.Documents;
using Ecp.SharedKernel;
using Ecp.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecp.Integration.Tests;

/// <summary>Scenario bodies for the migration slice, shared by the SQLite and SQL Server runs.</summary>
internal static class MigrationScenarios
{
    public static async Task The_sample_contracts_manifest_and_archive_load_end_to_end(TestApp app)
    {
        var mw = await MigrationWorld.CreateAsync(app);
        var batch = await mw.CreateBatchAsync();
        await mw.UploadAsync(batch, manifest: MigrationSamples.ManifestCsv, archive: MigrationSamples.AttachmentZip(includeManifest: false));
        var summary = await mw.ValidateAsync(batch);
        Assert.Empty(summary.Blockers);
        Assert.Equal(2, summary.Rows);
        Assert.Equal(2, summary.Accepted);
        Assert.Equal(0, summary.Errors);
        Assert.Equal(3, summary.Files);
        Assert.Equal(3, summary.FilesMatched);
        var progress = await mw.ValidateAndCommitAsync(batch);
        Assert.Equal(BatchStatus.Committed, progress.Status);

        // What the application now holds.
        await using var t = mw.World.AsScopeless();
        var contracts = await t.Get<ContractsDbContext>().Contracts.AsNoTracking().Where(c => c.MigrationBatchId == batch).OrderBy(c => c.AgreementReference).ToListAsync();
        Assert.Equal(["DEMO-AGR-0001", "DEMO-AGR-0002"], contracts.Select(c => c.AgreementReference));
        Assert.All(contracts, c =>
        {
            Assert.Equal("Effective", c.Status); Assert.True(c.RecordLocked); Assert.True(c.IsLegacyLoad);
            Assert.Equal("Medius", c.SourceSystem); Assert.Equal(batch, c.MigrationBatchId); Assert.Equal(RegisteredDateProvenance.Migration, c.RegisteredDateProvenance);
        });
        Assert.Equal("MED-1001", contracts[0].SourceRecordId);
        Assert.Equal("DEMO-AGR-0001", contracts[0].SourceAgreementReference);
        Assert.Equal("Effective", contracts[0].SourceStatus);
        Assert.Equal(new DateTime(2024, 1, 14, 14, 0, 0, DateTimeKind.Utc), contracts[0].RegisteredDateUtc); // 2024-01-15 00:00 in Australia/Brisbane (UTC+10)
        Assert.Equal("2024-01-15", contracts[0].SourceRegisteredValue);
        Assert.Equal(125000m, contracts[0].EstimatedValue);
        Assert.Null(contracts[1].EndDate);

        var suppliers = await t.Get<ContractsDbContext>().Suppliers.AsNoTracking().Where(x => x.MigrationBatchId == batch).OrderBy(x => x.VendorNumber).ToListAsync();
        Assert.Equal(["900001", "900002"], suppliers.Select(x => x.VendorNumber));
        var categories = await t.Get<ContractsDbContext>().Categories.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Code);
        Assert.Equal("PACKAGING-FLEX", categories[contracts[0].CategoryId]);
        Assert.Equal("ENERGY-ELEC", categories[contracts[1].CategoryId]);

        var docs = await t.Get<Ecp.Documents.Persistence.DocumentsDbContext>().Documents.AsNoTracking().Where(d => d.MigrationBatchId == batch).OrderBy(d => d.DocumentSourceId).ToListAsync();
        Assert.Equal(3, docs.Count);
        Assert.Equal(["ExecutedContract", "Schedule", "ExecutedContract"], docs.Select(d => d.DocumentTypeCode));
        Assert.All(docs, d => Assert.Equal(Ecp.Documents.Domain.DocumentStatus.Active, d.Status));

        var provenance = await t.Get<MigrationDbContext>().Provenance.AsNoTracking().Where(p => p.BatchId == batch).ToListAsync();
        Assert.Equal(2, provenance.Count(p => p.TargetEntityType == "Contract" && p.Action == ProvenanceAction.Created));
        Assert.Equal(2, provenance.Count(p => p.TargetEntityType == "Supplier" && p.Action == ProvenanceAction.Created));
        Assert.Equal(3, provenance.Count(p => p.TargetEntityType == "Document" && p.Action == ProvenanceAction.Created));
        Assert.Equal(2, provenance.Count(p => p.TargetEntityType == "Category" && p.Action == ProvenanceAction.Linked));
        Assert.All(provenance.Where(p => p.Action == ProvenanceAction.Created && p.TargetEntityType == "Contract"), p => Assert.NotNull(p.SourceFileSha256));

        var audit = await t.Get<AuditDbContext>().Events.AsNoTracking().Where(e => e.MigrationBatchId == batch).Select(e => e.Action).ToListAsync();
        Assert.Contains("migration.contract.legacy_loaded", audit);
        Assert.Contains("migration.supplier.created", audit);
        Assert.Contains("migration.document.imported", audit);
        Assert.Contains("migration.commit.completed", audit);
        Assert.Equal(2, audit.Count(a => a == "migration.contract.legacy_loaded"));
        Assert.True((await t.Get<Ecp.Audit.AuditChainVerifier>().VerifyAsync()).IsValid);

        // Reconcile and accept.
        var reconciliation = await mw.AsAdminAsync(s => s.Get<IMigrationCommit>().ReconcileAsync(batch, false, null));
        Assert.True(reconciliation.Balanced, string.Join("; ", reconciliation.Imbalances));
        Assert.Equal(new ReconciliationTotals(2, 2, 0, 0, 2, 0), reconciliation.Totals);
        Assert.Equal(3, reconciliation.Files.Imported);
        await mw.AsAdminAsync(s => s.Get<IMigrationCommit>().AcceptAsync(batch, new SampleCheckInput("Checked both contracts and the three documents against the source.", false)));
        Assert.Equal(BatchStatus.Accepted, (await mw.DetailAsync(batch)).Status);
    }
}
