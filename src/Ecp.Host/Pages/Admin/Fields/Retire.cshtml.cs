using Ecp.Configuration;
using Ecp.Identity;
using Ecp.Metadata;
using Ecp.SharedKernel;
using Ecp.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Admin.Fields;

/// <summary>Retiring is how a field leaves service: it is never deleted, so every value already entered is kept and still shown on the records that hold it. This page says so before it does it, and can bring a field back.</summary>
[Authorize(Policy = "perm:config.draft")]
public sealed class RetireModel(IOrganisationStructureService organisations, IAccessService access, ICustomFieldStudio studio, IConfigurationService configuration)
    : FieldsPage(organisations, access, studio, configuration)
{
    [BindProperty(SupportsGet = true)] public string Key { get; set; } = "";
    public FieldDefinition? Field { get; private set; }
    public bool Published { get; private set; }

    public Task<IActionResult> OnGetAsync(CancellationToken ct) => RunAsync(async () => { await LoadAsync(ct); return Page(); });

    private async Task LoadAsync(CancellationToken ct)
    {
        await LoadStateAsync(ct);
        Field = State.Working.Find(Key) ?? throw new ResourceNotFoundException();
        Published = State.Published.Find(Key) is not null;
    }

    public Task<IActionResult> OnPostAsync(CancellationToken ct) => RunAsync(async () =>
    {
        var org = await LoadStateAsync(ct);
        var field = State.Working.Find(Key) ?? throw new ResourceNotFoundException();
        await EnsureDraftAsync(org, ct);
        var retire = !field.IsRetired;
        await Studio.SetRetiredAsync(org, field.Key, retire, ct);
        Flash(retire
            ? $"{field.Label} is retired in the draft. Publish to apply it. Values already entered are kept."
            : $"{field.Label} is back in service in the draft. Publish to apply it.");
        return RedirectToPage("Index", new { organisationId = org });
    }, () => LoadAsync(CancellationToken.None));
}
