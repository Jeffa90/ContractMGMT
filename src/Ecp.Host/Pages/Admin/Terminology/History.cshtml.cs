using Ecp.Configuration;
using Ecp.Identity;
using Ecp.SharedKernel;
using Ecp.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Admin.Terminology;

/// <summary>Every published release of the organisation's terminology, what each changed, who published it and why. Restoring an earlier release publishes it again as a new one; history is never rewritten.</summary>
[Authorize(Policy = "perm:terminology.read")]
public sealed class HistoryModel(ITerminologyAdministration terminology, IConfigurationService configuration, IOrganisationStructureService organisations, IAccessService access, IAuthorizationService authorisation)
    : TerminologyPage(organisations, access, authorisation)
{
    [BindProperty] public int ToRelease { get; set; }
    [BindProperty] public string? Reason { get; set; }
    public IReadOnlyList<TerminologyReleaseView> Releases { get; private set; } = [];
    public bool CanRollback { get; private set; }

    public Task<IActionResult> OnGetAsync(CancellationToken ct) => RunAsync(async () => { await LoadAsync(ct); return Page(); });

    private async Task LoadAsync(CancellationToken ct)
    {
        var org = await ResolveOrganisationAsync(ct);
        Releases = await terminology.HistoryAsync(org, ct);
        CanRollback = await HoldsAsync("terminology.rollback");
    }

    public Task<IActionResult> OnPostRollbackAsync(CancellationToken ct) => StepUpAsync(async () =>
    {
        var org = await ResolveOrganisationAsync(ct);
        if (string.IsNullOrWhiteSpace(Reason)) throw new DomainException("reason_required", "Give a reason for restoring the earlier release.");
        var outcome = await configuration.RequestRollbackAsync(TerminologyKinds.Name, org, ToRelease, Reason!, ct);
        Flash(outcome.Release is not null ? "The earlier release is published again as a new release. History is kept, and no permission or workflow changed." : "Submitted for review. A second person has to review and publish the rollback; nothing changes until they do.");
        return RedirectToPage(new { organisationId = org });
    }, () => LoadAsync(ct));
}
