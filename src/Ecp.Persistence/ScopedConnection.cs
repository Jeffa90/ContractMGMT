using System.Data;
using System.Data.Common;
using System.Runtime.CompilerServices;
using Ecp.SharedKernel;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;

namespace Ecp.Persistence;

public interface IScopedConnection : IAsyncDisposable
{
    DatabaseProvider Provider { get; }
    DbConnection Connection { get; }
    DbTransaction? Transaction { get; }
    Task EnsureOpenAsync(CancellationToken ct);
    Task<DbTransaction> BeginTransactionAsync(CancellationToken ct);
    Task CommitAsync(CancellationToken ct);
    Task RollbackAsync(CancellationToken ct);
    /// <summary>Opens the connection and applies the current tenant scope to it (SQL Server session context), for code that runs its own fixed, reviewed commands (backup inventories, database integrity checks) instead of an EF context.</summary>
    Task ApplyTenantScopeAsync(CancellationToken ct);
}

/// <summary>
/// One database connection per DI scope, shared by every module context so that a business change and its audit
/// events commit in a single transaction (INV-16). On SQL Server the tenant scope is applied to
/// <c>SESSION_CONTEXT</c> before every command, which Row-Level Security predicates read (ADR-0003).
/// </summary>
public sealed class ScopedConnection : IScopedConnection
{
    private static readonly ConditionalWeakTable<DbConnection, ScopedConnection> Registry = new();
    private readonly ITenantContext _tenant;
    private long _appliedVersion = -1;
    private bool _disposing;

    public ScopedConnection(DatabaseOptions options, ITenantContext tenant)
    {
        _tenant = tenant;
        Provider = options.Provider;
        Connection = options.Provider == DatabaseProvider.SqlServer
            ? new SqlConnection(options.ConnectionString)
            : new SqliteConnection(options.ConnectionString);
        Registry.Add(Connection, this);
    }

    public DatabaseProvider Provider { get; }
    public DbConnection Connection { get; }
    public DbTransaction? Transaction { get; private set; }
    internal bool IsDisposing => _disposing;

    internal static bool TryGet(DbConnection connection, out ScopedConnection scoped)
        => Registry.TryGetValue(connection, out scoped!);

    public async Task EnsureOpenAsync(CancellationToken ct)
    {
        if (Connection.State == ConnectionState.Open) return;
        await Connection.OpenAsync(ct);
        _appliedVersion = -1;
        if (Provider == DatabaseProvider.Sqlite)
        {
            await using var pragma = Connection.CreateCommand();
            pragma.CommandText = "PRAGMA foreign_keys = ON;";
            await pragma.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task ApplyTenantScopeAsync(CancellationToken ct)
    {
        await EnsureOpenAsync(ct);
        await EnsureSessionContextAsync(Connection, Transaction, ct);
    }

    public async Task<DbTransaction> BeginTransactionAsync(CancellationToken ct)
    {
        await EnsureOpenAsync(ct);
        Transaction = await Connection.BeginTransactionAsync(ct);
        return Transaction;
    }

    public async Task CommitAsync(CancellationToken ct)
    {
        if (Transaction is null) return;
        var tx = Transaction;
        Transaction = null;
        await tx.CommitAsync(ct);
        await tx.DisposeAsync();
    }

    public async Task RollbackAsync(CancellationToken ct)
    {
        if (Transaction is null) return;
        var tx = Transaction;
        Transaction = null;
        try { await tx.RollbackAsync(ct); }
        finally { await tx.DisposeAsync(); }
    }

    /// <summary>Applies the current tenant scope to the SQL Server session if it has changed. No-op for other providers.</summary>
    internal async Task EnsureSessionContextAsync(DbConnection connection, DbTransaction? transaction, CancellationToken ct)
    {
        if (Provider != DatabaseProvider.SqlServer || _appliedVersion == _tenant.Version) return;
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "EXEC sp_set_session_context N'TenantId', @t; EXEC sp_set_session_context N'IsInstallationScope', @i;";
        var t = cmd.CreateParameter(); t.ParameterName = "@t";
        t.Value = _tenant.TenantId.HasValue ? _tenant.TenantId.Value : DBNull.Value;
        var i = cmd.CreateParameter(); i.ParameterName = "@i"; i.DbType = DbType.Boolean; i.Value = _tenant.IsInstallationScope;
        cmd.Parameters.Add(t); cmd.Parameters.Add(i);
        await cmd.ExecuteNonQueryAsync(ct);
        _appliedVersion = _tenant.Version;
    }

    internal void EnsureSessionContext(DbConnection connection, DbTransaction? transaction)
        => EnsureSessionContextAsync(connection, transaction, CancellationToken.None).GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        _disposing = true;
        if (Transaction is not null) await RollbackAsync(CancellationToken.None);
        await Connection.DisposeAsync();
    }
}
