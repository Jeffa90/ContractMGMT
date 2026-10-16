using System.ComponentModel.DataAnnotations;
using Ecp.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Contracts;

[Authorize(Policy = "perm:contracts.read")]
public sealed class EditModel(ISecuredContractService contracts) : EcpPageModel
{
    [BindProperty] [Required(ErrorMessage = "Enter a title.")] [StringLength(500)] public string Title { get; set; } = "";
    [BindProperty] [StringLength(4000)] public string? Description { get; set; }
    public Guid Id { get; private set; }
    public string Reference { get; private set; } = "";

    public Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct) => RunAsync(async () =>
    {
        var c = await contracts.GetAsync(id, ct);
        if (!c.CanEdit) return Forbid();
        (Id, Reference, Title, Description) = (c.Id, c.AgreementReference, c.Title, c.Description);
        return Page();
    });

    public Task<IActionResult> OnPostAsync(Guid id, CancellationToken ct) => RunAsync(async () =>
    {
        Id = id;
        if (!ModelState.IsValid) return Page();
        await contracts.UpdateDetailsAsync(id, Title, Description, ct); // authorised and lock-checked in the service and the domain
        Flash("Contract details saved.");
        return RedirectToPage("Details", new { id });
    });
}
