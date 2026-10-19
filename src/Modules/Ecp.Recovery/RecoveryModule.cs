using Ecp.Operations;
using Ecp.Persistence;
using Ecp.SharedKernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Ecp.Recovery;

public static class RecoveryModule
{
    public const string Name = "Recovery";
    public const int Tier = 9;

    /// <summary>
    /// Registers backup, restore, validation, evidence and the support bundle. The host supplies <see cref="IRestoreTargetFactory"/>, which knows how the modules are composed.
    /// </summary>
    public static IServiceCollection AddRecoveryModule(this IServiceCollection services, Action<RecoveryOptions> configure, DatabaseOptions database)
    {
        services.Configure(configure);
        services.AddSingleton<IDatabaseBackupProvider>(sp => database.Provider == DatabaseProvider.SqlServer
            ? ActivatorUtilities.CreateInstance<SqlServerBackupProvider>(sp, database)
            : new UnsupportedDatabaseBackupProvider(database.Provider.ToString()));
        services.TryAddSingleton<IInstallationInventory, InstallationInventory>();
        services.TryAddSingleton<IBackupCatalogue, BackupCatalogue>();
        services.TryAddSingleton<IBackupVerifier, BackupVerifier>();
        services.TryAddSingleton<IInstallationValidator, InstallationValidator>();
        services.TryAddSingleton<EvidenceWriter>();
        services.AddSingleton<RecentLogBuffer>();
        services.AddSingleton<ILoggerProvider>(sp => sp.GetRequiredService<RecentLogBuffer>());
        services.AddScoped<OperationAudit>();
        services.AddScoped<IBackupService, BackupService>();
        services.AddScoped<IRestoreService, RestoreService>();
        services.AddScoped<IEvidenceReports, EvidenceReports>();
        services.AddScoped<ISupportBundle, SupportBundle>();
        services.AddScoped<IOperationsAdministration, OperationsAdministration>();
        services.AddScoped<IEnablementAdministration, EnablementAdministration>();
        services.AddScoped<IImplementationEvidence, ImplementationEvidence>();
        services.AddScoped<IOperationsDashboard, OperationsDashboardService>();
        services.AddSingleton<OperationalFactCollector>();
        services.AddSingleton(_ => UpgradeCatalogue.Embedded());
        services.AddScoped<IUpgradePreflight, UpgradePreflight>();
        services.AddScoped<UpgradeService>();
        services.AddScoped<IUpgradeService>(sp => sp.GetRequiredService<UpgradeService>());
        services.AddScoped<IUpgradeRehearsal, UpgradeRehearsal>();
        services.AddScoped<IRollbackEvidence, RollbackEvidenceService>();
        services.AddScoped<IUpgradeAdministration, UpgradeAdministration>();
        services.AddScoped<IReadinessCheck, BackupReadinessCheck>();
        return services;
    }
}

/// <summary>Production readiness for recovery: configured, protected, recent and verified. It reports; it never declares a deployment ready.</summary>
internal sealed class BackupReadinessCheck(Microsoft.Extensions.Options.IOptions<RecoveryOptions> options, IDatabaseBackupProvider database, IBackupCatalogue catalogue, StartupFacts facts, IClock clock) : IReadinessCheck
{
    public string Id => "R-BKP-01";
    public async Task<ReadinessResult> RunAsync(CancellationToken ct = default)
    {
        const string description = "Backup and restore are configured, protected and recently verified";
        if (facts.Mode != EcpMode.Production && !options.Value.BackupConfigured)
            return new ReadinessResult(Id, description, ReadinessStatus.Healthy, "Not configured. Acceptable outside Production.");
        var o = options.Value;
        if (!database.Supported) return new ReadinessResult(Id, description, ReadinessStatus.Degraded, database.UnsupportedReason);
        if (!o.BackupConfigured) return new ReadinessResult(Id, description, ReadinessStatus.Degraded, "No backup directory is configured (Ecp:Recovery:BackupDirectory). Backups cannot be taken from the platform.");
        var notes = new List<string>();
        if (string.IsNullOrWhiteSpace(o.AdminConnectionString)) notes.Add("backups use the application account; configure a dedicated backup account");
        if (!o.EncryptionRequested && string.IsNullOrWhiteSpace(o.StorageProtection)) notes.Add("no encryption certificate and no storage protection statement");
        var last = (await catalogue.ListAsync(ct)).FirstOrDefault(b => b.State is BackupState.Verified or BackupState.VerifiedWithWarnings);
        if (last is null) notes.Add("no verified backup exists");
        else if (last.CreatedUtc is { } created && clock.UtcNow - created > TimeSpan.FromDays(Math.Max(1, o.StaleBackupDays))) notes.Add($"the last verified backup is more than {o.StaleBackupDays} days old");
        return notes.Count == 0 ? new ReadinessResult(Id, description, ReadinessStatus.Healthy, "A recent verified backup exists. Restore rehearsal evidence is the organisation's to keep.")
            : new ReadinessResult(Id, description, ReadinessStatus.Degraded, string.Join("; ", notes) + ".");
    }
}
