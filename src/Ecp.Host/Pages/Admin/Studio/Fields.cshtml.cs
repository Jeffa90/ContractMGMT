using Ecp.Configuration;
using Ecp.Identity;
using Ecp.Metadata;
using Ecp.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Admin.Studio;

/// <summary>The fields of an organisation: what is published, the open draft, its checks and impact, and the draft/validate/publish steps.</summary>
[Authorize(Policy = "perm:config.draft")]
public sealed class FieldsModel(IOrganisationStructureService organisations, IAccessService access, ICustomFieldStudio studio) : StudioPage(organisations, access)
{
    [BindProperty] public string? Note { get; set; }
    public StudioState? State { get; private set; }

    public Task<IActionResult> OnGetAsync(CancellationToken ct) => RunAsync(async () => { await LoadAsync(ct); return Page(); });

    private async Task LoadAsync(CancellationToken ct)
    {
        if (await ResolveOrganisationAsync(ct) is { } org) State = await studio.LoadAsync(org, false, ct);
    }

    private Task Reload() => LoadAsync(CancellationToken.None);

    public Task<IActionResult> OnPostStartAsync(CancellationToken ct) => Act(org => studio.StartDraftAsync(org, ct), "A draft is open. Add or change fields, then validate and publish.", Reload);
    public Task<IActionResult> OnPostMoveAsync(string key, int direction, CancellationToken ct) => Act(org => studio.MoveFieldAsync(org, key, direction, ct), "The order is changed in the draft.", Reload);
    public Task<IActionResult> OnPostRetireAsync(string key, CancellationToken ct) => Act(org => studio.SetRetiredAsync(org, key, true, ct), "The field is retired in the draft. Values already entered are kept.", Reload);
    public Task<IActionResult> OnPostReinstateAsync(string key, CancellationToken ct) => Act(org => studio.SetRetiredAsync(org, key, false, ct), "The field is reinstated in the draft.", Reload);
    public Task<IActionResult> OnPostRemoveAsync(string key, CancellationToken ct) => Act(org => studio.RemoveDraftFieldAsync(org, key, ct), "The field is removed from the draft.", Reload);
    public Task<IActionResult> OnPostValidateAsync(CancellationToken ct) => Act(async org => { await studio.ValidateAsync(org, ct); }, "Validated. The results are below.", Reload);
    public Task<IActionResult> OnPostPublishAsync(CancellationToken ct) => Act(async org => { await studio.PublishAsync(org, Note ?? "", ct); }, "Published. The new definitions apply to forms, search and exports from now on; every stored value is kept.", Reload);
    public Task<IActionResult> OnPostDiscardAsync(CancellationToken ct) => Act(org => studio.DiscardDraftAsync(org, ct), "The draft is discarded.", Reload);
}
