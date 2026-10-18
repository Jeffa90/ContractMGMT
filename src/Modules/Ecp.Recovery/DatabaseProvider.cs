using System.Data;
using System.Data.Common;
using System.Text.RegularExpressions;
using Ecp.Persistence;
using Ecp.SharedKernel;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Ecp.Recovery;

/// <summary>Which server and database a connection really reaches, as the server itself reports them (so an alias such as localhost and 127.0.0.1 cannot disguise the active database).</summary>
public sealed record DatabaseIdentity(string Server, string Database, string Version, string Edition)
{
    public bool Same(DatabaseIdentity other) => string.Equals(Server, other.Server, StringComparison.OrdinalIgnoreCase) && string.Equals(Database, other.Database, StringComparison.OrdinalIgnoreCase);
}

public sealed record DatabaseBackupRequest(string DatabaseName, string SqlServerPath, string BackupName, bool CopyOnly, string? EncryptionCertificate);
public sealed record DatabaseBackupHeader(string BackupType, bool CopyOnly, bool Compressed, bool HasChecksums, bool Damaged, bool Encrypted, string DatabaseName, long BackupBytes, string ServerName);
public sealed record DatabaseRestoreRequest(string SqlServerPath, string TargetDatabase, bool ReplaceExisting, bool InPlace);

/// <summary>
/// The database engine's own backup and restore (ADR-0034: SQL Server is authoritative; there is no logical export that pretends to be a backup). The SQL Server implementation uses BACKUP DATABASE with CHECKSUM, RESTORE
/// VERIFYONLY and RESTORE DATABASE. The connection string is a secret: it is never logged or put in an exception message.
/// </summary>
public interface IDatabaseBackupProvider
{
    string Engine { get; }
    bool Supported { get; }
    string UnsupportedReason { get; }
    /// <summary>The database the application itself is using.</summary>
    Task<DatabaseIdentity> IdentifyActiveAsync(CancellationToken ct);
    Task<DatabaseIdentity> IdentifyTargetAsync(string databaseName, CancellationToken ct);
    Task<bool> DatabaseExistsAsync(string databaseName, CancellationToken ct);
    Task BackupAsync(DatabaseBackupRequest request, CancellationToken ct);
    Task<DatabaseBackupHeader> ReadHeaderAsync(string sqlServerPath, CancellationToken ct);
    /// <summary>RESTORE VERIFYONLY with checksum verification. Returns null when the backup verifies, otherwise the (cleaned) reason.</summary>
    Task<string?> VerifyAsync(string sqlServerPath, CancellationToken ct);
    Task RestoreAsync(DatabaseRestoreRequest request, CancellationToken ct);
    /// <summary>The application connection string with the database replaced. A secret.</summary>
    string ConnectionStringFor(string databaseName);
    string ActiveDatabaseName { get; }
}

public sealed class UnsupportedDatabaseBackupProvider(string provider) : IDatabaseBackupProvider
{
    public string Engine => provider;
    public bool Supported => false;
    public string UnsupportedReason => $"Backup and restore need SQL Server. The {provider} provider is for demonstration and tests only and has no supported backup.";
    private DomainException No() => new("backup_unsupported", UnsupportedReason);
    public Task<DatabaseIdentity> IdentifyActiveAsync(CancellationToken ct) => throw No();
    public Task<DatabaseIdentity> IdentifyTargetAsync(string databaseName, CancellationToken ct) => throw No();
    public Task<bool> DatabaseExistsAsync(string databaseName, CancellationToken ct) => throw No();
    public Task BackupAsync(DatabaseBackupRequest request, CancellationToken ct) => throw No();
    public Task<DatabaseBackupHeader> ReadHeaderAsync(string sqlServerPath, CancellationToken ct) => throw No();
    public Task<string?> VerifyAsync(string sqlServerPath, CancellationToken ct) => throw No();
    public Task RestoreAsync(DatabaseRestoreRequest request, CancellationToken ct) => throw No();
    public string ConnectionStringFor(string databaseName) => throw No();
    public string ActiveDatabaseName => "";
}

public sealed partial class SqlServerBackupProvider(DatabaseOptions database, Microsoft.Extensions.Options.IOptions<RecoveryOptions> recovery, ILogger<SqlServerBackupProvider> log) : IDatabaseBackupProvider
{
    private RecoveryOptions options => recovery.Value;
    [GeneratedRegex("^[A-Za-z0-9_ .-]{1,128}$", RegexOptions.NonBacktracking)] private static partial Regex CertificateName();

    public string Engine => "SQL Server";
    public bool Supported => true;
    public string UnsupportedReason => "";
    public string ActiveDatabaseName => new SqlConnectionStringBuilder(database.ConnectionString).InitialCatalog;

    public string ConnectionStringFor(string databaseName)
    {
        var b = new SqlConnectionStringBuilder(database.ConnectionString) { InitialCatalog = databaseName };
        return b.ConnectionString;
    }

    private string AdminConnection()
    {
        var source = string.IsNullOrWhiteSpace(options.AdminConnectionString) ? database.ConnectionString : options.AdminConnectionString;
        return new SqlConnectionStringBuilder(source) { InitialCatalog = "master", Pooling = false }.ConnectionString;
    }

    private async Task<SqlConnection> OpenAdminAsync(CancellationToken ct)
    {
        var connection = new SqlConnection(AdminConnection());
        try { await connection.OpenAsync(ct); return connection; }
        catch (SqlException ex) { await connection.DisposeAsync(); throw new DomainException("admin_connection_failed", "The backup account could not connect to the database server: " + Redaction.Clean(ex.Message)); }
    }

    private SqlCommand Command(SqlConnection connection, string text)
    {
        var c = connection.CreateCommand();
        c.CommandText = text;
        c.CommandTimeout = Math.Max(0, options.DatabaseCommandTimeoutSeconds);
        return c;
    }

    private static string Quote(string name) => "[" + name.Replace("]", "]]", StringComparison.Ordinal) + "]";

    private static DatabaseIdentity ReadIdentity(DbDataReader r) => new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3));

    private const string IdentityQuery = "SELECT CAST(@@SERVERNAME AS nvarchar(256)), CAST(DB_NAME() AS nvarchar(256)), CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(64)), CAST(SERVERPROPERTY('Edition') AS nvarchar(128))";

    public async Task<DatabaseIdentity> IdentifyActiveAsync(CancellationToken ct)
    {
        await using var connection = new SqlConnection(new SqlConnectionStringBuilder(database.ConnectionString) { Pooling = false }.ConnectionString);
        try { await connection.OpenAsync(ct); }
        catch (SqlException ex) { throw new DomainException("database_unreachable", "The application database could not be reached: " + Redaction.Clean(ex.Message)); }
        await using var cmd = Command(connection, IdentityQuery);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return ReadIdentity(r);
    }

    public async Task<DatabaseIdentity> IdentifyTargetAsync(string databaseName, CancellationToken ct)
    {
        await using var connection = await OpenAdminAsync(ct);
        await using var cmd = Command(connection, "SELECT CAST(@@SERVERNAME AS nvarchar(256)), @n, CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(64)), CAST(SERVERPROPERTY('Edition') AS nvarchar(128))");
        cmd.Parameters.Add(new SqlParameter("@n", SqlDbType.NVarChar, 256) { Value = databaseName });
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return ReadIdentity(r);
    }

    public async Task<bool> DatabaseExistsAsync(string databaseName, CancellationToken ct)
    {
        await using var connection = await OpenAdminAsync(ct);
        await using var cmd = Command(connection, "SELECT CASE WHEN DB_ID(@n) IS NULL THEN 0 ELSE 1 END");
        cmd.Parameters.Add(new SqlParameter("@n", SqlDbType.NVarChar, 256) { Value = databaseName });
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)) == 1;
    }

    public async Task BackupAsync(DatabaseBackupRequest request, CancellationToken ct)
    {
        var withs = new List<string> { "CHECKSUM", "INIT", "NOFORMAT", "STATS = 25", "NAME = @name" };
        if (request.CopyOnly) withs.Add("COPY_ONLY");
        if (request.EncryptionCertificate is { Length: > 0 } cert)
        {
            if (!CertificateName().IsMatch(cert)) throw new DomainException("invalid_certificate", "The backup encryption certificate name is not valid.");
            withs.Add($"ENCRYPTION (ALGORITHM = AES_256, SERVER CERTIFICATE = {Quote(cert)})");
        }
        await using var connection = await OpenAdminAsync(ct);
        await using var cmd = Command(connection, $"BACKUP DATABASE @db TO DISK = @path WITH {string.Join(", ", withs)};");
        cmd.Parameters.Add(new SqlParameter("@db", SqlDbType.NVarChar, 256) { Value = request.DatabaseName });
        cmd.Parameters.Add(new SqlParameter("@path", SqlDbType.NVarChar, 1024) { Value = request.SqlServerPath });
        cmd.Parameters.Add(new SqlParameter("@name", SqlDbType.NVarChar, 128) { Value = request.BackupName });
        try { await cmd.ExecuteNonQueryAsync(ct); }
        catch (SqlException ex)
        {
            log.LogError("The database backup command failed (SQL error {Number}).", ex.Number);
            throw new DomainException("backup_failed", "The database server could not write the backup: " + Redaction.Clean(ex.Message) + " Check that the backup directory is shared with the database server and that the server's service account can write to it.");
        }
    }

    public async Task<DatabaseBackupHeader> ReadHeaderAsync(string sqlServerPath, CancellationToken ct)
    {
        await using var connection = await OpenAdminAsync(ct);
        await using var cmd = Command(connection, "RESTORE HEADERONLY FROM DISK = @path;");
        cmd.Parameters.Add(new SqlParameter("@path", SqlDbType.NVarChar, 1024) { Value = sqlServerPath });
        try
        {
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) throw new DomainException("backup_unreadable", "The database backup has no backup set.");
            string S(string column) { try { var o = r.GetOrdinal(column); return r.IsDBNull(o) ? "" : Convert.ToString(r.GetValue(o), System.Globalization.CultureInfo.InvariantCulture) ?? ""; } catch (IndexOutOfRangeException) { return ""; } }
            var type = S("BackupType") switch { "1" => "Full", "2" => "Transaction log", "5" => "Differential", var other => "Type " + other };
            _ = long.TryParse(S("BackupSize"), out var size);
            return new DatabaseBackupHeader(type, S("IsCopyOnly") is "1" or "True", S("Compressed") is "1" or "True", S("HasBackupChecksums") is "1" or "True", S("IsDamaged") is "1" or "True",
                S("KeyAlgorithm").Length > 0, S("DatabaseName"), size, S("ServerName"));
        }
        catch (SqlException ex) { throw new DomainException("backup_unreadable", "The database backup header could not be read: " + Redaction.Clean(ex.Message)); }
    }

    public async Task<string?> VerifyAsync(string sqlServerPath, CancellationToken ct)
    {
        await using var connection = await OpenAdminAsync(ct);
        await using var cmd = Command(connection, "RESTORE VERIFYONLY FROM DISK = @path WITH CHECKSUM;");
        cmd.Parameters.Add(new SqlParameter("@path", SqlDbType.NVarChar, 1024) { Value = sqlServerPath });
        try { await cmd.ExecuteNonQueryAsync(ct); return null; }
        catch (SqlException ex) { return Redaction.Clean(ex.Message); }
    }

    public async Task RestoreAsync(DatabaseRestoreRequest request, CancellationToken ct)
    {
        if (!request.InPlace && !SafeFiles.IsTargetDatabaseName(request.TargetDatabase)) throw new DomainException("invalid_target", "The target database name is not acceptable.");
        await using var connection = await OpenAdminAsync(ct);
        var moves = new List<(string Logical, string Physical)>();
        if (!request.InPlace)
        {
            await using var list = Command(connection, "RESTORE FILELISTONLY FROM DISK = @path;");
            list.Parameters.Add(new SqlParameter("@path", SqlDbType.NVarChar, 1024) { Value = request.SqlServerPath });
            string dataPath, logPath;
            await using (var paths = Command(connection, "SELECT CAST(SERVERPROPERTY('InstanceDefaultDataPath') AS nvarchar(512)), CAST(SERVERPROPERTY('InstanceDefaultLogPath') AS nvarchar(512));"))
            await using (var pr = await paths.ExecuteReaderAsync(ct))
            {
                await pr.ReadAsync(ct);
                dataPath = pr.IsDBNull(0) ? "" : pr.GetString(0); logPath = pr.IsDBNull(1) ? dataPath : pr.GetString(1);
            }
            if (dataPath.Length == 0) throw new DomainException("restore_failed", "The database server did not report its default data directory.");
            try
            {
                await using var r = await list.ExecuteReaderAsync(ct);
                var index = 0;
                while (await r.ReadAsync(ct))
                {
                    var logical = r.GetString(r.GetOrdinal("LogicalName"));
                    var type = r.GetString(r.GetOrdinal("Type"));
                    var extension = type == "L" ? ".ldf" : index == 0 ? ".mdf" : ".ndf";
                    var safe = Regex.Replace(logical, "[^A-Za-z0-9_]", "_");
                    var directory = type == "L" ? logPath : dataPath;
                    var separator = directory.Contains('\\') && !directory.Contains('/') ? "\\" : "/";
                    moves.Add((logical, directory.TrimEnd('/', '\\') + separator + request.TargetDatabase + "_" + safe + extension));
                    index++;
                }
            }
            catch (SqlException ex) { throw new DomainException("restore_failed", "The database backup's file list could not be read: " + Redaction.Clean(ex.Message)); }
        }

        var withs = new List<string> { "CHECKSUM", "STATS = 25", "RECOVERY" };
        if (request.ReplaceExisting || request.InPlace) withs.Add("REPLACE");
        for (var i = 0; i < moves.Count; i++) withs.Add($"MOVE @l{i} TO @p{i}");
        var quoted = Quote(request.TargetDatabase);
        try
        {
            if (request.InPlace)
            {
                await using var single = Command(connection, $"ALTER DATABASE {quoted} SET SINGLE_USER WITH ROLLBACK IMMEDIATE;");
                await single.ExecuteNonQueryAsync(ct);
                SqlConnection.ClearAllPools();
            }
            await using var cmd = Command(connection, $"RESTORE DATABASE @target FROM DISK = @path WITH {string.Join(", ", withs)};");
            cmd.Parameters.Add(new SqlParameter("@target", SqlDbType.NVarChar, 256) { Value = request.TargetDatabase });
            cmd.Parameters.Add(new SqlParameter("@path", SqlDbType.NVarChar, 1024) { Value = request.SqlServerPath });
            for (var i = 0; i < moves.Count; i++)
            {
                cmd.Parameters.Add(new SqlParameter($"@l{i}", SqlDbType.NVarChar, 256) { Value = moves[i].Logical });
                cmd.Parameters.Add(new SqlParameter($"@p{i}", SqlDbType.NVarChar, 1024) { Value = moves[i].Physical });
            }
            await cmd.ExecuteNonQueryAsync(ct);
            if (request.InPlace)
            {
                await using var multi = Command(connection, $"ALTER DATABASE {quoted} SET MULTI_USER;");
                await multi.ExecuteNonQueryAsync(ct);
            }
        }
        catch (SqlException ex)
        {
            log.LogError("The database restore command failed (SQL error {Number}).", ex.Number);
            throw new DomainException("restore_failed", "The database server could not restore the backup: " + Redaction.Clean(ex.Message));
        }
        finally { SqlConnection.ClearAllPools(); }
    }
}
