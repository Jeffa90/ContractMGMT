using Ecp.Configuration;
using Ecp.Identity;
using Ecp.Identity.Domain;
using Ecp.Operations;
using Ecp.Operations.Domain;
using Ecp.Persistence;
using Ecp.Recovery;
using Ecp.SharedKernel;
using Ecp.Tests.Common;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecp.Integration.Tests;

/// <summary>
/// OPS-02 (docs/UPGRADES.md): the provider-independent parts of upgrade safety: the manifest and baselines that ship inside this build, who may do what, the upgrade lease, and maintenance mode refusing configuration changes
/// wherever they are called from. The lightweight (SQLite) run is labelled as such; the real upgrade on SQL Server is in <c>UpgradeSqlServerTests</c>.
/// </summary>
internal static class UpgradeFoundationScenarios
{
    private static async Task InstallationAsync(IServiceProvider services, Func<IServiceProvider, Task> work)
    {
        await using var scope = services.CreateAsyncScope();
        await work(scope.ServiceProvider);
    }

    private static async Task<T> InstallationAsync<T>(IServiceProvider services, Func<IServiceProvider, Task<T>> work)
    {
        await using var scope = services.CreateAsyncScope();
        return await work(scope.ServiceProvider);
    }

    internal static async Task<Guid> SystemAdministratorAsync(TestApp app)
    {
        await using var s = app.ScopeFor(SystemTenant.Id);
        var id = await s.Get<IUserService>().CreateActiveTrustedAsync("Sysadmin", "sys" + Guid.NewGuid().ToString("N")[..6] + "@installation.example.test", "demo", "https://demo.example.test", "sys|" + Guid.NewGuid(), "t");
        await s.Get<IRoleAssignmentService>().GrantTrustedAsync(new GrantRequest(id, RoleCodes.SystemAdministrator, null, ScopeSet.Installation(), EffectiveFromUtc: app.Clock.UtcNow.AddDays(-30)), AssignmentSource.Demo, "Test");
        return id;
    }

    internal static TenantScope As(TestApp app, Guid tenant, Guid user, bool recentSignIn)
    {
        var s = app.ScopeFor(tenant);
        s.Get<CurrentUser>().Set(user);
        var actor = s.Get<ActorContext>(); actor.Type = ActorType.User; actor.ActorId = user.ToString(); actor.Source = "Test";
        if (recentSignIn) s.Get<ReauthenticationContext>().Set(app.Clock.UtcNow);
        return s;
    }

    public static async Task The_release_that_ships_in_this_build_is_intact_matches_its_migrations_and_the_frozen_baselines_and_reviews_are_complete(TestApp app)
    {
        var catalogue = UpgradeCatalogue.Embedded();
        var compiled = MigrationInventory.Build(app.Services.GetServices<ModuleDescriptor>());
        var manifest = catalogue.ManifestTo(UpgradeVersions.Current);
        Assert.NotNull(manifest);
        // The manifest is the build's own: intact, deterministic, and describing exactly these migrations.
        Assert.DoesNotContain(UpgradeManifestValidator.Validate(manifest!, catalogue, compiled), p => p.Critical);
        var notes = catalogue.Notes.Single(n => n.To == UpgradeVersions.Current);
        var rebuilt = UpgradeManifestBuilder.Build(notes, catalogue.Baseline(notes.From)!, compiled, catalogue.Reviews);
        Assert.Equal(UpgradeJson.Serialise(manifest!), UpgradeJson.Serialise(rebuilt));
        // Every frozen baseline is intact (its hash is its content's), and every migration it lists is still in this build, unchanged.
        foreach (var baseline in catalogue.Baselines)
        {
            Assert.Equal(baseline.LedgerSha256, UpgradeJson.LedgerHash(baseline.Modules));
            foreach (var module in baseline.Modules)
                foreach (var migration in module.Migrations)
                    Assert.Equal(migration.Checksum, compiled.Find(module.Module)!.Migrations.Single(m => m.Id == migration.Id).Checksum);
        }
        // Order is deterministic, identifiers are unique per module, and every module declares its current schema version.
        Assert.Equal(compiled.Modules.OrderBy(m => m.Tier).ThenBy(m => m.Module, StringComparer.Ordinal).Select(m => m.Module), compiled.Modules.Select(m => m.Module));
        foreach (var module in compiled.Modules)
        {
            Assert.Equal(module.Migrations.Select(m => m.Id).Distinct(StringComparer.Ordinal).Count(), module.Migrations.Count);
            Assert.Equal(module.Migrations.Select(m => m.Id).Order(StringComparer.Ordinal), module.Migrations.Select(m => m.Id));
            Assert.Equal(module.Migrations.Last().Id, module.SchemaVersion);
        }
        // Every migration that drops, rewrites or alters existing structure or data has a recorded review that covers what it does; no review names a migration that does not exist.
        foreach (var (module, entry) in compiled.All.Where(x => x.Entry.Categories.Any(MigrationCategories.NeedReview.Contains)))
        {
            var review = catalogue.Reviews.Entries.FirstOrDefault(r => r.Module == module && r.Migration == entry.Id);
            Assert.True(review is not null, $"{module}/{entry.Id} changes existing structure or data ({string.Join(", ", entry.Categories)}) and has no review in upgrades/migration-reviews.json.");
            Assert.All(entry.Categories.Where(MigrationCategories.NeedReview.Contains), c => Assert.Contains(c, review!.Categories));
        }
        foreach (var review in catalogue.Reviews.Entries) Assert.Contains(compiled.All, x => x.Module == review.Module && x.Entry.Id == review.Migration);
        // Nothing in the shipped documents is a secret.
        Assert.False(Redaction.ContainsSecret(System.Text.Encoding.UTF8.GetString(UpgradeJson.Serialise(manifest!))));
        await Task.CompletedTask;
    }

    public static async Task Maintenance_mode_refuses_configuration_publication_even_when_called_from_a_service_and_publication_resumes_when_it_is_released(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        var org = await PackResolutionScenarios.AddOrganisationAsync(w, "Gate Org", "GTE", TestPacks.Blank);
        var saved = await w.W.AsAsync("tenantAdmin", s => s.Get<ICapabilityAdministration>().SaveAsync(org.Org, [new CapabilityInput(CapabilityKeys.LegalReview, "on", "Gate test")]));
        var draft = await w.W.AsAsync("tenantAdmin", s => s.Get<IConfigurationService>().ValidateAsync(saved.Id));
        Assert.True(draft.IsPublishable);

        var operatorId = new OperatorIdentity("test-operator", "Test operator");
        await InstallationAsync(app.Services, s => s.GetRequiredService<IMaintenanceService>().EnableAsync("Upgrade in progress (test)", operatorId, MaintenanceCauses.UpgradeInProgress));
        app.Services.GetRequiredService<MaintenanceSnapshot>().Invalidate();
        var refused = await Assert.ThrowsAsync<DomainException>(() => w.W.AsAsync("tenantAdmin", s => s.Get<IConfigurationService>().PublishAsync(draft.Id, "Not now")));
        Assert.Equal("maintenance_mode", refused.Code);
        Assert.Equal("maintenance_mode", (await Assert.ThrowsAsync<DomainException>(() => InstallationAsync(app.Services, s => s.GetRequiredService<IMaintenanceGate>().EnsureWritableAsync("Committing a migration batch")))).Code);
        // A rollback of configuration is refused too.
        Assert.Equal("maintenance_mode", (await Assert.ThrowsAsync<DomainException>(() => w.W.AsAsync("tenantAdmin", s => s.Get<IConfigurationService>().RollbackAsync("Capabilities", org.Org, 1, "Not now")))).Code);

        // Leaving an upgrade state needs the explicit acknowledgement; once left, publication works again, once.
        Assert.Equal("upgrade_state", (await Assert.ThrowsAsync<DomainException>(() => InstallationAsync(app.Services, s => s.GetRequiredService<IMaintenanceService>().DisableAsync("carry on", operatorId)))).Code);
        await InstallationAsync(app.Services, s => s.GetRequiredService<IMaintenanceService>().DisableAsync("Upgrade validated (test)", operatorId, acknowledgeRestoreState: true));
        var release = await w.W.AsAsync("tenantAdmin", s => s.Get<IConfigurationService>().PublishAsync(draft.Id, "Now"));
        Assert.Equal(1, release.Number);
        await InstallationAsync(app.Services, s => s.GetRequiredService<IMaintenanceGate>().EnsureWritableAsync("Anything"));
    }

    public static async Task The_upgrade_services_need_their_own_permissions_a_recent_sign_in_and_the_installation_tenant_and_nothing_can_downgrade(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        var sysadmin = await SystemAdministratorAsync(app);

        // Reading is for people who hold operations.read in the installation's own tenant, and for no one else.
        await using (var s = As(app, SystemTenant.Id, sysadmin, recentSignIn: false))
        {
            var report = await s.Get<IUpgradeAdministration>().MigrationReportAsync();
            Assert.Equal(UpgradeVersions.Current, report.TargetVersion);
            Assert.NotEmpty(report.Modules);
            Assert.NotNull(await s.Get<IUpgradeAdministration>().HistoryAsync());
            Assert.Null(await s.Get<IUpgradeAdministration>().LastPostUpgradeValidationAsync());
            // Changing anything needs a recent sign-in even for the administrator.
            await Assert.ThrowsAsync<ReauthenticationRequiredException>(() => s.Get<IUpgradeAdministration>().ApplyAsync("bk-20261016T010000Z-aaaaaaaa", "reason", "UPGRADE x TO y", [], null));
            await Assert.ThrowsAsync<ReauthenticationRequiredException>(() => s.Get<IUpgradeAdministration>().RehearseAsync("bk-20261016T010000Z-aaaaaaaa", "ecp_target", "reason", "x", []));
            await Assert.ThrowsAsync<ReauthenticationRequiredException>(() => s.Get<IUpgradeAdministration>().ProveRollbackAsync("bk-20261016T010000Z-aaaaaaaa", "ecp_target", "reason", "x"));
        }
        // With a recent sign-in the request is judged on its merits: a backup that does not exist is refused, not guessed.
        await using (var s = As(app, SystemTenant.Id, sysadmin, recentSignIn: true))
        {
            var ex = await Assert.ThrowsAnyAsync<DomainException>(() => s.Get<IUpgradeAdministration>().ApplyAsync("bk-20261016T010000Z-aaaaaaaa", "reason", UpgradePhrases.For("1.0.0", UpgradeVersions.Current), [], null));
            Assert.NotEmpty(ex.Message);
            // Evidence files are fetched by exact name only; a path is never accepted.
            await Assert.ThrowsAsync<ResourceNotFoundException>(() => s.Get<IUpgradeAdministration>().EvidenceFileAsync("../../etc/passwd"));
            await Assert.ThrowsAsync<ResourceNotFoundException>(() => s.Get<IUpgradeAdministration>().EvidenceFileAsync("upgrade-20261016T010000Z-bk-20261016T010000Z-aaaaaaaa.json/../x"));
        }
        // A person with no operations permission, and the installation administrator's permission used from inside a customer tenant, are both refused.
        await using (var s = As(app, w.W.TenantId, w.W.Users["tenantAdmin"], recentSignIn: false))
        {
            await Assert.ThrowsAsync<ForbiddenException>(() => s.Get<IUpgradeAdministration>().MigrationReportAsync());
            await Assert.ThrowsAsync<ForbiddenException>(() => s.Get<IUpgradeAdministration>().HistoryAsync());
            await Assert.ThrowsAsync<ForbiddenException>(() => s.Get<IUpgradeAdministration>().ScriptAsync("Contracts"));
        }
        await using (var s = As(app, w.W.TenantId, sysadmin, recentSignIn: true))
        {
            await Assert.ThrowsAsync<ForbiddenException>(() => s.Get<IUpgradeAdministration>().MigrationReportAsync());
            await Assert.ThrowsAsync<ForbiddenException>(() => s.Get<IUpgradeAdministration>().ApplyAsync("bk-20261016T010000Z-aaaaaaaa", "reason", "x", [], null));
        }
        // The interface offers no way to downgrade: nothing on it is named for one.
        var methods = typeof(IUpgradeAdministration).GetMethods().Select(m => m.Name).Concat(typeof(IUpgradeService).GetMethods().Select(m => m.Name));
        Assert.DoesNotContain(methods, n => n.Contains("Downgrade", StringComparison.OrdinalIgnoreCase) || n.Contains("Revert", StringComparison.OrdinalIgnoreCase));
    }

    public static async Task Published_configuration_the_new_version_would_not_accept_blocks_and_one_of_an_unknown_kind_warns_and_the_probe_changes_nothing(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        var org = await PackResolutionScenarios.AddOrganisationAsync(w, "Probe Org", "PRB", TestPacks.Blank);
        // Releases that an older version published and this one would not accept are inserted directly: the platform itself would refuse to publish them today.
        await using (var s = app.ScopeFor(w.W.TenantId))
        {
            var db = s.Get<Ecp.Configuration.Persistence.ConfigurationDbContext>();
            db.Releases.Add(Ecp.Configuration.Domain.ConfigurationRelease.Create(w.W.TenantId, org.Org, "Mystery", 1, "{}", null, null, "Older version", "test", app.Clock.UtcNow));
            db.Releases.Add(Ecp.Configuration.Domain.ConfigurationRelease.Create(w.W.TenantId, org.Org, CapabilityKinds.Name, 99, "[]", null, null, "Older version", "test", app.Clock.UtcNow));
            await db.SaveChangesAsync();
        }

        async Task<(IReadOnlyList<UpgradeFinding> Findings, string Snapshot)> RunAsync()
        {
            await using var s = app.ScopeFor(w.W.TenantId);
            var db = s.Get<Ecp.Configuration.Persistence.ConfigurationDbContext>();
            var found = new List<UpgradeFinding>();
            foreach (var probe in s.Get<IEnumerable<IUpgradeProbe>>().Where(p => p.Area is "Configuration" or "Packs")) found.AddRange(await probe.ProbeAsync());
            var releases = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(db.Releases.AsQueryable().OrderBy(r => r.Kind).ThenBy(r => r.OrganisationId).ThenBy(r => r.Number));
            var drafts = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.CountAsync(db.Drafts);
            return (found, string.Join("|", releases.Select(r => $"{r.Kind}/{r.Number}/{r.ContentHash}")) + "#" + drafts);
        }

        var (findings, before) = await RunAsync();
        var invalid = Assert.Single(findings, f => f.Code == "UPG-CFG-002");
        Assert.Equal(UpgradeFindingSeverity.Blocker, invalid.Severity);
        Assert.Contains(CapabilityKinds.Name, invalid.Detail);
        var unknown = Assert.Single(findings, f => f.Code == "UPG-CFG-001");
        Assert.Equal(UpgradeFindingSeverity.Warning, unknown.Severity);
        Assert.Contains("Mystery", unknown.Detail);
        Assert.DoesNotContain(findings, f => f.Code.StartsWith("UPG-PCK", StringComparison.Ordinal) && f.Severity == UpgradeFindingSeverity.Blocker);   // the installed packs are fine
        Assert.False(Redaction.ContainsSecret(string.Join("\n", findings.Select(f => f.Detail + f.Description + f.Remediation))));

        // Reading changed nothing: not a release, not a hash, not a draft; and a second run says the same.
        var (again, after) = await RunAsync();
        Assert.Equal(before, after);
        Assert.Equal(findings.Select(f => f.Code).Order(), again.Select(f => f.Code).Order());
    }

    public static async Task One_upgrade_lease_at_a_time_and_a_database_that_cannot_be_upgraded_is_reported_blocked(TestApp app)
    {
        var scopes = app.Services.GetRequiredService<IServiceScopeFactory>();
        await using var first = await LeaseHolder.TryAcquireAsync(scopes, UpgradeLeases.Exclusive, IOperationLeases.InstallationScope, "instance-a", "upgrade on instance A", TimeSpan.FromMinutes(5));
        Assert.NotNull(first);
        Assert.Null(await LeaseHolder.TryAcquireAsync(scopes, UpgradeLeases.Exclusive, IOperationLeases.InstallationScope, "instance-b", "upgrade on instance B", TimeSpan.FromMinutes(5)));
        // The recovery lease is a different lease: holding both is what keeps backup, restore and validation out.
        await using var recovery = await LeaseHolder.TryAcquireAsync(scopes, RecoveryLeases.Exclusive, IOperationLeases.InstallationScope, "instance-a", "upgrade on instance A", TimeSpan.FromMinutes(5));
        Assert.NotNull(recovery);
        Assert.Null(await LeaseHolder.TryAcquireAsync(scopes, RecoveryLeases.Exclusive, IOperationLeases.InstallationScope, "instance-b", "backup on instance B", TimeSpan.FromMinutes(5)));

        if (app.Provider != DatabaseProvider.SqlServer)
        {
            // The lightweight provider has no migration history and no supported upgrade: the pre-flight says so and nothing may be applied.
            var report = await InstallationAsync(app.Services, s => s.GetRequiredService<IUpgradePreflight>().RunAsync(new UpgradeTarget(app.Services, app.ConnectionString, "", true, "test"), new PreflightOptions(null, PreflightPhase.Apply)));
            Assert.Equal(PreflightOutcome.Blocked, report.Outcome);
            Assert.False(report.CanApply);
            Assert.Contains(report.Checks, c => c.Code == "UPF-003" && c.Outcome == CheckOutcome.Failed);
            Assert.All(report.Checks, c => Assert.False(string.IsNullOrEmpty(c.CorrelationId)));
            Assert.All(report.Checks.Where(c => c.Outcome != CheckOutcome.Passed), c => Assert.False(string.IsNullOrWhiteSpace(c.Description)));
        }
    }
}

[Trait("Category", "Integration")]
[Trait("Provider", "SqliteNotSqlServerProof")]
public sealed class UpgradeFoundationScenarioTests_Sqlite
{
    [Fact] public Task Release() => ProviderRunner.SqliteAsync(UpgradeFoundationScenarios.The_release_that_ships_in_this_build_is_intact_matches_its_migrations_and_the_frozen_baselines_and_reviews_are_complete, OperationsFoundationScenarios.Configure);
    [Fact] public Task Gate() => ProviderRunner.SqliteAsync(UpgradeFoundationScenarios.Maintenance_mode_refuses_configuration_publication_even_when_called_from_a_service_and_publication_resumes_when_it_is_released, OperationsFoundationScenarios.Configure);
    [Fact] public Task Permissions() => ProviderRunner.SqliteAsync(UpgradeFoundationScenarios.The_upgrade_services_need_their_own_permissions_a_recent_sign_in_and_the_installation_tenant_and_nothing_can_downgrade, OperationsFoundationScenarios.Configure);
    [Fact] public Task Probes() => ProviderRunner.SqliteAsync(UpgradeFoundationScenarios.Published_configuration_the_new_version_would_not_accept_blocks_and_one_of_an_unknown_kind_warns_and_the_probe_changes_nothing, OperationsFoundationScenarios.Configure);
    [Fact] public Task Lease() => ProviderRunner.SqliteAsync(UpgradeFoundationScenarios.One_upgrade_lease_at_a_time_and_a_database_that_cannot_be_upgraded_is_reported_blocked, OperationsFoundationScenarios.Configure);
}

[Trait("Category", "SqlServer")]
public sealed class UpgradeFoundationScenarioTests_SqlServer
{
    [SqlServerFact] public Task Release() => ProviderRunner.SqlServerAsync(UpgradeFoundationScenarios.The_release_that_ships_in_this_build_is_intact_matches_its_migrations_and_the_frozen_baselines_and_reviews_are_complete, OperationsFoundationScenarios.Configure);
    [SqlServerFact] public Task Gate() => ProviderRunner.SqlServerAsync(UpgradeFoundationScenarios.Maintenance_mode_refuses_configuration_publication_even_when_called_from_a_service_and_publication_resumes_when_it_is_released, OperationsFoundationScenarios.Configure);
    [SqlServerFact] public Task Permissions() => ProviderRunner.SqlServerAsync(UpgradeFoundationScenarios.The_upgrade_services_need_their_own_permissions_a_recent_sign_in_and_the_installation_tenant_and_nothing_can_downgrade, OperationsFoundationScenarios.Configure);
    [SqlServerFact] public Task Probes() => ProviderRunner.SqlServerAsync(UpgradeFoundationScenarios.Published_configuration_the_new_version_would_not_accept_blocks_and_one_of_an_unknown_kind_warns_and_the_probe_changes_nothing, OperationsFoundationScenarios.Configure);
    [SqlServerFact] public Task Lease() => ProviderRunner.SqlServerAsync(UpgradeFoundationScenarios.One_upgrade_lease_at_a_time_and_a_database_that_cannot_be_upgraded_is_reported_blocked, OperationsFoundationScenarios.Configure);
}
