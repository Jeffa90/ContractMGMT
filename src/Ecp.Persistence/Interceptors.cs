using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Ecp.Persistence;

/// <summary>Keeps the scope's shared connection open between EF operations so session context and transactions survive.</summary>
public sealed class KeepScopedConnectionOpenInterceptor : DbConnectionInterceptor
{
    public override InterceptionResult ConnectionClosing(DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
        => ShouldKeepOpen(connection) ? InterceptionResult.Suppress() : result;

    public override ValueTask<InterceptionResult> ConnectionClosingAsync(DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
        => ValueTask.FromResult(ShouldKeepOpen(connection) ? InterceptionResult.Suppress() : result);

    private static bool ShouldKeepOpen(DbConnection connection)
        => ScopedConnection.TryGet(connection, out var scoped) && !scoped.IsDisposing;
}

/// <summary>
/// Before every command on a scope's shared connection: (1) aligns the command's transaction with the scope's current unit-of-work
/// transaction (or none), so a context can never use a finished transaction or miss the active one; and (2) makes sure the SQL Server
/// session carries the current tenant scope, so Row-Level Security predicates always evaluate against the scope the application
/// believes it is in (fail closed when none is set).
/// </summary>
public sealed class TenantSessionContextInterceptor : DbCommandInterceptor
{
    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    { Apply(command); return result; }

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    { await ApplyAsync(command, cancellationToken); return result; }

    public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    { Apply(command); return result; }

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    { await ApplyAsync(command, cancellationToken); return result; }

    public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    { Apply(command); return result; }

    public override async ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
    { await ApplyAsync(command, cancellationToken); return result; }

    /// <summary>
    /// While a unit of work is active every command joins its transaction. Otherwise a transaction EF started itself (for example while
    /// migrating) is left alone, and only a finished (stale) transaction is cleared.
    /// </summary>
    private static void AlignTransaction(DbCommand command, ScopedConnection scoped)
    {
        var current = scoped.Transaction;
        if (current is not null)
        {
            if (!ReferenceEquals(command.Transaction, current)) command.Transaction = current;
        }
        else if (command.Transaction is { Connection: null }) command.Transaction = null;
    }

    private static void Apply(DbCommand command)
    {
        if (command.Connection is { } c && ScopedConnection.TryGet(c, out var scoped))
        {
            AlignTransaction(command, scoped);
            scoped.EnsureSessionContext(c, command.Transaction);
        }
    }

    private static async Task ApplyAsync(DbCommand command, CancellationToken ct)
    {
        if (command.Connection is { } c && ScopedConnection.TryGet(c, out var scoped))
        {
            AlignTransaction(command, scoped);
            await scoped.EnsureSessionContextAsync(c, command.Transaction, ct);
        }
    }
}
