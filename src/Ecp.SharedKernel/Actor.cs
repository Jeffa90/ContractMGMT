namespace Ecp.SharedKernel;

public enum ActorType { User, System, Integration, Support, Preview }

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

/// <summary>
/// The Test/Demo-only user preview the current request runs under, set by the host from the signed session after the preview has been re-validated against the database (never from request input).
/// While active, the current user is the person being previewed (so every decision uses their access, not the administrator's) and the request cannot change anything: the database contexts refuse
/// every write except audit (and the preview service's own record, while it suspends the guard).
/// </summary>
public sealed class PreviewContext
{
    private int _suspended;
    public Guid? PreviewId { get; private set; }
    public Guid PreviewerUserId { get; private set; }
    public Guid TargetUserId { get; private set; }
    public string TargetName { get; private set; } = "";
    public DateTime ExpiresUtc { get; private set; }

    public bool Active => PreviewId.HasValue;
    public bool WritesRefused => Active && _suspended == 0;

    public void Activate(Guid previewId, Guid previewerUserId, Guid targetUserId, string targetName, DateTime expiresUtc)
    {
        PreviewId = previewId; PreviewerUserId = previewerUserId; TargetUserId = targetUserId; TargetName = targetName; ExpiresUtc = expiresUtc;
    }

    /// <summary>Lets the preview service write its own session record (start, refusal count, end) while a preview is active. Nothing else uses this.</summary>
    public IDisposable SuspendGuard() { _suspended++; return new Release(this); }

    private sealed class Release(PreviewContext owner) : IDisposable { private bool _done; public void Dispose() { if (_done) return; _done = true; owner._suspended--; } }
}

/// <summary>A write was attempted while previewing another person. Preview shows the application; it never acts.</summary>
public sealed class PreviewReadOnlyException() : Exception("Preview is read-only. Nothing was changed.");
