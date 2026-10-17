using Ecp.Configuration;
using Ecp.Lifecycle.Definitions;
using Ecp.SharedKernel;

namespace Ecp.Lifecycle.Capabilities;

/// <summary>What in a workflow definition belongs to a capability: a transition role, a state role or a guard that the capability catalogue binds to it.</summary>
public sealed record CapabilityUse(string Capability, string What);

public static class WorkflowCapabilityUse
{
    /// <summary>Every place a definition uses a capability, found only through the closed vocabularies. Nothing here knows a business, a state name or a pack.</summary>
    public static IReadOnlyList<CapabilityUse> Of(WorkflowDefinition definition)
    {
        var found = new List<CapabilityUse>();
        foreach (var t in definition.Transitions)
        {
            if (CapabilityCatalogue.OfTransitionRole(t.Role) is { } byRole) found.Add(new(byRole, $"the step '{t.Display}'"));
            foreach (var g in t.Guards) if (CapabilityCatalogue.OfGuard(g) is { } byGuard) found.Add(new(byGuard, $"the step '{t.Display}' (guard {g})"));
        }
        foreach (var s in definition.States) if (CapabilityCatalogue.OfStateRole(s.Role) is { } byState) found.Add(new(byState, $"the state '{s.Display}'"));
        return found;
    }
}

/// <summary>
/// The one rule that stops a disabled capability from starting new work, whatever page or call asks. A step whose role belongs to a capability that is off is refused, unless the record is already inside that capability (its
/// current state also belongs to it): work under way can be finished, cancelled or returned, but nothing new is started. It is a rule about the process, never about who may act; the permission check has already been made.
/// </summary>
internal sealed class CapabilityGate(ICapabilityResolver resolver)
{
    /// <summary>The capability that blocks this step, or null when the step may be taken.</summary>
    public async Task<string?> BlockedByAsync(Guid organisationId, WorkflowDefinition definition, TransitionDefinition transition, string fromStatus, CancellationToken ct)
    {
        var capability = CapabilityCatalogue.OfTransitionRole(transition.Role);
        if (capability is null) return null;
        var set = await resolver.GetAsync(organisationId, ct);
        if (set.IsEnabled(capability)) return null;
        return CapabilityCatalogue.OfStateRole(definition.State(fromStatus)?.Role) == capability ? null : capability;
    }

    public async Task RequireAsync(Guid organisationId, WorkflowDefinition definition, TransitionDefinition transition, string fromStatus, CancellationToken ct)
    {
        if (await BlockedByAsync(organisationId, definition, transition, fromStatus, ct) is { } capability) throw Disabled(capability);
    }

    public static DomainException Disabled(string capability)
        => new("capability_disabled", $"{CapabilityCatalogue.ByKey[capability].Label} is not used by this [[organisation|lower]], so nothing new can be started. Work already under way can still be finished.");
}
