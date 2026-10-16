using Ecp.Contracts;
using Ecp.Contracts.Domain;
using Ecp.Lifecycle.Definitions;

namespace Ecp.Lifecycle;

/// <summary>A new contract starts in the initial state of the contract workflow in force for its organisation, and is pinned to that release from the first moment.</summary>
internal sealed class LifecycleCreationPolicy(IWorkflowDefinitions definitions) : IContractCreationPolicy
{
    public async Task<ContractCreationSetup> ResolveAsync(Guid organisationId, CancellationToken ct = default)
    {
        var resolved = await definitions.CurrentAsync(WorkflowKinds.ContractLifecycle, organisationId, ct);
        var initial = resolved.Definition.Initial;
        return new ContractCreationSetup(initial.Code, new StateEffects(initial.Locked, initial.FreezeContent, initial.DocumentPolicy, initial.LockReason), resolved.WorkflowRef, resolved.ReleaseId);
    }
}
