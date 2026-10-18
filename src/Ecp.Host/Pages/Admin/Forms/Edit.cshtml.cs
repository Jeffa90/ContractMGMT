using System.Text.Json.Nodes;
using Ecp.Configuration;
using Ecp.Configuration.Forms;
using Ecp.Host.Pages.Admin.Reviews;
using Ecp.SharedKernel;
using Ecp.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Admin.Forms;

/// <summary>
/// One page that edits any configuration document that has a form: the contract workflow, the Variation workflow, the approval matrices and the notification policy. The person fills in fields, adds and moves rows with buttons and
/// reads a preview and an impact analysis; nothing is ever shown or typed as a document. Saving, checking, submitting and publishing are the configuration lifecycle's: this page only builds the document from the fields.
/// </summary>
[Authorize(Policy = "perm:config.draft|config.history.read")]
public sealed class EditModel(IConfigurationFormEditor editor, IConfigurationService configuration, IOrganisationStructureService organisations) : EcpPageModel
{
    [BindProperty(SupportsGet = true)] public string Kind { get; set; } = "";
    [BindProperty(SupportsGet = true)] public Guid? OrganisationId { get; set; }
    [BindProperty] public string? Note { get; set; }
    [BindProperty] public string? Op { get; set; }

    public IReadOnlyList<OrganisationSummary> Organisations { get; private set; } = [];
    public IReadOnlyList<FormSchema> Schemas => editor.Schemas();
    public FormEditState? State { get; private set; }
    public FormPageContext? Context { get; private set; }
    public FormInsight? Insight { get; private set; }
    public string? InsightProblem { get; private set; }

    private async Task<Guid> OrgAsync(CancellationToken ct)
    {
        Organisations = await organisations.ListOrganisationsAsync(ct);
        OrganisationId ??= Organisations.FirstOrDefault()?.Id;
        return OrganisationId ?? throw new ResourceNotFoundException();
    }

    private IReadOnlyDictionary<string, string[]> Posted()
        => Request.Form.Where(kv => kv.Key.StartsWith(FormBinder.Prefix, StringComparison.Ordinal)).ToDictionary(kv => kv.Key, kv => kv.Value.Select(v => v ?? "").ToArray(), StringComparer.Ordinal);

    public Task<IActionResult> OnGetAsync(CancellationToken ct) => RunAsync(async () =>
    {
        if (string.IsNullOrEmpty(Kind)) return Redirect("/Admin/Configuration");
        await ShowAsync(ct, null, []);
        return Page();
    });

    /// <summary>Loads the state and draws the page from a document (the saved one, or the one just built from the fields) and the problems to show beside it.</summary>
    private async Task ShowAsync(CancellationToken ct, JsonObject? document, IReadOnlyList<FormProblem> extra, IEnumerable<ValidationMessage>? messages = null)
    {
        var org = await OrgAsync(ct);
        State = await editor.OpenAsync(Kind, org, ct);
        var doc = document ?? State.Document;
        var problems = new List<FormProblem>(extra);
        problems.AddRange(messages is null ? State.Problems : editor.PlaceProblems(State.Schema, doc, messages));
        Context = new FormPageContext(State.Schema, doc, State.Lookups, problems, State.ReadOnly);
        try { Insight = await editor.InsightAsync(Kind, org, doc, ct); }
        catch (Exception ex) when (ex is DomainException or ForbiddenException) { InsightProblem = ex.Message; }
    }

    private Task<IActionResult> Redisplay(CancellationToken ct, JsonObject doc, IReadOnlyList<FormProblem> extra, IEnumerable<ValidationMessage>? messages = null) => RunAsync(async () => { await ShowAsync(ct, doc, extra, messages); return Page(); });

    public Task<IActionResult> OnPostStartAsync(CancellationToken ct) => RunAsync(async () =>
    {
        await editor.StartAsync(Kind, await OrgAsync(ct), ct);
        Flash("A draft is open. Change the fields, check it, then submit it.");
        return RedirectToPage(new { kind = Kind, organisationId = OrganisationId });
    });

    public Task<IActionResult> OnPostRowAsync(CancellationToken ct) => RunAsync(async () =>
    {
        var schema = editor.SchemaOf(Kind);
        var (doc, problems) = FormBinder.Bind(schema, Posted());
        FormBinder.Apply(schema, doc, Op);
        await ShowAsync(ct, doc, problems);
        return Page();
    });

    public Task<IActionResult> OnPostSaveAsync(CancellationToken ct) => RunAsync(async () =>
    {
        var org = await OrgAsync(ct);
        var schema = editor.SchemaOf(Kind);
        var (doc, problems) = FormBinder.Bind(schema, Posted());
        if (problems.Count > 0) { await ShowAsync(ct, doc, problems); return Page(); }
        await editor.SaveAsync(Kind, org, doc, ct);
        Flash("The draft is saved. Nothing is published. Check it before you submit it.");
        return RedirectToPage(new { kind = Kind, organisationId = org });
    });

    public Task<IActionResult> OnPostValidateAsync(CancellationToken ct) => RunAsync(async () =>
    {
        var org = await OrgAsync(ct);
        var schema = editor.SchemaOf(Kind);
        var (doc, problems) = FormBinder.Bind(schema, Posted());
        if (problems.Count > 0) { await ShowAsync(ct, doc, problems); return Page(); }
        var draft = await editor.ValidateAsync(Kind, org, doc, ct);
        if (draft.Validation is { IsValid: true } ok && ok.Messages.Count == 0)
        {
            Flash("The draft is valid. Read the preview and the impact below, then submit it.");
            return RedirectToPage(new { kind = Kind, organisationId = org });
        }
        await ShowAsync(ct, doc, [], draft.Validation?.Messages);
        return Page();
    });

    public Task<IActionResult> OnPostPublishAsync(CancellationToken ct) => StepUpRunAsync(async () =>
    {
        var org = await OrgAsync(ct);
        var schema = editor.SchemaOf(Kind);
        var (doc, problems) = FormBinder.Bind(schema, Posted());
        if (problems.Count > 0) { await ShowAsync(ct, doc, problems); return Page(); }
        var draft = await editor.ValidateAsync(Kind, org, doc, ct);
        if (draft.Validation is { IsValid: false } report) { await ShowAsync(ct, doc, [], report.Messages); return Page(); }
        var outcome = await configuration.SubmitOrPublishAsync(draft.Id, string.IsNullOrWhiteSpace(Note) ? schema.Title + " changed" : Note!, ct);
        Flash(outcome.Message("Published. Records created from now on follow it; records already in progress keep the release they started under."));
        return outcome.Release is not null ? RedirectToPage(new { kind = Kind, organisationId = org }) : Redirect("/Admin/Reviews?show=mine");
    }, async () => await ShowAsync(CancellationToken.None, null, []));

    public Task<IActionResult> OnPostDiscardAsync(CancellationToken ct) => RunAsync(async () =>
    {
        var org = await OrgAsync(ct);
        if (await configuration.FindOpenDraftAsync(Kind, org, ct) is { } draft) await configuration.DiscardDraftAsync(draft.Id, ct);
        Flash("The draft is discarded. What is published is unchanged.");
        return RedirectToPage(new { kind = Kind, organisationId = org });
    });
}
