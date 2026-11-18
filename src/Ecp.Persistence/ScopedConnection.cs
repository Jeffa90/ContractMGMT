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
}

/// <summary>
/// One database connection per DI scope, shared by every module context so that a business change and its audit
/// events commit in a single transaction (INV-16). Tenant isolation is enforced by the EF query filters and, in reviewed raw SQL,
/// by explicit TenantId predicates; the connection carries no session state (ADR-0044 retired SQL Server Row-Level Security).
/// </summary>
public sealed class ScopedConnection : IScopedConnection
{
    private static readonly ConditionalWeakTable<DbConnection, ScopedConnection> Registry = new();
    private bool _disposing;

    public ScopedConnection(DatabaseOptions options)
    {
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
        if (Provider == DatabaseProvider.Sqlite)
        {
            await using var pragma = Connection.CreateCommand();
            pragma.CommandText = "PRAGMA foreign_keys = ON;";
            await pragma.ExecuteNonQueryAsync(ct);
        }
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

    public async ValueTask DisposeAsync()
    {
        _disposing = true;
        if (Transaction is not null) await RollbackAsync(CancellationToken.None);
        await Connection.DisposeAsync();
    }
}
