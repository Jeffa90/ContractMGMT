using Ecp.Audit;
using Ecp.Identity;
using Ecp.Identity.Domain;
using Ecp.SharedKernel;
using Ecp.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecp.Integration.Tests;

/// <summary>
/// G-04: approver groups are registered and managed in forms. A key people already hold shows as unregistered rather than hidden; a person is added with dates and a preview of who can approve before and after; the
/// last person cannot be taken out of a group that a workflow, a matrix or a waiting approval relies on; a group in use cannot be retired; everything is audited and stays inside the organisation and the tenant.
/// </summary>
internal static class ApproverGroupScenarios
{
    private static string Unique(string p) => p + Guid.NewGuid().ToString("N")[..6];

    private static async Task<(LifecycleWorld W, string Admin, string OrgAdmin)> StartAsync(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        var orgAdmin = await TerminologyScenarios.AdminForAsync(w, w.W.Org1, "groupOrgAdmin", RoleCodes.OrganisationAdministrator);
        return (w, "tenantAdmin", orgAdmin);
    }

    private static async Task<List<Ecp.Audit.Domain.AuditEvent>> AuditAsync(LifecycleWorld w, string action)
    {
        await using var scope = w.W.AsScopeless();
        return await scope.Get<IAuditEventSource>().Events.Where(e => e.Action == action).OrderBy(e => e.Sequence).ToListAsync();
    }

    public static async Task A_key_people_already_hold_is_listed_as_unregistered_then_registered_and_a_group_is_created_updated_and_audited(TestApp app)
    {
        var (w, admin, _) = await StartAsync(app);
        var list = await w.W.AsAsync(admin, s => s.Get<IApproverGroupService>().ListAsync());
        var held = Assert.Single(list, g => g.Key == LifecycleWorld.ApproverGroup);
        Assert.False(held.Registered);
        Assert.Equal(1, held.Approvers); Assert.Equal(1, held.Backups);
        Assert.True(held.InUse > 0);                                                        // the reference workflow names it as its approver group

        var registered = await w.W.AsAsync(admin, s => s.Get<IApproverGroupService>().CreateAsync(LifecycleWorld.ApproverGroup, "Group Head", null, true));
        Assert.True(registered.Registered); Assert.Equal(1, registered.Approvers);
        await Assert.ThrowsAsync<DomainException>(() => w.W.AsAsync(admin, s => s.Get<IApproverGroupService>().CreateAsync(LifecycleWorld.ApproverGroup, "again", null, true)));   // duplicate key
        foreach (var bad in new[] { "", " ", "bad key", "-leading", "has/slash", new string('a', 101) })
            await Assert.ThrowsAsync<DomainException>(() => w.W.AsAsync(admin, s => s.Get<IApproverGroupService>().CreateAsync(bad, null, null, true)));

        var key = Unique("Dept");
        var created = await w.W.AsAsync(admin, s => s.Get<IApproverGroupService>().CreateAsync(key, "Department heads", w.W.Org1, true));
        Assert.Equal(w.W.Org1, created.OrganisationId); Assert.Equal(0, created.Approvers);
        await w.W.AsAsync(admin, s => s.Get<IApproverGroupService>().UpdateAsync(key, "Heads of department", false));
        var detail = await w.W.AsAsync(admin, s => s.Get<IApproverGroupService>().GetAsync(key));
        Assert.Equal("Heads of department", detail.Summary.Description); Assert.False(detail.Summary.AllowDelegation);
        Assert.Equal(2, (await AuditAsync(w, "approver_group.created")).Count);
        Assert.Single(await AuditAsync(w, "approver_group.updated"));
    }

    public static async Task People_are_added_with_dates_after_a_preview_and_ended_with_a_reason_and_the_group_stays_consistent(TestApp app)
    {
        var (w, admin, _) = await StartAsync(app);
        var key = Unique("Panel");
        await w.W.AsAsync(admin, s => s.Get<IApproverGroupService>().CreateAsync(key, "Panel", w.W.Org1, true));
        var anna = await w.UserAsync("annaPanel", RoleCodes.CategoryOwner, null, "Internal");     // an ordinary person with no approval role yet
        var ben = w.W.Users["annaPanel"];
        await using (var sc = w.W.AsScopeless()) { _ = ben; }
        var other = await w.UserAsync("benPanel", RoleCodes.CategoryOwner, null, "Internal");

        // Preview: nobody now, Anna afterwards.
        var effect = await w.W.AsAsync(admin, s => s.Get<IApproverGroupService>().PreviewAsync(key, new MembershipChange(true, anna, false, null)));
        Assert.Empty(effect.ApproversBefore); Assert.Single(effect.ApproversAfter); Assert.Null(effect.Refusal);

        var id = await w.W.AsAsync(admin, s => s.Get<IApproverGroupService>().AddMemberAsync(key, anna, false, w.W.Org1, "Confidential", null, null, "Panel chair"));
        var detail = await w.W.AsAsync(admin, s => s.Get<IApproverGroupService>().GetAsync(key));
        var member = Assert.Single(detail.Members);
        Assert.Equal(id, member.AssignmentId); Assert.True(member.IsActive); Assert.False(member.IsBackup);
        Assert.Contains(anna, (await w.W.AsAsync(admin, s => s.Get<IAccessDirectory>().ApproversAsync(key))).Select(a => a.UserId));          // the resolver sees exactly this

        // Twice is refused; a backup with a start date in the future is not an approver yet.
        await Assert.ThrowsAsync<DomainException>(() => w.W.AsAsync(admin, s => s.Get<IApproverGroupService>().AddMemberAsync(key, anna, false, w.W.Org1, "Confidential", null, null, "again")));
        var future = w.W.Now.AddDays(10);
        await w.W.AsAsync(admin, s => s.Get<IApproverGroupService>().AddMemberAsync(key, other, true, w.W.Org1, "Confidential", future, future.AddDays(30), "Holiday cover"));
        Assert.DoesNotContain(other, (await w.W.AsAsync(admin, s => s.Get<IAccessDirectory>().ApproversAsync(key))).Select(a => a.UserId));
        Assert.Contains(other, (await w.W.AsAsync(admin, s => s.Get<IAccessDirectory>().ApproversAsync(key, future.AddDays(1)))).Select(a => a.UserId));

        // Another organisation is refused for a group that belongs to this one.
        var elsewhere = await PackResolutionScenarios.AddOrganisationAsync(w, "Elsewhere", "ELW", TestPacks.Reference);
        await Assert.ThrowsAsync<DomainException>(() => w.W.AsAsync(admin, s => s.Get<IApproverGroupService>().AddMemberAsync(key, anna, false, elsewhere.Org, "Confidential", null, null, "wrong org")));

        // Nothing relies on this group, so the only approver can be taken out; the reason is kept.
        await w.W.AsAsync(admin, s => s.Get<IApproverGroupService>().EndMemberAsync(key, id, "Left the panel"));
        Assert.DoesNotContain(anna, (await w.W.AsAsync(admin, s => s.Get<IAccessDirectory>().ApproversAsync(key))).Select(a => a.UserId));
        Assert.Single(await AuditAsync(w, "approver_group.member_added"), e => e.Reason == "Panel chair");
        Assert.Single(await AuditAsync(w, "approver_group.member_ended"));
    }

    public static async Task A_group_that_work_relies_on_cannot_be_emptied_or_retired_and_the_preview_says_why(TestApp app)
    {
        var (w, admin, _) = await StartAsync(app);
        await w.W.AsAsync(admin, s => s.Get<IApproverGroupService>().CreateAsync(LifecycleWorld.ApproverGroup, "Group Head", w.W.Org1, true));
        var detail = await w.W.AsAsync(admin, s => s.Get<IApproverGroupService>().GetAsync(LifecycleWorld.ApproverGroup));
        Assert.Contains(detail.Usage, u => u.Kind == "Workflow");
        var primary = detail.Members.Single(m => !m.IsBackup);
        var backup = detail.Members.Single(m => m.IsBackup);

        // Ending one of the two is fine; ending the last is refused, in the preview and in the service.
        await w.W.AsAsync(admin, s => s.Get<IApproverGroupService>().EndMemberAsync(LifecycleWorld.ApproverGroup, backup.AssignmentId, "Backup left"));
        var effect = await w.W.AsAsync(admin, s => s.Get<IApproverGroupService>().PreviewAsync(LifecycleWorld.ApproverGroup, new MembershipChange(false, null, false, primary.AssignmentId)));
        Assert.True(effect.LeavesGroupEmpty);
        Assert.NotNull(effect.Refusal);
        Assert.Contains("nobody to approve", effect.Refusal);
        var refused = await Assert.ThrowsAsync<DomainException>(() => w.W.AsAsync(admin, s => s.Get<IApproverGroupService>().EndMemberAsync(LifecycleWorld.ApproverGroup, primary.AssignmentId, "Leaving")));
        Assert.Equal("group_in_use", refused.Code);
        Assert.Contains(LifecycleWorld.ApproverGroup, (await w.W.AsAsync(admin, s => s.Get<IAccessDirectory>().ApproversAsync(LifecycleWorld.ApproverGroup))).Select(_ => LifecycleWorld.ApproverGroup));

        // Adding a replacement first makes the change possible.
        var replacement = await w.UserAsync("replacementHead", RoleCodes.CategoryOwner, null, "Internal");
        await w.W.AsAsync(admin, s => s.Get<IApproverGroupService>().AddMemberAsync(LifecycleWorld.ApproverGroup, replacement, false, w.W.Org1, "Confidential", null, null, "Succession"));
        await w.W.AsAsync(admin, s => s.Get<IApproverGroupService>().EndMemberAsync(LifecycleWorld.ApproverGroup, primary.AssignmentId, "Moved on"));

        // In use: not retirable. Unused and empty: retirable, then closed to new members until reactivated.
        Assert.Equal("group_in_use", (await Assert.ThrowsAsync<DomainException>(() => w.W.AsAsync(admin, s => s.Get<IApproverGroupService>().RetireAsync(LifecycleWorld.ApproverGroup, "tidy")))).Code);
        var spare = Unique("Spare");
        await w.W.AsAsync(admin, s => s.Get<IApproverGroupService>().CreateAsync(spare, null, w.W.Org1, true));
        await w.W.AsAsync(admin, s => s.Get<IApproverGroupService>().RetireAsync(spare, "Not needed"));
        Assert.False((await w.W.AsAsync(admin, s => s.Get<IApproverGroupService>().GetAsync(spare))).Summary.IsActive);
        await Assert.ThrowsAsync<DomainException>(() => w.W.AsAsync(admin, s => s.Get<IApproverGroupService>().AddMemberAsync(spare, replacement, false, w.W.Org1, "Confidential", null, null, "x")));
        await w.W.AsAsync(admin, s => s.Get<IApproverGroupService>().ReactivateAsync(spare));
        Assert.True((await w.W.AsAsync(admin, s => s.Get<IApproverGroupService>().GetAsync(spare))).Summary.IsActive);
    }

    public static async Task Groups_are_confined_to_the_organisation_and_the_tenant_and_need_permission_to_manage_people(TestApp app)
    {
        var (w, admin, orgAdmin) = await StartAsync(app);
        var elsewhere = await PackResolutionScenarios.AddOrganisationAsync(w, "Other Org", "OTH", TestPacks.Reference);
        var theirs = Unique("Theirs");
        await w.W.AsAsync(admin, s => s.Get<IApproverGroupService>().CreateAsync(theirs, "Other organisation", elsewhere.Org, true));
        var shared = Unique("Shared");
        await w.W.AsAsync(admin, s => s.Get<IApproverGroupService>().CreateAsync(shared, "Whole tenant", null, true));

        // The organisation's administrator sees their own group and cannot reach another organisation's group or change a tenant-wide one.
        var visible = (await w.W.AsAsync(orgAdmin, s => s.Get<IApproverGroupService>().ListAsync())).Select(g => g.Key).ToList();
        Assert.DoesNotContain(theirs, visible);
        await Assert.ThrowsAsync<ResourceNotFoundException>(() => w.W.AsAsync(orgAdmin, s => s.Get<IApproverGroupService>().GetAsync(theirs)));
        await Assert.ThrowsAsync<ForbiddenException>(() => w.W.AsAsync(orgAdmin, s => s.Get<IApproverGroupService>().UpdateAsync(shared, "changed", true)));
        await Assert.ThrowsAsync<ForbiddenException>(() => w.W.AsAsync(orgAdmin, s => s.Get<IApproverGroupService>().CreateAsync(Unique("Mine"), null, elsewhere.Org, true)));
        await w.W.AsAsync(orgAdmin, s => s.Get<IApproverGroupService>().CreateAsync(Unique("Mine"), null, w.W.Org1, true));

        // People with no right to manage people see nothing.
        await Assert.ThrowsAsync<ForbiddenException>(() => w.W.AsAsync("legalA", s => s.Get<IApproverGroupService>().ListAsync()));

        // Another tenant sees none of this.
        var other = await StartAsync(app);
        Assert.DoesNotContain(theirs, (await other.W.W.AsAsync(other.Admin, s => s.Get<IApproverGroupService>().ListAsync())).Select(g => g.Key));
    }
}

[Trait("Category", "ApproverGroups")]
public sealed class ApproverGroupScenarioTests_Sqlite
{
    [Fact] public Task Registry() => ProviderRunner.SqliteAsync(ApproverGroupScenarios.A_key_people_already_hold_is_listed_as_unregistered_then_registered_and_a_group_is_created_updated_and_audited);
    [Fact] public Task Members() => ProviderRunner.SqliteAsync(ApproverGroupScenarios.People_are_added_with_dates_after_a_preview_and_ended_with_a_reason_and_the_group_stays_consistent);
    [Fact] public Task Protections() => ProviderRunner.SqliteAsync(ApproverGroupScenarios.A_group_that_work_relies_on_cannot_be_emptied_or_retired_and_the_preview_says_why);
    [Fact] public Task Isolation() => ProviderRunner.SqliteAsync(ApproverGroupScenarios.Groups_are_confined_to_the_organisation_and_the_tenant_and_need_permission_to_manage_people);
}

[Trait("Category", "SqlServer")]
public sealed class ApproverGroupScenarioTests_SqlServer
{
    [SqlServerFact] public Task Registry() => ProviderRunner.SqlServerAsync(ApproverGroupScenarios.A_key_people_already_hold_is_listed_as_unregistered_then_registered_and_a_group_is_created_updated_and_audited);
    [SqlServerFact] public Task Members() => ProviderRunner.SqlServerAsync(ApproverGroupScenarios.People_are_added_with_dates_after_a_preview_and_ended_with_a_reason_and_the_group_stays_consistent);
    [SqlServerFact] public Task Protections() => ProviderRunner.SqlServerAsync(ApproverGroupScenarios.A_group_that_work_relies_on_cannot_be_emptied_or_retired_and_the_preview_says_why);
    [SqlServerFact] public Task Isolation() => ProviderRunner.SqlServerAsync(ApproverGroupScenarios.Groups_are_confined_to_the_organisation_and_the_tenant_and_need_permission_to_manage_people);
}
