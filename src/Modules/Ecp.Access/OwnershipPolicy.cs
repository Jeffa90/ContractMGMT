using System.Text.Json;
using Ecp.Access.Domain;
using Ecp.Access.Persistence;
using Ecp.Audit;
using Ecp.Contracts;
using Ecp.Identity;
using Ecp.Identity.Access;
using Ecp.Persistence;
using Ecp.SharedKernel;
using Ecp.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Ecp.Access;

/// <summary>A standing "owns every current and future category" instruction, as shown to an administrator.</summary>
public sealed record OwnershipPolicyInfo(Guid Id, Guid OrganisationId, string OrganisationName, Guid UserId, string PersonName, bool Primary, string Origin, string CreatedBy, DateTime CreatedUtc,
    int CategoriesCovered, bool Open, DateTime? EndedUtc);

public sealed record PolicyOutcome(Guid PolicyId, bool Created, int Assigned, int AlreadyOwned, IReadOnlyList<string> Steps);

/// <summary>
/// "Own all current and future categories in this organisation" as a durable policy (docs/UX_REDESIGN.md section 9, ADR-0040). Category authorisation is unchanged: it still reads ordinary ownership
/// assignments. The policy keeps those assignments in step: setting it gives the person an assignment on every category that exists, and every category created afterwards, by any route, is given one in the
/// same transaction as the category. The policy remembers exactly which assignments it made, so ending it ends those and never ownership that someone gave explicitly.
/// </summary>
public interface ICategoryOwnershipPolicy
{
    Task<IReadOnlyList<OwnershipPolicyInfo>> ListAsync(Guid? organisationId = null, bool includeEnded = false, CancellationToken ct = default);
    /// <summary>The standing policy a person holds, with the organisations it covers (empty if none).</summary>
    Task<IReadOnlyList<OwnershipPolicyInfo>> ForPersonAsync(Guid userId, CancellationToken ct = default);
    /// <summary>Sets the policy for a person (and gives them ownership of today's categories). Needs a recent sign-in and the right to assign ownership in the organisation.</summary>
    Task<PolicyOutcome> SetAsync(Guid organisationId, Guid userId, bool primary, string? reason, CancellationToken ct = default);
    /// <summary>Ends the policy and only the ownership it created.</summary>
    Task EndAsync(Guid policyId, string reason, CancellationToken ct = default);
}

internal sealed record PolicyRequest(Guid OrganisationId, Guid UserId, bool Primary, string Origin, Guid? ApplicationId, string ByLabel, Guid? ByUserId, string? Reason);

/// <summary>The one implementation of the policy, used by the guided services (secured or trusted) and, as an observer, by every route that creates a category.</summary>
internal interface IOwnershipPolicyEngine
{
    Task<PolicyOutcome> SetAsync(PolicyRequest request, bool trusted, CancellationToken ct);
    Task EndForApplicationAsync(Guid applicationId, string reason, string by, CancellationToken ct);
    Task LinkAsync(Guid policyId, Guid applicationId, CancellationToken ct);
}

internal sealed class CategoryOwnershipPolicyService(
    AccessDbContext db, ICategoryAdministration admin, ISecuredCategoryService secured, IAccessService access, IPeopleDirectory people, IUserService users, IOrganisationStructureService structure,
    IUnitOfWork uow, IAuditWriter audit, ITenantContext tenant, IClock clock, ReauthenticationContext reauth) : ICategoryOwnershipPolicy, ICategoryLifecycleObserver, IOwnershipPolicyEngine
{
    private static readonly TimeSpan StepUpWindow = TimeSpan.FromMinutes(10);
    private const string Policy = "policy";

    private static bool Current(CategoryOwnerItem o, DateTime now) => o.EffectiveFromUtc <= now && (o.EffectiveToUtc is null || o.EffectiveToUtc > now);

    private ResourceDescriptor Describe(Guid organisationId) => new("Category", tenant.RequireTenantId(), organisationId, Id: Policy, Dimensions: ScopeDimensions.OrganisationOnly);

    // -- reading -----------------------------------------------------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<OwnershipPolicyInfo>> ListAsync(Guid? organisationId = null, bool includeEnded = false, CancellationToken ct = default)
    {
        var grants = await access.GrantsAsync("users.manage", ct);
        if (grants.Count == 0) throw new ForbiddenException("Seeing ownership policies needs permission to manage people.");
        var rows = await db.CategoryOwnershipPolicies.AsNoTracking().Where(p => (includeEnded || p.EndedUtc == null) && (organisationId == null || p.OrganisationId == organisationId)).OrderBy(p => p.CreatedUtc).ToListAsync(ct);
        rows = rows.Where(p => grants.Any(g => g.OrganisationIds is null || g.OrganisationIds.Contains(p.OrganisationId))).ToList();
        return await ToInfoAsync(rows, ct);
    }

    public async Task<IReadOnlyList<OwnershipPolicyInfo>> ForPersonAsync(Guid userId, CancellationToken ct = default)
    {
        // A person reads their own; anyone else's needs authority over the person.
        if (access.CurrentUserId != userId) await people.GetManageableUserAsync(userId, ct);
        var rows = await db.CategoryOwnershipPolicies.AsNoTracking().Where(p => p.UserId == userId && p.EndedUtc == null).OrderBy(p => p.CreatedUtc).ToListAsync(ct);
        return await ToInfoAsync(rows, ct);
    }

    private async Task<IReadOnlyList<OwnershipPolicyInfo>> ToInfoAsync(IReadOnlyList<CategoryOwnershipPolicy> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return [];
        var names = await people.DisplayNamesAsync(rows.Select(r => r.UserId).Distinct().ToList(), ct);
        var orgs = (await structure.ListOrganisationsAsync(ct)).ToDictionary(o => o.Id, o => o.Name);
        var now = clock.UtcNow;
        var all = await admin.ListAllAsync(ct);
        return rows.Select(p => new OwnershipPolicyInfo(p.Id, p.OrganisationId, orgs.GetValueOrDefault(p.OrganisationId, "Another organisation"), p.UserId, names.GetValueOrDefault(p.UserId, "Unknown person"), p.Primary, p.Origin,
            p.CreatedByLabel, p.CreatedUtc, all.Count(c => c.OrganisationId == p.OrganisationId && c.IsActive && c.Owners.Any(o => o.UserId == p.UserId && Current(o, now))), p.IsOpen, p.EndedUtc)).ToList();
    }

    // -- setting and ending -------------------------------------------------------------------------------------------------------------------------------

    public async Task<PolicyOutcome> SetAsync(Guid organisationId, Guid userId, bool primary, string? reason, CancellationToken ct = default)
    {
        await access.RequireAsync("categories.assign", Describe(organisationId), ct);
        await people.GetManageableUserAsync(userId, ct);
        reauth.Require(clock.UtcNow, StepUpWindow);
        var by = access.CurrentUserId is { } me ? (await users.GetAsync(me, ct))?.DisplayName ?? "an administrator" : "an administrator";
        return await uow.ExecuteAsync(c => SetAsync(new PolicyRequest(organisationId, userId, primary, "OwnershipScreen", null, by, access.CurrentUserId, reason), trusted: false, c), ct);
    }

    public async Task<PolicyOutcome> SetAsync(PolicyRequest r, bool trusted, CancellationToken ct)
    {
        var tenantId = tenant.RequireTenantId();
        if (await structure.GetOrganisationAsync(r.OrganisationId, ct) is null) throw new ResourceNotFoundException();
        if (!trusted) await access.RequireAsync("categories.assign", Describe(r.OrganisationId), ct);   // the same right the ownership screen needs, checked here whichever route got us here
        var policy = await db.CategoryOwnershipPolicies.FirstOrDefaultAsync(p => p.OrganisationId == r.OrganisationId && p.UserId == r.UserId && p.EndedUtc == null, ct);
        var created = policy is null;
        if (policy is null)
        {
            policy = CategoryOwnershipPolicy.Create(tenantId, r.OrganisationId, r.UserId, r.Primary, r.Origin, r.ApplicationId, r.ByLabel, r.ByUserId, clock.UtcNow, r.Reason);
            db.CategoryOwnershipPolicies.Add(policy);
        }
        else if (r.ApplicationId is { } app && policy.ApplicationId is null) policy.Link(app);
        var steps = new List<string>();
        var (assigned, already) = await CoverAsync(policy, (await admin.ListAllAsync(ct)).Where(c => c.OrganisationId == r.OrganisationId && c.IsActive).ToList(), trusted, r.Reason, steps, ct);
        await db.SaveChangesAsync(ct);
        audit.Record(new AuditEntry(created ? "access.ownership_policy.set" : "access.ownership_policy.confirmed", "User", r.UserId.ToString(), OrganisationId: r.OrganisationId, Reason: r.Reason,
            NewValuesJson: JsonSerializer.Serialize(new { policy = policy.Id, primary = policy.Primary, origin = r.Origin, assigned, alreadyOwned = already })));
        return new PolicyOutcome(policy.Id, created, assigned, already, steps);
    }

    /// <summary>Gives the policy's person an assignment on each category they do not already own (directly or through a parent). Ownership that exists is left exactly as it is and is not claimed by the policy.</summary>
    private async Task<(int Assigned, int Already)> CoverAsync(CategoryOwnershipPolicy policy, IReadOnlyList<CategoryItem> scope, bool trusted, string? reason, List<string> steps, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var everything = (await admin.ListAllAsync(ct)).Where(c => c.OrganisationId == policy.OrganisationId).ToDictionary(c => c.Id);
        bool OwnsDirectlyOrThroughParent(CategoryItem c)
        {
            var cur = c; var guard = 0;
            while (cur is not null && guard++ < 50)
            {
                if (cur.Owners.Any(o => o.UserId == policy.UserId && Current(o, now))) return true;
                cur = cur.ParentCategoryId is { } p && everything.TryGetValue(p, out var parent) ? parent : null!;
            }
            return false;
        }
        var ids = new List<Guid>(); var already = 0;
        // Shallow first, so a person given a parent is not also given each child.
        int Depth(CategoryItem c) { var d = 0; var cur = c; while (cur.ParentCategoryId is { } p && everything.TryGetValue(p, out var parent) && d < 50) { cur = parent; d++; } return d; }
        var assignedNow = new HashSet<Guid>();
        foreach (var cat in scope.OrderBy(Depth).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
        {
            var coveredByNew = false; { var cur = cat; var guard = 0; while (cur.ParentCategoryId is { } p && guard++ < 50 && everything.TryGetValue(p, out var parent)) { if (assignedNow.Contains(parent.Id)) { coveredByNew = true; break; } cur = parent; } }
            if (coveredByNew || OwnsDirectlyOrThroughParent(cat)) { already++; continue; }
            var primary = policy.Primary && !cat.Owners.Any(o => o.IsPrimary && Current(o, now));
            var why = reason ?? "Owns every current and future category in the organisation";
            var id = trusted
                ? await admin.AssignOwnerTrustedAsync(cat.Id, policy.UserId, primary, now, null, $"policy:{policy.Id}", why, ct)
                : await secured.AssignOwnerAsync(cat.Id, policy.UserId, primary, why, ct);
            ids.Add(id); assignedNow.Add(cat.Id);
            steps.Add($"Make them the {(primary ? "primary" : "backup")} owner of {cat.Name}.");
        }
        if (ids.Count > 0) policy.Record(ids);
        return (ids.Count, already);
    }

    public async Task EndAsync(Guid policyId, string reason, CancellationToken ct = default)
    {
        var why = Guard.NotBlank(reason, "Reason", 500);
        var policy = await db.CategoryOwnershipPolicies.FirstOrDefaultAsync(p => p.Id == policyId, ct) ?? throw new ResourceNotFoundException();
        await access.RequireAsync("categories.assign", Describe(policy.OrganisationId), ct);
        reauth.Require(clock.UtcNow, StepUpWindow);
        var by = access.CurrentUserId is { } me ? (await users.GetAsync(me, ct))?.DisplayName ?? "an administrator" : "an administrator";
        await uow.ExecuteAsync(async c => { await EndCoreAsync(policy, why, by, trusted: false, c); await db.SaveChangesAsync(c); }, ct);
    }

    public async Task LinkAsync(Guid policyId, Guid applicationId, CancellationToken ct)
    {
        var policy = await db.CategoryOwnershipPolicies.FirstOrDefaultAsync(p => p.Id == policyId, ct);
        if (policy is { ApplicationId: null }) { policy.Link(applicationId); await db.SaveChangesAsync(ct); }
    }

    public async Task EndForApplicationAsync(Guid applicationId, string reason, string by, CancellationToken ct)
    {
        foreach (var policy in await db.CategoryOwnershipPolicies.Where(p => p.ApplicationId == applicationId && p.EndedUtc == null).ToListAsync(ct)) await EndCoreAsync(policy, reason, by, trusted: true, ct);
        await db.SaveChangesAsync(ct);
    }

    private async Task EndCoreAsync(CategoryOwnershipPolicy policy, string reason, string by, bool trusted, CancellationToken ct)
    {
        var ended = 0;
        foreach (var id in policy.AssignmentIds)
        {
            try { if (trusted) await admin.EndOwnerTrustedAsync(id, reason, ct); else await secured.EndOwnershipAsync(id, reason, ct); ended++; }
            catch (DomainException) { /* already ended by someone else: nothing to undo */ }
        }
        policy.End(by, reason, clock.UtcNow);
        audit.Record(new AuditEntry("access.ownership_policy.ended", "User", policy.UserId.ToString(), OrganisationId: policy.OrganisationId, Reason: reason,
            NewValuesJson: JsonSerializer.Serialize(new { policy = policy.Id, ownershipsEnded = ended })));
    }

    // -- recognising a new category -----------------------------------------------------------------------------------------------------------------------

    public async Task OnCategoryActivatedAsync(Guid categoryId, Guid organisationId, CancellationToken ct = default)
    {
        var open = await db.CategoryOwnershipPolicies.Where(p => p.OrganisationId == organisationId && p.EndedUtc == null).ToListAsync(ct);
        if (open.Count == 0) return;
        var category = (await admin.ListAllAsync(ct)).FirstOrDefault(c => c.Id == categoryId && c.OrganisationId == organisationId && c.IsActive);
        if (category is null) return;
        foreach (var policy in open)
        {
            var steps = new List<string>();
            var (assigned, _) = await CoverAsync(policy, [category], trusted: true, "Owns every current and future category in the organisation", steps, ct);
            if (assigned > 0)
                audit.Record(new AuditEntry("access.ownership_policy.applied", "Category", categoryId.ToString(), OrganisationId: organisationId, Reason: "A new category was created",
                    NewValuesJson: JsonSerializer.Serialize(new { policy = policy.Id, userId = policy.UserId })));
        }
        await db.SaveChangesAsync(ct);
    }
}
