using Ecp.Contracts;
using Ecp.Identity;
using Ecp.Identity.Access;
using Ecp.Identity.Domain;
using Ecp.Lifecycle;
using Ecp.Notifications;
using Ecp.Notifications.Policy;
using Ecp.Tenancy;
using Ecp.Tenancy.Domain;
using Ecp.Tests.Common;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecp.Integration.Tests;

/// <summary>
/// S2 of the portability remediation (finding P-02a): who is told when nobody owns a contract's category is a rule of the organisation's notification policy, not a role named in code. The JBS reference
/// policy states today's fall-back (the organisation administrators), so its behaviour is unchanged; the Blank pack states none, so a Blank organisation's reminder never reaches a person because of a role
/// the JBS business happens to use. Both halves run on SQLite and SQL Server.
/// </summary>
internal static class ReminderFallbackScenarios
{
    private static Task<ReminderScanResult> Scan(LifecycleWorld w) => w.W.AsScopeless().Run(s => s.Get<IReminderScanner>().ScanAsync());

    private static async Task EndOwnershipAsync(LifecycleWorld w, Guid category, string ownerUser)
    {
        await using var s = w.W.AsScopeless();
        var cats = s.Get<ICategoryAdministration>();
        var current = (await cats.ListAllAsync()).Single(c => c.Id == category).Owners.Single(o => o.UserId == w.W.Users[ownerUser] && o.EffectiveToUtc == null);
        await cats.EndOwnerTrustedAsync(current.AssignmentId, "left the business");
    }

    public static async Task The_reference_policy_still_sends_an_unowned_reminder_to_the_organisation_administrators(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        await w.ToEffectiveInOrderAsync(w.Today.AddDays(20), lead: 30);
        await EndOwnershipAsync(w, w.W.CatPackaging, "ownerPackaging");

        var policy = await w.W.AsScopeless().Run(s => s.Get<INotificationPolicyStore>().CurrentAsync(w.W.Org1));
        Assert.Equal(["OrganisationAdministrators"], policy.Reminders.UnownedFallback);

        var result = await Scan(w);
        Assert.Equal(0, result.SkippedNoRecipient);
        Assert.True(result.RemindersSent >= 1);
        Assert.Contains(await w.InboxAsync("procAdmin"), n => n.Kind == "Reminder" && n.ReminderKind == "endDate");
        Assert.DoesNotContain(await w.InboxAsync("ownerPackaging"), n => n.Kind == "Reminder");
    }

    public static async Task A_blank_organisation_never_resolves_JBS_reminder_recipients_and_its_owners_are_still_reminded(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        Guid org, category, supplier; string owner;
        await using (var s = w.W.AsScopeless())
        {
            org = await s.Get<ITenantProvisioningService>().AddOrganisationAsync(new AddOrganisationRequest("Blank Reminder Org", "Australia/Sydney", "en-AU", "AUD", "RBL", TestPacks.Blank));
            category = await s.Get<ICategoryAdministration>().UpsertCategoryAsync(org, "GEN", "General", null);
            supplier = await s.Get<ISupplierService>().CreateAsync(new CreateSupplierRequest(org, "Blank Reminder Supplier Pty Ltd"));
            owner = "ownerRBL";
            var ownerId = await s.Get<IUserService>().CreateActiveTrustedAsync(owner, owner + "@" + w.W.TenantId.ToString("N")[..8] + ".example.test", "demo", "https://demo.example.test", owner + "|" + w.W.TenantId, "test");
            await s.Get<IRoleAssignmentService>().GrantTrustedAsync(new GrantRequest(ownerId, RoleCodes.CategoryOwner, null, ScopeSet.ForOrganisations([org], "Confidential"), null,
                EffectiveFromUtc: w.W.Now.AddDays(-5), ExplicitHighCeiling: true), AssignmentSource.Demo, "test");
            await s.Get<ICategoryAdministration>().AssignOwnerTrustedAsync(category, ownerId, true, w.W.Now.AddDays(-30), null, "test", null);
            w.W.Users[owner] = ownerId;
            // The organisation also has a person holding the very role the old fall-back named: they must never be told.
            var adminId = await s.Get<IUserService>().CreateActiveTrustedAsync("adminRBL", "adminRBL@" + w.W.TenantId.ToString("N")[..8] + ".example.test", "demo", "https://demo.example.test", "adminRBL|" + w.W.TenantId, "test");
            await s.Get<IRoleAssignmentService>().GrantTrustedAsync(new GrantRequest(adminId, RoleCodes.ProcurementAdministrator, null, ScopeSet.ForOrganisations([org], "Confidential"), null,
                EffectiveFromUtc: w.W.Now.AddDays(-5), ExplicitHighCeiling: true), AssignmentSource.Demo, "test");
            w.W.Users["adminRBL"] = adminId;
        }
        var contract = await w.W.AsAsync(owner, s => s.Get<ISecuredContractService>().CreateDraftAsync(new CreateContractRequest(org, "RBL-000001", "Blank reminder proof", supplier, category,
            CurrencyCode: "AUD", EstimatedValue: 1000m, EndDate: DateOnly.FromDateTime(w.W.Now.AddDays(20)))));
        await w.W.AsAsync(owner, s => s.Get<ILifecycleService>().TransitionAsync(new TransitionRequest(contract, "Activate")));

        // The Blank policy states no fall-back.
        var policy = await w.W.AsScopeless().Run(s => s.Get<INotificationPolicyStore>().CurrentAsync(org));
        Assert.Empty(policy.Reminders.UnownedFallback);

        // Nobody owns the category: the reminder is held (counted as skipped for lack of a recipient), not handed to the administrator, and no window is used up.
        await EndOwnershipAsync(w, category, owner);
        var held = await Scan(w);
        Assert.Equal(0, held.RemindersSent);
        Assert.True(held.SkippedNoRecipient >= 1);
        Assert.DoesNotContain(await w.InboxAsync("adminRBL"), n => n.Kind == "Reminder");
        Assert.DoesNotContain(await w.InboxAsync(owner), n => n.Kind == "Reminder");

        // Ownership is restored: the same reminder is now sent to the owner, once, and the administrator still receives nothing.
        await using (var s = w.W.AsScopeless())
            await s.Get<ICategoryAdministration>().AssignOwnerTrustedAsync(category, w.W.Users[owner], true, w.W.Now.AddDays(-1), null, "test", "returned");
        var sent = await Scan(w);
        Assert.Equal(1, sent.RemindersSent);
        Assert.Single(await w.InboxAsync(owner), n => n.Kind == "Reminder" && n.ReminderKind == "endDate");
        Assert.DoesNotContain(await w.InboxAsync("adminRBL"), n => n.Kind == "Reminder");
        Assert.Equal(0, (await Scan(w)).RemindersSent);
    }
}

public sealed class ReminderFallbackScenarioTests_Sqlite
{
    [Fact] public Task Reference_policy() => ProviderRunner.SqliteAsync(ReminderFallbackScenarios.The_reference_policy_still_sends_an_unowned_reminder_to_the_organisation_administrators, LifecycleScenarios.Scanner);
    [Fact] public Task Blank_policy() => ProviderRunner.SqliteAsync(ReminderFallbackScenarios.A_blank_organisation_never_resolves_JBS_reminder_recipients_and_its_owners_are_still_reminded, LifecycleScenarios.Scanner);
}

[Trait("Category", "SqlServer")]
public sealed class ReminderFallbackScenarioTests_SqlServer
{
    [SqlServerFact] public Task Reference_policy() => ProviderRunner.SqlServerAsync(ReminderFallbackScenarios.The_reference_policy_still_sends_an_unowned_reminder_to_the_organisation_administrators, LifecycleScenarios.Scanner);
    [SqlServerFact] public Task Blank_policy() => ProviderRunner.SqlServerAsync(ReminderFallbackScenarios.A_blank_organisation_never_resolves_JBS_reminder_recipients_and_its_owners_are_still_reminded, LifecycleScenarios.Scanner);
}
