using Ecp.Access;
using Ecp.Contracts;
using Ecp.Host.Security;
using Ecp.Identity;
using Ecp.SharedKernel;
using Ecp.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Admin.People;

/// <summary>Everything the wizard collects. The page carries it forward in hidden fields; nothing is trusted until the service has checked it at the last step.</summary>
public sealed class WizardInput
{
    public Guid? UserId { get; set; }
    public string? DisplayName { get; set; }
    public string? Email { get; set; }
    public bool AllowDemo { get; set; }
    public Guid? OrganisationId { get; set; }
    public List<string> Profiles { get; set; } = [];
    public string Scope { get; set; } = nameof(ScopeChoice.WholeOrganisation);
    public List<Guid> ScopeIds { get; set; } = [];
    public string? Ceiling { get; set; }
    public bool Privileged { get; set; }
    public string Ownership { get; set; } = nameof(OwnershipChoice.None);
    public List<Guid> OwnedIds { get; set; } = [];
    public string? ApproverGroup { get; set; }
    public string? Reason { get; set; }
    /// <summary>The administrator has read the review and confirms the access is to be given.</summary>
    public bool Confirm { get; set; }
    /// <summary>Show role details on the review (Advanced).</summary>
    public bool Advanced { get; set; }
}

public sealed record Choice(Guid Id, string Name);

/// <summary>
/// The Add person / Change access wizard (docs/UX_REDESIGN.md section 6): one question per step, defaults and a plain-English review. It asks the Access services to preview and then to apply,
/// so the review shows what saving will do and every delegation, self-grant and last-administrator rule is the real one.
/// </summary>
[Authorize(Policy = "perm:users.manage")]
public sealed class AccessModel(
    IPersonAccessService people, IAccessProfileService profiles, IEffectiveAccess effective, IOrganisationStructureService organisations, ICategoryAdministration categories,
    IApprovalAuthorities authorities, IAccessService access, DemoIdentityService demo) : EcpPageModel
{
    [BindProperty(SupportsGet = true)] public WizardInput Input { get; set; } = new();
    [BindProperty(SupportsGet = true)] public int Step { get; set; } = 1;

    public const int Last = 7;
    public bool EditMode => Input.UserId is not null;
    public string? PersonName { get; private set; }
    public IReadOnlyList<OrganisationSummary> Organisations { get; private set; } = [];
    public IReadOnlyList<ProfileOption> Options { get; private set; } = [];
    public IReadOnlyList<Choice> Divisions { get; private set; } = [];
    public IReadOnlyList<Choice> BusinessUnits { get; private set; } = [];
    public IReadOnlyList<Choice> Sites { get; private set; } = [];
    public IReadOnlyList<Choice> Categories { get; private set; } = [];
    public IReadOnlyList<string> Levels { get; private set; } = [];
    public int HighLevelRank { get; private set; }
    public IReadOnlyList<ApprovalAuthority> ApproverGroups { get; private set; } = [];
    public AccessPlan? Plan { get; private set; }
    public AccessReview? Review { get; private set; }
    public EffectiveAccessReport? Current { get; private set; }
    public bool DemoAvailable => people.DemonstrationSignInAllowed && demo.IsEnabled;
    public string DefaultCeiling { get; private set; } = "";

    public IEnumerable<AccessProfile> Chosen => Input.Profiles.Select(AccessProfiles.Find).OfType<AccessProfile>();
    public bool NeedsOwnership => Chosen.Any(p => p.OwnsCategories);
    public bool NeedsApproverGroup => Chosen.Any(p => p.NeedsApproverGroup);
    public bool NarrowingAllowed => Chosen.Any(p => !p.WholeOrganisationOnly);

    public static readonly string[] StepNames = ["Person", "What will they do?", "Where?", "[[category|plural]] they own", "Classification", "Approvals", "Review"];

    public bool Applies(int step) => step switch
    {
        1 or 2 or 7 => true,
        3 => NarrowingAllowed,
        4 => NeedsOwnership,
        5 => Chosen.Any(),
        6 => NeedsApproverGroup,
        _ => false
    };

    private int Next(int from) { for (var s = from + 1; s <= Last; s++) if (Applies(s)) return s; return Last; }
    private int Previous(int from) { for (var s = from - 1; s >= 1; s--) if (Applies(s)) return s; return 1; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct) => await RunAsync(async () =>
    {
        // Returning from a fresh sign-in (Step 7 in the address) rebuilds the review from what was in the address.
        await LoadAsync(ct);
        if (Step < 1 || Step > Last) Step = 1;
        if (Step == Last) await BuildReviewAsync(ct);
        return Page();
    });

    public async Task<IActionResult> OnPostNextAsync(CancellationToken ct) => await RunAsync(async () =>
    {
        await LoadAsync(ct);
        if (!ValidateStep(Step)) return Page();
        Step = Next(Step);
        if (Step == 5 && string.IsNullOrWhiteSpace(Input.Ceiling)) Input.Ceiling = DefaultCeiling;
        if (Step == Last) await BuildReviewAsync(ct);
        return Page();
    }, reload: () => LoadAsync(ct));

    public async Task<IActionResult> OnPostBackAsync(CancellationToken ct) => await RunAsync(async () =>
    {
        await LoadAsync(ct);
        Step = Previous(Step);
        return Page();
    });

    public async Task<IActionResult> OnPostSaveAsync(CancellationToken ct)
    {
        if (!Input.Confirm)
        {
            // Nothing is given until the administrator has read the review and said so.
            ModelState.AddModelError("Input.Confirm", "Read the review and tick the box to confirm before access is given.");
            return await RunAsync(async () => { await LoadAsync(ct); Step = Last; await BuildReviewAsync(ct); return Page(); }, reload: async () => { await LoadAsync(ct); Step = Last; await BuildReviewAsync(ct); });
        }
        // Changing roles, scope, ceiling, ownership or approver membership needs a recent sign-in. The answers travel in the address so the review comes back after signing in again.
        var authorisation = HttpContext.RequestServices.GetRequiredService<IAuthorizationService>();
        if (!(await authorisation.AuthorizeAsync(User, Policies.RecentAuthentication)).Succeeded)
            return Redirect("/Reauth?returnUrl=" + Uri.EscapeDataString(ResumeAddress()));
        return await RunAsync(async () =>
        {
            await LoadAsync(ct);
            var change = Change(Input.UserId ?? Guid.Empty);
            if (EditMode)
            {
                var result = await profiles.ApplyAsync(change, ct);
                Flash(Summarise(result, PersonName ?? "The person"));
                return Redirect("/Admin/People/Person/" + Input.UserId);
            }
            var added = await people.AddAsync(new NewPerson(Input.DisplayName ?? "", Input.Email ?? "", Input.AllowDemo), change, ct);
            Flash(added.IsTestPerson
                ? $"{Input.DisplayName} is added as a test person. They can now be chosen on the sign-in page under Demonstration sign-in."
                : $"{Input.DisplayName} is invited. They can sign in once your identity provider vouches for their email address. " + Summarise(added.Access, Input.DisplayName ?? "The person"));
            return Redirect("/Admin/People/Person/" + added.UserId);
        }, reload: async () => { await LoadAsync(ct); Step = Last; await BuildReviewAsync(ct); });
    }

    private static string Summarise(AccessResult? r, string name)
        => r is null || (r.GrantsCreated == 0 && r.OwnershipsCreated == 0) ? $"{name}'s access was already as chosen; nothing changed."
            : $"Access saved for {name}: {r.GrantsCreated} access grant(s) and {r.OwnershipsCreated} ownership(s) added." + (r.OwnershipPending ? " " + string.Join(" ", r.Notes) : "");

    private string ResumeAddress()
    {
        var parts = new List<string> { "Step=" + Last };
        void Add(string key, object? value) { if (value is not null && value.ToString() is { Length: > 0 } s) parts.Add($"Input.{key}=" + Uri.EscapeDataString(s)); }
        Add("UserId", Input.UserId); Add("DisplayName", Input.DisplayName); Add("Email", Input.Email); if (Input.AllowDemo) Add("AllowDemo", "true");
        Add("OrganisationId", Input.OrganisationId); Add("Scope", Input.Scope); Add("Ceiling", Input.Ceiling); if (Input.Privileged) Add("Privileged", "true");
        Add("Ownership", Input.Ownership); Add("ApproverGroup", Input.ApproverGroup); Add("Reason", Input.Reason);
        foreach (var p in Input.Profiles) Add("Profiles", p);
        foreach (var i in Input.ScopeIds) Add("ScopeIds", i);
        foreach (var i in Input.OwnedIds) Add("OwnedIds", i);
        return "/Admin/People/Access?" + string.Join("&", parts);
    }

    private AccessChange Change(Guid userId)
    {
        _ = Enum.TryParse<ScopeChoice>(Input.Scope, out var scope);
        _ = Enum.TryParse<OwnershipChoice>(Input.Ownership, out var ownership);
        var narrow = NarrowingAllowed && scope != ScopeChoice.WholeOrganisation;
        return new AccessChange(userId, Input.OrganisationId ?? Guid.Empty, Input.Profiles, narrow ? scope : ScopeChoice.WholeOrganisation, narrow ? Input.ScopeIds : [], Input.Ceiling,
            Input.Privileged, NeedsOwnership ? ownership : OwnershipChoice.None, NeedsOwnership && ownership == OwnershipChoice.Selected ? Input.OwnedIds : [],
            NeedsApproverGroup ? Input.ApproverGroup : null, string.IsNullOrWhiteSpace(Input.Reason) ? null : Input.Reason.Trim());
    }

    private bool ValidateStep(int step)
    {
        switch (step)
        {
            case 1:
                if (!EditMode)
                {
                    if (string.IsNullOrWhiteSpace(Input.DisplayName)) ModelState.AddModelError("Input.DisplayName", "Enter the person's name.");
                    if (string.IsNullOrWhiteSpace(Input.Email) || !Input.Email.Contains('@')) ModelState.AddModelError("Input.Email", "Enter the person's email address.");
                }
                if (Input.OrganisationId is null) ModelState.AddModelError("Input.OrganisationId", "Choose the [[organisation|lower]] they will work in.");
                break;
            case 2:
                if (Input.Profiles.Count == 0 && EditMode) ModelState.AddModelError("Input.Profiles", "Choose at least one thing this person will do.");
                break;
            case 3:
                if (NarrowingAllowed && Input.Scope != nameof(ScopeChoice.WholeOrganisation) && Input.ScopeIds.Count == 0) ModelState.AddModelError("Input.ScopeIds", "Choose at least one place, or choose the whole [[organisation|lower]].");
                break;
            case 4:
                if (Input.Ownership == nameof(OwnershipChoice.Selected) && Input.OwnedIds.Count == 0) ModelState.AddModelError("Input.OwnedIds", "Choose at least one [[category|lower]], or choose All or None.");
                break;
            case 6:
                if (string.IsNullOrWhiteSpace(Input.ApproverGroup)) ModelState.AddModelError("Input.ApproverGroup", "Choose the approver group.");
                break;
        }
        return ModelState.IsValid;
    }

    private async Task BuildReviewAsync(CancellationToken ct)
    {
        if (Input.OrganisationId is null) { Plan = new AccessPlan(false, [], ["Choose the [[organisation|lower]] first."], null, null); return; }
        if (Input.Profiles.Count == 0 && EditMode) { Plan = new AccessPlan(false, [], ["Choose what this person will do."], null, null); return; }
        Plan = EditMode
            ? await profiles.PreviewAsync(Change(Input.UserId!.Value), ct)
            : await people.PreviewNewAsync(new NewPerson(Input.DisplayName ?? "", Input.Email ?? "", Input.AllowDemo), Change(Guid.Empty), ct);
        if (Plan is { CanApply: true, After: { } after })
        {
            _ = Enum.TryParse<OwnershipChoice>(Input.Ownership, out var ownership);
            var scale = await access.ClassificationScaleAsync(ct);
            var org = Organisations.FirstOrDefault(o => o.Id == Input.OrganisationId)?.Name ?? "";
            Review = AccessReviewBuilder.Build(new ReviewInput(after.DisplayName, org, Input.Profiles, NeedsOwnership ? ownership : OwnershipChoice.None, Input.Ceiling,
                NeedsApproverGroup && !string.IsNullOrWhiteSpace(Input.ApproverGroup) ? [Input.ApproverGroup] : null), after, scale, Plan.Steps, Input.Advanced);
        }
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        var grants = await access.GrantsAsync("users.manage", ct);
        Organisations = (await organisations.ListOrganisationsAsync(ct)).Where(o => grants.Any(g => g.OrganisationIds is null || g.OrganisationIds.Contains(o.Id))).ToList();
        if (Input.OrganisationId is null && Organisations.Count == 1) Input.OrganisationId = Organisations[0].Id;
        if (Input.OrganisationId is { } org && Organisations.All(o => o.Id != org)) throw new ResourceNotFoundException();
        if (EditMode)
        {
            Current = await effective.ForUserAsync(Input.UserId!.Value, ct);
            PersonName = Current.DisplayName;
            if (Step == 1 && Organisations.Count == 1 && Input.Profiles.Count == 0) Step = 2;
        }
        var scale = await access.ClassificationScaleAsync(ct);
        Levels = scale.Levels; HighLevelRank = scale.HighCeilingThreshold;
        if (Input.OrganisationId is { } o)
        {
            Options = await profiles.ProfilesForAsync(o, ct);
            var structure = await organisations.GetAsync(o, ct);
            Divisions = structure.Divisions.Where(d => d.IsActive).Select(d => new Choice(d.Id, d.Name)).ToList();
            BusinessUnits = structure.BusinessUnits.Where(b => b.IsActive).Select(b => new Choice(b.Id, b.Name)).ToList();
            Sites = structure.Sites.Where(s => s.IsActive).Select(s => new Choice(s.Id, s.Name)).ToList();
            Categories = (await categories.ListAllAsync(ct)).Where(c => c.OrganisationId == o && c.IsActive).OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).Select(c => new Choice(c.Id, c.Name)).ToList();
            ApproverGroups = (await authorities.ViewAsync(o, ct)).Authorities;
            var top = Chosen.Select(p => AccessProfiles.DefaultCeiling(p, scale)).OrderByDescending(scale.Rank).FirstOrDefault();
            DefaultCeiling = top ?? scale.DefaultLevel;
        }
    }
}
