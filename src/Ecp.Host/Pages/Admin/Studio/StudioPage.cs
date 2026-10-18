using Ecp.Identity;
using Ecp.SharedKernel;
using Ecp.Tenancy;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Admin.Studio;

/// <summary>Shared behaviour of the Studio pages: the organisation being configured, and running an action then returning to the page.</summary>
public abstract class StudioPage(IOrganisationStructureService organisations, IAccessService access) : EcpPageModel
{
    protected IAccessService Access { get; } = access;
    [BindProperty(SupportsGet = true)] public Guid? OrganisationId { get; set; }
    public IReadOnlyList<OrganisationSummary> Organisations { get; private set; } = [];

    protected async Task<Guid?> ResolveOrganisationAsync(CancellationToken ct)
    {
        // Only the organisations the person holds a configuration right in: the others are not offered, and naming one is a plain not-found.
        var grants = (await Access.GrantsAsync("config.draft", ct)).Concat(await Access.GrantsAsync("config.history.read", ct)).ToList();
        Organisations = (await organisations.ListOrganisationsAsync(ct)).Where(o => grants.Any(g => g.OrganisationIds is null || g.OrganisationIds.Contains(o.Id))).ToList();
        if (OrganisationId is { } given && Organisations.All(o => o.Id != given)) throw new ResourceNotFoundException();
        OrganisationId ??= Organisations.FirstOrDefault()?.Id;
        return OrganisationId;
    }

    /// <summary>Publishing and rolling back: needs a recent sign-in, and the message depends on whether it took effect or went to a reviewer.</summary>
    protected Task<IActionResult> ActSensitive(Func<Guid, Task<string>> action, Func<Task> reload, object? route = null, string? page = null) => StepUpRunAsync(async () =>
    {
        var org = await ResolveOrganisationAsync(CancellationToken.None) ?? throw new ResourceNotFoundException();
        Flash(await action(org));
        return page is null ? RedirectToPage(route ?? new { organisationId = org }) : RedirectToPage(page, route ?? new { organisationId = org });
    }, reload);

    protected Task<IActionResult> Act(Func<Guid, Task> action, string flash, Func<Task> reload, object? route = null, string? page = null) => RunAsync(async () =>
    {
        var org = await ResolveOrganisationAsync(CancellationToken.None) ?? throw new ResourceNotFoundException();
        await action(org);
        Flash(flash);
        return page is null ? RedirectToPage(route ?? new { organisationId = org }) : RedirectToPage(page, route ?? new { organisationId = org });
    }, reload);
}
