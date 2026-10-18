using Ecp.Approvals;
using Ecp.Configuration;
using Ecp.Host.Pages.Admin.Reviews;
using Ecp.Lifecycle.Definitions;
using Ecp.Notifications.Policy;
using Ecp.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Admin.Configuration;

/// <summary>
/// Draft, validate, publish, history and rollback for the configuration documents that drive the lifecycle: the contract workflow, the Variation workflow and the notification policy. This is a plain document
/// editor, not the visual studio; the rules that validate a draft are the same ones that gate publication.
/// </summary>
[Authorize(Policy = "perm:config.draft|config.history.read")]
public sealed class IndexModel(IConfigurationService configuration, IOrganisationStructureService organisations, ConfigurationOptions options) : EcpPageModel
{
    public static readonly IReadOnlyList<(string Kind, string Title, string Help)> Kinds =
    [
        (WorkflowKinds.ContractLifecycle, "Contract workflow", "States, transitions, guards, permissions, locking and the renewal, unlock and approval settings."),
        (WorkflowKinds.Variation, "Variation workflow", "The stages a Variation passes through and the fields it may change."),
        (NotificationPolicyKinds.Policy, "Notification policy", "Who is told about what, the templates, reminder lead times and windows, [[field.evergreen]] and [[field.critical.notice]] rules."),
        (ApprovalsModule.MatrixKind, "Approval matrices", "Who must approve a submission: rows by value, currency, category, classification and more; sequential, parallel, first-response and all-required steps; exceptions; effective dates. A workflow opts in by naming a matrix. Try a change in the simulator before publishing.")
    ];

    [BindProperty(SupportsGet = true)] public string Kind { get; set; } = WorkflowKinds.ContractLifecycle;
    [BindProperty(SupportsGet = true)] public Guid? OrganisationId { get; set; }
    [BindProperty] public string? Definition { get; set; }
    [BindProperty] public string? Note { get; set; }
    [BindProperty] public int? ToRelease { get; set; }
    /// <summary>The raw document can be edited here only where the installation allows it (not in Production); the form editor is always available.</summary>
    public bool RawEditingAllowed => options.AllowRawDocumentEditing;
    public IReadOnlyList<OrganisationSummary> Organisations { get; private set; } = [];
    public DraftDto? Draft { get; private set; }
    public ReleaseDto? Latest { get; private set; }
    public GovernanceInfo? Governance { get; private set; }
    public IReadOnlyList<ReleaseDto> Releases { get; private set; } = [];
    public string? ReleasesDenied { get; private set; }

    public Task<IActionResult> OnGetAsync(CancellationToken ct) => RunAsync(async () => { await LoadAsync(ct); return Page(); });

    private async Task LoadAsync(CancellationToken ct)
    {
        Organisations = await organisations.ListOrganisationsAsync(ct);
        OrganisationId ??= Organisations.FirstOrDefault()?.Id;
        if (OrganisationId is not { } org) return;
        Latest = await configuration.LatestReleaseAsync(Kind, org, ct);
        Draft = await configuration.FindOpenDraftAsync(Kind, org, ct);
        Governance = await configuration.TryGovernanceAsync(Draft?.Id, ct);
        try { Releases = await configuration.ReleasesAsync(Kind, org, ct); }
        catch (Ecp.SharedKernel.ForbiddenException) { ReleasesDenied = "You cannot read the version history."; }
    }

    private Task<IActionResult> Act(Func<Task> action, string flash) => RunAsync(async () => { await action(); Flash(flash); return RedirectToPage(new { kind = Kind, organisationId = OrganisationId }); }, () => LoadAsync(CancellationToken.None));

    public Task<IActionResult> OnPostStartAsync(CancellationToken ct) => Act(async () =>
    {
        // A draft starts from the release in force; if none has been published the pack's reference definition is the starting point.
        var seed = Latest?.ContentJson;
        await configuration.GetOrCreateDraftAsync(Kind, OrganisationId!.Value, seed, ct);
    }, "A draft is open. Edit it, validate it, then publish.");

    private void RequireRawEditing() { if (!options.AllowRawDocumentEditing) throw new Ecp.SharedKernel.DomainException("raw_editing_off", "In this environment a draft is changed only through the form editor."); }

    public Task<IActionResult> OnPostSaveAsync(Guid draftId, CancellationToken ct) => Act(async () => { RequireRawEditing(); await configuration.SaveDraftAsync(draftId, Definition ?? "{}", ct); }, "The draft is saved. Validate it before publishing.");
    public Task<IActionResult> OnPostValidateAsync(Guid draftId, CancellationToken ct) => Act(async () => { RequireRawEditing(); await configuration.SaveDraftAsync(draftId, Definition ?? "{}", ct); var d = await configuration.ValidateAsync(draftId, ct); }, "Validated. See the result below.");
    public Task<IActionResult> OnPostPublishAsync(Guid draftId, CancellationToken ct) => StepUpRunAsync(async () =>
    {
        if (options.AllowRawDocumentEditing) await configuration.SaveDraftAsync(draftId, Definition ?? "{}", ct);
        await configuration.ValidateAsync(draftId, ct);
        var outcome = await configuration.SubmitOrPublishAsync(draftId, Note ?? "", ct);
        Flash(outcome.Message("Published. Agreements created from now on use it; agreements already pinned keep the release they started under."));
        return RedirectToPage(new { kind = Kind, organisationId = OrganisationId });
    }, () => LoadAsync(CancellationToken.None));
    public Task<IActionResult> OnPostDiscardAsync(Guid draftId, CancellationToken ct) => Act(() => configuration.DiscardDraftAsync(draftId, ct), "The draft is discarded.");
    public Task<IActionResult> OnPostRollbackAsync(CancellationToken ct) => StepUpRunAsync(async () =>
    {
        var outcome = await configuration.RequestRollbackAsync(Kind, OrganisationId!.Value, ToRelease ?? 0, Note ?? "", ct);
        Flash(outcome.Release is not null ? "The earlier release is published again as a new release. History is kept." : "Submitted for review. A second person has to review and publish the rollback; nothing changes until they do.");
        return RedirectToPage(new { kind = Kind, organisationId = OrganisationId });
    }, () => LoadAsync(CancellationToken.None));
}
