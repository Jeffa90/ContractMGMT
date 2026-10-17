using System.Text.RegularExpressions;
using Ecp.Audit;
using Ecp.Configuration;
using Ecp.Contracts;
using Ecp.Identity;
using Ecp.Identity.Domain;
using Ecp.Lifecycle;
using Ecp.Lifecycle.Definitions;
using Ecp.Packs;
using Ecp.SharedKernel;
using Ecp.Tenancy;
using Ecp.Tests.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Ecp.Integration.Tests;

/// <summary>
/// S7: the Pack Manager services through the real composition, on each provider. Upload, validate, preview, install, assign, retire and the trust store run as signed-in users against the real access evaluator,
/// so authorisation, tenant isolation and audit are exercised as the application uses them. Packs are built in memory by the real builder; nothing here names a business.
/// </summary>
internal static class PackManagerScenarios
{
    private static MemoryStream Stream(byte[] bytes) => new(bytes);

    private static Task<PackUploadResult> UploadAsync(LifecycleWorld w, string user, byte[] bytes, string name = "test.ecpkg")
        => w.W.AsAsync(user, s => s.Get<IPackManager>().UploadAsync(Stream(bytes), name));

    private static async Task<PackCatalogueEntry> InstallAsync(LifecycleWorld w, string user, byte[] bytes)
    {
        var up = await UploadAsync(w, user, bytes);
        Assert.True(up.Report.CanInstall, string.Join("; ", up.Report.Issues.Select(i => i.Code + " " + i.Message)));
        var result = await w.W.AsAsync(user, s => s.Get<IPackManager>().InstallAsync(up.Token));
        return result.Entry ?? throw new InvalidOperationException("not installed");
    }

    /// <summary>A business configuration administrator for one organisation: the role that holds both assignment of packs and drafting and publishing of configuration.</summary>
    private static async Task<string> ConfigAdminForAsync(LifecycleWorld w, Guid organisation, string key)
    {
        await using var s = w.W.AsScopeless();
        var id = await s.Get<IUserService>().CreateActiveTrustedAsync(key, key + "@" + w.W.TenantId.ToString("N")[..8] + ".example.test", "demo", "https://demo.example.test", key + "|" + w.W.TenantId, "test");
        await s.Get<IRoleAssignmentService>().GrantTrustedAsync(new GrantRequest(id, RoleCodes.BusinessConfigurationAdministrator, null, ScopeSet.ForOrganisations([organisation], "Internal"), null,
            EffectiveFromUtc: w.W.Now.AddDays(-5), ExplicitHighCeiling: true), AssignmentSource.Demo, "test");
        w.W.Users[key] = id;
        return key;
    }

    private static string Unique(string prefix) => prefix + "-" + Guid.NewGuid().ToString("N")[..8];

    public static async Task A_pack_is_uploaded_checked_previewed_installed_and_repeat_uploads_are_harmless(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        var key = Unique("flow");
        var bytes = PackTestKit.Build(key, "1.0.0");

        var up = await UploadAsync(w, "tenantAdmin", bytes);
        Assert.Equal(PackValidationOutcome.ValidWithWarnings, up.Report.Outcome);                 // unsigned synthetic pack in a Test environment: usable, with a warning
        Assert.Contains(up.Report.Issues, i => i.Code == PackCodes.UnsignedSyntheticAllowed);
        Assert.Equal(key, up.Report.Summary!.PackKey);

        // Nothing is installed by an upload.
        var before = await w.W.AsAsync("tenantAdmin", s => s.Get<IPackManager>().ListAsync());
        Assert.DoesNotContain(before, i => i.Entry.PackKey == key);

        var preview = await w.W.AsAsync("tenantAdmin", s => s.Get<IPackManager>().PreviewAsync(up.Token));
        Assert.NotNull(preview);
        Assert.Contains("will not publish any configuration", preview!.Statement);

        var installed = await w.W.AsAsync("tenantAdmin", s => s.Get<IPackManager>().InstallAsync(up.Token));
        Assert.False(installed.AlreadyAvailable);
        Assert.Equal(key, installed.Entry!.PackKey);
        Assert.Equal(Domain_Source.TenantUploaded, installed.Entry.Source);
        Assert.True(installed.Entry.IsUsable);
        // The quarantine copy is gone: the same token cannot install twice.
        await Assert.ThrowsAsync<DomainException>(() => w.W.AsAsync("tenantAdmin", s => s.Get<IPackManager>().InstallAsync(up.Token)));

        // The very same bytes again are recognised and install nothing new.
        var again = await UploadAsync(w, "tenantAdmin", bytes);
        Assert.Equal(PackValidationOutcome.Duplicate, again.Report.Outcome);
        var repeat = await w.W.AsAsync("tenantAdmin", s => s.Get<IPackManager>().InstallAsync(again.Token));
        Assert.True(repeat.AlreadyAvailable);
        var after = await w.W.AsAsync("tenantAdmin", s => s.Get<IPackManager>().ListAsync());
        Assert.Single(after, i => i.Entry.PackKey == key);

        // Different bytes under the same key and version are refused: versions never change.
        var different = PackTestKit.Build(key, "1.0.0", solutionPackJson: $$"""
            { "packId": "{{key}}", "name": "Changed", "version": "1.0.0", "synthetic": true, "publisher": "Test publisher", "description": "Changed after release.", "features": [], "ownershipModel": "ReadAllEditAssignedCategory",
              "approvalModel": "None", "defaults": { "classification": { "levels": ["Public", "Internal", "Confidential", "Restricted", "Legal Privileged"], "defaultLevel": "Internal", "privilegedLevels": ["Legal Privileged"] }, "contractTypes": [], "categories": [] } }
            """);
        var conflict = await UploadAsync(w, "tenantAdmin", different);
        Assert.Equal(PackValidationOutcome.Conflict, conflict.Report.Outcome);
        Assert.False(conflict.Report.CanInstall);
        await Assert.ThrowsAsync<DomainException>(() => w.W.AsAsync("tenantAdmin", s => s.Get<IPackManager>().InstallAsync(conflict.Token)));

        // Everything above is in the audit history.
        var audit = await w.W.AsAsync("tenantAdmin", s => s.Get<IPackManager>().AuditAsync());
        Assert.Contains(audit.Items, a => a.Action == "pack.upload.verified");
        Assert.Contains(audit.Items, a => a.Action == "pack.installed" && a.Subject == $"{key}@1.0.0");
        Assert.Contains(audit.Items, a => a.Action == "pack.upload.rejected");
    }

    public static async Task Pack_permissions_follow_the_confirmed_matrix_and_trust_keys_stay_with_the_tenant_administrator(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        await w.UserAsync("bca", RoleCodes.BusinessConfigurationAdministrator, null, "Internal");
        var bytes = PackTestKit.Build(Unique("perm"), "1.0.0");
        var (_, publicSpki) = PackKeys.Generate();
        var pem = PackKeys.ToPublicPem(publicSpki);

        // People who only work with contracts cannot even see the packs.
        foreach (var user in new[] { "ownerPackaging", "viewerAll", "legalA" })
        {
            await Assert.ThrowsAsync<ForbiddenException>(() => w.W.AsAsync(user, s => s.Get<IPackManager>().ListAsync()));
            await Assert.ThrowsAsync<ForbiddenException>(() => UploadAsync(w, user, bytes));
            await Assert.ThrowsAsync<ForbiddenException>(() => w.W.AsAsync(user, s => s.Get<IPackTrustKeys>().ListAsync()));
        }

        // An organisation administrator can look and assign but cannot upload or install; the auditor can read the history and nothing else.
        await w.W.AsAsync("orgAdmin", s => s.Get<IPackManager>().ListAsync());
        await Assert.ThrowsAsync<ForbiddenException>(() => UploadAsync(w, "orgAdmin", bytes));
        await Assert.ThrowsAsync<ForbiddenException>(() => w.W.AsAsync("orgAdmin", s => s.Get<IPackManager>().AuditAsync()));
        await w.W.AsAsync("auditor", s => s.Get<IPackManager>().AuditAsync());
        await Assert.ThrowsAsync<ForbiddenException>(() => UploadAsync(w, "auditor", bytes));

        // The business configuration administrator can upload and install an approved pack, but cannot retire it or touch trust keys.
        var entry = await InstallAsync(w, "bca", bytes);
        await Assert.ThrowsAsync<ForbiddenException>(() => w.W.AsAsync("bca", s => s.Get<IPackManager>().RetireAsync(entry.PackKey, entry.Version, "no")));
        await Assert.ThrowsAsync<ForbiddenException>(() => w.W.AsAsync("bca", s => s.Get<IPackTrustKeys>().RegisterAsync("k1", "Someone", pem, null, null)));
        await Assert.ThrowsAsync<ForbiddenException>(() => w.W.AsAsync("bca", s => s.Get<IPackTrustKeys>().ListAsync()));

        // The tenant administrator holds retire and trust management.
        await w.W.AsAsync("tenantAdmin", s => s.Get<IPackTrustKeys>().RegisterAsync("k1", "A test publisher", pem, null, null));
        await w.W.AsAsync("tenantAdmin", s => s.Get<IPackManager>().RetireAsync(entry.PackKey, entry.Version, "superseded"));
        var retired = await w.W.AsAsync("tenantAdmin", s => s.Get<IPackManager>().GetAsync(entry.PackKey, entry.Version));
        Assert.True(retired!.Entry.Retired);
        Assert.False(retired.Entry.IsUsable);
    }

    public static async Task Packs_and_keys_belong_to_one_tenant_and_an_upload_cannot_be_used_by_another_person_or_tenant(TestApp app)
    {
        var a = await LifecycleWorld.CreateAsync(app);
        var b = await LifecycleWorld.CreateAsync(app);
        var key = Unique("iso");
        var bytes = PackTestKit.Build(key, "1.0.0");
        var up = await UploadAsync(a, "tenantAdmin", bytes);
        await a.W.AsAsync("tenantAdmin", s => s.Get<IPackManager>().InstallAsync(up.Token));
        var (_, spki) = PackKeys.Generate();
        await a.W.AsAsync("tenantAdmin", s => s.Get<IPackTrustKeys>().RegisterAsync("iso-key", "Tenant A publisher", PackKeys.ToPublicPem(spki), null, null));

        // Tenant B does not see tenant A's pack or key.
        var listB = await b.W.AsAsync("tenantAdmin", s => s.Get<IPackManager>().ListAsync());
        Assert.DoesNotContain(listB, i => i.Entry.PackKey == key);
        var keysB = await b.W.AsAsync("tenantAdmin", s => s.Get<IPackTrustKeys>().ListAsync());
        Assert.DoesNotContain(keysB, k => k.KeyId == "iso-key");
        Assert.Null(await b.W.AsAsync("tenantAdmin", s => s.Get<IPackManager>().GetAsync(key, "1.0.0")));

        // A quarantined upload is not usable by another person in the same tenant, nor by another tenant.
        var held = await UploadAsync(a, "tenantAdmin", PackTestKit.Build(Unique("held"), "1.0.0"));
        await w_Cannot_use(a, "orgAdmin", held.Token);
        await w_Cannot_use(b, "tenantAdmin", held.Token);
        Assert.NotNull(await a.W.AsAsync("tenantAdmin", s => s.Get<IPackManager>().PreviewAsync(held.Token)));

        // The same pack can be installed independently in tenant B; neither installation affects the other.
        var inB = await InstallAsync(b, "tenantAdmin", bytes);
        Assert.Equal(key, inB.PackKey);
    }

    private static async Task w_Cannot_use(LifecycleWorld w, string user, Guid token)
    {
        try
        {
            var preview = await w.W.AsAsync(user, s => s.Get<IPackManager>().PreviewAsync(token));
            Assert.Null(preview);
        }
        catch (ForbiddenException) { /* also acceptable: this person may not preview at all */ }
        try { await w.W.AsAsync(user, s => s.Get<IPackManager>().InstallAsync(token)); Assert.Fail("The upload was usable by someone who did not make it."); }
        catch (Exception ex) when (ex is DomainException or ForbiddenException) { }
    }

    public static async Task Assigning_a_pack_creates_drafts_only_and_never_overwrites_published_configuration(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        var key = Unique("assign");
        var entry = await InstallAsync(w, "tenantAdmin", PackTestKit.Build(key, "1.0.0", content: ThirdPackContent(out var docs), extraDocuments: docs));

        // The organisation already works from the Blank pack and has a published lifecycle.
        var blankOrg = await PackResolutionScenarios.AddOrganisationAsync(w, "Assign Org", "ASN", TestPacks.Blank);
        var contract = await w.W.AsAsync(blankOrg.User, s => s.Get<ISecuredContractService>().CreateDraftAsync(new CreateContractRequest(blankOrg.Org, "ASN-000001", "Existing", blankOrg.Supplier, blankOrg.Category)));
        var admin = await ConfigAdminForAsync(w, blankOrg.Org, "cfgAssign");
        var publishedBefore = await w.W.AsAsync(admin, s => s.Get<IConfigurationService>().LatestReleaseAsync(WorkflowKinds.ContractLifecycle, blankOrg.Org));
        Assert.NotNull(publishedBefore);

        var preview = await w.W.AsAsync(admin, s => s.Get<IPackAssignments>().PreviewAsync(blankOrg.Org, key, "1.0.0"));
        Assert.Contains(preview.Items, i => i.Kind == WorkflowKinds.ContractLifecycle && i.HasPublishedRelease);
        Assert.Contains(preview.Warnings, m => m.Contains("Blank", StringComparison.OrdinalIgnoreCase) || m.Contains("blank"));

        var result = await w.W.AsAsync(admin, s => s.Get<IPackAssignments>().AssignAsync(blankOrg.Org, key, "1.0.0"));
        Assert.Contains(result.Kinds, k => k.Kind == WorkflowKinds.ContractLifecycle && k.Outcome.StartsWith("Draft created"));
        Assert.Contains("Nothing has been published", result.NextSteps[0]);

        // The published release is untouched, a draft waits, and the existing contract is unchanged.
        var publishedAfter = await w.W.AsAsync(admin, s => s.Get<IConfigurationService>().LatestReleaseAsync(WorkflowKinds.ContractLifecycle, blankOrg.Org));
        Assert.Equal(publishedBefore!.Number, publishedAfter!.Number);
        Assert.Equal(publishedBefore.ContentJson, publishedAfter.ContentJson);
        var draft = await w.W.AsAsync(admin, s => s.Get<IConfigurationService>().FindOpenDraftAsync(WorkflowKinds.ContractLifecycle, blankOrg.Org));
        Assert.NotNull(draft);
        Assert.Contains("Intake", draft!.ContentJson);
        var state = await w.W.AsAsync(blankOrg.User, s => s.Get<ILifecycleService>().StateAsync(contract));
        Assert.Equal("Open", state.Status);

        // Assigning again with a draft waiting keeps that draft.
        var second = await w.W.AsAsync(admin, s => s.Get<IPackAssignments>().AssignAsync(blankOrg.Org, key, "1.0.0"));
        Assert.Contains(second.Kinds, k => k.Outcome.Contains("kept"));

        // A retired pack cannot be assigned to another organisation.
        var other = await PackResolutionScenarios.AddOrganisationAsync(w, "Retired Org", "RET", null);
        var otherAdmin = await ConfigAdminForAsync(w, other.Org, "cfgRetired");
        await w.W.AsAsync("tenantAdmin", s => s.Get<IPackManager>().RetireAsync(key, "1.0.0", "no longer wanted"));
        await Assert.ThrowsAsync<DomainException>(() => w.W.AsAsync(otherAdmin, s => s.Get<IPackAssignments>().AssignAsync(other.Org, key, "1.0.0")));
        _ = entry;
    }

    /// <summary>The Blank pack's lifecycle, Variation workflow and notification policy with the initial state renamed: a pack that no source file in the repository knows about.</summary>
    internal static IEnumerable<(string, string, string, string, int, bool)> ThirdPackContent(out Dictionary<string, string> documents)
    {
        var root = Path.Combine(RepoRoot.Find(), "templates", "packs", "blank");
        var lifecycle = File.ReadAllText(Path.Combine(root, "workflows", "contract-lifecycle.json"));
        lifecycle = Regex.Replace(lifecycle, "\"code\": \"Open\", \"display\": \"Open\"", "\"code\": \"Intake\", \"display\": \"Intake\"");
        lifecycle = Regex.Replace(lifecycle, "\"from\": \\[ \"Open\" \\]", "\"from\": [ \"Intake\" ]");
        Assert.Contains("\"Intake\"", lifecycle);
        documents = new Dictionary<string, string>
        {
            ["lifecycle.json"] = lifecycle,
            ["variation.json"] = File.ReadAllText(Path.Combine(root, "workflows", "variation.json")),
            ["notifications.json"] = File.ReadAllText(Path.Combine(root, "notifications", "notification-policy.json")),
        };
        return
        [
            ("lifecycle.json", "content/contract-lifecycle.json", "WorkflowContractLifecycle", "organisation", 1, true),
            ("variation.json", "content/variation-workflow.json", "WorkflowVariation", "organisation", 1, true),
            ("notifications.json", "content/notification-policy.json", "NotificationPolicy", "organisation", 1, true),
        ];
    }

    public static async Task A_third_pack_the_repository_has_never_seen_runs_a_contract_with_no_source_change(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        var key = Unique("thirdparty");
        var package = PackTestKit.Build(key, "1.0.0", content: ThirdPackContent(out var docs), extraDocuments: docs);
        await InstallAsync(w, "tenantAdmin", package);

        var org = await PackResolutionScenarios.AddOrganisationAsync(w, "Third Business", "TRD", null);
        var admin = await ConfigAdminForAsync(w, org.Org, "cfgThird");
        var assigned = await w.W.AsAsync(admin, s => s.Get<IPackAssignments>().AssignAsync(org.Org, key, "1.0.0"));
        Assert.All(assigned.Kinds, k => Assert.False(k.NeedsAttention, k.Kind + ": " + k.Outcome + " " + k.Detail));

        // The administrator reviews and publishes each draft, as the product requires. Only then does the organisation run on the pack.
        foreach (var kind in new[] { WorkflowKinds.ContractLifecycle, WorkflowKinds.Variation, Ecp.Notifications.Policy.NotificationPolicyKinds.Policy })
        {
            var draft = await w.W.AsAsync(admin, s => s.Get<IConfigurationService>().FindOpenDraftAsync(kind, org.Org));
            Assert.NotNull(draft);
            await w.W.AsAsync(admin, s => s.Get<IConfigurationService>().PublishAsync(draft!.Id, "Reviewed pack content"));
        }

        var contract = await w.W.AsAsync(org.User, s => s.Get<ISecuredContractService>().CreateDraftAsync(new CreateContractRequest(org.Org, "TRD-000001", "Third pack", org.Supplier, org.Category)));
        var state = await w.W.AsAsync(org.User, s => s.Get<ILifecycleService>().StateAsync(contract));
        Assert.Equal("Intake", state.Status);                                                      // a state no compiled code or shipped pack contains
        await w.W.AsAsync(org.User, s => s.Get<ILifecycleService>().TransitionAsync(new TransitionRequest(contract, "Activate")));
        Assert.Equal("Active", (await w.W.AsAsync(org.User, s => s.Get<ILifecycleService>().StateAsync(contract))).Status);

        // The provenance records which pack, version and package the starting configuration came from.
        var status = await w.W.AsAsync(admin, s => s.Get<IPackAssignments>().OrganisationsAsync());
        var row = status.Single(o => o.OrganisationId == org.Org);
        Assert.Equal(key, row.PackKey);
        Assert.Equal("1.0.0", row.Version);
        Assert.True(row.PackUsable);
    }

    public static async Task Production_refuses_unsigned_and_untrusted_packs_until_the_publisher_key_is_trusted(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        var key = Unique("prod");
        using var signer = new TestSigningKey("publisher-1");
        var unsigned = PackTestKit.Build(key, "1.0.0");
        var signed = PackTestKit.Sign(unsigned, signer);

        // Unsigned: refused outright.
        var u = await UploadAsync(w, "tenantAdmin", unsigned);
        Assert.Equal(PackValidationOutcome.Untrusted, u.Report.Outcome);
        Assert.False(u.Report.CanInstall);
        await Assert.ThrowsAsync<DomainException>(() => w.W.AsAsync("tenantAdmin", s => s.Get<IPackManager>().InstallAsync(u.Token)));

        // Signed by a key nobody trusts: refused, and the report names the unknown key without exposing key material.
        var s1 = await UploadAsync(w, "tenantAdmin", signed);
        Assert.Equal(PackValidationOutcome.Untrusted, s1.Report.Outcome);
        Assert.Contains(s1.Report.Issues, i => i.Code == PackCodes.KeyUnknown);

        // The tenant administrator registers the publisher's PUBLIC key; the same file is now accepted and fully trusted.
        await w.W.AsAsync("tenantAdmin", s => s.Get<IPackTrustKeys>().RegisterAsync("publisher-1", "Test publisher", PackKeys.ToPublicPem(signer.PublicSpki), null, null));
        var s2 = await UploadAsync(w, "tenantAdmin", signed);
        Assert.Equal(PackValidationOutcome.Valid, s2.Report.Outcome);
        Assert.True(s2.Report.Summary!.ProductionEligible);
        var installed = await w.W.AsAsync("tenantAdmin", s => s.Get<IPackManager>().InstallAsync(s2.Token));
        Assert.True(installed.Entry!.Trust.IsTrusted);

        // Revoking the key stops the pack from being usable, without deleting anything.
        await w.W.AsAsync("tenantAdmin", s => s.Get<IPackTrustKeys>().RevokeAsync("publisher-1", "key retired"));
        var after = await w.W.AsAsync("tenantAdmin", s => s.Get<IPackManager>().GetAsync(key, "1.0.0"));
        Assert.False(after!.Entry.IsUsable);
        var revoked = await UploadAsync(w, "tenantAdmin", PackTestKit.Sign(PackTestKit.Build(Unique("prod2"), "1.0.0"), signer));
        Assert.Equal(PackValidationOutcome.Untrusted, revoked.Report.Outcome);

        // Key administration is in the audit history, and no history entry carries key material.
        var audit = await w.W.AsAsync("tenantAdmin", s => s.Get<IPackManager>().AuditAsync());
        Assert.Contains(audit.Items, a => a.Action == "pack.trust.registered");
        Assert.Contains(audit.Items, a => a.Action == "pack.trust.revoked");
        Assert.DoesNotContain(audit.Items, a => (a.Details ?? "").Contains("BEGIN"));
    }

    public static async Task The_package_cache_is_keyed_by_tenant_and_package_so_one_tenant_never_reads_anothers_bytes(TestApp app)
    {
        var a = await LifecycleWorld.CreateAsync(app);
        var b = await LifecycleWorld.CreateAsync(app);
        var key = Unique("cache");
        string Doc(string name) => $$"""
            { "packId": "{{key}}", "name": "{{name}}", "version": "1.0.0", "synthetic": true, "publisher": "Test publisher", "description": "d", "features": [], "ownershipModel": "ReadAllEditAssignedCategory", "approvalModel": "None",
              "defaults": { "classification": { "levels": ["Public"], "defaultLevel": "Public", "privilegedLevels": [] }, "contractTypes": [], "categories": [] } }
            """;
        // The same pack key and version, different bytes, in two tenants: allowed, because a pack belongs to the tenant that installed it.
        await InstallAsync(a, "tenantAdmin", PackTestKit.Build(key, "1.0.0", solutionPackJson: Doc("Tenant A edition")));
        await InstallAsync(b, "tenantAdmin", PackTestKit.Build(key, "1.0.0", solutionPackJson: Doc("Tenant B edition")));
        for (var i = 0; i < 3; i++)                                   // repeated reads exercise the cache
        {
            var docA = await a.W.AsAsync("tenantAdmin", s => s.Get<IPackLoader>().GetDocumentAsync(key, "1.0.0", "SolutionPack"));
            var docB = await b.W.AsAsync("tenantAdmin", s => s.Get<IPackLoader>().GetDocumentAsync(key, "1.0.0", "SolutionPack"));
            Assert.Contains("Tenant A edition", docA);
            Assert.DoesNotContain("Tenant B edition", docA);
            Assert.Contains("Tenant B edition", docB);
            Assert.DoesNotContain("Tenant A edition", docB);
        }
        // Retiring in tenant A is seen at once there and not in tenant B.
        await a.W.AsAsync("tenantAdmin", s => s.Get<IPackManager>().RetireAsync(key, "1.0.0", "test"));
        Assert.Null(await a.W.AsAsync("tenantAdmin", s => s.Get<IPackLoader>().FindAsync(key, "1.0.0", usableOnly: true)));
        Assert.NotNull(await b.W.AsAsync("tenantAdmin", s => s.Get<IPackLoader>().FindAsync(key, "1.0.0", usableOnly: true)));
    }

    public static async Task Diagnostics_report_unresolved_packs_and_fallback_definitions_without_changing_anything(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        // An organisation on the installation default (no pack of its own) gets the default pack's definitions when its first contract is created; it is then assigned a different pack.
        var fell = await PackResolutionScenarios.AddOrganisationAsync(w, "Fallback Org", "FBK", null);
        await w.W.AsAsync(fell.User, s => s.Get<ISecuredContractService>().CreateDraftAsync(new CreateContractRequest(fell.Org, "FBK-000001", "Fallback", fell.Supplier, fell.Category)));
        await using (var s = w.W.AsScopeless()) await s.Get<IOrganisationStructureService>().SetSolutionPackAsync(fell.Org, TestPacks.Blank);
        var ghost = await PackResolutionScenarios.AddOrganisationAsync(w, "Ghost Org", "GHO", "a-pack-nobody-installed");

        var releasesBefore = await w.W.AsAsync("tenantAdmin", s => s.Get<IConfigurationService>().ReleasesAsync(WorkflowKinds.ContractLifecycle, fell.Org));
        var findings = await w.W.AsAsync("tenantAdmin", s => s.Get<IPackDiagnostics>().OrganisationFindingsAsync());
        Assert.Contains(findings, f => f.OrganisationId == ghost.Org && f.Code == "pack_unresolved");
        Assert.Contains(findings, f => f.OrganisationId == fell.Org && f.Code == "starting_definitions_from_other_pack");
        Assert.DoesNotContain(findings, f => f.OrganisationId == w.W.Org1);
        Assert.All(findings, f => { Assert.DoesNotContain("   at ", f.Message); Assert.DoesNotContain(Path.GetTempPath(), f.Message); });

        // Reporting changed nothing.
        var releasesAfter = await w.W.AsAsync("tenantAdmin", s => s.Get<IConfigurationService>().ReleasesAsync(WorkflowKinds.ContractLifecycle, fell.Org));
        Assert.Equal(releasesBefore.Count, releasesAfter.Count);

        var sources = await w.W.AsAsync("tenantAdmin", s => s.Get<IPackDiagnostics>().SourcesAsync());
        Assert.True(sources.DirectoryFound);
        Assert.True(sources.UsableShippedPacks >= 2);
        Assert.NotEmpty(sources.Directory);                                                      // only a folder name is shown, never a server path
        Assert.DoesNotContain(Path.DirectorySeparatorChar, sources.Directory);
    }

    public static async Task Concurrent_uploads_and_installs_of_one_pack_leave_exactly_one_installed_copy(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        var key = Unique("race");
        var bytes = PackTestKit.Build(key, "1.0.0");
        var uploads = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => UploadAsync(w, "tenantAdmin", bytes)));
        var results = await Task.WhenAll(uploads.Select(async u =>
        {
            try { return (await w.W.AsAsync("tenantAdmin", s => s.Get<IPackManager>().InstallAsync(u.Token))).Entry?.PackKey; }
            catch (DomainException) { return null; }
        }));
        Assert.Contains(key, results);
        var list = await w.W.AsAsync("tenantAdmin", s => s.Get<IPackManager>().ListAsync());
        Assert.Single(list, i => i.Entry.PackKey == key);
    }

    public static async Task Hostile_packages_are_rejected_with_plain_messages_and_nothing_is_stored(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        var good = PackTestKit.Build(Unique("host"), "1.0.0");
        var entries = PackTestKit.Entries(good);

        var cases = new Dictionary<string, byte[]>
        {
            ["not a zip"] = PackTestKit.Utf8("this is not a package"),
            ["empty"] = [],
            ["path traversal"] = PackTestKit.Rezip(new Dictionary<string, byte[]>(entries) { ["content/../../evil.json"] = PackTestKit.Utf8("{}") }),
            ["unexpected file"] = PackTestKit.Rezip(new Dictionary<string, byte[]>(entries) { ["content/run.ps1"] = PackTestKit.Utf8("Write-Host hi") }),
            ["tampered content"] = PackTestKit.Rezip(entries.ToDictionary(e => e.Key, e => e.Key == "content/solution-pack.json" ? PackTestKit.Utf8("{ \"packId\": \"x\" }") : e.Value)),
        };
        foreach (var (name, bytes) in cases)
        {
            var up = await UploadAsync(w, "tenantAdmin", bytes);
            Assert.False(up.Report.CanInstall, name);
            Assert.All(up.Report.Issues, i =>
            {
                Assert.DoesNotContain("   at ", i.Message);                                         // no stack trace
                Assert.DoesNotContain(Path.GetTempPath(), i.Message);                              // no server path
                Assert.DoesNotContain("Exception", i.Message);
            });
            await Assert.ThrowsAsync<DomainException>(() => w.W.AsAsync("tenantAdmin", s => s.Get<IPackManager>().InstallAsync(up.Token)));
        }
        var list = await w.W.AsAsync("tenantAdmin", s => s.Get<IPackManager>().ListAsync());
        Assert.DoesNotContain(list, i => !i.Entry.IsShipped);
    }

    public static async Task The_validation_report_csv_defuses_formulas_and_shows_no_server_paths(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        var hostile = PackTestKit.Build("csvpack", "1.0.0", solutionPackJson: """
            { "packId": "csvpack", "name": "=1+1", "version": "1.0.0", "synthetic": true, "publisher": "@cmd", "description": "+SUM(A1)", "features": [], "ownershipModel": "ReadAllEditAssignedCategory", "approvalModel": "None",
              "defaults": { "classification": { "levels": ["Public"], "defaultLevel": "Public", "privilegedLevels": [] }, "contractTypes": [], "categories": [] } }
            """);
        var up = await UploadAsync(w, "tenantAdmin", hostile);
        var csv = System.Text.Encoding.UTF8.GetString((await w.W.AsAsync("tenantAdmin", s => s.Get<IPackManager>().ReportCsvAsync(up.Token)))!);
        foreach (var line in csv.Split('\n')) foreach (var cell in Regex.Matches(line, "\"((?:[^\"]|\"\")*)\"|([^,\"]+)").Select(m => m.Value.Trim('"')))
            Assert.False(cell.StartsWith('=') || cell.StartsWith('@') || cell.StartsWith('+') || (cell.StartsWith('-') && cell.Length > 1 && !char.IsDigit(cell[1])), "formula cell: " + cell);
    }
}

internal static class Domain_Source { public const string TenantUploaded = "TenantUploaded"; }

[Trait("Category", "Packs")]
public sealed class PackManagerScenarioTests_Sqlite
{
    [Fact] public Task Install_flow() => ProviderRunner.SqliteAsync(PackManagerScenarios.A_pack_is_uploaded_checked_previewed_installed_and_repeat_uploads_are_harmless);
    [Fact] public Task Permissions() => ProviderRunner.SqliteAsync(PackManagerScenarios.Pack_permissions_follow_the_confirmed_matrix_and_trust_keys_stay_with_the_tenant_administrator);
    [Fact] public Task Isolation() => ProviderRunner.SqliteAsync(PackManagerScenarios.Packs_and_keys_belong_to_one_tenant_and_an_upload_cannot_be_used_by_another_person_or_tenant);
    [Fact] public Task Assignment() => ProviderRunner.SqliteAsync(PackManagerScenarios.Assigning_a_pack_creates_drafts_only_and_never_overwrites_published_configuration);
    [Fact] public Task Third_pack() => ProviderRunner.SqliteAsync(PackManagerScenarios.A_third_pack_the_repository_has_never_seen_runs_a_contract_with_no_source_change);
    [Fact] public async Task Production_trust() { await using var app = await TestApp.CreateSqliteAsync(Ecp.SharedKernel.EcpMode.Production); await PackManagerScenarios.Production_refuses_unsigned_and_untrusted_packs_until_the_publisher_key_is_trusted(app); }
    [Fact] public Task Cache() => ProviderRunner.SqliteAsync(PackManagerScenarios.The_package_cache_is_keyed_by_tenant_and_package_so_one_tenant_never_reads_anothers_bytes);
    [Fact] public Task Diagnostics() => ProviderRunner.SqliteAsync(PackManagerScenarios.Diagnostics_report_unresolved_packs_and_fallback_definitions_without_changing_anything);
    [Fact] public Task Concurrency() => ProviderRunner.SqliteAsync(PackManagerScenarios.Concurrent_uploads_and_installs_of_one_pack_leave_exactly_one_installed_copy);
    [Fact] public Task Hostile() => ProviderRunner.SqliteAsync(PackManagerScenarios.Hostile_packages_are_rejected_with_plain_messages_and_nothing_is_stored);
    [Fact] public Task Csv() => ProviderRunner.SqliteAsync(PackManagerScenarios.The_validation_report_csv_defuses_formulas_and_shows_no_server_paths);
}

[Trait("Category", "SqlServer")]
public sealed class PackManagerScenarioTests_SqlServer
{
    [SqlServerFact] public Task Install_flow() => ProviderRunner.SqlServerAsync(PackManagerScenarios.A_pack_is_uploaded_checked_previewed_installed_and_repeat_uploads_are_harmless);
    [SqlServerFact] public Task Permissions() => ProviderRunner.SqlServerAsync(PackManagerScenarios.Pack_permissions_follow_the_confirmed_matrix_and_trust_keys_stay_with_the_tenant_administrator);
    [SqlServerFact] public Task Isolation() => ProviderRunner.SqlServerAsync(PackManagerScenarios.Packs_and_keys_belong_to_one_tenant_and_an_upload_cannot_be_used_by_another_person_or_tenant);
    [SqlServerFact] public Task Assignment() => ProviderRunner.SqlServerAsync(PackManagerScenarios.Assigning_a_pack_creates_drafts_only_and_never_overwrites_published_configuration);
    [SqlServerFact] public Task Third_pack() => ProviderRunner.SqlServerAsync(PackManagerScenarios.A_third_pack_the_repository_has_never_seen_runs_a_contract_with_no_source_change);
    [SqlServerFact] public async Task Production_trust() { await using var app = await TestApp.CreateSqlServerAsync(setupMode: Ecp.SharedKernel.EcpMode.Production); await PackManagerScenarios.Production_refuses_unsigned_and_untrusted_packs_until_the_publisher_key_is_trusted(app); }
    [SqlServerFact] public Task Cache() => ProviderRunner.SqlServerAsync(PackManagerScenarios.The_package_cache_is_keyed_by_tenant_and_package_so_one_tenant_never_reads_anothers_bytes);
    [SqlServerFact] public Task Diagnostics() => ProviderRunner.SqlServerAsync(PackManagerScenarios.Diagnostics_report_unresolved_packs_and_fallback_definitions_without_changing_anything);
    [SqlServerFact] public Task Concurrency() => ProviderRunner.SqlServerAsync(PackManagerScenarios.Concurrent_uploads_and_installs_of_one_pack_leave_exactly_one_installed_copy);
    [SqlServerFact] public Task Hostile() => ProviderRunner.SqlServerAsync(PackManagerScenarios.Hostile_packages_are_rejected_with_plain_messages_and_nothing_is_stored);
}
