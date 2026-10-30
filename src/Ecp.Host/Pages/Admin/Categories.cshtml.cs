using Ecp.Access;
using Ecp.Contracts;
using Ecp.Identity;
using Ecp.SharedKernel;
using Ecp.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Admin;

/// <summary>
/// Categories and owners (docs/UX_REDESIGN.md section 9): one row per category with a primary and a backup owner chosen from everyone the administrator may manage, assign-all, bulk selection, search and
/// a "needs an owner" filter. No CSV is needed (it stays under Bulk import). Choosing someone who lacks the owner role gives it to them in the same save. The services decide every permission.
/// </summary>
[Authorize(Policy = "perm:contracts.read|categories.manage|categories.assign")]
public sealed class CategoriesModel(ISecuredCategoryService categories, ICategoryOwnership ownership, ICategoryOwnershipPolicy policies, IOrganisationStructureService organisations, IAccessService access) : EcpPageModel
{
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    [BindProperty(SupportsGet = true)] public bool Needs { get; set; }
    [BindProperty] public Guid OrganisationId { get; set; }
    [BindProperty] public Guid UserId { get; set; }
    [BindProperty] public string Role { get; set; } = "primary";
    [BindProperty] public List<Guid> CategoryIds { get; set; } = [];
    [BindProperty] public string? Reason { get; set; }

    public OwnershipBoard Board { get; private set; } = new([], 0, 0);
    public IReadOnlyList<OrganisationSummary> ManageOrganisations { get; private set; } = [];
    public bool CanManage { get; private set; }
    public bool CanAssign { get; private set; }
    public OwnershipPlan? Plan { get; private set; }
    /// <summary>The plan is for "every current and future category" rather than for the ticked ones.</summary>
    public bool PlanAll { get; private set; }
    public IReadOnlyList<OwnershipPolicyInfo> Standing { get; private set; } = [];
    public string? PlanAction { get; private set; }
    private Dictionary<Guid, string> _orgs = [];
    private readonly Dictionary<Guid, IReadOnlyList<OwnerChoice>> _people = [];

    public async Task<IActionResult> OnGetAsync(CancellationToken ct) { await LoadAsync(ct); return Page(); }

    public Task<IActionResult> OnPostCreateAsync(Guid organisationId, string code, string name, Guid? parentCategoryId, CancellationToken ct) => RunAsync(async () =>
    {
        // The parent's code is resolved server-side; the trusted upsert is not used so authorisation applies.
        await categories.CreateAsync(new CreateCategoryRequest(organisationId, code, name, parentCategoryId), ct);
        Flash("[[category]] added.");
        return RedirectToPage();
    }, reload: () => LoadAsync(CancellationToken.None));

    /// <summary>Shows what a change would do before it is made: how many categories, who is replaced, whether the person gains the owner role.</summary>
    public Task<IActionResult> OnPostPlanAsync(CancellationToken ct) => RunAsync(async () =>
    {
        await LoadAsync(ct);
        Plan = await ownership.PlanAsync(OrganisationId, CategoryIds, UserId, Role == "primary", ct);
        PlanAction = "SetOwner";
        return Page();
    }, reload: () => LoadAsync(ct));

    public Task<IActionResult> OnPostPlanAllAsync(CancellationToken ct) => RunAsync(async () =>
    {
        await LoadAsync(ct);
        var all = Board.Rows.Where(r => r.IsActive && r.OrganisationId == OrganisationId && (r.ParentId is null || Board.Rows.All(p => p.CategoryId != r.ParentId))).Select(r => r.CategoryId).ToList();
        CategoryIds = all;
        // "Own all" is a standing instruction: today's categories are covered now and every one added later is covered as it is created. With none yet there is still something to confirm.
        Plan = all.Count == 0 ? new OwnershipPlan(0, 0, 0, false, (await ownership.PeopleAsync(OrganisationId, ct)).FirstOrDefault(p => p.UserId == UserId)?.DisplayName ?? "That person") : await ownership.PlanAsync(OrganisationId, all, UserId, Role == "primary", ct);
        PlanAll = true;
        return Page();
    }, reload: () => LoadAsync(ct));

    public Task<IActionResult> OnPostOwnAllAsync(CancellationToken ct) => StepUpRunAsync(async () =>
    {
        var outcome = await ownership.AssignAllAsync(OrganisationId, UserId, Role == "primary", Reason, ct);
        Flash(string.Join(" ", outcome.Notes));
        return RedirectToPage(new { Q, Needs });
    }, reload: () => LoadAsync(CancellationToken.None));

    public Task<IActionResult> OnPostEndStandingAsync(Guid policyId, string? reason, CancellationToken ct) => StepUpRunAsync(async () =>
    {
        await policies.EndAsync(policyId, string.IsNullOrWhiteSpace(reason) ? "Standing ownership ended from the ownership screen" : reason, ct);
        Flash("Standing ownership ended. The ownership it created is ended; ownership given any other way is untouched, and a [[category|lower]] added from now on is not recognised.");
        return RedirectToPage(new { Q, Needs });
    }, reload: () => LoadAsync(CancellationToken.None));

    public Task<IActionResult> OnPostSetOwnerAsync(CancellationToken ct) => StepUpRunAsync(async () =>
    {
        var outcome = await ownership.SetOwnerAsync(OrganisationId, CategoryIds, UserId, Role == "primary", Reason, ct);
        Flash($"{outcome.Assigned} owner assignment(s) made" + (outcome.Replaced > 0 ? $", {outcome.Replaced} previous primary owner(s) replaced" : "") + (outcome.Skipped > 0 ? $", {outcome.Skipped} already as chosen" : "") + ". " + string.Join(" ", outcome.Notes));
        return RedirectToPage(new { Q, Needs });
    }, reload: () => LoadAsync(CancellationToken.None));

    public Task<IActionResult> OnPostEndAsync(Guid assignmentId, string? reason, CancellationToken ct) => RunAsync(async () =>
    {
        await ownership.EndAsync(assignmentId, string.IsNullOrWhiteSpace(reason) ? "Ownership ended" : reason, ct);
        Flash("Ownership ended. That person can no longer edit in it.");
        return RedirectToPage(new { Q, Needs });
    });

    public Task<IActionResult> OnPostDeactivateAsync(Guid categoryId, CancellationToken ct) => RunAsync(async () =>
    {
        await categories.DeactivateAsync(categoryId, ct);
        Flash("[[category]] deactivated.");
        return RedirectToPage();
    });

    private async Task LoadAsync(CancellationToken ct)
    {
        Board = await ownership.BoardAsync(Q, Needs, ct);
        _orgs = (await organisations.ListOrganisationsAsync(ct)).ToDictionary(o => o.Id, o => o.Name);
        CanManage = await access.HoldsAnywhereAsync("categories.manage", ct);
        CanAssign = await access.HoldsAnywhereAsync("categories.assign", ct);
        var manageGrants = await access.GrantsAsync("categories.manage", ct);
        ManageOrganisations = _orgs.Where(o => manageGrants.Any(g => g.OrganisationIds is null || g.OrganisationIds.Contains(o.Key))).Select(o => new OrganisationSummary(o.Key, o.Value, "", "", "", "", null)).ToList();
        try { Standing = await policies.ListAsync(null, false, ct); }
        catch (ForbiddenException) { Standing = []; }   // seeing standing instructions needs permission to manage people
        if (CanAssign)
            foreach (var orgId in Board.Rows.Select(r => r.OrganisationId).Distinct())
            {
                try { _people[orgId] = await ownership.PeopleAsync(orgId, ct); }
                catch (ForbiddenException) { _people[orgId] = []; }   // an organisation outside what the caller manages
            }
    }

    public string OrganisationName(Guid id) => _orgs.TryGetValue(id, out var n) ? n : "";
    public IReadOnlyList<OwnerChoice> People(Guid organisationId) => _people.TryGetValue(organisationId, out var list) ? list : [];
    public IEnumerable<Guid> OrganisationsShown => Board.Rows.Select(r => r.OrganisationId).Distinct();
}
