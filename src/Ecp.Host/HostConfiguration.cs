using Ecp.Operations;
using Ecp.Persistence;
using Ecp.SharedKernel;
using Ecp.SharedKernel.Secrets;

namespace Ecp.Host;

/// <summary>Raised when start-up is refused because the configuration is not acceptable for the selected mode.</summary>
public sealed class StartupRefusedException(IReadOnlyList<GuardViolation> violations)
    : InvalidOperationException("Start-up refused: " + string.Join(" ", violations.Select(v => $"[{v.CheckId}] {v.Message}")))
{
    public IReadOnlyList<GuardViolation> Violations { get; } = violations;
}

public sealed record EcpHostOptions
{
    public EcpMode Mode { get; init; } = EcpMode.Demo;
    public bool DemoTenantResolution { get; init; }
    public bool DemoSeed { get; init; }
    public string DemoTenantName { get; init; } = "Demo Tenant A";
    public string DataDirectory { get; init; } = "data";
}

public sealed record ResolvedConfiguration(EcpHostOptions Host, DatabaseOptions Database, StartupFacts Facts);

public static class HostConfiguration
{
    /// <summary>
    /// Reads configuration, resolves the connection string (a secret reference in Production, never an inline value) and evaluates the
    /// Production guard. Secret values are never logged or placed in exceptions.
    /// </summary>
    public static ResolvedConfiguration Resolve(IConfiguration config, ISecretProvider secrets, string contentRoot)
    {
        var host = new EcpHostOptions
        {
            Mode = Enum.TryParse<EcpMode>(config["Ecp:Mode"], true, out var mode) ? mode : EcpMode.Production, // unknown value fails safe
            DemoTenantResolution = config.GetValue("Ecp:DemoTenantResolution", false),
            DemoSeed = config.GetValue("Ecp:DemoSeed", false),
            DemoTenantName = config["Ecp:DemoTenantName"] ?? "Demo Tenant A",
            DataDirectory = config["Ecp:DataDirectory"] ?? "data"
        };
        var provider = Enum.TryParse<DatabaseProvider>(config["Database:Provider"], true, out var p) ? p : DatabaseProvider.SqlServer;
        var inline = config["Database:ConnectionString"];
        var reference = config["Database:ConnectionStringRef"];
        string connection = "";
        var fromSecret = false;
        if (SecretReference.TryParse(reference, out var secretRef))
        {
            connection = secrets.Resolve(secretRef) ?? "";
            fromSecret = connection.Length > 0;
        }
        else if (!string.IsNullOrWhiteSpace(inline))
        {
            connection = inline;
        }

        var dataDirectory = Path.IsPathRooted(host.DataDirectory) ? host.DataDirectory : Path.Combine(contentRoot, host.DataDirectory);
        if (provider == DatabaseProvider.Sqlite && connection.Contains("Data Source=", StringComparison.OrdinalIgnoreCase) && !connection.Contains(":memory:", StringComparison.OrdinalIgnoreCase)
            && !connection.Contains("Mode=Memory", StringComparison.OrdinalIgnoreCase))
        {
            // Relative SQLite paths resolve against the content root so the Demo works from any working directory.
            var path = connection[(connection.IndexOf("Data Source=", StringComparison.OrdinalIgnoreCase) + "Data Source=".Length)..].Split(';')[0].Trim();
            if (!Path.IsPathRooted(path))
            {
                var full = Path.Combine(contentRoot, path);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                connection = connection.Replace(path, full, StringComparison.Ordinal);
            }
        }

        var database = new DatabaseOptions { Provider = provider, ConnectionString = connection, AutoMigrate = config.GetValue("Database:AutoMigrate", false) };
        var facts = new StartupFacts(host.Mode, provider, fromSecret, InlineConnectionStringPresent: !string.IsNullOrWhiteSpace(inline) && !fromSecret,
            host.DemoTenantResolution, host.DemoSeed, dataDirectory);
        var violations = ProductionGuard.Evaluate(facts);
        if (violations.Count > 0) throw new StartupRefusedException(violations);
        if (string.IsNullOrWhiteSpace(connection)) throw new InvalidOperationException("No database connection is configured.");
        return new ResolvedConfiguration(host with { DataDirectory = dataDirectory }, database, facts);
    }
}
