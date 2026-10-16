namespace Ecp.Persistence;

/// <summary>Runs before commit inside the unit of work's transaction (for example the audit writer).</summary>
public interface IBeforeCommitHook
{
    Task BeforeCommitAsync(CancellationToken ct);
}

/// <summary>Runs after the outermost unit of work rolls back: discards buffered work (audit events) and tracked entities so none of it can leak into a later transaction.</summary>
public interface IRollbackHook
{
    void OnRollback();
}

/// <summary>
/// Executes work in one database transaction across all module contexts in the scope. A business change, its audit
/// events and its outbox messages therefore commit or roll back together.
/// </summary>
public interface IUnitOfWork
{
    Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct = default);
    Task ExecuteAsync(Func<CancellationToken, Task> work, CancellationToken ct = default);
}

public sealed class UnitOfWork(IScopedConnection connection, IEnumerable<IBeforeCommitHook> hooks, IEnumerable<IRollbackHook> rollbackHooks) : IUnitOfWork
{
    private int _depth;

    public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct = default)
    {
        if (_depth > 0) return await work(ct); // join the outer unit of work
        _depth++;
        try
        {
            await connection.BeginTransactionAsync(ct);
            var result = await work(ct);
            foreach (var hook in hooks) await hook.BeforeCommitAsync(ct);
            await connection.CommitAsync(ct);
            return result;
        }
        catch
        {
            await connection.RollbackAsync(CancellationToken.None);
            foreach (var hook in rollbackHooks) hook.OnRollback();
            throw;
        }
        finally { _depth--; }
    }

    public Task ExecuteAsync(Func<CancellationToken, Task> work, CancellationToken ct = default)
        => ExecuteAsync<bool>(async c => { await work(c); return true; }, ct);
}
