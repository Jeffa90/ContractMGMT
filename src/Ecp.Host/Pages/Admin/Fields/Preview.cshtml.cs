using Ecp.Configuration;
using Ecp.Identity;
using Ecp.Metadata;
using Ecp.SharedKernel;
using Ecp.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Admin.Fields;

/// <summary>A saved field drawn the way the real forms will draw it, with the plain-English statement of what it does. Reading only; nothing changes.</summary>
[Authorize(Policy = "perm:config.draft|config.history.read")]
public sealed class PreviewModel(IOrganisationStructureService organisations, IAccessService access, ICustomFieldStudio studio, IConfigurationService configuration)
    : FieldsPage(organisations, access, studio, configuration)
{
    [BindProperty(SupportsGet = true)] public string Key { get; set; } = "";
    public FieldPreviewModel? Preview { get; private set; }
    public FieldDefinition? Field { get; private set; }
    public string StatusText { get; private set; } = "";

    public Task<IActionResult> OnGetAsync(CancellationToken ct) => RunAsync(async () =>
    {
        await LoadStateAsync(ct);
        Field = State.Working.Find(Key) ?? throw new ResourceNotFoundException();
        var published = State.Published.Find(Key);
        StatusText = FieldGuide.StatusOf(Field, published).Text;
        Preview = PreviewOf(Field, Field.Section, SectionLabel(State.Working, Field.Section), published is not null);
        return Page();
    });
}
