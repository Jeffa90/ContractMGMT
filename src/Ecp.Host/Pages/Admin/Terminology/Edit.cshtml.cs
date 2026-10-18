using Ecp.Configuration;
using Ecp.Host.Pages.Admin.Reviews;
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

    public sealed class GroupRowInput
    {
        public string? Key { get; set; }
        public string? Name { get; set; }
        public string? Description { get; set; }
    }

    public sealed class RoleRowInput
    {
        public string Code { get; set; } = "";
        public string Choice { get; set; } = "inherit";
    }

    public const int ExtraGroupRows = 3;

    [BindProperty] public List<RowInput> Rows { get; set; } = [];
    [BindProperty] public List<GroupRowInput> GroupRows { get; set; } = [];
    [BindProperty] public List<RoleRowInput> RoleRows { get; set; } = [];
    [BindProperty] public string? Note { get; set; }
    public TerminologyEditModel? Form { get; private set; }
    public IReadOnlyList<ValidationMessage> Problems { get; private set; } = [];
    public bool CanPublish { get; private set; }
    public GovernanceInfo? Governance { get; private set; }

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
        if (!fromPost || (GroupRows.Count == 0 && RoleRows.Count == 0))
        {
            GroupRows = Form.Groups.Select(g => new GroupRowInput { Key = g.Key, Name = g.Entry?.Name, Description = g.Entry?.Description }).ToList();
            for (var n = 0; n < ExtraGroupRows; n++) GroupRows.Add(new GroupRowInput());
            RoleRows = Form.Roles.Select(r => new RoleRowInput { Code = r.Code, Choice = r.Choice is null ? "inherit" : r.Choice.Value ? "offered" : "hidden" }).ToList();
        }
        CanPublish = await HoldsAsync("terminology.publish");
        Governance = await configuration.TryGovernanceAsync(Form.Draft?.Id, ct);
        if (Form.Draft?.Validation is { } report) Problems = report.Messages;
    }

    private IReadOnlyList<GroupInput> GroupInputs() => GroupRows.Select(g => new GroupInput(g.Key ?? "", g.Name, g.Description)).ToList();
    private IReadOnlyList<RoleInput> RoleInputs() => RoleRows.Select(r => new RoleInput(r.Code, r.Choice ?? "inherit")).ToList();

    private IReadOnlyList<TermInput> Inputs() => Rows.Select(r => new TermInput(r.Key, r.Override, r.Singular, r.Plural, r.Short, r.Description, r.Help)).ToList();

    /// <summary>Maps a validation message such as <c>$.terms['supplier','en'].singular</c> to the field it is about, so the error summary links to it.</summary>
    private void ShowProblems(IEnumerable<ValidationMessage> messages)
    {
        for (var i = 0; i < Rows.Count; i++) { }
        foreach (var m in messages.Where(m => m.Severity == ValidationSeverity.Error))
        {
            if (m.Path.StartsWith("$.groups[", StringComparison.Ordinal))
            {
                var gi = GroupRows.FindIndex(g => !string.IsNullOrEmpty(g.Key) ? m.Path.Contains($"['{g.Key}',", StringComparison.Ordinal) : m.Path.Contains("['',", StringComparison.Ordinal));
                var gf = m.Path.Contains(".description", StringComparison.Ordinal) ? "description" : m.Path.Contains(".name", StringComparison.Ordinal) ? "name" : "key";
                ModelState.AddModelError(gi >= 0 ? $"group-{gi}-{gf}" : string.Empty, "Approver group: " + m.Message);
                continue;
            }
            if (m.Path.StartsWith("$.roles[", StringComparison.Ordinal))
            {
                var ri = RoleRows.FindIndex(r => m.Path.Contains($"['{r.Code}']", StringComparison.Ordinal));
                ModelState.AddModelError(ri >= 0 ? $"role-{ri}" : string.Empty, "Role: " + m.Message);
                continue;
            }
            var row = Rows.FindIndex(r => m.Path.Contains($"['{r.Key}',", StringComparison.Ordinal));
            var field = m.Path.EndsWith(".singular", StringComparison.Ordinal) ? "singular" : m.Path.EndsWith(".plural", StringComparison.Ordinal) ? "plural" : m.Path.EndsWith(".short", StringComparison.Ordinal) ? "short"
                : m.Path.EndsWith(".description", StringComparison.Ordinal) ? "description" : m.Path.EndsWith(".help", StringComparison.Ordinal) ? "help" : "singular";
            ModelState.AddModelError(row >= 0 ? $"row-{row}-{field}" : string.Empty, (row >= 0 ? $"{Rows[row].Key}: " : "") + m.Message);
        }
    }

    private async Task<DraftDto> SaveAndValidateAsync(Guid org, CancellationToken ct)
    {
        var saved = await terminology.SaveAsync(org, Locale ?? "en", Inputs(), GroupInputs(), RoleInputs(), ct);
        return await configuration.ValidateAsync(saved.Id, ct);
    }

    public Task<IActionResult> OnPostSaveAsync(CancellationToken ct) => RunAsync(async () =>
    {
        var org = await ResolveOrganisationAsync(ct);
        await terminology.SaveAsync(org, Locale ?? "en", Inputs(), GroupInputs(), RoleInputs(), ct);
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
        var outcome = await configuration.SubmitOrPublishAsync(draft.Id, string.IsNullOrWhiteSpace(Note) ? "Terminology published" : Note!, ct);
        Flash(outcome.Message("Published. People see the new wording from their next page. Permissions, workflows and records are unchanged."));
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
