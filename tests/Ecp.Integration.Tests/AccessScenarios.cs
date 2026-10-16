using Ecp.Audit;
using Ecp.Audit.Persistence;
using Ecp.Contracts;
using Ecp.Contracts.Persistence;
using Ecp.Identity;
using Ecp.Identity.Access;
using Ecp.Identity.Domain;
using Ecp.Identity.Providers;
using Ecp.Identity.Persistence;
using Ecp.SharedKernel;
using Ecp.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecp.Integration.Tests;

/// <summary>
/// Access behaviour against a real database. The same scenarios run on the lightweight provider (provider-independent) and on SQL Server
/// (where query translation, constraints and Row-Level Security are real). Only the SQL Server run is evidence about SQL Server.
/// </summary>
internal static class AccessScenarios
{
    private static HashSet<string> Keys(params string[] keys) => keys.ToHashSet();

    public static async Task Viewer_all_contracts_sees_its_organisation_within_its_ceiling_and_nothing_else(AccessWorld w)
    {
        // C4 is Restricted (above Confidential); C6 belongs to the other organisation.
        Assert.Equal(Keys("C1", "C2", "C3", "C5", "C7"), await w.VisibleContractKeysAsync("viewerAll"));
    }

    public static async Task Category_viewer_sees_its_category_tree_only(AccessWorld w)
        => Assert.Equal(Keys("C1", "C2", "C5", "C7"), await w.VisibleContractKeysAsync("viewerCat")); // Packaging and Flexible Packaging; not Energy

    public static async Task Business_unit_viewer_sees_its_business_unit_only(AccessWorld w)
        => Assert.Equal(Keys("C1", "C2", "C7"), await w.VisibleContractKeysAsync("viewerBu")); // C5 has no business unit

    public static async Task Site_viewer_sees_its_site_only_and_classification_still_applies(AccessWorld w)
        => Assert.Equal(Keys("C2"), await w.VisibleContractKeysAsync("viewerSite")); // C4 is at Site B but Restricted

    public static async Task Restricted_viewer_is_the_intersection_of_site_category_and_ceiling(AccessWorld w)
        => Assert.Equal(Keys("C3"), await w.VisibleContractKeysAsync("viewerRestricted")); // Energy at Site A within Internal

    public static async Task Category_owner_reads_all_in_organisation_within_ceiling(AccessWorld w)
    {
        Assert.Equal(Keys("C1", "C2", "C3", "C5", "C7"), await w.VisibleContractKeysAsync("ownerPackaging"));
        Assert.Equal(Keys("C1", "C2", "C3", "C5", "C7"), await w.VisibleContractKeysAsync("ownerEnergy"));
    }

    public static async Task Legal_and_procurement_administrator_read_up_to_their_ceilings(AccessWorld w)
    {
        Assert.Equal(Keys("C1", "C2", "C3", "C4", "C5", "C7"), await w.VisibleContractKeysAsync("legal"));
        Assert.Equal(Keys("C1", "C2", "C3", "C5", "C7"), await w.VisibleContractKeysAsync("procAdmin"));
    }

    public static async Task Tenant_wide_auditor_reads_every_organisation_in_the_tenant(AccessWorld w)
        => Assert.Equal(Keys("C1", "C2", "C3", "C4", "C5", "C6", "C7"), await w.VisibleContractKeysAsync("auditor"));

    public static async Task Administrators_and_approvers_without_a_relationship_see_no_contracts(AccessWorld w)
    {
        foreach (var user in new[] { "tenantAdmin", "orgAdmin", "siteAdmin", "approver" })
            Assert.Empty(await w.VisibleContractKeysAsync(user));
    }

    public static async Task A_direct_read_outside_scope_or_above_the_ceiling_is_not_found_not_forbidden(AccessWorld w)
    {
        await w.AsAsync("viewerCat", async s =>
        {
            var svc = s.Get<ISecuredContractService>();
            Assert.Equal("C1", (await svc.GetAsync(w.Contracts["C1"])).Title[^2..]);
            await Assert.ThrowsAsync<ResourceNotFoundException>(() => svc.GetAsync(w.Contracts["C3"])); // another category
            await Assert.ThrowsAsync<ResourceNotFoundException>(() => svc.GetAsync(w.Contracts["C6"])); // another organisation
            await Assert.ThrowsAsync<ResourceNotFoundException>(() => svc.GetAsync(Guid.NewGuid()));    // does not exist: indistinguishable
        });
        await w.AsAsync("viewerAll", async s =>
            await Assert.ThrowsAsync<ResourceNotFoundException>(() => s.Get<ISecuredContractService>().GetAsync(w.Contracts["C4"]))); // above the ceiling
    }

    public static async Task An_in_scope_user_without_the_permission_is_forbidden(AccessWorld w)
    {
        await w.AsAsync("orgAdmin", async s => await Assert.ThrowsAsync<ForbiddenException>(() => s.Get<ISecuredContractService>().GetAsync(w.Contracts["C1"])));
        await w.AsAsync("viewerAll", async s =>
            await Assert.ThrowsAsync<ForbiddenException>(() => s.Get<ISecuredContractService>().UpdateDetailsAsync(w.Contracts["C1"], "Renamed", null)));
        await w.AsAsync("legal", async s =>
            await Assert.ThrowsAsync<ForbiddenException>(() => s.Get<ISecuredContractService>().UpdateDetailsAsync(w.Contracts["C1"], "Renamed", null)));
    }

    public static async Task Category_owner_edits_only_current_categories_and_never_a_locked_record(AccessWorld w)
    {
        await w.AsAsync("ownerPackaging", async s =>
        {
            var svc = s.Get<ISecuredContractService>();
            await svc.UpdateDetailsAsync(w.Contracts["C1"], "Packaging agreement (edited)", "by owner");
            await svc.UpdateDetailsAsync(w.Contracts["C2"], "Flexible packaging agreement (edited)", null); // subcategory of an owned category
            await Assert.ThrowsAsync<ForbiddenException>(() => svc.UpdateDetailsAsync(w.Contracts["C3"], "Not mine", null)); // Energy
            await Assert.ThrowsAsync<ForbiddenException>(() => svc.UpdateDetailsAsync(w.Contracts["C7"], "Locked", null));    // locked: layer 7
            Assert.True((await svc.GetAsync(w.Contracts["C1"])).CanEdit);
            Assert.False((await svc.GetAsync(w.Contracts["C3"])).CanEdit);
            Assert.False((await svc.GetAsync(w.Contracts["C7"])).CanEdit);
        });
        await w.AsAsync("ownerEnergy", async s =>
        {
            var svc = s.Get<ISecuredContractService>();
            await svc.UpdateDetailsAsync(w.Contracts["C3"], "Energy agreement (edited)", null);
            await Assert.ThrowsAsync<ForbiddenException>(() => svc.UpdateDetailsAsync(w.Contracts["C1"], "Ended ownership", null)); // the Packaging assignment ended 35 days ago
        });
    }

    public static async Task Ending_an_ownership_removes_edit_rights_immediately(AccessWorld w)
    {
        // Create a short-lived owner on Energy through the trusted path, prove edit works, end it, prove it stops, all on the same record.
        Guid user;
        await using (var s = w.AsScopeless())
            user = await s.Get<IUserService>().CreateActiveTrustedAsync("Temp Owner", "temp@" + w.TenantId.ToString("N")[..8] + ".example.test", null, null, null, "scenario");
        await using (var s = w.AsScopeless())
            await s.Get<IRoleAssignmentService>().GrantTrustedAsync(new GrantRequest(user, RoleCodes.CategoryOwner, null, ScopeSet.ForOrganisations([w.Org1], "Confidential")), AssignmentSource.Demo, "scenario");
        Guid assignment;
        await using (var s = w.AsScopeless())
            assignment = await s.Get<ICategoryAdministration>().AssignOwnerTrustedAsync(w.CatEnergy, user, false, w.Now.AddDays(-1), null, "scenario", null);

        await w.AsIdAsync(user, async s => { await s.Get<ISecuredContractService>().UpdateDetailsAsync(w.Contracts["C3"], "Edited by temporary owner", null); return true; });
        await w.AsAsync("procAdmin", async s => { await s.Get<ISecuredCategoryService>().EndOwnershipAsync(assignment, "Handover complete"); });
        await w.AsIdAsync(user, async s =>
        {
            await Assert.ThrowsAsync<ForbiddenException>(() => s.Get<ISecuredContractService>().UpdateDetailsAsync(w.Contracts["C3"], "Should now fail", null));
            return true;
        });
    }

    public static async Task Creating_a_contract_follows_ownership_scope_and_allocates_the_organisation_reference(AccessWorld w)
    {
        Guid first = default, second = default;
        string firstRef = "", secondRef = "";
        await w.AsAsync("ownerPackaging", async s =>
        {
            var svc = s.Get<ISecuredContractService>();
            var preview = await svc.PreviewNextReferenceAsync(w.Org1);
            first = await svc.CreateDraftAsync(new CreateContractRequest(w.Org1, "", "Created by owner", w.Sup1, w.CatPackaging, BusinessUnitId: w.Bu1, SiteId: w.Site1));
            second = await svc.CreateDraftAsync(new CreateContractRequest(w.Org1, "", "Created by owner (2)", w.Sup1, w.CatFlexible));
            firstRef = (await svc.GetAsync(first)).AgreementReference;
            secondRef = (await svc.GetAsync(second)).AgreementReference;
            Assert.Equal(preview, firstRef);
            await Assert.ThrowsAsync<ForbiddenException>(() => svc.CreateDraftAsync(new CreateContractRequest(w.Org1, "", "Not my category", w.Sup2, w.CatEnergy)));
            await Assert.ThrowsAsync<DomainException>(() => svc.CreateDraftAsync(new CreateContractRequest(w.Org1, "", "Bad class", w.Sup1, w.CatPackaging, Classification: "TopSecret")));
            await Assert.ThrowsAsync<DomainException>(() => svc.CreateDraftAsync(new CreateContractRequest(w.Org1, "", "Bad unit", w.Sup1, w.CatPackaging, BusinessUnitId: Guid.NewGuid())));
        });
        Assert.StartsWith("WLD", firstRef); // the organisation's prefix (D3)
        Assert.NotEqual(firstRef, secondRef);
        Assert.True(string.CompareOrdinal(firstRef, secondRef) < 0, "numbers are allocated in sequence");
        await w.AsAsync("viewerAll", async s =>
            await Assert.ThrowsAsync<ForbiddenException>(() => s.Get<ISecuredContractService>().CreateDraftAsync(new CreateContractRequest(w.Org1, "", "Viewer cannot create", w.Sup1, w.CatPackaging))));
        await w.AsAsync("procAdmin", async s =>
        {
            var id = await s.Get<ISecuredContractService>().CreateDraftAsync(new CreateContractRequest(w.Org1, "", "Created by administrator", w.Sup2, w.CatEnergy));
            Assert.Equal("Draft", (await s.Get<ISecuredContractService>().GetAsync(id)).Status);
            // another organisation is outside this administrator's scope: it does not exist for them
            await Assert.ThrowsAsync<ResourceNotFoundException>(() => s.Get<ISecuredContractService>().CreateDraftAsync(new CreateContractRequest(w.Org2, "", "Wrong org", w.Sup3, w.CatOrg2)));
        });
    }

    public static async Task A_failed_creation_does_not_consume_a_reference_number(AccessWorld w)
    {
        await w.AsAsync("procAdmin", async s =>
        {
            var svc = s.Get<ISecuredContractService>();
            var before = await svc.PreviewNextReferenceAsync(w.Org1);
            await Assert.ThrowsAsync<DomainException>(() => svc.CreateDraftAsync(new CreateContractRequest(w.Org1, "", "Unknown supplier", Guid.NewGuid(), w.CatEnergy)));
            Assert.Equal(before, await svc.PreviewNextReferenceAsync(w.Org1));
        });
    }

    public static async Task Supplier_visibility_follows_visible_contracts_for_narrowed_viewers(AccessWorld w)
    {
        await w.AsAsync("viewerAll", async s =>
        {
            var names = (await s.Get<ISecuredSupplierService>().ListAsync()).Select(x => x.Id).ToHashSet();
            Assert.Contains(w.Sup1, names); Assert.Contains(w.Sup2, names); Assert.Contains(w.SupUnused, names); // organisation-wide viewer
            Assert.DoesNotContain(w.Sup3, names);                                                               // other organisation
            await Assert.ThrowsAsync<ResourceNotFoundException>(() => s.Get<ISecuredSupplierService>().GetAsync(w.Sup3));
        });
        await w.AsAsync("viewerCat", async s =>
        {
            var svc = s.Get<ISecuredSupplierService>();
            var ids = (await svc.ListAsync()).Select(x => x.Id).ToHashSet();
            Assert.Equal(new HashSet<Guid> { w.Sup1 }, ids);                                  // only the supplier of visible contracts
            await Assert.ThrowsAsync<ResourceNotFoundException>(() => svc.GetAsync(w.Sup2));  // Energy supplier: invisible
            await Assert.ThrowsAsync<ResourceNotFoundException>(() => svc.GetAsync(w.SupUnused));
            Assert.Equal(4, (await svc.ContractsOfAsync(w.Sup1)).Count); // C1, C2, C5, C7
        });
        await w.AsAsync("orgAdmin", async s => Assert.Empty(await s.Get<ISecuredSupplierService>().ListAsync()));
    }

    public static async Task Category_lists_are_narrowed_by_category_scope(AccessWorld w)
    {
        await w.AsAsync("viewerCat", async s =>
        {
            var codes = (await s.Get<ISecuredCategoryService>().ListAsync()).Select(c => c.Code).ToHashSet();
            Assert.Equal(new HashSet<string> { "PACKAGING", "PACKAGING-FLEX" }, codes);
        });
        await w.AsAsync("ownerPackaging", async s =>
            Assert.Equal(new HashSet<string> { "PACKAGING", "PACKAGING-FLEX", "ENERGY" }, (await s.Get<ISecuredCategoryService>().ListAsync()).Select(c => c.Code).ToHashSet()));
    }

    public static async Task Category_administration_needs_the_taxonomy_permissions(AccessWorld w)
    {
        await w.AsAsync("orgAdmin", async s =>
        {
            var id = await s.Get<ISecuredCategoryService>().CreateAsync(new CreateCategoryRequest(w.Org1, "FACILITIES-X", "Facilities X"));
            Assert.NotEqual(Guid.Empty, id);
            // Organisation 2 is outside this administrator's scope, so it does not exist for them (404, not 403).
            await Assert.ThrowsAsync<ResourceNotFoundException>(() => s.Get<ISecuredCategoryService>().CreateAsync(new CreateCategoryRequest(w.Org2, "NOPE", "Other organisation")));
        });
        await w.AsAsync("viewerAll", async s =>
            await Assert.ThrowsAsync<ForbiddenException>(() => s.Get<ISecuredCategoryService>().CreateAsync(new CreateCategoryRequest(w.Org1, "VIEWER-CAT", "Not allowed"))));
        await w.AsAsync("procAdmin", async s =>
        {
            // reassigning ownership is an administrative act (categories.assign), not a Category Owner act
            var id = await s.Get<ISecuredCategoryService>().AssignOwnerAsync(w.CatEnergy, w.Users["ownerPackaging"], false, "Backup cover");
            Assert.NotEqual(Guid.Empty, id);
        });
        await w.AsAsync("ownerPackaging", async s =>
            await Assert.ThrowsAsync<ForbiddenException>(() => s.Get<ISecuredCategoryService>().AssignOwnerAsync(w.CatEnergy, w.Users["ownerPackaging"], false, null)));
    }

    public static async Task List_visibility_matches_the_single_record_decision_for_every_user_and_contract(AccessWorld w)
    {
        // The list predicate and the evaluator must never disagree: a record that cannot be read directly must not be listed, and vice versa.
        foreach (var user in w.Users.Keys)
        {
            var listed = await w.VisibleContractKeysAsync(user);
            foreach (var (key, id) in w.Contracts)
            {
                var allowed = await w.AsAsync(user, async s =>
                {
                    var descriptor = await s.Get<ISecuredContractService>().DescribeAsync(id);
                    var decision = await s.Get<IAccessService>().CheckSilentAsync("contracts.read", descriptor);
                    return decision.Allowed;
                });
                Assert.True(allowed == listed.Contains(key), $"{user} / {key}: evaluator says {allowed}, list says {listed.Contains(key)}");
            }
        }
    }

    public static async Task Denials_are_audited_and_the_audit_chain_stays_valid(AccessWorld w)
    {
        await w.AsAsync("orgAdmin", async s => await Assert.ThrowsAsync<ForbiddenException>(() => s.Get<ISecuredContractService>().GetAsync(w.Contracts["C1"])));
        await using var scope = w.AsScopeless();
        var denials = await scope.Get<AuditDbContext>().Events.Where(e => e.Action == "authorisation.denied" && e.ActorId == w.Users["orgAdmin"].ToString()).ToListAsync();
        Assert.NotEmpty(denials);
        Assert.All(denials, d => Assert.Equal("Denied", d.Outcome));
        Assert.Contains("contracts.read", denials[0].Reason);
        Assert.True((await scope.Get<AuditChainVerifier>().VerifyAsync()).IsValid);
    }

    public static async Task A_disabled_user_sees_nothing(AccessWorld w)
    {
        Guid id = default;
        await using (var s = w.AsScopeless())
        {
            id = await s.Get<IUserService>().CreateActiveTrustedAsync("Soon Disabled", "disabled@" + w.TenantId.ToString("N")[..8] + ".example.test", null, null, null, "scenario");
            await s.Get<IRoleAssignmentService>().GrantTrustedAsync(new GrantRequest(id, RoleCodes.Legal, null, ScopeSet.ForOrganisations([w.Org1], "Restricted")), AssignmentSource.Demo, "scenario");
        }
        Assert.NotEmpty((await w.AsIdAsync(id, s => s.Get<ISecuredContractService>().ListAsync(new ContractFilter()))).Items);
        await using (var s = w.AsScopeless()) await s.Get<IUserService>().DisableAsync(id, "Left the organisation");
        Assert.Empty((await w.AsIdAsync(id, s => s.Get<ISecuredContractService>().ListAsync(new ContractFilter()))).Items);
        await w.AsIdAsync(id, async s => { await Assert.ThrowsAsync<ForbiddenException>(() => s.Get<ISecuredContractService>().UpdateDetailsAsync(w.Contracts["C1"], "x", null)); return true; });
    }

    public static async Task Delegated_administration_grants_within_scope_and_refuses_the_rest(AccessWorld w)
    {
        Guid target;
        await using (var s = w.AsScopeless())
            target = await s.Get<IUserService>().CreateActiveTrustedAsync("New Colleague", "colleague@" + w.TenantId.ToString("N")[..8] + ".example.test", null, null, null, "scenario");

        await w.AsAsync("orgAdmin", async s =>
        {
            var svc = s.Get<IRoleAssignmentService>();
            // within Organisation 1
            var id = await svc.GrantAsync(new GrantRequest(target, RoleCodes.Viewer, ViewerKind.Category, ScopeSet.ForOrganisations([w.Org1], "Internal", categories: [w.CatFlexible]), Reason: "New starter"));
            Assert.NotEqual(Guid.Empty, id);
            // outside Organisation 1, above its role, tenant-wide, self, and the top ceiling are refused
            await Assert.ThrowsAsync<ForbiddenException>(() => svc.GrantAsync(new GrantRequest(target, RoleCodes.Legal, null, ScopeSet.ForOrganisations([w.Org2], "Internal"))));
            await Assert.ThrowsAsync<ForbiddenException>(() => svc.GrantAsync(new GrantRequest(target, RoleCodes.TenantAdministrator, null, ScopeSet.Tenant("Internal"))));
            await Assert.ThrowsAsync<ForbiddenException>(() => svc.GrantAsync(new GrantRequest(target, RoleCodes.Legal, null, ScopeSet.ForOrganisations([w.Org1], "Restricted"))));
            await Assert.ThrowsAsync<DomainException>(() => svc.GrantAsync(new GrantRequest(w.Users["orgAdmin"], RoleCodes.Legal, null, ScopeSet.ForOrganisations([w.Org1], "Internal"))));
            await Assert.ThrowsAsync<DomainException>(() => svc.GrantAsync(new GrantRequest(target, RoleCodes.Viewer, ViewerKind.Site, ScopeSet.ForOrganisations([w.Org1], "Internal")))); // site viewer needs sites
        });
        await w.AsAsync("siteAdmin", async s =>
        {
            var svc = s.Get<IRoleAssignmentService>();
            await svc.GrantAsync(new GrantRequest(target, RoleCodes.Viewer, ViewerKind.Site, ScopeSet.ForOrganisations([w.Org1], "Internal", sites: [w.Site1])));
            await Assert.ThrowsAsync<ForbiddenException>(() => svc.GrantAsync(new GrantRequest(target, RoleCodes.Viewer, ViewerKind.Site, ScopeSet.ForOrganisations([w.Org1], "Internal", sites: [w.Site2]))));
            await Assert.ThrowsAsync<ForbiddenException>(() => svc.GrantAsync(new GrantRequest(target, RoleCodes.Approver, null, ScopeSet.ForOrganisations([w.Org1], "Internal", sites: [w.Site1]), "GroupA")));
        });
        await w.AsAsync("viewerAll", async s =>
            await Assert.ThrowsAsync<ForbiddenException>(() => s.Get<IRoleAssignmentService>().GrantAsync(new GrantRequest(target, RoleCodes.Viewer, ViewerKind.AllContracts, ScopeSet.ForOrganisations([w.Org1], "Internal")))));

        // the new colleague now sees exactly the Flexible Packaging record in scope
        // the new colleague holds two separate assignments: a Category Viewer of Flexible Packaging (ceiling Internal) and a Site A viewer (ceiling Internal).
        // C2 is Confidential so the first grant cannot show it; the second shows the Internal records at Site A. Assignments are never merged.
        var visible = await w.VisibleContractKeysForIdAsync(target);
        Assert.Equal(Keys("C1", "C3", "C7"), visible);
    }

    public static async Task Revocation_ends_authority_keeps_history_and_protects_the_last_administrator(AccessWorld w)
    {
        Guid ownerId = w.Users["ownerPackaging"];
        var assignmentId = await w.AsAsync("tenantAdmin", async s => (await s.Get<IRoleAssignmentService>().ListForUserAsync(ownerId)).Single().Id);
        await w.AsAsync("tenantAdmin", async s => await s.Get<IRoleAssignmentService>().RevokeAsync(assignmentId, "Role change"));
        await w.AsAsync("tenantAdmin", async s =>
        {
            Assert.Empty(await s.Get<IRoleAssignmentService>().ListForUserAsync(ownerId));                         // no active assignment
            Assert.Single(await s.Get<IRoleAssignmentService>().ListForUserAsync(ownerId, includeEnded: true));    // history retained
        });
        Assert.Empty(await w.VisibleContractKeysAsync("ownerPackaging"));

        // The only Tenant Administrator cannot be removed or disabled.
        var adminAssignment = await w.AsAsync("tenantAdmin", async s => (await s.Get<IRoleAssignmentService>().ListForUserAsync(w.Users["tenantAdmin"])).Single().Id);
        await w.AsAsync("tenantAdmin", async s =>
        {
            var ex = await Assert.ThrowsAsync<DomainException>(() => s.Get<IRoleAssignmentService>().RevokeAsync(adminAssignment, "Leaving"));
            Assert.Equal("last_administrator", ex.Code);
            var disable = await Assert.ThrowsAsync<DomainException>(() => s.Get<IUserService>().DisableAsync(w.Users["tenantAdmin"], "Leaving"));
            Assert.Equal("last_administrator", disable.Code);
        });
        // With a second Tenant Administrator in place the first can step down.
        Guid second;
        await using (var s = w.AsScopeless())
        {
            second = await s.Get<IUserService>().CreateActiveTrustedAsync("Second Admin", "admin2@" + w.TenantId.ToString("N")[..8] + ".example.test", null, null, null, "scenario");
            await s.Get<IRoleAssignmentService>().GrantTrustedAsync(new GrantRequest(second, RoleCodes.TenantAdministrator, null, ScopeSet.Tenant("Internal")), AssignmentSource.Demo, "scenario");
        }
        await w.AsIdAsync(second, async s => { await s.Get<IRoleAssignmentService>().RevokeAsync(adminAssignment, "Stepping down"); return true; });
    }

    public static async Task Identities_link_by_issuer_and_subject_invite_only_and_never_by_email_alone(AccessWorld w)
    {
        await using var s = w.AsScopeless();
        var users = s.Get<IUserService>();
        var email = "invitee@" + w.TenantId.ToString("N")[..8] + ".example.test";
        var invited = await users.InviteAsync("Invited Person", email);

        // unknown identity, no invitation
        var stranger = await users.ResolveLoginAsync(new ExternalLogin("demo", "https://idp.test", "stranger-1", "stranger@example.test", true, "Stranger", []));
        Assert.False(stranger.Succeeded); Assert.Equal("not_invited", stranger.DenialCode);
        // matching email but the provider does not vouch for it
        var unverified = await users.ResolveLoginAsync(new ExternalLogin("demo", "https://idp.test", "impostor-1", email, false, "Impostor", []));
        Assert.False(unverified.Succeeded);
        // verified email links the invited account once
        var first = await users.ResolveLoginAsync(new ExternalLogin("demo", "https://idp.test", "person-1", email, true, "Invited Person", []));
        Assert.True(first.Succeeded); Assert.Equal(invited, first.UserId);
        Assert.Equal(UserStatus.Active, (await users.GetAsync(invited))!.Status);
        // afterwards the account is found by (issuer, subject)
        Assert.Equal(invited, (await users.ResolveLoginAsync(new ExternalLogin("demo", "https://idp.test", "person-1", null, false, null, []))).UserId);
        // another subject asserting the same verified email cannot take over the now-active account
        var takeover = await users.ResolveLoginAsync(new ExternalLogin("demo", "https://idp.test", "person-2", email, true, "Takeover", []));
        Assert.False(takeover.Succeeded);
        // the same subject at a different issuer is a different identity
        Assert.False((await users.ResolveLoginAsync(new ExternalLogin("other", "https://other-idp.test", "person-1", null, false, null, []))).Succeeded);
        // a disabled account cannot sign in
        await users.DisableAsync(invited, "Test");
        Assert.Equal("user_disabled", (await users.ResolveLoginAsync(new ExternalLogin("demo", "https://idp.test", "person-1", null, false, null, []))).DenialCode);
    }

    public static async Task Group_mappings_grant_while_the_user_is_in_the_group_and_end_when_they_leave(AccessWorld w)
    {
        await using var s = w.AsScopeless();
        var users = s.Get<IUserService>();
        var email = "mapped@" + w.TenantId.ToString("N")[..8] + ".example.test";
        var userId = await users.InviteAsync("Mapped Person", email);
        await s.Get<IGroupMappingService>().AddAsync("demo", "group-legal", RoleCodes.Legal, null, ScopeSet.ForOrganisations([w.Org1], "Restricted"), null);
        var assignments = s.Get<IRoleAssignmentService>();

        await users.ResolveLoginAsync(new ExternalLogin("demo", "https://idp.test", "mapped-1", email, true, "Mapped Person", ["group-legal", "unrelated"]));
        var granted = await assignments.ListForUserAsync(userId);
        Assert.Equal(RoleCodes.Legal, Assert.Single(granted).RoleCode);
        Assert.Equal(AssignmentSource.GroupMapping, granted[0].Source);

        // signing in again does not duplicate the assignment
        await users.ResolveLoginAsync(new ExternalLogin("demo", "https://idp.test", "mapped-1", null, false, null, ["group-legal"]));
        Assert.Single(await assignments.ListForUserAsync(userId));

        // leaving the group ends the mapped authority
        await users.ResolveLoginAsync(new ExternalLogin("demo", "https://idp.test", "mapped-1", null, false, null, ["unrelated"]));
        Assert.Empty(await assignments.ListForUserAsync(userId));
        Assert.Single(await assignments.ListForUserAsync(userId, includeEnded: true));

        // a provider that cannot list the groups (overage) never revokes
        await users.ResolveLoginAsync(new ExternalLogin("demo", "https://idp.test", "mapped-1", null, false, null, ["group-legal"]));
        await users.ResolveLoginAsync(new ExternalLogin("demo", "https://idp.test", "mapped-1", null, false, null, [], GroupsOverage: true));
        Assert.Single(await assignments.ListForUserAsync(userId));
    }

    public static async Task Identity_data_is_tenant_scoped_and_another_tenant_cannot_see_it(AccessWorld w, AccessWorld other)
    {
        await w.AsAsync("legal", async s =>
        {
            var visible = (await s.Get<IUserService>().ListAsync()).Select(u => u.Id).ToHashSet();
            Assert.Contains(w.Users["legal"], visible);
            Assert.DoesNotContain(other.Users["legal"], visible);
            Assert.Null(await s.Get<IUserService>().GetAsync(other.Users["legal"]));
        });
        // the other tenant's contracts do not exist for this tenant's users
        await w.AsAsync("auditor", async s =>
        {
            await Assert.ThrowsAsync<ResourceNotFoundException>(() => s.Get<ISecuredContractService>().GetAsync(other.Contracts["C1"]));
            var all = await s.Get<ISecuredContractService>().ListAsync(new ContractFilter(PageSize: 200));
            Assert.DoesNotContain(all.Items, i => other.Contracts.ContainsValue(i.Id));
        });
        // the same (issuer, subject) can exist independently in two tenants
        await using var a = w.AsScopeless(); await using var b = other.AsScopeless();
        var ua = await a.Get<IUserService>().CreateActiveTrustedAsync("Shared Subject", "shared@a.example.test", "demo", "https://shared-idp.test", "shared-subject", "scenario");
        var ub = await b.Get<IUserService>().CreateActiveTrustedAsync("Shared Subject", "shared@b.example.test", "demo", "https://shared-idp.test", "shared-subject", "scenario");
        Assert.Equal(ua, (await a.Get<IUserService>().ResolveLoginAsync(new ExternalLogin("demo", "https://shared-idp.test", "shared-subject", null, false, null, []))).UserId);
        Assert.Equal(ub, (await b.Get<IUserService>().ResolveLoginAsync(new ExternalLogin("demo", "https://shared-idp.test", "shared-subject", null, false, null, []))).UserId);
    }

    public static async Task Provider_settings_are_validated_and_stored_without_secret_values(AccessWorld w)
    {
        await using var s = w.AsScopeless();
        var registry = s.Get<IProviderRegistry>();
        var bad = await registry.SaveAsync(new IdentityProviderSettings("corp-oidc", IdentityProviderKind.Oidc, "Corporate", "https://idp.example.test", "client", "literal-secret-value", null));
        Assert.Contains(bad, i => i.Code == "client_secret_ref" && i.Blocking);
        Assert.Empty(await registry.ListAsync());
        var ok = await registry.SaveAsync(new IdentityProviderSettings("corp-oidc", IdentityProviderKind.Oidc, "Corporate", "https://idp.example.test", "client", "env:ECP_TEST_NOT_SET", null));
        Assert.DoesNotContain(ok, i => i.Blocking);
        var stored = Assert.Single(await registry.ListAsync());
        Assert.Equal("env:ECP_TEST_NOT_SET", stored.ClientSecretRef);
        Assert.Null(await registry.TestSignInAsync("corp-oidc"));
        await registry.RecordTestSignInAsync("corp-oidc");
        Assert.NotNull(await registry.TestSignInAsync("corp-oidc"));
        // editing the settings invalidates the test sign-in
        await registry.SaveAsync(new IdentityProviderSettings("corp-oidc", IdentityProviderKind.Oidc, "Corporate", "https://idp2.example.test", "client", "env:ECP_TEST_NOT_SET", null));
        Assert.Null(await registry.TestSignInAsync("corp-oidc"));
        // the audit trail names the provider and that a secret reference is set, but never its target
        var events = await s.Get<AuditDbContext>().Events.Where(e => e.EntityType == nameof(IdentityProviderRegistration)).ToListAsync();
        Assert.NotEmpty(events);
        Assert.All(events, e => Assert.DoesNotContain("ECP_TEST_NOT_SET", e.NewValuesJson ?? ""));
    }
}
