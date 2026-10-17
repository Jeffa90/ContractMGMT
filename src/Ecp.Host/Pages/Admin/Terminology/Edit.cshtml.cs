using Ecp.Configuration;
using Ecp.Identity;
using Ecp.SharedKernel;
using Ecp.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Admin.Terminology;

/// <summary>
/// The terminology draft as a form, one locale at a time. A row that is not ticked inherits (the pack's wording, then the platform's); a ticked row is the organisation's own wording. Saving never publishes. What the person typed is
/// kept when something is wrong: it is saved into the draft first, then validated, and the page shows the draft again with the problems beside the rows they belong to.
/// </summary>
[Authorize(Policy = "perm:terminology.draft")]
public sealed class EditModel(ITerminologyAdministration terminology, IConfigurationService configuration, IOrganisationStructureService organisations, IAccessService access, IAuthorizationService authorisation)
    : TerminologyPage(organisations, access, authorisation)
{
    public sealed class RowInput
    {
        public string Key { get; set; } = "";
        public bool Override { get; set; }
        public string? Singular { get; set; }
        public string? Plural { get; set; }
        public string? Short { get; set; }
        public string? Description { get; set; }
        public string? Help { get; set; }
    }

    [BindProperty] public List<RowInput> Rows { get; set; } = [];
    [BindProperty] public string? Note { get; set; }
    public TerminologyEditModel? Form { get; private set; }
    public IReadOnlyList<ValidationMessage> Problems { get; private set; } = [];
    public bool CanPublish { get; private set; }

    public Task<IActionResult> OnGetAsync(CancellationToken ct) => RunAsync(async () => { await LoadAsync(ct, fromPost: false); return Page(); });

    private async Task LoadAsync(CancellationToken ct, bool fromPost)
    {
        var org = await ResolveOrganisationAsync(ct);
        Form = await terminology.EditModelAsync(org, Locale, ct);
        Locale = Form.Locale;
        if (!fromPost || Rows.Count == 0)
            Rows = Form.Rows.Select(r => new RowInput
            {
                Key = r.Key, Override = r.Entry is not null, Singular = r.Entry?.Singular, Plural = r.Entry?.Plural, Short = r.Entry?.Short, Description = r.Entry?.Description, Help = r.Entry?.Help
            }).ToList();
        CanPublish = await HoldsAsync("terminology.publish");
        if (Form.Draft?.Validation is { } report) Problems = report.Messages;
    }

    private IReadOnlyList<TermInput> Inputs() => Rows.Select(r => new TermInput(r.Key, r.Override, r.Singular, r.Plural, r.Short, r.Description, r.Help)).ToList();

    /// <summary>Maps a validation message such as <c>$.terms['supplier','en'].singular</c> to the field it is about, so the error summary links to it.</summary>
    private void ShowProblems(IEnumerable<ValidationMessage> messages)
    {
        for (var i = 0; i < Rows.Count; i++) { }
        foreach (var m in messages.Where(m => m.Severity == ValidationSeverity.Error))
        {
            var row = Rows.FindIndex(r => m.Path.Contains($"['{r.Key}',", StringComparison.Ordinal));
            var field = m.Path.EndsWith(".singular", StringComparison.Ordinal) ? "singular" : m.Path.EndsWith(".plural", StringComparison.Ordinal) ? "plural" : m.Path.EndsWith(".short", StringComparison.Ordinal) ? "short"
                : m.Path.EndsWith(".description", StringComparison.Ordinal) ? "description" : m.Path.EndsWith(".help", StringComparison.Ordinal) ? "help" : "singular";
            ModelState.AddModelError(row >= 0 ? $"row-{row}-{field}" : string.Empty, (row >= 0 ? $"{Rows[row].Key}: " : "") + m.Message);
        }
    }

    private async Task<DraftDto> SaveAndValidateAsync(Guid org, CancellationToken ct)
    {
        var saved = await terminology.SaveAsync(org, Locale ?? "en", Inputs(), ct);
        return await configuration.ValidateAsync(saved.Id, ct);
    }

    public Task<IActionResult> OnPostSaveAsync(CancellationToken ct) => RunAsync(async () =>
    {
        var org = await ResolveOrganisationAsync(ct);
        await terminology.SaveAsync(org, Locale ?? "en", Inputs(), ct);
        Flash("The draft is saved. Nothing is published. Validate it, then preview it.");
        return RedirectToPage(new { organisationId = org, locale = Locale });
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
        return RedirectToPage(new { organisationId = org, locale = Locale });
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
        await configuration.PublishAsync(draft.Id, string.IsNullOrWhiteSpace(Note) ? "Terminology published" : Note!, ct);
        Flash("Published. People see the new wording from their next page. Permissions, workflows and records are unchanged.");
        return RedirectToPage("Index", new { organisationId = org, locale = Locale });
    }, () => LoadAsync(ct, fromPost: true));

    public Task<IActionResult> OnPostDiscardAsync(CancellationToken ct) => RunAsync(async () =>
    {
        var org = await ResolveOrganisationAsync(ct);
        var draft = await configuration.FindOpenDraftAsync(TerminologyKinds.Name, org, ct);
        if (draft is not null) await configuration.DiscardDraftAsync(draft.Id, ct);
        Flash("The draft is discarded. The published wording is unchanged.");
        return RedirectToPage("Index", new { organisationId = org, locale = Locale });
    });
}
