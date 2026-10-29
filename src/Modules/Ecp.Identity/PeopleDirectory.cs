using Ecp.Audit;
using Ecp.Identity.Access;
using Ecp.Identity.Domain;
using Ecp.Identity.Persistence;
using Ecp.Persistence;
using Ecp.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ecp.Identity;

public sealed record PersonItem(Guid Id, string DisplayName, string Email, UserStatus Status, IReadOnlyList<string> Roles);

/// <summary>People queries for the signed-in user. Each method authorises on the server; the unsecured <see cref="IUserService"/> is for trusted internal callers.</summary>
public interface IPeopleDirectory
{
    /// <summary>Display names only (no email) for rendering owners and authors. Requires at least one active role assignment.</summary>
    Task<IReadOnlyDictionary<Guid, string>> DisplayNamesAsync(IEnumerable<Guid> ids, CancellationToken ct = default);

    /// <summary>Users the caller may administer (<c>users.manage</c>): everyone for a tenant-wide administrator; otherwise people with a role in the caller's organisations, and invitees not yet assigned.</summary>
    Task<IReadOnlyList<PersonItem>> ManageableUsersAsync(CancellationToken ct = default);
    Task<PersonItem> GetManageableUserAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<AssignmentDto>> AssignmentsOfAsync(Guid userId, bool includeEnded, CancellationToken ct = default);

    /// <summary>People who hold an active CategoryOwner role covering an organisation (requires <c>categories.assign</c>).</summary>
    Task<IReadOnlyList<PersonItem>> OwnerCandidatesAsync(Guid organisationId, CancellationToken ct = default);

    /// <summary>Names (no email) of the active people who hold any active role covering the organisation, for choosing a person in a form. Needs the caller to hold an active role; at most 500.</summary>
    Task<IReadOnlyList<PersonName>> ColleaguesAsync(Guid organisationId, CancellationToken ct = default);

    /// <summary>Among the people the caller may administer, those who sign in through the named provider (for example the demonstration provider, to mark synthetic test people).</summary>
    Task<IReadOnlySet<Guid>> LinkedToProviderAsync(string providerKey, CancellationToken ct = default);
}

public sealed record PersonName(Guid Id, string DisplayName);

/// <summary>Administration of people, authorised by <c>users.manage</c>.</summary>
public interface IUserAdministration
{
    Task<Guid> InviteAsync(string displayName, string email, CancellationToken ct = default);
    Task DisableAsync(Guid userId, string reason, CancellationToken ct = default);
    Task ReactivateAsync(Guid userId, CancellationToken ct = default);
}

internal sealed class PeopleDirectory(IdentityDbContext db, IAccessService access, IClock clock) : IPeopleDirectory
{
    public async Task<IReadOnlyDictionary<Guid, string>> DisplayNamesAsync(IEnumerable<Guid> ids, CancellationToken ct = default)
    {
        var subject = await access.GetSubjectAsync(ct);
        if (subject is null || !subject.Assignments.Any(a => a.IsActiveAt(clock.UtcNow))) return new Dictionary<Guid, string>();
        var wanted = ids.Distinct().ToArray();
        return await db.Users.AsNoTracking().Where(u => wanted.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
    }

    public async Task<IReadOnlyList<PersonItem>> ManageableUsersAsync(CancellationToken ct = default)
    {
        var grants = await access.GrantsAsync("users.manage", ct);
        if (grants.Count == 0) throw new ForbiddenException("You cannot manage users.");
        var now = clock.UtcNow;
        var users = await db.Users.AsNoTracking().OrderBy(u => u.DisplayName).ToListAsync(ct);
        var assignments = (await db.RoleAssignments.AsNoTracking().ToListAsync(ct));
        var everyone = grants.Any(g => g.OrganisationIds is null);
        var orgs = grants.Where(g => g.OrganisationIds is not null).SelectMany(g => g.OrganisationIds!).ToHashSet();
        var result = new List<PersonItem>();
        foreach (var u in users)
        {
            var mine = assignments.Where(a => a.UserId == u.Id).ToList();
            var visible = everyone || mine.Count == 0 || mine.Any(a => a.ScopeLevel == ScopeLevel.Organisation && a.OrganisationIds.Any(orgs.Contains));
            if (visible) result.Add(ToItem(u, mine, now));
        }
        return result;
    }

    public async Task<PersonItem> GetManageableUserAsync(Guid id, CancellationToken ct = default)
        => (await ManageableUsersAsync(ct)).FirstOrDefault(p => p.Id == id) ?? throw new ResourceNotFoundException();

    public async Task<IReadOnlyList<AssignmentDto>> AssignmentsOfAsync(Guid userId, bool includeEnded, CancellationToken ct = default)
    {
        await GetManageableUserAsync(userId, ct); // 404 unless the caller may administer this person
        var now = clock.UtcNow;
        var rows = await db.RoleAssignments.AsNoTracking().Where(a => a.UserId == userId).OrderBy(a => a.CreatedUtc).ToListAsync(ct);
        return rows.Where(a => includeEnded || a.IsActiveAt(now)).Select(a => new AssignmentDto(a.Id, a.UserId, a.RoleCode, a.ViewerKind, a.Scope, a.ApproverGroupKey,
            a.EffectiveFromUtc, a.EffectiveToUtc, a.GrantedByLabel, a.Reason, a.Source, a.IsActiveAt(now))).ToList();
    }

    public async Task<IReadOnlyList<PersonItem>> OwnerCandidatesAsync(Guid organisationId, CancellationToken ct = default)
    {
        var grants = await access.GrantsAsync("categories.assign", ct);
        if (!grants.Any(g => g.OrganisationIds is null || g.OrganisationIds.Contains(organisationId))) throw new ForbiddenException("You cannot assign owners here.");
        var now = clock.UtcNow;
        var owners = (await db.RoleAssignments.AsNoTracking().Where(a => a.RoleCode == RoleCodes.CategoryOwner).ToListAsync(ct))
            .Where(a => a.IsActiveAt(now) && (a.ScopeLevel == ScopeLevel.Tenant || a.OrganisationIds.Contains(organisationId))).Select(a => a.UserId).Distinct().ToList();
        var users = await db.Users.AsNoTracking().Where(u => owners.Contains(u.Id) && u.Status == UserStatus.Active).OrderBy(u => u.DisplayName).ToListAsync(ct);
        return users.Select(u => new PersonItem(u.Id, u.DisplayName, u.Email, u.Status, [RoleCodes.CategoryOwner])).ToList();
    }

    public async Task<IReadOnlyList<PersonName>> ColleaguesAsync(Guid organisationId, CancellationToken ct = default)
    {
        var subject = await access.GetSubjectAsync(ct);
        var now = clock.UtcNow;
        if (subject is null || !subject.Assignments.Any(a => a.IsActiveAt(now))) return [];
        var ids = (await db.RoleAssignments.AsNoTracking().ToListAsync(ct)).Where(a => a.IsActiveAt(now) && (a.ScopeLevel == ScopeLevel.Tenant || a.OrganisationIds.Contains(organisationId))).Select(a => a.UserId).Distinct().ToList();
        return await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id) && u.Status == UserStatus.Active).OrderBy(u => u.DisplayName).Take(500).Select(u => new PersonName(u.Id, u.DisplayName)).ToListAsync(ct);
    }

    public async Task<IReadOnlySet<Guid>> LinkedToProviderAsync(string providerKey, CancellationToken ct = default)
    {
        var manageable = (await ManageableUsersAsync(ct)).Select(p => p.Id).ToHashSet();
        var linked = await db.ExternalIdentities.AsNoTracking().Where(x => x.ProviderKey == providerKey).Select(x => x.UserId).ToListAsync(ct);
        return linked.Where(manageable.Contains).ToHashSet();
    }

    private static PersonItem ToItem(UserProfile u, List<RoleAssignment> assignments, DateTime now)
        => new(u.Id, u.DisplayName, u.Email, u.Status, assignments.Where(a => a.IsActiveAt(now)).Select(a => a.RoleCode + (a.ViewerKind is { } k ? $" ({k})" : "")).Distinct().ToList());
}

internal sealed class UserAdministration(IAccessService access, IUserService users, IPeopleDirectory people) : IUserAdministration
{
    private async Task RequireAsync(CancellationToken ct)
    {
        if ((await access.GrantsAsync("users.manage", ct)).Count == 0) throw new ForbiddenException("You cannot manage users.");
    }

    public async Task<Guid> InviteAsync(string displayName, string email, CancellationToken ct = default)
    {
        await RequireAsync(ct);
        return await users.InviteAsync(displayName, email, ct);
    }

    public async Task DisableAsync(Guid userId, string reason, CancellationToken ct = default)
    {
        await people.GetManageableUserAsync(userId, ct);
        if (userId == access.CurrentUserId) throw new DomainException("self_disable", "You cannot disable your own account.");
        await users.DisableAsync(userId, reason, ct);
    }

    public async Task ReactivateAsync(Guid userId, CancellationToken ct = default)
    {
        await people.GetManageableUserAsync(userId, ct);
        await users.ReactivateAsync(userId, ct);
    }
}
