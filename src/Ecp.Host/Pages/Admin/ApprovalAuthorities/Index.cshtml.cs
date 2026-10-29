using Ecp.Access;
using Ecp.Identity;
using Ecp.SharedKernel;
using Ecp.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Admin.ApprovalAuthorities;

/// <summary>
/// Approval authorities by name: who approves, and who stands in for them. The authorities the organisation's workflows already name are listed with nobody in them; the administrator only picks people. The
/// stable machine key is shown under Advanced and never has to be typed. Changes are reviewed in words first and need a tick to confirm.
/// </summary>
[Authorize(Policy = "perm:users.manage")]
public sealed class IndexModel(IApprovalAuthorities authorities, IOrganisationStructureService organisations, IAccessService access) : EcpPageModel
{
    [BindProperty(SupportsGet = true)] public Guid? OrganisationId { get; set; }
    [BindProperty] public string? Key { get; set; }
    [BindProperty] public string? DisplayName { get; set; }
    [BindProperty] public List<Guid> MemberIds { get; set; } = [];
    [BindProperty] public List<Guid> BackupIds { get; set; } = [];
    [BindProperty] public string? Reason { get; set; }
    [BindProperty] public bool Confirm { get; set; }

    public IReadOnlyList<OrganisationSummary> Organisations { get; private set; } = [];
    public AuthoritiesView? View { get; private set; }
    public AuthorityPlan? Plan { get; private set; }
    /// <summary>The authority being edited, if a change is open.</summary>
    public string? Editing { get; private set; }
    public bool Creating { get; private set; }
    public bool ShowKeys { get; private set; }

    public Task<IActionResult> OnGetAsync(string? edit, bool? create, bool? advanced, CancellationToken ct) => RunAsync(async () =>
    {
        await LoadAsync(ct);
        Editing = edit; Creating = create == true; ShowKeys = advanced == true;
        if (edit is not null && View?.Authorities.FirstOrDefault(a => a.Key == edit) is { } a) { MemberIds = a.Members.Select(m => m.UserId).ToList(); BackupIds = a.Backups.Select(m => m.UserId).ToList(); Key = a.Key; }
        return Page();
    });

    private AuthorityChange Change() => new(OrganisationId ?? Guid.Empty, string.IsNullOrWhiteSpace(Key) ? null : Key, DisplayName, MemberIds, BackupIds, string.IsNullOrWhiteSpace(Reason) ? null : Reason);

    public Task<IActionResult> OnPostReviewAsync(CancellationToken ct) => RunAsync(async () =>
    {
        await LoadAsync(ct);
        Editing = Key; Creating = string.IsNullOrWhiteSpace(Key);
        Plan = await authorities.PreviewAsync(Change(), ct);
        ModelState.Clear();
        return Page();
    }, () => LoadAsync(ct));

    public async Task<IActionResult> OnPostSaveAsync(CancellationToken ct)
    {
        if (!Confirm)
        {
            await LoadAsync(ct);
            Editing = Key; Creating = string.IsNullOrWhiteSpace(Key);
            Plan = await authorities.PreviewAsync(Change(), ct);
            ModelState.AddModelError("Confirm", "Read what will change and tick the box to confirm.");
            return Page();
        }
        return await StepUpRunAsync(async () =>
        {
            var plan = await authorities.SaveAsync(Change(), ct);
            Flash($"{plan.DisplayName} is saved. " + string.Join(" ", plan.Steps));
            return RedirectToPage(new { organisationId = OrganisationId });
        }, () => LoadAsync(ct));
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        var grants = await access.GrantsAsync("users.manage", ct);
        Organisations = (await organisations.ListOrganisationsAsync(ct)).Where(o => grants.Any(g => g.OrganisationIds is null || g.OrganisationIds.Contains(o.Id))).ToList();
        OrganisationId ??= Organisations.FirstOrDefault()?.Id;
        if (OrganisationId is { } org)
        {
            if (Organisations.All(o => o.Id != org)) throw new ResourceNotFoundException();
            View = await authorities.ViewAsync(org, ct);
        }
    }
}
