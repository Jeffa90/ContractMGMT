using Ecp.Audit;
using Ecp.Configuration;
using Ecp.Contracts;
using Ecp.DataMigration;
using Ecp.Documents;
using Ecp.Documents.Storage;
using Ecp.Identity;
using Ecp.Lifecycle;
using Ecp.Notifications;
using Ecp.Operations;
using Ecp.Persistence;
using Ecp.Recovery;
using Ecp.Access;
using Ecp.Setup;
using Ecp.SharedKernel;
using Ecp.SharedKernel.Secrets;
using Ecp.Tenancy;

namespace Ecp.Host;

/// <summary>Where an installation keeps its files. The running application uses the data directory; a restored copy opened for validation uses its own.</summary>
public sealed record CompositionPaths(string DataDirectory, string DocumentsRoot, string PacksRoot);

/// <summary>
/// The one place the platform's modules are composed. The running application and a restored copy opened beside it for validation (<see cref="RestoreTargetFactory"/>) are built by the same code, so validation exercises
/// the real services against the restored data.
/// </summary>
public static class PlatformComposition
{
    public static CompositionPaths PathsFor(string dataDirectory, IConfiguration configuration)
        => new(dataDirectory, Path.Combine(dataDirectory, "documents"), configuration["Ecp:Packs:StorageDirectory"] is { Length: > 0 } packs ? packs : Path.Combine(dataDirectory, "packs"));

    public static IServiceCollection AddEcpPlatformModules(this IServiceCollection services, IConfiguration configuration, ResolvedConfiguration resolved, CompositionPaths paths, string hostVersion, string contentRoot, string webRoot)
    {
        var production = resolved.Host.Mode == EcpMode.Production;
        services.AddEcpPersistence(resolved.Database);
        services.AddAuditModule();
        services.AddTenancyModule();
        services.AddIdentityModule(o => { o.Mode = resolved.Host.Mode; o.RequireEmergencyConfirmation = production; });
        var governance = GovernanceMode.Resolve(resolved.Host.Mode, configuration[GovernanceMode.SettingKey]);
        services.AddSingleton(governance);
        services.AddConfigurationModule(o => { o.RequireSecondPersonPublish = governance.SecondPersonRequired; o.GovernanceMode = governance.Name; o.RequireRecentSignInForRollback = true; o.AllowRawDocumentEditing = !governance.IsProductionEquivalent; o.DefaultPackId = configuration["Ecp:DefaultPackId"]; }, packs: o =>
        {
            configuration.GetSection("Ecp:Packs").Bind(o);
            o.Mode = resolved.Host.Mode;                                                       // the host decides the mode; configuration cannot override it
            o.StorageDirectory = paths.PacksRoot;
            if (production) o.AllowUnsignedSyntheticPacks = false;                              // Production never accepts unsigned packs, whatever the setting says
        });
        services.AddContractsModule(o => configuration.GetSection("Contracts").Bind(o));
        services.AddDocumentsModule(o => configuration.GetSection("Documents").Bind(o), new LocalFileSystemOptions { RootPath = paths.DocumentsRoot });
        services.AddMigrationModule(o =>
        {
            configuration.GetSection("Migration").Bind(o);
            o.Mode = resolved.Host.Mode;
            o.RequireDataClassification = resolved.Host.Mode == EcpMode.Test && governance.SecondPersonRequired;
            if (production) o.RequireMalwareScan = true; // no scanner adapter exists yet, so Production holds attachments rather than importing them unscanned (MIG-F014)
        });
        // The demonstration scanner stands in for a provider adapter. Demo uses it by default; Test only when Documents:Scanner:Provider is set to "mock" on purpose; Production never gets it (G-25).
        var scannerOptions = new ScannerProviderOptions();
        configuration.GetSection("Documents:Scanner").Bind(scannerOptions);
        scannerOptions.Mode = resolved.Host.Mode;
        Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.Replace(services, ServiceDescriptor.Singleton(scannerOptions));
        if (resolved.Host.Mode != EcpMode.Production && scannerOptions.EffectiveProvider == "mock") services.AddSingleton<IMalwareScanner, MockMalwareScanner>();
        // The installation default solution pack applies only to an organisation that has none of its own; it is unset unless the installer sets it (docs/SETUP_AND_ONBOARDING.md).
        var installationDefaultPack = configuration["Ecp:DefaultPackId"];
        services.AddLifecycleModule(o => { o.Mode = resolved.Host.Mode; o.DefaultPackId = installationDefaultPack; });
        services.AddNotificationsModule(o =>
        {
            configuration.GetSection("Notifications").Bind(o);
            o.Mode = resolved.Host.Mode;
            o.DefaultPackId = installationDefaultPack;
        });
        // Email settings saved in the application override these starting values; both are read through IEmailConfigurationService (G-12).
        var startingNotifications = new NotificationsOptions();
        configuration.GetSection("Notifications").Bind(startingNotifications);
        services.AddSingleton(new HostModeAccessor(resolved.Host.Mode));
        services.AddSingleton(new EmailEnvironmentDefaults(startingNotifications.EmailProvider, startingNotifications.Smtp));
        services.AddSingleton(new EmailReadinessOptions { MaxAge = TimeSpan.FromHours(Math.Clamp(configuration.GetValue("Ecp:Email:ReadinessMaxAgeHours", 168), 1, 24 * 90)) });
        services.AddAccessModule();
        services.AddOperationsModule(paths.DataDirectory);
        services.AddSetupModule(new SetupEnvironment(resolved.Host.Mode, paths.DataDirectory,
            DemoIdentityEnabled: resolved.Host.Mode != EcpMode.Production && resolved.Host.DemoTenantResolution, DemoSeedEnabled: resolved.Host.DemoSeed,
            ConnectionFromSecretReference: resolved.Facts.ConnectionFromSecretReference, DatabaseProvider: resolved.Database.Provider.ToString(), Version: hostVersion,
            AllowRemoteSetup: configuration.GetValue("Ecp:Setup:AllowRemote", false), GovernanceName: governance.Name, GovernanceLabel: governance.Label, GovernanceNote: governance.Note));
        services.AddRecoveryModule(o => BindRecovery(o, configuration, resolved, contentRoot, webRoot), resolved.Database);
        return services;
    }

    /// <summary>Binds <c>Ecp:Recovery</c>. The administrator connection is a secret reference in Production; an inline value is accepted outside Production only.</summary>
    private static void BindRecovery(RecoveryOptions o, IConfiguration configuration, ResolvedConfiguration resolved, string contentRoot, string webRoot)
    {
        var section = configuration.GetSection("Ecp:Recovery");
        section.Bind(o);
        var reference = section["AdminConnectionStringRef"];
        if (SecretReference.TryParse(reference, out var secretRef)) o.AdminConnectionString = new EnvironmentAndFileSecretProvider().Resolve(secretRef) ?? "";
        else if (resolved.Host.Mode == EcpMode.Production) o.AdminConnectionString = "";
        if (!string.IsNullOrWhiteSpace(o.BackupDirectory) && !Path.IsPathRooted(o.BackupDirectory)) o.BackupDirectory = Path.GetFullPath(o.BackupDirectory, contentRoot);
        if (!string.IsNullOrWhiteSpace(o.RestoreTargetDirectory) && !Path.IsPathRooted(o.RestoreTargetDirectory)) o.RestoreTargetDirectory = Path.GetFullPath(o.RestoreTargetDirectory, contentRoot);
        o.ServedRoots = [webRoot, contentRoot];
    }
}

/// <summary>Opens a restored copy as a second, separate set of services over its own database, document store and package store.</summary>
internal sealed class RestoreTargetFactory(IConfiguration configuration, ResolvedConfiguration resolved, string hostVersion, string contentRoot, string webRoot) : IRestoreTargetFactory
{
    public IRestoreTargetHost Open(RestoreTarget target)
    {
        var database = new DatabaseOptions { Provider = resolved.Database.Provider, ConnectionString = target.ConnectionString, AutoMigrate = false };
        var copy = new ResolvedConfiguration(resolved.Host with { DataDirectory = Path.GetDirectoryName(target.DocumentsDirectory)! }, database, resolved.Facts);
        var paths = new CompositionPaths(copy.Host.DataDirectory, target.DocumentsDirectory, target.PacksDirectory);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        services.AddSingleton<ISecretProvider>(new EnvironmentAndFileSecretProvider());
        services.AddSingleton(copy.Host);
        services.AddSingleton(copy.Facts);
        services.AddSingleton(new Ecp.SharedKernel.Localisation.LocalisationOptions());
        services.AddSingleton<ISetupProtector, PlainSetupProtector>();
        services.AddEcpPlatformModules(configuration, copy, paths, hostVersion, contentRoot, webRoot);
        return new TargetHost(services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = false }));
    }

    private sealed class TargetHost(ServiceProvider provider) : IRestoreTargetHost
    {
        public IServiceProvider Services => provider;
        public ValueTask DisposeAsync() => provider.DisposeAsync();
    }

    /// <summary>A restored copy never completes setup or stores a setup secret; this protector exists only so the setup module can be composed.</summary>
    private sealed class PlainSetupProtector : ISetupProtector
    {
        public string Protect(string plain) => plain;
        public string Unprotect(string protectedText) => protectedText;
    }
}
