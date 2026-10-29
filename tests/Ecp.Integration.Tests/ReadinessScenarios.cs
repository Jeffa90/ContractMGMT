using Ecp.Configuration;
using Ecp.Identity;
using Ecp.Identity.Domain;
using Ecp.Setup.Onboarding;
using Ecp.SharedKernel;
using Ecp.Tests.Common;
using Xunit;

namespace Ecp.Integration.Tests;

/// <summary>UX-5: the set-up checklist. What is Required comes from the selected pack; the state of every item is computed from facts; publishing is refused while a Required item is open.</summary>
internal static class ReadinessScenarios
{
    private static async Task<(OnboardingWorld World, TenantScope Scope, IOnboardingService Svc)> StartAsync(TestApp app)
    {
        var w = await OnboardingWorld.CreateAsync(app);
        var s = app.ScopeFor(w.TenantId);
        s.Get<CurrentUser>().Set(w.Users[OnboardingWorld.OrgAdmin]);
        return (w, s, s.Get<IOnboardingService>());
    }

    public static async Task What_is_Required_comes_from_the_selected_pack_and_the_platform_insists_only_on_its_universal_items(TestApp app)
    {
        var (w, s, svc) = await StartAsync(app);
        await using var _ = s;
        // The Blank pack asks for almost nothing: only the platform's universal items are Required, and none of them is open apart from the procedural ones.
        await svc.SelectPackAsync(w.OrganisationId, TestPacks.Blank, loadDefaults: false);
        var blank = await svc.ReadinessAsync(w.OrganisationId);
        Assert.Equal(TestPacks.Blank, blank.PackKey);
        Assert.Equal([ReadinessCatalogue.Organisation, ReadinessCatalogue.Review, ReadinessCatalogue.Publish], blank.Items.Where(i => i.Level == ReadinessLevel.Required).Select(i => i.Key));
        Assert.True(blank.CanPublish);
        Assert.Equal(13, blank.Items.Count);
        // The reference pack for a procurement business asks for more.
        await svc.SelectPackAsync(w.OrganisationId, TestPacks.Reference, loadDefaults: false);
        var jbs = await svc.ReadinessAsync(w.OrganisationId);
        var required = jbs.Items.Where(i => i.Level == ReadinessLevel.Required).Select(i => i.Key).ToHashSet();
        Assert.Superset(new HashSet<string> { ReadinessCatalogue.Categories, ReadinessCatalogue.People, ReadinessCatalogue.Owners, ReadinessCatalogue.Workflow }, required);
        Assert.True(jbs.PackDefinesRequirements);
        Assert.False(jbs.CanPublish);
        // The platform's own items cannot be relaxed by any pack.
        Assert.All(new[] { ReadinessCatalogue.Organisation, ReadinessCatalogue.Review, ReadinessCatalogue.Publish }, k => Assert.Equal(ReadinessLevel.Required, jbs.Items.Single(i => i.Key == k).Level));
    }

    public static async Task Publishing_is_refused_in_words_while_a_Required_item_of_the_pack_is_open_and_allowed_once_it_is_done(TestApp app)
    {
        var (w, s, svc) = await StartAsync(app);
        await using var _ = s;
        await svc.SelectPackAsync(w.OrganisationId, TestPacks.Reference, loadDefaults: false);
        await svc.SaveSectionAsync(w.OrganisationId, "categories", "code,name,parentCode\nPACKAGING,Packaging,\n");
        var validated = await svc.ValidateAsync(w.OrganisationId);
        Assert.True(validated.IsPublishable);                                                   // the draft itself is fine ...
        var refused = await Assert.ThrowsAsync<DomainException>(() => svc.PublishAsync(w.OrganisationId, "Too early"));
        Assert.Equal("required_items_open", refused.Code);                                      // ... but the organisation is not ready
        Assert.Contains("Not ready to publish", refused.RawMessage);
        Assert.Contains("[[contract.owner|plural|sentence]]", refused.RawMessage);              // names what is open (as terminology tokens, expanded for the reader)
        Assert.Equal(ReadinessState.NotStarted, (await svc.ReadinessAsync(w.OrganisationId)).Items.Single(i => i.Key == ReadinessCatalogue.People).State);
        await Assert.ThrowsAsync<DomainException>(() => svc.SubmitOrPublishAsync(w.OrganisationId, "Also refused"));

        // The draft's own categories count towards the checklist (publishing creates them), and nothing is ticked by hand.
        var afterCategories = await svc.ReadinessAsync(w.OrganisationId);
        Assert.Equal(ReadinessState.Complete, afterCategories.Items.Single(i => i.Key == ReadinessCatalogue.Categories).State);
        Assert.Equal(ReadinessState.NotStarted, afterCategories.Items.Single(i => i.Key == ReadinessCatalogue.Owners).State);
    }

    public static async Task An_owner_without_the_role_that_lets_owners_act_needs_attention_not_a_green_tick(TestApp app)
    {
        var (w, s, svc) = await StartAsync(app);
        await using var _ = s;
        await svc.SelectPackAsync(w.OrganisationId, TestPacks.Reference, loadDefaults: false);
        await svc.SaveSectionAsync(w.OrganisationId, "categories", "code,name,parentCode\nPACKAGING,Packaging,\nENERGY,Energy,\n");
        await svc.SaveSectionAsync(w.OrganisationId, "owners", $"categoryCode,email,isPrimary\nPACKAGING,{OnboardingWorld.Owner},true\n");
        var r = await svc.ReadinessAsync(w.OrganisationId);
        var owners = r.Items.Single(i => i.Key == ReadinessCatalogue.Owners);
        Assert.Equal(ReadinessState.NeedsAttention, owners.State);                              // Packaging has an owner who cannot act, Energy has none
        Assert.Contains("do not hold the role", owners.Detail);
        // Give the owner the role: the item still needs the second category's owner, and says so.
        await using var trusted = app.ScopeFor(w.TenantId);
        await trusted.Get<IRoleAssignmentService>().GrantTrustedAsync(new GrantRequest(w.Users[OnboardingWorld.Owner], RoleCodes.CategoryOwner, null, ScopeSet.ForOrganisations([w.OrganisationId], "Internal")), AssignmentSource.Manual, "test");
        var after = await svc.ReadinessAsync(w.OrganisationId);
        Assert.Equal(ReadinessState.NeedsAttention, after.Items.Single(i => i.Key == ReadinessCatalogue.Owners).State);
        Assert.Equal(ReadinessState.Complete, after.Items.Single(i => i.Key == ReadinessCatalogue.People).State);
        await svc.SaveSectionAsync(w.OrganisationId, "owners", $"categoryCode,email,isPrimary\nPACKAGING,{OnboardingWorld.Owner},true\nENERGY,{OnboardingWorld.Owner},true\n");
        Assert.Equal(ReadinessState.Complete, (await svc.ReadinessAsync(w.OrganisationId)).Items.Single(i => i.Key == ReadinessCatalogue.Owners).State);
    }

    public static async Task The_checklist_is_authorised_like_the_rest_of_onboarding(TestApp app)
    {
        var (w, s, svc) = await StartAsync(app);
        await using var _ = s;
        await using var plain = app.ScopeFor(w.TenantId);
        plain.Get<CurrentUser>().Set(w.Users[OnboardingWorld.Plain]);
        await Assert.ThrowsAnyAsync<Exception>(() => plain.Get<IOnboardingService>().ReadinessAsync(w.OrganisationId));
        var other = await OnboardingWorld.CreateAsync(app);
        await Assert.ThrowsAnyAsync<Exception>(() => svc.ReadinessAsync(other.OrganisationId));   // another tenant's organisation
    }
}

public static class ReadinessVocabularyTests
{
    public static void The_checklist_and_the_pack_vocabulary_name_the_same_items_and_the_shipped_packs_use_them_correctly()
    {
        Assert.Equal(OnboardingRequirementVocabulary.Keys, ReadinessCatalogue.Items.Select(i => i.Key));
        var root = RepoRoot.Find();
        foreach (var file in Directory.GetFiles(Path.Combine(root, "templates", "packs"), "pack.json", SearchOption.AllDirectories))
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
            if (!doc.RootElement.TryGetProperty("onboardingRequirements", out var reqs)) continue;
            foreach (var p in reqs.EnumerateObject())
            {
                Assert.Contains(p.Name, OnboardingRequirementVocabulary.Keys);
                Assert.Contains(p.Value.GetString(), OnboardingRequirementVocabulary.Levels);
                Assert.DoesNotContain(p.Name, OnboardingRequirementVocabulary.Universal);       // a pack cannot relax the platform's own items
            }
        }
    }
}
