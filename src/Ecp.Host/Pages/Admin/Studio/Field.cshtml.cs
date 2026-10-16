using Ecp.Contracts;
using Ecp.Identity;
using Ecp.Identity.Domain;
using Ecp.Metadata;
using Ecp.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Admin.Studio;

/// <summary>Create or change one field of the open draft: label and help, type, choices, validation, who sees and changes it, where it is used, and a preview of how it will look in five places before anything is published.</summary>
[Authorize(Policy = "perm:config.draft")]
public sealed class FieldModel(IOrganisationStructureService organisations, IAccessService access, ICustomFieldStudio studio, IEnumerable<IWorkflowStateSource> stateSources) : StudioPage(organisations, access)
{
    [BindProperty(SupportsGet = true)] public string? Key { get; set; }
    [BindProperty] public FieldEdit Edit { get; set; } = new();
    public StudioState State { get; private set; } = null!;
    public bool IsNew => string.IsNullOrEmpty(Key);
    public IReadOnlyList<string> Roles => RoleCodes.All;
    public IReadOnlyList<string> Levels { get; private set; } = [];
    public IReadOnlySet<string>? KnownStates { get; private set; }
    public FieldDefinition? Preview { get; private set; }
    public bool Locked { get; private set; }

    public Task<IActionResult> OnGetAsync(CancellationToken ct) => RunAsync(async () =>
    {
        await LoadAsync(ct);
        if (!IsNew)
        {
            var existing = State.Working.Find(Key!) ?? throw new Ecp.SharedKernel.ResourceNotFoundException();
            Edit = FieldEdit.From(existing);
            Preview = existing;
        }
        return Page();
    });

    /// <summary>Shows the five previews for what is on the form, without saving anything.</summary>
    public Task<IActionResult> OnPostPreviewAsync(CancellationToken ct) => RunAsync(async () =>
    {
        await LoadAsync(ct);
        ModelState.Clear();
        Preview = Edit.Build(ModelState);
        return Page();
    });

    public Task<IActionResult> OnPostAsync(CancellationToken ct) => RunAsync(async () =>
    {
        await LoadAsync(ct);
        if (!IsNew) Edit.Key = Key!;
        var definition = Edit.Build(ModelState);
        if (definition is null || !ModelState.IsValid) { Preview = definition; return Page(); }
        if (!State.HasDraft) { ModelState.AddModelError(string.Empty, "Start a draft before changing fields."); return Page(); }
        await studio.SaveFieldAsync(OrganisationId!.Value, definition, ct);
        Flash("The field is saved in the draft. Validate the draft to see whether it can be published.");
        return RedirectToPage("Fields", new { organisationId = OrganisationId });
    }, () => LoadAsync(CancellationToken.None));

    private async Task LoadAsync(CancellationToken ct)
    {
        var org = await ResolveOrganisationAsync(ct) ?? throw new Ecp.SharedKernel.ResourceNotFoundException();
        State = await studio.LoadAsync(org, false, ct);
        Levels = (await Access.ClassificationScaleAsync(ct)).Levels;
        foreach (var source in stateSources) if (await source.StatesAsync(org, ct) is { } known) { KnownStates = known; break; }
        Locked = !IsNew && State.Published.Find(Key!) is not null;
    }

    public CustomFieldForm PreviewForm(FieldDefinition d, bool editable, string sample)
    {
        var set = new CustomFieldSet { Sections = State.Working.Sections, Lookups = State.Working.Lookups, Fields = [d] };
        var raw = sample.Length == 0 ? d.Default : [sample];
        return new CustomFieldForm(0, set, [new CustomFieldEntry(d, raw, raw.Count == 0 ? "" : string.Join("; ", raw), editable)], [], []);
    }
}
