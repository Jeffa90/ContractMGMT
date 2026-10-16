using Ecp.Contracts;
using Ecp.Contracts.Register;
using Ecp.Lifecycle.Definitions;
using Ecp.Lifecycle.Domain;
using Ecp.SharedKernel;
using Ecp.Tenancy;

namespace Ecp.Lifecycle;

// The register's lists that depend on lifecycle facts (legal review, approvals, Variations, renewals). Each returns contract ids only; the register still applies the caller's own access to them,
// so a set can narrow a result but never reveal a contract the person could not already see.

internal static class SetPaging
{
    public const int PageSize = 200, MaxPages = 25;

    public static async Task<IReadOnlySet<Guid>> CollectAsync<T>(Func<int, Task<PagedResult<T>>> page, Func<T, Guid> id)
    {
        var ids = new HashSet<Guid>();
        for (var p = 1; p <= MaxPages; p++)
        {
            var r = await page(p);
            foreach (var item in r.Items) ids.Add(id(item));
            if (p * PageSize >= r.Total || r.Items.Count == 0) break;
        }
        return ids;
    }
}

internal sealed class AwaitingLegalSet(ILegalReviewService legal) : IContractSetProvider
{
    public string Key => "awaitingLegal";
    public Task<IReadOnlySet<Guid>> IdsAsync(string? argument, CancellationToken ct = default)
        => SetPaging.CollectAsync(p => legal.QueueAsync(new LegalQueueFilter(Status: LegalReviewStatus.AwaitingLegal, Page: p, PageSize: SetPaging.PageSize), ct), i => i.ContractId);
}

internal sealed class LegalReviewStatusSet(ILegalReviewService legal) : IContractSetProvider
{
    public string Key => "legalReviewStatus";
    public async Task<IReadOnlySet<Guid>> IdsAsync(string? argument, CancellationToken ct = default)
    {
        if (!Enum.TryParse<LegalReviewStatus>(argument, true, out var status)) return new HashSet<Guid>();
        return await SetPaging.CollectAsync(p => legal.QueueAsync(new LegalQueueFilter(Status: status, OpenOnly: false, Page: p, PageSize: SetPaging.PageSize), ct), i => i.ContractId);
    }
}

internal sealed class PendingApprovalSet(IRegisterAuthorisationService approvals) : IContractSetProvider
{
    public string Key => "pendingApproval";
    public Task<IReadOnlySet<Guid>> IdsAsync(string? argument, CancellationToken ct = default)
        => SetPaging.CollectAsync(p => approvals.PendingAsync(onlyActionable: false, p, SetPaging.PageSize, ct), i => i.ContractId);
}

internal sealed class VariationsInProgressSet(IVariationService variations) : IContractSetProvider
{
    public string Key => "variationsInProgress";
    public Task<IReadOnlySet<Guid>> IdsAsync(string? argument, CancellationToken ct = default)
        => SetPaging.CollectAsync(p => variations.InProgressAsync(p, SetPaging.PageSize, ct), i => i.ContractId);
}

internal sealed class RenewalsInProgressSet(IRenewalService renewals) : IContractSetProvider
{
    public string Key => "renewalsInProgress";
    public Task<IReadOnlySet<Guid>> IdsAsync(string? argument, CancellationToken ct = default)
        => SetPaging.CollectAsync(p => renewals.InProgressAsync(p, SetPaging.PageSize, ct), i => i.PredecessorContractId);
}

/// <summary>Contracts waiting on the signed-in person: a Legal review to do, an approval to decide, or an owner step (rework, drafting, signature).</summary>
internal sealed class AwaitingMyActionSet(ILegalReviewService legal, IRegisterAuthorisationService approvals, ISecuredContractService contracts, WorkflowStatusGroups groups) : IContractSetProvider
{
    public string Key => "awaitingMyAction";
    public async Task<IReadOnlySet<Guid>> IdsAsync(string? argument, CancellationToken ct = default)
    {
        var ids = new HashSet<Guid>(await SetPaging.CollectAsync(p => legal.QueueAsync(new LegalQueueFilter(OutstandingForMe: true, Page: p, PageSize: SetPaging.PageSize), ct), i => i.ContractId));
        foreach (var id in await SetPaging.CollectAsync(p => approvals.PendingAsync(onlyActionable: true, p, SetPaging.PageSize, ct), i => i.ContractId)) ids.Add(id);
        var ownerStatuses = await groups.StatusesWithRolesAsync(WorkflowStatusGroups.OwnerActionRoles, null, ct);
        if (ownerStatuses.Count > 0)
            foreach (var id in await SetPaging.CollectAsync(p => contracts.ListAsync(new ContractFilter(OwnedByMe: true, StatusIn: ownerStatuses, Page: p, PageSize: SetPaging.PageSize), ct), i => i.Id)) ids.Add(id);
        return ids;
    }
}

/// <summary>Groups of workflow states by their role in the definition, so lists never name a state: the states with the "effective" kind of role are "live".</summary>
internal sealed class WorkflowStatusGroups(IWorkflowDefinitions workflows, IOrganisationStructureService structure)
{
    public static readonly string[] LiveRoles = ["effective", "variation", "renewal", "suspended"];
    public static readonly string[] OwnerActionRoles = ["legalRework", "legalCleared", "drafting", "signature"];

    public async Task<IReadOnlyList<string>> StatusesWithRolesAsync(string[] roles, Guid? organisationId, CancellationToken ct)
    {
        var codes = new HashSet<string>(StringComparer.Ordinal);
        var orgs = organisationId is { } one ? [one] : (await structure.ListOrganisationsAsync(ct)).Select(o => o.Id).ToList();
        foreach (var org in orgs)
            foreach (var s in (await workflows.CurrentAsync(WorkflowKinds.ContractLifecycle, org, ct)).Definition.States.Where(s => s.Role is not null && roles.Contains(s.Role))) codes.Add(s.Code);
        return codes.ToList();
    }
}

internal sealed class LiveStatusGroup(WorkflowStatusGroups groups) : IStatusGroupProvider
{
    public string Key => "live";
    public Task<IReadOnlyList<string>> StatusesAsync(Guid? organisationId, CancellationToken ct = default) => groups.StatusesWithRolesAsync(WorkflowStatusGroups.LiveRoles, organisationId, ct);
}

/// <summary>The workflow's own state codes, so a custom field's role/state rules can be checked against real states when definitions are published.</summary>
internal sealed class WorkflowStates(IWorkflowDefinitions workflows) : Ecp.Metadata.IWorkflowStateSource
{
    public async Task<IReadOnlySet<string>?> StatesAsync(Guid organisationId, CancellationToken ct = default)
    {
        try { return (await workflows.CurrentAsync(WorkflowKinds.ContractLifecycle, organisationId, ct)).Definition.States.Select(s => s.Code).ToHashSet(StringComparer.Ordinal); }
        catch (Exception ex) when (ex is Ecp.SharedKernel.DomainException or Ecp.SharedKernel.ResourceNotFoundException) { return null; }
    }
}
