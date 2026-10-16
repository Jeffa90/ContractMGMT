using Ecp.Identity;
using Ecp.Configuration;
using Ecp.Contracts.Register;
using Ecp.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Admin.Studio;

/// <summary>The list views an organisation shares: draft, check, publish and discard. A published view appears for everyone in the organisation, each seeing only what their own access allows.</summary>
[Authorize(Policy = "perm:config.draft")]
public sealed class ViewsModel(IOrganisationStructureService organisations, IAccessService access, IContractViewStudio studio) : StudioPage(organisations, access)
{
    [BindProperty] public string? Note { get; set; }
    public ViewStudioState? State { get; private set; }

    public Task<IActionResult> OnGetAsync(CancellationToken ct) => RunAsync(async () => { await LoadAsync(ct); return Page(); });

    private async Task LoadAsync(CancellationToken ct)
    {
        if (await ResolveOrganisationAsync(ct) is { } org) State = await studio.LoadAsync(org, false, ct);
    }

    private Task Reload() => LoadAsync(CancellationToken.None);

    public Task<IActionResult> OnPostStartAsync(CancellationToken ct) => Act(org => studio.StartDraftAsync(org, ct), "A draft is open. Add or change views, then validate and publish.", Reload);
    public Task<IActionResult> OnPostRemoveAsync(string key, CancellationToken ct) => Act(org => studio.RemoveViewAsync(org, key, ct), "The view is removed from the draft.", Reload);
    public Task<IActionResult> OnPostValidateAsync(CancellationToken ct) => Act(async org => { await studio.ValidateAsync(org, ct); }, "Validated. The results are below.", Reload);
    public Task<IActionResult> OnPostPublishAsync(CancellationToken ct) => Act(async org => { await studio.PublishAsync(org, Note ?? "", ct); }, "Published. The views are available to everyone in the organisation.", Reload);
    public Task<IActionResult> OnPostDiscardAsync(CancellationToken ct) => Act(org => studio.DiscardDraftAsync(org, ct), "The draft is discarded.", Reload);
}
