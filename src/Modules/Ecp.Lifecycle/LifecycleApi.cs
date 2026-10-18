using Ecp.Approvals;
using Ecp.Contracts;
using Ecp.Lifecycle.Definitions;
using Ecp.Lifecycle.Domain;
using Ecp.Lifecycle.Persistence;
using Ecp.Persistence;
using Ecp.SharedKernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ecp.Lifecycle;

/// <summary>A request to take one step of the workflow. The caller never names the target state: the pinned definition decides it from the transition code and the current state.</summary>
public sealed record TransitionRequest(Guid ContractId, string TransitionCode, string? Comment = null, string? Reason = null, string? ExpectedStatus = null, string? ClientRequestId = null);

public sealed record TransitionResult(Guid SubjectId, string FromStatus, string ToStatus, string TransitionCode, bool Replayed);

/// <summary>A step offered from the current state, with whether the caller may take it now and, if not, why in plain words.</summary>
public sealed record AvailableTransition(string Code, string Display, string ToStatus, string ToDisplay, bool Allowed, string? BlockedReason, bool CommentRequired, bool ReasonRequired, bool Managed, string Role);

public sealed record HistoryItem(DateTime OccurredUtc, string FromStatus, string FromDisplay, string ToStatus, string ToDisplay, string TransitionCode, string TransitionDisplay, string Actor, string? Comment, string SubjectKind, string? WorkflowRef);

/// <summary>Where a contract stands in its workflow, for the contract page: the state, the workflow it is pinned to and the states of that workflow in order.</summary>
public sealed record LifecycleState(string Status, string StatusDisplay, string? StatusRole, string WorkflowRef, int ReleaseNumber, bool IsPinned, bool Locked, bool ContentFrozen, IReadOnlyList<LifecycleStateStep> Path,
    IReadOnlyList<string> UploadTypes);

public sealed record LifecycleStateStep(string Code, string Display, bool IsCurrent);

/// <summary>The step could not be taken. <see cref="Reasons"/> are the individual plain-language causes.</summary>
public sealed class TransitionBlockedException(string message, IReadOnlyList<string> reasons) : DomainException("transition_blocked", message)
{
    public IReadOnlyList<string> Reasons { get; } = reasons;
}

/// <summary>Takes a contract through its pinned workflow on behalf of the signed-in user. Steps that carry their own data (a Legal round, an approval, a Variation) are refused here and offered by their own services.</summary>
public interface ILifecycleService
{
    Task<IReadOnlyList<AvailableTransition>> AvailableAsync(Guid contractId, CancellationToken ct = default);
    Task<TransitionResult> TransitionAsync(TransitionRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<HistoryItem>> HistoryAsync(Guid contractId, CancellationToken ct = default);
    Task<LifecycleState> StateAsync(Guid contractId, CancellationToken ct = default);
}

/// <summary>Disposition checks the lifecycle consults before an archive. Retention periods (Q-08) and legal holds are not configurable yet, so the default allows archive; a deployment that holds legal-hold data registers its own.</summary>
public interface IDispositionChecks
{
    /// <summary>Null when the contract may be archived, otherwise why not, in plain words.</summary>
    Task<string?> BlockReasonAsync(Guid contractId, CancellationToken ct = default);
}

internal sealed class NoDispositionBlocks : IDispositionChecks
{
    public Task<string?> BlockReasonAsync(Guid contractId, CancellationToken ct = default) => Task.FromResult<string?>(null);
}

public static class LifecycleModule
{
    public const string Name = "Lifecycle";
    public const string Schema = "lifecycle";
    public const int Tier = 7;

    public static IServiceCollection AddLifecycleModule(this IServiceCollection services, Action<LifecycleOptions>? configure = null)
    {
        services.AddScoped<Ecp.SharedKernel.IUpgradeProbe, LifecycleUpgradeProbe>();
        services.AddOptions<LifecycleOptions>();
        if (configure is not null) services.Configure(configure);
        services.AddApprovalsModule();
        services.AddModuleDbContext<LifecycleDbContext>(Name, Tier, Schema);
        services.AddScoped<Ecp.Configuration.IConfigurationKind>(sp => new WorkflowDefinitionKind(WorkflowKinds.ContractLifecycle, sp.GetRequiredService<IApprovalMatrixStore>(), () => sp.GetRequiredService<Ecp.Configuration.ICapabilityResolver>()));
        services.AddScoped<Ecp.Configuration.IConfigurationKind>(sp => new WorkflowDefinitionKind(WorkflowKinds.Variation, sp.GetRequiredService<IApprovalMatrixStore>(), () => sp.GetRequiredService<Ecp.Configuration.ICapabilityResolver>()));
        services.AddScoped<IWorkflowDefinitions, WorkflowDefinitionStore>();
        services.TryAddScoped<IDispositionChecks, NoDispositionBlocks>();
        services.AddScoped<GuardEvaluator>();
        services.AddScoped<Ecp.Lifecycle.Capabilities.CapabilityGate>();
        services.AddScoped<Ecp.Configuration.ICapabilityCompatibility, Ecp.Lifecycle.Capabilities.LifecycleCapabilityCompatibility>();
        services.AddScoped<LifecycleEngine>();
        services.AddScoped<ILifecycleService, LifecycleService>();
        services.AddScoped<LifecycleEvents>();
        services.AddScoped<ILifecycleOutbox, LifecycleOutbox>();
        services.AddScoped<IContractCreationPolicy, LifecycleCreationPolicy>();
        services.AddScoped<IStatusLabels, StatusLabels>();
        services.AddScoped<LegalReviewService>();
        services.AddScoped<ILegalReviewService>(sp => sp.GetRequiredService<LegalReviewService>());
        services.AddScoped<VisibleContracts>();
        services.AddScoped<SignatureRequestService>();
        services.AddScoped<ISignatureRequestService>(sp => sp.GetRequiredService<SignatureRequestService>());
        services.AddScoped<ISignatureEventInbox>(sp => sp.GetRequiredService<SignatureRequestService>());
        services.AddScoped<WorkflowStatusGroups>();
        services.AddScoped<Ecp.Contracts.Register.IStatusGroupProvider, LiveStatusGroup>();
        services.AddScoped<Ecp.Metadata.IWorkflowStateSource, WorkflowStates>();
        services.AddScoped<Ecp.Contracts.Register.IContractSetProvider, AwaitingLegalSet>();
        services.AddScoped<Ecp.Contracts.Register.IContractSetProvider, LegalReviewStatusSet>();
        services.AddScoped<Ecp.Contracts.Register.IContractSetProvider, PendingApprovalSet>();
        services.AddScoped<Ecp.Contracts.Register.IContractSetProvider, VariationsInProgressSet>();
        services.AddScoped<Ecp.Contracts.Register.IContractSetProvider, RenewalsInProgressSet>();
        services.AddScoped<Ecp.Contracts.Register.IContractSetProvider, AwaitingMyActionSet>();
        services.AddScoped<ApprovalFactService>();
        services.AddScoped<ApprovalFactsBuilder>();
        services.AddScoped<IApprovalFactSource, LifecycleFactSource>();
        services.AddScoped<IApprovalSimulationInputs, ApprovalSimulationInputs>();
        services.AddScoped<LifecycleMatrixReferences>();
        services.AddScoped<IApprovalReferenceData>(sp => sp.GetRequiredService<LifecycleMatrixReferences>());
        services.AddScoped<IApprovalMatrixReferences>(sp => sp.GetRequiredService<LifecycleMatrixReferences>());
        services.AddScoped<IRegisterAuthorisationService, RegisterAuthorisationService>();
        services.AddScoped<IPendingApprovalSource, PendingApprovalSource>();
        services.AddScoped<IRecordUnlockService, RecordUnlockService>();
        services.AddScoped<VariationService>();
        services.AddScoped<IVariationService>(sp => sp.GetRequiredService<VariationService>());
        services.AddScoped<IVariationOutcomes>(sp => sp.GetRequiredService<VariationService>());
        services.AddScoped<IRenewalService, RenewalService>();
        // Signature providers. A future live adapter registers its own provider, status mapper, callback verifier and executed-document retriever here (or in its own module); the lifecycle does not change.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISignatureProvider, ManualSignatureProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISignatureProvider, MockSignatureProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISignatureProvider, UnconnectedSignatureProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IExecutedDocumentRetriever, MockSignatureProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISignatureStatusMapper, MockStatusMapper>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISignatureCallbackVerifier, MockCallbackVerifier>());
        services.TryAddSingleton<ISignatureProviderFactory, SignatureProviderFactory>();
        return services;
    }
}

/// <summary>State codes mapped to the display names in the workflow definitions in force (terminology can change; the codes never do).</summary>
public interface IStatusLabels
{
    /// <summary>Display names for the states of every organisation's contract workflow, merged (the first organisation's wins where they differ).</summary>
    Task<IReadOnlyDictionary<string, string>> AllAsync(CancellationToken ct = default);
}

internal sealed class StatusLabels(IWorkflowDefinitions definitions, Ecp.Tenancy.IOrganisationStructureService structure) : IStatusLabels
{
    public async Task<IReadOnlyDictionary<string, string>> AllAsync(CancellationToken ct = default)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var org in await structure.ListOrganisationsAsync(ct))
            foreach (var s in (await definitions.CurrentAsync(WorkflowKinds.ContractLifecycle, org.Id, ct)).Definition.States)
                labels.TryAdd(s.Code, s.Display);
        return labels;
    }
}
