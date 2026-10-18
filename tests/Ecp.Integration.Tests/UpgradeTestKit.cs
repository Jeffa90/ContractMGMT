using Ecp.Operations;
using Ecp.Persistence;
using Ecp.Recovery;
using Ecp.SharedKernel;
using Ecp.Tests.Common;
using Microsoft.Extensions.DependencyInjection;

namespace Ecp.Integration.Tests;

/// <summary>
/// The N-1 fixture (docs/UPGRADES.md, "What N-1 means"). It is not a historical production release: it is a real SQL Server database migrated to exactly the frozen schema baseline of the previous platform version (the
/// committed ledger), then populated by this build's own services with the three shipped businesses, contracts in several states, documents, configuration, installed packs and queued notifications. That proves the upgrade
/// mechanics, the migration of populated data, and what must survive. It does not prove that the previous release's binaries wrote the data, and the tables this build adds are the only difference between N-1 and N.
/// </summary>
internal sealed class N1Installation : IAsyncDisposable
{
    public const string SourceVersion = "1.0.0";
    public required TestApp App { get; init; }
    public required RecoveryTestKit.Layout Layout { get; init; }
    public required SyntheticInstallation Data { get; init; }
    public static readonly OperatorIdentity Operator = new("upgrade-test-operator", "Upgrade test operator");

    public static async Task<N1Installation> CreateAsync(Action<IServiceCollection>? more = null)
    {
        var layout = new RecoveryTestKit.Layout();
        var app = await TestApp.CreateSqlServerAsync(migrate: false, configure: RecoveryTestKit.Configure(layout, services => { LifecycleScenarios.Scanner(services); more?.Invoke(services); }));
        var baseline = UpgradeCatalogue.Embedded().Baseline(SourceVersion) ?? throw new InvalidOperationException("The frozen baseline for " + SourceVersion + " is missing.");
        await app.Services.GetRequiredService<MigrationRunner>().RunToAsync(baseline.Modules.ToDictionary(m => m.Module, m => (string?)m.SchemaVersion));
        return new N1Installation { App = app, Layout = layout, Data = await SyntheticInstallation.BuildAsync(app) };
    }

    public static string NewTargetName() => "ecp_upg_" + Guid.NewGuid().ToString("N")[..12];

    public async Task<BackupResult> BackupAsync(bool holdMaintenance = true)
    {
        await using var scope = App.Services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<IBackupService>().CreateAsync(new BackupRequest("Pre-upgrade backup (test)", Operator, HoldMaintenance: holdMaintenance));
        if (!result.Succeeded) throw new InvalidOperationException("Backup failed: " + result.Failure);
        return result;
    }

    public async Task<T> WithAsync<T>(Func<IServiceProvider, Task<T>> body)
    {
        await using var scope = App.Services.CreateAsyncScope();
        return await body(scope.ServiceProvider);
    }

    public Task EnterMaintenanceAsync(string reason = "Pre-upgrade maintenance (test)") => WithAsync(async s => await s.GetRequiredService<IMaintenanceService>().EnableAsync(reason, Operator, ct: default));

    public UpgradeRequest Request(string backupId, IReadOnlyList<string>? accept = null, string? confirm = null, int quiescenceSeconds = 2)
        => new(backupId, "Upgrade rehearsal in a test", confirm ?? UpgradePhrases.For(SourceVersion, UpgradeVersions.Current), Operator, accept ?? [], null, quiescenceSeconds);

    public ValueTask DisposeAsync() => App.DisposeAsync();
}
