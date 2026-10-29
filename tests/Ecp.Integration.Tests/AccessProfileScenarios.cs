using Ecp.Access;
using Ecp.Audit;
using Ecp.Contracts;
using Ecp.Identity;
using Ecp.Identity.Access;
using Ecp.Identity.Domain;
using Ecp.Identity.Providers;
using Ecp.Persistence;
using Ecp.SharedKernel;
using Ecp.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecp.Integration.Tests;

/// <summary>
/// UX-2: access profiles are a presentation layer over the security engine. They turn plain choices into ordinary grants and ownerships in one transaction through the services that enforce delegation,
/// self-grant and last-administrator rules; effective access is computed by the evaluator itself, never from the provenance rows.
/// </summary>
internal static class AccessProfileScenarios
{
    private static async Task<T> AdminAsync<T>(AccessWorld w, string admin, Func<TenantScope, Task<T>> work) => await w.AsAsync(admin, s => { s.Get<ReauthenticationContext>().Set(w.Now); return work(s); });

    private static async Task<Guid> NewPersonAsync(AccessWorld w, string name)
    {
        await using var s = w.AsScopeless();
        return await s.Get<IUserService>().CreateActiveTrustedAsync(name, name.Replace(' ', '.').ToLowerInvariant() + "@" + w.TenantId.ToString("N")[..8] + ".example.test", "demo", "https://demo.example.test", name + "|" + w.TenantId, "test");
    }

    private static async Task<List<Ecp.Audit.Domain.AuditEvent>> AuditAsync(AccessWorld w, string action)
    {
        await using var scope = w.AsScopeless();
        return await scope.Get<IAuditEventSource>().Events.Where(e => e.Action == action).OrderBy(e => e.Sequence).ToListAsync();
    }

    public static async Task A_contract_manager_profile_becomes_an_ordinary_role_grant_and_category_ownership(AccessWorld w)
    {
        var person = await NewPersonAsync(w, "Maya Manager");
        var result = await AdminAsync(w, "orgAdmin", s => s.Get<IAccessProfileService>().ApplyAsync(
            new AccessChange(person, w.Org1, [AccessProfiles.ContractManager], Ownership: OwnershipChoice.Selected, OwnedCategoryIds: [w.CatPackaging, w.CatEnergy], Reason: "New starter")));
        Assert.Equal(1, result.GrantsCreated);
        Assert.Equal(2, result.OwnershipsCreated);

        await using var scope = w.AsScopeless();
        var grants = await scope.Get<IRoleAssignmentService>().ListForUserAsync(person);
        var grant = Assert.Single(grants);
        Assert.Equal(RoleCodes.CategoryOwner, grant.RoleCode);                                    // an existing stable role code, nothing invented
        Assert.Equal([w.Org1], grant.Scope.OrganisationIds);
        Assert.Equal(AssignmentSource.Manual, grant.Source);                                      // the profile adds no new kind of authority
        var owned = (await scope.Get<ICategoryAdministration>().ListAllAsync()).Where(c => c.Owners.Any(o => o.UserId == person && o.EffectiveToUtc is null)).Select(c => c.Id).ToHashSet();
        Assert.Equal(new[] { w.CatPackaging, w.CatEnergy }.ToHashSet(), owned);

        var report = await AdminAsync(w, "orgAdmin", s => s.Get<IEffectiveAccess>().ForUserAsync(person));
        Assert.Contains(report.Profiles, p => p.Key == AccessProfiles.ContractManager);
        var create = report.Abilities.Single(a => a.Key == "create-contracts");
        Assert.True(create.Can);
        Assert.Contains("Packaging", create.Where);                                               // says where, in names, not ids
        Assert.NotEmpty(create.Because);
        Assert.False(report.Abilities.Single(a => a.Key == "manage-people").Can);                  // a manager cannot administer people
        Assert.Single(await AuditAsync(w, "access.profile.applied"), e => e.EntityId == person.ToString());
    }


    // -- UX-9: durable all-category ownership, the review, and approval authorities by name ------------------------------------------------------------------

    private static async Task<HashSet<Guid>> OwnedByAsync(AccessWorld w, Guid person)
    {
        await using var scope = w.AsScopeless();
        return (await scope.Get<ICategoryAdministration>().ListAllAsync()).Where(c => c.Owners.Any(o => o.UserId == person && o.EffectiveToUtc is null)).Select(c => c.Id).ToHashSet();
    }

    public static async Task Owning_all_categories_means_current_and_future_ones_and_ending_it_leaves_other_ownership_alone(AccessWorld w)
    {
        var maya = await NewPersonAsync(w, "Maya AllOwner");
        var erin = await NewPersonAsync(w, "Erin Explicit");
        // Erin was given Energy explicitly, before and independently of any policy.
        await AdminAsync(w, "orgAdmin", s => s.Get<IAccessProfileService>().ApplyAsync(new AccessChange(erin, w.Org1, [AccessProfiles.ContractManager], Ownership: OwnershipChoice.Selected, OwnedCategoryIds: [w.CatEnergy])));

        var result = await AdminAsync(w, "orgAdmin", s => s.Get<IAccessProfileService>().ApplyAsync(new AccessChange(maya, w.Org1, [AccessProfiles.ContractManager], Ownership: OwnershipChoice.All)));
        Assert.True(result.OwnershipsCreated >= 1);
        var before = await OwnedByAsync(w, maya);
        Assert.Contains(w.CatPackaging, before); Assert.Contains(w.CatEnergy, before);          // the categories that exist (Flexible is covered through its parent)

        var policies = await AdminAsync(w, "orgAdmin", s => s.Get<ICategoryOwnershipPolicy>().ForPersonAsync(maya));
        var policy = Assert.Single(policies);
        Assert.Equal(w.Org1, policy.OrganisationId);
        Assert.True(policy.Open);

        // A category created afterwards, by the Categories screen's service, is recognised in the same transaction.
        var created = await AdminAsync(w, "orgAdmin", s => s.Get<ISecuredCategoryService>().CreateAsync(new CreateCategoryRequest(w.Org1, "LOGISTICS", "Logistics")));
        Assert.Contains(created, await OwnedByAsync(w, maya));
        // ... and by the trusted route set-up, onboarding and imports use.
        var upserted = await AdminAsync(w, "orgAdmin", s => s.Get<ICategoryAdministration>().UpsertCategoryAsync(w.Org1, "SERVICES", "Services", null));
        Assert.Contains(upserted, await OwnedByAsync(w, maya));
        // A different organisation's category is not theirs: the policy is for one organisation.
        var elsewhere = await AdminAsync(w, "tenantAdmin", s => s.Get<ICategoryAdministration>().UpsertCategoryAsync(w.Org2, "LEGAL-SERVICES", "Legal services", null));
        Assert.DoesNotContain(elsewhere, await OwnedByAsync(w, maya));
        // Category authorisation itself is unchanged: she can create in the new category and the report explains why.
        var report = await AdminAsync(w, "orgAdmin", s => s.Get<IEffectiveAccess>().ForUserAsync(maya));
        Assert.Single(report.Standing);
        var edit = report.Abilities.Single(a => a.Key == "edit-contracts");
        Assert.True(edit.Can);
        Assert.Contains("every current and future", edit.Where);
        Assert.Contains("standing instruction", string.Join(" ", edit.Because));
        Assert.NotEmpty(await AuditAsync(w, "access.ownership_policy.set"));
        Assert.Contains(await AuditAsync(w, "access.ownership_policy.applied"), e => e.EntityId == created.ToString());

        // Ending the profile ends the policy and only the ownership it created. Erin keeps hers.
        var app = (await AdminAsync(w, "orgAdmin", s => s.Get<IAccessProfileService>().ApplicationsOfAsync(maya, false))).Single();
        await AdminAsync(w, "orgAdmin", async s => { await s.Get<IAccessProfileService>().EndApplicationAsync(app.Id, "No longer needed"); return true; });
        Assert.Empty(await OwnedByAsync(w, maya));
        Assert.Empty(await AdminAsync(w, "orgAdmin", s => s.Get<ICategoryOwnershipPolicy>().ForPersonAsync(maya)));
        Assert.Contains(w.CatEnergy, await OwnedByAsync(w, erin));
        Assert.NotEmpty(await AuditAsync(w, "access.ownership_policy.ended"));
        var later = await AdminAsync(w, "orgAdmin", s => s.Get<ISecuredCategoryService>().CreateAsync(new CreateCategoryRequest(w.Org1, "FLEET", "Fleet")));
        Assert.DoesNotContain(later, await OwnedByAsync(w, maya));                                  // nothing is recognised any more
    }

    public static async Task A_standing_ownership_policy_leaves_existing_ownership_untouched_and_is_not_visible_to_another_tenant(AccessWorld w, AccessWorld other)
    {
        var sam = await NewPersonAsync(w, "Sam ExistingOwner");
        var tara = await NewPersonAsync(w, "Tara AllOwner");
        await AdminAsync(w, "orgAdmin", s => s.Get<IAccessProfileService>().ApplyAsync(new AccessChange(sam, w.Org1, [AccessProfiles.ContractManager], Ownership: OwnershipChoice.Selected, OwnedCategoryIds: [w.CatPackaging])));
        await AdminAsync(w, "orgAdmin", s => s.Get<IAccessProfileService>().ApplyAsync(new AccessChange(tara, w.Org1, [AccessProfiles.ContractManager], Ownership: OwnershipChoice.All)));
        await using (var scope = w.AsScopeless())
        {
            var packaging = (await scope.Get<ICategoryAdministration>().ListAllAsync()).Single(c => c.Id == w.CatPackaging);
            var primary = Assert.Single(packaging.Owners, o => o.IsPrimary);
            Assert.NotEqual(tara, primary.UserId);                                               // an existing primary is not displaced by a policy
            Assert.Contains(packaging.Owners, o => o.UserId == sam);                             // explicit ownership is left exactly as it was
            Assert.Contains(packaging.Owners, o => o.UserId == tara && !o.IsPrimary);            // the policy owner stands beside them
        }
        // Another tenant sees none of it and cannot end it.
        Assert.Empty(await AdminAsync(other, "tenantAdmin", s => s.Get<ICategoryOwnershipPolicy>().ListAsync()));
        var policy = (await AdminAsync(w, "orgAdmin", s => s.Get<ICategoryOwnershipPolicy>().ListAsync())).Single(p => p.UserId == tara);
        await Assert.ThrowsAnyAsync<Exception>(() => AdminAsync(other, "tenantAdmin", async s => { await s.Get<ICategoryOwnershipPolicy>().EndAsync(policy.Id, "not mine"); return true; }));
        Assert.Contains(await AdminAsync(w, "orgAdmin", s => s.Get<ICategoryOwnershipPolicy>().ListAsync()), p => p.UserId == tara && p.Open);
    }

    public static async Task The_policy_needs_the_right_to_assign_ownership_and_a_recent_sign_in(AccessWorld w)
    {
        var person = await NewPersonAsync(w, "Pia Policy");
        await AdminAsync(w, "orgAdmin", s => s.Get<IAccessProfileService>().ApplyAsync(new AccessChange(person, w.Org1, [AccessProfiles.ContractManager])));
        // A tenant administrator alone cannot assign ownership (existing rule), so cannot set a policy either.
        await Assert.ThrowsAnyAsync<Exception>(() => AdminAsync(w, "tenantAdmin", s => s.Get<ICategoryOwnershipPolicy>().SetAsync(w.Org1, person, true, "try")));
        // Without a recent sign-in nothing is set.
        await Assert.ThrowsAsync<ReauthenticationRequiredException>(() => w.AsAsync("orgAdmin", s => s.Get<ICategoryOwnershipPolicy>().SetAsync(w.Org1, person, true, "stale")));
        Assert.Empty(await AdminAsync(w, "orgAdmin", s => s.Get<ICategoryOwnershipPolicy>().ForPersonAsync(person)));
        var outcome = await AdminAsync(w, "orgAdmin", s => s.Get<ICategoryOwnershipPolicy>().SetAsync(w.Org1, person, true, "Owns everything"));
        Assert.True(outcome.Created);
        var again = await AdminAsync(w, "orgAdmin", s => s.Get<ICategoryOwnershipPolicy>().SetAsync(w.Org1, person, true, "Again"));
        Assert.False(again.Created);                                                                 // idempotent: one standing policy per person and organisation
        Assert.Single(await AdminAsync(w, "orgAdmin", s => s.Get<ICategoryOwnershipPolicy>().ForPersonAsync(person)));
    }

    public static async Task The_review_says_in_plain_English_what_saving_would_give_and_shows_codes_only_in_advanced_details(AccessWorld w)
    {
        var person = await NewPersonAsync(w, "Rhea Review");
        var change = new AccessChange(person, w.Org1, [AccessProfiles.ContractManager], Ownership: OwnershipChoice.All, Ceiling: "Internal");
        var plan = await AdminAsync(w, "orgAdmin", s => s.Get<IAccessProfileService>().PreviewAsync(change));
        Assert.True(plan.CanApply, string.Join("; ", plan.Problems));
        await using var scope = w.AsScopeless();
        var scale = await scope.Get<IAccessService>().ClassificationScaleAsync();
        var input = new ReviewInput("Rhea Review", "World One", change.ProfileKeys, change.Ownership, change.Ceiling);
        var review = AccessReviewBuilder.Build(input, plan.After!, scale, plan.Steps);
        Assert.Contains(review.WillBeAbleTo, l => l.Text.StartsWith("Create", StringComparison.Ordinal));
        Assert.Contains(review.WillBeAbleTo, l => l.Text.StartsWith("Edit", StringComparison.Ordinal));
        Assert.Contains(review.WillBeAbleTo, l => l.Text.StartsWith("Upload", StringComparison.Ordinal));
        Assert.Contains(review.WillNotBeAbleTo, l => l.Text.Contains("formal review", StringComparison.Ordinal));
        Assert.Contains(review.WillNotBeAbleTo, l => l.Text.Contains("own submissions", StringComparison.Ordinal));
        Assert.Contains(review.WillNotBeAbleTo, l => l.Text.StartsWith("Administer", StringComparison.Ordinal));
        Assert.Contains(review.WillNotBeAbleTo, l => l.Text.Contains("above Internal", StringComparison.Ordinal) || l.Text.Contains("above", StringComparison.Ordinal));
        Assert.Contains(review.Facts, f => f.Label.Contains("ownership", StringComparison.OrdinalIgnoreCase) && f.Value.Contains("current and future", StringComparison.Ordinal));
        Assert.Contains(review.Facts, f => f.Label.Contains("ceiling", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(review.AdvancedGrants);                                                          // no codes unless asked
        Assert.DoesNotContain(RoleCodes.CategoryOwner, string.Join(" ", review.WillBeAbleTo.Select(l => l.Text + l.Where).Concat(review.WillNotBeAbleTo.Select(l => l.Text))));
        var advanced = AccessReviewBuilder.Build(input, plan.After!, scale, plan.Steps, includeAdvanced: true);
        Assert.Contains(advanced.AdvancedGrants, g => g.RoleCode.Contains(RoleCodes.CategoryOwner, StringComparison.Ordinal));
        // The review is a dry run: nothing was kept.
        Assert.Empty(await OwnedByAsync(w, person));
    }

    public static async Task Approval_authorities_are_set_up_by_name_and_membership_changes_go_through_the_approver_group_rules(AccessWorld w)
    {
        var pat = await NewPersonAsync(w, "Pat Approver");
        var bo = await NewPersonAsync(w, "Bo Backup");
        var plan = await AdminAsync(w, "orgAdmin", s => s.Get<IApprovalAuthorities>().PreviewAsync(new AuthorityChange(w.Org1, null, "Registration Approvers", [pat], [bo])));
        Assert.True(plan.CanApply, string.Join("; ", plan.Problems));
        Assert.True(plan.IsNew);
        Assert.Matches("^[A-Z0-9-]+$", plan.Key);                                                   // a key was made for them
        var view0 = await AdminAsync(w, "orgAdmin", s => s.Get<IApprovalAuthorities>().ViewAsync(w.Org1));
        Assert.DoesNotContain(view0.Authorities, a => a.Key == plan.Key);                           // a preview registers nothing

        var saved = await AdminAsync(w, "orgAdmin", s => s.Get<IApprovalAuthorities>().SaveAsync(new AuthorityChange(w.Org1, null, "Registration Approvers", [pat], [bo])));
        var view = await AdminAsync(w, "orgAdmin", s => s.Get<IApprovalAuthorities>().ViewAsync(w.Org1));
        var authority = view.Authorities.Single(a => a.Key == saved.Key);
        Assert.Equal("Registration Approvers", authority.DisplayName);
        Assert.Equal(["Pat Approver"], authority.Members.Select(m => m.Name));
        Assert.Equal(["Bo Backup"], authority.Backups.Select(m => m.Name));
        Assert.True(authority.Registered);
        await using (var scope = w.AsScopeless())
        {
            var held = await scope.Get<IRoleAssignmentService>().ListForUserAsync(pat);
            Assert.Contains(held, a => a.RoleCode == RoleCodes.Approver && a.ApproverGroupKey == saved.Key);      // the ordinary role, carrying the generated key
        }
        // Changing the people: Pat is replaced by Bo as a member.
        await AdminAsync(w, "orgAdmin", s => s.Get<IApprovalAuthorities>().SaveAsync(new AuthorityChange(w.Org1, saved.Key, null, [bo], [])));
        var after = (await AdminAsync(w, "orgAdmin", s => s.Get<IApprovalAuthorities>().ViewAsync(w.Org1))).Authorities.Single(a => a.Key == saved.Key);
        Assert.Equal(["Bo Backup"], after.Members.Select(m => m.Name));
        Assert.Empty(after.Backups);
        // A second authority with the same name gets its own key.
        var twin = await AdminAsync(w, "orgAdmin", s => s.Get<IApprovalAuthorities>().SaveAsync(new AuthorityChange(w.Org1, null, "Registration Approvers", [pat], [])));
        Assert.NotEqual(saved.Key, twin.Key);
        // It needs a recent sign-in, and a person cannot be both a member and a backup.
        await Assert.ThrowsAsync<ReauthenticationRequiredException>(() => w.AsAsync("orgAdmin", s => s.Get<IApprovalAuthorities>().SaveAsync(new AuthorityChange(w.Org1, saved.Key, null, [pat], []))));
        await Assert.ThrowsAnyAsync<DomainException>(() => AdminAsync(w, "orgAdmin", s => s.Get<IApprovalAuthorities>().SaveAsync(new AuthorityChange(w.Org1, saved.Key, null, [pat], [pat]))));
        Assert.NotEmpty(await AuditAsync(w, "access.approval_authority.saved"));
    }

    public static async Task A_manager_who_owns_nothing_is_told_why_they_cannot_create_yet(AccessWorld w)
    {
        var person = await NewPersonAsync(w, "Nina NoOwnership");
        await AdminAsync(w, "tenantAdmin", s => s.Get<IAccessProfileService>().ApplyAsync(new AccessChange(person, w.Org1, [AccessProfiles.ContractManager])));
        var report = await AdminAsync(w, "tenantAdmin", s => s.Get<IEffectiveAccess>().ForUserAsync(person));
        var create = report.Abilities.Single(a => a.Key == "create-contracts");
        Assert.False(create.Can);
        Assert.NotNull(create.Conditions);
        Assert.Contains(report.Attention, a => a.Code == "role-without-ownership");
    }

    public static async Task A_failure_part_way_through_leaves_nothing_behind(AccessWorld w)
    {
        var person = await NewPersonAsync(w, "Paul Partial");
        var before = (await AuditAsync(w, "access.profile.applied")).Count;
        // The role would be granted first; the ownership step then fails because none of the chosen categories exists. All of it must be undone.
        await Assert.ThrowsAsync<DomainException>(() => AdminAsync(w, "tenantAdmin", s => s.Get<IAccessProfileService>().ApplyAsync(
            new AccessChange(person, w.Org1, [AccessProfiles.ReadOnly, AccessProfiles.ContractManager], Ownership: OwnershipChoice.Selected, OwnedCategoryIds: [Guid.NewGuid()]))));
        await using var scope = w.AsScopeless();
        Assert.Empty(await scope.Get<IRoleAssignmentService>().ListForUserAsync(person, includeEnded: true));
        Assert.DoesNotContain((await scope.Get<ICategoryAdministration>().ListAllAsync()).SelectMany(c => c.Owners), o => o.UserId == person);
        Assert.Equal(before, (await AuditAsync(w, "access.profile.applied")).Count);               // the audit buffer was discarded with the transaction
        Assert.Empty(await AdminAsync(w, "tenantAdmin", s => s.Get<IAccessProfileService>().ApplicationsOfAsync(person, includeEnded: true)));
    }

    public static async Task Previewing_shows_what_saving_would_do_and_changes_nothing(AccessWorld w)
    {
        var person = await NewPersonAsync(w, "Peter Preview");
        var plan = await AdminAsync(w, "orgAdmin", s => s.Get<IAccessProfileService>().PreviewAsync(
            new AccessChange(person, w.Org1, [AccessProfiles.ContractManager], Ownership: OwnershipChoice.All)));
        Assert.True(plan.CanApply);
        Assert.Contains(plan.Steps, st => st.Kind == "grant");
        Assert.Contains(plan.Steps, st => st.Kind == "ownership");
        Assert.NotNull(plan.After);
        Assert.True(plan.After!.Abilities.Single(a => a.Key == "create-contracts").Can);
        Assert.False(plan.Before!.Abilities.Single(a => a.Key == "create-contracts").Can);
        await using var scope = w.AsScopeless();
        Assert.Empty(await scope.Get<IRoleAssignmentService>().ListForUserAsync(person, includeEnded: true));   // nothing persisted
        Assert.DoesNotContain((await scope.Get<ICategoryAdministration>().ListAllAsync()).SelectMany(c => c.Owners), o => o.UserId == person);
    }

    public static async Task The_delegation_rules_still_decide_who_may_give_what(AccessWorld w)
    {
        var person = await NewPersonAsync(w, "Dana Delegated");
        // A site administrator cannot give an organisation-wide administrator profile, whatever the wizard offers.
        var plan = await AdminAsync(w, "siteAdmin", s => s.Get<IAccessProfileService>().PreviewAsync(new AccessChange(person, w.Org1, [AccessProfiles.OrganisationAdministrator])));
        Assert.False(plan.CanApply);
        Assert.NotEmpty(plan.Problems);
        await Assert.ThrowsAnyAsync<Exception>(() => AdminAsync(w, "siteAdmin", s => s.Get<IAccessProfileService>().ApplyAsync(new AccessChange(person, w.Org1, [AccessProfiles.OrganisationAdministrator]))));
        await using var scope = w.AsScopeless();
        Assert.Empty(await scope.Get<IRoleAssignmentService>().ListForUserAsync(person, includeEnded: true));
        // The catalogue marks it as not available to this administrator so the wizard can say so before they try.
        var offered = await AdminAsync(w, "siteAdmin", s => s.Get<IAccessProfileService>().ProfilesForAsync(w.Org1));
        Assert.False(offered.Single(o => o.Key == AccessProfiles.OrganisationAdministrator).Offered);
    }

    public static async Task A_person_cannot_give_themselves_access_through_a_profile(AccessWorld w)
    {
        var me = w.Users["orgAdmin"];
        var plan = await AdminAsync(w, "orgAdmin", s => s.Get<IAccessProfileService>().PreviewAsync(new AccessChange(me, w.Org1, [AccessProfiles.ContractManager], Ownership: OwnershipChoice.All)));
        Assert.False(plan.CanApply);
        Assert.Contains(plan.Problems, p => p.Contains("yourself", StringComparison.OrdinalIgnoreCase));
        await Assert.ThrowsAsync<DomainException>(() => AdminAsync(w, "orgAdmin", s => s.Get<IAccessProfileService>().ApplyAsync(new AccessChange(me, w.Org1, [AccessProfiles.ContractManager]))));
    }

    public static async Task Changing_access_needs_a_recent_sign_in(AccessWorld w)
    {
        var person = await NewPersonAsync(w, "Rita Recent");
        await Assert.ThrowsAsync<ReauthenticationRequiredException>(() => w.AsAsync("tenantAdmin", s => s.Get<IAccessProfileService>().ApplyAsync(new AccessChange(person, w.Org1, [AccessProfiles.ReadOnly]))));
        await using var scope = w.AsScopeless();
        Assert.Empty(await scope.Get<IRoleAssignmentService>().ListForUserAsync(person, includeEnded: true));
    }

    public static async Task An_approval_authority_needs_an_approver_group_and_a_reviewer_needs_the_step_switched_on(AccessWorld w)
    {
        var person = await NewPersonAsync(w, "Alex Approver");
        var e = await Assert.ThrowsAsync<DomainException>(() => AdminAsync(w, "tenantAdmin", s => s.Get<IAccessProfileService>().ApplyAsync(new AccessChange(person, w.Org1, [AccessProfiles.ApprovalAuthority]))));
        Assert.Equal("approver_group_required", e.Code);
        var ok = await AdminAsync(w, "tenantAdmin", s => s.Get<IAccessProfileService>().ApplyAsync(new AccessChange(person, w.Org1, [AccessProfiles.ApprovalAuthority], ApproverGroupKey: "GroupA")));
        Assert.Equal(1, ok.GrantsCreated);
        var report = await AdminAsync(w, "tenantAdmin", s => s.Get<IEffectiveAccess>().ForUserAsync(person));
        Assert.Contains("GroupA", report.ApproverGroups);
        var approve = report.Abilities.Single(a => a.Key == "approve-contracts");
        Assert.True(approve.Can);
        Assert.False(string.IsNullOrEmpty(approve.Conditions));                                    // "only while a request for their group is waiting"
    }

    public static async Task Deleting_every_provenance_row_changes_nobodys_permissions(AccessWorld w)
    {
        var person = await NewPersonAsync(w, "Pia Provenance");
        await AdminAsync(w, "orgAdmin", s => s.Get<IAccessProfileService>().ApplyAsync(new AccessChange(person, w.Org1, [AccessProfiles.ContractManager, AccessProfiles.ReadOnly], Ownership: OwnershipChoice.All)));
        var before = await AdminAsync(w, "orgAdmin", s => s.Get<IEffectiveAccess>().ForUserAsync(person));
        Assert.NotEmpty(before.Profiles);
        await using (var scope = w.AsScopeless())
        {
            var db = scope.Get<Ecp.Access.Persistence.AccessDbContext>();
            db.ProfileApplications.RemoveRange(db.ProfileApplications);
            await scope.Get<IUnitOfWork>().ExecuteAsync(async c => { await db.SaveChangesAsync(c); });
        }
        var after = await AdminAsync(w, "orgAdmin", s => s.Get<IEffectiveAccess>().ForUserAsync(person));
        Assert.Empty(after.Profiles);                                                             // the explanation is gone ...
        Assert.Equal(before.Abilities.Where(a => a.Can).Select(a => a.Key), after.Abilities.Where(a => a.Can).Select(a => a.Key));   // ... the permissions are not
        Assert.Equal(before.Owns.Select(o => o.CategoryId), after.Owns.Select(o => o.CategoryId));
        Assert.Equal(before.Grants.Count(g => g.Active), after.Grants.Count(g => g.Active));
    }

    public static async Task Ending_an_application_ends_only_what_it_created(AccessWorld w)
    {
        var person = await NewPersonAsync(w, "Eli Ended");
        await AdminAsync(w, "orgAdmin", s => s.Get<IAccessProfileService>().ApplyAsync(new AccessChange(person, w.Org1, [AccessProfiles.ContractManager], Ownership: OwnershipChoice.Selected, OwnedCategoryIds: [w.CatEnergy])));
        // A separate, direct grant that the profile did not create must survive.
        await using (var s = w.AsScopeless())
            await s.Get<IRoleAssignmentService>().GrantTrustedAsync(new GrantRequest(person, RoleCodes.Viewer, ViewerKind.AllContracts, ScopeSet.ForOrganisations([w.Org1], "Internal")), AssignmentSource.Manual, "test");
        var application = Assert.Single(await AdminAsync(w, "orgAdmin", s => s.Get<IAccessProfileService>().ApplicationsOfAsync(person, includeEnded: false)));
        await AdminAsync(w, "orgAdmin", async s => { await s.Get<IAccessProfileService>().EndApplicationAsync(application.Id, "Moved teams"); return true; });
        await using var scope = w.AsScopeless();
        var active = await scope.Get<IRoleAssignmentService>().ListForUserAsync(person);
        Assert.Equal([RoleCodes.Viewer], active.Select(a => a.RoleCode));
        Assert.DoesNotContain((await scope.Get<ICategoryAdministration>().ListAllAsync()).SelectMany(c => c.Owners), o => o.UserId == person && o.EffectiveToUtc is null);
        Assert.Empty(await AdminAsync(w, "orgAdmin", s => s.Get<IAccessProfileService>().ApplicationsOfAsync(person, includeEnded: false)));
        Assert.Single(await AuditAsync(w, "access.profile.ended"), e => e.EntityId == person.ToString());
    }

    public static async Task Another_tenant_cannot_see_or_change_a_person(AccessWorld w, AccessWorld other)
    {
        var person = await NewPersonAsync(w, "Tess Tenant");
        await Assert.ThrowsAsync<ResourceNotFoundException>(() => AdminAsync(other, "tenantAdmin", s => s.Get<IEffectiveAccess>().ForUserAsync(person)));
        await Assert.ThrowsAnyAsync<Exception>(() => AdminAsync(other, "tenantAdmin", s => s.Get<IAccessProfileService>().ApplyAsync(new AccessChange(person, other.Org1, [AccessProfiles.ReadOnly]))));
    }

    public static async Task A_person_and_their_access_are_added_in_one_transaction_or_not_at_all(AccessWorld w)
    {
        var email = "atomic.person@" + w.TenantId.ToString("N")[..8] + ".example.test";
        // Refused: a site administrator may not give the organisation administrator profile, so the person must not be created either.
        await Assert.ThrowsAnyAsync<Exception>(() => AdminAsync(w, "siteAdmin", s => s.Get<IPersonAccessService>().AddAsync(
            new NewPerson("Atomic Person", email, false), new AccessChange(Guid.Empty, w.Org1, [AccessProfiles.OrganisationAdministrator]))));
        await using (var scope = w.AsScopeless()) Assert.Null(await scope.Get<IUserService>().FindByEmailAsync(email));

        var plan = await AdminAsync(w, "orgAdmin", s => s.Get<IPersonAccessService>().PreviewNewAsync(
            new NewPerson("Atomic Person", email, false), new AccessChange(Guid.Empty, w.Org1, [AccessProfiles.ContractManager], Ownership: OwnershipChoice.Selected, OwnedCategoryIds: [w.CatEnergy])));
        Assert.True(plan.CanApply, string.Join("; ", plan.Problems));
        Assert.Contains(plan.Steps, st => st.Kind == "person");
        await using (var scope = w.AsScopeless()) Assert.Null(await scope.Get<IUserService>().FindByEmailAsync(email));   // a preview creates nobody

        var added = await AdminAsync(w, "orgAdmin", s => s.Get<IPersonAccessService>().AddAsync(
            new NewPerson("Atomic Person", email, false), new AccessChange(Guid.Empty, w.Org1, [AccessProfiles.ContractManager], Ownership: OwnershipChoice.Selected, OwnedCategoryIds: [w.CatEnergy])));
        Assert.False(added.IsTestPerson);
        Assert.Equal(1, added.Access!.GrantsCreated);
        await using var check = w.AsScopeless();
        var user = await check.Get<IUserService>().FindByEmailAsync(email);
        Assert.Equal(UserStatus.Invited, user!.Status);                                           // a real invitation: becomes active at the first verified sign-in
        // The same address again is reported, not duplicated.
        var again = await AdminAsync(w, "orgAdmin", s => s.Get<IPersonAccessService>().PreviewNewAsync(new NewPerson("Atomic Person", email, false), new AccessChange(Guid.Empty, w.Org1, [AccessProfiles.ReadOnly])));
        Assert.False(again.CanApply);
        Assert.Contains(again.Problems, pr => pr.Contains("already has an account", StringComparison.OrdinalIgnoreCase));
    }

    public static async Task A_test_person_signs_in_through_the_demonstration_provider_and_gets_no_access_from_that(AccessWorld w)
    {
        var email = "test.person@" + w.TenantId.ToString("N")[..8] + ".example.test";
        var added = await AdminAsync(w, "orgAdmin", s => s.Get<IPersonAccessService>().AddAsync(new NewPerson("Test Person", email, true), new AccessChange(Guid.Empty, w.Org1, [])));
        Assert.True(added.IsTestPerson);
        await using var scope = w.AsScopeless();
        var user = (await scope.Get<IUserService>().FindByEmailAsync(email))!;
        Assert.Equal(UserStatus.Active, user.Status);
        // They resolve through the demonstration provider like any demo user; the sign-in grants nothing.
        var resolved = await scope.Get<IUserService>().ResolveLoginAsync(new ExternalLogin(TestIdentity.ProviderKey, TestIdentity.Issuer, email, email, true, "Test Person", []));
        Assert.True(resolved.Succeeded);
        Assert.Empty(await scope.Get<IRoleAssignmentService>().ListForUserAsync(user.Id, includeEnded: true));
        // The People list marks them as a test person (everyone who signs in through the demonstration provider is synthetic).
        var rows = await AdminAsync(w, "orgAdmin", s => s.Get<IPersonAccessService>().ListAsync());
        Assert.True(rows.Single(r => r.Id == user.Id).IsTestPerson);
        Assert.Single(await AuditAsync(w, "access.test_person.created"), e => e.EntityId == user.Id.ToString());
        // Someone who may not manage people cannot create one.
        await Assert.ThrowsAnyAsync<Exception>(() => AdminAsync(w, "viewerAll", s => s.Get<IPersonAccessService>().AddAsync(new NewPerson("Nope", "nope@" + w.TenantId.ToString("N")[..8] + ".example.test", true), new AccessChange(Guid.Empty, w.Org1, []))));
    }

    // ---- First administrator and Test-mode access -----------------------------------------------------------------------------------------------------------

    private static async Task<Guid> AdminPersonAsync(AccessWorld w, string name)
    {
        var id = await NewPersonAsync(w, name);
        await using var s = w.AsScopeless();
        await s.Get<IRoleAssignmentService>().GrantTrustedAsync(new GrantRequest(id, RoleCodes.OrganisationAdministrator, null, ScopeSet.ForOrganisations([w.Org1], "Internal"), Reason: "test"), AssignmentSource.Setup, "setup:test");
        return id;
    }

    private static async Task<T> AsPersonAsync<T>(AccessWorld w, Guid id, Func<TenantScope, Task<T>> work) => await w.AsIdAsync(id, s => { s.Get<ReauthenticationContext>().Set(w.Now); return work(s); });

    public static async Task Set_up_choices_become_ordinary_grants_through_the_trusted_path_and_administer_only_grants_nothing(AccessWorld w)
    {
        var chosen = await AdminPersonAsync(w, "Setup Sam");
        await using (var s = w.AsScopeless())
        {
            var result = await s.Get<IAdministratorBootstrap>().ApplyAtSetupAsync(chosen, w.Org1, new BootstrapChoices(ManageContracts: true, OwnAllCategories: true, Review: true, Approve: false, ReadOnly: false), "setup:test");
            Assert.Equal(2, result.GrantsCreated);                                               // Contract Manager (CategoryOwner) and Reviewer (Legal)
            Assert.Equal(2, result.OwnershipsCreated);                                           // Packaging and Energy; the sub-category is covered by its parent
            var held = (await s.Get<IRoleAssignmentService>().ListForUserAsync(chosen)).Select(a => a.RoleCode).OrderBy(x => x).ToList();
            Assert.Equal(new[] { RoleCodes.CategoryOwner, RoleCodes.Legal, RoleCodes.OrganisationAdministrator }.OrderBy(x => x), held);
            Assert.All((await s.Get<IRoleAssignmentService>().ListForUserAsync(chosen)).Where(a => a.RoleCode != RoleCodes.OrganisationAdministrator), a => Assert.Equal(AssignmentSource.Setup, a.Source));
            // Run again: nothing is duplicated.
            var again = await s.Get<IAdministratorBootstrap>().ApplyAtSetupAsync(chosen, w.Org1, new BootstrapChoices(true, true, true, false, false), "setup:test");
            Assert.Equal(0, again.GrantsCreated); Assert.Equal(0, again.OwnershipsCreated);
        }
        var report = await AsPersonAsync(w, chosen, s => s.Get<IEffectiveAccess>().ForMeAsync());
        Assert.True(report.Abilities.Single(a => a.Key == "create-contracts").Can);              // the first thing the first administrator wanted to do
        Assert.True(report.Abilities.Single(a => a.Key == "legal-review").Can);
        Assert.False(report.Abilities.Single(a => a.Key == "approve-contracts").Can);

        var onlyAdmin = await AdminPersonAsync(w, "Admin Only");
        await using var s2 = w.AsScopeless();
        var none = await s2.Get<IAdministratorBootstrap>().ApplyAtSetupAsync(onlyAdmin, w.Org1, BootstrapChoices.None, "setup:test");
        Assert.Equal(0, none.GrantsCreated);
        Assert.Equal([RoleCodes.OrganisationAdministrator], (await s2.Get<IRoleAssignmentService>().ListForUserAsync(onlyAdmin)).Select(a => a.RoleCode));
        Assert.Contains(none.Notes, n => n.Contains("administer only", StringComparison.OrdinalIgnoreCase));
    }

    public static async Task Full_test_mode_access_is_bounded_audited_reversible_and_never_grants_system_authority(AccessWorld w)
    {
        var me = await AdminPersonAsync(w, "Tess Tester");
        var plan = await AsPersonAsync(w, me, s => s.Get<IAdministratorBootstrap>().PlanTestAccessAsync(w.Org1));
        Assert.True(plan.Available);
        Assert.NotEmpty(plan.WillGrant);
        Assert.Contains(plan.NotGranted, n => n.Contains("System Administrator", StringComparison.Ordinal));      // said before applying
        Assert.NotNull(plan.After);
        Assert.True(plan.After!.Abilities.Single(a => a.Key == "create-contracts").Can);
        await using (var s = w.AsScopeless()) Assert.DoesNotContain(await s.Get<IRoleAssignmentService>().ListForUserAsync(me), a => a.RoleCode == RoleCodes.ProcurementAdministrator);   // planning changed nothing

        var result = await AsPersonAsync(w, me, s => s.Get<IAdministratorBootstrap>().GiveFullTestAccessAsync(w.Org1, "Trying the product"));
        Assert.True(result.GrantsCreated >= 3);
        await using (var s = w.AsScopeless())
        {
            var grants = await s.Get<IRoleAssignmentService>().ListForUserAsync(me);
            var roles = grants.Select(a => a.RoleCode).ToHashSet();
            Assert.Contains(RoleCodes.ProcurementAdministrator, roles); Assert.Contains(RoleCodes.CategoryOwner, roles); Assert.Contains(RoleCodes.Legal, roles); Assert.Contains(RoleCodes.Approver, roles);
            Assert.DoesNotContain(RoleCodes.SystemAdministrator, roles); Assert.DoesNotContain(RoleCodes.TenantAdministrator, roles);   // never
            Assert.All(grants.Where(a => a.RoleCode != RoleCodes.OrganisationAdministrator), a => { Assert.Equal(AssignmentSource.TestBootstrap, a.Source); Assert.Equal("Confidential", a.Scope.ClassificationCeiling); });
        }
        var report = await AsPersonAsync(w, me, s => s.Get<IEffectiveAccess>().ForMeAsync());
        Assert.True(report.Abilities.Single(a => a.Key == "create-contracts").Can);
        Assert.True(report.Abilities.Single(a => a.Key == "close-contracts").Can);
        Assert.Contains("TEST-APPROVERS", report.ApproverGroups);
        Assert.Single(await AuditAsync(w, "access.test_bootstrap"), e => e.EntityId == me.ToString());
        Assert.Contains(report.Grants, g => g.Source.Contains("Test-mode access", StringComparison.Ordinal));      // says where it came from

        // Reversible: only what it created.
        var ended = await AsPersonAsync(w, me, s => s.Get<IAdministratorBootstrap>().EndTestAccessAsync("Finished testing"));
        Assert.True(ended >= 3);
        await using var after = w.AsScopeless();
        Assert.Equal([RoleCodes.OrganisationAdministrator], (await after.Get<IRoleAssignmentService>().ListForUserAsync(me)).Select(a => a.RoleCode));
        Assert.DoesNotContain((await after.Get<ICategoryAdministration>().ListAllAsync()).SelectMany(c => c.Owners), o => o.UserId == me && o.EffectiveToUtc is null);
        Assert.Single(await AuditAsync(w, "access.test_bootstrap.ended"));
    }

    public static async Task Full_test_mode_access_needs_an_administrator_a_recent_sign_in_and_a_reason(AccessWorld w)
    {
        var plain = w.Users["viewerAll"];
        await Assert.ThrowsAsync<ForbiddenException>(() => AsPersonAsync(w, plain, s => s.Get<IAdministratorBootstrap>().GiveFullTestAccessAsync(w.Org1, "I would like everything")));
        var admin = await AdminPersonAsync(w, "Rae Recent");
        await Assert.ThrowsAsync<ReauthenticationRequiredException>(() => w.AsIdAsync(admin, s => s.Get<IAdministratorBootstrap>().GiveFullTestAccessAsync(w.Org1, "No fresh sign-in")));
        await Assert.ThrowsAnyAsync<DomainException>(() => AsPersonAsync(w, admin, s => s.Get<IAdministratorBootstrap>().GiveFullTestAccessAsync(w.Org1, " ")));
        // An administrator of one organisation cannot use it for another.
        await Assert.ThrowsAsync<ForbiddenException>(() => AsPersonAsync(w, admin, s => s.Get<IAdministratorBootstrap>().GiveFullTestAccessAsync(w.Org2, "Wrong organisation")));
        await using var scope = w.AsScopeless();
        Assert.Equal([RoleCodes.OrganisationAdministrator], (await scope.Get<IRoleAssignmentService>().ListForUserAsync(admin)).Select(a => a.RoleCode));
    }

    public static async Task Test_mode_access_does_not_exist_in_production(AccessWorld w)
    {
        var admin = await AdminPersonAsync(w, "Prod Admin");
        Assert.False(await AsPersonAsync(w, admin, s => Task.FromResult(s.Get<IAdministratorBootstrap>().TestModeAvailable)));
        var plan = await AsPersonAsync(w, admin, s => s.Get<IAdministratorBootstrap>().PlanTestAccessAsync(w.Org1));
        Assert.False(plan.Available);
        var refused = await Assert.ThrowsAsync<DomainException>(() => AsPersonAsync(w, admin, s => s.Get<IAdministratorBootstrap>().GiveFullTestAccessAsync(w.Org1, "Production attempt")));
        Assert.Equal("test_only", refused.Code);
        await Assert.ThrowsAsync<DomainException>(() => AsPersonAsync(w, admin, s => s.Get<IAdministratorBootstrap>().EndTestAccessAsync("x")));
        await using var scope = w.AsScopeless();
        Assert.Equal([RoleCodes.OrganisationAdministrator], (await scope.Get<IRoleAssignmentService>().ListForUserAsync(admin)).Select(a => a.RoleCode));   // nothing granted
        Assert.Empty(await AuditAsync(w, "access.test_bootstrap"));
        // The delegated route still refuses a person giving themselves access, in Production as anywhere.
        await Assert.ThrowsAsync<DomainException>(() => AsPersonAsync(w, admin, s => s.Get<IAccessProfileService>().ApplyAsync(new AccessChange(admin, w.Org1, [AccessProfiles.ContractManager]))));
    }

    // ---- Identity group mappings (Advanced) ------------------------------------------------------------------------------------------------------------------

    private static async Task RegisterProviderAsync(AccessWorld w)
    {
        await using var s = w.AsScopeless();
        var issues = await s.Get<IProviderRegistry>().SaveAsync(new IdentityProviderSettings("corp-sso", IdentityProviderKind.Oidc, "Company sign-in", "https://idp.example.test", "client", "env:ECP_TEST_NOT_SET", null));
        Assert.DoesNotContain(issues, i => i.Blocking);
    }

    public static async Task Mappings_need_the_right_to_manage_them_a_recent_sign_in_and_a_closed_safe_catalogue(AccessWorld w)
    {
        await RegisterProviderAsync(w);
        var request = new MappingRequest("corp-sso", "Finance-Readers", RoleCodes.Viewer, w.Org1);
        // An organisation administrator can give roles to people but does not hold the right to manage mappings; nor does anyone else but the tenant administrator.
        foreach (var who in new[] { "orgAdmin", "viewerAll", "procAdmin" })
        {
            await Assert.ThrowsAsync<ForbiddenException>(() => AdminAsync(w, who, s => s.Get<IGroupMappingAdministration>().ListAsync()));
            await Assert.ThrowsAsync<ForbiddenException>(() => AdminAsync(w, who, s => s.Get<IGroupMappingAdministration>().AddAsync(request)));
        }
        await Assert.ThrowsAsync<ReauthenticationRequiredException>(() => w.AsAsync("tenantAdmin", s => s.Get<IGroupMappingAdministration>().AddAsync(request)));
        var preview = await AdminAsync(w, "tenantAdmin", s => s.Get<IGroupMappingAdministration>().PreviewAsync(request));
        Assert.True(preview.CanSave, string.Join("; ", preview.Problems));
        Assert.Contains("Finance-Readers", preview.Sentence);                                    // in words, naming the provider and the group
        Assert.Contains("Company sign-in", preview.Sentence);
        var id = await AdminAsync(w, "tenantAdmin", s => s.Get<IGroupMappingAdministration>().AddAsync(request));
        var rows = await AdminAsync(w, "tenantAdmin", s => s.Get<IGroupMappingAdministration>().ListAsync());
        var row = Assert.Single(rows);
        Assert.Equal(id, row.Id);
        Assert.True(row.Active);
        Assert.DoesNotContain(RoleCodes.Viewer, row.Access);                                      // the access is described, not coded
        await AdminAsync(w, "tenantAdmin", async s => { await s.Get<IGroupMappingAdministration>().EndAsync(id, "No longer needed"); return true; });
        Assert.False(Assert.Single(await AdminAsync(w, "tenantAdmin", s => s.Get<IGroupMappingAdministration>().ListAsync())).Active);

        // The closed catalogue: no administrator or approval access through a group, however the call is made.
        foreach (var role in new[] { RoleCodes.OrganisationAdministrator, RoleCodes.BusinessConfigurationAdministrator, RoleCodes.Approver, RoleCodes.BackupApprover, RoleCodes.TenantAdministrator, RoleCodes.SystemAdministrator })
        {
            var refusal = await AdminAsync(w, "tenantAdmin", s => s.Get<IGroupMappingAdministration>().PreviewAsync(request with { RoleCode = role }));
            Assert.False(refusal.CanSave, role);
            await using var scope = w.AsScopeless();
            var low = await Assert.ThrowsAsync<DomainException>(() => scope.Get<IGroupMappingService>().AddAsync("corp-sso", "Admins", role, null, ScopeSet.ForOrganisations([w.Org1], "Internal"), role is RoleCodes.Approver or RoleCodes.BackupApprover ? "G1" : null));
            Assert.Contains(low.Code, new[] { "mapping_role_not_allowed", "mapping_not_allowed" });   // refused at the store too, not only by the screen
        }
        Assert.All(GroupMappingPolicy.SafeRoles, r => Assert.True(Ecp.Setup.Onboarding.OnboardingRoles.MappableByOnboarding.Contains(r)));   // one catalogue, not two
        Assert.Equal(GroupMappingPolicy.SafeRoles.Count, Ecp.Setup.Onboarding.OnboardingRoles.MappableByOnboarding.Count);
    }

    public static async Task A_mapping_is_narrow_by_construction_no_high_ceiling_no_unchosen_places_and_viewer_kind_follows_the_places(AccessWorld w)
    {
        await RegisterProviderAsync(w);
        var good = new MappingRequest("corp-sso", "Packaging-Readers", RoleCodes.Viewer, w.Org1, ScopeChoice.Categories, [w.CatPackaging], "Internal");
        Assert.True((await AdminAsync(w, "tenantAdmin", s => s.Get<IGroupMappingAdministration>().PreviewAsync(good))).CanSave);
        // A high classification level is for named people.
        Assert.False((await AdminAsync(w, "tenantAdmin", s => s.Get<IGroupMappingAdministration>().PreviewAsync(good with { Ceiling = "Restricted" }))).CanSave);
        // Selected places must be chosen; roles that cannot be narrowed are not narrowed.
        Assert.False((await AdminAsync(w, "tenantAdmin", s => s.Get<IGroupMappingAdministration>().PreviewAsync(good with { ScopeIds = [] }))).CanSave);
        Assert.False((await AdminAsync(w, "tenantAdmin", s => s.Get<IGroupMappingAdministration>().PreviewAsync(new MappingRequest("corp-sso", "Reviewers", RoleCodes.Legal, w.Org1, ScopeChoice.Categories, [w.CatPackaging])))).CanSave);
        // An unknown provider and a blank group are refused in words.
        var bad = await AdminAsync(w, "tenantAdmin", s => s.Get<IGroupMappingAdministration>().PreviewAsync(new MappingRequest("nope", " ", RoleCodes.Viewer, w.Org1)));
        Assert.False(bad.CanSave); Assert.True(bad.Problems.Count >= 2);
        // Saved: the viewer kind comes from the places chosen, never from the administrator.
        await AdminAsync(w, "tenantAdmin", s => s.Get<IGroupMappingAdministration>().AddAsync(good));
        await using var scope = w.AsScopeless();
        var stored = Assert.Single(await scope.Get<IGroupMappingService>().ListAsync());
        Assert.Equal(ViewerKind.Category, stored.ViewerKind);
        Assert.Equal([w.CatPackaging], stored.Scope.CategoryIds);
        Assert.Equal("Internal", stored.Scope.ClassificationCeiling);
        Assert.Equal(2, (await AuditAsync(w, "rolemapping.created")).Count + 1);                // exactly one, from the audited store
    }

    /// <summary>Run against an installation whose mode is Production.</summary>
    public static async Task Demonstration_sign_in_and_synthetic_people_are_refused_in_production_at_the_service(AccessWorld w)
    {
        Assert.False(await AdminAsync(w, "orgAdmin", s => Task.FromResult(s.Get<IPersonAccessService>().DemonstrationSignInAllowed)));
        var email = "prod.test@" + w.TenantId.ToString("N")[..8] + ".example.test";
        var refused = await Assert.ThrowsAsync<DomainException>(() => AdminAsync(w, "orgAdmin", s => s.Get<IPersonAccessService>().AddAsync(new NewPerson("Prod Test", email, true), new AccessChange(Guid.Empty, w.Org1, []))));
        Assert.Equal("test_only", refused.Code);
        await using (var scope = w.AsScopeless()) Assert.Null(await scope.Get<IUserService>().FindByEmailAsync(email));          // nothing was created
        // A real invitation still works in Production.
        var invited = await AdminAsync(w, "orgAdmin", s => s.Get<IPersonAccessService>().AddAsync(new NewPerson("Prod Real", "prod.real@" + w.TenantId.ToString("N")[..8] + ".example.test", false), new AccessChange(Guid.Empty, w.Org1, [])));
        Assert.False(invited.IsTestPerson);
        // The People list never marks anyone as a test person in Production.
        var rows = await AdminAsync(w, "orgAdmin", s => s.Get<IPersonAccessService>().ListAsync());
        Assert.DoesNotContain(rows, r => r.IsTestPerson);
    }

    // ---- Preview as this user (Test/Demo only) -------------------------------------------------------------------------------------------------------------

    private static Task<T> Previewer<T>(AccessWorld w, string key, Func<TenantScope, Task<T>> work) => AdminAsync(w, key, work);

    public static async Task Preview_starts_for_a_person_the_administrator_may_manage_and_is_audited(AccessWorld w)
    {
        var target = w.Users["viewerCat"];
        var grant = await Previewer(w, "orgAdmin", s => s.Get<IPreviewService>().StartAsync(target, "Checking what a category viewer sees"));
        Assert.Equal(target, grant.TargetUserId);
        Assert.Equal(w.Users["orgAdmin"], grant.PreviewerUserId);
        Assert.True(grant.ExpiresUtc > w.Now && grant.ExpiresUtc <= w.Now.AddMinutes(31));
        var started = Assert.Single(await AuditAsync(w, "preview.started"));
        Assert.Equal(target.ToString(), started.EntityId);
        Assert.Contains(w.Users["orgAdmin"].ToString(), started.NewValuesJson);                  // previewer, target, tenant and organisations are recorded
        Assert.Contains(w.TenantId.ToString(), started.NewValuesJson);
        Assert.Equal("Checking what a category viewer sees", started.Reason);
        // The previewed person's account, identity and credentials are untouched.
        await using var scope = w.AsScopeless();
        Assert.Equal(UserStatus.Active, (await scope.Get<IUserService>().GetAsync(target))!.Status);
        Assert.Null((await scope.Get<IUserService>().GetAsync(target))!.LastSignInUtc);
    }

    public static async Task Preview_needs_a_reason_a_recent_sign_in_and_the_right_to_manage_the_person(AccessWorld w)
    {
        var target = w.Users["viewerCat"];
        await Assert.ThrowsAsync<ReauthenticationRequiredException>(() => w.AsAsync("orgAdmin", s => s.Get<IPreviewService>().StartAsync(target, "No fresh sign-in")));
        await Assert.ThrowsAnyAsync<DomainException>(() => Previewer(w, "orgAdmin", s => s.Get<IPreviewService>().StartAsync(target, " ")));
        // A person who cannot manage people cannot preview anyone, and learns nothing about whether the person exists.
        await Assert.ThrowsAnyAsync<Exception>(() => Previewer(w, "viewerAll", s => s.Get<IPreviewService>().StartAsync(target, "Curious")));
        Assert.Empty(await AuditAsync(w, "preview.started"));
    }

    public static async Task You_may_preview_only_what_you_could_give_and_never_yourself_a_disabled_person_or_the_installation(AccessWorld w)
    {
        // A tenant-wide person is not even visible to an organisation administrator: it is not found, so nothing about them is revealed.
        await Assert.ThrowsAsync<ResourceNotFoundException>(() => Previewer(w, "orgAdmin", s => s.Get<IPreviewService>().StartAsync(w.Users["auditor"], "Looking")));
        // The legal reviewer holds a Restricted ceiling. An organisation administrator may not give a ceiling that high, so may not preview it.
        var refused = await Assert.ThrowsAsync<DomainException>(() => Previewer(w, "orgAdmin", s => s.Get<IPreviewService>().StartAsync(w.Users["legal"], "Looking")));
        Assert.Equal("preview_refused", refused.Code);
        Assert.Contains("not allowed to give", refused.RawMessage);
        foreach (var code in RoleCodes.All.Where(c => c != RoleCodes.Legal)) Assert.DoesNotContain(code, refused.RawMessage);   // words, not role codes (the person is named "Legal" in this world)
        var eligibility = await Previewer(w, "orgAdmin", s => s.Get<IPreviewService>().CanPreviewAsync(w.Users["legal"]));
        Assert.False(eligibility.Allowed);
        // A tenant administrator may preview both.
        Assert.NotNull(await Previewer(w, "tenantAdmin", s => s.Get<IPreviewService>().StartAsync(w.Users["legal"], "Tenant administrator checks the reviewer")));
        Assert.NotNull(await Previewer(w, "tenantAdmin", s => s.Get<IPreviewService>().StartAsync(w.Users["auditor"], "Tenant administrator checks the auditor")));
        // Not yourself.
        Assert.Equal("preview_refused", (await Assert.ThrowsAsync<DomainException>(() => Previewer(w, "orgAdmin", s => s.Get<IPreviewService>().StartAsync(w.Users["orgAdmin"], "Me")))).Code);
        // Not a disabled person.
        await using (var scope = w.AsScopeless()) await scope.Get<IUserService>().DisableAsync(w.Users["viewerBu"], "Left");
        Assert.Equal("preview_refused", (await Assert.ThrowsAsync<DomainException>(() => Previewer(w, "orgAdmin", s => s.Get<IPreviewService>().StartAsync(w.Users["viewerBu"], "Gone")))).Code);
    }

    public static async Task A_preview_runs_with_the_previewed_persons_real_access_not_the_administrators(AccessWorld w)
    {
        var target = w.Users["viewerCat"];
        var expected = await w.VisibleContractKeysForIdAsync(target);                            // what the person really sees
        var adminSees = await w.VisibleContractKeysForIdAsync(w.Users["orgAdmin"]);
        var grant = await Previewer(w, "orgAdmin", s => s.Get<IPreviewService>().StartAsync(target, "Compare"));
        // The host does exactly this after re-validating the session: the current person becomes the target and the request is marked as a preview.
        await using var scope = w.AsScopeless();
        scope.Get<CurrentUser>().Set(target);
        scope.Get<PreviewContext>().Activate(grant.PreviewId, grant.PreviewerUserId, target, grant.TargetName, grant.ExpiresUtc);
        var page = await scope.Get<ISecuredContractService>().ListAsync(new ContractFilter(PageSize: 200));
        var seen = w.Contracts.Where(kv => page.Items.Any(i => i.Id == kv.Value)).Select(kv => kv.Key).ToHashSet();
        Assert.Equal(expected, seen);                                                            // the target's decisions
        Assert.NotEmpty(expected);
        Assert.Empty(await scope.Get<IAccessService>().GrantsAsync("users.manage"));          // the administrator's authority is not inherited
        var mine = await scope.Get<IEffectiveAccess>().ForMeAsync();
        Assert.Equal(target, mine.UserId);
        Assert.False(mine.Abilities.Single(a => a.Key == "manage-people").Can);
        _ = adminSees;
    }

    public static async Task A_preview_cannot_write_anything_but_its_own_record_and_the_audit_trail(AccessWorld w)
    {
        var target = w.Users["viewerCat"];
        var grant = await Previewer(w, "orgAdmin", s => s.Get<IPreviewService>().StartAsync(target, "Read only check"));
        await using var scope = w.AsScopeless();
        scope.Get<CurrentUser>().Set(target);
        scope.Get<PreviewContext>().Activate(grant.PreviewId, grant.PreviewerUserId, target, grant.TargetName, grant.ExpiresUtc);
        var before = (await scope.Get<IRoleAssignmentService>().ListForUserAsync(target, includeEnded: true)).Count;
        // Anything that would change business or identity data is refused by the database layer, whatever the page did.
        await Assert.ThrowsAsync<PreviewReadOnlyException>(() => scope.Get<IRoleAssignmentService>().GrantTrustedAsync(
            new GrantRequest(target, RoleCodes.Legal, null, ScopeSet.ForOrganisations([w.Org1], "Internal"), ExplicitHighCeiling: true), AssignmentSource.Manual, "attempt"));
        await Assert.ThrowsAsync<PreviewReadOnlyException>(() => scope.Get<IUserService>().DisableAsync(target, "attempt"));
        Assert.Equal(before, (await scope.Get<IRoleAssignmentService>().ListForUserAsync(target, includeEnded: true)).Count);
        // The preview's own bookkeeping still works: a refused write is counted and audited, and ending records the end.
        var preview = scope.Get<IPreviewService>();
        await preview.RecordRefusedWriteAsync(grant.PreviewId, "POST", "/Contracts/Create");
        await preview.EndAsync(grant.PreviewId, "Exited by the administrator");
        Assert.Single(await AuditAsync(w, "preview.write_refused"));
        var ended = Assert.Single(await AuditAsync(w, "preview.ended"));
        Assert.Contains("\"refusedWrites\":1", ended.NewValuesJson);
        Assert.Null(await preview.ResolveAsync(grant.PreviewId, grant.PreviewerUserId));        // once ended it is over everywhere
    }

    public static async Task A_preview_ends_when_it_expires_or_the_previewer_loses_the_right_or_the_person_is_disabled(AccessWorld w)
    {
        var target = w.Users["viewerCat"];
        var grant = await Previewer(w, "orgAdmin", s => s.Get<IPreviewService>().StartAsync(target, "Expiry"));
        await using (var scope = w.AsScopeless())
        {
            var preview = scope.Get<IPreviewService>();
            Assert.NotNull(await preview.ResolveAsync(grant.PreviewId, grant.PreviewerUserId));
            Assert.Null(await preview.ResolveAsync(grant.PreviewId, w.Users["tenantAdmin"]));   // only the previewer's session may carry it
            Assert.Null(await preview.ResolveAsync(Guid.NewGuid(), grant.PreviewerUserId));
        }
        // Disabling the person ends it.
        await using (var scope = w.AsScopeless()) await scope.Get<IUserService>().DisableAsync(target, "Left");
        await using (var scope = w.AsScopeless()) Assert.Null(await scope.Get<IPreviewService>().ResolveAsync(grant.PreviewId, grant.PreviewerUserId));
        // A fresh preview expires after its lifetime.
        var second = await Previewer(w, "orgAdmin", s => s.Get<IPreviewService>().StartAsync(w.Users["viewerSite"], "Expiry two"));
        await using (var scope = w.AsScopeless()) Assert.NotNull(await scope.Get<IPreviewService>().ResolveAsync(second.PreviewId, second.PreviewerUserId));
        w.Advance(TimeSpan.FromMinutes(31));
        await using (var scope = w.AsScopeless()) Assert.Null(await scope.Get<IPreviewService>().ResolveAsync(second.PreviewId, second.PreviewerUserId));
    }

    public static async Task A_preview_cannot_cross_tenants(AccessWorld w, AccessWorld other)
    {
        // Another tenant's person does not exist for this administrator.
        await Assert.ThrowsAsync<ResourceNotFoundException>(() => Previewer(w, "tenantAdmin", s => s.Get<IPreviewService>().StartAsync(other.Users["viewerCat"], "Across")));
        // A preview row from one tenant means nothing in another.
        var grant = await Previewer(w, "orgAdmin", s => s.Get<IPreviewService>().StartAsync(w.Users["viewerCat"], "Own tenant"));
        await using var scope = other.AsScopeless();
        Assert.Null(await scope.Get<IPreviewService>().ResolveAsync(grant.PreviewId, grant.PreviewerUserId));
    }

    public static async Task An_invited_person_is_previewed_as_they_will_be_once_active_without_changing_the_account(AccessWorld w)
    {
        var email = "invited.preview@" + w.TenantId.ToString("N")[..8] + ".example.test";
        var added = await AdminAsync(w, "orgAdmin", s => s.Get<IPersonAccessService>().AddAsync(new NewPerson("Invited Preview", email, false),
            new AccessChange(Guid.Empty, w.Org1, [AccessProfiles.ContractManager], Ownership: OwnershipChoice.Selected, OwnedCategoryIds: [w.CatEnergy])));
        var grant = await Previewer(w, "orgAdmin", s => s.Get<IPreviewService>().StartAsync(added.UserId, "Check before they sign in"));
        await using var scope = w.AsScopeless();
        scope.Get<CurrentUser>().Set(added.UserId);
        Assert.NotEqual(UserStatus.Active, (await scope.Get<IAccessService>().GetSubjectAsync())!.Status);      // not previewing: an invited person has no access
        await using var previewing = w.AsScopeless();
        previewing.Get<CurrentUser>().Set(added.UserId);
        previewing.Get<PreviewContext>().Activate(grant.PreviewId, grant.PreviewerUserId, added.UserId, grant.TargetName, grant.ExpiresUtc);
        var subject = await previewing.Get<IAccessService>().GetSubjectAsync();
        Assert.Equal(UserStatus.Active, subject!.Status);                                         // evaluated as they will be
        await using var check = w.AsScopeless();
        Assert.Equal(UserStatus.Invited, (await check.Get<IUserService>().FindByEmailAsync(email))!.Status);   // the account itself is unchanged
    }

    public static async Task Preview_does_not_exist_in_production(AccessWorld w)
    {
        Assert.False(await AdminAsync(w, "orgAdmin", s => Task.FromResult(s.Get<IPreviewService>().Available)));
        var refused = await Assert.ThrowsAsync<DomainException>(() => AdminAsync(w, "orgAdmin", s => s.Get<IPreviewService>().StartAsync(w.Users["viewerCat"], "Production attempt")));
        Assert.Equal("test_only", refused.Code);
        Assert.Empty(await AuditAsync(w, "preview.started"));
        // Even a session that somehow names a preview is ignored in Production.
        await using var scope = w.AsScopeless();
        Assert.Null(await scope.Get<IPreviewService>().ResolveAsync(Guid.NewGuid(), w.Users["orgAdmin"]));
    }

    /// <summary>For every person in the world, each ability the report says they have is one the evaluator grants for at least one of its permissions, and the reverse.</summary>
    public static async Task Effective_access_agrees_with_the_evaluator_for_every_person_and_permission(AccessWorld w)
    {
        foreach (var (key, id) in w.Users)
        {
            var report = await AdminAsync(w, "tenantAdmin", s => s.Get<IEffectiveAccess>().ForUserAsync(id));
            await using var scope = w.AsScopeless();
            var subject = await scope.Get<IAccessDirectory>().LoadSubjectAsync(id);
            var scale = await scope.Get<IAccessService>().ClassificationScaleAsync();
            var evaluator = AccessEvaluator.Default;
            foreach (var ability in report.Abilities)
            {
                var expected = AbilityCatalogue.PermissionsOf(ability.Key).Any(p => subject is not null && evaluator.GrantsFor(subject, scale, w.Now, p).Any(g => g.Relationship != GrantRelationship.ActiveCategoryOwnership));
                var anyGrant = AbilityCatalogue.PermissionsOf(ability.Key).Any(p => subject is not null && evaluator.GrantsFor(subject, scale, w.Now, p).Count > 0);
                if (expected) Assert.True(ability.Can, $"{key}: {ability.Key} should be available (the evaluator grants it).");
                if (!anyGrant) Assert.False(ability.Can, $"{key}: {ability.Key} must not be available (the evaluator grants nothing).");
            }
        }
    }

    public static async Task A_person_can_read_their_own_effective_access_and_nobody_elses(AccessWorld w)
    {
        var mine = await w.AsAsync("viewerAll", s => s.Get<IEffectiveAccess>().ForMeAsync());
        Assert.Equal(w.Users["viewerAll"], mine.UserId);
        Assert.True(mine.Abilities.Single(a => a.Key == "view-contracts").Can);
        Assert.False(mine.Abilities.Single(a => a.Key == "create-contracts").Can);
        Assert.NotEmpty(mine.Abilities.Single(a => a.Key == "create-contracts").GrantedBy);        // says which profile would give it
        await Assert.ThrowsAnyAsync<Exception>(() => w.AsAsync("viewerAll", s => s.Get<IEffectiveAccess>().ForUserAsync(w.Users["tenantAdmin"])));
    }
}

public static class AccessProfileCatalogueTests_Shared
{
    public static void Every_permission_has_an_ability_and_every_ability_has_a_permission()
    {
        var matrix = PermissionMatrix.Instance;
        var missing = matrix.Permissions.Where(p => !AbilityCatalogue.AbilityOfPermission.ContainsKey(p)).ToList();
        Assert.True(missing.Count == 0, "Permissions with no ability: " + string.Join(", ", missing));
        var unknown = AbilityCatalogue.AbilityOfPermission.Where(kv => AbilityCatalogue.Find(kv.Value) is null).Select(kv => kv.Key).ToList();
        Assert.True(unknown.Count == 0, "Permissions mapped to an ability that does not exist: " + string.Join(", ", unknown));
        var stale = AbilityCatalogue.AbilityOfPermission.Keys.Where(p => !matrix.Permissions.Contains(p)).ToList();
        Assert.True(stale.Count == 0, "Abilities map permissions that no longer exist: " + string.Join(", ", stale));
        var empty = AbilityCatalogue.All.Where(a => !AbilityCatalogue.PermissionsOf(a.Key).Any()).Select(a => a.Key).ToList();
        Assert.True(empty.Count == 0, "Abilities with no permission: " + string.Join(", ", empty));
    }

    public static void Profiles_use_only_existing_non_privileged_roles_and_are_unique()
    {
        Assert.Equal(AccessProfiles.All.Count, AccessProfiles.All.Select(p => p.Key).Distinct().Count());
        foreach (var profile in AccessProfiles.All)
            foreach (var role in profile.RoleCodes)
            {
                Assert.Contains(role, RoleCodes.All);
                Assert.NotEqual(RoleCodes.SystemAdministrator, role);                             // a profile can never carry installation or tenant authority
                Assert.NotEqual(RoleCodes.TenantAdministrator, role);
                Assert.NotEmpty(PermissionMatrix.Instance.PermissionsOf(role));
            }
    }
}
