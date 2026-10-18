using Ecp.Persistence;
using Ecp.Recovery;
using Ecp.SharedKernel;
using Ecp.Tests.Common;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecp.Integration.Tests;

/// <summary>A test that needs a real SQL Server whose backup directory is shared with the test process. Without both it is skipped and reported as NOT VERIFIED, never as passed.</summary>
public sealed class SqlServerBackupFactAttribute : FactAttribute
{
    public SqlServerBackupFactAttribute() { if (!RecoveryTestKit.Available) Skip = RecoveryTestKit.NotVerifiedReason; }
}

/// <summary>
/// Backup and restore tests against a real SQL Server (docs/BACKUP_AND_RESTORE.md, "Verification"). The server writes its .bak files into a directory shared with this process
/// (<c>ECP_TEST_SQL_BACKUP_DIR</c> as this process sees it, <c>ECP_TEST_SQL_BACKUP_DIR_SERVER</c> as the server sees it; <c>tools/sqlserver-up.sh --backup-dir</c> sets both up).
/// </summary>
internal static class RecoveryTestKit
{
    public const string HostVariable = "ECP_TEST_SQL_BACKUP_DIR";
    public const string ServerVariable = "ECP_TEST_SQL_BACKUP_DIR_SERVER";
    public const string NotVerifiedReason = "NOT VERIFIED: ECP_TEST_SQL, ECP_TEST_SQL_BACKUP_DIR and ECP_TEST_SQL_BACKUP_DIR_SERVER are not all set, so no SQL Server backup can be taken and read back by this test.";
    public static string? HostDirectory => Environment.GetEnvironmentVariable(HostVariable) is { Length: > 0 } v ? v : null;
    public static string? ServerDirectory => Environment.GetEnvironmentVariable(ServerVariable) is { Length: > 0 } v ? v : null;
    public static bool Available => SqlServerEnvironment.IsAvailable && HostDirectory is not null && ServerDirectory is not null;

    /// <summary>One test's private backup directory (as this process and the server see it) and restore area.</summary>
    internal sealed class Layout
    {
        public string Name { get; } = "t" + Guid.NewGuid().ToString("N")[..12];
        public string BackupDirectory => Path.Combine(HostDirectory!, Name);
        public string ServerBackupDirectory => ServerDirectory!.TrimEnd('/') + "/" + Name;
        public string RestoreRoot { get; } = Path.Combine(Path.GetTempPath(), "ecp-restore-" + Guid.NewGuid().ToString("N"));
        public string? Certificate { get; set; }
        public string AdminConnection { get; set; } = "";
        public bool WithAdminAccount { get; set; }
        public string? StorageProtection { get; set; } = "Test volume; synthetic data only.";

        public Layout() { Directory.CreateDirectory(BackupDirectory); if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(BackupDirectory, (UnixFileMode)0b111_111_111); }
    }

    public static Action<RecoveryOptions> OptionsFor(Layout layout) => o =>
    {
        o.BackupDirectory = layout.BackupDirectory;
        o.SqlServerBackupDirectory = layout.ServerBackupDirectory;
        o.RestoreTargetDirectory = layout.RestoreRoot;
        o.StorageProtection = layout.StorageProtection ?? "";
        o.EncryptionCertificate = layout.Certificate ?? "";
        o.AdminConnectionString = layout.WithAdminAccount ? layout.AdminConnection : "";
        o.ServedRoots = [];
    };

    /// <summary>Adds the recovery module to a test application, and a factory that opens a restored copy with the same composition over its own database and files.</summary>
    public static Action<IServiceCollection> Configure(Layout layout, Action<IServiceCollection>? more = null) => services =>
    {
        var database = (DatabaseOptions)services.Last(d => d.ServiceType == typeof(DatabaseOptions)).ImplementationInstance!;
        services.AddRecoveryModule(OptionsFor(layout), database);
        services.AddSingleton<IRestoreTargetFactory>(sp => new TargetFactory(sp.GetRequiredService<IClock>(), layout, more));
        more?.Invoke(services);
    };

    private sealed class TargetFactory(IClock clock, Layout layout, Action<IServiceCollection>? more) : IRestoreTargetFactory
    {
        public IRestoreTargetHost Open(RestoreTarget target)
        {
            var root = Path.GetDirectoryName(target.DocumentsDirectory)!;
            var provider = TestApp.BuildDetached(DatabaseProvider.SqlServer, target.ConnectionString, clock, root, EcpMode.Test,
                services => { services.AddRecoveryModule(OptionsFor(layout), new DatabaseOptions { Provider = DatabaseProvider.SqlServer, ConnectionString = target.ConnectionString }); more?.Invoke(services); });
            return new Host(provider);
        }

        private sealed class Host(ServiceProvider provider) : IRestoreTargetHost
        {
            public IServiceProvider Services => provider;
            public ValueTask DisposeAsync() => provider.DisposeAsync();
        }
    }

    /// <summary>Drops a restored database the test created (it lives on the disposable server, not in a TestApp).</summary>
    public static async Task DropAsync(string databaseName)
    {
        if (!SafeFiles.IsTargetDatabaseName(databaseName)) return;
        Microsoft.Data.SqlClient.SqlConnection.ClearAllPools();
        await using var c = new Microsoft.Data.SqlClient.SqlConnection(SqlServerEnvironment.ConnectionString);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = $"IF DB_ID(N'{databaseName}') IS NOT NULL BEGIN ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{databaseName}]; END";
        await cmd.ExecuteNonQueryAsync();
    }
}
