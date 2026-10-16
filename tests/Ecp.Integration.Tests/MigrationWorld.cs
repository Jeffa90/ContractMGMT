using System.Text;
using Ecp.Contracts;
using Ecp.DataMigration;
using Ecp.DataMigration.Domain;
using Ecp.DataMigration.Mapping;
using Ecp.Identity;
using Ecp.Identity.Domain;
using Ecp.SharedKernel;
using Ecp.Tests.Common;
using Microsoft.Extensions.DependencyInjection;

namespace Ecp.Integration.Tests;

/// <summary>
/// A tenant ready for migration scenarios: the access world, the sample taxonomy, a migration project with a legacy-load policy, the sample mapping profile
/// published, and two procurement administrators (the second is the second person for legacy-load confirmation). Built through the real services.
/// </summary>
internal sealed class MigrationWorld
{
    public required TestApp App { get; init; }
    public required AccessWorld World { get; init; }
    public Guid ProjectId { get; set; }
    public Guid ProfileId { get; set; }
    public Guid ProfileVersionId { get; set; }
    public Guid AdminId => World.Users["procAdmin"];
    public Guid Admin2Id { get; set; }
    public Guid TenantId => World.TenantId;
    public Guid Org1 => World.Org1;
    public const string SourceSystem = "Medius";

    public static async Task<MigrationWorld> CreateAsync(TestApp app, bool legacyPolicy = true, bool allowRegisteredDate = true, bool createMissingCategories = false)
    {
        var world = await AccessWorld.CreateAsync(app, "TenantM" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant());
        var mw = new MigrationWorld { App = app, World = world };
        await using (var s = world.AsScopeless())
            await s.Get<ICategoryAdministration>().UpsertCategoryAsync(world.Org1, "ENERGY-ELEC", "Electricity", "ENERGY");

        // A second procurement administrator.
        await using (var s = world.AsScopeless())
        {
            mw.Admin2Id = await s.Get<IUserService>().CreateActiveTrustedAsync("Procurement Admin Two", "procadmin2@" + world.TenantId.ToString("N")[..8] + ".example.test", "demo", "https://demo.example.test", "procAdmin2|" + world.TenantId, "world");
            await s.Get<IRoleAssignmentService>().GrantTrustedAsync(new GrantRequest(mw.Admin2Id, RoleCodes.ProcurementAdministrator, null, ScopeSet.ForOrganisations([world.Org1], "Confidential"),
                null, EffectiveFromUtc: app.Clock.UtcNow.AddDays(-100), ExplicitHighCeiling: true), AssignmentSource.Demo, "world");
        }

        await mw.AsAdminAsync(async s =>
        {
            var projects = s.Get<IMigrationProjects>();
            mw.ProjectId = await projects.CreateAsync(new CreateProjectInput(world.Org1, "Sample register load", SourceSystem, "2026.1", "data.owner@example.test", app.Clock.UtcNow.AddDays(-2)));
            if (legacyPolicy)
                await projects.SetLegacyPolicyAsync(mw.ProjectId, new LegacyPolicyInput(allowRegisteredDate, app.Clock.UtcNow.AddDays(30), ["Effective", "Ended"], false, null));

            var profiles = s.Get<IMappingProfiles>();
            mw.ProfileId = await profiles.CreateAsync(world.Org1, "Sample register profile", SourceSystem, "2026.1");
            mw.ProfileVersionId = await profiles.OpenDraftAsync(mw.ProfileId);
            await profiles.ImportFieldMappingsAsync(mw.ProfileVersionId, MigrationSamples.Open("field-mapping-profile-complete.csv"));
            await profiles.ImportLookupsAsync(mw.ProfileVersionId, MigrationSamples.Open("status-lookup-map.csv"));
            await profiles.SetOptionsAsync(mw.ProfileVersionId, new ProfileOptions(createMissingCategories, null, ["Evergreen"]));
            await profiles.PublishAsync(mw.ProfileVersionId, "Sample profile");
        });
        return mw;
    }

    public Task<T> AsAdminAsync<T>(Func<TenantScope, Task<T>> work) => World.AsIdAsync(AdminId, work);
    public Task AsAdminAsync(Func<TenantScope, Task> work) => World.AsIdAsync<bool>(AdminId, async s => { await work(s); return true; });
    public Task<T> AsAdmin2Async<T>(Func<TenantScope, Task<T>> work) => World.AsIdAsync(Admin2Id, work);

    public Task<Guid> CreateBatchAsync(BatchKind kind = BatchKind.Final, DuplicateStrategy strategy = DuplicateStrategy.SkipExisting, DocumentDuplicateOption option = DocumentDuplicateOption.AddVersion,
        bool legacy = true, Guid? profileVersion = null, string? name = null)
        => AsAdminAsync(s => s.Get<IMigrationBatches>().CreateAsync(new CreateBatchInput(ProjectId, name ?? "Batch " + Guid.NewGuid().ToString("N")[..6], kind, strategy, option, legacy, profileVersion ?? ProfileVersionId)));

    public async Task UploadAsync(Guid batchId, byte[]? contracts = null, byte[]? manifest = null, byte[]? archive = null, string contractsName = "contracts-import.csv")
    {
        await AsAdminAsync(async s =>
        {
            var intake = s.Get<IMigrationIntake>();
            await intake.UploadContractsAsync(batchId, new MemoryStream(contracts ?? MigrationSamples.ContractsCsv), contractsName);
            if (manifest is not null) await intake.UploadManifestAsync(batchId, new MemoryStream(manifest), "attachment-manifest.csv");
            if (archive is not null) await intake.UploadArchiveAsync(batchId, new MemoryStream(archive), "attachments.zip");
        });
    }

    public Task<ValidationSummary> ValidateAsync(Guid batchId) => AsAdminAsync(s => s.Get<IMigrationValidation>().ValidateAsync(batchId));
    public Task<BatchDetail> DetailAsync(Guid batchId) => AsAdminAsync(s => s.Get<IMigrationBatches>().GetAsync(batchId));

    /// <summary>Validate, confirm (second person, for a legacy load) and commit as the first administrator.</summary>
    public async Task<CommitProgress> ValidateAndCommitAsync(Guid batchId, bool legacy = true)
    {
        var summary = await ValidateAsync(batchId);
        if (summary.Blockers.Count > 0) throw new InvalidOperationException("Validation blocked: " + string.Join("; ", summary.Blockers));
        if (legacy) await AsAdmin2Async(s => { var c = s.Get<IMigrationCommit>().ConfirmLegacyLoadAsync(batchId); return c.Done(); });
        return await AsAdminAsync(s => s.Get<IMigrationCommit>().CommitAsync(batchId));
    }

    /// <summary>A trusted look at the target, not through any user's permissions.</summary>
    public async Task<T> TargetAsync<T>(Func<TenantScope, Task<T>> read)
    {
        await using var s = World.AsScopeless();
        return await read(s);
    }

    private static string Q(string v) => v.Contains(',') || v.Contains('"') || v.Contains('\n') ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;

    /// <summary>One line of the contracts file in the sample layout.</summary>
    public static string Row(string id, string reference, string title = "Synthetic agreement", string supplier = "Southern Cross Demo Supplies Pty Ltd", string vendor = "900001", string category = "Packaging",
        string sub = "Flexible Packaging", string status = "Effective", string start = "2024-01-01", string effective = "2024-01-01", string registered = "2024-01-15", string review = "2026-11-30",
        string end = "2027-12-31", string currency = "AUD", string value = "1000", string ext = "NoExtension", string legal = "Yes", string? folder = null)
        => string.Join(",", new[] { id, reference, title, supplier, vendor, category, sub, status, start, effective, registered, review, end, currency, value, ext, legal, folder ?? reference }.Select(Q));

    public const string ManifestHeader = "SourceRecordId,AgreementReference,RelativePath,FileName,DocumentType,DocumentDate,VersionLabel,IsExecutedCopy,ParentDocumentSourceId,Sha256,Notes";

    public static string ManifestRow(string id, string reference, string path, string type = "Executed Contract", string date = "2024-01-10", string executed = "Yes", string version = "1.0", string parent = "", string sha = "")
        => string.Join(",", new[] { id, reference, path, path[(path.LastIndexOf('/') + 1)..], type, date, version, executed, parent, sha, "" }.Select(Q));

    public static byte[] Csv(params string[] lines) => [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(string.Join("\r\n", lines) + "\r\n")];
    public const string ContractsHeader = "SourceRecordId,AgreementReference,ContractTitle,SupplierName,VendorNumber,Category,Subcategory,Status,StartDate,EffectiveDate,RegisteredDate,ReviewDate,EndDate,Currency,EstimatedValue,ExtensionType,LegalReviewRequired,DocumentFolder";
}

internal static class TaskExtensions
{
    /// <summary>Awaits the task so a failure surfaces (ContinueWith would swallow it) and yields a value for helpers that return one.</summary>
    public static async Task<bool> Done(this Task task) { await task; return true; }
}
