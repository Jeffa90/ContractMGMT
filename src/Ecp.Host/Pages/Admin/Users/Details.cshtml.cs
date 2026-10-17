using Ecp.Contracts;
using Ecp.Identity;
using Ecp.Identity.Access;
using Ecp.Identity.Domain;
using Ecp.SharedKernel;
using Ecp.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Admin.Users;

[Authorize(Policy = "perm:users.manage")]
public sealed class DetailsModel(IPeopleDirectory people, IUserAdministration admin, IRoleAssignmentService roles, IAccessService access,
    IOrganisationStructureService structure, ISecuredCategoryService categories, Ecp.Configuration.ITerminologyResolver terminology) : EcpPageModel
{
    public PersonItem Person { get; private set; } = null!;
    public IReadOnlyList<AssignmentDto> Assignments { get; private set; } = [];
    public IReadOnlyList<string> GrantableRoles { get; private set; } = [];
    /// <summary>Roles the administrator could grant but that every organisation they administer has chosen not to offer. Presentation only: nothing is revoked, and the assignments below still list them.</summary>
    public IReadOnlyList<string> RolesNotOffered { get; private set; } = [];
    public IReadOnlyList<OrganisationSummary> Organisations { get; private set; } = [];
    public IReadOnlyList<DivisionDto> Divisions { get; private set; } = [];
    public IReadOnlyList<BusinessUnitDto> BusinessUnits { get; private set; } = [];
    public IReadOnlyList<SiteDto> Sites { get; private set; } = [];
    public IReadOnlyList<CategoryItem> Categories { get; private set; } = [];
    public IReadOnlyList<string> Levels { get; private set; } = [];
    public string DefaultLevel { get; private set; } = "";
    private Dictionary<Guid, string> _names = [];

    public Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct) => RunAsync(async () => { await LoadAsync(id, ct); return Page(); });

    public Task<IActionResult> OnPostGrantAsync(Guid id, string role, string? viewerKind, string level, Guid[]? organisations, Guid[]? divisions, Guid[]? businessUnits,
        Guid[]? sites, Guid[]? categories, string ceiling, string? approverGroup, string reason, CancellationToken ct) => RunAsync(async () =>
    {
        ViewerKind? kind = string.IsNullOrEmpty(viewerKind) ? null : Enum.Parse<ViewerKind>(viewerKind);
        var scope = level switch
        {
            "Tenant" => ScopeSet.Tenant(ceiling, categories is { Length: > 0 } ? categories : null),
            "Installation" => ScopeSet.Installation(ceiling),
            _ => ScopeSet.ForOrganisations(organisations ?? [], ceiling, businessUnits, divisions, sites, categories is { Length: > 0 } ? categories : null)
        };
        await roles.GrantAsync(new GrantRequest(id, role, kind, scope, string.IsNullOrWhiteSpace(approverGroup) ? null : approverGroup.Trim(), Reason: reason), ct);
        Flash("Role granted.");
        return RedirectToPage(new { id });
    }, reload: () => LoadAsync(id, CancellationToken.None));

    public Task<IActionResult> OnPostRevokeAsync(Guid id, Guid assignmentId, string reason, CancellationToken ct) => RunAsync(async () =>
    {
        await people.AssignmentsOfAsync(id, includeEnded: true, ct); // 404 unless the caller may administer this person
        await roles.RevokeAsync(assignmentId, reason, ct);
        Flash("Role assignment ended.");
        return RedirectToPage(new { id });
    }, reload: () => LoadAsync(id, CancellationToken.None));

    public Task<IActionResult> OnPostDisableAsync(Guid id, string reason, CancellationToken ct) => RunAsync(async () =>
    {
        await admin.DisableAsync(id, reason, ct);
        Flash("Account disabled.");
        return RedirectToPage(new { id });
    }, reload: () => LoadAsync(id, CancellationToken.None));

    public Task<IActionResult> OnPostReactivateAsync(Guid id, CancellationToken ct) => RunAsync(async () =>
    {
        await admin.ReactivateAsync(id, ct);
        Flash("Account reactivated.");
        return RedirectToPage(new { id });
    });

    private async Task LoadAsync(Guid id, CancellationToken ct)
    {
        Person = await people.GetManageableUserAsync(id, ct);
        Assignments = await people.AssignmentsOfAsync(id, includeEnded: true, ct);
        var subject = await access.GetSubjectAsync(ct);
        var now = DateTime.UtcNow;
        GrantableRoles = subject is null ? [] : subject.Assignments.Where(a => a.IsActiveAt(now) && AccessEvaluator.Default.Matrix.Of(a.RoleCode, "roles.delegate") != Grant.None)
            .SelectMany(a => DelegationPolicy.GrantableBy(a.RoleCode)).Distinct().Order().ToList();
        Organisations = await structure.ListOrganisationsAsync(ct);
        // A role is offered when at least one organisation the grantor can delegate in offers it; a role every such organisation hides drops out of the picker.
        var delegating = subject is null ? [] : subject.Assignments.Where(a => a.IsActiveAt(now) && AccessEvaluator.Default.Matrix.Of(a.RoleCode, "roles.delegate") != Grant.None).ToList();
        var inReach = delegating.Any(a => a.Scope.Level != ScopeLevel.Organisation) ? Organisations : Organisations.Where(o => delegating.Any(a => a.Scope.OrganisationIds.Contains(o.Id))).ToList();
        var offeredAnywhere = new HashSet<string>(StringComparer.Ordinal);
        foreach (var o in inReach)
        {
            var words = await terminology.GetAsync(o.Id, null, ct);
            foreach (var r in GrantableRoles.Where(words.IsRoleOffered)) offeredAnywhere.Add(r);
        }
        if (inReach.Count > 0)
        {
            RolesNotOffered = GrantableRoles.Where(r => !offeredAnywhere.Contains(r)).ToList();
            GrantableRoles = GrantableRoles.Where(offeredAnywhere.Contains).ToList();
        }
        _names = Organisations.ToDictionary(o => o.Id, o => o.Name);
        var divs = new List<DivisionDto>(); var bus = new List<BusinessUnitDto>(); var sites = new List<SiteDto>();
        foreach (var o in Organisations) { var s = await structure.GetAsync(o.Id, ct); divs.AddRange(s.Divisions); bus.AddRange(s.BusinessUnits); sites.AddRange(s.Sites); foreach (var x in s.Divisions) _names[x.Id] = x.Name; foreach (var x in s.BusinessUnits) _names[x.Id] = x.Name; foreach (var x in s.Sites) _names[x.Id] = x.Name; }
        (Divisions, BusinessUnits, Sites) = (divs, bus, sites);
        Categories = await categories.ListAsync(ct);
        foreach (var c in Categories) _names[c.Id] = c.Name;
        var scale = await access.ClassificationScaleAsync(ct);
        (Levels, DefaultLevel) = (scale.Levels, scale.DefaultLevel);
    }

    public string Describe(AssignmentDto a)
    {
        string Names(IEnumerable<Guid> ids) => string.Join(", ", ids.Select(i => _names.TryGetValue(i, out var n) ? n : "?"));
        var parts = new List<string>
        {
            a.Scope.Level switch { ScopeLevel.Installation => "Installation", ScopeLevel.Tenant => "Whole tenant", _ => Names(a.Scope.OrganisationIds) }
        };
        if (a.Scope.NarrowsDivision) parts.Add("[[division|plural]]: " + Names(a.Scope.DivisionIds));
        if (a.Scope.NarrowsBusinessUnit) parts.Add("[[business.unit|plural]]: " + Names(a.Scope.BusinessUnitIds));
        if (a.Scope.NarrowsSite) parts.Add("[[site|plural]]: " + Names(a.Scope.SiteIds));
        if (a.Scope.NarrowsCategory) parts.Add("[[category|plural]]: " + Names(a.Scope.CategoryIds!));
        return string.Join("; ", parts);
    }

    public string Period(AssignmentDto a) => Format.Date(a.EffectiveFromUtc) + (a.EffectiveToUtc is { } to ? " to " + Format.Date(to) : " onwards");
}
