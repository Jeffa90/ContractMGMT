using Ecp.Audit;
using Ecp.Audit.Persistence;
using Ecp.Contracts;
using Ecp.Identity;
using Ecp.Identity.Access;
using Ecp.Identity.Domain;
using Ecp.SharedKernel;
using Ecp.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecp.Integration.Tests;

/// <summary>Support and emergency access (IDN-03): reason-based, time-limited, audited, read-bounded, never past the tenant boundary and never able to alter an Effective record.</summary>
internal static class ElevationScenarios
{
    private sealed record Installation(Guid Admin1, Guid Admin2);

    private static async Task<Installation> InstallationAsync(TestApp app)
    {
        async Task<Guid> Admin(string key)
        {
            await using var s = app.ScopeFor(SystemTenant.Id);
            var id = await s.Get<IUserService>().CreateActiveTrustedAsync("Sysadmin " + key, key + Guid.NewGuid().ToString("N")[..6] + "@installation.example.test", "demo", "https://demo.example.test", key + "|" + Guid.NewGuid(), "test");
            await s.Get<IRoleAssignmentService>().GrantTrustedAsync(new GrantRequest(id, RoleCodes.SystemAdministrator, null, ScopeSet.Installation(), EffectiveFromUtc: app.Clock.UtcNow.AddDays(-30)), AssignmentSource.Demo, "test");
            return id;
        }
        return new Installation(await Admin("one"), await Admin("two"));
    }

    /// <summary>A request as the installation administrator in their own tenant, with a recent authentication (or without).</summary>
    private static TenantScope AsAdmin(TestApp app, Guid admin, bool recentlyAuthenticated = true)
    {
        var s = app.ScopeFor(SystemTenant.Id);
        s.Get<CurrentUser>().Set(admin);
        var actor = s.Get<ActorContext>(); actor.Type = ActorType.User; actor.ActorId = admin.ToString(); actor.Source = "Test";
        if (recentlyAuthenticated) s.Get<ReauthenticationContext>().Set(app.Clock.UtcNow);
        return s;
    }

    /// <summary>A request that runs under an elevation, exactly as the host sets it up after re-validating the session's claim.</summary>
    private static async Task<TenantScope?> UnderAsync(TestApp app, Guid tenantId, Guid elevationId, Guid admin)
    {
        var s = app.ScopeFor(tenantId);
        var grant = await s.Get<IElevationService>().ResolveActiveAsync(tenantId, elevationId, admin);
        if (grant is null) { await s.DisposeAsync(); return null; }
        s.Get<ElevationContext>().Activate(grant);
        s.Get<CurrentUser>().Set(admin);
        var actor = s.Get<ActorContext>(); actor.Type = ActorType.Support; actor.ActorId = admin.ToString(); actor.ElevationId = grant.ElevationId; actor.Source = "Test";
        return s;
    }

    private static async Task<ElevationDto> OpenAsync(TestApp app, Installation i, Guid tenant, ElevationKind kind = ElevationKind.Support, int minutes = 60, string? reason = null)
    {
        await using var s = AsAdmin(app, i.Admin1);
        return await s.Get<IElevationService>().OpenAsync(new OpenElevationRequest(tenant, kind, reason ?? "Customer reported contracts missing from their register", TimeSpan.FromMinutes(minutes)));
    }

    private static async Task<AccessWorld> WorldAsync(TestApp app) => await AccessWorld.CreateAsync(app, "TenantE" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant());

    // ---- AUT-012 ---------------------------------------------------------------------------------------------------------------------------------

    public static async Task Opening_access_needs_the_permission_a_recent_sign_in_a_real_reason_and_a_limited_time(TestApp app)
    {
        var i = await InstallationAsync(app); var w = await WorldAsync(app);
        // No recent authentication: refused (the host sends the person to sign in again).
        await using (var s = AsAdmin(app, i.Admin1, recentlyAuthenticated: false))
            await Assert.ThrowsAsync<ReauthenticationRequiredException>(() => s.Get<IElevationService>().OpenAsync(new OpenElevationRequest(w.TenantId, ElevationKind.Support, "A proper reason for access", TimeSpan.FromHours(1))));
        // Not an installation administrator: refused, whatever their tenant role.
        await using (var s = app.ScopeFor(w.TenantId))
        {
            s.Get<CurrentUser>().Set(w.Users["tenantAdmin"]); s.Get<ReauthenticationContext>().Set(app.Clock.UtcNow);
            await Assert.ThrowsAsync<ForbiddenException>(() => s.Get<IElevationService>().OpenAsync(new OpenElevationRequest(w.TenantId, ElevationKind.Support, "A proper reason for access", TimeSpan.FromHours(1))));
        }
        await using (var s = AsAdmin(app, i.Admin1))
        {
            var svc = s.Get<IElevationService>();
            Assert.Equal("reason_too_short", (await Assert.ThrowsAsync<DomainException>(() => svc.OpenAsync(new OpenElevationRequest(w.TenantId, ElevationKind.Support, "help", TimeSpan.FromHours(1))))).Code);
            Assert.Equal("invalid_duration", (await Assert.ThrowsAsync<DomainException>(() => svc.OpenAsync(new OpenElevationRequest(w.TenantId, ElevationKind.Support, "A proper reason for access", TimeSpan.FromHours(5))))).Code);
            Assert.Equal("invalid_target", (await Assert.ThrowsAsync<DomainException>(() => svc.OpenAsync(new OpenElevationRequest(SystemTenant.Id, ElevationKind.Support, "A proper reason for access", TimeSpan.FromHours(1))))).Code);
            Assert.Equal("tenant_not_found", (await Assert.ThrowsAsync<DomainException>(() => svc.OpenAsync(new OpenElevationRequest(Guid.NewGuid(), ElevationKind.Support, "A proper reason for access", TimeSpan.FromHours(1))))).Code);
            Assert.Equal("ceiling_too_high", (await Assert.ThrowsAsync<DomainException>(() => svc.OpenAsync(new OpenElevationRequest(w.TenantId, ElevationKind.Support, "A proper reason for access", TimeSpan.FromHours(1), "Restricted")))).Code);
        }
        var ok = await OpenAsync(app, i, w.TenantId);
        Assert.Equal(ElevationState.Active, ok.State);
        Assert.True(ok.ExpiresUtc > app.Clock.UtcNow && ok.ExpiresUtc <= app.Clock.UtcNow.AddHours(4));
    }

    // ---- no bypass of tenant isolation, read-only bounds ------------------------------------------------------------------------------------------

    public static async Task Support_access_is_read_only_bounded_by_the_ceiling_and_confined_to_one_tenant(TestApp app)
    {
        var i = await InstallationAsync(app); var a = await WorldAsync(app); var b = await WorldAsync(app);
        var e = await OpenAsync(app, i, a.TenantId);
        await using var under = await UnderAsync(app, a.TenantId, e.Id, i.Admin1);
        Assert.NotNull(under);
        var contracts = under!.Get<ISecuredContractService>();
        // Reads what the ceiling (the tenant's default level) allows: ordinary records yes, Confidential/Restricted no.
        var page = await contracts.ListAsync(new ContractFilter(PageSize: 200));
        Assert.Contains(page.Items, c => c.Id == a.Contracts["C1"]);
        Assert.DoesNotContain(page.Items, c => c.Id == a.Contracts["C2"]);     // Confidential
        Assert.DoesNotContain(page.Items, c => c.Id == a.Contracts["C4"]);     // Restricted
        // Any write is refused, including the ones the underlying role would hold.
        await Assert.ThrowsAsync<ForbiddenException>(() => contracts.UpdateDetailsAsync(a.Contracts["C1"], "Changed by support", null));
        await Assert.ThrowsAsync<ForbiddenException>(() => contracts.ChangeClassificationAsync(a.Contracts["C1"], "Confidential", null));
        // The other tenant does not exist for this elevation: its ids answer 404 and its records are never listed.
        await Assert.ThrowsAsync<ResourceNotFoundException>(() => contracts.GetAsync(b.Contracts["C1"]));
        Assert.DoesNotContain(page.Items, c => c.Id == b.Contracts["C1"]);
        // An elevation for tenant A cannot be resolved in tenant B's scope, nor by a different person.
        await using (var other = app.ScopeFor(b.TenantId)) Assert.Null(await other.Get<IElevationService>().ResolveActiveAsync(b.TenantId, e.Id, i.Admin1));
        await using (var other = app.ScopeFor(a.TenantId)) Assert.Null(await other.Get<IElevationService>().ResolveActiveAsync(a.TenantId, e.Id, i.Admin2));
        // The tenant's own roles do not leak in: an installation administrator has none there, and the elevation grants no administration.
        Assert.False(await under.Get<IAccessService>().HoldsAnywhereAsync("users.manage"));
        Assert.False(await under.Get<IAccessService>().HoldsAnywhereAsync("contracts.unlock_emergency"));
    }

    public static async Task Emergency_access_adds_only_account_recovery_and_can_never_alter_an_effective_record(TestApp app)
    {
        var i = await InstallationAsync(app); var w = await WorldAsync(app);
        app.Services.GetRequiredService<IdentityOptions>().RequireEmergencyConfirmation = false;
        var e = await OpenAsync(app, i, w.TenantId, ElevationKind.Emergency, reason: "The only tenant administrator has left and nobody can sign in");
        await using var under = await UnderAsync(app, w.TenantId, e.Id, i.Admin1);
        var access = under!.Get<IAccessService>();
        Assert.True(await access.HoldsAnywhereAsync("users.manage"));
        Assert.True(await access.HoldsAnywhereAsync("roles.delegate"));
        // It still cannot touch the locked record: no edit, no unlock, no approval, no Variation.
        var c7 = w.Contracts["C7"];
        foreach (var permission in new[] { "contracts.edit_owned", "contracts.unlock_emergency", "approvals.action", "variations.create", "documents.upload", "classification.lower", "config.publish" })
            Assert.False(await access.HoldsAnywhereAsync(permission), permission);
        await Assert.ThrowsAsync<ForbiddenException>(() => under.Get<ISecuredContractService>().UpdateDetailsAsync(c7, "Silently changed", null));
        await using var t = w.AsScopeless();
        var effective = await t.Get<Ecp.Contracts.Persistence.ContractsDbContext>().Contracts.AsNoTracking().FirstAsync(c => c.Id == c7);
        Assert.NotEqual("Silently changed", effective.ContractTitle);
        // What it can do: restore a tenant administrator (a grant within the delegation rules). The action is attributed and carries the elevation.
        var rescuer = await w.AsAsync("procAdmin", async s => await s.Get<IUserService>().GetAsync(w.Users["procAdmin"]));
        var granted = await under.Get<IRoleAssignmentService>().GrantAsync(new GrantRequest(w.Users["procAdmin"], RoleCodes.OrganisationAdministrator, null, ScopeSet.ForOrganisations([w.Org1], "Internal"), Reason: "Restoring administration"));
        Assert.NotEqual(Guid.Empty, granted);
        Assert.NotNull(rescuer);
        var ev = await t.Get<AuditDbContext>().Events.AsNoTracking().Where(x => x.Action == "role.granted" && x.ElevationId == e.Id).ToListAsync();
        Assert.Single(ev);
        Assert.Equal("Support", ev[0].ActorType);
    }

    // ---- expiry, ending, confirmation, visibility ---------------------------------------------------------------------------------------------

    public static async Task Access_ends_on_expiry_or_when_ended_and_the_tenants_administrators_see_all_of_it(TestApp app)
    {
        var i = await InstallationAsync(app); var w = await WorldAsync(app);
        var e = await OpenAsync(app, i, w.TenantId, minutes: 30);
        await using (var under = await UnderAsync(app, w.TenantId, e.Id, i.Admin1))
            Assert.NotEmpty((await under!.Get<ISecuredContractService>().ListAsync(new ContractFilter())).Items);

        // The tenant's administrator sees it while active and what was done under it.
        var seen = await w.AsAsync("tenantAdmin", s => s.Get<IElevationService>().ListForCurrentTenantAsync(activeOnly: true));
        Assert.Contains(seen, x => x.Id == e.Id && x.State == ElevationState.Active && x.InitiatorLabel.StartsWith("Sysadmin"));
        var actions = await w.AsAsync("tenantAdmin", s => s.Get<IElevationService>().ActionsAsync(e.Id));
        Assert.Contains(actions, a => a.Action == "elevation.opened");
        // A viewer, or an administrator of another tenant, sees none of it.
        await w.AsAsync("viewerAll", async s => await Assert.ThrowsAsync<ForbiddenException>(() => s.Get<IElevationService>().ListForCurrentTenantAsync(false)));

        // The tenant administrator ends it early; a session using it stops working immediately.
        await w.AsAsync("tenantAdmin", s => s.Get<IElevationService>().EndAsync(w.TenantId, e.Id, "Support finished"));
        Assert.Null(await UnderAsync(app, w.TenantId, e.Id, i.Admin1));
        var after = (await w.AsAsync("tenantAdmin", s => s.Get<IElevationService>().ListForCurrentTenantAsync(false))).Single(x => x.Id == e.Id);
        Assert.Equal(ElevationState.Ended, after.State); Assert.Equal("Support finished", after.EndReason);

        // Expiry: an elevation simply stops being active; extending needs a new reasoned request.
        var e2 = await OpenAsync(app, i, w.TenantId, minutes: 15);
        Assert.NotNull(await UnderAsync(app, w.TenantId, e2.Id, i.Admin1));
        app.Clock.Advance(TimeSpan.FromMinutes(16));
        Assert.Null(await UnderAsync(app, w.TenantId, e2.Id, i.Admin1));
        var states = (await w.AsAsync("tenantAdmin", s => s.Get<IElevationService>().ListForCurrentTenantAsync(false))).ToDictionary(x => x.Id, x => x.State);
        Assert.Equal(ElevationState.Expired, states[e2.Id]);
    }

    public static async Task Emergency_access_waits_for_a_second_installation_administrator_where_the_installation_requires_it(TestApp app)
    {
        var i = await InstallationAsync(app); var w = await WorldAsync(app);
        app.Services.GetRequiredService<IdentityOptions>().RequireEmergencyConfirmation = true;
        var pending = await OpenAsync(app, i, w.TenantId, ElevationKind.Emergency, reason: "Locked out of the tenant after a mapping change");
        Assert.Equal(ElevationState.PendingConfirmation, pending.State);
        Assert.Null(await UnderAsync(app, w.TenantId, pending.Id, i.Admin1));           // not usable until confirmed
        await using (var s = AsAdmin(app, i.Admin1))
            Assert.Equal("second_person_required", (await Assert.ThrowsAsync<DomainException>(() => s.Get<IElevationService>().ConfirmAsync(w.TenantId, pending.Id))).Code);
        await using (var s = AsAdmin(app, i.Admin2, recentlyAuthenticated: false))
            await Assert.ThrowsAsync<ReauthenticationRequiredException>(() => s.Get<IElevationService>().ConfirmAsync(w.TenantId, pending.Id));
        await using (var s = AsAdmin(app, i.Admin2)) { var confirmed = await s.Get<IElevationService>().ConfirmAsync(w.TenantId, pending.Id); Assert.Equal(ElevationState.Active, confirmed.State); Assert.Contains("Sysadmin", confirmed.ConfirmedByLabel); }
        Assert.NotNull(await UnderAsync(app, w.TenantId, pending.Id, i.Admin1));
        // A request nobody confirms lapses.
        var stale = await OpenAsync(app, i, w.TenantId, ElevationKind.Emergency, reason: "A second request that nobody confirms in time");
        app.Clock.Advance(TimeSpan.FromMinutes(31));
        await using (var s = AsAdmin(app, i.Admin2)) await Assert.ThrowsAsync<DomainException>(() => s.Get<IElevationService>().ConfirmAsync(w.TenantId, stale.Id));
    }

    public static async Task Every_action_under_an_elevation_carries_its_identifier_in_the_audit_trail(TestApp app)
    {
        var i = await InstallationAsync(app); var w = await WorldAsync(app);
        var e = await OpenAsync(app, i, w.TenantId);
        await using (var under = await UnderAsync(app, w.TenantId, e.Id, i.Admin1))
        {
            await under!.Get<ISecuredContractService>().GetAsync(w.Contracts["C1"]);
            await Assert.ThrowsAsync<ForbiddenException>(() => under.Get<ISecuredContractService>().UpdateDetailsAsync(w.Contracts["C1"], "No", null)); // the denial is audited too
        }
        await using var t = w.AsScopeless();
        var events = await t.Get<AuditDbContext>().Events.AsNoTracking().Where(x => x.ElevationId == e.Id).ToListAsync();
        Assert.Contains(events, x => x.Action == "elevation.opened");
        Assert.Contains(events, x => x.Action == "authorisation.denied" && x.Outcome == "Denied" && x.ActorType == "Support");
        Assert.All(events, x => Assert.Equal(w.TenantId, x.TenantId));            // recorded in the tenant's own chain
        var verify = await t.Get<AuditChainVerifier>().VerifyAsync();
        Assert.True(verify.IsValid);
    }
}
