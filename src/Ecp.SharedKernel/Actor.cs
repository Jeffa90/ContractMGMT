namespace Ecp.SharedKernel;

public enum ActorType { User, System, Integration, Support }

public interface IActorContext
{
    ActorType Type { get; }
    string? ActorId { get; }
    string Source { get; }
    string CorrelationId { get; }
    /// <summary>The support or emergency elevation this work runs under, if any. Every audit event written under an elevation carries it.</summary>
    Guid? ElevationId { get; }
}

/// <summary>Mutable per-scope actor and correlation information. Set by the host (requests) or the job runner.</summary>
public sealed class ActorContext : IActorContext
{
    public ActorType Type { get; set; } = ActorType.System;
    public string? ActorId { get; set; } = "system";
    public string Source { get; set; } = "Job";
    public string CorrelationId { get; set; } = Guid.NewGuid().ToString("N");
    public Guid? ElevationId { get; set; }
}

/// <summary>
/// Evidence of when the signed-in person last authenticated (set by the host from the verified session, never from request input). Services that guard sensitive actions
/// (opening an elevation, force-unlocking a record) require it to be recent, so a stolen idle session cannot perform them.
/// </summary>
public sealed class ReauthenticationContext
{
    public DateTime? LastAuthenticationUtc { get; private set; }
    public void Set(DateTime utc) => LastAuthenticationUtc = utc;
    public void Clear() => LastAuthenticationUtc = null;

    public bool IsRecent(DateTime nowUtc, TimeSpan window) => LastAuthenticationUtc is { } at && nowUtc - at <= window && at <= nowUtc.AddMinutes(1);

    /// <summary>Throws <see cref="ReauthenticationRequiredException"/> unless the person authenticated within the window.</summary>
    public void Require(DateTime nowUtc, TimeSpan window)
    {
        if (!IsRecent(nowUtc, window)) throw new ReauthenticationRequiredException();
    }
}

/// <summary>The action needs a recent authentication (step-up). The host turns this into a redirect to the re-authentication page.</summary>
public sealed class ReauthenticationRequiredException() : Exception("This action needs you to confirm your identity again. Sign in again and retry.");
