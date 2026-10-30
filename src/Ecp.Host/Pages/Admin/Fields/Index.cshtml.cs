using Ecp.Configuration;
using Ecp.Identity;
using Ecp.Metadata;
using Ecp.SharedKernel;
using Ecp.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Admin.Fields;

/// <summary>
/// The fields an organisation records on its [[contract|plural|lower]]: what each is, where it appears, whether it is required and whether it is in force. Adding, editing, previewing, retiring and the version history
/// all start here. Changes wait in a draft that the person never has to open by hand; one panel at the foot publishes them, or submits them for a second person where the organisation requires one.
/// </summary>
[Authorize(Policy = "perm:config.draft|config.history.read")]
public sealed class IndexModel(IOrganisationStructureService organisations, IAccessService access, ICustomFieldStudio studio, IConfigurationService configuration)
    : FieldsPage(organisations, access, studio, configuration)
{
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    [BindProperty] public string? Note { get; set; }
    public IReadOnlyList<FieldRow> Found { get; private set; } = [];

    public Task<IActionResult> OnGetAsync(CancellationToken ct) => RunAsync(async () => { await ReloadAsync(ct); return Page(); });

    private async Task ReloadAsync(CancellationToken ct)
    {
        if (await ResolveOrganisationAsync(ct) is null) return;
        await LoadStateAsync(ct);
        Found = Rows(Q);
    }

    private Task Reload() => ReloadAsync(CancellationToken.None);

    public Task<IActionResult> OnPostMoveAsync(string key, int direction, CancellationToken ct) => Act(org => Studio.MoveFieldAsync(org, key, direction, ct), "The order is changed. Publish to apply it.", Reload);

    public Task<IActionResult> OnPostDiscardAsync(CancellationToken ct) => Act(org => Studio.DiscardDraftAsync(org, ct), "The unpublished changes are discarded. The fields are as they were.", Reload);

    /// <summary>Checks the changes, then publishes them, or submits them for a second person to publish, whichever this person's organisation requires.</summary>
    public Task<IActionResult> OnPostPublishAsync(CancellationToken ct) => StepUpRunAsync(async () =>
    {
        var org = await LoadStateAsync(ct);
        var report = await Studio.ValidateAsync(org, ct);
        if (!report.IsValid)
        {
            ModelState.AddModelError(string.Empty, "These changes cannot be published yet. Fix what is listed under \"Checks\" and try again.");
            await ReloadAsync(ct);
            return Page();
        }
        var note = string.IsNullOrWhiteSpace(Note) ? Describe() : Note.Trim();
        var outcome = await Studio.SubmitOrPublishAsync(org, note, ct);
        Flash(outcome.Submitted
            ? "Submitted for publication. A second person has to review and publish it; nothing changes on the forms until they do."
            : "Published. The fields are on the forms, in search, filters and exports from now on. Every value already entered is kept.");
        return RedirectToPage(new { organisationId = org });
    }, () => ReloadAsync(CancellationToken.None));

    /// <summary>A sentence for the version history when the person leaves the note blank: what was added, changed or retired.</summary>
    private string Describe()
    {
        var parts = new List<string>();
        foreach (var f in PendingFields)
        {
            var (key, _) = FieldGuide.StatusOf(f, State.Published.Find(f.Key));
            parts.Add((key switch { "new" => "Added ", "retiring" => "Retired ", "reinstating" => "Reinstated ", _ => "Changed " }) + f.Label);
        }
        return parts.Count == 0 ? "Fields updated" : string.Join("; ", parts);
    }
}
