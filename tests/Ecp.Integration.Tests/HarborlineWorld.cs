using Ecp.Configuration;
using Ecp.Contracts;
using Ecp.Identity;
using Ecp.Identity.Domain;
using Ecp.Lifecycle;
using Ecp.Lifecycle.Definitions;
using Ecp.Lifecycle.Domain;
using Ecp.Packs;
using Ecp.Setup.Onboarding;
using Ecp.SharedKernel;
using Ecp.Tenancy;
using Ecp.Tenancy.Domain;
using Ecp.Tests.Common;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecp.Integration.Tests;

/// <summary>
/// S9 (docs/SECOND_BUSINESS_PROOF.md): a synthetic general contractor, Harborline Construction, run on the shipped external pack `construction-subcontracts` and on nothing else. Nothing here writes to a table or names a
/// construction state in a way the platform could see: the pack is uploaded and installed through the Pack Manager, assigned, onboarded and published through the same services an administrator's pages call.
/// The only construction words in this file are the pack's own documents' codes (states, steps, field keys, group keys), which the test must name to drive the pack; the platform itself is never told them.
/// </summary>
internal sealed class HarborlineWorld
{
    public const string PackKey = "construction-subcontracts";
    public const string ShippedVersion = "1.1.0";
    public const string Prefix = "HBL";

    public TestApp App { get; }
    public Guid TenantId { get; private set; }
    public Guid Org { get; private set; }
    public Dictionary<string, Guid> Users { get; } = [];
    public Dictionary<string, Guid> Categories { get; } = [];
    public Dictionary<string, Guid> Sites { get; } = [];
    public Dictionary<string, Guid> Divisions { get; } = [];
    public Guid Subcontractor { get; private set; }
    /// <summary>The pack version this organisation works from: the shipped 1.0.0, or an uploaded copy of the same documents under a new version.</summary>
    public string PackVersion { get; private set; } = ShippedVersion;
    public DateTime Now => App.Clock.UtcNow;
    private int _n;

    private HarborlineWorld(TestApp app) { App = app; }

    /// <summary>The bytes of the pack exactly as the build produces them (unsigned; the signed copy is a separate test).</summary>
    public static byte[] PackBytes() => File.ReadAllBytes(Path.Combine(TestShippedPacks.UnsignedDirectory, $"{PackKey}-{ShippedVersion}.ecpkg"));

    /// <summary>
    /// The same documents built again from the pack source under another version, as a publisher would hand them to an administrator: a file the installation has never seen, so uploading it is a real install. The source is
    /// copied to a temporary folder, only the two version strings change, and nothing is written into the repository.
    /// </summary>
    public static byte[] UploadableCopy(string version, Func<string, string, string>? change = null)
    {
        var source = Path.Combine(RepoRoot.Find(), "templates", "packs", PackKey);
        var temp = Path.Combine(Path.GetTempPath(), "ecp-hbl-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var file in Directory.EnumerateFiles(source, "*.json", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(source, file);
                var target = Path.Combine(temp, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var text = File.ReadAllText(file);
                if (relative is "pack.json" or "pack-source.json") text = text.Replace("\"version\": \"" + ShippedVersion + "\"", "\"version\": \"" + version + "\"");
                File.WriteAllText(target, change is null ? text : change(relative, text));
            }
            var built = PackBuilder.Build(temp);
            Assert.True(built.Succeeded, string.Join("; ", built.Issues.Select(i => i.Code + " " + i.Message)));
            return built.Package!;
        }
        finally { try { Directory.Delete(temp, true); } catch { } }
    }

    /// <summary>Documents the pack carries, read from the built package so a test sees what an administrator's upload sees.</summary>
    public static IReadOnlyDictionary<string, string> PackDocuments()
    {
        var opened = PackReader.Open(PackBytes());
        Assert.NotNull(opened.Manifest);
        return opened.Manifest!.Content.ToDictionary(c => c.Kind, c => opened.Document(c.Kind)!, StringComparer.Ordinal);
    }

    public TenantScope AsScopeless() => App.ScopeFor(TenantId);

    public async Task<T> AsAsync<T>(string userKey, Func<TenantScope, Task<T>> work)
    {
        await using var s = App.ScopeFor(TenantId);
        s.Get<CurrentUser>().Set(Users[userKey]);
        var actor = s.Get<ActorContext>();
        actor.Type = ActorType.User; actor.ActorId = Users[userKey].ToString(); actor.Source = "Test";
        return await work(s);
    }

    public Task AsAsync(string userKey, Func<TenantScope, Task> work) => AsAsync<bool>(userKey, async s => { await work(s); return true; });

    /// <summary>Creates a tenant whose first organisation is Harborline (US locale, dollars, Chicago) pointing at the pack, and its people. Nothing is installed or published yet.</summary>
    public static async Task<HarborlineWorld> CreateTenantAsync(TestApp app, string? tenantName = null)
    {
        var w = new HarborlineWorld(app);
        await using var bootstrap = app.Services.CreateAsyncScope();
        var name = tenantName ?? "Harborline" + Guid.NewGuid().ToString("N")[..6];
        var provisioned = await bootstrap.ServiceProvider.GetRequiredService<ITenantProvisioningService>().ProvisionAsync(new ProvisionTenantRequest(
            name, TenancyMode.MultiOrganisation, EcpMode.Test, "Harborline Construction", "America/Chicago", "en-US", "USD", Prefix, PackKey));
        w.TenantId = provisioned.TenantId; w.Org = provisioned.OrganisationId;
        await w.AddPeopleAsync();
        return w;
    }

    /// <summary>
    /// Adds Harborline as one more organisation of a tenant that already runs others (the shared-tenant half of the isolation proof). The scale and branding are tenant-level, so a tenant that shares them with other
    /// businesses publishes only the organisation-level kinds; the tenant-level drafts the pack creates are left open and the test asserts they are reported as such.
    /// </summary>
    public static async Task<HarborlineWorld> CreateInTenantAsync(TestApp app, Guid tenantId, string name = "Harborline Construction")
    {
        var w = new HarborlineWorld(app) { TenantId = tenantId };
        await using var s = app.ScopeFor(tenantId);
        w.Org = await s.Get<ITenantProvisioningService>().AddOrganisationAsync(new AddOrganisationRequest(name, "America/Chicago", "en-US", "USD", Prefix, PackKey));
        await w.AddPeopleAsync();
        return w;
    }

    private Task<Guid> GrantAsync(string key, string role, string? group, string ceiling, Guid? categoryToOwn = null, bool tenantWide = false, ViewerKind? kind = null, ScopeSet? scope = null)
        => GrantCoreAsync(key, role, group, scope ?? (tenantWide ? ScopeSet.Tenant(ceiling) : ScopeSet.ForOrganisations([Org], ceiling)), kind, categoryToOwn);

    private async Task<Guid> GrantCoreAsync(string key, string role, string? group, ScopeSet scope, ViewerKind? kind, Guid? categoryToOwn)
    {
        await using var s = App.ScopeFor(TenantId);
        if (!Users.TryGetValue(key, out var id))
        {
            id = await s.Get<IUserService>().CreateActiveTrustedAsync("Harborline " + key, "hbl." + key + "@" + TenantId.ToString("N")[..8] + ".example.test", "demo", "https://demo.example.test", "hbl." + key + "|" + TenantId, "test");
            Users[key] = id;
        }
        await s.Get<IRoleAssignmentService>().GrantTrustedAsync(new GrantRequest(id, role, kind, scope, group, EffectiveFromUtc: Now.AddDays(-30), ExplicitHighCeiling: true), AssignmentSource.Demo, "test");
        if (categoryToOwn is { } c) await s.Get<ICategoryAdministration>().AssignOwnerTrustedAsync(c, id, true, Now.AddDays(-30), null, "test", null);
        return id;
    }

    private async Task AddPeopleAsync()
    {
        // One administrator for the organisation and the tenant (the tenant-wide drafts a pack carries are a separate draft each), as a small business has.
        await GrantAsync("admin", RoleCodes.OrganisationAdministrator, null, "Restricted");
        await GrantAsync("admin", RoleCodes.TenantAdministrator, null, "Restricted", tenantWide: true);
        await GrantAsync("commercial", RoleCodes.ProcurementAdministrator, null, "Confidential");
        await GrantAsync("director1", RoleCodes.Approver, "ProjectDirectors", "Confidential");
        await GrantAsync("directorDeputy", RoleCodes.BackupApprover, "ProjectDirectorDeputies", "Confidential");
        await GrantAsync("director2", RoleCodes.Approver, "ProjectDirectors", "Confidential");
        await GrantAsync("commercialManager", RoleCodes.Approver, "CommercialManagers", "Confidential");
        await GrantAsync("commercialDeputy", RoleCodes.BackupApprover, "CommercialManagerDeputies", "Confidential");
        await GrantAsync("regionalDirector", RoleCodes.Approver, "RegionalDirectors", "Confidential");
        await GrantAsync("safetyManager", RoleCodes.Approver, "SafetyRisk", "Confidential");
        await GrantAsync("auditor", RoleCodes.Auditor, null, "Confidential");
    }

    /// <summary>Uploads the built package through the Pack Manager as the tenant administrator, installs it and returns the validation report.</summary>
    public async Task<PackUploadResult> UploadAndInstallAsync(string version = "1.1.1", byte[]? bytes = null)
    {
        PackVersion = version;
        bytes ??= UploadableCopy(version);
        var up = await AsAsync("admin", s => s.Get<IPackManager>().UploadAsync(new MemoryStream(bytes), $"{PackKey}-{version}.ecpkg"));
        Assert.True(up.Report.CanInstall, up.Report.Outcome + ": " + string.Join("\n", up.Report.Issues.Select(i => i.Severity + " " + i.Code + " " + i.Message)));
        await AsAsync("admin", s => s.Get<IPackManager>().InstallAsync(up.Token));
        return up;
    }

    public Task<PackAssignmentResult> AssignAsync() => AsAsync("admin", s => s.Get<IPackAssignments>().AssignAsync(Org, PackKey, PackVersion));

    /// <summary>Publishes one kind's open draft as the administrator, after validating it for the organisation.</summary>
    public async Task<ReleaseDto> PublishAsync(string kind)
    {
        var scopeOrg = kind is "ClassificationPolicy" or "Branding" ? Guid.Empty : Org;
        var draft = await AsAsync("admin", s => s.Get<IConfigurationService>().FindOpenDraftAsync(kind, scopeOrg));
        Assert.True(draft is not null, $"{kind}: no open draft.");
        var validated = await AsAsync("admin", s => s.Get<IConfigurationService>().ValidateAsync(draft!.Id));
        Assert.True(validated.IsPublishable, $"{kind}: " + string.Join("; ", validated.Validation!.Messages.Select(m => $"{m.Severity} {m.Code} {m.Path} {m.Message}")));
        return await AsAsync("admin", s => s.Get<IConfigurationService>().PublishAsync(validated.Id, "Harborline onboarding"));
    }

    /// <summary>The documented publication order (docs/CONSTRUCTION_SUBCONTRACTS_PACK.md): scale, fields, wording, matrices, workflows (each names a matrix), policy, views, branding.</summary>
    public static readonly string[] PublicationOrder =
    [
        "ClassificationPolicy", "CustomField", "Terminology", "ApprovalMatrix", WorkflowKinds.Variation, WorkflowKinds.ContractLifecycle, Ecp.Notifications.Policy.NotificationPolicyKinds.Policy, "ContractListView", "Branding"
    ];

    public static readonly string[] TenantLevelKinds = ["ClassificationPolicy", "Branding"];

    public async Task PublishAllAsync(bool includeTenantLevel = true)
    {
        foreach (var kind in PublicationOrder.Where(k => includeTenantLevel || !TenantLevelKinds.Contains(k))) await PublishAsync(kind);
    }

    /// <summary>
    /// The onboarding journey's structure step through the real service: two legal entities, regions and divisions, projects with their own time zones, the pack's trade packages and subcontract types, package managers, and group mappings.
    /// Approver groups are not modelled by onboarding (ASM-24); their members were granted when the people were created.
    /// </summary>
    public async Task OnboardAsync()
    {
        var defaults = App.Services.GetRequiredService<ISolutionPackCatalogue>().Find(PackKey)!.Defaults;
        var managers = new[] { "packageManagerA", "packageManagerB" };
        foreach (var m in managers) await GrantAsync(m, RoleCodes.CategoryOwner, null, "Confidential");
        await GrantAsync("projectViewer", RoleCodes.Viewer, null, "Internal", kind: ViewerKind.AllContracts);
        var emails = managers.ToDictionary(m => m, m => "hbl." + m + "@" + TenantId.ToString("N")[..8] + ".example.test");
        var doc = new OnboardingDocument(1, null,
            new StructureSection([new("HBL-US", "Harborline Construction LLC"), new("HBL-CA", "Harborline Canada ULC")],
                [new("CENTRAL", "Central Region"), new("SOUTH", "South Region")],
                [new("BUILD", "Buildings", "CENTRAL"), new("CIVIL", "Civil", "SOUTH")],
                [new("PRJ-LAKESIDE", "Lakeside Tower", null, "America/Chicago"), new("PRJ-FOOTHILLS", "Foothills Plant", null, "America/Denver")]),
            [.. defaults.Categories.Select(c => new CategoryEntry(c.Code, c.Name, c.ParentCode))],
            [.. defaults.ContractTypes.Select(c => new ContractTypeEntry(c.Code, c.Name))],
            [new("EARTHWORKS", emails["packageManagerA"], true), new("CONCRETE", emails["packageManagerA"], true), new("STEEL", emails["packageManagerB"], true), new("MECHANICAL", emails["packageManagerB"], true),
             new("ELECTRICAL", emails["packageManagerB"], true), new("FINISHES", emails["packageManagerA"], true)],
            [], []);
        await using var s = App.ScopeFor(TenantId);
        s.Get<CurrentUser>().Set(Users["admin"]);
        var release = await s.Get<IConfigurationBootstrap>().DraftValidatePublishAsync(OrganisationOnboardingKind.KindName, Org, doc.ToJson(), "onboarding-test", "Harborline onboarding");
        Assert.True(release.Number >= 1);
        var structure = await s.Get<IOrganisationStructureService>().GetAsync(Org);
        foreach (var site in structure.Sites) Sites[site.Key] = site.Id;
        foreach (var d in structure.Divisions) Divisions[d.Key] = d.Id;
        foreach (var c in await s.Get<ICategoryAdministration>().ListAllAsync()) Categories[c.Code] = c.Id;
        Subcontractor = await s.Get<ISupplierService>().CreateAsync(new CreateSupplierRequest(Org, "Lakeside Concrete LLC", VendorNumber: "SUB-1001"));
    }

    /// <summary>A world that has been taken from a fresh tenant to a published, onboarded organisation, the whole journey as an administrator does it.</summary>
    public static async Task<HarborlineWorld> ReadyAsync(TestApp app, bool upload = false)
    {
        var w = await CreateTenantAsync(app);
        if (upload) await w.UploadAndInstallAsync();
        var assigned = await w.AssignAsync();
        Assert.All(assigned.Kinds.Where(k => k.Kind != CapabilityKinds.Name), k => Assert.False(k.NeedsAttention, k.Kind + ": " + k.Outcome + " " + k.Detail));
        await w.PublishAllAsync();
        await w.OnboardAsync();
        return w;
    }

    // ---- contracts ----

    public static string Reference(int n) => $"{Prefix}-{n:000000}";

    public async Task<Guid> NewSubcontractAsync(decimal value = 100_000m, string? riskLevel = null, string? title = null, string owner = "packageManagerA", string trade = "CONCRETE", string site = "PRJ-LAKESIDE",
        string classification = "Internal", IReadOnlyDictionary<string, string?>? custom = null, string currency = "USD", DateOnly? end = null, int? lead = null, int? notice = null, bool critical = false)
    {
        var n = Interlocked.Increment(ref _n);
        var id = await AsAsync(owner, s => s.Get<ISecuredContractService>().CreateDraftAsync(new CreateContractRequest(Org, Reference(n), title ?? $"Subcontract {n}", Subcontractor, Categories[trade],
            Classification: classification, CurrencyCode: currency, EstimatedValue: value, RiskLevel: riskLevel, ContractType: "LUMP_SUM", SiteId: Sites[site], EndDate: end ?? DateOnly.FromDateTime(Now.AddDays(300)),
            CustomValues: custom ?? new Dictionary<string, string?> { ["project_number"] = "P-2026-01" })));
        if (lead is not null || notice is not null || critical) await AsAsync(owner, s => s.Get<ISecuredContractService>().UpdateReminderSettingsAsync(id, lead, notice, critical));
        return id;
    }

    public static MemoryStream Bytes(string text = "synthetic document") => new(System.Text.Encoding.UTF8.GetBytes(text));

    public Task Step(string user, Guid contract, string code, string? comment = null, string? reason = null)
        => AsAsync(user, s => s.Get<ILifecycleService>().TransitionAsync(new TransitionRequest(contract, code, comment, reason)));

    public Task<string> StatusAsync(Guid contract) => AsScopeless().Run(async s => (await s.Get<IContractLifecyclePort>().FindAsync(contract))!.Status);

    public Task AddDocumentAsync(string user, Guid contract, string type = "FinalAgreement", string title = "Subcontract agreement")
        => AsAsync(user, s => s.Get<ISecuredContractDocuments>().AddAsync(new AddContractDocumentRequest(contract, type, title, title + ".pdf", "application/pdf", Bytes(title))));

    /// <summary>Draft to Ready for signature (internal review first).</summary>
    public async Task ToReadyForSignatureAsync(Guid contract, string owner = "packageManagerA")
    {
        await Step(owner, contract, "SubmitForInternalReview");
        await Step("commercial", contract, "ReleaseForSignature");
    }

    /// <summary>Ready for signature to Sent for signature to a signed (executed) copy, through the mock signature service the Demo and Test installations carry.</summary>
    public async Task SignAsync(Guid contract, string owner = "packageManagerA")
    {
        await AddDocumentAsync(owner, contract);
        await AsAsync(owner, s => s.Get<ISignatureRequestService>().RequestSignatureAsync(SubjectKinds.Contract, contract, "mock"));
        await AsAsync(owner, s => s.Get<ISignatureRequestService>().SimulateEventAsync(SubjectKinds.Contract, contract, SignatureRequestStatus.Completed));
    }

    /// <summary>Draft to Awaiting activation approval through every step, including a signature request on the mock provider.</summary>
    public async Task<Guid> ToAwaitingActivationAsync(Guid contract, string owner = "packageManagerA")
    {
        await ToReadyForSignatureAsync(contract, owner);
        await SignAsync(contract, owner);
        return await AsAsync(owner, s => s.Get<IRegisterAuthorisationService>().SubmitAsync(SubjectKinds.Contract, contract, "Executed copy attached"));
    }

    public async Task<ApprovalDetail> DetailAsync(string user, Guid request) => await AsAsync(user, s => s.Get<IRegisterAuthorisationService>().GetAsync(request));

    /// <summary>Takes a subcontract all the way to Active, approving with the first tier's people and then whichever tiers its plan names.</summary>
    public async Task<Guid> ToActiveAsync(decimal value = 100_000m, string? riskLevel = null, IReadOnlyDictionary<string, string?>? custom = null, string owner = "packageManagerA", string trade = "CONCRETE", DateOnly? end = null, int? lead = null, int? notice = null, bool critical = false)
    {
        var id = await NewSubcontractAsync(value, riskLevel, owner: owner, trade: trade, custom: custom, end: end, lead: lead, notice: notice, critical: critical);
        var request = await ToAwaitingActivationAsync(id, owner);
        await ApproveAllAsync(request);
        Assert.Equal("Active", await StatusAsync(id));
        return id;
    }

    public async Task ApproveAllAsync(Guid request)
    {
        for (var guard = 0; guard < 8; guard++)
        {
            var detail = await DetailAsync("commercial", request);
            if (detail.Status != ApprovalStatus.Pending) return;
            var next = detail.Plan!.Steps.First(st => st.Outstanding.Count > 0);
            await AsAsync(ApproverFor(next.ApproverGroup), s => s.Get<IRegisterAuthorisationService>().ApproveAsync(request, "Approved"));
        }
    }

    public static string ApproverFor(string group) => group switch
    {
        "ProjectDirectors" => "director1", "CommercialManagers" => "commercialManager", "RegionalDirectors" => "regionalDirector", "SafetyRisk" => "safetyManager", _ => throw new ArgumentOutOfRangeException(nameof(group), group, "No test approver for this group.")
    };

    /// <summary>Takes a change order through preparation, signature on the mock provider and submission, ready for the approval matrix.</summary>
    public async Task<(Guid VariationId, Guid RequestId)> ChangeOrderToApprovalAsync(Guid contract, VariationInput input, string owner = "packageManagerA")
    {
        var variation = await AsAsync(owner, s => s.Get<IVariationService>().CreateAsync(contract, input));
        await AsAsync(owner, s => s.Get<IVariationService>().TransitionAsync(variation, "Prepare", null));
        await AsAsync(owner, s => s.Get<IVariationService>().AddDocumentAsync(new AddVariationDocumentRequest(variation, "Final", "Change order", "co.pdf", "application/pdf", Bytes("change order"))));
        await AsAsync(owner, s => s.Get<ISignatureRequestService>().RequestSignatureAsync(SubjectKinds.Variation, variation, "mock"));
        await AsAsync(owner, s => s.Get<ISignatureRequestService>().SimulateEventAsync(SubjectKinds.Variation, variation, SignatureRequestStatus.Completed));
        var request = await AsAsync(owner, s => s.Get<IRegisterAuthorisationService>().SubmitAsync(SubjectKinds.Variation, variation, "Signed change order"));
        return (variation, request);
    }

    public Task<ContractFacts> FactsAsync(Guid contract) => AsScopeless().Run(async s => (await s.Get<IContractLifecyclePort>().FindAsync(contract))!);

    /// <summary>The value of a configured field on a contract as the signed-in person sees it ("" when it has none).</summary>
    public async Task<string> CustomValueAsync(Guid contract, string key, string user = "commercial")
    {
        var form = await AsAsync(user, s => s.Get<Ecp.Contracts.IContractCustomFields>().ForContractAsync(contract));
        return form.Fields.FirstOrDefault(f => f.Definition.Key == key) is { HasValue: true } entry ? string.Join(",", entry.Raw) : "";
    }

    public Task<Ecp.Approvals.ApprovalResolution> ResolveAsync(string matrix, Ecp.Approvals.Resolution.ApprovalFacts facts, Guid contract, Guid? submitter = null, DateTime? asOf = null)
        => AsScopeless().Run(async s =>
        {
            var resource = await s.Get<ISecuredContractService>().DescribeAsync(contract);
            return await s.Get<Ecp.Approvals.IApprovalResolver>().ResolveAsync(new Ecp.Approvals.ResolutionRequest(Org, matrix, facts, resource, submitter, asOf ?? Now, Ecp.Approvals.MatrixSource.Published));
        });

    public static Ecp.Approvals.Resolution.ApprovalFacts Facts(decimal value, string currency = "USD", string? risk = null, Dictionary<string, string?>? custom = null, string? contractType = "LUMP_SUM")
        => new(value, currency, ["CONCRETE"], contractType, null, null, null, "Internal", risk, null, custom ?? []);

    /// <summary>The shape of a plan, one entry per step: key at stage with its mode.</summary>
    public static string Shape(Ecp.Approvals.ApprovalResolution r) => string.Join(", ", r.Plan!.Steps.OrderBy(x => x.Stage).ThenBy(x => x.Key, StringComparer.Ordinal).Select(x => $"{x.Key[(x.Key.IndexOf('/') + 1)..]}@{x.Stage}:{x.Mode}"));

    public Task RunJobsAsync() => AsScopeless().Run(s => s.Get<Ecp.Notifications.INotificationJobs>().RunAllAsync());

    public Task<Ecp.Notifications.ReminderScanResult> ScanRemindersAsync() => AsScopeless().Run(s => s.Get<Ecp.Notifications.IReminderScanner>().ScanAsync());

    public async Task<List<Ecp.Notifications.InboxItem>> InboxAsync(string user)
        => (await AsAsync(user, s => s.Get<Ecp.Notifications.INotificationInbox>().ListAsync(new Ecp.Notifications.InboxFilter(PageSize: 200)))).Items.ToList();

    public DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(Now, TimeZoneInfo.FindSystemTimeZoneById("America/Chicago")));
}
