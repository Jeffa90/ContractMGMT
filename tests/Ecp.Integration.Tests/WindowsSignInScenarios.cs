using Ecp.Audit.Persistence;
using Ecp.Identity;
using Ecp.Identity.Domain;
using Ecp.Identity.Persistence;
using Ecp.Identity.Providers;
using Ecp.Identity.Windows;
using Ecp.SharedKernel;
using Ecp.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecp.Integration.Tests;

/// <summary>
/// Windows sign-in is invite-only: Windows Authentication proves identity and nothing else. These scenarios run the real service over the real schema; the
/// Windows principal itself is supplied as account name + SID, exactly what the web server gives the application. They do NOT prove an IIS run.
/// </summary>
public static class WindowsSignInScenarios
{
    private const string Sid1 = "S-1-5-21-1111111111-2222222222-3333333333-1105";
    private const string Sid2 = "S-1-5-21-1111111111-2222222222-3333333333-1106";

    private static async Task<Guid> EnableAsync(TestApp app, Guid tenant, string? domains = null)
    {
        await using var s = app.ScopeFor(tenant);
        var issues = await s.Get<IProviderRegistry>().SaveAsync(new IdentityProviderSettings("windows", IdentityProviderKind.Windows, "Windows sign-in", domains, null, null, null));
        Assert.DoesNotContain(issues, i => i.Blocking);
        return tenant;
    }

    private static async Task<Guid> InviteAsync(TestApp app, Guid tenant, string name, string email, string? account, bool grantRole = true)
    {
        await using var s = app.ScopeFor(tenant);
        var users = s.Get<IUserService>();
        var id = await users.InviteAsync(name, email);
        if (account is not null) await s.Get<IWindowsAccounts>().SetInvitedAccountAsync(id, account, "scenario");
        if (grantRole)
            await s.Get<IRoleAssignmentService>().GrantTrustedAsync(new GrantRequest(id, RoleCodes.TenantAdministrator, null, ScopeSet.Tenant("Internal")), AssignmentSource.Demo, "scenario");
        return id;
    }

    private static async Task<LoginResolution> SignInAsync(TestApp app, Guid tenant, string account, string sid)
    {
        await using var s = app.ScopeFor(tenant);
        return await s.Get<IWindowsSignIn>().ResolveAsync(new WindowsLogin(account, sid));
    }

    private static async Task<List<Ecp.Audit.Domain.AuditEvent>> AuditAsync(TestApp app, Guid tenant, string action)
    {
        await using var s = app.ScopeFor(tenant);
        return await s.Get<AuditDbContext>().Events.Where(e => e.Action == action).ToListAsync();
    }

    public static async Task An_invited_active_person_with_access_signs_in_and_the_account_is_linked_to_their_SID(TestApp app)
    {
        var (t, _) = await app.ProvisionAsync("Tenant W1", "WA1"); await EnableAsync(app, t);
        var id = await InviteAsync(app, t, "Alex Admin", "alex@example.org", "dom\\alex");
        var r = await SignInAsync(app, t, "DOM\\Alex", Sid1);
        Assert.True(r.Succeeded); Assert.Equal(id, r.UserId);
        await using (var s = app.ScopeFor(t))
        {
            var state = await s.Get<IWindowsAccounts>().GetAsync(id);
            Assert.True(state!.Linked);
            Assert.Equal(UserStatus.Active, (await s.Get<IdentityDbContext>().Users.SingleAsync(u => u.Id == id)).Status);
        }
        // Later sign-ins match by SID: the account can be renamed in the directory and the person still signs in.
        Assert.True((await SignInAsync(app, t, "DOM\\Alex.Renamed", Sid1)).Succeeded);
    }

    public static async Task An_uninvited_windows_account_is_refused_and_nothing_is_created(TestApp app)
    {
        var (t, _) = await app.ProvisionAsync("Tenant W2", "WA2"); await EnableAsync(app, t);
        await InviteAsync(app, t, "Someone Else", "else@example.org", "DOM\\else");
        var before = await CountUsersAsync(app, t);
        var r = await SignInAsync(app, t, "DOM\\stranger", Sid2);
        Assert.False(r.Succeeded); Assert.Equal("not_invited", r.DenialCode);
        Assert.Equal(before, await CountUsersAsync(app, t));
        var refused = Assert.Single(await AuditAsync(app, t, "auth.signin_refused"));
        Assert.Equal("not_invited", refused.Reason);
        Assert.Contains("DOM\\STRANGER", refused.EntityId);
        Assert.False(string.IsNullOrEmpty(refused.CorrelationId));
    }

    private static async Task<int> CountUsersAsync(TestApp app, Guid t) { await using var s = app.ScopeFor(t); return await s.Get<IdentityDbContext>().Users.CountAsync(); }

    public static async Task Email_alone_never_links_a_windows_account(TestApp app)
    {
        var (t, _) = await app.ProvisionAsync("Tenant W3", "WA3"); await EnableAsync(app, t);
        await InviteAsync(app, t, "Email Only", "email.only@example.org", account: null);   // invited, but no Windows account named
        // An account whose name looks like the invited email's local part is still not that person.
        var r = await SignInAsync(app, t, "DOM\\email.only", Sid1);
        Assert.False(r.Succeeded); Assert.Equal("not_invited", r.DenialCode);
    }

    public static async Task An_invited_person_without_effective_access_is_refused_and_not_linked(TestApp app)
    {
        var (t, _) = await app.ProvisionAsync("Tenant W4", "WA4"); await EnableAsync(app, t);
        var id = await InviteAsync(app, t, "No Role", "norole@example.org", "DOM\\norole", grantRole: false);
        var r = await SignInAsync(app, t, "DOM\\norole", Sid1);
        Assert.False(r.Succeeded); Assert.Equal("no_access", r.DenialCode);
        await using var s = app.ScopeFor(t);
        Assert.False((await s.Get<IWindowsAccounts>().GetAsync(id))!.Linked);   // a refusal changes nothing about the person
        Assert.Equal(UserStatus.Invited, (await s.Get<IdentityDbContext>().Users.SingleAsync(u => u.Id == id)).Status);
        Assert.Single(await AuditAsync(app, t, "auth.signin_refused"));
    }

    public static async Task A_disabled_person_is_refused(TestApp app)
    {
        var (t, _) = await app.ProvisionAsync("Tenant W5", "WA5"); await EnableAsync(app, t);
        await InviteAsync(app, t, "Keeper", "keeper@example.org", "DOM\\keeper");   // a second administrator so the first can be disabled
        var id = await InviteAsync(app, t, "Leaver", "leaver@example.org", "DOM\\leaver");
        Assert.True((await SignInAsync(app, t, "DOM\\leaver", Sid1)).Succeeded);
        await using (var s = app.ScopeFor(t)) await s.Get<IUserService>().DisableAsync(id, "left");
        var r = await SignInAsync(app, t, "DOM\\leaver", Sid1);
        Assert.False(r.Succeeded); Assert.Equal("user_disabled", r.DenialCode);
    }

    public static async Task A_linked_account_name_cannot_be_claimed_by_a_different_SID(TestApp app)
    {
        var (t, _) = await app.ProvisionAsync("Tenant W6", "WA6"); await EnableAsync(app, t);
        await InviteAsync(app, t, "Real Person", "real@example.org", "DOM\\real");
        Assert.True((await SignInAsync(app, t, "DOM\\real", Sid1)).Succeeded);
        // The directory account was deleted and re-created: same name, new SID. It must not inherit the access.
        var r = await SignInAsync(app, t, "DOM\\real", Sid2);
        Assert.False(r.Succeeded); Assert.Equal("not_invited", r.DenialCode);
    }

    public static async Task An_invitation_that_names_an_account_is_not_hijacked_before_the_first_sign_in(TestApp app)
    {
        var (t, _) = await app.ProvisionAsync("Tenant W7", "WA7"); await EnableAsync(app, t);
        await InviteAsync(app, t, "First", "first@example.org", "DOM\\shared");
        await using var s = app.ScopeFor(t);
        var other = await s.Get<IUserService>().InviteAsync("Second", "second@example.org");
        var ex = await Assert.ThrowsAsync<DomainException>(() => s.Get<IWindowsAccounts>().SetInvitedAccountAsync(other, "dom\\SHARED", "scenario"));
        Assert.Equal("windows_account_taken", ex.Code);
    }

    public static async Task Windows_authentication_grants_no_role_and_changes_no_existing_access(TestApp app)
    {
        var (t, org) = await app.ProvisionAsync("Tenant W8", "WA8"); await EnableAsync(app, t);
        var id = await InviteAsync(app, t, "Scoped", "scoped@example.org", "DOM\\scoped", grantRole: false);
        await using (var s = app.ScopeFor(t))
            await s.Get<IRoleAssignmentService>().GrantTrustedAsync(new GrantRequest(id, RoleCodes.Legal, null, ScopeSet.ForOrganisations([org], "Restricted")), AssignmentSource.Demo, "scenario");
        List<string> Roles(IEnumerable<RoleAssignment> a) => a.Select(x => x.RoleCode).OrderBy(x => x, StringComparer.Ordinal).ToList();
        List<string> before, after;
        await using (var s = app.ScopeFor(t)) before = Roles(await s.Get<IdentityDbContext>().RoleAssignments.Where(a => a.UserId == id).ToListAsync());
        Assert.True((await SignInAsync(app, t, "DOM\\scoped", Sid1)).Succeeded);
        await using (var s = app.ScopeFor(t)) after = Roles(await s.Get<IdentityDbContext>().RoleAssignments.Where(a => a.UserId == id).ToListAsync());
        Assert.Equal(before, after);
        Assert.Equal([RoleCodes.Legal], after);
    }

    public static async Task Domain_restrictions_and_unusable_identities_are_refused(TestApp app)
    {
        var (t, _) = await app.ProvisionAsync("Tenant W9", "WA9"); await EnableAsync(app, t, domains: "dom");
        await InviteAsync(app, t, "Dom Person", "dom@example.org", "DOM\\person");
        Assert.Equal("domain_not_allowed", (await SignInAsync(app, t, "OTHER\\person", Sid1)).DenialCode);
        Assert.Equal("account_unreadable", (await SignInAsync(app, t, "DOM\\person", "S-1-5-7")).DenialCode);                                  // anonymous
        Assert.Equal("account_unreadable", (await SignInAsync(app, t, "DOM\\person", "S-1-5-21-1-2-3-501")).DenialCode);                       // built-in Guest
        Assert.Equal("account_unreadable", (await SignInAsync(app, t, "not-an-account", Sid1)).DenialCode);
        Assert.Equal("account_unreadable", (await SignInAsync(app, t, "DOM\\person", "not-a-sid")).DenialCode);
        Assert.True((await SignInAsync(app, t, "DOM\\person", Sid1)).Succeeded);
    }

    public static async Task Windows_sign_in_is_refused_when_the_provider_is_not_enabled_and_is_tenant_isolated(TestApp app)
    {
        var (a, _) = await app.ProvisionAsync("Tenant WA", "WAA"); await EnableAsync(app, a);
        var (b, _) = await app.ProvisionAsync("Tenant WB", "WAB");   // never enabled
        await InviteAsync(app, a, "In A", "ina@example.org", "DOM\\ina");
        await InviteAsync(app, b, "In B", "inb@example.org", "DOM\\inb");
        Assert.Equal("windows_not_enabled", (await SignInAsync(app, b, "DOM\\inb", Sid1)).DenialCode);
        Assert.Equal("not_invited", (await SignInAsync(app, a, "DOM\\inb", Sid1)).DenialCode);   // B's invitation is invisible in A
    }

    public static async Task The_first_administrator_named_at_setup_signs_in_with_their_windows_account(TestApp app)
    {
        var (t, _) = await app.ProvisionAsync("Tenant WF", "WAF"); await EnableAsync(app, t);
        // What the setup wizard's administrator step does: invite, record the Windows account, grant the administrator role (trusted, source Setup).
        Guid id;
        await using (var s = app.ScopeFor(t))
        {
            id = await s.Get<IUserService>().InviteAsync("First Administrator", "admin@example.org");
            await s.Get<IWindowsAccounts>().SetInvitedAccountAsync(id, "DOM\\setup.admin", "setup");
            await s.Get<IRoleAssignmentService>().GrantTrustedAsync(new GrantRequest(id, RoleCodes.TenantAdministrator, null, ScopeSet.Tenant("Internal"),
                Reason: "Initial administrator named during setup", ExplicitHighCeiling: true), AssignmentSource.Setup, "setup:test", null);
        }
        var r = await SignInAsync(app, t, "DOM\\setup.admin", Sid1);
        Assert.True(r.Succeeded); Assert.Equal(id, r.UserId);
        Assert.Contains(await AuditAsync(app, t, "user.activated"), e => e.EntityId == id.ToString());
    }
}
