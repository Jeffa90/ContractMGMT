using Ecp.Notifications;
using Ecp.Operations;
using Ecp.SharedKernel;
using Ecp.Tenancy;
using Ecp.Tenancy.Domain;
using Microsoft.Extensions.Options;

namespace Ecp.Host;

/// <summary>
/// Runs the platform's background work on a timer, once per active tenant: turn lifecycle events into notifications, deliver email, send reminders, close expired emergency unlocks. Each run is recorded
/// (see the administration page). A failure in one tenant, or one job, is logged and does not stop the others; the next tick tries again, and every job is safe to repeat.
/// </summary>
public sealed class BackgroundJobService(IServiceScopeFactory scopes, IOptions<NotificationsOptions> options, ILogger<BackgroundJobService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var seconds = options.Value.JobIntervalSeconds;
        if (seconds <= 0) { log.LogInformation("Background jobs are turned off (Notifications:JobIntervalSeconds is 0)."); return; }
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(seconds));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "A background job cycle failed."); }
        }
    }

    /// <summary>
    /// One cycle across every active tenant. While maintenance mode is on no new work is taken (work already running finishes its current unit; every job is safe to repeat). Each tenant's cycle holds an operation lease, so two
    /// instances sharing a database never run the same tenant's jobs at the same time (docs/OPERATION_LEASES.md). Public so the cycle can be driven directly.
    /// </summary>
    public async Task RunOnceAsync(CancellationToken ct)
    {
        IReadOnlyList<TenantInfo> tenants;
        await using (var registry = scopes.CreateAsyncScope())
        {
            try
            {
                if ((await registry.ServiceProvider.GetRequiredService<IMaintenanceService>().GetAsync(ct)).Enabled)
                {
                    log.LogInformation("Background jobs are paused: the installation is in maintenance mode.");
                    OperationalLog.BackgroundCycle(log, 0, 0, paused: true);
                    return;
                }
            }
            catch (Exception ex) when (ex is System.Data.Common.DbException or InvalidOperationException) { log.LogWarning("The maintenance flag could not be read ({Type}); jobs continue.", ex.GetType().Name); }
            using var _ = registry.ServiceProvider.GetRequiredService<TenantContext>().UseInstallationScope();
            tenants = await registry.ServiceProvider.GetRequiredService<ITenantDirectory>().ListAsync(ct);
        }
        var owner = $"{Environment.MachineName}:{Environment.ProcessId}";
        int ran = 0, failed = 0;
        foreach (var tenant in tenants.Where(t => t.Status == TenantStatus.Active && t.Id != SystemTenant.Id))
        {
            try
            {
                await using var lease = await LeaseHolder.TryAcquireAsync(scopes, "background.jobs", tenant.Id.ToString("N"), owner, "background job cycle", TimeSpan.FromMinutes(Math.Max(2, options.Value.JobIntervalSeconds / 20)), ct);
                if (lease is null) { log.LogInformation("Another instance is running background jobs for tenant {Tenant}; skipped.", tenant.Id); continue; }
                await using var scope = scopes.CreateAsyncScope();
                using var token = scope.ServiceProvider.GetRequiredService<TenantContext>().Use(tenant.Id);
                var actor = scope.ServiceProvider.GetRequiredService<ActorContext>();
                actor.Type = ActorType.System; actor.ActorId = "system"; actor.Source = "Job";
                await scope.ServiceProvider.GetRequiredService<INotificationJobs>().RunAllAsync(ct);
                await scope.ServiceProvider.GetRequiredService<Ecp.Documents.IDocumentScanJob>().RunAsync(ct);
                ran++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { failed++; log.LogError(ex, "Background jobs failed for tenant {Tenant}.", tenant.Id); }
        }
        OperationalLog.BackgroundCycle(log, ran, failed, paused: false);
    }
}
