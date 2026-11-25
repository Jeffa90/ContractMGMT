using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.SqlClient;

namespace JbsContracts.Api.Infrastructure;

/// <summary>Opens SQL Server connections. Every query is parameterised (Dapper); no SQL is ever built from user input.</summary>
public sealed class Db(AppSettings settings)
{
    static Db()
    {
        // Send .NET dates as datetime2, not the legacy datetime type (which rounds to 1/300 s and would break audit hashes).
        SqlMapper.AddTypeMap(typeof(DateTime), System.Data.DbType.DateTime2);
        SqlMapper.AddTypeMap(typeof(DateTime?), System.Data.DbType.DateTime2);
    }

    /// <summary>
    /// Begins a transaction that changes data. Every change takes the same short turn first (an application lock held until the
    /// transaction ends), so changes queue for a moment and can never deadlock one another, whatever they touch. The audit chain is
    /// written one event at a time anyway, so this costs nothing in practice for an app of this size.
    /// </summary>
    public static async Task<SqlTransaction> BeginWriteAsync(SqlConnection c)
    {
        var tx = c.BeginTransaction();
        await c.ExecuteAsync("EXEC sp_getapplock @Resource = N'contracts.write', @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 60000",
            transaction: tx);
        return tx;
    }

    public async Task<SqlConnection> OpenAsync(CancellationToken ct = default)
    {
        var builder = new SqlConnectionStringBuilder(settings.ConnectionString) { ApplicationName = "JBS Contracts" };
        var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync(ct);
        return connection;
    }
}

/// <summary>
/// Applies database/*.sql in name order, once each, recording them in dbo.SchemaVersions exactly as
/// scripts/ci/Invoke-SqlMigrations.ps1 does (same table, same line-ending-neutral SHA-256 checksum, same GO splitting).
/// Used only for local development and tests (Contracts:ApplyDatabaseScripts); on the servers the deploy runs the PowerShell script.
/// </summary>
public static partial class DatabaseScripts
{
    [GeneratedRegex(@"^\s*GO\s*(?:--[^\r\n]*)?\r?$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex GoLine();

    public static string ScriptsFolder => Path.Combine(AppContext.BaseDirectory, "database");

    public static string Checksum(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n"))));

    public static async Task<IReadOnlyList<string>> ApplyAsync(string connectionString, string? folder = null, CancellationToken ct = default)
    {
        folder ??= ScriptsFolder;
        var scripts = Directory.GetFiles(folder, "*.sql").OrderBy(Path.GetFileName, StringComparer.Ordinal).ToList();
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await connection.ExecuteAsync("""
            IF OBJECT_ID(N'dbo.SchemaVersions', N'U') IS NULL
            CREATE TABLE dbo.SchemaVersions (
                ScriptName NVARCHAR(255) NOT NULL CONSTRAINT PK_SchemaVersions PRIMARY KEY,
                Checksum   CHAR(64)      NOT NULL,
                AppliedAt  DATETIME2(0)  NOT NULL CONSTRAINT DF_SchemaVersions_AppliedAt DEFAULT SYSUTCDATETIME(),
                AppliedBy  NVARCHAR(128) NOT NULL CONSTRAINT DF_SchemaVersions_AppliedBy DEFAULT SUSER_SNAME(),
                Baselined  BIT           NOT NULL CONSTRAINT DF_SchemaVersions_Baselined DEFAULT 0
            );
            """);
        var applied = (await connection.QueryAsync<string>("SELECT ScriptName FROM dbo.SchemaVersions")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ran = new List<string>();
        foreach (var path in scripts)
        {
            var name = Path.GetFileName(path);
            if (applied.Contains(name)) continue;
            var text = await File.ReadAllTextAsync(path, ct);
            await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(ct);
            foreach (var batch in GoLine().Split(text).Where(b => b.Trim().Length > 0))
                await connection.ExecuteAsync(batch, transaction: tx, commandTimeout: 600);
            await connection.ExecuteAsync("INSERT INTO dbo.SchemaVersions (ScriptName, Checksum, Baselined) VALUES (@name, @checksum, 0)",
                new { name, checksum = Checksum(text) }, tx);
            await tx.CommitAsync(ct);
            ran.Add(name);
        }
        return ran;
    }
}
