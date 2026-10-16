namespace Ecp.SharedKernel;

public enum ActorType { User, System, Integration, Support }

public interface IActorContext
{
    ActorType Type { get; }
    string? ActorId { get; }
    string Source { get; }
    string CorrelationId { get; }
}

/// <summary>Mutable per-scope actor and correlation information. Set by the host (requests) or the job runner.</summary>
public sealed class ActorContext : IActorContext
{
    public ActorType Type { get; set; } = ActorType.System;
    public string? ActorId { get; set; } = "system";
    public string Source { get; set; } = "Job";
    public string CorrelationId { get; set; } = Guid.NewGuid().ToString("N");
}
