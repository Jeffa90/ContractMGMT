using Ecp.Audit;
using Microsoft.EntityFrameworkCore;
using Ecp.Configuration;
using Ecp.Identity;
using Ecp.Identity.Domain;
using Ecp.Packs;
using Ecp.SharedKernel;
using Ecp.Tests.Common;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecp.Integration.Tests;

/// <summary>
/// S4: terminology through the real composition on each provider. Labels are data in layers (organisation, then the solution pack, then the platform's neutral wording), versioned and published through the ordinary
/// configuration lifecycle, resolved once per page, cached by tenant, organisation, locale and published version, and never able to change what anyone may do.
/// </summary>
internal static class TerminologyScenarios
{
    private const string Kind = "Terminology";

    private static string Unique(string prefix) => prefix + "-" + Guid.NewGuid().ToString("N")[..8];

    internal static string Overrides(params (string Key, string Singular, string Plural)[] terms)
        => new TerminologyDocument(TerminologyDocument.Overrides, terms.Select(t => new TermEntry(t.Key, "en", t.Singular, t.Plural)).ToList()).ToJson();

    /// <summary>A user who holds the business configuration administrator role for one organisation (it holds terminology.draft, publish and rollback).</summary>
    internal static async Task<string> AdminForAsync(LifecycleWorld w, Guid organisation, string key, string role = RoleCodes.BusinessConfigurationAdministrator)
    {
        await using var s = w.W.AsScopeless();
        var id = await s.Get<IUserService>().CreateActiveTrustedAsync(key, key + "@" + w.W.TenantId.ToString("N")[..8] + ".example.test", "demo", "https://demo.example.test", key + "|" + w.W.TenantId, "test");
        await s.Get<IRoleAssignmentService>().GrantTrustedAsync(new GrantRequest(id, role, null, ScopeSet.ForOrganisations([organisation], "Internal"), null,
            EffectiveFromUtc: w.W.Now.AddDays(-5), ExplicitHighCeiling: true), AssignmentSource.Demo, "test");
        w.W.Users[key] = id;
        return key;
    }

    private static Task<TermSet> TermsAsync(LifecycleWorld w, string user, Guid? org, string locale = "en")
        => w.W.AsAsync(user, s => s.Get<ITerminologyResolver>().GetAsync(org, locale));

    private static async Task<ReleaseDto> PublishAsync(LifecycleWorld w, string user, Guid org, string json, string note = "Reviewed")
    {
        var draft = await w.W.AsAsync(user, s => s.Get<IConfigurationService>().GetOrCreateDraftAsync(Kind, org, json));
        if (draft.ContentJson != json) draft = await w.W.AsAsync(user, s => s.Get<IConfigurationService>().SaveDraftAsync(draft.Id, json));
        var validated = await w.W.AsAsync(user, s => s.Get<IConfigurationService>().ValidateAsync(draft.Id));
        Assert.True(validated.IsPublishable, string.Join("; ", validated.Validation!.Messages.Select(m => m.Code + " " + m.Message)));
        return await w.W.AsAsync(user, s => s.Get<IConfigurationService>().PublishAsync(draft.Id, note));
    }

    private static async Task InstallAsync(LifecycleWorld w, string key, byte[] package)
    {
        var up = await w.W.AsAsync("tenantAdmin", s => s.Get<IPackManager>().UploadAsync(new MemoryStream(package), "t.ecpkg"));
        Assert.True(up.Report.CanInstall, string.Join("; ", up.Report.Issues.Select(i => i.Code + " " + i.Message)));
        await w.W.AsAsync("tenantAdmin", s => s.Get<IPackManager>().InstallAsync(up.Token));
    }

    // ---- 1. layers ----

    public static async Task Each_organisation_is_described_in_its_own_packs_words_and_a_blank_organisation_never_sees_a_business_word(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        var key = Unique("agreements");
        await InstallAsync(w, key, PackTestKit.Build(key, "1.0.0", content: PackManagerScenarios.ThirdPackContent(out var docs), extraDocuments: docs));

        var jbs = await PackResolutionScenarios.AddOrganisationAsync(w, "Reference Org", "TRJ", TestPacks.Reference);
        var blank = await PackResolutionScenarios.AddOrganisationAsync(w, "Blank Org", "TRB", "blank");
        var third = await PackResolutionScenarios.AddOrganisationAsync(w, "Third Org", "TRT", key);
        var implied = await PackResolutionScenarios.AddOrganisationAsync(w, "Default Org", "TRD", null);   // no pack of its own: the installation default (the reference pack in these hosts)

        var forJbs = await TermsAsync(w, "tenantAdmin", jbs.Org);
        Assert.Equal("Procurement Administrator", forJbs.Singular("contract.administrator"));
        Assert.Equal("Category Owner", forJbs.Singular("contract.owner"));
        Assert.Equal("Legal Review", forJbs.Singular("legal.review"));
        Assert.Equal("Group Head of Procurement", forJbs.Singular("primary.approver"));
        Assert.Equal("Supplier", forJbs.Singular("supplier"));
        Assert.Equal("Procurement Contract Register", forJbs.Singular("contract.register"));
        Assert.Equal(TermLayer.Pack, forJbs.LayerOf("contract.owner"));

        var forBlank = await TermsAsync(w, "tenantAdmin", blank.Org);
        Assert.Equal("Contract Administrator", forBlank.Singular("contract.administrator"));
        Assert.Equal("Contract Owner", forBlank.Singular("contract.owner"));
        Assert.Equal("Review", forBlank.Singular("legal.review"));
        Assert.Equal("Registration Approver", forBlank.Singular("primary.approver"));
        Assert.Equal("Counterparty", forBlank.Singular("supplier"));
        foreach (var (k, term) in forBlank.Terms)
            Assert.DoesNotMatch("(?i)jbs|procurement|group head|supplier", term.Singular + " " + term.Plural);

        var forThird = await TermsAsync(w, "tenantAdmin", third.Org);
        Assert.Equal("Agreement", forThird.Singular("contract"));
        Assert.Equal("Counterparties", forThird.Plural("supplier"));
        Assert.Equal("Portfolio", forThird.Singular("category"));
        Assert.Equal("Agreement Manager", forThird.Singular("contract.owner"));
        Assert.Equal("Formal Review", forThird.Singular("legal.review"));
        Assert.Equal("Activation Approval", forThird.Singular("registration.approval"));
        Assert.Equal("Business", forThird.Singular("organisation"));

        Assert.Equal("Procurement Administrator", (await TermsAsync(w, "tenantAdmin", implied.Org)).Singular("contract.administrator"));   // the installation default is what the host configures

        // The three do not leak into one another, and the platform's own layer stays neutral.
        Assert.NotEqual(forJbs.Singular("contract.owner"), forThird.Singular("contract.owner"));
        Assert.Equal("Contract Owner", TermSet.Neutral().Singular("contract.owner"));
    }

    public static async Task A_pack_that_carries_no_terminology_still_works_and_uses_the_neutral_wording(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        var key = Unique("plain");
        await InstallAsync(w, key, PackTestKit.Build(key, "1.0.0"));
        var org = await PackResolutionScenarios.AddOrganisationAsync(w, "Plain Org", "TPL", key);
        var set = await TermsAsync(w, "tenantAdmin", org.Org);
        Assert.Equal("Contract Owner", set.Singular("contract.owner"));
        Assert.Equal(TermLayer.Platform, set.LayerOf("contract.owner"));
        // An organisation id that does not exist resolves safely to the neutral wording (nothing is thrown, nothing is leaked).
        Assert.Equal("Contract", (await TermsAsync(w, "tenantAdmin", Guid.NewGuid())).Singular("contract"));
    }

    public static async Task Approver_group_names_and_role_visibility_follow_the_layers_and_never_touch_authority(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        var key = Unique("agreements");
        await InstallAsync(w, key, PackTestKit.Build(key, "1.0.0", content: PackManagerScenarios.ThirdPackContent(out var docs), extraDocuments: docs));
        var jbs = await PackResolutionScenarios.AddOrganisationAsync(w, "Group Ref Org", "TGJ", TestPacks.Reference);
        var blank = await PackResolutionScenarios.AddOrganisationAsync(w, "Group Blank Org", "TGB", "blank");
        var third = await PackResolutionScenarios.AddOrganisationAsync(w, "Group Third Org", "TGT", key);

        // Layer 2: each pack names its own approver group; the key itself is what is stored and compared.
        var forJbs = await TermsAsync(w, "tenantAdmin", jbs.Org);
        var forBlank = await TermsAsync(w, "tenantAdmin", blank.Org);
        var forThird = await TermsAsync(w, "tenantAdmin", third.Org);
        Assert.Equal("Group Head of Procurement", forJbs.Group("GroupHeadProcurement"));
        Assert.Equal("Registration Approvers", forBlank.Group("Approvers"));
        Assert.Equal("Activation Approvers", forThird.Group("Approvers"));
        Assert.Equal("GroupHeadProcurement", forBlank.Group("GroupHeadProcurement"));          // no leak from the other pack: an unnamed group is its own key
        Assert.Equal("Approvers", forJbs.Group("Approvers"));
        // Role visibility: nothing is hidden by default; the third pack hides one administrative role for a business with no divisions or sites.
        Assert.Empty(forJbs.HiddenRoles);
        Assert.Empty(forBlank.HiddenRoles);
        Assert.Equal([RoleCodes.DivisionSiteAdministrator], forThird.HiddenRoles.ToArray());

        // Layer 1: the organisation's own published choice wins over its pack, in that organisation only.
        var admin = await AdminForAsync(w, blank.Org, "groupAdmin");
        var json = new TerminologyDocument(TerminologyDocument.Overrides, [], [new GroupDisplayName("Approvers", "en", "Contract Approvers")], [new RoleOffer(RoleCodes.Legal, false)]).ToJson();
        await PublishAsync(w, admin, blank.Org, json);
        forBlank = await TermsAsync(w, "tenantAdmin", blank.Org);
        Assert.Equal("Contract Approvers", forBlank.Group("Approvers"));
        Assert.False(forBlank.IsRoleOffered(RoleCodes.Legal));
        Assert.Equal("Group Head of Procurement", (await TermsAsync(w, "tenantAdmin", jbs.Org)).Group("GroupHeadProcurement"));
        Assert.True((await TermsAsync(w, "tenantAdmin", jbs.Org)).IsRoleOffered(RoleCodes.Legal));
        Assert.Equal("Activation Approvers", (await TermsAsync(w, "tenantAdmin", third.Org)).Group("Approvers"));

        // The organisation brings back the role its pack hid.
        var thirdAdmin = await AdminForAsync(w, third.Org, "thirdAdmin");
        await PublishAsync(w, thirdAdmin, third.Org, new TerminologyDocument(TerminologyDocument.Overrides, [], null, [new RoleOffer(RoleCodes.DivisionSiteAdministrator, true)]).ToJson());
        Assert.True((await TermsAsync(w, "tenantAdmin", third.Org)).IsRoleOffered(RoleCodes.DivisionSiteAdministrator));

        // Hiding changes no authority: the trusted path still grants the hidden role, its holder is found, and the grant evaluates as before.
        var legal = await AdminForAsync(w, blank.Org, "hiddenLegal", RoleCodes.Legal);
        var holders = await w.W.AsAsync("tenantAdmin", s => s.Get<IAccessDirectory>().HoldersOfRoleAsync(RoleCodes.Legal, blank.Org));
        Assert.Contains(w.W.Users[legal], holders);
        Assert.NotEqual(Ecp.Identity.Access.Grant.None, Ecp.Identity.Access.PermissionMatrix.Instance.Of(RoleCodes.Legal, "legal.clear"));

        // A draft that names a role that does not exist, hides an administrative role, or carries markup in a group name cannot be published.
        foreach (var bad in new[]
        {
            new TerminologyDocument(TerminologyDocument.Overrides, [], null, [new RoleOffer("ContractAdministrator", false)]),
            new TerminologyDocument(TerminologyDocument.Overrides, [], null, [new RoleOffer(RoleCodes.OrganisationAdministrator, false)]),
            new TerminologyDocument(TerminologyDocument.Overrides, [], [new GroupDisplayName("Approvers", "en", "<script>x</script>")], null),
            new TerminologyDocument(TerminologyDocument.Overrides, [], [new GroupDisplayName("not a key", "en", "Name")], null),
        })
        {
            var draft = await w.W.AsAsync(admin, s => s.Get<IConfigurationService>().GetOrCreateDraftAsync(Kind, blank.Org, bad.ToJson()));
            if (draft.ContentJson != bad.ToJson()) draft = await w.W.AsAsync(admin, s => s.Get<IConfigurationService>().SaveDraftAsync(draft.Id, bad.ToJson()));
            var checkedDraft = await w.W.AsAsync(admin, s => s.Get<IConfigurationService>().ValidateAsync(draft.Id));
            Assert.False(checkedDraft.IsPublishable);
            await w.W.AsAsync(admin, s => s.Get<IConfigurationService>().DiscardDraftAsync(draft.Id));
        }
        Assert.Equal("Contract Approvers", (await TermsAsync(w, "tenantAdmin", blank.Org)).Group("Approvers"));   // nothing changed
    }

    // ---- 2. lifecycle ----

    public static async Task An_override_is_drafted_validated_previewed_published_removed_rolled_back_and_audited(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        var org = await PackResolutionScenarios.AddOrganisationAsync(w, "Override Org", "TRO", TestPacks.Reference);
        var admin = await AdminForAsync(w, org.Org, "termAdmin");

        Assert.Equal("Supplier", (await TermsAsync(w, admin, org.Org)).Singular("supplier"));

        // Draft and validate: nothing changes for anyone until it is published.
        var draftJson = Overrides(("supplier", "Partner", "Partners"));
        var draft = await w.W.AsAsync(admin, s => s.Get<IConfigurationService>().GetOrCreateDraftAsync(Kind, org.Org, draftJson));
        Assert.Equal("Supplier", (await TermsAsync(w, admin, org.Org)).Singular("supplier"));
        var validated = await w.W.AsAsync(admin, s => s.Get<IConfigurationService>().ValidateAsync(draft.Id));
        Assert.True(validated.IsPublishable);

        // Preview: what the organisation would see, without publishing.
        var preview = await w.W.AsAsync(admin, s => s.Get<ITerminologyResolver>().PreviewAsync(org.Org, TerminologyDocument.Parse(draftJson), "en"));
        Assert.Equal("Partner", preview.Singular("supplier"));
        Assert.Equal("Category Owner", preview.Singular("contract.owner"));            // everything else inherited from the pack
        Assert.Equal("Supplier", (await TermsAsync(w, admin, org.Org)).Singular("supplier"));

        var r1 = await w.W.AsAsync(admin, s => s.Get<IConfigurationService>().PublishAsync(draft.Id, "First override"));
        Assert.Equal(1, r1.Number);
        var afterPublish = await TermsAsync(w, admin, org.Org);
        Assert.Equal("Partner", afterPublish.Singular("supplier"));
        Assert.Equal(TermLayer.Organisation, afterPublish.LayerOf("supplier"));
        Assert.Equal("Category Owner", afterPublish.Singular("contract.owner"));
        Assert.Equal(TermLayer.Pack, afterPublish.LayerOf("contract.owner"));

        // Removing the override and publishing resumes inheritance from the pack.
        var r2 = await PublishAsync(w, admin, org.Org, Overrides(), "Remove the override");
        Assert.Equal(2, r2.Number);
        var resumed = await TermsAsync(w, admin, org.Org);
        Assert.Equal("Supplier", resumed.Singular("supplier"));
        Assert.Equal(TermLayer.Pack, resumed.LayerOf("supplier"));

        // Rollback publishes release 1 again as release 3; history is never rewritten.
        var r3 = await w.W.AsAsync(admin, s => s.Get<IConfigurationService>().RollbackAsync(Kind, org.Org, 1, "The partner wording was right"));
        Assert.Equal(3, r3.Number);
        Assert.Equal(1, r3.RollbackOfNumber);
        Assert.Equal("Partner", (await TermsAsync(w, admin, org.Org)).Singular("supplier"));
        var history = await w.W.AsAsync(admin, s => s.Get<IConfigurationService>().ReleasesAsync(Kind, org.Org));
        Assert.Equal([3, 2, 1], history.Select(h => h.Number));
        Assert.Equal(r1.ContentHash, r3.ContentHash);

        // Every step is in the audit trail under the stable action codes, and the audit records no label text beyond the hash.
        await using var scope = w.W.AsScopeless();
        var events = (await scope.Get<IAuditEventSource>().Events.Where(e => e.EntityType == Kind).OrderBy(e => e.Sequence).ToListAsync()).Select(e => e.Action).ToList();
        foreach (var action in new[] { "config.draft.created", "config.draft.validated", "config.published", "config.rolled_back" }) Assert.Contains(action, events);
    }

    // ---- 3. validation ----

    public static async Task A_draft_with_a_problem_cannot_be_published_and_nothing_executable_is_accepted(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        var org = await PackResolutionScenarios.AddOrganisationAsync(w, "Validate Org", "TRV", TestPacks.Reference);
        var admin = await AdminForAsync(w, org.Org, "termValidator");

        async Task<DraftDto> TryAsync(string json)
        {
            var draft = await w.W.AsAsync(admin, s => s.Get<IConfigurationService>().GetOrCreateDraftAsync(Kind, org.Org, json));
            draft = await w.W.AsAsync(admin, s => s.Get<IConfigurationService>().SaveDraftAsync(draft.Id, json));
            return await w.W.AsAsync(admin, s => s.Get<IConfigurationService>().ValidateAsync(draft.Id));
        }

        var duplicate = await TryAsync(new TerminologyDocument(TerminologyDocument.Overrides, [new("supplier", "en", "A", "As"), new("supplier", "en", "B", "Bs")]).ToJson());
        Assert.False(duplicate.IsPublishable);
        Assert.Contains(duplicate.Validation!.Messages, m => m.Code == "term_duplicate");
        await Assert.ThrowsAsync<DomainException>(() => w.W.AsAsync(admin, s => s.Get<IConfigurationService>().PublishAsync(duplicate.Id, "should be refused")));

        var script = await TryAsync(Overrides(("supplier", "<script>alert(1)</script>", "Partners")));
        Assert.False(script.IsPublishable);
        Assert.Contains(script.Validation!.Messages, m => m.Code == "term_singular_markup");

        var template = await TryAsync(Overrides(("supplier", "{{7*7}}", "Partners")));
        Assert.False(template.IsPublishable);

        var tooLong = await TryAsync(Overrides(("supplier", new string('x', 81), "Partners")));
        Assert.False(tooLong.IsPublishable);

        // A complete document that leaves out a required key is refused.
        var missing = TermCatalogue.RequiredKeys.Where(k => k != "contract").Select(k => new TermEntry(k, "en", "X", "Xs")).ToList();
        var incomplete = await TryAsync(new TerminologyDocument(TerminologyDocument.Complete, missing).ToJson());
        Assert.False(incomplete.IsPublishable);
        Assert.Contains(incomplete.Validation!.Messages, m => m.Code == "term_required_missing" && m.Message.Contains("'contract'"));

        // Nothing was published by any of the above.
        Assert.Null(await w.W.AsAsync(admin, s => s.Get<IConfigurationService>().LatestReleaseAsync(Kind, org.Org)));
        Assert.Equal("Supplier", (await TermsAsync(w, admin, org.Org)).Singular("supplier"));
    }

    // ---- 4. permissions: labels never change authority ----

    public static async Task Terminology_permissions_follow_the_matrix_and_a_label_never_changes_what_anyone_may_do(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        var org = w.W.Org1;
        var json = Overrides(("contract.administrator", "Head Clerk", "Head Clerks"));

        // People who only work with contracts cannot draft, publish, read history or roll back; resolving a label needs no permission at all.
        foreach (var user in new[] { "ownerPackaging", "viewerAll", "legalA", "procAdmin", "approver" })
        {
            await Assert.ThrowsAsync<ForbiddenException>(() => w.W.AsAsync(user, s => s.Get<IConfigurationService>().GetOrCreateDraftAsync(Kind, org, json)));
            await Assert.ThrowsAsync<ForbiddenException>(() => w.W.AsAsync(user, s => s.Get<IConfigurationService>().ReleasesAsync(Kind, org)));
            await Assert.ThrowsAsync<ForbiddenException>(() => w.W.AsAsync(user, s => s.Get<IConfigurationService>().RollbackAsync(Kind, org, 1, "no")));
            Assert.NotNull(await TermsAsync(w, user, org));
        }

        // The auditor reads the history only; the organisation administrator and the tenant administrator may draft and publish.
        await Assert.ThrowsAsync<ForbiddenException>(() => w.W.AsAsync("auditor", s => s.Get<IConfigurationService>().GetOrCreateDraftAsync(Kind, org, json)));
        await w.W.AsAsync("auditor", s => s.Get<IConfigurationService>().ReleasesAsync(Kind, org));
        var release = await PublishAsync(w, "orgAdmin", org, json);
        Assert.Equal(1, release.Number);

        // Changing a label changed no authority: the administrator role still holds exactly what the matrix gives it, under its stable code.
        var before = Ecp.Identity.Access.PermissionMatrix.Instance.Of(RoleCodes.ProcurementAdministrator, "contracts.read");
        Assert.Equal("Head Clerk", (await TermsAsync(w, "orgAdmin", org)).Singular("contract.administrator"));
        Assert.Equal(before, Ecp.Identity.Access.PermissionMatrix.Instance.Of(RoleCodes.ProcurementAdministrator, "contracts.read"));
        var contract = await w.NewDraftAsync();
        Assert.NotNull(await w.W.AsAsync("procAdmin", s => s.Get<Ecp.Contracts.ISecuredContractService>().GetAsync(contract)));            // the stable role still reads contracts, whatever it is called
        await Assert.ThrowsAsync<ForbiddenException>(() => w.W.AsAsync("procAdmin", s => s.Get<IConfigurationService>().GetOrCreateDraftAsync(Kind, org, json)));   // still no configuration authority

        // The general configuration permissions are not enough: terminology is delegated on its own permissions.
        var terms = new[] { "terminology.read", "terminology.draft", "terminology.publish", "terminology.rollback", "terminology.audit.read" };
        foreach (var permission in terms) Assert.Contains(permission, Ecp.Identity.Access.PermissionMatrix.Instance.Permissions);
        Assert.Equal(Ecp.Identity.Access.Grant.None, Ecp.Identity.Access.PermissionMatrix.Instance.Of(RoleCodes.CategoryOwner, "terminology.draft"));
        Assert.Equal(Ecp.Identity.Access.Grant.None, Ecp.Identity.Access.PermissionMatrix.Instance.Of(RoleCodes.Legal, "terminology.publish"));
        Assert.Equal(Ecp.Identity.Access.Grant.Yes, Ecp.Identity.Access.PermissionMatrix.Instance.Of(RoleCodes.Auditor, "terminology.read"));
    }

    // ---- 5. second person ----

    public static async Task Second_person_publication_applies_to_terminology(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        var org = await PackResolutionScenarios.AddOrganisationAsync(w, "Two Person Org", "TRS", TestPacks.Reference);
        var editor = await AdminForAsync(w, org.Org, "termEditor");
        var publisher = await AdminForAsync(w, org.Org, "termPublisher");
        await using (var s = w.W.AsScopeless()) s.Get<ConfigurationOptions>().RequireSecondPersonPublish = true;

        var draft = await w.W.AsAsync(editor, s => s.Get<IConfigurationService>().GetOrCreateDraftAsync(Kind, org.Org, Overrides(("site", "Plant", "Plants"))));
        await w.W.AsAsync(editor, s => s.Get<IConfigurationService>().SaveDraftAsync(draft.Id, Overrides(("site", "Plant", "Plants"))));
        await w.W.AsAsync(editor, s => s.Get<IConfigurationService>().ValidateAsync(draft.Id));
        var refused = await Assert.ThrowsAsync<DomainException>(() => w.W.AsAsync(editor, s => s.Get<IConfigurationService>().PublishAsync(draft.Id, "my own change")));
        Assert.Equal("second_person_required", refused.Code);
        Assert.Equal("Site", (await TermsAsync(w, editor, org.Org)).Singular("site"));

        var release = await w.W.AsAsync(publisher, s => s.Get<IConfigurationService>().PublishAsync(draft.Id, "Reviewed by a second person"));
        Assert.Equal(1, release.Number);
        Assert.Equal("Plant", (await TermsAsync(w, publisher, org.Org)).Singular("site"));
    }

    // ---- 6. isolation ----

    public static async Task Terminology_never_crosses_a_tenant_or_an_organisation_and_the_cache_is_keyed_by_both(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        var other = await LifecycleWorld.CreateAsync(app);                 // a second tenant in the same installation
        var a = await PackResolutionScenarios.AddOrganisationAsync(w, "Org A", "TIA", TestPacks.Reference);
        var b = await PackResolutionScenarios.AddOrganisationAsync(w, "Org B", "TIB", TestPacks.Reference);
        var adminA = await AdminForAsync(w, a.Org, "termIsoA");

        await PublishAsync(w, adminA, a.Org, Overrides(("contract", "Matter", "Matters")));
        Assert.Equal("Matter", (await TermsAsync(w, adminA, a.Org)).Singular("contract"));
        Assert.Equal("Matter", (await TermsAsync(w, adminA, a.Org)).Singular("contract"));          // served from the cache, same answer

        // Another organisation in the same tenant is untouched, even though it works from the same pack.
        Assert.Equal("Contract", (await TermsAsync(w, "tenantAdmin", b.Org)).Singular("contract"));
        // The administrator of A cannot draft for B.
        var denied = await Assert.ThrowsAnyAsync<Exception>(() => w.W.AsAsync(adminA, s => s.Get<IConfigurationService>().GetOrCreateDraftAsync(Kind, b.Org, Overrides(("contract", "Matter", "Matters")))));
        Assert.True(denied is ForbiddenException or ResourceNotFoundException, denied.GetType().Name);          // refused, or not even visible: never allowed

        // Another tenant never sees the release or the draft, whatever organisation id it asks for.
        Assert.Equal("Contract", (await TermsAsync(other, "tenantAdmin", a.Org)).Singular("contract"));
        Assert.Equal("Contract", (await TermsAsync(other, "tenantAdmin", other.W.Org1)).Singular("contract"));
        Assert.Null(await other.W.AsAsync("tenantAdmin", s => s.Get<IConfigurationService>().LatestReleaseAsync(Kind, a.Org)));

        // The cache has separate entries per tenant and organisation.
        await using var scope = w.W.AsScopeless();
        var cache = scope.Get<ITerminologyCache>();
        Assert.True(cache.Statistics.Entries >= 3);
    }

    // ---- 7. cache ----

    public static async Task The_cache_is_used_invalidated_by_publish_and_rollback_and_never_required(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        var org = await PackResolutionScenarios.AddOrganisationAsync(w, "Cache Org", "TRC", TestPacks.Reference);
        var admin = await AdminForAsync(w, org.Org, "termCache");

        var before = await HitsAsync(w);
        await TermsAsync(w, admin, org.Org);
        await TermsAsync(w, admin, org.Org);
        Assert.True(await HitsAsync(w) > before, "the second read of an unchanged organisation should be served from the cache");

        await PublishAsync(w, admin, org.Org, Overrides(("site", "Plant", "Plants")));
        Assert.Equal("Plant", (await TermsAsync(w, admin, org.Org)).Singular("site"));                // visible at once after a publish
        await w.W.AsAsync(admin, s => s.Get<IConfigurationService>().RollbackAsync(Kind, org.Org, 1, "same again"));
        Assert.Equal("Plant", (await TermsAsync(w, admin, org.Org)).Singular("site"));
        await PublishAsync(w, admin, org.Org, Overrides(("site", "Yard", "Yards")));
        Assert.Equal("Yard", (await TermsAsync(w, admin, org.Org)).Singular("site"));
        await w.W.AsAsync(admin, s => s.Get<IConfigurationService>().RollbackAsync(Kind, org.Org, 1, "back to the first"));
        Assert.Equal("Plant", (await TermsAsync(w, admin, org.Org)).Singular("site"));               // visible at once after a rollback

        // Different locales are different entries.
        Assert.Equal("Organisation", (await TermsAsync(w, admin, org.Org, "en-AU")).Singular("organisation"));
        Assert.Equal("Organization", (await TermsAsync(w, admin, org.Org, "en-US")).Singular("organisation"));

        static async Task<long> HitsAsync(LifecycleWorld world) { await using var s = world.W.AsScopeless(); return s.Get<ITerminologyCache>().Statistics.Hits; }
    }

    internal sealed class FaultyCache : ITerminologyCache
    {
        public int Faults;
        public bool TryGet(Guid tenantId, Guid organisationId, string locale, string version, out TermSet? set) { Faults++; throw new InvalidOperationException("cache down"); }
        public void Store(Guid tenantId, Guid organisationId, string locale, string version, TermSet set) { Faults++; throw new InvalidOperationException("cache down"); }
        public void Invalidate(Guid tenantId, Guid organisationId) => throw new InvalidOperationException("cache down");
        public TerminologyCacheStatistics Statistics => new(0, 0, Faults, 0);
        public void CountFallback() { }
        public void Clear() { }
    }

    public static async Task When_the_cache_fails_terminology_is_resolved_from_persistence(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        var org = await PackResolutionScenarios.AddOrganisationAsync(w, "Faulty Cache Org", "TRF", TestPacks.Reference);
        var admin = await AdminForAsync(w, org.Org, "termFaulty");
        await PublishAsync(w, admin, org.Org, Overrides(("site", "Plant", "Plants")));              // publishing also survives an invalidation failure
        var set = await TermsAsync(w, admin, org.Org);
        Assert.Equal("Plant", set.Singular("site"));
        Assert.Equal("Category Owner", set.Singular("contract.owner"));
        await using var scope = w.W.AsScopeless();
        Assert.True(((FaultyCache)scope.Get<ITerminologyCache>()).Faults > 0);
    }

    // ---- 8. assignment ----

    public static async Task Assigning_a_pack_prepares_a_terminology_draft_and_never_publishes_or_overwrites_published_terminology(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        var key = Unique("assign");
        await InstallAsync(w, key, PackTestKit.Build(key, "1.0.0", content: PackManagerScenarios.ThirdPackContent(out var docs), extraDocuments: docs));

        // Preview says what terminology would be introduced, before anything is assigned.
        var fresh = await PackResolutionScenarios.AddOrganisationAsync(w, "Fresh Org", "TAF", null);
        var admin = await AdminForAsync(w, fresh.Org, "termAssign");
        var preview = await w.W.AsAsync(admin, s => s.Get<IPackAssignments>().PreviewAsync(fresh.Org, key, "1.0.0"));
        var item = preview.Items.Single(i => i.Kind == Kind);
        Assert.Contains($"{TermCatalogue.All.Count} terms", item.Effect);
        Assert.Contains("Nothing is published", item.Effect);
        Assert.False(item.HasPublishedRelease);

        var result = await w.W.AsAsync(admin, s => s.Get<IPackAssignments>().AssignAsync(fresh.Org, key, "1.0.0"));
        var outcome = result.Kinds.Single(k => k.Kind == Kind);
        Assert.False(outcome.NeedsAttention, outcome.Outcome + " " + outcome.Detail);
        Assert.Contains("Draft created", outcome.Outcome);

        // A draft exists, it carries its source pack and version, and nothing is published.
        var draft = await w.W.AsAsync(admin, s => s.Get<IConfigurationService>().FindOpenDraftAsync(Kind, fresh.Org));
        Assert.NotNull(draft);
        var doc = TerminologyDocument.Parse(draft!.ContentJson);
        Assert.Equal(TerminologyDocument.Overrides, doc.Mode);
        Assert.All(doc.Terms, t => { Assert.Equal(key, t.Source!.PackKey); Assert.Equal("1.0.0", t.Source.PackVersion); });
        Assert.Null(await w.W.AsAsync(admin, s => s.Get<IConfigurationService>().LatestReleaseAsync(Kind, fresh.Org)));

        // The pack's words apply as defaults at once (the inherited layer); publishing the draft makes them the organisation's own.
        var set = await TermsAsync(w, admin, fresh.Org);
        Assert.Equal("Agreement Manager", set.Singular("contract.owner"));
        Assert.Equal(TermLayer.Pack, set.LayerOf("contract.owner"));
        await w.W.AsAsync(admin, s => s.Get<IConfigurationService>().ValidateAsync(draft.Id));
        await w.W.AsAsync(admin, s => s.Get<IConfigurationService>().PublishAsync(draft.Id, "Reviewed"));
        Assert.Equal(TermLayer.Organisation, (await TermsAsync(w, admin, fresh.Org)).LayerOf("contract.owner"));

        // An organisation that already publishes its own terminology keeps it: assignment adds no draft and changes no release.
        var owner = await PackResolutionScenarios.AddOrganisationAsync(w, "Owner Org", "TAO", TestPacks.Reference);
        var ownerAdmin = await AdminForAsync(w, owner.Org, "termAssignOwner");
        await PublishAsync(w, ownerAdmin, owner.Org, Overrides(("contract.owner", "Steward", "Stewards")));
        var ownerPreview = await w.W.AsAsync(ownerAdmin, s => s.Get<IPackAssignments>().PreviewAsync(owner.Org, key, "1.0.0"));
        Assert.True(ownerPreview.Items.Single(i => i.Kind == Kind).HasPublishedRelease);
        var assigned = await w.W.AsAsync(ownerAdmin, s => s.Get<IPackAssignments>().AssignAsync(owner.Org, key, "1.0.0"));
        Assert.Equal("Existing terminology kept", assigned.Kinds.Single(k => k.Kind == Kind).Outcome);
        Assert.Null(await w.W.AsAsync(ownerAdmin, s => s.Get<IConfigurationService>().FindOpenDraftAsync(Kind, owner.Org)));
        var kept = await TermsAsync(w, ownerAdmin, owner.Org);
        Assert.Equal("Steward", kept.Singular("contract.owner"));                                           // the organisation's own wording wins
        Assert.Equal("Agreement", kept.Singular("contract"));                                               // the pack's wording fills in beneath it
        Assert.Equal(1, (await w.W.AsAsync(ownerAdmin, s => s.Get<IConfigurationService>().LatestReleaseAsync(Kind, owner.Org)))!.Number);
    }

    // ---- 8b. pack validation and preview ----

    public static async Task A_pack_with_an_invalid_terminology_document_cannot_be_installed_and_a_valid_one_previews_its_words(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        async Task<PackUploadResult> Upload(string key, string terminologyJson)
            => await w.W.AsAsync("tenantAdmin", s => s.Get<IPackManager>().UploadAsync(new MemoryStream(PackTestKit.Build(key, "1.0.0", content: [PackTestKit.TerminologyEntry()],
                extraDocuments: new Dictionary<string, string> { ["terminology.json"] = terminologyJson })), "t.ecpkg"));

        // A complete document that leaves out a required key.
        var missing = System.Text.Json.JsonSerializer.Serialize(new { schemaVersion = 1, mode = "complete", terms = TermCatalogue.RequiredKeys.Where(k => k != "supplier").Select(k => new { key = k, locale = "en", singular = "X", plural = "Xs" }) });
        var a = await Upload(Unique("badmissing"), missing);
        Assert.False(a.Report.CanInstall);
        Assert.Contains(a.Report.Issues, i => i.Code == PackCodes.ContentInvalid && i.Message.Contains("Terminology") && i.Message.Contains("'supplier'"));

        // Duplicate keys, an over-long label and template syntax are each refused with a message that says where.
        var dup = System.Text.Json.JsonSerializer.Serialize(new { schemaVersion = 1, mode = "overrides", terms = new object[] { new { key = "site", locale = "en", singular = "A", plural = "As" }, new { key = "site", locale = "en", singular = "B", plural = "Bs" } } });
        var b = await Upload(Unique("baddup"), dup);
        Assert.False(b.Report.CanInstall);
        Assert.Contains(b.Report.Issues, i => i.Message.Contains("more than once"));
        var template = System.Text.Json.JsonSerializer.Serialize(new { schemaVersion = 1, mode = "overrides", terms = new object[] { new { key = "site", locale = "en", singular = "{{7*7}}", plural = "As" } } });
        Assert.False((await Upload(Unique("badtemplate"), template)).Report.CanInstall);

        // A valid pack: the preview names the terms it would introduce, beside the neutral wording, and installing publishes nothing.
        var good = await Upload(Unique("goodterms"), PackTestKit.ThirdPackTerminologyJson());
        Assert.True(good.Report.CanInstall, string.Join("; ", good.Report.Issues.Select(i => i.Code + " " + i.Message)));
        var preview = await w.W.AsAsync("tenantAdmin", s => s.Get<IPackManager>().PreviewAsync(good.Token));
        Assert.NotNull(preview!.Terminology);
        var contract = preview.Terminology!.Single(r => r.Key == "contract");
        Assert.Equal("Agreement", contract.Singular);
        Assert.Equal("Contract", contract.NeutralSingular);
        Assert.True(contract.DiffersFromNeutral);
        Assert.Contains(preview.Kinds, k => k.Kind == "Terminology");
        Assert.Contains("will not publish any configuration", preview.Statement);
        await w.W.AsAsync("tenantAdmin", s => s.Get<IPackManager>().InstallAsync(good.Token));
        Assert.Equal("Contract", (await TermsAsync(w, "tenantAdmin", w.W.Org1)).Singular("contract"));             // installing changed nobody's words
    }

    // ---- 9. several keys, one operation ----

    public static async Task Several_terms_are_resolved_in_one_operation_and_an_unknown_key_is_a_diagnostic_only(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        var org = await PackResolutionScenarios.AddOrganisationAsync(w, "Many Org", "TRM", "blank");
        var many = await w.W.AsAsync("tenantAdmin", s => s.Get<ITerminologyResolver>().ResolveManyAsync(org.Org, ["contract", "supplier", "contract.owner", "no.such.key", "contract"], "en"));
        Assert.Equal(4, many.Count);
        Assert.Equal("Contracts", many["contract"].Plural);
        Assert.Equal("Counterparty", many["supplier"].Singular);
        Assert.Equal(TermLayer.Unknown, many["no.such.key"].Layer);
        Assert.Equal("no.such.key", many["no.such.key"].Singular);                                           // the key as a last-resort diagnostic, never the preferred label
    }
}

[Trait("Category", "Terminology")]
public sealed class TerminologyScenarioTests_Sqlite
{
    [Fact] public Task Layers() => ProviderRunner.SqliteAsync(TerminologyScenarios.Each_organisation_is_described_in_its_own_packs_words_and_a_blank_organisation_never_sees_a_business_word);
    [Fact] public Task Plain_pack() => ProviderRunner.SqliteAsync(TerminologyScenarios.A_pack_that_carries_no_terminology_still_works_and_uses_the_neutral_wording);
    [Fact] public Task Lifecycle() => ProviderRunner.SqliteAsync(TerminologyScenarios.An_override_is_drafted_validated_previewed_published_removed_rolled_back_and_audited);
    [Fact] public Task Validation() => ProviderRunner.SqliteAsync(TerminologyScenarios.A_draft_with_a_problem_cannot_be_published_and_nothing_executable_is_accepted);
    [Fact] public Task Permissions() => ProviderRunner.SqliteAsync(TerminologyScenarios.Terminology_permissions_follow_the_matrix_and_a_label_never_changes_what_anyone_may_do);
    [Fact] public Task Second_person() => ProviderRunner.SqliteAsync(TerminologyScenarios.Second_person_publication_applies_to_terminology);
    [Fact] public Task Isolation() => ProviderRunner.SqliteAsync(TerminologyScenarios.Terminology_never_crosses_a_tenant_or_an_organisation_and_the_cache_is_keyed_by_both);
    [Fact] public Task Cache() => ProviderRunner.SqliteAsync(TerminologyScenarios.The_cache_is_used_invalidated_by_publish_and_rollback_and_never_required);
    [Fact] public Task Faulty_cache() => ProviderRunner.SqliteAsync(TerminologyScenarios.When_the_cache_fails_terminology_is_resolved_from_persistence, s => s.AddSingleton<ITerminologyCache>(new TerminologyScenarios.FaultyCache()));
    [Fact] public Task Assignment() => ProviderRunner.SqliteAsync(TerminologyScenarios.Assigning_a_pack_prepares_a_terminology_draft_and_never_publishes_or_overwrites_published_terminology);
    [Fact] public Task Pack_validation() => ProviderRunner.SqliteAsync(TerminologyScenarios.A_pack_with_an_invalid_terminology_document_cannot_be_installed_and_a_valid_one_previews_its_words);
    [Fact] public Task Groups_and_roles() => ProviderRunner.SqliteAsync(TerminologyScenarios.Approver_group_names_and_role_visibility_follow_the_layers_and_never_touch_authority);
    [Fact] public Task Many() => ProviderRunner.SqliteAsync(TerminologyScenarios.Several_terms_are_resolved_in_one_operation_and_an_unknown_key_is_a_diagnostic_only);
}

[Trait("Category", "SqlServer")]
public sealed class TerminologyScenarioTests_SqlServer
{
    [SqlServerFact] public Task Layers() => ProviderRunner.SqlServerAsync(TerminologyScenarios.Each_organisation_is_described_in_its_own_packs_words_and_a_blank_organisation_never_sees_a_business_word);
    [SqlServerFact] public Task Lifecycle() => ProviderRunner.SqlServerAsync(TerminologyScenarios.An_override_is_drafted_validated_previewed_published_removed_rolled_back_and_audited);
    [SqlServerFact] public Task Validation() => ProviderRunner.SqlServerAsync(TerminologyScenarios.A_draft_with_a_problem_cannot_be_published_and_nothing_executable_is_accepted);
    [SqlServerFact] public Task Permissions() => ProviderRunner.SqlServerAsync(TerminologyScenarios.Terminology_permissions_follow_the_matrix_and_a_label_never_changes_what_anyone_may_do);
    [SqlServerFact] public Task Second_person() => ProviderRunner.SqlServerAsync(TerminologyScenarios.Second_person_publication_applies_to_terminology);
    [SqlServerFact] public Task Isolation() => ProviderRunner.SqlServerAsync(TerminologyScenarios.Terminology_never_crosses_a_tenant_or_an_organisation_and_the_cache_is_keyed_by_both);
    [SqlServerFact] public Task Cache() => ProviderRunner.SqlServerAsync(TerminologyScenarios.The_cache_is_used_invalidated_by_publish_and_rollback_and_never_required);
    [SqlServerFact] public Task Pack_validation() => ProviderRunner.SqlServerAsync(TerminologyScenarios.A_pack_with_an_invalid_terminology_document_cannot_be_installed_and_a_valid_one_previews_its_words);
    [SqlServerFact] public Task Groups_and_roles() => ProviderRunner.SqlServerAsync(TerminologyScenarios.Approver_group_names_and_role_visibility_follow_the_layers_and_never_touch_authority);
    [SqlServerFact] public Task Assignment() => ProviderRunner.SqlServerAsync(TerminologyScenarios.Assigning_a_pack_prepares_a_terminology_draft_and_never_publishes_or_overwrites_published_terminology);
}
