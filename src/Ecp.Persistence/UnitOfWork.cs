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
/// Work that must happen only if the outermost unit of work commits (for example dropping a cache entry for configuration that was just published). Actions run once after the commit, in order,
/// and are discarded on rollback. An action must not throw into the caller: a failure is swallowed because the commit has already happened.
/// </summary>
public interface IAfterCommit
{
    void Register(Action action);
}

public sealed class AfterCommitQueue : IAfterCommit
{
    private readonly List<Action> _actions = [];
    public void Register(Action action) { lock (_actions) _actions.Add(action); }
    internal void Run()
    {
        Action[] actions; lock (_actions) { actions = [.. _actions]; _actions.Clear(); }
        foreach (var action in actions) { try { action(); } catch { /* the commit has happened; a failed notification cannot undo it */ } }
    }
    internal void Discard() { lock (_actions) _actions.Clear(); }
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

public sealed class UnitOfWork(IScopedConnection connection, IEnumerable<IBeforeCommitHook> hooks, IEnumerable<IRollbackHook> rollbackHooks, AfterCommitQueue afterCommit) : IUnitOfWork
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
            afterCommit.Run();
            return result;
        }
        catch
        {
            await connection.RollbackAsync(CancellationToken.None);
            foreach (var hook in rollbackHooks) hook.OnRollback();
            afterCommit.Discard();
            throw;
        }
        finally { _depth--; }
    }

    public Task ExecuteAsync(Func<CancellationToken, Task> work, CancellationToken ct = default)
        => ExecuteAsync<bool>(async c => { await work(c); return true; }, ct);
}
