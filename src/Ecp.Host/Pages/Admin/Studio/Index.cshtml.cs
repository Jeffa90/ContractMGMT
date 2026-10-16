using Ecp.Contracts.Register;
using Ecp.Identity;
using Ecp.Metadata;
using Ecp.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Admin.Studio;

/// <summary>The Configuration Studio: form-based editing of the additional fields and the shared list views of an organisation. Everything here is a draft until it is validated and published.</summary>
[Authorize(Policy = "perm:config.draft|config.history.read")]
public sealed class IndexModel(IOrganisationStructureService organisations, IAccessService access, ICustomFieldStudio fields, IContractViewStudio views) : StudioPage(organisations, access)
{
    public bool FieldDraft { get; private set; }
    public bool ViewDraft { get; private set; }
    public int FieldCount { get; private set; }
    public int ViewCount { get; private set; }
    public int? FieldRelease { get; private set; }
    public bool CanDraft { get; private set; } = true;

    public Task<IActionResult> OnGetAsync(CancellationToken ct) => RunAsync(async () =>
    {
        if (await ResolveOrganisationAsync(ct) is not { } org) return Page();
        try
        {
            var f = await fields.LoadAsync(org, false, ct);
            (FieldDraft, FieldCount, FieldRelease) = (f.HasDraft, f.Published.Fields.Count, f.Release?.Number);
            var v = await views.LoadAsync(org, false, ct);
            (ViewDraft, ViewCount) = (v.Draft is not null, v.Published.Views.Count);
        }
        catch (Ecp.SharedKernel.ForbiddenException) { CanDraft = false; }   // history-only readers see the pages that show history
        return Page();
    });
}
