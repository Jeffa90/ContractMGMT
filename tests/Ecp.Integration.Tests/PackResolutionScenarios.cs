using Ecp.Configuration;
using Ecp.Contracts;
using Ecp.Identity;
using Ecp.Identity.Access;
using Ecp.Identity.Domain;
using Ecp.Lifecycle;
using Ecp.Lifecycle.Definitions;
using Ecp.Notifications;
using Ecp.Notifications.Policy;
using Ecp.SharedKernel;
using Ecp.Tenancy;
using Ecp.Tenancy.Domain;
using Ecp.Tests.Common;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecp.Integration.Tests;

/// <summary>
/// S1 of the portability remediation (docs/PORTABILITY_REMEDIATION_PLAN.md): an organisation works from <b>its own</b> solution pack, never from a pack chosen in code. Two organisations of one tenant, one on
/// the JBS reference pack and one on the Blank pack, each get their own lifecycle, Variation workflow and notification policy. This is the first step of PRT-002 (a second business runs with no source change):
/// the Blank organisation's contract moves through its own states and not one of the 17 JBS states appears. The full second business (its own fields, approvals and rules) is slice S9.
/// </summary>
internal static class PackResolutionScenarios
{
    internal sealed record PackOrg(Guid Org, Guid Category, Guid Supplier, string User);

    internal static async Task<PackOrg> AddOrganisationAsync(LifecycleWorld w, string name, string prefix, string? pack)
    {
        await using var s = w.W.AsScopeless();
        var org = await s.Get<ITenantProvisioningService>().AddOrganisationAsync(new AddOrganisationRequest(name, "Australia/Sydney", "en-AU", "AUD", prefix, pack));
        var category = await s.Get<ICategoryAdministration>().UpsertCategoryAsync(org, "GEN", "General", null);
        var supplier = await s.Get<ISupplierService>().CreateAsync(new CreateSupplierRequest(org, name + " Supplier Pty Ltd"));
        var user = "admin" + prefix;
        var id = await s.Get<IUserService>().CreateActiveTrustedAsync(user, user + "@" + w.W.TenantId.ToString("N")[..8] + ".example.test", "demo", "https://demo.example.test", user + "|" + w.W.TenantId, "test");
        await s.Get<IRoleAssignmentService>().GrantTrustedAsync(new GrantRequest(id, RoleCodes.CategoryOwner, null, ScopeSet.ForOrganisations([org], "Confidential"), null,
            EffectiveFromUtc: w.W.Now.AddDays(-5), ExplicitHighCeiling: true), AssignmentSource.Demo, "test");
        await s.Get<ICategoryAdministration>().AssignOwnerTrustedAsync(category, id, true, w.W.Now.AddDays(-30), null, "test", null);
        w.W.Users[user] = id;
        return new PackOrg(org, category, supplier, user);
    }

    private static Task<Guid> NewContractAsync(LifecycleWorld w, PackOrg o, string reference)
        => w.W.AsAsync(o.User, s => s.Get<ISecuredContractService>().CreateDraftAsync(new CreateContractRequest(o.Org, reference, "Pack proof " + reference, o.Supplier, o.Category,
            CurrencyCode: "AUD", EstimatedValue: 1000m, EndDate: DateOnly.FromDateTime(w.W.Now.AddDays(300)))));

    public static async Task Each_organisation_works_from_its_own_pack_and_a_blank_organisation_never_sees_a_JBS_state(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        var jbs = await AddOrganisationAsync(w, "Reference Org", "PRJ", TestPacks.Reference);
        var blank = await AddOrganisationAsync(w, "Blank Org", "PRB", TestPacks.Blank);
        var jbsStates = PackFilesForTests.JbsStateCodes();

        var jContract = await NewContractAsync(w, jbs, "PRJ-000001");
        var bContract = await NewContractAsync(w, blank, "PRB-000001");

        // Each starts in its own pack's initial state, pinned to its own definition.
        var jState = await w.W.AsAsync(jbs.User, s => s.Get<ILifecycleService>().StateAsync(jContract));
        var bState = await w.W.AsAsync(blank.User, s => s.Get<ILifecycleService>().StateAsync(bContract));
        Assert.Equal("Draft", jState.Status);
        Assert.Equal("Open", bState.Status);
        Assert.Contains(jState.Path, p => p.Code == "AwaitingLegal");
        Assert.All(bState.Path, p => Assert.DoesNotContain(p.Code, jbsStates));

        // The Blank organisation's contract moves through its own states: Open to Active, locked as every active agreement is.
        await w.W.AsAsync(blank.User, s => s.Get<ILifecycleService>().TransitionAsync(new TransitionRequest(bContract, "Activate")));
        var active = await w.W.AsAsync(blank.User, s => s.Get<ILifecycleService>().StateAsync(bContract));
        Assert.Equal("Active", active.Status);
        Assert.True(active.Locked);
        var history = await w.W.AsAsync(blank.User, s => s.Get<ILifecycleService>().HistoryAsync(bContract));
        Assert.NotEmpty(history);
        Assert.All(history, h => { Assert.DoesNotContain(h.FromStatus, jbsStates); Assert.DoesNotContain(h.ToStatus, jbsStates); });

        // The definitions installed for each organisation are their pack's: the Blank one names the neutral approver group and none of the JBS states or groups.
        var releases = app.Services.CreateAsyncScope();
        await using (releases)
        {
            // The Variation workflow is installed the first time something asks for it (the second scenario does); the contract lifecycle is installed when the first contract is created.
            foreach (var kind in new[] { WorkflowKinds.ContractLifecycle })
            {
                var b = await w.W.AsScopeless().Run(s => s.Get<IConfigurationReleases>().LatestAsync(kind, blank.Org));
                var j = await w.W.AsScopeless().Run(s => s.Get<IConfigurationReleases>().LatestAsync(kind, jbs.Org));
                Assert.NotNull(b); Assert.NotNull(j);
                Assert.Contains("\"Approvers\"", b!.ContentJson, StringComparison.Ordinal);
                Assert.DoesNotContain("GroupHeadProcurement", b.ContentJson, StringComparison.Ordinal);
                Assert.Contains("GroupHeadProcurement", j!.ContentJson, StringComparison.Ordinal);
                foreach (var state in jbsStates) Assert.DoesNotContain($"\"code\": \"{state}\"", b.ContentJson, StringComparison.Ordinal);
            }
        }

        // The JBS organisation is untouched by any of this.
        var jAfter = await w.W.AsAsync(jbs.User, s => s.Get<ILifecycleService>().StateAsync(jContract));
        Assert.Equal("Draft", jAfter.Status);
    }

    public static async Task A_blank_organisation_resolves_its_variation_workflow_and_notification_policy_and_can_open_a_variation(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        var blank = await AddOrganisationAsync(w, "Blank Org", "PRB", TestPacks.Blank);
        var contract = await NewContractAsync(w, blank, "PRB-000001");
        await w.W.AsAsync(blank.User, s => s.Get<ILifecycleService>().TransitionAsync(new TransitionRequest(contract, "Activate")));

        // The Variation workflow in force is the neutral one (its first state is Open, not the JBS Draft), and the page-level lookups that read it do not fail.
        var fields = await w.W.AsAsync(blank.User, s => s.Get<IVariationService>().ChangeableFieldsAsync(contract));
        Assert.Contains("endDate", fields);
        var variation = await w.W.AsAsync(blank.User, s => s.Get<IVariationService>().CreateAsync(contract,
            new VariationInput("Extend term", "Neutral pack proof", "Because", null, false, null, null, null,
                new Dictionary<string, string?> { ["endDate"] = DateOnly.FromDateTime(w.W.Now.AddDays(500)).ToString("yyyy-MM-dd") })));
        var detail = await w.W.AsAsync(blank.User, s => s.Get<IVariationService>().GetAsync(variation));
        Assert.Equal("Open", detail.Summary.Status);
        var parent = await w.W.AsAsync(blank.User, s => s.Get<ILifecycleService>().StateAsync(contract));
        Assert.Equal("Amending", parent.Status);

        // The notification policy is the neutral one and defines the reminder rules.
        var policy = await w.W.AsScopeless().Run(s => s.Get<INotificationPolicyStore>().CurrentAsync(blank.Org));
        Assert.NotNull(policy.RuleFor("reminder.endDate"));
        Assert.NotNull(policy.RuleFor("reminder.criticalNotice"));
        Assert.Null(policy.RuleFor("legal.submitted"));                     // the neutral pack has no Legal review
    }

    public static async Task An_organisation_without_a_pack_uses_the_installation_default_and_is_refused_when_there_is_none(TestApp app)
    {
        // The test hosts set the installation default to the reference pack, so an organisation with no pack of its own gets that pack's definitions.
        var w = await LifecycleWorld.CreateAsync(app);
        var nopack = await AddOrganisationAsync(w, "No Pack Org", "PRN", null);
        var contract = await NewContractAsync(w, nopack, "PRN-000001");
        Assert.Equal("Draft", (await w.W.AsAsync(nopack.User, s => s.Get<ILifecycleService>().StateAsync(contract))).Status);
    }

    public static async Task With_no_installation_default_an_organisation_without_a_pack_is_refused_and_one_with_a_pack_is_not(TestApp _)
    {
        // A host with no installation default (the shipped configuration): the standard test world cannot be built there, because its organisations have no pack, so the definition stores are asked directly.
        await using var undefaulted = await TestApp.CreateSqliteAsync(configure: svc =>
        {
            svc.AddLifecycleModule(o => { o.Mode = EcpMode.Test; o.DefaultPackId = null; });
            svc.AddNotificationsModule(o => { o.Mode = EcpMode.Test; o.DefaultPackId = null; });
        });
        await using var bootstrap = undefaulted.Services.CreateAsyncScope();
        var provisioned = await bootstrap.ServiceProvider.GetRequiredService<ITenantProvisioningService>()
            .ProvisionAsync(new ProvisionTenantRequest("NoDefaultTenant", TenancyMode.MultiOrganisation, EcpMode.Test, "Orphan Org", "Australia/Sydney", "en-AU", "AUD", "ORP"));
        await using var scope = undefaulted.ScopeFor(provisioned.TenantId);
        var ownPack = await scope.Get<ITenantProvisioningService>().AddOrganisationAsync(new AddOrganisationRequest("Own Pack Org", "Australia/Sydney", "en-AU", "AUD", "OWN", TestPacks.Blank));

        var refused = await Assert.ThrowsAsync<DomainException>(() => scope.Get<IWorkflowDefinitions>().CurrentAsync(WorkflowKinds.ContractLifecycle, provisioned.OrganisationId));
        Assert.Equal("no_solution_pack", refused.Code);
        Assert.Contains("Ecp:DefaultPackId", refused.Message, StringComparison.Ordinal);
        var refusedPolicy = await Assert.ThrowsAsync<DomainException>(() => scope.Get<INotificationPolicyStore>().CurrentAsync(provisioned.OrganisationId));
        Assert.Equal("no_solution_pack", refusedPolicy.Code);

        // Nothing was installed for the refused organisation, and an organisation with a pack of its own is not affected by the missing default.
        Assert.Null(await scope.Get<IConfigurationReleases>().LatestAsync(WorkflowKinds.ContractLifecycle, provisioned.OrganisationId));
        var own = await scope.Get<IWorkflowDefinitions>().CurrentAsync(WorkflowKinds.ContractLifecycle, ownPack);
        Assert.Equal("Open", own.Definition.Initial.Code);
        Assert.NotNull((await scope.Get<INotificationPolicyStore>().CurrentAsync(ownPack)).RuleFor("reminder.endDate"));
    }
}

/// <summary>The 17 state codes of the JBS reference lifecycle, read from the shipped pack file.</summary>
internal static class PackFilesForTests
{
    public static IReadOnlyList<string> JbsStateCodes()
    {
        var json = File.ReadAllText(Path.Combine(RepoRoot.Find(), "templates", "packs", "jbs-procurement", "workflows", "contract-lifecycle.json"));
        var (def, _) = WorkflowDefinitionParser.Parse(json);
        return def!.States.Select(s => s.Code).ToList();
    }
}

public sealed class PackResolutionScenarioTests_Sqlite
{
    [Fact] public Task Own_pack() => ProviderRunner.SqliteAsync(PackResolutionScenarios.Each_organisation_works_from_its_own_pack_and_a_blank_organisation_never_sees_a_JBS_state);
    [Fact] public Task Variation_and_policy() => ProviderRunner.SqliteAsync(PackResolutionScenarios.A_blank_organisation_resolves_its_variation_workflow_and_notification_policy_and_can_open_a_variation);
    [Fact] public Task Installation_default() => ProviderRunner.SqliteAsync(PackResolutionScenarios.An_organisation_without_a_pack_uses_the_installation_default_and_is_refused_when_there_is_none);
    [Fact] public Task No_installation_default() => ProviderRunner.SqliteAsync(PackResolutionScenarios.With_no_installation_default_an_organisation_without_a_pack_is_refused_and_one_with_a_pack_is_not);
}

[Trait("Category", "SqlServer")]
public sealed class PackResolutionScenarioTests_SqlServer
{
    [SqlServerFact] public Task Own_pack() => ProviderRunner.SqlServerAsync(PackResolutionScenarios.Each_organisation_works_from_its_own_pack_and_a_blank_organisation_never_sees_a_JBS_state);
    [SqlServerFact] public Task Variation_and_policy() => ProviderRunner.SqlServerAsync(PackResolutionScenarios.A_blank_organisation_resolves_its_variation_workflow_and_notification_policy_and_can_open_a_variation);
    [SqlServerFact] public Task Installation_default() => ProviderRunner.SqlServerAsync(PackResolutionScenarios.An_organisation_without_a_pack_uses_the_installation_default_and_is_refused_when_there_is_none);
    [SqlServerFact] public Task No_installation_default() => ProviderRunner.SqlServerAsync(PackResolutionScenarios.With_no_installation_default_an_organisation_without_a_pack_is_refused_and_one_with_a_pack_is_not);
}
