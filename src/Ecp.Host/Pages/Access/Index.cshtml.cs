using Ecp.Identity;
using Ecp.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Access;

public sealed record RoleRow(string Role, string Scope, string Ceiling);

/// <summary>My access: the signed-in person's own role assignments. (UX-2 adds what each means in plain English.)</summary>
[Authorize]
public sealed class IndexModel(IAccessService access, IRoleAssignmentService assignments, IOrganisationStructureService organisations) : EcpPageModel
{
    public IReadOnlyList<RoleRow> Roles { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (access.CurrentUserId is not { } me) return Challenge();
        var orgNames = (await organisations.ListOrganisationsAsync(ct)).ToDictionary(o => o.Id, o => o.Name);
        Roles = (await assignments.ListForUserAsync(me, includeEnded: false, ct)).Select(a => new RoleRow(
            a.RoleCode + (a.ViewerKind is { } k ? $" ({k})" : ""), DescribeScope(a.Scope, orgNames), a.Scope.ClassificationCeiling)).ToList();
        return Page();
    }

    internal static string DescribeScope(Ecp.Identity.Domain.ScopeSet scope, IReadOnlyDictionary<Guid, string> orgNames)
    {
        var parts = new List<string>
        {
            scope.Level switch
            {
                Ecp.Identity.Domain.ScopeLevel.Installation => "Installation",
                Ecp.Identity.Domain.ScopeLevel.Tenant => "Whole tenant",
                _ => string.Join(", ", scope.OrganisationIds.Select(o => orgNames.TryGetValue(o, out var n) ? n : "organisation"))
            }
        };
        if (scope.NarrowsBusinessUnit) parts.Add($"{scope.BusinessUnitIds.Count} business unit(s)");
        if (scope.NarrowsDivision) parts.Add($"{scope.DivisionIds.Count} division(s)");
        if (scope.NarrowsSite) parts.Add($"{scope.SiteIds.Count} site(s)");
        if (scope.NarrowsCategory) parts.Add($"{scope.CategoryIds!.Count} categor{(scope.CategoryIds.Count == 1 ? "y" : "ies")}");
        return string.Join("; ", parts);
    }
}
