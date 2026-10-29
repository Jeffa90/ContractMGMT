using System.Text.Json;
using Ecp.Audit;
using Ecp.Configuration;
using Ecp.Identity;
using Ecp.Identity.Domain;
using Ecp.Setup;
using Ecp.SharedKernel;
using Ecp.Tenancy;
using Ecp.Tests.Common;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecp.Integration.Tests;

/// <summary>Scenario bodies for the first-run setup wizard, shared by the SQLite and SQL Server runs.</summary>
internal static class SetupScenarios
{
    public const string AdminEmail = "first.admin@setup.example.invalid";
    public const string AdminName = "First Administrator";
    public const string SecretRef = "env:ECP_SETUP_TEST_SECRET_UNSET";
    public static readonly SetupActor Actor = new("setup session");

    public static Dictionary<string, string> Inputs(string stage) => stage switch
    {
        "mode" => new() { ["deployment"] = "Local", ["tenancyMode"] = "SingleOrganisation", ["acknowledge"] = "true" },
        "tenant" => new() { ["tenantName"] = "Setup Test Tenant" },
        "pack" => new() { ["packId"] = "blank" },
        "identity" => new() { ["kind"] = "Oidc", ["key"] = "corp-sso", ["displayName"] = "Company sign-in", ["authority"] = "https://idp.example.invalid/", ["clientId"] = "ecp-setup-test", ["clientSecretRef"] = SecretRef, ["groupsClaim"] = "groups" },
        "admin" => new() { ["displayName"] = AdminName, ["email"] = AdminEmail, ["installationAdmin"] = "true" },
        "organisation" => new() { ["organisationName"] = "Setup Test Organisation", ["culture"] = "en-AU", ["timeZone"] = "Australia/Brisbane", ["currency"] = "AUD", ["referencePrefix"] = "STO" },
        "branding" => new() { ["productName"] = "Setup Test Platform", ["accentColour"] = "#1f3a8a", ["footerText"] = "Synthetic installation" },
        "classification" => new() { ["levels"] = "Public\nInternal\nConfidential\nRestricted", ["defaultLevel"] = "Internal" },
        "notifications" => new() { ["provider"] = "Mock", ["from"] = "platform@setup.example.invalid", ["testRecipient"] = AdminEmail },
        _ => []
    };

    public static async Task<StageResult> RunAsync(ISetupService svc, string stage, Dictionary<string, string>? values = null, bool amending = false)
        => await svc.ExecuteAsync(stage, values ?? Inputs(stage), Actor, amending);

    public static async Task MustAsync(ISetupService svc, string stage, Dictionary<string, string>? values = null)
    {
        var r = await RunAsync(svc, stage, values);
        Assert.True(r.Succeeded, $"Stage '{stage}' failed: {r.Message} {string.Join(" ", r.Details)}");
    }

    public static async Task WalkAsync(ISetupService svc, params string[] stages)
    {
        foreach (var s in stages) await MustAsync(svc, s);
    }

    public static readonly string[] BeforeReadiness = ["mode", "database", "storage", "tenant", "pack", "identity", "admin", "organisation", "branding", "classification", "notifications"];

    public static async Task AcknowledgeAndRunReadinessAsync(ISetupService svc)
    {
        var first = await RunAsync(svc, "readiness");
        if (first.Succeeded) return;
        var warnings = (await svc.RunChecksAsync()).Where(c => c.Status == "Fail" && c.Severity == "Warning").ToList();
        Assert.NotEmpty(warnings);
        var values = warnings.ToDictionary(w => "ack." + w.Id, w => "Accepted for a synthetic test installation.");
        await MustAsync(svc, "readiness", values);
    }

    public static async Task CompleteInstallationAsync(ISetupService svc)
    {
        await WalkAsync(svc, BeforeReadiness);
        Assert.True((await RunAsync(svc, "demo")).Succeeded is false); // the test host has no demonstration data applier
        Assert.True((await svc.SkipAsync("demo", Actor)).Succeeded);
        await AcknowledgeAndRunReadinessAsync(svc);
        await MustAsync(svc, "complete");
    }

    public static async Task<string> OpenSessionAsync(ISetupService svc)
    {
        var token = await svc.IssueTokenAsync();
        var session = await svc.BeginSessionAsync(token, fromLoopback: true);
        Assert.True(session.Granted, session.Refusal);
        return session.SessionId!;
    }

    // --- scenarios -----------------------------------------------------------------------------------------------------------------------

    public static async Task A_full_installation_completes_and_leaves_a_consistent_system(TestApp app)
    {
        var svc = app.Services.GetRequiredService<ISetupService>();
        Assert.False(await svc.IsCompleteAsync());
        await CompleteInstallationAsync(svc);
        Assert.True(await svc.IsCompleteAsync());

        var snapshot = await svc.SnapshotAsync();
        Assert.True(snapshot.IsComplete);
        var tenantId = snapshot.TenantId!.Value;
        await using var scope = app.ScopeFor(tenantId);

        // the tenant, its organisation and its published configuration exist
        Assert.NotNull(await scope.Get<ITenantDirectory>().FindAsync(tenantId));
        var orgs = await scope.Get<IOrganisationStructureService>().ListOrganisationsAsync();
        var org = Assert.Single(orgs);
        Assert.Equal("STO", org.ReferencePrefix); Assert.Equal("Australia/Brisbane", org.TimeZoneId); Assert.Equal("en-AU", org.CultureCode);
        var configuration = scope.Get<IConfigurationService>();
        foreach (var kind in new[] { "SolutionPack", "Branding", "ClassificationPolicy" }) Assert.NotNull(await configuration.LatestReleaseAsync(kind, Guid.Empty));

        // the named administrator holds the tenant administrator role and is still only invited (they link at their first verified sign-in)
        var user = await scope.Get<IUserService>().FindByEmailAsync(AdminEmail);
        Assert.NotNull(user); Assert.Equal(UserStatus.Invited, user!.Status);
        var assignments = await scope.Get<IRoleAssignmentService>().ListForUserAsync(user.Id);
        Assert.Contains(assignments, a => a.RoleCode == RoleCodes.TenantAdministrator && a.Source == AssignmentSource.Setup);
        // ...and Organisation Administrator of the first organisation, so that organisation onboarding can start (tenant administrators hold tenant-level configuration rights only)
        Assert.Contains(assignments, a => a.RoleCode == RoleCodes.OrganisationAdministrator && a.Scope.OrganisationIds.Contains(org.Id) && a.Source == AssignmentSource.Setup);

        // the installation administrator exists in the installation scope
        await using (var system = app.ScopeFor(SystemTenant.Id))
        {
            var sys = await system.Get<IUserService>().FindByEmailAsync(AdminEmail);
            Assert.NotNull(sys);
            Assert.Contains(await system.Get<IRoleAssignmentService>().ListForUserAsync(sys!.Id), a => a.RoleCode == RoleCodes.SystemAdministrator);
        }

        // the audit chain for the tenant is intact and recorded the setup events
        var chain = await scope.Get<AuditChainVerifier>().VerifyAsync();
        Assert.True(chain.IsValid, chain.Reason);
        var events = await scope.Get<IAuditEventSource>().Events.Select(e => e.Action).ToListAsync2();
        Assert.Contains("setup.tenant.created", events); Assert.Contains("setup.completed", events); Assert.Contains("setup.organisation.created", events);
    }

    public static async Task Setup_resumes_after_a_restart_without_repeating_or_duplicating_work(TestApp app)
    {
        var first = app.Services.GetRequiredService<ISetupService>();
        await WalkAsync(first, "mode", "database", "storage", "tenant", "pack");
        var before = await first.SnapshotAsync();

        // a restart: new services, nothing in memory, same database and data directory
        var restarted = app.RestartServices().GetRequiredService<ISetupService>();
        var after = await restarted.SnapshotAsync();
        Assert.Equal(before.TenantId, after.TenantId);
        Assert.Equal(before.CompletedStages, after.CompletedStages);
        Assert.Equal("identity", after.NextStageId);
        Assert.All(new[] { "mode", "database", "storage", "tenant", "pack" }, id => Assert.Equal(StageStatus.Complete, after.Stages.Single(s => s.Id == id).Status));

        // continuing on the restarted instance finishes the installation, and still only one tenant exists
        await WalkAsync(restarted, "identity", "admin", "organisation", "branding", "classification", "notifications");
        Assert.True((await restarted.SkipAsync("demo", Actor)).Succeeded);
        await AcknowledgeAndRunReadinessAsync(restarted);
        await MustAsync(restarted, "complete");
        await using var scope = app.Services.CreateAsyncScope();
        var tenants = await scope.ServiceProvider.GetRequiredService<ITenantDirectory>().ListAsync();
        Assert.Single(tenants, t => t.Name == "Setup Test Tenant");

        // and a further restart sees an installation that is complete
        Assert.True(await app.RestartServices().GetRequiredService<ISetupService>().IsCompleteAsync());
    }

    public static async Task The_database_copy_restores_state_when_the_bootstrap_file_is_lost(TestApp app)
    {
        var svc = app.Services.GetRequiredService<ISetupService>();
        await WalkAsync(svc, "mode", "database", "tenant");
        var file = Path.Combine(app.DocumentRoot, "bootstrap", "setup.state");
        Assert.True(File.Exists(file));
        File.Delete(file);
        var restarted = app.RestartServices().GetRequiredService<ISetupService>();
        var snapshot = await restarted.SnapshotAsync();
        Assert.NotNull(snapshot.TenantId);
        Assert.Equal(StageStatus.Complete, snapshot.Stages.Single(s => s.Id == "tenant").Status);
    }

    public static async Task Every_configuring_stage_is_idempotent(TestApp app)
    {
        var svc = app.Services.GetRequiredService<ISetupService>();
        await WalkAsync(svc, BeforeReadiness);
        // run each configuring stage a second time with the same input: success, and nothing is duplicated
        foreach (var stage in new[] { "mode", "database", "storage", "tenant", "pack", "identity", "admin", "organisation", "branding", "classification", "notifications" })
        {
            var again = await RunAsync(svc, stage);
            Assert.True(again.Succeeded, $"{stage} again: {again.Message}");
        }
        var tenantId = (await svc.SnapshotAsync()).TenantId!.Value;
        await using var scope = app.ScopeFor(tenantId);
        Assert.Single(await scope.Get<IOrganisationStructureService>().ListOrganisationsAsync());
        Assert.Single(await scope.Get<IUserService>().ListAsync());
        Assert.Single(await scope.Get<IRoleAssignmentService>().ListAsync(activeOnly: false), a => a.RoleCode == RoleCodes.TenantAdministrator);
        var configuration = scope.Get<IConfigurationService>();
        Assert.Equal(1, (await configuration.LatestReleaseAsync("Branding", Guid.Empty))!.Number);
        Assert.Equal(1, (await configuration.LatestReleaseAsync("ClassificationPolicy", Guid.Empty))!.Number);
        Assert.Equal(1, (await configuration.LatestReleaseAsync("SolutionPack", Guid.Empty))!.Number);
        Assert.Single((await scope.Get<IProviderRegistry>().ListAsync()));
        await using var all = app.Services.CreateAsyncScope();
        Assert.Single(await all.ServiceProvider.GetRequiredService<ITenantDirectory>().ListAsync());
    }

    public static async Task Stages_cannot_run_before_the_stages_they_depend_on(TestApp app)
    {
        var svc = app.Services.GetRequiredService<ISetupService>();
        var early = await RunAsync(svc, "organisation");
        Assert.False(early.Succeeded); Assert.Contains("Complete first", early.Message);
        Assert.False((await RunAsync(svc, "tenant")).Succeeded); // needs mode and database
        await WalkAsync(svc, "mode", "database", "tenant");
        Assert.False((await RunAsync(svc, "admin")).Succeeded);  // needs the identity provider
        Assert.False((await RunAsync(svc, "complete")).Succeeded);
        Assert.False((await RunAsync(svc, "nonsense")).Succeeded);
    }

    public static async Task Invalid_input_is_refused_with_a_message_and_changes_nothing(TestApp app)
    {
        var svc = app.Services.GetRequiredService<ISetupService>();
        await WalkAsync(svc, "mode", "database", "tenant", "pack");
        var badIdentity = Inputs("identity"); badIdentity["authority"] = "http://idp.example.invalid/"; // not HTTPS
        var r = await RunAsync(svc, "identity", badIdentity);
        Assert.False(r.Succeeded);
        var literal = Inputs("identity"); literal["clientSecretRef"] = "super-secret-value-123"; // a literal secret, not a reference
        Assert.False((await RunAsync(svc, "identity", literal)).Succeeded);
        var snapshot = await svc.SnapshotAsync();
        Assert.NotEqual(StageStatus.Complete, snapshot.Stages.Single(s => s.Id == "identity").Status);
        Assert.DoesNotContain("super-secret-value-123", r.Message + string.Join(" ", r.Details));
        Assert.False((await RunAsync(svc, "mode", new() { ["deployment"] = "Local", ["tenancyMode"] = "SingleOrganisation" })).Succeeded); // acknowledgement missing
        Assert.False((await RunAsync(svc, "mode", new() { ["deployment"] = "Mainframe", ["tenancyMode"] = "SingleOrganisation", ["acknowledge"] = "true" })).Succeeded);
    }

    public static async Task The_token_is_single_use_expires_and_only_one_session_can_be_open(TestApp app)
    {
        var svc = app.Services.GetRequiredService<ISetupService>();
        var token = await svc.IssueTokenAsync();

        Assert.False((await svc.BeginSessionAsync("not-the-token", true)).Granted);
        Assert.False((await svc.BeginSessionAsync(token, fromLoopback: false)).Granted);          // remote setup is not allowed by default
        var session = await svc.BeginSessionAsync(token, true);
        Assert.True(session.Granted);
        Assert.False((await svc.BeginSessionAsync(token, true)).Granted);                           // single use
        Assert.True(await svc.ValidateSessionAsync(session.SessionId));
        Assert.False(await svc.ValidateSessionAsync("a-different-session"));
        Assert.False(await svc.ValidateSessionAsync(null));

        var second = await svc.IssueTokenAsync();                                                   // issuing a token ends the open session
        Assert.False(await svc.ValidateSessionAsync(session.SessionId));
        var s2 = await svc.BeginSessionAsync(second, true);
        Assert.True(s2.Granted);
        var third = await svc.IssueTokenAsync();
        Assert.True((await svc.BeginSessionAsync(third, true)).Granted is true);                    // the earlier session was ended by the new token

        // a session ends 60 minutes after its last activity
        var token4 = await svc.IssueTokenAsync();
        var s4 = await svc.BeginSessionAsync(token4, true);
        app.Clock.Advance(TimeSpan.FromMinutes(59));
        Assert.True(await svc.ValidateSessionAsync(s4.SessionId));                                  // activity slides the expiry
        app.Clock.Advance(TimeSpan.FromMinutes(59));
        Assert.True(await svc.ValidateSessionAsync(s4.SessionId));
        app.Clock.Advance(TimeSpan.FromMinutes(61));
        Assert.False(await svc.ValidateSessionAsync(s4.SessionId));

        // a token expires after 24 hours
        var token5 = await svc.IssueTokenAsync();
        app.Clock.Advance(TimeSpan.FromHours(24).Add(TimeSpan.FromMinutes(1)));
        Assert.False((await svc.BeginSessionAsync(token5, true)).Granted);
    }

    public static async Task Repeated_wrong_tokens_lock_the_entry_for_a_while(TestApp app)
    {
        var svc = app.Services.GetRequiredService<ISetupService>();
        var token = await svc.IssueTokenAsync();
        for (var i = 0; i < 5; i++) Assert.False((await svc.BeginSessionAsync("wrong-" + i, true)).Granted);
        var locked = await svc.BeginSessionAsync(token, true);
        Assert.False(locked.Granted); Assert.Contains("Too many", locked.Refusal);
        app.Clock.Advance(TimeSpan.FromMinutes(6));
        Assert.True((await svc.BeginSessionAsync(token, true)).Granted);
    }

    public static async Task The_state_file_holds_a_token_hash_never_the_token(TestApp app)
    {
        var svc = app.Services.GetRequiredService<ISetupService>();
        var token = await svc.IssueTokenAsync();
        var session = await svc.BeginSessionAsync(token, true);
        await WalkAsync(svc, "mode", "database", "tenant", "pack");
        var raw = await File.ReadAllTextAsync(Path.Combine(app.DocumentRoot, "bootstrap", "setup.state"));
        var plain = new PlainSetupProtector().Unprotect(raw);
        Assert.DoesNotContain(token, plain);
        Assert.DoesNotContain(session.SessionId!, plain);
        var doc = JsonDocument.Parse(plain).RootElement;
        Assert.True(doc.TryGetProperty("sessionHash", out _) || doc.TryGetProperty("SessionHash", out _));
    }

    public static async Task After_completion_setup_is_closed_and_only_amendable_stages_can_be_reopened(TestApp app)
    {
        var svc = app.Services.GetRequiredService<ISetupService>();
        await CompleteInstallationAsync(svc);
        Assert.False((await svc.BeginSessionAsync("anything", true)).Granted);
        await Assert.ThrowsAsync<DomainException>(() => svc.IssueTokenAsync());
        Assert.False((await RunAsync(svc, "branding")).Succeeded);                                   // closed without the amend path
        Assert.False((await RunAsync(svc, "tenant", amending: true)).Succeeded);                      // not amendable
        Assert.False((await RunAsync(svc, "organisation", amending: true)).Succeeded);
        var changed = Inputs("branding"); changed["productName"] = "Renamed Platform";
        Assert.True((await RunAsync(svc, "branding", changed, amending: true)).Succeeded);
        var tenantId = (await svc.SnapshotAsync()).TenantId!.Value;
        await using var scope = app.ScopeFor(tenantId);
        Assert.Equal(2, (await scope.Get<IConfigurationService>().LatestReleaseAsync("Branding", Guid.Empty))!.Number);
        // closed state survives a restart
        Assert.True(await app.RestartServices().GetRequiredService<ISetupService>().IsCompleteAsync());
    }

    public static async Task The_report_contains_no_secrets_tokens_references_or_personal_addresses(TestApp app)
    {
        var svc = app.Services.GetRequiredService<ISetupService>();
        var token = await svc.IssueTokenAsync();
        var session = await svc.BeginSessionAsync(token, true);
        await CompleteInstallationAsync(svc);
        var report = await svc.BuildReportAsync();
        var json = JsonSerializer.Serialize(report);
        foreach (var forbidden in new[] { token, session.SessionId!, SecretRef, "ECP_SETUP_TEST_SECRET_UNSET", AdminEmail, app.ConnectionString, "idp.example.invalid", "Data Source", "Password=" })
            Assert.DoesNotContain(forbidden, json, StringComparison.OrdinalIgnoreCase);
        Assert.True(report.SetupComplete);
        Assert.Contains(report.Checks, c => c.Id == "R-AUD-01" && c.Status == "Pass");
        Assert.Contains(AdminName, report.Administrators);
        Assert.Contains(report.Notes, n => n.Contains("not approved for production", StringComparison.OrdinalIgnoreCase));
    }

    public static async Task Readiness_blocks_completion_until_blocking_checks_pass_and_warnings_are_acknowledged(TestApp app)
    {
        var svc = app.Services.GetRequiredService<ISetupService>();
        await WalkAsync(svc, "mode", "database", "storage", "tenant", "pack");
        Assert.False((await RunAsync(svc, "readiness")).Succeeded);                                   // dependencies unmet
        await WalkAsync(svc, "identity", "admin", "organisation", "branding", "classification", "notifications");
        var blockedByWarnings = await RunAsync(svc, "readiness");
        Assert.False(blockedByWarnings.Succeeded);
        Assert.Contains("Acknowledge", blockedByWarnings.Message);
        Assert.Contains(blockedByWarnings.Details, d => d.StartsWith("R-ID-02"));
        await AcknowledgeAndRunReadinessAsync(svc);
        var report = await svc.BuildReportAsync();
        Assert.Contains(report.Checks, c => c.Id == "R-ID-02" && c.AcknowledgedBy == "setup session");
        await MustAsync(svc, "complete");
    }

    /// <summary>UX-4: the administrator names what they will do in the organisation, and set-up issues exactly those profiles through the trusted path.</summary>
    public static async Task The_initial_administrator_gets_exactly_the_operational_profiles_they_chose(TestApp app, bool choose)
    {
        var svc = app.Services.GetRequiredService<ISetupService>();
        await WalkAsync(svc, "mode", "database", "storage", "tenant", "pack", "identity");
        var admin = Inputs("admin");
        if (choose) { admin["doManage"] = "true"; admin["doOwn"] = "true"; admin["doRead"] = "true"; }
        await MustAsync(svc, "admin", admin);
        await WalkAsync(svc, "organisation");
        await using var rootScope = app.Services.CreateAsyncScope();
        var tenant = (await rootScope.ServiceProvider.GetRequiredService<ITenantDirectory>().ListAsync()).Single();
        await using var scope = app.ScopeFor(tenant.Id);
        var user = (await scope.Get<IUserService>().FindByEmailAsync(AdminEmail))!;
        var held = (await scope.Get<IRoleAssignmentService>().ListForUserAsync(user.Id)).Select(a => a.RoleCode).OrderBy(x => x).ToList();
        if (choose)
        {
            Assert.Contains(RoleCodes.CategoryOwner, held); Assert.Contains(RoleCodes.Viewer, held);
            Assert.Contains(RoleCodes.OrganisationAdministrator, held); Assert.Contains(RoleCodes.TenantAdministrator, held);
            Assert.DoesNotContain(RoleCodes.Legal, held); Assert.DoesNotContain(RoleCodes.Approver, held);          // not chosen, not granted
        }
        else Assert.Equal(new[] { RoleCodes.OrganisationAdministrator, RoleCodes.TenantAdministrator }.OrderBy(x => x), held);   // "administer only" grants nothing else
    }

    /// <summary>UX-4: in Demo and Test the operational choices are ticked for the administrator; in Production nothing is ticked for them.</summary>
    public static async Task Operational_choices_are_ticked_in_test_and_never_in_production(TestApp app, bool ticked)
    {
        var svc = app.Services.GetRequiredService<ISetupService>();
        var stage = (await svc.DescribeAsync("admin"))!;
        string? Value(string name) => stage.Fields.Single(f => f.Name == name).Value;
        Assert.Equal(ticked ? "true" : null, Value("doManage"));
        Assert.Equal(ticked ? "true" : null, Value("doOwn"));
        Assert.Null(Value("doReview")); Assert.Null(Value("doApprove")); Assert.Null(Value("doRead"));            // never ticked for anyone
        Assert.Equal(["doManage", "doOwn", "doReview", "doApprove", "doRead"], stage.Fields.Where(f => f.Name.StartsWith("do", StringComparison.Ordinal)).Select(f => f.Name));
    }

    public static async Task Production_refuses_demonstration_features_and_unsafe_configuration(TestApp app)
    {
        // The test application is composed in Production setup mode over a database that is not reached through a secret reference.
        var svc = app.Services.GetRequiredService<ISetupService>();
        var mode = await RunAsync(svc, "mode", new() { ["deployment"] = "Container", ["tenancyMode"] = "SingleOrganisation" });
        Assert.False(mode.Succeeded);
        Assert.Contains("R-SEC-02", mode.Message);                                                       // the Production guard speaks for the host configuration

        var snapshot = await svc.SnapshotAsync();
        Assert.DoesNotContain(snapshot.Stages, s => s.Id == "demo");                                      // the demonstration step does not exist in Production
        Assert.False((await RunAsync(svc, "demo")).Succeeded);
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.AutoCompleteForDemoAsync("test"));
        Assert.False(await svc.IsCompleteAsync());
    }
}

internal static class QueryExtensions
{
    public static Task<List<T>> ToListAsync2<T>(this IQueryable<T> source) => Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(source);
}
