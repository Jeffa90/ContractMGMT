using System.ComponentModel.DataAnnotations;
using Ecp.Contracts;
using Ecp.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Contracts;

[Authorize(Policy = "perm:contracts.read")]
public sealed class EditModel(ISecuredContractService contracts, IContractCustomFields customFields, IOrganisationStructureService organisations, ICoreFieldLists coreLists) : EcpPageModel
{
    [BindProperty] [Required(ErrorMessage = "Enter a title.")] [StringLength(500)] public string Title { get; set; } = "";
    [BindProperty] [StringLength(4000)] public string? Description { get; set; }
    [BindProperty] public ContractFieldsInput Input { get; set; } = new();
    public Guid Id { get; private set; }
    public string Reference { get; private set; } = "";
    public ContractFieldOptions Options { get; private set; } = ContractFieldOptions.Empty;
    public CustomFieldForm? Fields { get; private set; }

    public Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct) => RunAsync(async () =>
    {
        var c = await contracts.GetAsync(id, ct);
        if (!c.CanEdit) return Forbid();
        (Id, Reference, Title, Description) = (c.Id, c.AgreementReference, c.Title, c.Description);
        Input = ContractFieldsInput.From(c);
        await LoadAsync(c, null, null, ct);
        return Page();
    });

    public Task<IActionResult> OnPostAsync(Guid id, CancellationToken ct) => RunAsync(async () =>
    {
        Id = id;
        var c = await contracts.GetAsync(id, ct);
        if (!c.CanEdit) return Forbid();
        Reference = c.AgreementReference;
        var submitted = CustomFieldFormReader.Read(Request.Form);
        if (!ModelState.IsValid) { await LoadAsync(c, submitted, null, ct); return Page(); }
        var changes = Input.ToChanges();
        changes["contractTitle"] = Title;
        changes["description"] = string.IsNullOrWhiteSpace(Description) ? null : Description;
        try
        {
            // The core fields and the configured fields are saved together or not at all.
            await contracts.UpdateAllAsync(id, changes, submitted, ct);
        }
        catch (CustomFieldValidationException ex)
        {
            await LoadAsync(c, submitted, ex.Errors, ct);
            foreach (var (key, message) in ex.Errors) ModelState.AddModelError("cf_" + key, message);
            return Page();
        }
        Flash("[[contract]] details saved.");
        return RedirectToPage("Details", new { id });
    }, async () => { var c = await contracts.GetAsync(id, ct); await LoadAsync(c, CustomFieldFormReader.Read(Request.Form), null, ct); });

    private async Task LoadAsync(ContractDetails c, CustomFieldInput? submitted, IReadOnlyDictionary<string, string>? errors, CancellationToken ct)
    {
        Id = c.Id; Reference = c.AgreementReference;
        Options = await ContractFieldOptionsLoader.LoadAsync(c.OrganisationId, organisations, coreLists, ct);
        Fields = await customFields.ForContractAsync(c.Id, submitted, errors, ct);
    }
}
