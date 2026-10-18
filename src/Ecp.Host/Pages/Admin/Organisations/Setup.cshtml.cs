using Ecp.Setup.Organisations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Admin.Organisations;

/// <summary>The set-up checklist of one organisation, read from what is actually published and drafted. Viewing needs <c>organisations.manage</c> or <c>onboarding.run</c> for the organisation; resetting needs the former.</summary>
[Authorize]
public sealed class SetupModel(IOrganisationCreation creation) : EcpPageModel
{
    [BindProperty(SupportsGet = true)] public Guid Id { get; set; }
    [BindProperty] public string? Reason { get; set; }
    public SetupChecklist? Checklist { get; private set; }

    public Task<IActionResult> OnGetAsync(CancellationToken ct) => RunAsync(async () => { Checklist = await creation.ChecklistAsync(Id, ct); return Page(); });

    public Task<IActionResult> OnPostResetAsync(CancellationToken ct) => StepUpRunAsync(async () =>
    {
        var outcome = await creation.ResetSetupAsync(Id, Reason ?? "", ct);
        Flash($"Set-up reset: {outcome.DraftsDiscarded} drafts discarded and {outcome.StructureItemsEnded} structure items ended.");
        return RedirectToPage(new { id = Id });
    }, async () => Checklist = await creation.ChecklistAsync(Id, ct));
}
