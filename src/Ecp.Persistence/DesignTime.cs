using Ecp.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Ecp.Persistence;

/// <summary>Helpers for <c>IDesignTimeDbContextFactory</c> implementations (migration generation does not connect to a database).</summary>
public static class DesignTime
{
    public static (DbContextOptions<T> Options, DbContextServices Services) Create<T>(string schema) where T : TenantDbContext
    {
        var tenant = new TenantContext();
        var connection = new ScopedConnection(
            new DatabaseOptions { Provider = DatabaseProvider.SqlServer, ConnectionString = "Server=design-time-only;Database=design;Trusted_Connection=True;TrustServerCertificate=True" },
            tenant);
        var options = new DbContextOptionsBuilder<T>()
            .UseSqlServer(connection.Connection, o => o.MigrationsHistoryTable("__EFMigrationsHistory", schema))
            .ReplaceService<IModelCacheKeyFactory, ProviderAwareModelCacheKeyFactory>()
            .Options;
        return (options, new DbContextServices(tenant, connection, new SystemClock()));
    }
}
