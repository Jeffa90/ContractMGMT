using Ecp.Contracts;
using Ecp.Identity;
using Ecp.Identity.Domain;
using Ecp.Persistence;
using Ecp.SharedKernel;

namespace Ecp.Access;

/// <summary>A person who could own categories, and whether they already hold the role that lets owners act.</summary>
public sealed record OwnerChoice(Guid UserId, string DisplayName, bool HoldsRole);

public sealed record OwnerRef(Guid AssignmentId, Guid UserId, string Name, bool HoldsRole);

/// <summary>One row of the ownership board: a category, who owns it, and anything that needs attention.</summary>
public sealed record OwnershipRow(Guid CategoryId, Guid OrganisationId, string Name, string Code, int Depth, Guid? ParentId, string? ParentName, bool IsActive,
    IReadOnlyList<OwnerRef> Primaries, IReadOnlyList<OwnerRef> Backups, IReadOnlyList<string> Attention)
{
    public bool NeedsOwner => IsActive && Primaries.Count == 0;
}

public sealed record OwnershipBoard(IReadOnlyList<OwnershipRow> Rows, int NeedsOwner, int NeedsAttention);

public sealed record OwnershipOutcome(int Assigned, int Replaced, int Skipped, bool RoleAdded, IReadOnlyList<string> Notes);

/// <summary>What assigning would do, said before it is done.</summary>
public sealed record OwnershipPlan(int Categories, int AlreadyOwned, int WillReplace, bool WillAddRole, string PersonName);

/// <summary>
/// Ownership of categories in a form (docs/UX_REDESIGN.md section 9): a primary and a backup owner per category, in plain choices, from everyone the administrator may manage. Choosing someone who lacks the role
/// that lets owners act gives them it in the same save. Every step is the real secured service, so the delegation and permission rules apply; a refusal rolls the whole save back.
/// </summary>
public interface ICategoryOwnership
{
    Task<OwnershipBoard> BoardAsync(string? search = null, bool needsOwnerOnly = false, CancellationToken ct = default);
    Task<IReadOnlyList<OwnerChoice>> PeopleAsync(Guid organisationId, CancellationToken ct = default);
    Task<OwnershipPlan> PlanAsync(Guid organisationId, IReadOnlyList<Guid> categoryIds, Guid userId, bool primary, CancellationToken ct = default);
    /// <summary>Makes the person the primary or backup owner of each category (and gives them the owner role first if they lack it). A new primary replaces the current one, who is ended with a reason.</summary>
    Task<OwnershipOutcome> SetOwnerAsync(Guid organisationId, IReadOnlyList<Guid> categoryIds, Guid userId, bool primary, string? reason, CancellationToken ct = default);
    /// <summary>Every top-level active category of the organisation (owning one covers its sub-categories), for one person.</summary>
    Task<OwnershipOutcome> AssignAllAsync(Guid organisationId, Guid userId, bool primary, string? reason, CancellationToken ct = default);
    Task EndAsync(Guid assignmentId, string reason, CancellationToken ct = default);
}

internal sealed class CategoryOwnershipService(
    ISecuredCategoryService categories, IPeopleDirectory people, IRoleAssignmentService roles, IAccessProfileService profiles, IUnitOfWork uow,
    IUserService users, IClock clock, ReauthenticationContext reauth) : ICategoryOwnership
{
    private static readonly TimeSpan StepUpWindow = TimeSpan.FromMinutes(10);

    private static bool HoldsOwnerRole(IEnumerable<AssignmentDto> assignments, Guid organisationId, DateTime now)
        => assignments.Any(a => a.IsActive && a.RoleCode == RoleCodes.CategoryOwner && a.EffectiveFromUtc <= now && (a.EffectiveToUtc is null || a.EffectiveToUtc > now)
            && (a.Scope.Level == ScopeLevel.Tenant || a.Scope.OrganisationIds.Contains(organisationId)));

    public async Task<OwnershipBoard> BoardAsync(string? search = null, bool needsOwnerOnly = false, CancellationToken ct = default)
    {
        var all = await categories.ListAsync(ct);
        var now = clock.UtcNow;
        var ownerIds = all.SelectMany(c => c.Owners.Where(o => o.EffectiveFromUtc <= now && (o.EffectiveToUtc is null || o.EffectiveToUtc > now)).Select(o => o.UserId)).Distinct().ToList();
        var names = await people.DisplayNamesAsync(ownerIds, ct);
        var everyone = (await roles.ListAsync(activeOnly: true, ct)).GroupBy(x => x.UserId).ToDictionary(g => g.Key, g => (IReadOnlyList<AssignmentDto>)g.ToList());
        bool Holds(Guid user, Guid org) => everyone.TryGetValue(user, out var list) && HoldsOwnerRole(list, org, now);
        var byId = all.ToDictionary(c => c.Id);
        int Depth(CategoryItem c) { var d = 0; var cur = c; while (cur.ParentCategoryId is { } p && byId.TryGetValue(p, out var parent) && d < 20) { cur = parent; d++; } return d; }
        var ordered = new List<CategoryItem>();
        void Walk(CategoryItem c) { ordered.Add(c); foreach (var child in all.Where(x => x.ParentCategoryId == c.Id).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)) Walk(child); }
        foreach (var root in all.Where(c => c.ParentCategoryId is null || !byId.ContainsKey(c.ParentCategoryId.Value)).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)) Walk(root);

        var rows = new List<OwnershipRow>();
        foreach (var c in ordered)
        {
            var current = c.Owners.Where(o => o.EffectiveFromUtc <= now && (o.EffectiveToUtc is null || o.EffectiveToUtc > now)).ToList();
            var primaries = new List<OwnerRef>(); var backups = new List<OwnerRef>(); var attention = new List<string>();
            foreach (var o in current)
            {
                var r = new OwnerRef(o.AssignmentId, o.UserId, names.GetValueOrDefault(o.UserId, "Unknown person"), Holds(o.UserId, c.OrganisationId));
                (o.IsPrimary ? primaries : backups).Add(r);
                if (!r.HoldsRole) attention.Add($"{r.Name} owns this but does not hold the role that lets owners create and edit.");
            }
            if (primaries.Count > 1) attention.Add("There is more than one primary owner. Keep one.");
            rows.Add(new OwnershipRow(c.Id, c.OrganisationId, c.Name, c.Code, Depth(c), c.ParentCategoryId, c.ParentCategoryId is { } pid && byId.TryGetValue(pid, out var par) ? par.Name : null, c.IsActive, primaries, backups, attention));
        }
        var needs = rows.Count(r => r.NeedsOwner);
        var attn = rows.Count(r => r.Attention.Count > 0);
        IEnumerable<OwnershipRow> shown = rows;
        if (!string.IsNullOrWhiteSpace(search)) shown = shown.Where(r => r.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || r.Code.Contains(search, StringComparison.OrdinalIgnoreCase));
        if (needsOwnerOnly) shown = shown.Where(r => r.NeedsOwner || r.Attention.Count > 0);
        return new OwnershipBoard(shown.ToList(), needs, attn);
    }

    public async Task<IReadOnlyList<OwnerChoice>> PeopleAsync(Guid organisationId, CancellationToken ct = default)
    {
        var now = clock.UtcNow;
        var everyone = (await roles.ListAsync(activeOnly: true, ct)).GroupBy(x => x.UserId).ToDictionary(g => g.Key, g => (IReadOnlyList<AssignmentDto>)g.ToList());
        var result = new List<OwnerChoice>();
        foreach (var p in await people.ManageableUsersAsync(ct))
        {
            if (p.Status == UserStatus.Disabled) continue;
            result.Add(new OwnerChoice(p.Id, p.DisplayName, everyone.TryGetValue(p.Id, out var list) && HoldsOwnerRole(list, organisationId, now)));
        }
        return result.OrderBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private async Task<(IReadOnlyList<CategoryItem> Targets, UserDto Person, bool HoldsRole)> ResolveAsync(Guid organisationId, IReadOnlyList<Guid> categoryIds, Guid userId, CancellationToken ct)
    {
        var person = await users.GetAsync(userId, ct) ?? throw new ResourceNotFoundException();
        await people.GetManageableUserAsync(userId, ct);   // not found unless the administrator may manage this person
        var targets = (await categories.ListAsync(ct)).Where(c => c.IsActive && c.OrganisationId == organisationId && categoryIds.Contains(c.Id)).ToList();
        if (targets.Count == 0) throw new DomainException("ownership_required", "Choose at least one [[category|lower]].");
        var held = HoldsOwnerRole(await roles.ListForUserAsync(userId, includeEnded: false, ct), organisationId, clock.UtcNow);
        return (targets, person, held);
    }

    public async Task<OwnershipPlan> PlanAsync(Guid organisationId, IReadOnlyList<Guid> categoryIds, Guid userId, bool primary, CancellationToken ct = default)
    {
        var (targets, person, held) = await ResolveAsync(organisationId, categoryIds, userId, ct);
        var now = clock.UtcNow;
        bool Current(CategoryOwnerItem o) => o.EffectiveFromUtc <= now && (o.EffectiveToUtc is null || o.EffectiveToUtc > now);
        var already = targets.Count(c => c.Owners.Any(o => Current(o) && o.UserId == userId && o.IsPrimary == primary));
        var replace = primary ? targets.Count(c => c.Owners.Any(o => Current(o) && o.IsPrimary && o.UserId != userId)) : 0;
        return new OwnershipPlan(targets.Count, already, replace, !held, person.DisplayName);
    }

    public async Task<OwnershipOutcome> SetOwnerAsync(Guid organisationId, IReadOnlyList<Guid> categoryIds, Guid userId, bool primary, string? reason, CancellationToken ct = default)
    {
        var (targets, person, held) = await ResolveAsync(organisationId, categoryIds, userId, ct);
        reauth.Require(clock.UtcNow, StepUpWindow);
        return await uow.ExecuteAsync(async c =>
        {
            var notes = new List<string>(); int assigned = 0, replaced = 0, skipped = 0; var roleAdded = false;
            if (!held)
            {
                // The role that lets an owner act is given in the same save, by the same guided service, so ownership never lands on a person who cannot use it.
                await profiles.ApplyAsync(new AccessChange(userId, organisationId, [AccessProfiles.ContractManager], Reason: reason ?? "Given when made an owner"), c);
                roleAdded = true;
                notes.Add($"{person.DisplayName} was also given the {AccessProfiles.Find(AccessProfiles.ContractManager)!.Label} profile so they can act as an owner.");
            }
            var now = clock.UtcNow;
            bool Current(CategoryOwnerItem o) => o.EffectiveFromUtc <= now && (o.EffectiveToUtc is null || o.EffectiveToUtc > now);
            foreach (var cat in targets)
            {
                var mine = cat.Owners.Where(o => Current(o) && o.UserId == userId).ToList();
                if (mine.Any(o => o.IsPrimary == primary)) { skipped++; continue; }
                if (!primary && mine.Any(o => o.IsPrimary)) throw new DomainException("backup_is_primary", $"{person.DisplayName} is already the primary owner of {cat.Name}. A backup must be someone else.");
                foreach (var m in mine) await categories.EndOwnershipAsync(m.AssignmentId, $"Changed to {(primary ? "primary" : "backup")} owner", c);   // a person is either primary or backup, never both
                if (primary)
                    foreach (var old in cat.Owners.Where(o => Current(o) && o.IsPrimary && o.UserId != userId))
                    {
                        await categories.EndOwnershipAsync(old.AssignmentId, $"Replaced as primary owner by {person.DisplayName}", c);
                        replaced++;
                    }
                await categories.AssignOwnerAsync(cat.Id, userId, primary, reason ?? $"Made {(primary ? "primary" : "backup")} owner from the ownership screen", c);
                assigned++;
            }
            return new OwnershipOutcome(assigned, replaced, skipped, roleAdded, notes);
        }, ct);
    }

    public async Task<OwnershipOutcome> AssignAllAsync(Guid organisationId, Guid userId, bool primary, string? reason, CancellationToken ct = default)
    {
        var all = (await categories.ListAsync(ct)).Where(c => c.IsActive && c.OrganisationId == organisationId).ToList();
        var top = all.Where(c => c.ParentCategoryId is null || all.All(p => p.Id != c.ParentCategoryId)).Select(c => c.Id).ToList();
        if (top.Count == 0) throw new DomainException("no_categories", "There are no [[category|plural|lower]] yet.");
        return await SetOwnerAsync(organisationId, top, userId, primary, reason, ct);
    }

    public async Task EndAsync(Guid assignmentId, string reason, CancellationToken ct = default)
        => await categories.EndOwnershipAsync(assignmentId, Guard.NotBlank(reason, "Reason", 1000), ct);
}
