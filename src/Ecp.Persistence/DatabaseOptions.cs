namespace Ecp.Persistence;

public enum DatabaseProvider
{
    /// <summary>SQL Server or Azure SQL: the only provider supported in Production.</summary>
    SqlServer,

    /// <summary>
    /// Lightweight provider for Demo/Test on machines without SQL Server and for provider-independent tests.
    /// Never proof of SQL Server behaviour; refused in Production (readiness R-DB-01).
    /// </summary>
    Sqlite
}

public sealed class DatabaseOptions
{
    public DatabaseProvider Provider { get; set; } = DatabaseProvider.Sqlite;

    /// <summary>The resolved connection string. Never logged unmasked. In Production it comes from a secret reference.</summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>Apply migrations at start-up. Allowed in Demo/Test; Production uses the explicit migrate command.</summary>
    public bool AutoMigrate { get; set; }
}
