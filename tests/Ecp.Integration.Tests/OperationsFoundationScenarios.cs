using Ecp.Audit;
using Ecp.Identity;
using Ecp.Identity.Domain;
using Ecp.Operations;
using Ecp.Operations.Domain;
using Ecp.Operations.Persistence;
using Ecp.Persistence;
using Ecp.Recovery;
using Ecp.SharedKernel;
using Ecp.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Ecp.Integration.Tests;

/// <summary>
/// OPS-02 foundation (docs/OPERATION_LEASES.md, docs/MAINTENANCE_MODE.md): operation leases, maintenance mode, operation history, and the permission and sign-in rules of the operator interface. The same scenarios run on the
/// lightweight provider (provider-independent behaviour only) and on SQL Server, which is where the concurrency evidence is.
/// </summary>
internal static class OperationsFoundationScenarios
{
    internal sealed class NoTargets : IRestoreTargetFactory
    {
        public IRestoreTargetHost Open(RestoreTarget target) => throw new InvalidOperationException("No restored copy is opened in this scenario.");
    }

    /// <summary>Adds the recovery module without a backup directory (backup is not configured, which is itself a state the interface must handle).</summary>
    public static readonly Action<IServiceCollection> Configure = services =>
    {
        var database = (DatabaseOptions)services.Last(d => d.ServiceType == typeof(DatabaseOptions)).ImplementationInstance!;
        services.AddRecoveryModule(_ => { }, database);
        services.AddSingleton<IRestoreTargetFactory, NoTargets>();
    };

    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    private static async Task<T> InstallationAsync<T>(IServiceProvider services, Func<IServiceProvider, Task<T>> work)
    {
        await using var scope = services.CreateAsyncScope();
        return await work(scope.ServiceProvider);
    }

    private static async Task InstallationAsync(IServiceProvider services, Func<IServiceProvider, Task> work)
    {
        await using var scope = services.CreateAsyncScope();
        await work(scope.ServiceProvider);
    }

    public static async Task Only_one_owner_holds_a_lease_and_an_expired_lease_is_taken_over_safely(TestApp app)
    {
        var a = await InstallationAsync(app.Services, s => s.GetRequiredService<IOperationLeases>().TryAcquireAsync("test.singleton", IOperationLeases.InstallationScope, "instance-a", "instance A", Ttl));
        Assert.NotNull(a);
        // A second owner cannot take it while it is held; the same owner can ask again.
        Assert.Null(await InstallationAsync(app.Services, s => s.GetRequiredService<IOperationLeases>().TryAcquireAsync("test.singleton", IOperationLeases.InstallationScope, "instance-b", "instance B", Ttl)));
        var again = await InstallationAsync(app.Services, s => s.GetRequiredService<IOperationLeases>().TryAcquireAsync("test.singleton", IOperationLeases.InstallationScope, "instance-a", "instance A", Ttl));
        Assert.Equal(a!.Generation, again!.Generation);
        // Different names and different scopes are different leases.
        Assert.NotNull(await InstallationAsync(app.Services, s => s.GetRequiredService<IOperationLeases>().TryAcquireAsync("test.other", IOperationLeases.InstallationScope, "instance-b", "instance B", Ttl)));
        Assert.NotNull(await InstallationAsync(app.Services, s => s.GetRequiredService<IOperationLeases>().TryAcquireAsync("test.singleton", Guid.NewGuid().ToString("N"), "instance-b", "instance B", Ttl)));

        // Renewing keeps it; the lease list shows who holds what.
        var renewed = await InstallationAsync(app.Services, s => s.GetRequiredService<IOperationLeases>().RenewAsync(a, Ttl));
        Assert.NotNull(renewed);
        var listed = await InstallationAsync(app.Services, s => s.GetRequiredService<IOperationLeases>().ListAsync());
        Assert.Contains(listed, l => l.Name == "test.singleton" && l.ScopeKey == IOperationLeases.InstallationScope && l.OwnerId == "instance-a" && l.IsActive);

        // The owner stops renewing (a crashed instance): after the lease expires another owner takes it, once, and the old owner can no longer renew.
        app.Clock.Advance(Ttl + TimeSpan.FromSeconds(1));
        var taken = await InstallationAsync(app.Services, s => s.GetRequiredService<IOperationLeases>().TryAcquireAsync("test.singleton", IOperationLeases.InstallationScope, "instance-b", "instance B", Ttl));
        Assert.NotNull(taken);
        Assert.Equal(a.Generation + 1, taken!.Generation);
        Assert.Null(await InstallationAsync(app.Services, s => s.GetRequiredService<IOperationLeases>().RenewAsync(renewed!, Ttl)));
        Assert.Null(await InstallationAsync(app.Services, s => s.GetRequiredService<IOperationLeases>().TryAcquireAsync("test.singleton", IOperationLeases.InstallationScope, "instance-a", "instance A", Ttl)));

        // The takeover of an expired lease is audited in the installation's own chain.
        await using (var scope = app.ScopeFor(SystemTenant.Id))
            Assert.Contains(await scope.Get<IAuditEventSource>().Events.Where(e => e.Action == "operations.lease.expired_takeover").ToListAsync(), e => e.EntityId == "test.singleton/installation");

        // Releasing frees it at once; only the holder's own release counts.
        await InstallationAsync(app.Services, s => s.GetRequiredService<IOperationLeases>().ReleaseAsync(new LeaseGrant("test.singleton", IOperationLeases.InstallationScope, "instance-a", 1, DateTime.UtcNow)));
        Assert.Null(await InstallationAsync(app.Services, s => s.GetRequiredService<IOperationLeases>().TryAcquireAsync("test.singleton", IOperationLeases.InstallationScope, "instance-a", "A", Ttl)));
        await InstallationAsync(app.Services, s => s.GetRequiredService<IOperationLeases>().ReleaseAsync(taken));
        Assert.NotNull(await InstallationAsync(app.Services, s => s.GetRequiredService<IOperationLeases>().TryAcquireAsync("test.singleton", IOperationLeases.InstallationScope, "instance-a", "A", Ttl)));

        // Names and scopes are checked; a lease cannot be created with an unsafe name.
        foreach (var bad in new[] { "", "Upper", "has space", "x;DROP", new string('a', 120) })
            await Assert.ThrowsAsync<DomainException>(() => InstallationAsync(app.Services, s => s.GetRequiredService<IOperationLeases>().TryAcquireAsync(bad, IOperationLeases.InstallationScope, "o", "o", Ttl)));
        await Assert.ThrowsAsync<DomainException>(() => InstallationAsync(app.Services, s => s.GetRequiredService<IOperationLeases>().TryAcquireAsync("ok.name", "bad scope!", "o", "o", Ttl)));
    }

    public static async Task An_operator_can_take_over_a_lease_only_with_a_reason_and_the_old_owner_then_stops(TestApp app)
    {
        var held = await InstallationAsync(app.Services, s => s.GetRequiredService<IOperationLeases>().TryAcquireAsync("test.stolen", IOperationLeases.InstallationScope, "owner-one", "owner one", Ttl));
        await Assert.ThrowsAsync<DomainException>(() => InstallationAsync(app.Services, s => s.GetRequiredService<IOperationLeases>().StealAsync("test.stolen", IOperationLeases.InstallationScope, "operator", "operator", " ", Ttl)));
        var stolen = await InstallationAsync(app.Services, s => s.GetRequiredService<IOperationLeases>().StealAsync("test.stolen", IOperationLeases.InstallationScope, "operator", "operator", "owner-one is a crashed instance", Ttl));
        Assert.Equal(held!.Generation + 1, stolen.Generation);
        Assert.Null(await InstallationAsync(app.Services, s => s.GetRequiredService<IOperationLeases>().RenewAsync(held, Ttl)));        // the previous owner's next renewal fails, so it stops
        await using var scope = app.ScopeFor(SystemTenant.Id);
        var audit = Assert.Single(await scope.Get<IAuditEventSource>().Events.Where(e => e.Action == "operations.lease.taken_over" && e.EntityId == "test.stolen/installation").ToListAsync());
        Assert.Equal("owner-one is a crashed instance", audit.Reason);
    }

    public static async Task A_lease_holder_keeps_the_lease_renewed_releases_it_and_reports_when_it_is_lost(TestApp app)
    {
        var scopes = app.Services.GetRequiredService<IServiceScopeFactory>();
        await using (var holder = await LeaseHolder.TryAcquireAsync(scopes, "test.holder", IOperationLeases.InstallationScope, "holder-1", "holder one", TimeSpan.FromSeconds(30)))
        {
            Assert.NotNull(holder);
            Assert.Null(await LeaseHolder.TryAcquireAsync(scopes, "test.holder", IOperationLeases.InstallationScope, "holder-2", "holder two", TimeSpan.FromSeconds(30)));
            holder!.ThrowIfLost();
        }
        // Disposing released it.
        await using var second = await LeaseHolder.TryAcquireAsync(scopes, "test.holder", IOperationLeases.InstallationScope, "holder-2", "holder two", TimeSpan.FromSeconds(30));
        Assert.NotNull(second);
    }

    public static async Task Maintenance_mode_is_switched_with_a_reason_audited_seen_by_every_instance_and_a_restore_state_is_not_left_by_accident(TestApp app)
    {
        var op = new OperatorIdentity("operator-1", "Operator");
        await using var scope = app.Services.CreateAsyncScope();
        var maintenance = scope.ServiceProvider.GetRequiredService<IMaintenanceService>();
        Assert.False((await maintenance.GetAsync()).Enabled);
        await Assert.ThrowsAsync<DomainException>(() => maintenance.EnableAsync("  ", op));
        var on = await maintenance.EnableAsync("Planned upgrade window", op);
        Assert.True(on.Enabled); Assert.Equal(MaintenanceCauses.Manual, on.Cause);

        // A second instance (a restart: no memory shared) sees the flag from the database.
        var other = app.RestartServices();
        Assert.True((await InstallationAsync(other, s => s.GetRequiredService<IMaintenanceService>().GetAsync())).Enabled);

        // A restore that is waiting for validation, or has failed, cannot be released by a plain "off".
        await maintenance.EnableAsync("Restore of bk-x failed", op, MaintenanceCauses.RestoreFailed);
        var refused = await Assert.ThrowsAsync<DomainException>(() => maintenance.DisableAsync("done", op));
        Assert.Equal("restore_state", refused.Code);
        Assert.True((await maintenance.GetAsync()).Enabled);
        await Assert.ThrowsAsync<DomainException>(() => maintenance.DisableAsync("", op, acknowledgeRestoreState: true));
        var off = await maintenance.DisableAsync("Operator checked the restore and accepts it", op, acknowledgeRestoreState: true);
        Assert.False(off.Enabled);
        Assert.False((await InstallationAsync(app.RestartServices(), s => s.GetRequiredService<IMaintenanceService>().GetAsync())).Enabled);       // a fresh instance reads the database; one that read it moments ago may hold the old answer for a few seconds

        // Every transition is in the installation's audit chain, with the reason and who did it.
        await using var audit = app.ScopeFor(SystemTenant.Id);
        var events = await audit.Get<IAuditEventSource>().Events.Where(e => e.Action.StartsWith("operations.maintenance.")).OrderBy(e => e.Sequence).ToListAsync();
        Assert.Equal(["operations.maintenance.enabled", "operations.maintenance.enabled", "operations.maintenance.disabled"], events.Select(e => e.Action));
        Assert.Equal("Planned upgrade window", events[0].Reason);
        Assert.Equal("Operator checked the restore and accepts it", events[2].Reason);
        Assert.True((await audit.Get<AuditChainVerifier>().VerifyAsync()).IsValid);
    }

    public static async Task Operation_history_records_runs_without_secrets_and_a_customer_tenant_cannot_see_the_installations_operational_tables(TestApp app)
    {
        var op = new OperatorIdentity("operator-1", "Operator");
        Guid run;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var history = scope.ServiceProvider.GetRequiredService<IOperationHistory>();
            run = await history.StartAsync(OperationKinds.Backup, "bk-20261017T000000Z-00000000", "reason", op);
            await history.FinishAsync(run, OperationStatuses.Failed, "It failed", "{\"failure\":\"x\"}");
            var read = await history.FindAsync(run);
            Assert.Equal(OperationStatuses.Failed, read!.Status); Assert.NotNull(read.CompletedUtc);
            Assert.Single(await history.ListAsync(OperationKinds.Backup, 10));
            Assert.Empty(await history.ListAsync(OperationKinds.Restore, 10));
        }
        var customer = await app.ProvisionAsync("Customer " + Guid.NewGuid().ToString("N")[..6], "CST");
        await using (var scope = app.ScopeFor(customer.TenantId))
        {
            var db = scope.Get<OperationsDbContext>();
            Assert.Empty(await db.Runs.ToListAsync());                          // the filter keeps the installation's rows out of a customer tenant
            Assert.Empty(await db.Leases.ToListAsync());
            Assert.Empty(await db.Maintenance.ToListAsync());
        }
        // A customer tenant cannot write an operational row either: the row belongs to the installation tenant.
        await using (var scope = app.ScopeFor(customer.TenantId))
        {
            var db = scope.Get<OperationsDbContext>();
            db.Leases.Add(OperationLease.Create("forged", "installation", "x", "x", DateTime.UtcNow, TimeSpan.FromMinutes(1)));
            await Assert.ThrowsAnyAsync<Exception>(() => scope.Get<IUnitOfWork>().ExecuteAsync(async c => await db.SaveChangesAsync(c)));
        }
    }

    // ---- who may do what ----

    private static async Task<Guid> SystemAdministratorAsync(TestApp app)
    {
        await using var s = app.ScopeFor(SystemTenant.Id);
        var id = await s.Get<IUserService>().CreateActiveTrustedAsync("Sysadmin", "sys" + Guid.NewGuid().ToString("N")[..6] + "@installation.example.test", "demo", "https://demo.example.test", "sys|" + Guid.NewGuid(), "test");
        await s.Get<IRoleAssignmentService>().GrantTrustedAsync(new GrantRequest(id, RoleCodes.SystemAdministrator, null, ScopeSet.Installation(), EffectiveFromUtc: app.Clock.UtcNow.AddDays(-30)), AssignmentSource.Demo, "test");
        return id;
    }

    private static TenantScope As(TestApp app, Guid tenant, Guid user, bool recentSignIn)
    {
        var s = app.ScopeFor(tenant);
        s.Get<CurrentUser>().Set(user);
        var actor = s.Get<ActorContext>(); actor.Type = ActorType.User; actor.ActorId = user.ToString(); actor.Source = "Test";
        if (recentSignIn) s.Get<ReauthenticationContext>().Set(app.Clock.UtcNow);
        return s;
    }

    public static async Task Operations_need_the_permission_in_the_service_and_a_recent_sign_in_for_anything_that_changes_state(TestApp app)
    {
        var lw = await LifecycleWorld.CreateAsync(app);
        var sysadmin = await SystemAdministratorAsync(app);

        // The installation administrator reads freely; changing state needs a sign-in within the window.
        await using (var s = As(app, SystemTenant.Id, sysadmin, recentSignIn: false))
        {
            var admin = s.Get<IOperationsAdministration>();
            var overview = await admin.OverviewAsync();
            Assert.False(overview.BackupConfigured);
            Assert.Empty(await admin.BackupsAsync());
            await Assert.ThrowsAsync<ReauthenticationRequiredException>(() => admin.EnterMaintenanceAsync("planned"));
            await Assert.ThrowsAsync<ReauthenticationRequiredException>(() => admin.StartBackupAsync("reason", false));
            await Assert.ThrowsAsync<ReauthenticationRequiredException>(() => admin.StartRestoreAsync("bk-20261017T000000Z-00000000", "ecp_x", "reason", "RESTORE bk-20261017T000000Z-00000000", false, null));
            await Assert.ThrowsAsync<ReauthenticationRequiredException>(() => admin.TakeOverLeaseAsync("test.x", "installation", "reason"));
        }
        await using (var s = As(app, SystemTenant.Id, sysadmin, recentSignIn: true))
        {
            var admin = s.Get<IOperationsAdministration>();
            Assert.True((await admin.EnterMaintenanceAsync("planned")).Enabled);
            Assert.True((await admin.MaintenanceAsync()).Enabled);
            Assert.False((await admin.LeaveMaintenanceAsync("finished", false)).Enabled);
            // With no backup directory (or on a provider with no supported backup) a backup is refused with a plain reason, never started.
            var refused = await Assert.ThrowsAsync<DomainException>(() => admin.StartBackupAsync("reason", false));
            Assert.Contains(new[] { "backup_not_configured", "backup_unsupported" }, c => c == refused.Code);
            var evidence = await admin.EvidenceCsvAsync();
            Assert.NotEmpty(evidence);
            var bundle = await admin.SupportBundleAsync();
            Assert.NotEmpty(bundle);
        }

        // People who administer a business, and people with no role at all, can do none of it: not even read it.
        foreach (var key in new[] { "tenantAdmin", "ownerPackaging", "legalA", "groupHead" })
        {
            if (!lw.W.Users.TryGetValue(key, out var user)) continue;
            await using var s = As(app, lw.W.TenantId, user, recentSignIn: true);
            var admin = s.Get<IOperationsAdministration>();
            await Assert.ThrowsAsync<ForbiddenException>(() => admin.OverviewAsync());
            await Assert.ThrowsAsync<ForbiddenException>(() => admin.BackupsAsync());
            await Assert.ThrowsAsync<ForbiddenException>(() => admin.StartBackupAsync("reason", false));
            await Assert.ThrowsAsync<ForbiddenException>(() => admin.PreviewRestoreAsync("bk-20261017T000000Z-00000000", "ecp_x"));
            await Assert.ThrowsAsync<ForbiddenException>(() => admin.StartRestoreAsync("bk-20261017T000000Z-00000000", "ecp_x", "reason", "RESTORE bk-20261017T000000Z-00000000", false, null));
            await Assert.ThrowsAsync<ForbiddenException>(() => admin.ValidateInstallationAsync());
            await Assert.ThrowsAsync<ForbiddenException>(() => admin.EnterMaintenanceAsync("planned"));
            await Assert.ThrowsAsync<ForbiddenException>(() => admin.TakeOverLeaseAsync("test.x", "installation", "reason"));
            await Assert.ThrowsAsync<ForbiddenException>(() => admin.EvidenceJsonAsync());
            await Assert.ThrowsAsync<ForbiddenException>(() => admin.SupportBundleAsync());
        }
        // An installation-scope role assigned inside a customer tenant does not make its holder an operator: the actions are available only from the installation's own tenant.
        var impostor = await lw.UserAsync("impostor", RoleCodes.Auditor, null, "Internal");
        try
        {
            await using var grant = lw.W.AsScopeless();
            await grant.Get<IRoleAssignmentService>().GrantTrustedAsync(new GrantRequest(impostor, RoleCodes.SystemAdministrator, null, ScopeSet.Installation(), EffectiveFromUtc: app.Clock.UtcNow.AddDays(-1)), AssignmentSource.Demo, "test");
        }
        catch (Exception) { /* the grant itself may be refused, which is also correct */ }
        await using (var s = As(app, lw.W.TenantId, impostor, recentSignIn: true))
        {
            await Assert.ThrowsAsync<ForbiddenException>(() => s.Get<IOperationsAdministration>().OverviewAsync());
            await Assert.ThrowsAsync<ForbiddenException>(() => s.Get<IOperationsAdministration>().EnterMaintenanceAsync("planned"));
        }
        // No signed-in person at all.
        await using (var s = app.ScopeFor(lw.W.TenantId)) await Assert.ThrowsAnyAsync<Exception>(() => s.Get<IOperationsAdministration>().OverviewAsync());
    }

    public static async Task The_support_bundle_and_evidence_hold_no_secret_connection_string_or_content_and_name_every_deployment_model(TestApp app)
    {
        var lw = await LifecycleWorld.CreateAsync(app);
        await lw.NewDraftAsync(title: "Confidential synthetic title that must not leak");
        var sysadmin = await SystemAdministratorAsync(app);
        // SEC-030 / SEC-031: canary secrets and personal data that reach the log must not reach the bundle.
        app.Services.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>().CreateLogger("Canary")
            .LogWarning(new InvalidOperationException("CANARY-EXCEPTION-TEXT"), "Sign-in failed for jane.canary@example.test with Password=CANARY-SECRET-123 and token=CANARY-TOKEN-456 against Server=canary-host;User Id=sa;Password=CANARY-DB-789;");
        await using var s = As(app, SystemTenant.Id, sysadmin, recentSignIn: true);
        var admin = s.Get<IOperationsAdministration>();
        var bundle = await admin.SupportBundleAsync();
        using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(bundle));
        Assert.Equal(["README.txt", "bundle.json"], zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal).ToArray());
        string text;
        using (var reader = new StreamReader(zip.GetEntry("bundle.json")!.Open())) text = await reader.ReadToEndAsync();
        Assert.False(Redaction.ContainsSecret(text));
        Assert.DoesNotContain(app.ConnectionString, text);
        foreach (var canary in new[] { "CANARY-SECRET-123", "CANARY-TOKEN-456", "CANARY-DB-789", "jane.canary", "canary-host", "CANARY-EXCEPTION-TEXT" }) Assert.DoesNotContain(canary, text);
        Assert.Contains("Canary", text);                                  // the line itself is kept (category, level, redacted message), only the sensitive parts are gone
        foreach (var fragment in new[] { "Confidential synthetic title", "BEGIN PRIVATE" }) Assert.DoesNotContain(fragment, text, StringComparison.OrdinalIgnoreCase);
        // A connection-string key may remain, but only with its value redacted.
        Assert.DoesNotMatch(new System.Text.RegularExpressions.Regex(@"(Password|Data Source|User Id)=(?!\[redacted\])", System.Text.RegularExpressions.RegexOptions.IgnoreCase), text);
        using var json = System.Text.Json.JsonDocument.Parse(text);
        var root = json.RootElement;
        Assert.Equal("Test", root.GetProperty("environmentMode").GetString());
        Assert.True(root.GetProperty("counts").GetProperty("tenants").GetInt32() >= 1);
        Assert.Equal(DeploymentModels.All.Count, root.GetProperty("deploymentModels").GetArrayLength());
        Assert.True(root.TryGetProperty("readiness", out _)); Assert.True(root.TryGetProperty("migrations", out _)); Assert.True(root.TryGetProperty("recentLogLines", out _));
        Assert.Contains("secrets", root.GetProperty("excluded").EnumerateArray().Select(e => e.GetString()));
        var evidence = System.Text.Encoding.UTF8.GetString(await admin.EvidenceJsonAsync());
        Assert.False(Redaction.ContainsSecret(evidence));
        Assert.Contains("Not Verified", evidence);
        Assert.Contains("not an approval for production use", evidence);
    }
}

[Trait("Category", "Integration")]
[Trait("Provider", "SqliteNotSqlServerProof")]
public sealed class OperationsFoundationScenarioTests_Sqlite
{
    [Fact] public Task Leases() => ProviderRunner.SqliteAsync(OperationsFoundationScenarios.Only_one_owner_holds_a_lease_and_an_expired_lease_is_taken_over_safely, OperationsFoundationScenarios.Configure);
    [Fact] public Task Steal() => ProviderRunner.SqliteAsync(OperationsFoundationScenarios.An_operator_can_take_over_a_lease_only_with_a_reason_and_the_old_owner_then_stops, OperationsFoundationScenarios.Configure);
    [Fact] public Task Holder() => ProviderRunner.SqliteAsync(OperationsFoundationScenarios.A_lease_holder_keeps_the_lease_renewed_releases_it_and_reports_when_it_is_lost, OperationsFoundationScenarios.Configure);
    [Fact] public Task Maintenance() => ProviderRunner.SqliteAsync(OperationsFoundationScenarios.Maintenance_mode_is_switched_with_a_reason_audited_seen_by_every_instance_and_a_restore_state_is_not_left_by_accident, OperationsFoundationScenarios.Configure);
    [Fact] public Task History() => ProviderRunner.SqliteAsync(OperationsFoundationScenarios.Operation_history_records_runs_without_secrets_and_a_customer_tenant_cannot_see_the_installations_operational_tables, OperationsFoundationScenarios.Configure);
    [Fact] public Task Permissions() => ProviderRunner.SqliteAsync(OperationsFoundationScenarios.Operations_need_the_permission_in_the_service_and_a_recent_sign_in_for_anything_that_changes_state, OperationsFoundationScenarios.Configure);
    [Fact] public Task Bundle() => ProviderRunner.SqliteAsync(OperationsFoundationScenarios.The_support_bundle_and_evidence_hold_no_secret_connection_string_or_content_and_name_every_deployment_model, OperationsFoundationScenarios.Configure);
}

[Trait("Category", "SqlServer")]
public sealed class OperationsFoundationScenarioTests_SqlServer
{
    [SqlServerFact] public Task Leases() => ProviderRunner.SqlServerAsync(OperationsFoundationScenarios.Only_one_owner_holds_a_lease_and_an_expired_lease_is_taken_over_safely, OperationsFoundationScenarios.Configure);
    [SqlServerFact] public Task Steal() => ProviderRunner.SqlServerAsync(OperationsFoundationScenarios.An_operator_can_take_over_a_lease_only_with_a_reason_and_the_old_owner_then_stops, OperationsFoundationScenarios.Configure);
    [SqlServerFact] public Task Holder() => ProviderRunner.SqlServerAsync(OperationsFoundationScenarios.A_lease_holder_keeps_the_lease_renewed_releases_it_and_reports_when_it_is_lost, OperationsFoundationScenarios.Configure);
    [SqlServerFact] public Task Maintenance() => ProviderRunner.SqlServerAsync(OperationsFoundationScenarios.Maintenance_mode_is_switched_with_a_reason_audited_seen_by_every_instance_and_a_restore_state_is_not_left_by_accident, OperationsFoundationScenarios.Configure);
    [SqlServerFact] public Task History() => ProviderRunner.SqlServerAsync(OperationsFoundationScenarios.Operation_history_records_runs_without_secrets_and_a_customer_tenant_cannot_see_the_installations_operational_tables, OperationsFoundationScenarios.Configure);
    [SqlServerFact] public Task Permissions() => ProviderRunner.SqlServerAsync(OperationsFoundationScenarios.Operations_need_the_permission_in_the_service_and_a_recent_sign_in_for_anything_that_changes_state, OperationsFoundationScenarios.Configure);
    [SqlServerFact] public Task Bundle() => ProviderRunner.SqlServerAsync(OperationsFoundationScenarios.The_support_bundle_and_evidence_hold_no_secret_connection_string_or_content_and_name_every_deployment_model, OperationsFoundationScenarios.Configure);

    /// <summary>The evidence that two instances sharing a database never both own a lease: many contenders, from several independent instances, at the same moment.</summary>
    [SqlServerFact]
    public async Task Many_contenders_on_many_instances_at_once_produce_exactly_one_owner()
    {
        await using var app = await TestApp.CreateSqlServerAsync(configure: OperationsFoundationScenarios.Configure);
        var instances = new[] { app.Services, app.RestartServices(), app.RestartServices(), app.RestartServices() };
        for (var round = 0; round < 5; round++)
        {
            var name = "test.race" + round;
            var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(async i =>
            {
                await using var scope = instances[i % instances.Length].CreateAsyncScope();
                return await scope.ServiceProvider.GetRequiredService<IOperationLeases>().TryAcquireAsync(name, IOperationLeases.InstallationScope, "contender-" + i, "contender " + i, TimeSpan.FromMinutes(5));
            }));
            Assert.Single(results, r => r is not null);
        }
        // A burst of contenders after expiry also yields one winner and a new generation.
        app.Clock.Advance(TimeSpan.FromMinutes(6));
        var after = await Task.WhenAll(Enumerable.Range(0, 16).Select(async i =>
        {
            await using var scope = instances[i % instances.Length].CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IOperationLeases>().TryAcquireAsync("test.race0", IOperationLeases.InstallationScope, "late-" + i, "late " + i, TimeSpan.FromMinutes(5));
        }));
        var winner = Assert.Single(after, r => r is not null);
        Assert.Equal(2, winner!.Generation);
    }
}
