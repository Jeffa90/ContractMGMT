using Ecp.Host.Security;
using Ecp.Identity;
using Ecp.SharedKernel;
using Ecp.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Admin.Capabilities;

/// <summary>
/// Shared behaviour of the Capabilities pages: the organisation being worked on (only those the person holds a capability right in; naming any other is a plain not-found) and the step-up that publishing and rolling
/// back need. The services check the permission on every call. A capability switches an optional step of the process on or off; it never changes who may do anything.
/// </summary>
public abstract class CapabilityPage(IOrganisationStructureService organisations, IAccessService access, IAuthorizationService authorisation) : EcpPageModel
{
    [BindProperty(SupportsGet = true)] public Guid? OrganisationId { get; set; }
    public IReadOnlyList<OrganisationSummary> Organisations { get; private set; } = [];

    protected async Task<Guid> ResolveOrganisationAsync(CancellationToken ct)
    {
        var grants = (await access.GrantsAsync("capabilities.read", ct)).Concat(await access.GrantsAsync("capabilities.draft", ct)).ToList();
        Organisations = (await organisations.ListOrganisationsAsync(ct)).Where(o => grants.Any(g => g.OrganisationIds is null || g.OrganisationIds.Contains(o.Id))).ToList();
        if (OrganisationId is { } given && Organisations.All(o => o.Id != given)) throw new ResourceNotFoundException();
        OrganisationId ??= Organisations.FirstOrDefault()?.Id;
        if (OrganisationId is not { } org) throw new ResourceNotFoundException();
        await Terms.UseOrganisationAsync(org, ct);
        return org;
    }

    protected async Task<bool> HoldsAsync(string permission) => (await authorisation.AuthorizeAsync(User, Policies.PermissionPrefix + permission)).Succeeded;
    protected async Task<bool> RecentAsync() => (await authorisation.AuthorizeAsync(User, Policies.RecentAuthentication)).Succeeded;
    protected IActionResult ReauthRedirect() => Redirect("/Reauth?returnUrl=" + Uri.EscapeDataString(Request.Path.Value + Request.QueryString.Value));
    protected async Task<IActionResult> StepUpAsync(Func<Task<IActionResult>> body, Func<Task>? reload = null) => await RecentAsync() ? await RunAsync(body, reload) : ReauthRedirect();
}
