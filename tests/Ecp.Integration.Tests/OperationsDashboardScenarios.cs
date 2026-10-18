using System.Diagnostics.Metrics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ecp.Audit;
using Ecp.Configuration.Domain;
using Ecp.Configuration.Persistence;
using Ecp.Notifications;
using Ecp.Notifications.Domain;
using Ecp.Notifications.Persistence;
using Ecp.Operations;
using Ecp.Operations.Domain;
using Ecp.Persistence;
using Ecp.Recovery;
using Ecp.SharedKernel;
using Ecp.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Ecp.Integration.Tests;

/// <summary>
/// OPS-03 (docs/OPERATIONS_DASHBOARD.md, docs/OPS03_REQUIREMENTS_MATRIX.md): the operations dashboard aggregates what the platform already knows without reading another tenant's records, judges it with one set of rules,
/// never turns Not Verified into a pass, flags stuck work, offers retry only where it is valid, and keeps its observability and support bundle free of secrets and content.
/// </summary>
internal static class OperationsDashboardScenarios
{
    private static readonly OperatorIdentity Operator = new("dashboard-test", "Dashboard test");

    private static async Task<OperationsDashboard> BuildAsync(TestApp app, Guid sysadmin)
    {
        await using var s = UpgradeFoundationScenarios.As(app, SystemTenant.Id, sysadmin, recentSignIn: false);
        return await s.Get<IOperationsDashboard>().BuildAsync();
    }

    private static DashboardItem Item(OperationsDashboard d, string panel, string code) => d.Panel(panel)!.Items.First(i => i.Code == code);

    private static async Task FinishAsync(TestApp app, string kind, string status, string summary, string reference = "ref")
    {
        await using var s = app.ScopeFor(SystemTenant.Id);
        var history = s.Get<IOperationHistory>();
        var id = await history.StartAsync(kind, reference, "dashboard test", Operator);
        await history.FinishAsync(id, status, summary, "{}");
    }

    public static async Task The_dashboard_is_for_the_installation_administrator_only_and_never_reads_from_a_customer_tenant(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        var sysadmin = await UpgradeFoundationScenarios.SystemAdministratorAsync(app);
        var d = await BuildAsync(app, sysadmin);
        Assert.True(d.DetailVisible);
        Assert.Equal(["health", "readiness", "versions", "upgrade", "recovery", "jobs", "outbox", "configuration", "integrity", "certificates"], d.Panels.Select(p => p.Key));
        Assert.False(string.IsNullOrWhiteSpace(d.CorrelationId));

        // A business administrator, and the installation administrator acting from inside a customer tenant, are refused. Nothing is built.
        await using (var s = UpgradeFoundationScenarios.As(app, w.W.TenantId, w.W.Users["tenantAdmin"], recentSignIn: true))
            await Assert.ThrowsAsync<ForbiddenException>(() => s.Get<IOperationsDashboard>().BuildAsync());
        await using (var s = UpgradeFoundationScenarios.As(app, w.W.TenantId, sysadmin, recentSignIn: true))
            await Assert.ThrowsAsync<ForbiddenException>(() => s.Get<IOperationsDashboard>().BuildAsync());
        foreach (var user in new[] { "ownerPackaging", "legalA", "groupHead" })
            await using (var s = UpgradeFoundationScenarios.As(app, w.W.TenantId, w.W.Users[user], recentSignIn: false))
                await Assert.ThrowsAsync<ForbiddenException>(() => s.Get<IOperationsDashboard>().BuildAsync());
    }

    public static async Task Every_status_is_one_of_four_distinct_words_a_panel_is_its_worst_item_and_not_verified_is_never_a_pass(TestApp app)
    {
        var sysadmin = await UpgradeFoundationScenarios.SystemAdministratorAsync(app);
        var d = await BuildAsync(app, sysadmin);
        foreach (var p in d.Panels)
        {
            Assert.Equal(OpsStatuses.Worst(p.Items.Select(i => i.Status)), p.Status);
            Assert.NotEmpty(p.Items);
        }
        Assert.Equal(OpsStatuses.Worst(d.Panels.Select(p => p.Status)), d.Overall);
        Assert.NotEqual(OpsStatus.Verified, d.Overall);                       // something here is always Not Verified (TLS, secret expiry, unbuilt catalogue checks)

        // The catalogue: every entry appears exactly once; those that do not run are Not Verified and say why; the coverage figures add up.
        var readiness = d.Panel("readiness")!;
        Assert.Equal(ReadinessCatalogue.Entries.Select(e => e.Id), readiness.Items.Select(i => i.Code));
        Assert.Equal(d.Coverage.Total, d.Coverage.RunHere + d.Coverage.RunElsewhere + d.Coverage.NotBuilt);
        foreach (var id in new[] { "R-DEP-D1", "R-DEP-I1", "R-DEP-A1", "R-AI-01", "R-SEC-03" }) Assert.Equal(OpsStatus.NotVerified, Item(d, "readiness", id).Status);
        foreach (var id in ReadinessCatalogue.RunElsewhere) { var item = Item(d, "readiness", id); Assert.Equal(OpsStatus.NotVerified, item.Status); Assert.Contains("not run from the dashboard", item.Explanation); }
        Assert.Equal(d.Coverage.NotBuilt + d.Coverage.RunElsewhere, readiness.Items.Count(i => i.Explanation.Contains("not been built") || i.Explanation.Contains("not run from the dashboard")));
        // A line that says "Verified" never carries words that admit it was not checked.
        foreach (var i in d.Panels.SelectMany(p => p.Items).Where(i => i.Status == OpsStatus.Verified))
        {
            Assert.DoesNotContain("not been built", i.Explanation); Assert.DoesNotContain("cannot be looked at", i.Explanation); Assert.DoesNotContain("not run from the dashboard", i.Explanation);
        }
        // Things the platform cannot read here are Not Verified, with a reason.
        Assert.Equal(OpsStatus.NotVerified, Item(d, "certificates", "CRT-004").Status);          // TLS
        Assert.Equal(OpsStatus.NotVerified, Item(d, "certificates", "CRT-005").Status);          // secret expiry
        Assert.Equal(OpsStatus.NotVerified, Item(d, "certificates", "CRT-002").Status);          // no key-ring inspector in this host
        Assert.Equal(OpsStatus.NotVerified, Item(d, "recovery", "REC-003").Status);              // no restore recorded
        Assert.Equal(OpsStatus.NotVerified, Item(d, "integrity", "INT-003").Status);             // never validated
        if (app.Provider != DatabaseProvider.SqlServer) { Assert.Equal(OpsStatus.NotVerified, Item(d, "recovery", "REC-001").Status); Assert.Equal(OpsStatus.NotVerified, Item(d, "upgrade", "UPR-001").Status); }
    }

    public static async Task Jobs_and_the_outbox_are_counted_across_tenants_from_counts_only_and_match_each_tenants_own_figures(TestApp app)
    {
        var options = app.Services.GetRequiredService<IOptions<NotificationsOptions>>().Value;
        var previous = options.EmailProvider;
        options.EmailProvider = "graph";                                       // not configured: deliveries fail closed, which is what the dashboard must show
        try
        {
            var w1 = await LifecycleWorld.CreateAsync(app); var w2 = await LifecycleWorld.CreateAsync(app);
            foreach (var w in new[] { w1, w2 })
            {
                var id = await w.NewDraftAsync();
                await w.As("ownerPackaging", s => s.Get<Ecp.Lifecycle.ILegalReviewService>().SubmitAsync(new Ecp.Lifecycle.SubmitLegalRequest(Ecp.Lifecycle.Domain.SubjectKinds.Contract, id, null)));
            }
            for (var i = 0; i < 8; i++) { await w1.RunJobsAsync(); w1.App.Clock.Advance(TimeSpan.FromHours(13)); }       // first tenant: attempts run out -> dead letters
            await w2.RunJobsAsync();                                                                                     // second tenant: one failed attempt, will retry
            var sysadmin = await UpgradeFoundationScenarios.SystemAdministratorAsync(app);

            var s1 = await w1.As("procAdmin", s => s.Get<INotificationAdmin>().SummaryAsync());
            var s2 = await w2.As("procAdmin", s => s.Get<INotificationAdmin>().SummaryAsync());
            Assert.True(s1.DeadLetter > 0); Assert.True(s2.Failed + s2.Pending > 0);

            var d = await BuildAsync(app, sysadmin);
            var outbox = Item(d, "outbox", "OUT-001");
            Assert.Equal(OpsStatus.Warning, outbox.Status);
            Assert.Contains($"{s1.DeadLetter + s2.DeadLetter} dead-lettered", outbox.Explanation);
            Assert.Contains($"{s1.Failed + s2.Failed} failed", outbox.Explanation);
            Assert.Contains($"{s1.Pending + s2.Pending} waiting", outbox.Explanation);
            Assert.Equal(OpsStatus.Verified, Item(d, "jobs", "JOB-001").Status is OpsStatus.Verified or OpsStatus.Warning ? OpsStatus.Verified : OpsStatus.Failed);   // jobs ran; a failing e-mail provider is not a failing job

            // Counts and times only: no address, subject, name or message text reaches the dashboard.
            var json = JsonSerializer.Serialize(DashboardExport.Of(d));
            Assert.DoesNotContain("example.test", json); Assert.DoesNotContain("Lifecycle ", json); Assert.DoesNotContain("LC-", json);
        }
        finally { options.EmailProvider = previous; }
    }

    public static async Task A_stale_job_an_expired_lease_and_a_run_with_no_live_lease_are_flagged_and_never_retried_automatically(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        var sysadmin = await UpgradeFoundationScenarios.SystemAdministratorAsync(app);
        var now = app.Clock.UtcNow;
        await using (var s = app.ScopeFor(w.W.TenantId))
        {
            var db = s.Get<NotificationsDbContext>();
            db.JobRuns.Add(JobRun.Start(w.W.TenantId, "stale-test", now.AddHours(-3)));                          // recorded as running three hours ago, never finished
            await db.SaveChangesAsync();
        }
        Assert.Equal(OpsStatus.Failed, Item(await BuildAsync(app, sysadmin), "jobs", "JOB-001").Status);

        // A lease whose holder stopped without releasing it, and an operation recorded as running with no lease behind it.
        await using (var s = app.ScopeFor(SystemTenant.Id))
        {
            Assert.NotNull(await s.Get<IOperationLeases>().TryAcquireAsync("test.expired", IOperationLeases.InstallationScope, "instance-x", "an instance that stopped", TimeSpan.FromMinutes(1)));
            await s.Get<IOperationHistory>().StartAsync(OperationKinds.Backup, "bk-stuck", "stuck", Operator);
        }
        app.Clock.Advance(TimeSpan.FromMinutes(20));
        var d = await BuildAsync(app, sysadmin);
        var stale = Assert.Single(d.Panel("jobs")!.Items, i => i.Code == "JOB-STALE");
        Assert.Equal(OpsStatus.Warning, stale.Status);
        var stuck = Assert.Single(d.Panel("jobs")!.Items, i => i.Code == "JOB-STUCK");
        Assert.Equal(OpsStatus.Failed, stuck.Status);
        Assert.False(string.IsNullOrWhiteSpace(stuck.CorrelationId));
        Assert.Contains("never retried automatically", stuck.NextStep!);
        // Looking changes nothing: the run is still Running, the lease is untouched.
        await using var after = app.ScopeFor(SystemTenant.Id);
        Assert.Contains(await after.Get<IOperationHistory>().ListAsync(OperationKinds.Backup, 10), r => r.Reference == "bk-stuck" && r.Status == OperationStatuses.Running);
    }

    public static async Task Failures_show_the_last_result_with_a_correlation_id_offer_a_new_attempt_only_where_valid_and_leave_maintenance_on(TestApp app)
    {
        var sysadmin = await UpgradeFoundationScenarios.SystemAdministratorAsync(app);
        await FinishAsync(app, OperationKinds.Backup, OperationStatuses.Failed, "The database server could not write the backup.");
        await FinishAsync(app, OperationKinds.Validation, OperationStatuses.Failed, "1 check failed.");
        await FinishAsync(app, OperationKinds.Restore, OperationStatuses.Failed, "The restore stopped at stage 9.");
        await FinishAsync(app, OperationKinds.Upgrade, OperationStatuses.Failed, "A migration failed.");
        await using (var s = app.ScopeFor(SystemTenant.Id))
            await s.Get<IMaintenanceService>().EnableAsync("Upgrade failed (test)", Operator, MaintenanceCauses.UpgradeFailed);
        app.Services.GetRequiredService<MaintenanceSnapshot>().Invalidate();

        var d = await BuildAsync(app, sysadmin);
        FailureEntry F(string kind) => Assert.Single(d.Failures, f => f.Kind == kind);
        Assert.True(F(OperationKinds.Backup).RetryValid); Assert.True(F(OperationKinds.Validation).RetryValid);
        Assert.False(F(OperationKinds.Restore).RetryValid); Assert.False(F(OperationKinds.Upgrade).RetryValid);
        Assert.Contains("never retried", F(OperationKinds.Upgrade).NextStep); Assert.Contains("never retried", F(OperationKinds.Restore).NextStep);
        Assert.All(d.Failures, f => { Assert.False(string.IsNullOrWhiteSpace(f.CorrelationId)); Assert.NotNull(f.WhenUtc); Assert.Equal(OperationStatuses.Failed, f.Status); });
        Assert.Equal(OpsStatus.Failed, Item(d, "health", "HLT-002").Status);                  // a failed upgrade is Failed, not softened to a warning
        Assert.Equal(OpsStatus.Failed, d.Overall);
        // The dashboard does not clear anything: maintenance mode is still on, with the same cause.
        await using var after = app.ScopeFor(SystemTenant.Id);
        var status = await after.Get<IMaintenanceService>().GetAsync();
        Assert.True(status.Enabled); Assert.Equal(MaintenanceCauses.UpgradeFailed, status.Cause);
        Assert.Equal(OpsStatus.Failed, Item(d, "upgrade", "UPR-002").Status);
    }

    public static async Task Expiring_and_expired_pack_signing_keys_are_flagged_and_unreadable_sources_stay_not_verified(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        var sysadmin = await UpgradeFoundationScenarios.SystemAdministratorAsync(app);
        var now = app.Clock.UtcNow;
        async Task AddKey(string id, DateTime? notAfter)
        {
            await using var s = app.ScopeFor(w.W.TenantId);
            var db = s.Get<ConfigurationDbContext>();
            db.TrustedKeys.Add(TrustedPublisherKey.Create(w.W.TenantId, id, "ES256", new byte[] { 1, 2, 3 }, id.PadRight(8, 'x'), "Key " + id, null, notAfter, "test", now));
            await db.SaveChangesAsync();
        }
        await AddKey("no-end-date", null);
        Assert.Equal(OpsStatus.Verified, Item(await BuildAsync(app, sysadmin), "certificates", "CRT-001").Status);
        await AddKey("ends-soon", now.AddDays(10));
        Assert.Equal(OpsStatus.Warning, Item(await BuildAsync(app, sysadmin), "certificates", "CRT-001").Status);
        await AddKey("has-expired", now.AddDays(-1));
        var d = await BuildAsync(app, sysadmin);
        var keys = Item(d, "certificates", "CRT-001");
        Assert.Equal(OpsStatus.Failed, keys.Status);
        Assert.DoesNotContain("ends-soon", JsonSerializer.Serialize(DashboardExport.Of(d)));       // key identifiers and material never appear
    }

    public static async Task Operational_events_and_metrics_carry_the_correlation_id_and_no_secret(TestApp app)
    {
        var sysadmin = await UpgradeFoundationScenarios.SystemAdministratorAsync(app);
        var seen = new List<(string Name, long Value, string Tags)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) => { if (instrument.Meter.Name == OperationalTelemetry.MeterName) l.EnableMeasurementEvents(instrument); };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => { lock (seen) seen.Add((instrument.Name, value, string.Join(",", tags.ToArray().Select(t => $"{t.Key}={t.Value}")))); });
        listener.Start();

        await FinishAsync(app, OperationKinds.Restore, OperationStatuses.Failed, "failed with Password=CANARY-EVENT-1 for jane.canary@example.test", "canary-ref");
        var d = await BuildAsync(app, sysadmin);
        listener.RecordObservableInstruments();

        lock (seen)
        {
            Assert.Contains(seen, m => m.Name == "ecp.operations.finished" && m.Value == 1 && m.Tags == "kind=Restore,status=Failed");
            Assert.Contains(seen, m => m.Name == "ecp.dashboard.builds" && m.Tags.StartsWith("overall=", StringComparison.Ordinal));
            Assert.Contains(seen, m => m.Name == "ecp.health.panels");
            Assert.DoesNotContain(seen, m => m.Tags.Contains("CANARY", StringComparison.OrdinalIgnoreCase) || m.Tags.Contains("example.test", StringComparison.Ordinal));
        }
        Assert.Contains(d.Metrics, kv => kv.Key.StartsWith("health.", StringComparison.Ordinal));

        // The structured event names the operation and its correlation id, as a warning (so it reaches the support bundle's redacted lines), and never the operator's free text.
        var lines = app.Services.GetServices<RecentLogBuffer>().SelectMany(b => b.Snapshot()).ToList();
        var failure = lines.Last(l => l.Message.StartsWith("Operation Restore finished: Failed", StringComparison.Ordinal));
        Assert.Contains("(correlation ", failure.Message);
        Assert.Equal("Warning", failure.Level);
        Assert.DoesNotContain(lines, l => l.Message.Contains("CANARY", StringComparison.Ordinal));
    }

    public static async Task The_support_bundle_needs_a_recent_sign_in_is_audited_lists_its_files_by_hash_and_holds_no_canary(TestApp app)
    {
        var w = await LifecycleWorld.CreateAsync(app);
        var sysadmin = await UpgradeFoundationScenarios.SystemAdministratorAsync(app);
        // Canaries in every place operational data can come from: an operation summary, a failed job's own error text, and the log.
        await FinishAsync(app, OperationKinds.Backup, OperationStatuses.Failed, "failed with Password=CANARY-BUNDLE-1 and token=CANARY-BUNDLE-2 for jane.canary@example.test", "canary-backup");
        await using (var s = app.ScopeFor(w.W.TenantId))
        {
            var db = s.Get<NotificationsDbContext>();
            var run = JobRun.Start(w.W.TenantId, "canary-job", app.Clock.UtcNow.AddMinutes(-5));
            run.Fail("provider said Password=CANARY-JOB-3 for contract LC-SECRET-TITLE", app.Clock.UtcNow);
            db.JobRuns.Add(run);
            await db.SaveChangesAsync();
        }
        app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Canary").LogWarning("Sign-in failed for jane.canary@example.test with Password=CANARY-LOG-4");

        await using (var s = UpgradeFoundationScenarios.As(app, SystemTenant.Id, sysadmin, recentSignIn: false))
            await Assert.ThrowsAsync<ReauthenticationRequiredException>(() => s.Get<IOperationsAdministration>().SupportBundleAsync());
        await using (var s = UpgradeFoundationScenarios.As(app, w.W.TenantId, w.W.Users["tenantAdmin"], recentSignIn: true))
            await Assert.ThrowsAsync<ForbiddenException>(() => s.Get<IOperationsAdministration>().SupportBundleAsync());
        byte[] bytes;
        await using (var s = UpgradeFoundationScenarios.As(app, SystemTenant.Id, sysadmin, recentSignIn: true))
            bytes = await s.Get<IOperationsAdministration>().SupportBundleAsync();

        using var zip = new ZipArchive(new MemoryStream(bytes));
        Assert.Equal(["README.txt", "bundle.json", "manifest.json", "summary.csv"], zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal));
        Assert.All(zip.Entries, e => Assert.True(Ecp.Recovery.SupportBundleRules.SafeEntryName(e.FullName)));
        string Read(string name) { using var r = new StreamReader(zip.GetEntry(name)!.Open()); return r.ReadToEnd(); }
        var all = string.Join("\n", zip.Entries.Select(e => Read(e.FullName)));
        foreach (var canary in new[] { "CANARY-BUNDLE-1", "CANARY-BUNDLE-2", "CANARY-JOB-3", "CANARY-LOG-4", "jane.canary", "LC-SECRET-TITLE", "example.test" }) Assert.DoesNotContain(canary, all);

        // The manifest names every other file with its size and SHA-256, and those hashes are true.
        using var manifest = JsonDocument.Parse(Read("manifest.json"));
        var listed = manifest.RootElement.GetProperty("entries").EnumerateArray().ToDictionary(e => e.GetProperty("name").GetString()!, e => (e.GetProperty("bytes").GetInt32(), e.GetProperty("sha256").GetString()!));
        Assert.Equal(["README.txt", "bundle.json", "summary.csv"], listed.Keys.Order(StringComparer.Ordinal));
        foreach (var (name, (size, sha)) in listed)
        {
            var entry = zip.GetEntry(name)!;
            using var ms = new MemoryStream(); using (var es = entry.Open()) es.CopyTo(ms);
            Assert.Equal(size, ms.Length); Assert.Equal(Convert.ToHexString(SHA256.HashData(ms.ToArray())).ToLowerInvariant(), sha);
        }
        // The bundle describes the dashboard's facts (counts and times), the metrics and the way observability is (not) exported.
        using var bundle = JsonDocument.Parse(Read("bundle.json"));
        Assert.True(bundle.RootElement.TryGetProperty("operationalFacts", out _)); Assert.True(bundle.RootElement.TryGetProperty("metrics", out _));
        Assert.Contains("Not Verified", bundle.RootElement.GetProperty("observability").GetProperty("exporters").GetString());
        // Generation is one audited act: who, how big, which hash, which correlation id.
        await using var audit = app.ScopeFor(SystemTenant.Id);
        var events = await audit.Get<IAuditEventSource>().Events.Where(e => e.Action == "operations.supportbundle.exported").ToListAsync();
        var ev = Assert.Single(events);
        Assert.Contains(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()[..16], ev.EntityId);
    }
}

[Trait("Category", "Integration")]
[Trait("Provider", "SqliteNotSqlServerProof")]
public sealed class OperationsDashboardScenarioTests_Sqlite
{
    [Fact] public Task Access() => ProviderRunner.SqliteAsync(OperationsDashboardScenarios.The_dashboard_is_for_the_installation_administrator_only_and_never_reads_from_a_customer_tenant, OperationsFoundationScenarios.Configure);
    [Fact] public Task Statuses() => ProviderRunner.SqliteAsync(OperationsDashboardScenarios.Every_status_is_one_of_four_distinct_words_a_panel_is_its_worst_item_and_not_verified_is_never_a_pass, OperationsFoundationScenarios.Configure);
    [Fact] public Task Outbox() => ProviderRunner.SqliteAsync(OperationsDashboardScenarios.Jobs_and_the_outbox_are_counted_across_tenants_from_counts_only_and_match_each_tenants_own_figures, OperationsFoundationScenarios.Configure);
    [Fact] public Task Stuck() => ProviderRunner.SqliteAsync(OperationsDashboardScenarios.A_stale_job_an_expired_lease_and_a_run_with_no_live_lease_are_flagged_and_never_retried_automatically, OperationsFoundationScenarios.Configure);
    [Fact] public Task Failures() => ProviderRunner.SqliteAsync(OperationsDashboardScenarios.Failures_show_the_last_result_with_a_correlation_id_offer_a_new_attempt_only_where_valid_and_leave_maintenance_on, OperationsFoundationScenarios.Configure);
    [Fact] public Task Certificates() => ProviderRunner.SqliteAsync(OperationsDashboardScenarios.Expiring_and_expired_pack_signing_keys_are_flagged_and_unreadable_sources_stay_not_verified, OperationsFoundationScenarios.Configure);
    [Fact] public Task Observability() => ProviderRunner.SqliteAsync(OperationsDashboardScenarios.Operational_events_and_metrics_carry_the_correlation_id_and_no_secret, OperationsFoundationScenarios.Configure);
    [Fact] public Task Bundle() => ProviderRunner.SqliteAsync(OperationsDashboardScenarios.The_support_bundle_needs_a_recent_sign_in_is_audited_lists_its_files_by_hash_and_holds_no_canary, OperationsFoundationScenarios.Configure);
}

[Trait("Category", "SqlServer")]
public sealed class OperationsDashboardScenarioTests_SqlServer
{
    [SqlServerFact] public Task Access() => ProviderRunner.SqlServerAsync(OperationsDashboardScenarios.The_dashboard_is_for_the_installation_administrator_only_and_never_reads_from_a_customer_tenant, OperationsFoundationScenarios.Configure);
    [SqlServerFact] public Task Statuses() => ProviderRunner.SqlServerAsync(OperationsDashboardScenarios.Every_status_is_one_of_four_distinct_words_a_panel_is_its_worst_item_and_not_verified_is_never_a_pass, OperationsFoundationScenarios.Configure);
    [SqlServerFact] public Task Outbox() => ProviderRunner.SqlServerAsync(OperationsDashboardScenarios.Jobs_and_the_outbox_are_counted_across_tenants_from_counts_only_and_match_each_tenants_own_figures, OperationsFoundationScenarios.Configure);
    [SqlServerFact] public Task Stuck() => ProviderRunner.SqlServerAsync(OperationsDashboardScenarios.A_stale_job_an_expired_lease_and_a_run_with_no_live_lease_are_flagged_and_never_retried_automatically, OperationsFoundationScenarios.Configure);
    [SqlServerFact] public Task Failures() => ProviderRunner.SqlServerAsync(OperationsDashboardScenarios.Failures_show_the_last_result_with_a_correlation_id_offer_a_new_attempt_only_where_valid_and_leave_maintenance_on, OperationsFoundationScenarios.Configure);
    [SqlServerFact] public Task Certificates() => ProviderRunner.SqlServerAsync(OperationsDashboardScenarios.Expiring_and_expired_pack_signing_keys_are_flagged_and_unreadable_sources_stay_not_verified, OperationsFoundationScenarios.Configure);
    [SqlServerFact] public Task Observability() => ProviderRunner.SqlServerAsync(OperationsDashboardScenarios.Operational_events_and_metrics_carry_the_correlation_id_and_no_secret, OperationsFoundationScenarios.Configure);
    [SqlServerFact] public Task Bundle() => ProviderRunner.SqlServerAsync(OperationsDashboardScenarios.The_support_bundle_needs_a_recent_sign_in_is_audited_lists_its_files_by_hash_and_holds_no_canary, OperationsFoundationScenarios.Configure);
}
