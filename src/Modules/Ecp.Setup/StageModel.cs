using System.Security.Cryptography;
using Ecp.Audit;
using Ecp.Persistence;
using Ecp.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace Ecp.Setup;

/// <summary>Constants shared with the host's demonstration identity (a test keeps them in step).</summary>
public static class SetupDemo
{
    public const string ProviderKey = "demo";
    public const string Issuer = "https://demo.ecp.invalid";
}

/// <summary>Applies the synthetic demonstration data (Demo and Test only). Implemented by the host, which owns the sample data.</summary>
public interface IDemoDataApplier
{
    Task ApplyAsync(CancellationToken ct);
}

/// <summary>Everything a stage needs to do its work. Stages receive input values only through <see cref="Values"/>; nothing is read from the request directly.</summary>
public sealed class StageContext(SetupDocument doc, StageRecord record, IReadOnlyDictionary<string, string> values, SetupActor actor, SetupEnvironment env,
    IServiceProvider services, CancellationToken ct)
{
    public SetupDocument Doc { get; } = doc;
    public StageRecord Record { get; } = record;
    public IReadOnlyDictionary<string, string> Values { get; } = values;
    public SetupActor Actor { get; } = actor;
    public SetupEnvironment Env { get; } = env;
    public IServiceProvider Services { get; } = services;
    public CancellationToken Ct { get; } = ct;

    /// <summary>Runs every readiness check against the current (possibly unsaved) state. Set by the setup service.</summary>
    public Func<CancellationToken, Task<IReadOnlyList<ReadinessSnapshot>>> RunChecks { get; init; } = _ => Task.FromResult<IReadOnlyList<ReadinessSnapshot>>([]);

    public T Get<T>() where T : notnull => Services.GetRequiredService<T>();

    /// <summary>A fresh service scope (own database context) carrying the same actor, for work in a different tenant scope.</summary>
    public AsyncServiceScope NewScope()
    {
        var scope = Services.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
        var current = Get<ActorContext>(); var copy = scope.ServiceProvider.GetRequiredService<ActorContext>();
        copy.Type = current.Type; copy.ActorId = current.ActorId; copy.Source = current.Source; copy.CorrelationId = current.CorrelationId;
        return scope;
    }

    public string? Value(string name) => Values.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;
    public bool Flag(string name) => Values.TryGetValue(name, out var v) && (v is "true" or "on" or "1" || v.Contains("true", StringComparison.OrdinalIgnoreCase));

    public string Required(string name, string label)
        => Value(name) ?? throw new DomainException("required", $"{label} is required.");

    /// <summary>Remembers a non-secret input so the form is pre-filled when the stage is reopened.</summary>
    public void Remember(string name, string? value)
    {
        if (value is null) Record.Data.Remove("in." + name); else Record.Data["in." + name] = value;
    }

    public IDisposable UseTenant() => Get<TenantContext>().Use(Doc.TenantId ?? throw new DomainException("tenant_missing", "The tenant has not been created yet."));

    /// <summary>Records an audit event inside a unit of work, in the tenant scope (or the installation scope before the tenant exists).</summary>
    public async Task AuditAsync(string action, string entityType, string entityId, string? newValuesJson = null)
    {
        var tenant = Get<TenantContext>();
        using var _ = Doc.TenantId is { } t ? tenant.Use(t) : tenant.UseInstallationScope();
        var audit = Get<IAuditWriter>();
        await Get<IUnitOfWork>().ExecuteAsync(_ => { audit.Record(new AuditEntry(action, entityType, entityId, NewValuesJson: newValuesJson, Reason: Actor.Label)); return Task.CompletedTask; }, Ct);
    }
}

/// <summary>One step of the installation wizard. Stages are idempotent: running one twice with the same input changes nothing the second time.</summary>
public interface ISetupStage
{
    string Id { get; }
    string Title { get; }
    string Summary { get; }
    string ActionLabel { get; }
    IReadOnlyList<string> DependsOn { get; }
    /// <summary>May be re-run by a re-authenticated administrator after setup is complete.</summary>
    bool Amendable { get; }
    /// <summary>Can be skipped without blocking completion.</summary>
    bool Optional { get; }
    bool AppliesTo(SetupEnvironment env);
    IReadOnlyList<StageField> Fields(SetupDocument doc, SetupEnvironment env, Func<string, string?> remembered);
    IReadOnlyList<string> Notes(SetupDocument doc, SetupEnvironment env);
    Task<StageResult> ExecuteAsync(StageContext context);
    /// <summary>What the person is about to confirm, said in plain English before the step does anything (null for steps with nothing to review).</summary>
    Task<Ecp.Access.AccessReview?> ReviewAsync(StageContext context) => Task.FromResult<Ecp.Access.AccessReview?>(null);
}

public abstract class SetupStageBase : ISetupStage
{
    public abstract string Id { get; }
    public abstract string Title { get; }
    public abstract string Summary { get; }
    public virtual string ActionLabel => "Save and continue";
    public virtual IReadOnlyList<string> DependsOn => [];
    public virtual bool Amendable => false;
    public virtual bool Optional => false;
    public virtual bool AppliesTo(SetupEnvironment env) => true;
    public virtual IReadOnlyList<StageField> Fields(SetupDocument doc, SetupEnvironment env, Func<string, string?> remembered) => [];
    public virtual IReadOnlyList<string> Notes(SetupDocument doc, SetupEnvironment env) => [];
    public virtual Task<Ecp.Access.AccessReview?> ReviewAsync(StageContext context) => Task.FromResult<Ecp.Access.AccessReview?>(null);
    public abstract Task<StageResult> ExecuteAsync(StageContext context);
}

public enum CheckLevel { Blocking, Warning }

public sealed record CheckOutcome(bool Passed, string Message, CheckLevel Level = CheckLevel.Blocking)
{
    public static CheckOutcome Pass(string message) => new(true, message);
    public static CheckOutcome Fail(string message, CheckLevel level = CheckLevel.Blocking) => new(false, message, level);
}

public sealed record CheckContext(SetupDocument Doc, SetupEnvironment Env, IServiceProvider Services, CancellationToken Ct);

/// <summary>A readiness check run by the setup wizard and the administrator's setup page. Messages never contain secrets or connection details.</summary>
public interface ISetupCheck
{
    string Id { get; }
    string Description { get; }
    Task<CheckOutcome> RunAsync(CheckContext context);
}

public static class SetupSecrets
{
    /// <summary>Random URL-safe secret (256 bits). Used for the setup token and the setup session; only its hash is stored.</summary>
    public static string NewSecret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Hash(string secret) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secret)));

    public static bool Matches(string? secret, string? hash)
        => secret is not null && hash is not null && CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(Hash(secret)), System.Text.Encoding.ASCII.GetBytes(hash));
}
