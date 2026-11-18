using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Ecp.Persistence;

/// <summary>Keeps the scope's shared connection open between EF operations so the unit-of-work transaction survives.</summary>
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
/// Before every command on a scope's shared connection, aligns the command's transaction with the scope's current unit-of-work
/// transaction (or none), so a context can never use a finished transaction or miss the active one.
/// </summary>
public sealed class ScopedTransactionInterceptor : DbCommandInterceptor
{
    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    { Apply(command); return result; }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    { Apply(command); return ValueTask.FromResult(result); }

    public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    { Apply(command); return result; }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    { Apply(command); return ValueTask.FromResult(result); }

    public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    { Apply(command); return result; }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
    { Apply(command); return ValueTask.FromResult(result); }

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
        if (command.Connection is { } c && ScopedConnection.TryGet(c, out var scoped)) AlignTransaction(command, scoped);
    }
}
