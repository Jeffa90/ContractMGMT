using Ecp.Access;
using Ecp.Host.Security;
using Ecp.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Access;

/// <summary>
/// Set up my access. In Demo and Test an administrator can give themselves broad, bounded access to try the product (and take it away again), with exactly what will be granted said first. In Production the page
/// explains that nobody can give themselves access and what to do instead. All rules are the service's.
/// </summary>
[Authorize(Policy = "perm:users.manage")]
public sealed class SetupModel(IAdministratorBootstrap bootstrap, IOrganisationStructureService organisations, Ecp.Identity.IAccessService access, IEffectiveAccess effective) : EcpPageModel
{
    [BindProperty(SupportsGet = true)] public Guid? OrganisationId { get; set; }
    [BindProperty] public string? Reason { get; set; }
    public IReadOnlyList<OrganisationSummary> Organisations { get; private set; } = [];
    public TestAccessPlan? Plan { get; private set; }
    public bool TestMode => bootstrap.TestModeAvailable;
    public EffectiveAccessReport? Mine { get; private set; }

    public Task<IActionResult> OnGetAsync(CancellationToken ct) => RunAsync(async () => { await LoadAsync(ct); return Page(); });

    public Task<IActionResult> OnPostGiveAsync(CancellationToken ct) => StepUpRunAsync(async () =>
    {
        var org = OrganisationId ?? throw new Ecp.SharedKernel.DomainException("organisation_required", "Choose the [[organisation|lower]].");
        var result = await bootstrap.GiveFullTestAccessAsync(org, Reason ?? "Trying the product in Test mode", ct);
        Flash($"Test-mode access is on: {result.GrantsCreated} access grant(s) and {result.OwnershipsCreated} ownership(s) added. " + string.Join(" ", result.Notes));
        return Redirect("/");
    }, reload: () => LoadAsync(ct));

    public Task<IActionResult> OnPostEndAsync(CancellationToken ct) => StepUpRunAsync(async () =>
    {
        var ended = await bootstrap.EndTestAccessAsync(Reason ?? "Finished trying the product", ct);
        Flash($"Test-mode access was removed ({ended} grant(s) ended). Access you were given any other way is untouched.");
        return RedirectToPage(new { OrganisationId });
    }, reload: () => LoadAsync(ct));

    private async Task LoadAsync(CancellationToken ct)
    {
        var grants = await access.GrantsAsync("users.manage", ct);
        Organisations = (await organisations.ListOrganisationsAsync(ct)).Where(o => grants.Any(g => g.OrganisationIds is null || g.OrganisationIds.Contains(o.Id))).ToList();
        if (OrganisationId is null && Organisations.Count == 1) OrganisationId = Organisations[0].Id;
        Mine = await effective.ForMeAsync(ct);
        if (TestMode && OrganisationId is { } org) Plan = await bootstrap.PlanTestAccessAsync(org, ct);
    }
}
