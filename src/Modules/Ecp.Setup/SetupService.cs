using Ecp.Operations;
using Ecp.Persistence;
using Ecp.SharedKernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ecp.Setup;

public interface ISetupService
{
    /// <summary>True once installation has been completed. Cheap after the first positive answer.</summary>
    Task<bool> IsCompleteAsync(CancellationToken ct = default);

    /// <summary>Creates the resumable state on first start. Never issues a token.</summary>
    Task EnsureInitialisedAsync(CancellationToken ct = default);

    /// <summary>Issues a new single-use token (replacing any earlier token and ending any open session). The token is returned once and never stored.</summary>
    Task<string> IssueTokenAsync(CancellationToken ct = default);

    /// <summary>Exchanges the token for a setup session. The token is consumed. Refused after completion, from non-loopback addresses unless allowed, or while a session is open.</summary>
    Task<SetupSessionResult> BeginSessionAsync(string? token, bool fromLoopback, CancellationToken ct = default);

    /// <summary>True while the session is the single open session; slides its expiry.</summary>
    Task<bool> ValidateSessionAsync(string? sessionId, CancellationToken ct = default);
    Task EndSessionAsync(CancellationToken ct = default);

    Task<SetupSnapshot> SnapshotAsync(bool amending = false, CancellationToken ct = default);
    Task<StageDescriptor?> DescribeAsync(string stageId, bool amending = false, CancellationToken ct = default);
    Task<StageResult> ExecuteAsync(string stageId, IReadOnlyDictionary<string, string> values, SetupActor actor, bool amending = false, CancellationToken ct = default);
    Task<StageResult> SkipAsync(string stageId, SetupActor actor, CancellationToken ct = default);

    /// <summary>Runs the readiness checks without changing any stage (used by the administrator's setup page).</summary>
    Task<IReadOnlyList<ReadinessSnapshot>> RunChecksAsync(CancellationToken ct = default);

    /// <summary>Sanitised report: no secrets, secret-reference targets, connection strings, tokens or hashes.</summary>
    Task<SetupReport> BuildReportAsync(CancellationToken ct = default);

    /// <summary>Demo and Test only: marks installation complete when the host seeded demonstration data and no organisation set-up is wanted.</summary>
    Task AutoCompleteForDemoAsync(string reason, CancellationToken ct = default);
}

internal sealed class SetupService(ISetupStore store, SetupEnvironment env, IEnumerable<ISetupStage> stageList, IEnumerable<ISetupCheck> checkList,
    IServiceScopeFactory scopes, IClock clock, ILogger<SetupService> logger) : ISetupService
{
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromHours(24);
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(60);
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private readonly IReadOnlyList<ISetupStage> _stages = Order(stageList);
    private readonly ISetupCheck[] _checks = [.. checkList];
    private volatile bool _complete;
    private int _failures;
    private DateTime _lockedUntil;

    /// <summary>Stage display order. Dependencies are enforced separately; this is the order the wizard presents.</summary>
    private static readonly string[] Order_ = ["mode", "database", "storage", "tenant", "pack", "identity", "admin", "organisation", "branding", "classification", "notifications", "access", "demo", "readiness", "complete"];

    private static IReadOnlyList<ISetupStage> Order(IEnumerable<ISetupStage> stages)
        => [.. stages.OrderBy(s => { var i = Array.IndexOf(Order_, s.Id); return i < 0 ? int.MaxValue : i; })];

    public async Task<bool> IsCompleteAsync(CancellationToken ct = default)
    {
        if (_complete) return true;
        if (store.CompletionMarkerExists()) return _complete = true;
        var doc = await store.LoadAsync(ct);
        if (doc is { IsComplete: true })
        {
            await store.SaveAsync(doc, ct); // restores the marker if it was removed; the database copy is authoritative
            return _complete = true;
        }
        return false;
    }

    private async Task<SetupDocument> LoadOrCreateAsync(CancellationToken ct)
    {
        var doc = await store.LoadAsync(ct);
        if (doc is not null) return doc;
        doc = new SetupDocument { Mode = env.HostMode, CreatedUtc = clock.UtcNow };
        await store.SaveAsync(doc, ct);
        return doc;
    }

    public async Task EnsureInitialisedAsync(CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try { await LoadOrCreateAsync(ct); }
        finally { Gate.Release(); }
    }

    public async Task<string> IssueTokenAsync(CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var doc = await LoadOrCreateAsync(ct);
            if (doc.IsComplete) throw new DomainException("setup_complete", "Setup is complete. A signed-in administrator reopens it from Administration > Setup after re-authenticating.");
            var token = SetupSecrets.NewSecret();
            doc.TokenHash = SetupSecrets.Hash(token);
            doc.TokenExpiresUtc = clock.UtcNow.Add(TokenLifetime);
            doc.SessionHash = null; doc.SessionExpiresUtc = null;
            await store.SaveAsync(doc, ct);
            return token;
        }
        finally { Gate.Release(); }
    }

    public async Task<SetupSessionResult> BeginSessionAsync(string? token, bool fromLoopback, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var now = clock.UtcNow;
            if (_lockedUntil > now) return new(false, null, "Too many incorrect tokens. Wait a few minutes, or issue a new token from the command line.");
            var doc = await LoadOrCreateAsync(ct);
            if (doc.IsComplete) return new(false, null, "Setup is already complete.");
            if (!fromLoopback && !env.AllowRemoteSetup) return new(false, null, "Setup can only be opened from the server itself unless remote setup is explicitly allowed (Ecp:Setup:AllowRemote).");
            if (doc.SessionHash is not null && doc.SessionExpiresUtc > now) return new(false, null, "A setup session is already open. It ends after 60 minutes without activity, or issue a new token from the command line.");
            var valid = doc.TokenHash is not null && doc.TokenExpiresUtc > now && SetupSecrets.Matches(token?.Trim(), doc.TokenHash);
            if (!valid)
            {
                if (++_failures >= 5) { _lockedUntil = now.AddMinutes(5); _failures = 0; }
                logger.LogWarning("A setup token was refused.");
                return new(false, null, "The setup token is not valid, has expired, or was already used. Issue a new token from the command line.");
            }
            _failures = 0;
            var session = SetupSecrets.NewSecret();
            doc.TokenHash = null; doc.TokenExpiresUtc = null;            // single use
            doc.SessionHash = SetupSecrets.Hash(session);
            doc.SessionExpiresUtc = now.Add(SessionLifetime);
            await store.SaveAsync(doc, ct);
            logger.LogInformation("A setup session was opened.");
            return new(true, session, null);
        }
        finally { Gate.Release(); }
    }

    public async Task<bool> ValidateSessionAsync(string? sessionId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(sessionId) || _complete) return false;
        await Gate.WaitAsync(ct);
        try
        {
            var doc = await store.LoadAsync(ct);
            var now = clock.UtcNow;
            if (doc is null || doc.IsComplete || doc.SessionHash is null || doc.SessionExpiresUtc <= now || !SetupSecrets.Matches(sessionId, doc.SessionHash)) return false;
            if (doc.SessionExpiresUtc - now < SessionLifetime - TimeSpan.FromMinutes(5)) // slide, but not on every request
            {
                doc.SessionExpiresUtc = now.Add(SessionLifetime);
                await store.SaveAsync(doc, ct);
            }
            return true;
        }
        finally { Gate.Release(); }
    }

    public async Task EndSessionAsync(CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var doc = await store.LoadAsync(ct);
            if (doc is null || doc.SessionHash is null) return;
            doc.SessionHash = null; doc.SessionExpiresUtc = null;
            await store.SaveAsync(doc, ct);
        }
        finally { Gate.Release(); }
    }

    private bool IsAvailable(ISetupStage stage, SetupDocument doc, bool amending, out string? reason)
    {
        reason = null;
        if (!stage.AppliesTo(env)) { reason = "Not applicable in this mode."; return false; }
        if (doc.IsComplete && !(amending && stage.Amendable)) { reason = doc.IsComplete ? "Setup is complete." : null; return false; }
        var missing = stage.DependsOn.Where(d => _stages.Any(s => s.Id == d && s.AppliesTo(env)) && !doc.Done(d)).Select(d => _stages.First(s => s.Id == d).Title).ToList();
        if (missing.Count > 0) { reason = "Complete first: " + string.Join(", ", missing) + "."; return false; }
        return true;
    }

    public async Task<SetupSnapshot> SnapshotAsync(bool amending = false, CancellationToken ct = default)
    {
        var doc = await store.LoadAsync(ct) ?? new SetupDocument { Mode = env.HostMode, CreatedUtc = clock.UtcNow };
        var applicable = _stages.Where(s => s.AppliesTo(env)).ToList();
        var summaries = applicable.Select(s => new StageSummary(s.Id, s.Title, doc.StatusOf(s.Id), IsAvailable(s, doc, amending, out var r), r)).ToList();
        var next = applicable.FirstOrDefault(s => !doc.Done(s.Id) && IsAvailable(s, doc, amending, out _))?.Id;
        return new SetupSnapshot(doc.IsComplete, summaries.Count(s => s.Status is StageStatus.Complete or StageStatus.Skipped), summaries.Count, next, summaries, doc.Mode, doc.CompletedUtc, doc.TenantId);
    }

    public async Task<StageDescriptor?> DescribeAsync(string stageId, bool amending = false, CancellationToken ct = default)
    {
        var stage = _stages.FirstOrDefault(s => s.Id == stageId);
        if (stage is null) return null;
        var doc = await store.LoadAsync(ct) ?? new SetupDocument { Mode = env.HostMode, CreatedUtc = clock.UtcNow };
        var record = doc.Stages.TryGetValue(stageId, out var r) ? r : new StageRecord();
        string? Remembered(string name) => record.Data.TryGetValue("in." + name, out var v) ? v : null;
        var fields = stage.Fields(doc, env, Remembered).Select(f => f with { Value = Remembered(f.Name) ?? f.Value }).ToList();
        var available = IsAvailable(stage, doc, amending, out var reason);
        Ecp.Access.AccessReview? review = null;
        if (available && doc.TenantId is not null)
        {
            // A step that asks for confirmation shows what it will do first. The review is a dry run in its own scope and changes nothing.
            await using var scope = scopes.CreateAsyncScope();
            var actorContext = scope.ServiceProvider.GetRequiredService<ActorContext>();
            actorContext.Type = ActorType.System; actorContext.ActorId = "setup:review"; actorContext.Source = "Setup";
            try { review = await stage.ReviewAsync(new StageContext(doc, record, new Dictionary<string, string>(), new SetupActor("review"), env, scope.ServiceProvider, ct)); }
            catch (Exception ex) when (ex is DomainException or ForbiddenException or ResourceNotFoundException) { logger.LogInformation("Setup review for {Stage} could not be prepared: {Reason}", stageId, ex.Message); }
        }
        return new StageDescriptor(stage.Id, stage.Title, stage.Summary, doc.StatusOf(stageId), fields, stage.Notes(doc, env), stage.ActionLabel, record.Note, available, reason, review);
    }

    public async Task<StageResult> ExecuteAsync(string stageId, IReadOnlyDictionary<string, string> values, SetupActor actor, bool amending = false, CancellationToken ct = default)
    {
        var stage = _stages.FirstOrDefault(s => s.Id == stageId);
        if (stage is null) return StageResult.Fail("That step does not exist.");
        await Gate.WaitAsync(ct);
        try
        {
            var doc = await LoadOrCreateAsync(ct);
            if (!IsAvailable(stage, doc, amending, out var reason)) return StageResult.Fail(reason ?? "This step is not available yet.");
            var record = doc.Stage(stageId);
            StageResult result;
            await using (var scope = scopes.CreateAsyncScope())
            {
                var actorContext = scope.ServiceProvider.GetRequiredService<ActorContext>();
                // A signed-in administrator amending a step is recorded as that user; the setup session before installation is recorded as the system acting for setup.
                actorContext.Type = actor.UserId is null ? ActorType.System : ActorType.User;
                actorContext.ActorId = actor.UserId?.ToString() ?? "setup:" + actor.Label;
                actorContext.Source = "Setup";
                try { result = await stage.ExecuteAsync(new StageContext(doc, record, values, actor, env, scope.ServiceProvider, ct) { RunChecks = c => RunChecksAsync(doc, c) }); }
                catch (DomainException ex) { result = StageResult.Fail(ex.Message); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Setup step {Stage} failed", stageId);
                    result = StageResult.Fail("The step could not be completed. The cause is in the application log; no change was made that cannot be repeated safely.");
                }
            }
            record.UpdatedUtc = clock.UtcNow;
            record.Note = result.Message;
            if (result.Succeeded) { record.Status = StageStatus.Complete; record.CompletedBy = actor.Label; }
            else if (record.Status == StageStatus.NotStarted) record.Status = StageStatus.InProgress;
            await store.SaveAsync(doc, ct);
            if (doc.IsComplete) _complete = true;
            return result;
        }
        finally { Gate.Release(); }
    }

    public async Task<StageResult> SkipAsync(string stageId, SetupActor actor, CancellationToken ct = default)
    {
        var stage = _stages.FirstOrDefault(s => s.Id == stageId);
        if (stage is null) return StageResult.Fail("That step does not exist.");
        if (!stage.Optional) return StageResult.Fail("This step is required.");
        await Gate.WaitAsync(ct);
        try
        {
            var doc = await LoadOrCreateAsync(ct);
            if (!IsAvailable(stage, doc, false, out var reason)) return StageResult.Fail(reason ?? "This step is not available yet.");
            var record = doc.Stage(stageId);
            record.Status = StageStatus.Skipped; record.UpdatedUtc = clock.UtcNow; record.CompletedBy = actor.Label; record.Note = "Skipped.";
            await store.SaveAsync(doc, ct);
            return StageResult.Ok("Skipped.");
        }
        finally { Gate.Release(); }
    }

    public async Task<IReadOnlyList<ReadinessSnapshot>> RunChecksAsync(CancellationToken ct = default)
    {
        var doc = await store.LoadAsync(ct) ?? new SetupDocument { Mode = env.HostMode, CreatedUtc = clock.UtcNow };
        return await RunChecksAsync(doc, ct);
    }

    internal async Task<IReadOnlyList<ReadinessSnapshot>> RunChecksAsync(SetupDocument doc, CancellationToken ct)
    {
        var results = new List<ReadinessSnapshot>();
        foreach (var check in _checks)
        {
            await using var scope = scopes.CreateAsyncScope();
            try
            {
                var outcome = await check.RunAsync(new CheckContext(doc, env, scope.ServiceProvider, ct));
                results.Add(new ReadinessSnapshot(check.Id, check.Description, outcome.Level.ToString(), outcome.Passed ? "Pass" : "Fail", outcome.Message));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Setup check {Check} failed to run", check.Id);
                results.Add(new ReadinessSnapshot(check.Id, check.Description, nameof(CheckLevel.Blocking), "Fail", "The check could not be completed."));
            }
        }
        return results;
    }

    public async Task<SetupReport> BuildReportAsync(CancellationToken ct = default)
    {
        var doc = await store.LoadAsync(ct) ?? new SetupDocument { Mode = env.HostMode, CreatedUtc = clock.UtcNow };
        var checks = await RunChecksAsync(doc, ct);
        var acks = doc.Acknowledgements.GroupBy(a => a.CheckId).ToDictionary(g => g.Key, g => g.Last());
        var integrations = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["database"] = env.DatabaseProvider + (env.ConnectionFromSecretReference ? " (connection from secret reference)" : ""),
            ["governance"] = env.GovernanceName ?? "not reported",
            ["identity"] = doc.Get("identity", "in.kind") ?? "not configured",
            ["email"] = doc.Get("notifications", "in.provider") ?? "not configured",
            ["storage"] = doc.Get("storage", "provider") ?? "not verified",
        };
        var notes = new List<string>();
        if (env.HostMode != EcpMode.Production) notes.Add($"Installed in {env.HostMode} mode. This installation is not approved for production use; approval is external governance.");
        if (doc.DemoDataApplied) notes.Add("Synthetic demonstration data was applied.");
        return new SetupReport(clock.UtcNow, env.HostMode.ToString(), doc.Deployment, env.Version, doc.IsComplete, doc.CompletedUtc, doc.TenancyMode,
            [.. _stages.Where(s => s.AppliesTo(env)).Select(s => (s.Id, s.Title, doc.StatusOf(s.Id).ToString(), doc.Stages.TryGetValue(s.Id, out var r) ? r.UpdatedUtc : null))],
            [.. checks.Select(c => new SetupReportCheck(c.Id, c.Description, c.Severity, c.Status, c.Message,
                acks.TryGetValue(c.Id, out var a) ? a.By : null, acks.TryGetValue(c.Id, out var b) ? b.Reason : null))],
            integrations, doc.PackId, doc.Get("admin", "names") is { } names ? names.Split('|', StringSplitOptions.RemoveEmptyEntries) : [], notes);
    }

    public async Task AutoCompleteForDemoAsync(string reason, CancellationToken ct = default)
    {
        if (env.HostMode == EcpMode.Production) throw new InvalidOperationException("Setup cannot be completed automatically in Production.");
        await Gate.WaitAsync(ct);
        try
        {
            var doc = await LoadOrCreateAsync(ct);
            if (doc.IsComplete) { _complete = true; return; }
            doc.Mode = env.HostMode;
            doc.CompletedUtc = clock.UtcNow; doc.CompletedBy = "automatic (" + reason + ")";
            doc.TokenHash = null; doc.TokenExpiresUtc = null; doc.SessionHash = null; doc.SessionExpiresUtc = null;
            doc.DemoDataApplied = true;
            await store.SaveAsync(doc, ct);
            _complete = true;
        }
        finally { Gate.Release(); }
    }
}
