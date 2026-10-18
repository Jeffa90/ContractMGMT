using Ecp.Identity;
using Ecp.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Admin.ApproverGroups;

/// <summary>
/// The approver groups: who decides approvals, by group. A group is named by a permanent key that approval matrices and workflows use; its members are the approver and backup-approver roles carrying that key. The page lists registered
/// groups and the keys people already hold without a registration (so none is hidden), and registers a group through a form. Everything is decided by the service.
/// </summary>
[Authorize(Policy = "perm:users.manage")]
public sealed class IndexModel(IApproverGroupService groups, IOrganisationStructureService organisations, IAccessService access) : EcpPageModel
{
    [BindProperty] public string? Key { get; set; }
    [BindProperty] public string? Description { get; set; }
    [BindProperty] public Guid? OrganisationId { get; set; }
    [BindProperty] public bool AllowDelegation { get; set; } = true;
    public IReadOnlyList<ApproverGroupSummary> Groups { get; private set; } = [];
    public IReadOnlyList<OrganisationSummary> Organisations { get; private set; } = [];
    public bool CoversEverywhere { get; private set; }
    public string OrganisationName(Guid? id) => id is null ? "All organisations" : Organisations.FirstOrDefault(o => o.Id == id)?.Name ?? "Another organisation";

    public Task<IActionResult> OnGetAsync(CancellationToken ct) => RunAsync(async () => { await LoadAsync(ct); return Page(); });

    private async Task LoadAsync(CancellationToken ct)
    {
        Groups = await groups.ListAsync(ct);
        var grants = await access.GrantsAsync("users.manage", ct);
        CoversEverywhere = grants.Any(g => g.OrganisationIds is null);
        Organisations = (await organisations.ListOrganisationsAsync(ct)).Where(o => grants.Any(g => g.OrganisationIds is null || g.OrganisationIds.Contains(o.Id))).ToList();
    }

    public Task<IActionResult> OnPostCreateAsync(CancellationToken ct) => RunAsync(async () =>
    {
        var created = await groups.CreateAsync(Key ?? "", Description, OrganisationId, AllowDelegation, ct);
        Flash($"Approver group “{created.Key}” is registered. Add its members next.");
        return RedirectToPage("Details", new { key = created.Key });
    }, () => LoadAsync(ct));
}
