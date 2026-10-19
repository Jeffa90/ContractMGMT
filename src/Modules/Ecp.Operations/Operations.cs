using Ecp.Persistence;
using Ecp.SharedKernel;
using Ecp.SharedKernel.Localisation;
using Ecp.SharedKernel.Secrets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Ecp.Operations;

public enum ReadinessStatus { Healthy, Degraded, Unhealthy }

public sealed record ReadinessResult(string Id, string Description, ReadinessStatus Status, string Message);

public sealed record ReadinessReport(ReadinessStatus Overall, IReadOnlyList<ReadinessResult> Results);

public interface IReadinessCheck
{
    string Id { get; }
    Task<ReadinessResult> RunAsync(CancellationToken ct);
}

/// <summary>What the host was started with, reduced to facts the guard can judge (no secret values).</summary>
public sealed record StartupFacts(
    EcpMode Mode, DatabaseProvider Provider, bool ConnectionFromSecretReference, bool InlineConnectionStringPresent,
    bool DemoTenantResolutionEnabled, bool DemoSeedEnabled, string DataDirectory);

public sealed record GuardViolation(string CheckId, string Message);

/// <summary>
/// Production refuses development identity/tenant resolution, the lightweight database provider, inline secrets, demonstration data
/// and ephemeral storage (SETUP_AND_READINESS_SPEC section 4, SEC-010). The host aborts start-up when any violation exists.
/// </summary>
public static class ProductionGuard
{
    public static IReadOnlyList<GuardViolation> Evaluate(StartupFacts f)
    {
        var violations = new List<GuardViolation>();
        if (f.Mode != EcpMode.Production) return violations;

        if (f.DemoTenantResolutionEnabled)
            violations.Add(new("R-ID-01", "Demonstration tenant/identity resolution must be disabled in Production."));
        if (f.Provider != DatabaseProvider.SqlServer)
            violations.Add(new("R-DB-01", "Production requires SQL Server or Azure SQL; the lightweight provider is refused."));
        if (f.InlineConnectionStringPresent)
            violations.Add(new("R-SEC-02", "The database connection string must come from a secret reference, not from configuration values."));
        else if (!f.ConnectionFromSecretReference)
            violations.Add(new("R-SEC-02", "A database connection secret reference (env:, file:, keyvault:) is required in Production."));
        if (f.DemoSeedEnabled)
            violations.Add(new("R-DEMO-01", "Demonstration data must not be present or seeded in Production."));
        if (string.IsNullOrWhiteSpace(f.DataDirectory) || IsEphemeral(f.DataDirectory))
            violations.Add(new("R-ST-01", "Document storage must be a persistent directory, not a temporary location."));
        return violations;
    }

    private static bool IsEphemeral(string path)
    {
        var full = Path.GetFullPath(path);
        var temp = Path.GetFullPath(Path.GetTempPath());
        return full.StartsWith(temp, StringComparison.Ordinal) || full.StartsWith("/tmp/", StringComparison.Ordinal);
    }
}

public sealed class ReadinessService(IEnumerable<IReadinessCheck> checks, ILogger<ReadinessService> logger)
{
    public async Task<ReadinessReport> RunAsync(CancellationToken ct = default)
    {
        var results = new List<ReadinessResult>();
        foreach (var check in checks)
        {
            try { results.Add(await check.RunAsync(ct)); }
            catch (Exception ex)
            {
                logger.LogError(ex, "Readiness check {Check} failed", check.Id);
                results.Add(new ReadinessResult(check.Id, check.Id, ReadinessStatus.Unhealthy, "The check could not be completed."));
            }
        }
        var overall = results.Any(r => r.Status == ReadinessStatus.Unhealthy) ? ReadinessStatus.Unhealthy
            : results.Any(r => r.Status == ReadinessStatus.Degraded) ? ReadinessStatus.Degraded : ReadinessStatus.Healthy;
        return new ReadinessReport(overall, results);
    }
}

/// <summary>R-DB-01: the database is reachable and every module's migrations are applied.</summary>
internal sealed class DatabaseReadinessCheck(MigrationRunner runner) : IReadinessCheck
{
    public string Id => "R-DB-01";

    public async Task<ReadinessResult> RunAsync(CancellationToken ct)
    {
        try
        {
            var status = await runner.GetStatusAsync(ct);
            return status.IsCurrent
                ? new ReadinessResult(Id, "Database reachable and migrations applied", ReadinessStatus.Healthy, "Migrations are current.")
                : new ReadinessResult(Id, "Database reachable and migrations applied", ReadinessStatus.Unhealthy, $"Pending migrations for: {string.Join(", ", status.Pending)}.");
        }
        catch (Exception)
        {
            return new ReadinessResult(Id, "Database reachable and migrations applied", ReadinessStatus.Unhealthy, "The database is not reachable.");
        }
    }
}

/// <summary>R-EML-01: reports whether email delivery has been proved. Healthy only after a test that reached the provider and delivered the synthetic message; anything else is Degraded and says why.</summary>
public sealed class EmailReadinessCheck(IEmailConfigurationService email, HostModeAccessor? hostMode = null) : IReadinessCheck
{
    public string Id => "R-EML-01";

    public async Task<ReadinessResult> RunAsync(CancellationToken ct)
    {
        const string description = "Email provider verified";
        var info = await email.ReadinessAsync(ct);
        var code = info.Code is { Length: > 0 } c ? $" [{c}]" : "";
        // Outside Production a simulated or untested provider is an expected trial state; it is labelled, not hidden.
        var mode = hostMode ?? new HostModeAccessor(EcpMode.Production);
        var trial = mode.Mode != EcpMode.Production && (info.State is EmailState.Simulated or EmailState.NotConfigured || (info.State == EmailState.NotVerified && EmailProviderKeys.IsSimulated(info.ProviderKey)));
        var status = info.State == EmailState.Healthy || trial ? ReadinessStatus.Healthy : ReadinessStatus.Degraded;
        return new ReadinessResult(Id, description, status, $"{info.State}: {info.Summary}{code}");
    }
}

public sealed class DataDirectoryOptions { public string Path { get; set; } = ""; }

/// <summary>R-ST-01 (part): the data directory exists and a write/read/delete round trip succeeds.</summary>
internal sealed class DataDirectoryReadinessCheck(DataDirectoryOptions options) : IReadinessCheck
{
    public string Id => "R-ST-02";

    public async Task<ReadinessResult> RunAsync(CancellationToken ct)
    {
        const string description = "Data directory writable";
        try
        {
            Directory.CreateDirectory(options.Path);
            var probe = Path.Combine(options.Path, $".ready-{Guid.NewGuid():N}");
            await File.WriteAllTextAsync(probe, "ok", ct);
            var read = await File.ReadAllTextAsync(probe, ct);
            File.Delete(probe);
            return read == "ok" ? new(Id, description, ReadinessStatus.Healthy, "Write, read and delete succeeded.")
                                : new(Id, description, ReadinessStatus.Unhealthy, "Read-back did not match.");
        }
        catch (Exception) { return new(Id, description, ReadinessStatus.Unhealthy, "The data directory is not writable."); }
    }
}

/// <summary>R-OPS-02: culture data (ICU) and time-zone data are present and the configured defaults resolve.</summary>
internal sealed class GlobalisationReadinessCheck(LocalisationOptions options) : IReadinessCheck
{
    public string Id => "R-OPS-02";

    public Task<ReadinessResult> RunAsync(CancellationToken ct)
    {
        const string description = "ICU and time-zone data present";
        var failed = GlobalisationProbe.Run(options.DefaultCulture, options.DefaultTimeZoneId).Where(r => !r.Ok).ToList();
        return Task.FromResult(failed.Count == 0
            ? new ReadinessResult(Id, description, ReadinessStatus.Healthy, "Culture and time-zone data are present.")
            : new ReadinessResult(Id, description, ReadinessStatus.Unhealthy, string.Join(" ", failed.Select(f => f.Message))));
    }
}

public static class OperationsModule
{
    public const string Name = "Operations";
    public const string Schema = "ops";
    public const int Tier = 8;

    public static IServiceCollection AddOperationsModule(this IServiceCollection services, string dataDirectory)
    {
        services.AddModuleDbContext<Ecp.Operations.Persistence.OperationsDbContext>(Name, Tier, Schema);
        services.AddSingleton<MaintenanceSnapshot>();
        services.AddScoped<IOperationLeases, OperationLeases>();
        services.AddScoped<IMaintenanceService, MaintenanceService>();
        services.AddScoped<Ecp.SharedKernel.IMaintenanceGate, MaintenanceGate>();
        services.AddScoped<IOperationHistory, OperationHistory>();
        services.AddScoped<IMigrationRecords, MigrationRecords>();
        services.AddSingleton(new DataDirectoryOptions { Path = dataDirectory });
        services.AddScoped<IReadinessCheck, DatabaseReadinessCheck>();
        services.AddScoped<IReadinessCheck, DataDirectoryReadinessCheck>();
        services.TryAddSingleton(new LocalisationOptions());
        services.AddScoped<IReadinessCheck, GlobalisationReadinessCheck>();
        services.AddScoped<ReadinessService>();
        services.AddSingleton<IEmailSenderFactory, EmailSenderFactory>();
        services.TryAddSingleton(new EmailReadinessOptions());
        services.AddScoped<IEmailConfigurationService, EmailConfigurationService>();
        services.AddScoped<IAcceptedExceptions, AcceptedExceptions>();
        services.AddScoped<Ecp.SharedKernel.IMockScannerAcceptance, MockScannerAcceptance>();
        services.TryAddSingleton<ISecretProvider>(new Ecp.SharedKernel.Secrets.EnvironmentAndFileSecretProvider());
        return services;
    }
}
