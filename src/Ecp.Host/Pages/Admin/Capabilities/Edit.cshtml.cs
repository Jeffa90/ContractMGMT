using Ecp.Configuration;
using Ecp.Host.Pages.Admin.Reviews;
using Ecp.Identity;
using Ecp.SharedKernel;
using Ecp.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Admin.Capabilities;

/// <summary>
/// The capability draft as a form: for each capability, inherit (the pack's default, then the platform's) or choose on or off. Saving never publishes. What the person chose is kept when something is wrong: it is saved into
/// the draft first, then validated, and the page shows the draft again with the problems beside the rows they belong to. Validation checks that what is chosen fits the organisation's workflows and work in flight.
/// </summary>
[Authorize(Policy = "perm:capabilities.draft")]
public sealed class EditModel(ICapabilityAdministration capabilities, IConfigurationService configuration, IOrganisationStructureService organisations, IAccessService access, IAuthorizationService authorisation)
    : CapabilityPage(organisations, access, authorisation)
{
    public sealed class RowInput
    {
        public string Key { get; set; } = "";
        public string Choice { get; set; } = "inherit";
        public string? Note { get; set; }
    }

    [BindProperty] public List<RowInput> Rows { get; set; } = [];
    [BindProperty] public string? Note { get; set; }
    public CapabilityEditModel? Form { get; private set; }
    public IReadOnlyList<ValidationMessage> Problems { get; private set; } = [];
    public bool CanPublish { get; private set; }
    public GovernanceInfo? Governance { get; private set; }

    public Task<IActionResult> OnGetAsync(CancellationToken ct) => RunAsync(async () => { await LoadAsync(ct, fromPost: false); return Page(); });

    private async Task LoadAsync(CancellationToken ct, bool fromPost)
    {
        var org = await ResolveOrganisationAsync(ct);
        Form = await capabilities.EditModelAsync(org, ct);
        if (!fromPost || Rows.Count == 0)
            Rows = Form.Rows.Select(r => new RowInput { Key = r.Key, Choice = r.Choice is null ? "inherit" : r.Choice.Value ? "on" : "off", Note = r.Note }).ToList();
        CanPublish = await HoldsAsync("capabilities.publish");
        Governance = await configuration.TryGovernanceAsync(Form.Draft?.Id, ct);
        if (Form.Draft?.Validation is { } report) Problems = report.Messages;
    }

    private IReadOnlyList<CapabilityInput> Inputs() => Rows.Select(r => new CapabilityInput(r.Key, r.Choice, r.Note)).ToList();

    /// <summary>Maps a validation message such as <c>$.capabilities['legal.review']</c> to the row it is about, so the error summary links to it.</summary>
    private void ShowProblems(IEnumerable<ValidationMessage> messages)
    {
        foreach (var m in messages.Where(m => m.Severity == ValidationSeverity.Error))
        {
            var row = Rows.FindIndex(r => m.Path.Contains($"['{r.Key}']", StringComparison.Ordinal));
            ModelState.AddModelError(row >= 0 ? $"cap-{row}-choice" : string.Empty, (row >= 0 ? $"{Rows[row].Key}: " : "") + Terms.Expand(m.Message));
        }
    }

    private async Task<DraftDto> SaveAndValidateAsync(Guid org, CancellationToken ct)
    {
        var saved = await capabilities.SaveAsync(org, Inputs(), ct);
        return await configuration.ValidateAsync(saved.Id, ct);
    }

    public Task<IActionResult> OnPostSaveAsync(CancellationToken ct) => RunAsync(async () =>
    {
        var org = await ResolveOrganisationAsync(ct);
        await capabilities.SaveAsync(org, Inputs(), ct);
        Flash("The draft is saved. Nothing is published. Validate it, then preview it.");
        return RedirectToPage(new { organisationId = org });
    }, () => LoadAsync(ct, fromPost: true));

    public Task<IActionResult> OnPostValidateAsync(CancellationToken ct) => RunAsync(async () =>
    {
        var org = await ResolveOrganisationAsync(ct);
        var draft = await SaveAndValidateAsync(org, ct);
        if (draft.Validation is { IsValid: false } report)
        {
            await LoadAsync(ct, fromPost: true);
            ShowProblems(report.Messages);
            return Page();
        }
        Flash("The draft is valid. Preview it, then publish when you are satisfied.");
        return RedirectToPage(new { organisationId = org });
    }, () => LoadAsync(ct, fromPost: true));

    public Task<IActionResult> OnPostPublishAsync(CancellationToken ct) => StepUpAsync(async () =>
    {
        var org = await ResolveOrganisationAsync(ct);
        var draft = await SaveAndValidateAsync(org, ct);
        if (draft.Validation is { IsValid: false } report)
        {
            await LoadAsync(ct, fromPost: true);
            ShowProblems(report.Messages);
            return Page();
        }
        var outcome = await configuration.SubmitOrPublishAsync(draft.Id, string.IsNullOrWhiteSpace(Note) ? "Capabilities published" : Note!, ct);
        Flash(outcome.Message("Published. The pages, lists and lifecycle follow the new choices from the next request. Permissions and records are unchanged."));
        return RedirectToPage("Index", new { organisationId = org });
    }, () => LoadAsync(ct, fromPost: true));

    public Task<IActionResult> OnPostDiscardAsync(CancellationToken ct) => RunAsync(async () =>
    {
        var org = await ResolveOrganisationAsync(ct);
        var draft = await configuration.FindOpenDraftAsync(CapabilityKinds.Name, org, ct);
        if (draft is not null) await configuration.DiscardDraftAsync(draft.Id, ct);
        Flash("The draft is discarded. The published choices are unchanged.");
        return RedirectToPage("Index", new { organisationId = org });
    });
}
