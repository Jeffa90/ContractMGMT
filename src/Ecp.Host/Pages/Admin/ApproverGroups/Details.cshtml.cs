using Ecp.Identity;
using Ecp.SharedKernel;
using Ecp.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Admin.ApproverGroups;

/// <summary>
/// One approver group: its settings, its members (with effective dates), what relies on it, and a two-step way to add or end a member: preview what would change (who can approve before and after, and what relies on the group),
/// then confirm. The service refuses to empty a group that something relies on, and to retire one that is in use.
/// </summary>
[Authorize(Policy = "perm:users.manage")]
public sealed class DetailsModel(IApproverGroupService groups, IPeopleDirectory people, IOrganisationStructureService organisations, IAccessService access, IClassificationScaleProvider scales) : EcpPageModel
{
    [BindProperty(SupportsGet = true)] public string Key { get; set; } = "";
    [BindProperty] public string? Description { get; set; }
    [BindProperty] public bool AllowDelegation { get; set; }
    [BindProperty] public Guid UserId { get; set; }
    [BindProperty] public string Role { get; set; } = "primary";
    [BindProperty] public Guid OrganisationId { get; set; }
    [BindProperty] public string? Ceiling { get; set; }
    [BindProperty] public DateTime? From { get; set; }
    [BindProperty] public DateTime? To { get; set; }
    [BindProperty] public string? Reason { get; set; }
    [BindProperty] public Guid AssignmentId { get; set; }
    public ApproverGroupDetail? Group { get; private set; }
    public IReadOnlyList<PersonItem> People { get; private set; } = [];
    public IReadOnlyList<OrganisationSummary> Organisations { get; private set; } = [];
    public IReadOnlyList<string> Levels { get; private set; } = [];
    public MembershipEffect? Effect { get; private set; }
    public string? PreviewKind { get; private set; }
    public string OrganisationName(Guid? id) => id is null ? "-" : Organisations.FirstOrDefault(o => o.Id == id)?.Name ?? "Another organisation";

    public Task<IActionResult> OnGetAsync(CancellationToken ct) => RunAsync(async () => { await LoadAsync(ct); return Page(); });

    private async Task LoadAsync(CancellationToken ct)
    {
        Group = await groups.GetAsync(Key, ct);
        Description ??= Group.Summary.Description;
        if (!Request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase)) AllowDelegation = Group.Summary.AllowDelegation;
        People = (await people.ManageableUsersAsync(ct)).Where(p => p.Status == Ecp.Identity.Domain.UserStatus.Active).OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
        var grants = await access.GrantsAsync("users.manage", ct);
        Organisations = (await organisations.ListOrganisationsAsync(ct)).Where(o => grants.Any(g => g.OrganisationIds is null || g.OrganisationIds.Contains(o.Id))).ToList();
        if (Group.Summary.OrganisationId is { } own) Organisations = Organisations.Where(o => o.Id == own).ToList();
        var scale = await scales.GetAsync(ct);
        Levels = scale.Levels.Where(l => !scale.PrivilegedLevels.Contains(l, StringComparer.OrdinalIgnoreCase)).ToList();
        Ceiling ??= Levels.Contains("Confidential") ? "Confidential" : scale.DefaultLevel;
        if (OrganisationId == Guid.Empty && Organisations.Count == 1) OrganisationId = Organisations[0].Id;
    }

    public Task<IActionResult> OnPostSaveAsync(CancellationToken ct) => RunAsync(async () =>
    {
        await groups.UpdateAsync(Key, Description, AllowDelegation, ct);
        Flash("The group’s settings are saved.");
        return RedirectToPage(new { key = Key });
    }, () => LoadAsync(ct));

    public Task<IActionResult> OnPostPreviewAddAsync(CancellationToken ct) => RunAsync(async () =>
    {
        await LoadAsync(ct);
        PreviewKind = "add";
        Effect = await groups.PreviewAsync(Key, new MembershipChange(true, UserId == Guid.Empty ? null : UserId, Role == "backup", null, From, To), ct);
        return Page();
    }, () => LoadAsync(ct));

    public Task<IActionResult> OnPostAddAsync(CancellationToken ct) => RunAsync(async () =>
    {
        await groups.AddMemberAsync(Key, UserId, Role == "backup", OrganisationId, Ceiling ?? "", From, To, Reason, ct);
        Flash("The person is added to the group.");
        return RedirectToPage(new { key = Key });
    }, () => LoadAsync(ct));

    public Task<IActionResult> OnPostPreviewEndAsync(CancellationToken ct) => RunAsync(async () =>
    {
        await LoadAsync(ct);
        PreviewKind = "end";
        Effect = await groups.PreviewAsync(Key, new MembershipChange(false, null, false, AssignmentId), ct);
        return Page();
    }, () => LoadAsync(ct));

    public Task<IActionResult> OnPostEndAsync(CancellationToken ct) => RunAsync(async () =>
    {
        await groups.EndMemberAsync(Key, AssignmentId, Reason ?? "", ct);
        Flash("The membership has ended.");
        return RedirectToPage(new { key = Key });
    }, () => LoadAsync(ct));

    public Task<IActionResult> OnPostRetireAsync(CancellationToken ct) => RunAsync(async () =>
    {
        await groups.RetireAsync(Key, Reason ?? "", ct);
        Flash("The group is retired.");
        return RedirectToPage(new { key = Key });
    }, () => LoadAsync(ct));

    public Task<IActionResult> OnPostReactivateAsync(CancellationToken ct) => RunAsync(async () =>
    {
        await groups.ReactivateAsync(Key, ct);
        Flash("The group is active again.");
        return RedirectToPage(new { key = Key });
    }, () => LoadAsync(ct));
}
