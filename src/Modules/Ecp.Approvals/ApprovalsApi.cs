using Ecp.Approvals.Definitions;
using Ecp.Approvals.Domain;
using Ecp.Approvals.Persistence;
using Ecp.Approvals.Resolution;
using Ecp.Identity.Access;
using Ecp.Persistence;
using Ecp.SharedKernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ecp.Approvals;

public static class ApprovalsModule
{
    public const string Name = "Approvals";
    public const string Schema = "approvals";
    public const int Tier = 6;
    /// <summary>The configuration kind that carries approval matrix documents.</summary>
    public const string MatrixKind = "ApprovalMatrix";

    public static IServiceCollection AddApprovalsModule(this IServiceCollection services)
    {
        services.AddModuleDbContext<ApprovalsDbContext>(Name, Tier, Schema);
        services.AddScoped<Ecp.Configuration.IConfigurationKind, ApprovalMatrixKind>();
        services.AddScoped<IApprovalMatrixStore, ApprovalMatrixStore>();
        services.AddSingleton<Ecp.Configuration.Forms.IFormSchemaSource, Definitions.MatrixFormSchemas>();
        services.AddScoped<IApprovalDelegations, ApprovalDelegations>();
        services.AddScoped<ApprovalParticipants>();
        services.AddScoped<IApprovalResolver, ApprovalResolver>();
        services.AddScoped<IApprovalSimulator, ApprovalSimulator>();
        return services;
    }
}

// ---- what the matrix is read from -------------------------------------------------------------------------------------------------------------------------

public enum MatrixSourceKind { Published, Release, Draft }

/// <summary>Which version of the matrix document to use: the one in force, a numbered release (history), or the open draft (to try a change before publishing it).</summary>
public sealed record MatrixSource(MatrixSourceKind Kind, int? ReleaseNumber = null)
{
    public static MatrixSource Published { get; } = new(MatrixSourceKind.Published);
    public static MatrixSource Release(int number) => new(MatrixSourceKind.Release, number);
    public static MatrixSource Draft { get; } = new(MatrixSourceKind.Draft);
}

public sealed record LoadedMatrices(MatrixSet Set, int? ReleaseNumber, Guid? ReleaseId, string ContentHash, string Label, string Json);

public interface IApprovalMatrixStore
{
    /// <summary>The matrix document for the organisation. A published release is read as a trusted lookup; a numbered release needs the right to read version history and the draft needs the right to edit drafts.</summary>
    Task<LoadedMatrices> LoadAsync(Guid organisationId, MatrixSource source, CancellationToken ct = default);
    /// <summary>The matrix document in force, or null when nothing has been published.</summary>
    Task<LoadedMatrices?> TryLoadPublishedAsync(Guid organisationId, CancellationToken ct = default);
}

/// <summary>Optional reference data that sharpens validation (known category, business unit, site and contract type codes). Implemented by a higher module that knows them; absent means validation is more conservative.</summary>
public interface IApprovalReferenceData
{
    Task<MatrixReferenceData> LoadAsync(Guid organisationId, CancellationToken ct = default);
}

/// <summary>Optional: tells the matrix which matrix codes workflows still refer to, so a published change cannot remove a matrix that approvals depend on.</summary>
public interface IApprovalMatrixReferences
{
    Task<IReadOnlyList<string>> ReferencedMatrixCodesAsync(Guid organisationId, CancellationToken ct = default);
}

/// <summary>
/// What a matrix may be judged on: the dimensions for which the platform really has a fact on every submission, and the custom field keys an administrator has made available. A matrix that conditions on
/// anything else is refused at publication, because that row could never match and an approval would quietly route round it.
/// </summary>
public sealed record ApprovalFactCatalogue(IReadOnlySet<string> Dimensions, IReadOnlySet<string> CustomKeys)
{
    public const string Value = "value", Categories = "categories", ContractTypes = "contractTypes", LegalEntities = "legalEntities", BusinessUnits = "businessUnits", Sites = "sites",
        Classifications = "classifications", RiskLevels = "riskLevels", SupplierTypes = "supplierTypes";
}

/// <summary>Implemented by the module that builds the facts of a submission, so what the matrix may condition on is exactly what that module supplies.</summary>
public interface IApprovalFactSource
{
    Task<ApprovalFactCatalogue> CatalogueAsync(Guid organisationId, CancellationToken ct = default);
}

/// <summary>Lets other modules supply facts the platform does not hold itself (contract type, risk level, supplier type, custom fields). A fact that no contributor supplies stays unknown and never satisfies a condition.</summary>
public interface IApprovalFactContributor
{
    Task<ApprovalFacts> EnrichAsync(ApprovalFacts facts, Guid organisationId, string subjectKind, Guid subjectId, CancellationToken ct = default);
}

// ---- resolving --------------------------------------------------------------------------------------------------------------------------------------------

/// <summary>
/// Everything the resolver needs. <see cref="Resource"/> describes the thing being approved the way the access layer sees it (organisation, scope dimensions, classification); the resolver asks the access
/// evaluator whether each possible approver could really act on it, so a group member who cannot open a record at this classification never becomes its approver.
/// </summary>
public sealed record ResolutionRequest(Guid OrganisationId, string MatrixCode, ApprovalFacts Facts, ResourceDescriptor Resource, Guid? SubmitterUserId, DateTime AsOfUtc, MatrixSource Source);

public sealed record ApproverExplanation(string StepKey, string StepDisplay, Guid UserId, string Name, bool Included, string Role, Guid? OnBehalfOfUserId, string? OnBehalfOfName, string Summary, IReadOnlyList<string> Reasons);

public sealed record ApprovalResolution(
    bool Resolved, string? BlockCode, string? BlockMessage, ApprovalPlan? Plan, IReadOnlyList<TraceEntry> Trace, IReadOnlyList<ApproverExplanation> Explanations, string MatrixCode, int? ReleaseNumber, string SourceLabel,
    DateOnly EffectiveDate, DateTime AsOfUtc, IReadOnlyList<string> RowCodes);

/// <summary>Who may respond to one step of a stored plan today: judged against the group and the delegations as they are now, not as they were at submission.</summary>
public sealed record StepParticipation(IReadOnlyList<PlanApprover> Participants, IReadOnlyList<ApproverExplanation> Explanations);

public interface IApprovalResolver
{
    /// <summary>Chooses the matching rows for the facts on the date, builds the steps and names the people who can act on each. Never throws for "no one can approve": that is a blocked resolution with a trace and a message.</summary>
    Task<ApprovalResolution> ResolveAsync(ResolutionRequest request, CancellationToken ct = default);
    /// <summary>The people who can respond to a step of an existing plan right now (the current group members and delegations, filtered by the same access rules as at resolution).</summary>
    Task<StepParticipation> ParticipantsAsync(ApprovalPlan plan, string stepKey, ResourceDescriptor resource, Guid? submitterUserId, DateTime asOfUtc, CancellationToken ct = default);
}

// ---- simulating -------------------------------------------------------------------------------------------------------------------------------------------

public sealed record SimulationRequest(Guid OrganisationId, string MatrixCode, ApprovalFacts Facts, ResourceDescriptor Resource, Guid? SubmitterUserId, DateOnly? AsOfDate, MatrixSource Source);

public sealed record SimulationResult(ApprovalResolution Resolution, bool IsFuture, bool IsPast, IReadOnlyList<string> Notes);

public sealed record MatrixSummary(string Code, string Name, string Currency, int Rows, int Exceptions, DateOnly? EffectiveFrom, DateOnly? EffectiveTo, bool InForceToday);

public interface IApprovalSimulator
{
    /// <summary>Runs a full resolution for sample facts without touching any record. Needs <c>approvals.simulate</c>; the run is audited because it shows who the approvers are.</summary>
    Task<SimulationResult> SimulateAsync(SimulationRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<MatrixSummary>> MatricesAsync(Guid organisationId, MatrixSource source, CancellationToken ct = default);
}

// ---- delegating -------------------------------------------------------------------------------------------------------------------------------------------

public sealed record CreateDelegationRequest(Guid OrganisationId, string GroupKey, Guid ToUserId, DateTime ValidFromUtc, DateTime ValidToUtc, string Reason);

public sealed record DelegationInfo(
    Guid Id, Guid OrganisationId, string GroupKey, Guid FromUserId, string FromName, Guid ToUserId, string ToName, DateTime ValidFromUtc, DateTime ValidToUtc, string Reason, DateTime CreatedUtc,
    DateTime? RevokedUtc, string? RevokedBy, string? RevokeReason, bool IsActive);

public interface IApprovalDelegations
{
    /// <summary>The signed-in approver delegates their own authority. Refused for a circular arrangement, for someone outside the group, for yourself and for periods over 90 days.</summary>
    Task<Guid> CreateAsync(CreateDelegationRequest request, CancellationToken ct = default);
    /// <summary>Ends a delegation. The person who delegated may; so may an administrator with <c>approvals.delegate.administer</c>.</summary>
    Task RevokeAsync(Guid id, string reason, CancellationToken ct = default);
    Task<IReadOnlyList<DelegationInfo>> MineAsync(CancellationToken ct = default);
    /// <summary>For administrators: the delegations of an organisation. Needs <c>approvals.matrix.read</c>.</summary>
    Task<PagedResult<DelegationInfo>> ListAsync(Guid organisationId, bool activeOnly, int page = 1, int pageSize = 25, CancellationToken ct = default);
    /// <summary>Trusted: the delegations in force for the organisation and groups at an instant, as used by the resolver.</summary>
    Task<IReadOnlyList<DelegationInfo>> ActiveAsync(Guid organisationId, IReadOnlyCollection<string> groupKeys, DateTime asOfUtc, CancellationToken ct = default);
}

/// <summary>Registered by the composition root to give the matrix its own organisation-scoped descriptor for authorisation.</summary>
internal static class ApprovalResources
{
    public static ResourceDescriptor Matrix(Guid tenantId, Guid organisationId, string? id = null)
        => new("ApprovalMatrix", tenantId, organisationId, Id: id, Dimensions: ScopeDimensions.OrganisationOnly);
}

// ---- building simulation input from choices ---------------------------------------------------------------------------------------------------------------

public sealed record ChoiceItem(Guid Id, string Code, string Name, Guid? ParentId);

/// <summary>What an administrator can choose from when describing a sample submission. Codes and names only; nothing about people or records.</summary>
public sealed record SimulationChoices(
    IReadOnlyList<ChoiceItem> Categories, IReadOnlyList<ChoiceItem> BusinessUnits, IReadOnlyList<ChoiceItem> Sites, IReadOnlyList<ChoiceItem> LegalEntities, IReadOnlyList<string> Classifications);

/// <summary>A sample submission as the administrator describes it: choices by id, plus free values for the facts the platform does not itself hold.</summary>
public sealed record SimulationInputs(
    Guid OrganisationId, decimal? Value, string? Currency, Guid? CategoryId, Guid? BusinessUnitId, Guid? SiteId, Guid? LegalEntityId, string? Classification, string? ContractType, string? RiskLevel,
    string? SupplierType, IReadOnlyDictionary<string, string?> Custom);

public sealed record SimulationSubject(ApprovalFacts Facts, ResourceDescriptor Resource);

/// <summary>Implemented by the module that knows categories, structure and contracts. Both calls need <c>approvals.simulate</c> in the organisation.</summary>
public interface IApprovalSimulationInputs
{
    /// <summary>The inputs of an existing contract, exactly as a live submission would see it (same fact service), so an administrator can ask "who would approve this one?". Needs <c>approvals.simulate</c> and the right to read the contract.</summary>
    Task<SimulationInputs> FromContractAsync(Guid contractId, CancellationToken ct = default);
    Task<SimulationChoices> ChoicesAsync(Guid organisationId, CancellationToken ct = default);
    /// <summary>Turns the choices into the facts the matrix is judged on and the descriptor the access layer judges approvers against, from stored reference data (never from request-supplied codes).</summary>
    Task<SimulationSubject> BuildAsync(SimulationInputs inputs, CancellationToken ct = default);
}
