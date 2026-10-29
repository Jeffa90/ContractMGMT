using Ecp.Access.Persistence;
using Ecp.Contracts;
using Ecp.Identity;
using Ecp.Identity.Access;
using Ecp.Identity.Domain;
using Ecp.SharedKernel;
using Ecp.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Ecp.Access;

/// <summary>Names for the identifiers in a scope, loaded once per report. A caller who may not read a structure sees counts instead of names.</summary>
internal sealed class ScopeNames
{
    public Dictionary<Guid, string> Organisations { get; } = [];
    public Dictionary<Guid, string> BusinessUnits { get; } = [];
    public Dictionary<Guid, string> Divisions { get; } = [];
    public Dictionary<Guid, string> Sites { get; } = [];
    public Dictionary<Guid, string> Categories { get; } = [];

    public static async Task<ScopeNames> LoadAsync(IOrganisationStructureService structure, ICategoryAdministration categories, CancellationToken ct)
    {
        var names = new ScopeNames();
        foreach (var o in await structure.ListOrganisationsAsync(ct))
        {
            names.Organisations[o.Id] = o.Name;
            try
            {
                var s = await structure.GetAsync(o.Id, ct);
                foreach (var b in s.BusinessUnits) names.BusinessUnits[b.Id] = b.Name;
                foreach (var d in s.Divisions) names.Divisions[d.Id] = d.Name;
                foreach (var x in s.Sites) names.Sites[x.Id] = x.Name;
            }
            catch (Exception ex) when (ex is ForbiddenException or ResourceNotFoundException) { /* counts are shown instead */ }
        }
        foreach (var c in await categories.ListAllAsync(ct)) names.Categories[c.Id] = c.Name;
        return names;
    }

    private static string List(IEnumerable<Guid> ids, Dictionary<Guid, string> names, string countWord)
    {
        var all = ids.ToList();
        var known = all.Where(names.ContainsKey).Select(i => names[i]).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        return known.Count == all.Count ? string.Join(", ", known) : $"{all.Count} {countWord}";
    }

    public string Where(ScopeSet scope)
    {
        var parts = new List<string>
        {
            scope.Level switch
            {
                ScopeLevel.Installation => "the whole installation",
                ScopeLevel.Tenant => "all [[organisation|plural|lower]]",
                _ => List(scope.OrganisationIds, Organisations, "[[organisation|plural|lower]]")
            }
        };
        if (scope.NarrowsDivision) parts.Add("[[division|plural|lower]]: " + List(scope.DivisionIds, Divisions, "[[division|plural|lower]]"));
        if (scope.NarrowsBusinessUnit) parts.Add("[[business.unit|plural|lower]]: " + List(scope.BusinessUnitIds, BusinessUnits, "[[business.unit|plural|lower]]"));
        if (scope.NarrowsSite) parts.Add("[[site|plural|lower]]: " + List(scope.SiteIds, Sites, "[[site|plural|lower]]"));
        if (scope.NarrowsCategory) parts.Add("[[category|plural|lower]]: " + List(scope.CategoryIds!, Categories, "[[category|plural|lower]]"));
        return string.Join("; ", parts);
    }

    public string WhereOfGrant(AccessGrant g)
    {
        var parts = new List<string> { g.OrganisationIds is null ? "all [[organisation|plural|lower]]" : List(g.OrganisationIds, Organisations, "[[organisation|plural|lower]]") };
        if (g.DivisionIds.Count > 0) parts.Add("[[division|plural|lower]]: " + List(g.DivisionIds, Divisions, "[[division|plural|lower]]"));
        if (g.BusinessUnitIds.Count > 0) parts.Add("[[business.unit|plural|lower]]: " + List(g.BusinessUnitIds, BusinessUnits, "[[business.unit|plural|lower]]"));
        if (g.SiteIds.Count > 0) parts.Add("[[site|plural|lower]]: " + List(g.SiteIds, Sites, "[[site|plural|lower]]"));
        if (g.CategoryIds is not null) parts.Add("[[category|plural|lower]]: " + List(g.CategoryIds, Categories, "[[category|plural|lower]]"));
        return string.Join("; ", parts);
    }
}

internal sealed class EffectiveAccessService(
    IAccessService access, IAccessDirectory directory, IPeopleDirectory people, IRoleAssignmentService roles, IUserService users, ICategoryAdministration categoryAdmin,
    IOrganisationStructureService structure, IClock clock, AccessDbContext db) : IEffectiveAccess
{
    public async Task<EffectiveAccessReport> ForUserAsync(Guid userId, CancellationToken ct = default)
    {
        var person = await people.GetManageableUserAsync(userId, ct);   // not found unless the caller may administer this person
        var assignments = await people.AssignmentsOfAsync(userId, includeEnded: true, ct);
        return await BuildAsync(userId, person.DisplayName, person.Email, person.Status, assignments, ct);
    }

    public async Task<EffectiveAccessReport> ForMeAsync(CancellationToken ct = default)
    {
        var me = access.CurrentUserId ?? throw new ForbiddenException("Sign in to see your access.");
        var user = await users.GetAsync(me, ct) ?? throw new ResourceNotFoundException();
        var assignments = await roles.ListForUserAsync(me, includeEnded: true, ct);
        return await BuildAsync(me, user.DisplayName, user.Email, user.Status, assignments, ct);
    }

    private static UserStatusView View(UserStatus s) => s switch { UserStatus.Active => UserStatusView.Active, UserStatus.Disabled => UserStatusView.Disabled, _ => UserStatusView.Invited };

    private async Task<EffectiveAccessReport> BuildAsync(Guid userId, string name, string email, UserStatus status, IReadOnlyList<AssignmentDto> assignments, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var scale = await access.ClassificationScaleAsync(ct);
        var names = await ScopeNames.LoadAsync(structure, categoryAdmin, ct);
        var categories = await categoryAdmin.ListAllAsync(ct);
        var owns = categories.Where(c => c.Owners.Any(o => o.UserId == userId && o.EffectiveFromUtc <= now && (o.EffectiveToUtc is null || o.EffectiveToUtc > now)))
            .Select(c => new OwnedCategory(c.Id, c.Name, c.OrganisationId, c.Owners.Any(o => o.UserId == userId && o.IsPrimary))).OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();

        var applications = await db.ProfileApplications.AsNoTracking().Where(a => a.UserId == userId).OrderBy(a => a.AppliedUtc).ToListAsync(ct);
        var profileOfGrant = new Dictionary<Guid, AppliedProfile>();
        foreach (var a in applications.Where(a => a.IsOpen)) foreach (var g in a.GrantIds) profileOfGrant[g] = new AppliedProfile(a.ProfileKey, a.AppliedByLabel, a.AppliedUtc, a.Origin);

        var grantViews = assignments.Select(a => new GrantView(a.Id, RoleToken(a.RoleCode), a.RoleCode + (a.ViewerKind is { } k ? $" ({k})" : ""), names.Where(a.Scope), a.Scope.ClassificationCeiling,
            SourceText(a.Source, profileOfGrant.GetValueOrDefault(a.Id)), profileOfGrant.TryGetValue(a.Id, out var p) ? Label(p.Key) : null, a.EffectiveFromUtc, a.EffectiveToUtc,
            a.IsActive, a.EffectiveFromUtc > now, a.EffectiveToUtc is { } to && to <= now, a.ApproverGroupKey)).ToList();

        var subject = status == UserStatus.Disabled ? null : await directory.LoadSubjectAsync(userId, ct);
        // An invited person has no sign-in yet. The report shows what they will be able to do once active, so the evaluator is asked as if the account were active.
        var evaluated = subject is null ? null : subject with { Status = UserStatus.Active, PermissionAllowlist = null };
        var evaluator = AccessEvaluator.Default;
        var grantsBy = new Dictionary<string, IReadOnlyList<AccessGrant>>(StringComparer.Ordinal);
        foreach (var permission in evaluator.Matrix.Permissions) grantsBy[permission] = evaluated is null ? [] : evaluator.GrantsFor(evaluated, scale, now, permission);

        var abilities = new List<AbilityStatus>();
        foreach (var ability in AbilityCatalogue.All.OrderBy(a => a.Order))
        {
            var perms = AbilityCatalogue.PermissionsOf(ability.Key).ToList();
            var grants = perms.SelectMany(pm => grantsBy.GetValueOrDefault(pm) ?? []).GroupBy(g => (g.AssignmentId, g.Relationship)).Select(g => g.First()).ToList();
            abilities.Add(AbilityOf(ability, grants, owns, categories, names, profileOfGrant, assignments, evaluator.Matrix, perms));
        }

        var highest = assignments.Where(a => a.IsActive).Select(a => a.Scope.ClassificationCeiling).OrderByDescending(scale.Rank).FirstOrDefault() ?? "None";
        var badges = applications.Where(a => a.IsOpen).Select(a => new ProfileBadge(a.ProfileKey, Label(a.ProfileKey), a.Id, a.AppliedUtc, a.AppliedByLabel, a.OrganisationId)).ToList();
        var attention = AttentionOf(status, assignments, owns, categories);
        var groups = assignments.Where(a => a.IsActive && !string.IsNullOrEmpty(a.ApproverGroupKey)).Select(a => a.ApproverGroupKey!).Distinct().OrderBy(x => x).ToList();
        return new EffectiveAccessReport(userId, name, email, View(status), badges, highest, abilities, owns, grantViews, attention, groups);
    }

    private sealed record AppliedProfile(string Key, string By, DateTime AppliedUtc, string Origin);

    private static string Label(string profileKey) => AccessProfiles.Find(profileKey)?.Label ?? profileKey;

    internal static string RoleToken(string roleCode) => TermCatalogue.KeyForRole(roleCode) is { Length: > 0 } key ? $"[[{key}]]" : roleCode;

    private static string SourceText(AssignmentSource source, AppliedProfile? profile) => profile is not null
        ? $"{Label(profile.Key)} profile, given by {profile.By} on {profile.AppliedUtc:yyyy-MM-dd}" + (profile.Origin switch { "TestBootstrap" => " (Test-mode access, testing only)", "Setup" => " (chosen during set-up)", _ => "" })
        : source switch
        {
            AssignmentSource.Manual => "Given directly by an administrator",
            AssignmentSource.GroupMapping => "From an identity group when they sign in",
            AssignmentSource.Onboarding => "Given by organisation onboarding",
            AssignmentSource.Setup => "Given during first-run set-up",
            AssignmentSource.Demo => "Demonstration data",
            AssignmentSource.TestBootstrap => "Test-mode access (testing only)",
            _ => source.ToString()
        };

    private static AbilityStatus AbilityOf(Ability ability, List<AccessGrant> grants, List<OwnedCategory> owns, IReadOnlyList<CategoryItem> categories, ScopeNames names,
        Dictionary<Guid, AppliedProfile> profileOfGrant, IReadOnlyList<AssignmentDto> assignments, PermissionMatrix matrix, List<string> perms)
    {
        if (grants.Count == 0)
        {
            var through = AccessProfiles.All.Where(p => p.RoleCodes.Any(r => perms.Any(pm => matrix.Of(r, pm) != Grant.None))).Select(p => p.Label).ToList();
            return new AbilityStatus(ability.Key, ability.Group, ability.Label, false, "", [], null, through);
        }
        string Because(IEnumerable<AccessGrant> gs) => string.Join("; ", gs.Select(g =>
        {
            var a = assignments.FirstOrDefault(x => x.Id == g.AssignmentId);
            var source = a is null ? "" : SourceText(a.Source, profileOfGrant.GetValueOrDefault(a.Id));
            return $"{RoleToken(g.RoleCode)} ({source})";
        }).Distinct());

        var unconditional = grants.Where(g => g.Relationship == GrantRelationship.None).ToList();
        var owner = grants.Where(g => g.Relationship == GrantRelationship.ActiveCategoryOwnership).ToList();
        var approval = grants.Where(g => g.Relationship == GrantRelationship.AssignedApproval).ToList();
        var other = grants.Where(g => g.Relationship is not (GrantRelationship.None or GrantRelationship.ActiveCategoryOwnership or GrantRelationship.AssignedApproval)).ToList();

        var where = new List<string>(); var because = new List<string>(); var conditions = new List<string>(); var can = false;
        if (unconditional.Count > 0) { can = true; where.AddRange(unconditional.Select(names.WhereOfGrant).Distinct()); because.Add(Because(unconditional)); }
        if (owner.Count > 0)
        {
            var ownedInScope = owns.Where(o => owner.Any(g => g.OrganisationIds is null || g.OrganisationIds.Contains(o.OrganisationId))).ToList();
            if (ownedInScope.Count > 0)
            {
                can = true;
                where.Add("in the [[category|plural|lower]] they own: " + string.Join(", ", ownedInScope.Select(o => o.Name)));
                because.Add(Because(owner) + "; owns the [[category|lower]]");
            }
            else conditions.Add("Their role allows this, but they do not own a [[category|lower]] yet. Owning a [[category|lower]] is what lets them do it.");
        }
        if (approval.Count > 0)
        {
            can = true;
            where.Add("for approver group " + string.Join(", ", approval.Select(g => g.ApproverGroupKey).Where(k => !string.IsNullOrEmpty(k)).Distinct()));
            conditions.Add("Only while a request for their approver group is waiting.");
            because.Add(Because(approval));
        }
        if (other.Count > 0) { can = true; where.Add("where the organisation's policy allows it"); because.Add(Because(other)); }
        var through2 = can ? [] : AccessProfiles.All.Where(p => p.RoleCodes.Any(r => perms.Any(pm => matrix.Of(r, pm) != Grant.None))).Select(p => p.Label).ToList();
        return new AbilityStatus(ability.Key, ability.Group, ability.Label, can, string.Join("; ", where), because, conditions.Count == 0 ? null : string.Join(" ", conditions), through2);
    }

    private static IReadOnlyList<AttentionItem> AttentionOf(UserStatus status, IReadOnlyList<AssignmentDto> assignments, List<OwnedCategory> owns, IReadOnlyList<CategoryItem> categories)
    {
        var items = new List<AttentionItem>();
        if (status == UserStatus.Invited) items.Add(new("invited", "Invited: they become active at their first sign-in. Until then they cannot use the system."));
        if (status == UserStatus.Disabled) items.Add(new("disabled", "This account is disabled, so they have no access."));
        var active = assignments.Where(a => a.IsActive).ToList();
        if (status != UserStatus.Disabled && active.Count == 0) items.Add(new("no-access", "They hold no active role, so they can sign in but see nothing."));
        var ownerRole = active.Where(a => a.RoleCode == RoleCodes.CategoryOwner).ToList();
        if (owns.Count > 0 && ownerRole.Count == 0)
            items.Add(new("owner-without-role", "They own " + string.Join(", ", owns.Take(3).Select(o => o.Name)) + (owns.Count > 3 ? " and more" : "") + " but do not hold a role that lets owners create and edit. Give them the [[contract]] Manager profile."));
        if (ownerRole.Count > 0 && owns.Count == 0 && categories.Any(c => c.IsActive))
            items.Add(new("role-without-ownership", "They hold a role that lets owners work on [[contract|plural|lower]] but do not own any [[category|lower]], so they cannot create or edit anything yet."));
        return items;
    }
}
