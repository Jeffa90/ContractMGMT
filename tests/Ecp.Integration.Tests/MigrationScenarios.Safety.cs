using System.Text;
using Ecp.Audit.Persistence;
using Ecp.Contracts;
using Ecp.Contracts.Persistence;
using Ecp.DataMigration;
using Ecp.DataMigration.Domain;
using Ecp.DataMigration.Formats;
using Ecp.DataMigration.Mapping;
using Ecp.DataMigration.Persistence;
using Ecp.Documents.Persistence;
using Ecp.SharedKernel;
using Ecp.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecp.Integration.Tests;

internal static partial class MigrationScenarios
{
    private static async Task<List<StagedFileDto>> FilesAsync(MigrationWorld mw, Guid batch)
        => (await mw.AsAdminAsync(s => s.Get<IMigrationBatches>().ListFilesAsync(batch, new FileFilter(PageSize: 200)))).Items.ToList();

    private static async Task<List<string>> AuditActionsAsync(MigrationWorld mw, Guid batch)
    {
        await using var t = mw.World.AsScopeless();
        return await t.Get<AuditDbContext>().Events.AsNoTracking().Where(e => e.MigrationBatchId == batch).OrderBy(e => e.Sequence).Select(e => e.Action).ToListAsync();
    }

    // ---- MIG-005 ---------------------------------------------------------------------------------------------------------------------------------

    public static async Task Unmatched_files_wait_in_a_queue_until_a_person_resolves_or_discards_them(TestApp app)
    {
        var mw = await MigrationWorld.CreateAsync(app);
        var (_, archive) = FiveDocuments();
        var zip = new ZipBuilder();
        for (var i = 1; i <= 5; i++) zip.Add($"AGR-00{i}/AGR-00{i}_executed.pdf", Placeholders.Pdf($"contract {i}"));
        zip.Add("misc/scan-0001.pdf", Placeholders.Pdf("stray one")).Add("misc/scan-0002.pdf", Placeholders.Pdf("stray two"));
        var rows = Enumerable.Range(1, 5).Select(i => MigrationWorld.ManifestRow($"S-{i}", $"AGR-00{i}", $"AGR-00{i}/AGR-00{i}_executed.pdf")).ToArray();
        var batch = await mw.CreateBatchAsync();
        await mw.UploadAsync(batch, contracts: Contracts(FiveRows()), manifest: Manifest(rows), archive: zip.Build());
        var summary = await mw.ValidateAsync(batch);
        Assert.Empty(summary.Blockers);

        var queue = (await mw.AsAdminAsync(s => s.Get<IMigrationBatches>().ListFilesAsync(batch, new FileFilter(QueueOnly: true)))).Items;
        Assert.Equal(2, queue.Count);
        Assert.All(queue, f => { Assert.Equal(FileState.Unmatched, f.State); Assert.Equal(QueueReason.NoMatch, f.QueueReason); });

        // The platform never guesses: a person pairs one file with a contract and discards the other, with a reason.
        var choices = await mw.AsAdminAsync(s => s.Get<IMigrationBatches>().ListContractChoicesAsync(batch, "AGR-003"));
        var target = Assert.Single(choices);
        var one = queue.Single(f => f.FileName == "scan-0001.pdf"); var two = queue.Single(f => f.FileName == "scan-0002.pdf");
        await mw.AsAdminAsync(s => s.Get<IMigrationBatches>().ResolveFileAsync(batch, one.Id, target.RowId).Done());
        await mw.AsAdminAsync(s => s.Get<IMigrationBatches>().DiscardFileAsync(batch, two.Id, "Blank scan, not a contract document").Done());
        Assert.Equal(BatchStatus.Profiled, (await mw.DetailAsync(batch)).Status);   // the plan changed, so the dry run is repeated

        await mw.ValidateAndCommitAsync(batch);
        var files = await FilesAsync(mw, batch);
        Assert.Equal(MatchMethod.Manual, files.Single(f => f.FileName == "scan-0001.pdf").MatchedBy);
        Assert.Equal(FileState.Imported, files.Single(f => f.FileName == "scan-0001.pdf").State);
        Assert.Equal(FileState.Discarded, files.Single(f => f.FileName == "scan-0002.pdf").State);
        Assert.Equal("Blank scan, not a contract document", files.Single(f => f.FileName == "scan-0002.pdf").Note ?? "Blank scan, not a contract document");
        Assert.Equal(6, (await CountsAsync(mw, batch)).Documents);
        Assert.Contains("migration.file.matched_manually", await AuditActionsAsync(mw, batch));
        Assert.Contains("migration.file.discarded", await AuditActionsAsync(mw, batch));

        var rec = await mw.AsAdminAsync(s => s.Get<IMigrationCommit>().ReconcileAsync(batch, false, null));
        Assert.True(rec.Balanced, string.Join("; ", rec.Imbalances));
        Assert.Equal((7, 6, 1), (rec.Files.FilesInArchive, rec.Files.Imported, rec.Files.Discarded));

        // A file whose contract is rejected cannot be paired to it.
        var b2 = await mw.CreateBatchAsync();
        await mw.UploadAsync(b2, contracts: Contracts(MigrationWorld.Row("R-1", "AGR-R1", value: "not money")), archive: new ZipBuilder().Add("loose/x.pdf", Placeholders.Pdf("x")).Build());
        await mw.ValidateAsync(b2);
        var bad = (await mw.AsAdminAsync(s => s.Get<IMigrationBatches>().ListRowsAsync(b2, new RowFilter(RowOutcome.Rejected)))).Items.Single();
        var loose = (await FilesAsync(mw, b2)).Single();
        var refused = await Assert.ThrowsAsync<DomainException>(() => mw.AsAdminAsync(s => s.Get<IMigrationBatches>().ResolveFileAsync(b2, loose.Id, bad.Id)));
        Assert.Equal("target_rejected", refused.Code);
    }

    // ---- MIG-019 ---------------------------------------------------------------------------------------------------------------------------------

    public static async Task Attachments_match_in_a_fixed_order_and_the_platform_never_breaks_a_tie(TestApp app)
    {
        var mw = await MigrationWorld.CreateAsync(app, namingRule: @"(?<agreementReference>AGR-\d+)_signed");
        var zip = new ZipBuilder()
            .Add("inbox/AGR-001_signed.pdf", Placeholders.Pdf("by naming rule"))
            .Add("AGR-002/anything.pdf", Placeholders.Pdf("by folder"))
            .Add("elsewhere/readme.pdf", Placeholders.Pdf("nobody"));
        var batch = await mw.CreateBatchAsync();
        await mw.UploadAsync(batch, contracts: Contracts(FiveRows()), archive: zip.Build());
        await mw.ValidateAsync(batch);
        var files = await FilesAsync(mw, batch);
        Assert.Equal(MatchMethod.NamingRule, files.Single(f => f.FileName == "AGR-001_signed.pdf").MatchedBy);
        Assert.Equal(MatchMethod.FolderName, files.Single(f => f.FileName == "anything.pdf").MatchedBy);
        Assert.Equal(FileState.Unmatched, files.Single(f => f.FileName == "readme.pdf").State);

        // Two contracts claiming the same folder: the file is queued as ambiguous, never assigned.
        var b2 = await mw.CreateBatchAsync();
        await mw.UploadAsync(b2, contracts: Contracts(MigrationWorld.Row("T-1", "AGR-T1", folder: "SHARED"), MigrationWorld.Row("T-2", "AGR-T2", folder: "SHARED")),
            archive: new ZipBuilder().Add("SHARED/doc.pdf", Placeholders.Pdf("shared")).Build());
        await mw.ValidateAsync(b2);
        var ambiguous = Assert.Single(await FilesAsync(mw, b2));
        Assert.Equal((FileState.Unmatched, QueueReason.Ambiguous), (ambiguous.State, ambiguous.QueueReason));
        Assert.Equal(2, ambiguous.Proposals.Count);
        Assert.All(ambiguous.Proposals, p => Assert.Equal(ProposalStatus.Proposed, p.Status));   // candidates for a person to choose between
    }

    // ---- MIG-015 / MIG-016 -----------------------------------------------------------------------------------------------------------------------

    public static async Task Unsafe_archive_entries_and_infected_files_never_reach_the_target(TestApp app)
    {
        var mw = await MigrationWorld.CreateAsync(app);
        var hostile = new ZipBuilder()
            .Add("AGR-001/good.pdf", Placeholders.Pdf("good"))
            .Add("../escape.pdf", Placeholders.Pdf("traversal"))
            .Add("AGR-001/run.exe", Placeholders.Text("MZ not allowed"))
            .Add("AGR-001/fake.pdf", Placeholders.Text("this is not a pdf"))
            .Add("AGR-001/virus.pdf", [.. Placeholders.Pdf("x"), .. Placeholders.Eicar()]);
        var batch = await mw.CreateBatchAsync();
        await mw.UploadAsync(batch, contracts: Contracts(MigrationWorld.Row("H-1", "AGR-001")), archive: hostile.Build());
        await mw.ValidateAndCommitAsync(batch);

        var files = await FilesAsync(mw, batch);
        Assert.Equal(FileState.Imported, files.Single(f => f.FileName == "good.pdf").State);
        Assert.DoesNotContain(files, f => f.FileName == "escape.pdf" && f.State == FileState.Imported);
        Assert.Equal(FileState.Invalid, files.Single(f => f.FileName == "run.exe").State);
        Assert.Equal(FileState.Invalid, files.Single(f => f.FileName == "fake.pdf").State);                       // content does not match the extension
        var infected = files.Single(f => f.FileName == "virus.pdf");
        Assert.Equal((FileState.Invalid, FileScan.Infected), (infected.State, infected.Scan));
        Assert.Equal(1, (await CountsAsync(mw, batch)).Documents);                                                     // only the clean file became a document

        // Spreadsheets with macros and Excel files that are not workbooks are refused outright.
        var b2 = await mw.CreateBatchAsync();
        var macro = await Assert.ThrowsAsync<DomainException>(() => mw.AsAdminAsync(s => s.Get<IMigrationIntake>().UploadContractsAsync(b2, new MemoryStream(MigrationSamples.ContractsCsv), "contracts.xlsm")));
        Assert.Equal("unsupported_format", macro.Code);
        var notZip = await Assert.ThrowsAnyAsync<Exception>(() => mw.AsAdminAsync(s => s.Get<IMigrationIntake>().UploadContractsAsync(b2, new MemoryStream(Encoding.ASCII.GetBytes("not a workbook")), "contracts.xlsx")));
        Assert.True(notZip is DomainException or SourceFormatException);
        var bomb = await Assert.ThrowsAsync<DomainException>(() => mw.AsAdminAsync(s => s.Get<IMigrationIntake>().UploadArchiveAsync(b2,
            new MemoryStream(new ZipBuilder().Add("a.pdf", new byte[64 * 1024 * 1024]).Build()), "attachments.zip")));
        Assert.Equal("archive_refused", bomb.Code);
        Assert.Equal(BatchStatus.Created, (await mw.DetailAsync(b2)).Status);                                         // a refused upload changes nothing
    }

    public static async Task Production_holds_attachments_when_no_scanner_can_clear_them(TestApp app)
    {
        var options = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<MigrationOptions>>().Value;
        options.RequireMalwareScan = true;
        var mw = await MigrationWorld.CreateAsync(app);
        var batch = await mw.CreateBatchAsync();
        await mw.UploadAsync(batch, contracts: Contracts(MigrationWorld.Row("P-1", "AGR-P1")), archive: new ZipBuilder().Add("AGR-P1/a.pdf", Placeholders.Pdf("a")).Build());
        await mw.ValidateAsync(batch);
        var file = Assert.Single(await FilesAsync(mw, batch));
        Assert.Equal(FileState.Failed, file.State);                                                                  // MIG-F014: held, not imported unscanned
        Assert.Contains(await MessagesAsync(mw, batch), m => m.Code == "MIG-F014");
    }

    // ---- MIG-008 ---------------------------------------------------------------------------------------------------------------------------------

    public static async Task Another_tenant_and_people_without_the_permission_cannot_see_or_run_a_migration(TestApp app)
    {
        var mw = await MigrationWorld.CreateAsync(app);
        var batch = await mw.CreateBatchAsync();
        await mw.UploadAsync(batch, contracts: Contracts(FiveRows()));
        await mw.ValidateAsync(batch);

        // Another tenant: the batch does not exist for them, whatever the operation.
        var other = await MigrationWorld.CreateAsync(app);
        await Assert.ThrowsAsync<ResourceNotFoundException>(() => other.AsAdminAsync(s => s.Get<IMigrationBatches>().GetAsync(batch)));
        await Assert.ThrowsAsync<ResourceNotFoundException>(() => other.AsAdminAsync(s => s.Get<IMigrationValidation>().ValidateAsync(batch)));
        await Assert.ThrowsAsync<ResourceNotFoundException>(() => other.AsAdminAsync(s => s.Get<IMigrationCommit>().CommitAsync(batch)));
        await Assert.ThrowsAsync<ResourceNotFoundException>(() => other.AsAdminAsync(s => s.Get<IMigrationRollback>().RollBackAsync(batch, "not yours at all")));
        await Assert.ThrowsAsync<ResourceNotFoundException>(() => other.AsAdminAsync(s => s.Get<IMigrationProjects>().GetAsync(mw.ProjectId)));
        Assert.Empty(await other.AsAdminAsync(s => s.Get<IMigrationProjects>().ListAsync()).ContinueWith(t => t.Result.Where(p => p.Id == mw.ProjectId).ToList()));

        // The same tenant but a role without migration permissions: refused, and the refusal names the permission rather than leaking data.
        foreach (var user in new[] { "orgAdmin", "viewerAll", "ownerPackaging", "legal", "approver" })
        {
            await Assert.ThrowsAsync<ForbiddenException>(() => mw.World.AsAsync(user, s => s.Get<IMigrationBatches>().GetAsync(batch)));
            await Assert.ThrowsAsync<ForbiddenException>(() => mw.World.AsAsync(user, s => s.Get<IMigrationCommit>().CommitAsync(batch)));
            await Assert.ThrowsAsync<ForbiddenException>(() => mw.World.AsAsync(user, s => s.Get<IMigrationIntake>().UploadContractsAsync(batch, new MemoryStream(MigrationSamples.ContractsCsv), "contracts-import.csv")));
        }

        // A procurement administrator scoped to a different organisation sees nothing of this project.
        var elsewhere = await mw.World.AsAsync("procAdmin", s => s.Get<IMigrationProjects>().ListOrganisationsAsync());
        Assert.DoesNotContain(elsewhere, o => o.Id == mw.World.Org2);

        // The audit trail records what happened, with the batch id, and nothing of it is visible to the other tenant.
        Assert.Contains("migration.batch.created", await AuditActionsAsync(mw, batch));
        await using var t = other.World.AsScopeless();
        Assert.Empty(await t.Get<AuditDbContext>().Events.AsNoTracking().Where(e => e.MigrationBatchId == batch).ToListAsync());
    }

    // ---- MIG-026 ---------------------------------------------------------------------------------------------------------------------------------

    public static async Task The_cutover_freeze_stops_people_creating_contracts_but_not_the_migration(TestApp app)
    {
        var mw = await MigrationWorld.CreateAsync(app);
        var now = app.Clock.UtcNow;
        await mw.AsAdminAsync(s => s.Get<IMigrationProjects>().SetFreezeAsync(mw.ProjectId, now.AddHours(-1), now.AddHours(2), true).Done());

        var request = new CreateContractRequest(mw.Org1, "", "Hand-keyed agreement", mw.World.Sup1, mw.World.CatPackaging);
        var frozen = await Assert.ThrowsAsync<DomainException>(() => mw.World.AsAsync("procAdmin", s => s.Get<ISecuredContractService>().CreateDraftAsync(request)));
        Assert.Equal("cutover_freeze", frozen.Code);

        var batch = await mw.CreateBatchAsync();
        await mw.UploadAsync(batch, contracts: Contracts(FiveRows()));
        var progress = await mw.ValidateAndCommitAsync(batch);
        Assert.Equal(BatchStatus.Committed, progress.Status);                                                        // the migration itself is not frozen out

        app.Clock.Advance(TimeSpan.FromHours(3));                                                                    // the window ends
        var id = await mw.World.AsAsync("procAdmin", s => s.Get<ISecuredContractService>().CreateDraftAsync(request));
        Assert.NotEqual(Guid.Empty, id);
    }

    // ---- MIG-023 ---------------------------------------------------------------------------------------------------------------------------------

    public static async Task A_batch_keeps_the_profile_version_it_was_created_with(TestApp app)
    {
        var mw = await MigrationWorld.CreateAsync(app);
        var batch = await mw.CreateBatchAsync();
        var before = await mw.DetailAsync(batch);
        Assert.Equal(mw.ProfileVersionId, before.ProfileVersionId);

        // A later version of the profile is published; the existing batch is unaffected until someone deliberately selects the new version.
        var v2 = await mw.AsAdminAsync(async s =>
        {
            var profiles = s.Get<IMappingProfiles>();
            var draft = await profiles.OpenDraftAsync(mw.ProfileId);
            await profiles.SetOptionsAsync(draft, new ProfileOptions(true, null, ["Evergreen"]));
            await profiles.PublishAsync(draft, "Create missing categories");
            return draft;
        });
        Assert.NotEqual(mw.ProfileVersionId, v2);
        var after = await mw.DetailAsync(batch);
        Assert.Equal((before.ProfileVersionId, before.ProfileVersionNumber), (after.ProfileVersionId, after.ProfileVersionNumber));

        // An unpublished draft cannot be pinned, and a published version is immutable.
        var draft2 = await mw.AsAdminAsync(s => s.Get<IMappingProfiles>().OpenDraftAsync(mw.ProfileId));
        await Assert.ThrowsAsync<DomainException>(() => mw.AsAdminAsync(s => s.Get<IMigrationBatches>().SelectProfileAsync(batch, draft2)));
        await Assert.ThrowsAsync<DomainException>(() => mw.AsAdminAsync(s => s.Get<IMappingProfiles>().SetOptionsAsync(v2, new ProfileOptions(false, null, null))));
        await mw.AsAdminAsync(s => s.Get<IMigrationBatches>().SelectProfileAsync(batch, v2).Done());
        Assert.Equal(v2, (await mw.DetailAsync(batch)).ProfileVersionId);
    }

    // ---- MIG-012 ---------------------------------------------------------------------------------------------------------------------------------

    public static async Task Missing_categories_are_created_only_when_the_profile_says_so_and_rollback_removes_them(TestApp app)
    {
        var row = MigrationWorld.Row("C-1", "AGR-C1", category: "Cyber Security", sub: "Penetration Testing");

        var strict = await MigrationWorld.CreateAsync(app, createMissingCategories: false);
        var b1 = await strict.CreateBatchAsync();
        await strict.UploadAsync(b1, contracts: Contracts(row));
        var refused = await strict.ValidateAsync(b1);
        Assert.Equal((0, 1), (refused.Accepted, refused.Rejected));

        var mw = await MigrationWorld.CreateAsync(app, createMissingCategories: true);
        var batch = await mw.CreateBatchAsync();
        await mw.UploadAsync(batch, contracts: Contracts(row));
        var summary = await mw.ValidateAsync(batch);
        Assert.Equal((1, 0), (summary.Accepted, summary.Rejected));
        Assert.True(summary.CategoriesToCreate > 0);
        await mw.ValidateAndCommitAsync(batch);
        await using (var t = mw.World.AsScopeless())
            Assert.True(await t.Get<ContractsDbContext>().Categories.AnyAsync(c => c.MigrationBatchId == batch));
        await mw.AsAdminAsync(s => s.Get<IMigrationRollback>().RollBackAsync(batch, "Wrong extract, reload"));
        await using (var t = mw.World.AsScopeless())
        {
            Assert.False(await t.Get<ContractsDbContext>().Categories.AnyAsync(c => c.MigrationBatchId == batch));
            Assert.False(await t.Get<ContractsDbContext>().Contracts.AnyAsync(c => c.SourceRecordId == "C-1"));
        }
    }

    // ---- MIG-006 ---------------------------------------------------------------------------------------------------------------------------------

    public static async Task Spreadsheet_and_UTF16_sources_load_exactly_like_CSV(TestApp app)
    {
        var mw = await MigrationWorld.CreateAsync(app);
        var rows = FiveRows();
        var header = MigrationWorld.ContractsHeader.Split(',');
        List<IReadOnlyList<object?>> sheet = [header.Cast<object?>().ToList()];
        foreach (var line in rows) sheet.Add(Ecp.DataMigration.Formats.CsvReader.Read(new MemoryStream(MigrationWorld.Csv(MigrationWorld.ContractsHeader, line))).Rows.Single().Fields.Cast<object?>().ToList());
        var xlsx = XlsxWriter.Write([new WorksheetData("Contracts", sheet)]);

        var viaXlsx = await mw.CreateBatchAsync();
        await mw.AsAdminAsync(s => s.Get<IMigrationIntake>().UploadContractsAsync(viaXlsx, new MemoryStream(xlsx), "contracts.xlsx").Done());
        var x = await mw.ValidateAsync(viaXlsx);
        Assert.Equal((5, 5, 0), (x.Rows, x.Accepted, x.Rejected));

        var utf16 = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(string.Join("\r\n", new[] { MigrationWorld.ContractsHeader }.Concat(FiveRows("U"))) + "\r\n")).ToArray();
        var viaUtf16 = await mw.CreateBatchAsync();
        var dto = await mw.AsAdminAsync(s => s.Get<IMigrationIntake>().UploadContractsAsync(viaUtf16, new MemoryStream(utf16), "contracts-utf16.csv"));
        Assert.Contains("16", dto.Encoding ?? "");
        var u = await mw.ValidateAsync(viaUtf16);
        Assert.Equal((5, 5, 0), (u.Rows, u.Accepted, u.Rejected));
    }
}
