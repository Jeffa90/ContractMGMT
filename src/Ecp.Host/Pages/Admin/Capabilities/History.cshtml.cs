using Ecp.Configuration;
using Ecp.Identity;
using Ecp.SharedKernel;
using Ecp.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Admin.Capabilities;

/// <summary>Every published release of the organisation's capability choices, what each changed, who published it and why. Restoring an earlier release publishes it again as a new one; history is never rewritten.</summary>
[Authorize(Policy = "perm:capabilities.read")]
public sealed class HistoryModel(ICapabilityAdministration capabilities, IConfigurationService configuration, IOrganisationStructureService organisations, IAccessService access, IAuthorizationService authorisation)
    : CapabilityPage(organisations, access, authorisation)
{
    [BindProperty] public int ToRelease { get; set; }
    [BindProperty] public string? Reason { get; set; }
    public IReadOnlyList<CapabilityReleaseView> Releases { get; private set; } = [];
    public bool CanRollback { get; private set; }

    public Task<IActionResult> OnGetAsync(CancellationToken ct) => RunAsync(async () => { await LoadAsync(ct); return Page(); });

    private async Task LoadAsync(CancellationToken ct)
    {
        var org = await ResolveOrganisationAsync(ct);
        Releases = await capabilities.HistoryAsync(org, ct);
        CanRollback = await HoldsAsync("capabilities.rollback");
    }

    public Task<IActionResult> OnPostRollbackAsync(CancellationToken ct) => StepUpAsync(async () =>
    {
        var org = await ResolveOrganisationAsync(ct);
        if (string.IsNullOrWhiteSpace(Reason)) throw new DomainException("reason_required", "Give a reason for restoring the earlier release.");
        await configuration.RollbackAsync(CapabilityKinds.Name, org, ToRelease, Reason!, ct);       // validated like any release: it is refused if it no longer fits the workflows or work in flight
        Flash("The earlier release is published again as a new release. History is kept, and no permission changed.");
        return RedirectToPage(new { organisationId = org });
    }, () => LoadAsync(ct));
}
