using Ecp.Approvals;
using Ecp.Identity;
using Ecp.SharedKernel;
using Ecp.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Admin.Approvals;

/// <summary>An approver hands their own authority to another member of the group for a bounded period; an administrator can see (and end) the delegations of an organisation. A delegate acts on behalf of the person who delegated and never passes it on.</summary>
[Authorize(Policy = "perm:approvals.delegate|approvals.matrix.read")]
public sealed class DelegationsModel(IApprovalDelegations delegations, IAccessDirectory directory, IAccessService access, CurrentUser currentUser, IOrganisationStructureService organisations, IClock clock) : EcpPageModel
{
    [BindProperty(SupportsGet = true)] public Guid? OrganisationId { get; set; }
    [BindProperty(SupportsGet = true)] public string? GroupKey { get; set; }
    [BindProperty] public Guid? ToUserId { get; set; }
    [BindProperty] public DateOnly? FromDate { get; set; }
    [BindProperty] public DateOnly? ToDate { get; set; }
    [BindProperty] public string? Reason { get; set; }
    [BindProperty] public string? EndReason { get; set; }
    public IReadOnlyList<OrganisationSummary> Organisations { get; private set; } = [];
    public IReadOnlyList<DelegationInfo> Mine { get; private set; } = [];
    public PagedResult<DelegationInfo>? All { get; private set; }
    public IReadOnlyList<ApproverInfo> Members { get; private set; } = [];
    public bool CanDelegate { get; private set; }
    public bool CanAdminister { get; private set; }
    public Guid? Me => currentUser.UserId;

    public Task<IActionResult> OnGetAsync(CancellationToken ct) => RunAsync(async () => { await LoadAsync(ct); return Page(); });

    private async Task LoadAsync(CancellationToken ct)
    {
        Organisations = await OrganisationChoices.AllowedAsync(organisations, access, ct, "approvals.delegate", "approvals.matrix.read");
        OrganisationId ??= Organisations.FirstOrDefault()?.Id;
        if (OrganisationId is { } shown) await Terms.UseOrganisationAsync(shown, ct);
        CanDelegate = await access.HoldsAnywhereAsync("approvals.delegate", ct);
        CanAdminister = await access.HoldsAnywhereAsync("approvals.delegate.administer", ct);
        if (CanDelegate) Mine = await delegations.MineAsync(ct);
        // Only a member of the group is shown who else is in it.
        if (CanDelegate && !string.IsNullOrWhiteSpace(GroupKey))
        {
            var members = await directory.ApproversAsync(GroupKey.Trim(), ct);
            if (Me is { } me && members.Any(m => m.UserId == me)) Members = members.Where(m => m.UserId != me).GroupBy(m => m.UserId).Select(g => g.First()).OrderBy(m => m.DisplayName, StringComparer.Ordinal).ToList();
        }
        if (OrganisationId is { } org && await access.HoldsAnywhereAsync("approvals.matrix.read", ct))
        {
            try { All = await delegations.ListAsync(org, activeOnly: false, 1, 50, ct); } catch (ForbiddenException) { All = null; }
        }
    }

    public Task<IActionResult> OnPostCreateAsync(CancellationToken ct) => RunAsync(async () =>
    {
        if (OrganisationId is not { } org || string.IsNullOrWhiteSpace(GroupKey) || ToUserId is not { } to || FromDate is not { } from || ToDate is not { } until)
            throw new DomainException("delegation_incomplete", "Choose the [[organisation|lower]], the group, the person and both dates.");
        var start = DateTime.SpecifyKind(from.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
        var begins = start < clock.UtcNow ? clock.UtcNow : start;
        var ends = DateTime.SpecifyKind(until.AddDays(1).ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);       // the last day is included
        await delegations.CreateAsync(new CreateDelegationRequest(org, GroupKey.Trim(), to, begins, ends, Reason ?? ""), ct);
        Flash("Your authority is delegated for that period. It is recorded, audited and shown as 'on behalf of' whenever the person uses it.");
        return RedirectToPage(new { organisationId = org });
    }, () => LoadAsync(ct));

    public Task<IActionResult> OnPostEndAsync(Guid id, CancellationToken ct) => RunAsync(async () =>
    {
        await delegations.RevokeAsync(id, EndReason ?? "", ct);
        Flash("The delegation is ended.");
        return RedirectToPage(new { organisationId = OrganisationId });
    }, () => LoadAsync(ct));
}
