using Ecp.Contracts;
using Ecp.Identity;
using Ecp.SharedKernel;
using Ecp.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Admin;

public sealed record CategoryRow(CategoryItem Category, string Indent);

[Authorize(Policy = "perm:contracts.read")]
public sealed class CategoriesModel(ISecuredCategoryService categories, IOrganisationStructureService organisations, IPeopleDirectory people,
    IAccessService access) : EcpPageModel
{
    public IReadOnlyList<CategoryRow> Rows { get; private set; } = [];
    public IReadOnlyList<OrganisationSummary> ManageOrganisations { get; private set; } = [];
    public bool CanManage { get; private set; }
    public bool CanAssign { get; private set; }
    private Dictionary<Guid, string> _orgs = [];
    private IReadOnlyDictionary<Guid, string> _users = new Dictionary<Guid, string>();
    private readonly Dictionary<Guid, IReadOnlyList<PersonItem>> _candidates = [];

    public async Task<IActionResult> OnGetAsync(CancellationToken ct) { await LoadAsync(ct); return Page(); }

    public Task<IActionResult> OnPostCreateAsync(Guid organisationId, string code, string name, Guid? parentCategoryId, CancellationToken ct) => RunAsync(async () =>
    {
        // The parent's code is resolved server-side; the trusted upsert is not used so authorisation applies.
        await categories.CreateAsync(new CreateCategoryRequest(organisationId, code, name, parentCategoryId), ct);
        Flash("[[category]] added.");
        return RedirectToPage();
    }, reload: () => LoadAsync(CancellationToken.None));

    public Task<IActionResult> OnPostAssignAsync(Guid categoryId, Guid userId, CancellationToken ct) => RunAsync(async () =>
    {
        await categories.AssignOwnerAsync(categoryId, userId, false, null, ct);
        Flash("Owner assigned.");
        return RedirectToPage();
    });

    public Task<IActionResult> OnPostEndAsync(Guid assignmentId, string? reason, CancellationToken ct) => RunAsync(async () =>
    {
        await categories.EndOwnershipAsync(assignmentId, string.IsNullOrWhiteSpace(reason) ? "Ownership ended" : reason, ct);
        Flash("Ownership ended.");
        return RedirectToPage();
    });

    public Task<IActionResult> OnPostDeactivateAsync(Guid categoryId, CancellationToken ct) => RunAsync(async () =>
    {
        await categories.DeactivateAsync(categoryId, ct);
        Flash("[[category]] deactivated.");
        return RedirectToPage();
    });

    private async Task LoadAsync(CancellationToken ct)
    {
        var all = await categories.ListAsync(ct);
        _orgs = (await organisations.ListOrganisationsAsync(ct)).ToDictionary(o => o.Id, o => o.Name);
        CanManage = await access.HoldsAnywhereAsync("categories.manage", ct);
        CanAssign = await access.HoldsAnywhereAsync("categories.assign", ct);
        var manageGrants = await access.GrantsAsync("categories.manage", ct);
        ManageOrganisations = _orgs.Where(o => manageGrants.Any(g => g.OrganisationIds is null || g.OrganisationIds.Contains(o.Key))).Select(o => new OrganisationSummary(o.Key, o.Value, "", "", "", "", null)).ToList();

        var rows = new List<CategoryRow>();
        void Add(CategoryItem c, int depth)
        {
            rows.Add(new CategoryRow(c, string.Concat(Enumerable.Repeat("- ", depth))));
            foreach (var child in all.Where(x => x.ParentCategoryId == c.Id)) Add(child, depth + 1);
        }
        foreach (var root in all.Where(c => c.ParentCategoryId is null || all.All(p => p.Id != c.ParentCategoryId))) Add(root, 0);
        Rows = rows;
        _users = await people.DisplayNamesAsync(rows.SelectMany(r => r.Category.Owners.Select(o => o.UserId)), ct);
        if (CanAssign)
            foreach (var orgId in rows.Select(r => r.Category.OrganisationId).Distinct())
            {
                try { _candidates[orgId] = await people.OwnerCandidatesAsync(orgId, ct); }
                catch (ForbiddenException) { _candidates[orgId] = []; } // an organisation outside the caller's assignment scope
            }
    }

    public string OrganisationName(Guid id) => _orgs.TryGetValue(id, out var n) ? n : "";
    public string UserName(Guid id) => _users.TryGetValue(id, out var n) ? n : "Unknown user";

    /// <summary>People who can own categories in an organisation: those holding an active CategoryOwner assignment that covers it.</summary>
    public IEnumerable<PersonItem> OwnerCandidates(Guid organisationId) => _candidates.TryGetValue(organisationId, out var list) ? list : [];
}
