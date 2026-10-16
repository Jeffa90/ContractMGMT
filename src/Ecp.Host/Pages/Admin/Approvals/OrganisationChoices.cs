using Ecp.Identity;
using Ecp.Identity.Access;
using Ecp.Tenancy;

namespace Ecp.Host.Pages.Admin.Approvals;

/// <summary>The organisations a person may use an approval page for: only those where they hold at least one of the permissions the page needs, so the page never opens on an organisation it would refuse.</summary>
public static class OrganisationChoices
{
    public static async Task<IReadOnlyList<OrganisationSummary>> AllowedAsync(IOrganisationStructureService organisations, IAccessService access, CancellationToken ct, params string[] permissions)
    {
        var grants = new List<AccessGrant>();
        foreach (var permission in permissions) grants.AddRange(await access.GrantsAsync(permission, ct));
        return (await organisations.ListOrganisationsAsync(ct)).Where(o => grants.Any(g => g.OrganisationIds is null || g.OrganisationIds.Contains(o.Id))).ToList();
    }
}
