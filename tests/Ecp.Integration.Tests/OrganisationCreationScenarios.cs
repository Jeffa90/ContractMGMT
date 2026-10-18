using Ecp.Audit;
using Ecp.Configuration;
using Ecp.Identity;
using Ecp.Identity.Domain;
using Ecp.Setup.Organisations;
using Ecp.SharedKernel;
using Ecp.Tenancy;
using Ecp.Tenancy.Domain;
using Ecp.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecp.Integration.Tests;

/// <summary>
/// G-02: a Tenant Administrator creates another organisation from a form without code or SQL. Every problem is reported against its field before anything is created; nothing is auto-published; the initial administrator
/// is scoped to the new organisation alone; a single-organisation installation is widened first, once, and never narrowed; setup of an unused organisation can be reset, and one that holds work cannot; everything is audited and
/// stays inside the tenant.
/// </summary>
internal static class OrganisationCreationScenarios
{
    private static string Unique(string p) => p + Guid.NewGuid().ToString("N")[..5].ToUpperInvariant();

    private const string Structure = "type,key,name,parentKey,timeZone,registrationNumber\nDivision,CIVIL,Civil Division,,,\nBusinessUnit,ROADS,Roads and Bridges,CIVIL,,\nSite,SYD,Sydney Office,,Australia/Sydney,\nLegalEntity,HBL-AU,Harborline Australia Pty Ltd,,,ACN 000 000 019";

    private static NewOrganisationInput Valid(string key, Guid? admin, string? name = null)
        => new(name ?? "Harborline Australia " + key, "Harborline Australia Pty Ltd", key, "AU", "en-AU", "Australia/Sydney", "AUD", "/", 5, HarborlineWorld.PackKey, admin, Structure);

    private static async Task<List<Ecp.Audit.Domain.AuditEvent>> AuditAsync(HarborlineWorld w, string action)
    {
        await using var scope = w.AsScopeless();
        return await scope.Get<IAuditEventSource>().Events.Where(e => e.Action == action).OrderBy(e => e.Sequence).ToListAsync();
    }

    public static async Task Every_problem_is_reported_against_its_field_before_anything_is_created(TestApp app)
    {
        var w = await HarborlineWorld.CreateTenantAsync(app);
        var key = Unique("OP");
        var existing = await w.AsAsync("admin", s => s.Get<IOrganisationStructureService>().ListOrganisationsAsync());

        var bad = new NewOrganisationInput("", "", "9x", "ZZ", "not a locale", "Mars/Olympus", "XYZZY", "#", 2, "no-such-pack", Guid.NewGuid(), "type,key,name\nPlanet,X,Y\nDivision,bad key,Z");
        var plan = await w.AsAsync("admin", s => s.Get<IOrganisationCreation>().PreviewAsync(bad));
        Assert.False(plan.CanCreate);
        foreach (var field in new[] { "Name", "LegalName", "Key", "ReferenceSeparator", "ReferenceDigits", "Country", "Culture", "TimeZone", "Currency", "PackKey", "InitialAdministratorId", "StructureCsv" })
            Assert.True(plan.Problems.Any(p => p.IsError && p.Field == field), $"No problem was reported for {field}. Reported: " + string.Join(" | ", plan.Problems.Select(p => p.Field + ": " + p.Message)));
        Assert.Empty(plan.Actions);                                                       // nothing is promised when something is wrong

        // A duplicate name and a duplicate key are caught, whatever the case.
        var dupName = await w.AsAsync("admin", s => s.Get<IOrganisationCreation>().PreviewAsync(Valid(key, null, existing[0].Name.ToUpperInvariant())));
        Assert.Contains(dupName.Problems, p => p.Field == "Name" && p.IsError);
        var dupKey = await w.AsAsync("admin", s => s.Get<IOrganisationCreation>().PreviewAsync(Valid(existing[0].ReferencePrefix.ToLowerInvariant(), null)));
        Assert.Contains(dupKey.Problems, p => p.Field == "Key" && p.IsError);

        // A structure that points at a division it does not list, or reuses the key made from the legal name, is refused.
        var cross = await w.AsAsync("admin", s => s.Get<IOrganisationCreation>().PreviewAsync(Valid(key, null) with { StructureCsv = "type,key,name,parentKey\nBusinessUnit,ROADS,Roads,NOWHERE\nLegalEntity,MAIN,Again" }));
        Assert.Equal(2, cross.Problems.Count(p => p.Field == "StructureCsv" && p.IsError));

        // Create refuses the same input, and creates nothing.
        await Assert.ThrowsAsync<DomainException>(() => w.AsAsync("admin", s => s.Get<IOrganisationCreation>().CreateAsync(bad)));
        Assert.Equal(existing.Count, (await w.AsAsync("admin", s => s.Get<IOrganisationStructureService>().ListOrganisationsAsync())).Count);

        // A valid form with no administrator is allowed, with a note that says what that means.
        var fine = await w.AsAsync("admin", s => s.Get<IOrganisationCreation>().PreviewAsync(Valid(key, null)));
        Assert.True(fine.CanCreate);
        Assert.Contains(fine.Problems, p => !p.IsError && p.Field == "InitialAdministratorId");
        Assert.Equal($"{key}/00001", fine.SampleReference);
        Assert.NotEmpty(fine.Actions);
        Assert.Equal(existing.Count, (await w.AsAsync("admin", s => s.Get<IOrganisationStructureService>().ListOrganisationsAsync())).Count);
    }

    public static async Task An_organisation_is_created_with_structure_an_administrator_and_pack_drafts_and_nothing_is_published(TestApp app)
    {
        var w = await HarborlineWorld.CreateTenantAsync(app);
        var key = Unique("AU");
        var candidates = await w.AsAsync("admin", s => s.Get<IOrganisationCreation>().CandidateAdministratorsAsync());
        var person = w.Users["commercial"];
        Assert.Contains(candidates, c => c.Id == person);

        var created = await w.AsAsync("admin", s => s.Get<IOrganisationCreation>().CreateAsync(Valid(key, person)));
        var orgId = created.OrganisationId;
        Assert.NotEmpty(created.Done); Assert.NotEmpty(created.NextSteps);

        await using var read = w.AsScopeless();
        var org = await read.Get<IOrganisationStructureService>().GetOrganisationAsync(orgId);
        Assert.NotNull(org);
        Assert.Equal(key, org!.ReferencePrefix); Assert.Equal("Australia/Sydney", org.TimeZoneId); Assert.Equal("en-AU", org.CultureCode); Assert.Equal("AUD", org.DefaultCurrencyCode);
        var structure = await read.Get<IOrganisationStructureService>().GetAsync(orgId);
        Assert.Equal(["HBL-AU", "MAIN"], structure.LegalEntities.Select(l => l.Key).OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal("Harborline Australia Pty Ltd", structure.LegalEntities.Single(l => l.Key == "MAIN").Name);
        Assert.Equal("CIVIL", structure.Divisions.Single().Key);
        Assert.Equal(structure.Divisions.Single().Id, structure.BusinessUnits.Single().DivisionId);
        Assert.Equal("Australia/Sydney", structure.Sites.Single().TimeZoneId);

        // The initial administrator can administer this organisation only.
        var held = (await read.Get<IRoleAssignmentService>().ListForUserAsync(person)).Where(a => a.RoleCode == RoleCodes.OrganisationAdministrator).ToList();
        var grant = Assert.Single(held);
        Assert.Equal([orgId], grant.Scope.OrganisationIds);
        Assert.Equal(AssignmentSource.Setup, grant.Source);

        // The pack choice is recorded and whatever the Tenant Administrator may draft is a draft; nothing is published or submitted.
        var releases = read.Get<IConfigurationReleases>();
        Assert.Equal(HarborlineWorld.PackKey, org.SolutionPackKey);
        var atCreation = await releases.OpenDraftsAsync(orgId);
        Assert.All(atCreation, d => { Assert.False(d.Submitted); });
        foreach (var d in atCreation) Assert.Null(await releases.LatestAsync(d.Kind, orgId));
        Assert.Contains(created.NextSteps, n => n.Contains("Assign"));

        // The new organisation's administrator prepares the rest from the pack: drafts only, none published.
        var assigned = await w.AsAsync("commercial", s => s.Get<IPackAssignments>().AssignAsync(orgId, HarborlineWorld.PackKey, HarborlineWorld.ShippedVersion));
        Assert.Contains(assigned.Kinds, k => k.Outcome.StartsWith("Draft created", StringComparison.Ordinal));
        var drafts = await releases.OpenDraftsAsync(orgId);
        Assert.True(drafts.Count > atCreation.Count);
        foreach (var d in drafts) Assert.Null(await releases.LatestAsync(d.Kind, orgId));
        Assert.Null(await releases.LatestAsync(RegionalKinds.Name, orgId));
        Assert.All(drafts, d => Assert.False(d.Submitted));

        // The checklist is read from those facts.
        var checklist = await w.AsAsync("admin", s => s.Get<IOrganisationCreation>().ChecklistAsync(orgId));
        Assert.Equal(ChecklistState.Done, checklist.Items.Single(i => i.Key == "organisation").State);
        Assert.Equal(ChecklistState.Done, checklist.Items.Single(i => i.Key == "administrator").State);
        Assert.Equal(ChecklistState.Done, checklist.Items.Single(i => i.Key == "pack").State);
        Assert.Contains(checklist.Items, i => i.Key == "workflow" && i.State == ChecklistState.InProgress);
        Assert.Equal(ChecklistState.InProgress, checklist.Items.Single(i => i.Key == "regional").State);       // drafted from the pack, not published
        Assert.True(checklist.CanReset);

        // Duplicates are refused the second time.
        await Assert.ThrowsAsync<DomainException>(() => w.AsAsync("admin", s => s.Get<IOrganisationCreation>().CreateAsync(Valid(key, null))));

        // Audit: the creation, each structure item and the summary.
        Assert.Contains(await AuditAsync(w, "organisation.created"), e => e.EntityId == orgId.ToString());
        Assert.Contains(await AuditAsync(w, "organisation.setup.created"), e => e.EntityId == orgId.ToString());
        Assert.NotEmpty(await AuditAsync(w, "structure.site.created"));
    }

    public static async Task Only_a_tenant_administrator_creates_organisations_and_nothing_crosses_a_tenant(TestApp app)
    {
        var w = await HarborlineWorld.CreateTenantAsync(app);
        var other = await HarborlineWorld.CreateTenantAsync(app);
        var key = Unique("IS");

        // An organisation administrator or a procurement administrator may not create, list or widen.
        foreach (var user in new[] { "commercial", "director1" })
        {
            await Assert.ThrowsAsync<ForbiddenException>(() => w.AsAsync(user, s => s.Get<IOrganisationCreation>().CreateAsync(Valid(key, null))));
            await Assert.ThrowsAsync<ForbiddenException>(() => w.AsAsync(user, s => s.Get<IOrganisationCreation>().ListAsync()));
            await Assert.ThrowsAsync<ForbiddenException>(() => w.AsAsync(user, s => s.Get<IOrganisationCreation>().WidenTenancyAsync("because")));
        }
        Assert.Empty(await AuditAsync(w, "organisation.setup.created"));

        // Another tenant's administrator sees only their own organisations and cannot open this one's checklist.
        var created = await w.AsAsync("admin", s => s.Get<IOrganisationCreation>().CreateAsync(Valid(key, null)));
        var theirs = await other.AsAsync("admin", s => s.Get<IOrganisationCreation>().ListAsync());
        Assert.DoesNotContain(theirs, o => o.Id == created.OrganisationId);
        await Assert.ThrowsAnyAsync<Exception>(() => other.AsAsync("admin", s => s.Get<IOrganisationCreation>().ChecklistAsync(created.OrganisationId)));
        await Assert.ThrowsAnyAsync<Exception>(() => other.AsAsync("admin", s => s.Get<IOrganisationCreation>().ResetSetupAsync(created.OrganisationId, "not mine")));

        // The same key and name may exist in another tenant: uniqueness is per tenant.
        var same = await other.AsAsync("admin", s => s.Get<IOrganisationCreation>().CreateAsync(Valid(key, null)));
        Assert.NotEqual(created.OrganisationId, same.OrganisationId);

        // An organisation administrator of the new organisation can follow its checklist (onboarding.run) but cannot reset it or create others.
        var admin = w.Users["commercial"];
        await using (var g = w.AsScopeless())
            await g.Get<IRoleAssignmentService>().GrantTrustedAsync(new GrantRequest(admin, RoleCodes.OrganisationAdministrator, null, ScopeSet.ForOrganisations([created.OrganisationId], "Restricted"), ExplicitHighCeiling: true), AssignmentSource.Demo, "test");
        var view = await w.AsAsync("commercial", s => s.Get<IOrganisationCreation>().ChecklistAsync(created.OrganisationId));
        Assert.NotEmpty(view.Items);
        await Assert.ThrowsAsync<ForbiddenException>(() => w.AsAsync("commercial", s => s.Get<IOrganisationCreation>().ResetSetupAsync(created.OrganisationId, "not allowed")));
    }

    public static async Task A_single_organisation_installation_is_widened_once_with_a_reason_and_never_narrowed(TestApp app)
    {
        var w = await HarborlineWorld.CreateTenantAsync(app, tenancy: TenancyMode.SingleOrganisation);
        var key = Unique("W");
        var before = await w.AsAsync("admin", s => s.Get<IOrganisationCreation>().TenancyAsync());
        Assert.True(before.IsSingleOrganisation); Assert.Equal(1, before.Organisations);

        var plan = await w.AsAsync("admin", s => s.Get<IOrganisationCreation>().PreviewAsync(Valid(key, null)));
        Assert.True(plan.WidenRequired); Assert.False(plan.CanCreate); Assert.Contains(plan.Problems, p => p.Field == "Tenancy");
        await Assert.ThrowsAsync<DomainException>(() => w.AsAsync("admin", s => s.Get<IOrganisationCreation>().CreateAsync(Valid(key, null))));

        await Assert.ThrowsAsync<DomainException>(() => w.AsAsync("admin", s => s.Get<IOrganisationCreation>().WidenTenancyAsync("  ")));           // a reason is required
        await w.AsAsync("admin", s => s.Get<IOrganisationCreation>().WidenTenancyAsync("A second company joins the group"));
        var widened = Assert.Single(await AuditAsync(w, "tenant.tenancy_widened"));
        Assert.Contains("A second company joins the group", widened.Reason);
        Assert.Contains("MultiOrganisation", widened.NewValuesJson);
        Assert.False((await w.AsAsync("admin", s => s.Get<IOrganisationCreation>().TenancyAsync())).IsSingleOrganisation);

        // It is not widened again, and there is no way back.
        await Assert.ThrowsAsync<DomainException>(() => w.AsAsync("admin", s => s.Get<IOrganisationCreation>().WidenTenancyAsync("again")));
        var created = await w.AsAsync("admin", s => s.Get<IOrganisationCreation>().CreateAsync(Valid(key, null)));
        Assert.NotEqual(Guid.Empty, created.OrganisationId);
        Assert.Throws<DomainException>(() => Tenant.Create("x", TenancyMode.MultiOrganisation, EcpMode.Test, DateTime.UtcNow).WidenTo(TenancyMode.SingleOrganisation));
    }

    public static async Task Setup_of_an_unused_organisation_is_reset_and_one_that_holds_work_is_not(TestApp app)
    {
        var w = await HarborlineWorld.ReadyAsync(app);
        var key = Unique("RS");
        var created = await w.AsAsync("admin", s => s.Get<IOrganisationCreation>().CreateAsync(Valid(key, w.Users["commercial"])));
        var orgId = created.OrganisationId;
        await w.AsAsync("commercial", s => s.Get<IPackAssignments>().AssignAsync(orgId, HarborlineWorld.PackKey, HarborlineWorld.ShippedVersion));
        await using (var read = w.AsScopeless()) Assert.NotEmpty(await read.Get<IConfigurationReleases>().OpenDraftsAsync(orgId));

        await Assert.ThrowsAsync<DomainException>(() => w.AsAsync("admin", s => s.Get<IOrganisationCreation>().ResetSetupAsync(orgId, " ")));
        var outcome = await w.AsAsync("admin", s => s.Get<IOrganisationCreation>().ResetSetupAsync(orgId, "Entered the wrong legal name"));
        Assert.True(outcome.DraftsDiscarded > 0); Assert.Equal(5, outcome.StructureItemsEnded);
        await using (var read = w.AsScopeless())
        {
            Assert.Empty(await read.Get<IConfigurationReleases>().OpenDraftsAsync(orgId));
            var structure = await read.Get<IOrganisationStructureService>().GetAsync(orgId);
            Assert.All(structure.LegalEntities, l => Assert.False(l.IsActive));
            Assert.NotNull(await read.Get<IOrganisationStructureService>().GetOrganisationAsync(orgId));        // never deleted
        }
        var reset = Assert.Single(await AuditAsync(w, "organisation.setup.reset"));
        Assert.Contains("wrong legal name", reset.Reason);

        // An organisation that already holds an agreement cannot be reset.
        await w.NewSubcontractAsync();
        var refused = await Assert.ThrowsAsync<DomainException>(() => w.AsAsync("admin", s => s.Get<IOrganisationCreation>().ResetSetupAsync(w.Org, "oops")));
        Assert.Equal("organisation_in_use", refused.Code);
        Assert.False((await w.AsAsync("admin", s => s.Get<IOrganisationCreation>().ChecklistAsync(w.Org))).CanReset);
    }

    public static async Task Creating_widening_and_resetting_need_a_recent_sign_in(TestApp app)
    {
        var w = await HarborlineWorld.CreateTenantAsync(app);
        await using (var scope = w.AsScopeless()) scope.Get<ConfigurationOptions>().RequireRecentSignInForRollback = true;
        try
        {
            await Assert.ThrowsAsync<ReauthenticationRequiredException>(() => w.AsAsync("admin", s => s.Get<IOrganisationCreation>().CreateAsync(Valid(Unique("SU"), null))));
            await Assert.ThrowsAsync<ReauthenticationRequiredException>(() => w.AsAsync("admin", s => s.Get<IOrganisationCreation>().WidenTenancyAsync("x")));
            await Assert.ThrowsAsync<ReauthenticationRequiredException>(() => w.AsAsync("admin", s => s.Get<IOrganisationCreation>().ResetSetupAsync(w.Org, "x")));
            Assert.Empty(await AuditAsync(w, "organisation.setup.created"));
            var recent = await w.AsAsync("admin", s => { s.Get<ReauthenticationContext>().Set(w.Now); return s.Get<IOrganisationCreation>().CreateAsync(Valid(Unique("SU"), null)); });
            Assert.NotEqual(Guid.Empty, recent.OrganisationId);
        }
        finally { await using var scope = w.AsScopeless(); scope.Get<ConfigurationOptions>().RequireRecentSignInForRollback = false; }
    }
}

[Trait("Category", "Governance")]
public sealed class OrganisationCreationSqliteTests
{
    [Fact] public Task Problems() => ProviderRunner.SqliteAsync(OrganisationCreationScenarios.Every_problem_is_reported_against_its_field_before_anything_is_created);
    [Fact] public Task Create() => ProviderRunner.SqliteAsync(OrganisationCreationScenarios.An_organisation_is_created_with_structure_an_administrator_and_pack_drafts_and_nothing_is_published);
    [Fact] public Task Isolation() => ProviderRunner.SqliteAsync(OrganisationCreationScenarios.Only_a_tenant_administrator_creates_organisations_and_nothing_crosses_a_tenant);
    [Fact] public Task Widen() => ProviderRunner.SqliteAsync(OrganisationCreationScenarios.A_single_organisation_installation_is_widened_once_with_a_reason_and_never_narrowed);
    [Fact] public Task Reset() => ProviderRunner.SqliteAsync(OrganisationCreationScenarios.Setup_of_an_unused_organisation_is_reset_and_one_that_holds_work_is_not);
    [Fact] public Task StepUp() => ProviderRunner.SqliteAsync(OrganisationCreationScenarios.Creating_widening_and_resetting_need_a_recent_sign_in);
}

[Trait("Category", "SqlServer")]
public sealed class OrganisationCreationSqlServerTests
{
    [SqlServerFact] public Task Problems() => ProviderRunner.SqlServerAsync(OrganisationCreationScenarios.Every_problem_is_reported_against_its_field_before_anything_is_created);
    [SqlServerFact] public Task Create() => ProviderRunner.SqlServerAsync(OrganisationCreationScenarios.An_organisation_is_created_with_structure_an_administrator_and_pack_drafts_and_nothing_is_published);
    [SqlServerFact] public Task Isolation() => ProviderRunner.SqlServerAsync(OrganisationCreationScenarios.Only_a_tenant_administrator_creates_organisations_and_nothing_crosses_a_tenant);
    [SqlServerFact] public Task Widen() => ProviderRunner.SqlServerAsync(OrganisationCreationScenarios.A_single_organisation_installation_is_widened_once_with_a_reason_and_never_narrowed);
    [SqlServerFact] public Task Reset() => ProviderRunner.SqlServerAsync(OrganisationCreationScenarios.Setup_of_an_unused_organisation_is_reset_and_one_that_holds_work_is_not);
    [SqlServerFact] public Task StepUp() => ProviderRunner.SqlServerAsync(OrganisationCreationScenarios.Creating_widening_and_resetting_need_a_recent_sign_in);
}
