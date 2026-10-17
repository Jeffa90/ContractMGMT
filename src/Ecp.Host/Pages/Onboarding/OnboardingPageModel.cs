using Ecp.Setup.Onboarding;
using Ecp.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Onboarding;

/// <summary>Shared behaviour of the onboarding pages: the caller needs onboarding.run, and one organisation is selected (query string, defaulting to the first the caller can see).</summary>
[Authorize(Policy = "perm:onboarding.run")]
public abstract class OnboardingPageModel(IOnboardingService onboarding, IOrganisationStructureService organisations) : EcpPageModel
{
    protected IOnboardingService Onboarding { get; } = onboarding;

    [BindProperty(SupportsGet = true)] public Guid? Org { get; set; }

    /// <summary>True when the caller holds onboarding.run but not the organisation-level configuration rights it needs (for example a Tenant Administrator, whose configuration rights cover tenant-level definitions only).</summary>
    public bool Blocked { get; private set; }

    /// <summary>Runs a handler; a refusal by the configuration module is explained on the page (still HTTP 403) instead of ending in a bare error.</summary>
    protected Task<IActionResult> GuardedAsync(Func<Task<IActionResult>> body, Func<Task>? reload = null)
        => RunAsync(async () =>
        {
            try { return await body(); }
            catch (Ecp.SharedKernel.ForbiddenException) { Blocked = true; Response.StatusCode = StatusCodes.Status403Forbidden; return Page(); }
        }, reload);
    public IReadOnlyList<OrganisationSummary> Organisations { get; private set; } = [];
    public Guid OrganisationId { get; private set; }
    public string OrganisationName => Organisations.FirstOrDefault(o => o.Id == OrganisationId)?.Name ?? "";

    protected async Task<bool> SelectOrganisationAsync(CancellationToken ct)
    {
        Organisations = await organisations.ListOrganisationsAsync(ct);
        var chosen = Org is { } id ? Organisations.FirstOrDefault(o => o.Id == id) : Organisations.FirstOrDefault();
        if (chosen is null) return false;
        OrganisationId = chosen.Id;
        await Terms.UseOrganisationAsync(chosen.Id, ct);      // the pages describe this organisation in its own words
        return true;
    }
}
