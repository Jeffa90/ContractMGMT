using Ecp.Contracts.Persistence;
using Ecp.Identity;
using Ecp.Identity.Access;
using Ecp.Identity.Domain;
using Ecp.SharedKernel;
using Ecp.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Ecp.Contracts;

/// <summary>The attributes of an imaginary contract used to ask "what could this person do with a record like this?".</summary>
public sealed record SampleRecord(Guid OrganisationId, Guid? CategoryId, Guid? BusinessUnitId, Guid? SiteId, string Classification, bool Locked);

public sealed record SimulationResult(string Permission, string Description, AccessDecision Decision);

/// <summary>
/// Access simulation: runs the real evaluator against a synthetic record. Nothing is read from or written to a contract, and a simulated
/// denial is not audited as a real one. Requires <c>users.manage</c> or <c>onboarding.run</c>.
/// </summary>
public interface IAccessSimulator
{
    /// <summary>The user's actual current assignments, as the evaluator sees them. The caller must be allowed to administer the person.</summary>
    Task<AccessSubject> SubjectOfAsync(Guid userId, CancellationToken ct = default);

    Task<IReadOnlyList<SimulationResult>> SimulateAsync(AccessSubject subject, SampleRecord sample, CancellationToken ct = default);

    /// <summary>Categories of an organisation to choose from when composing a sample record (same authorisation as the simulation).</summary>
    Task<IReadOnlyList<CategoryItem>> CategoriesOfAsync(Guid organisationId, CancellationToken ct = default);
}

internal sealed class AccessSimulator(ContractsDbContext db, IAccessService access, IPeopleDirectory people, IUserService users, IRoleAssignmentService assignments,
    ContractAccessContext context, IOrganisationStructureService structure, ITenantContext tenant, IClock clock) : IAccessSimulator
{
    /// <summary>The permissions a business user cares about when asking what a role allows.</summary>
    public static readonly string[] Permissions =
        ["contracts.read", "documents.download", "suppliers.read", "contracts.create", "contracts.edit_owned", "documents.upload", "legal.review", "audit.read"];

    private async Task RequireAsync(CancellationToken ct)
    {
        if ((await access.GrantsAsync("users.manage", ct)).Count == 0 && (await access.GrantsAsync("onboarding.run", ct)).Count == 0)
            throw new ForbiddenException("You cannot run access simulations.");
    }

    public async Task<AccessSubject> SubjectOfAsync(Guid userId, CancellationToken ct = default)
    {
        await RequireAsync(ct);
        _ = await people.GetManageableUserAsync(userId, ct);
        var user = await users.GetAsync(userId, ct) ?? throw new ResourceNotFoundException();
        var rows = await assignments.ListForUserAsync(userId, includeEnded: true, ct);
        return new AccessSubject(userId, tenant.RequireTenantId(), user.Status,
            rows.Select(a => new AssignmentView(a.Id, a.RoleCode, a.ViewerKind, a.Scope, a.ApproverGroupKey, a.EffectiveFromUtc, a.EffectiveToUtc)).ToList());
    }

    public async Task<IReadOnlyList<CategoryItem>> CategoriesOfAsync(Guid organisationId, CancellationToken ct = default)
    {
        await RequireAsync(ct);
        var rows = await db.Categories.AsNoTracking().Where(c => c.OrganisationId == organisationId && c.IsActive).OrderBy(c => c.Code).ToListAsync(ct);
        return rows.Select(c => new CategoryItem(c.Id, c.OrganisationId, c.Code, c.Name, c.ParentCategoryId, c.DivisionId, c.IsActive, [])).ToList();
    }

    public async Task<IReadOnlyList<SimulationResult>> SimulateAsync(AccessSubject subject, SampleRecord sample, CancellationToken ct = default)
    {
        await RequireAsync(ct);
        var org = await structure.GetAsync(sample.OrganisationId, ct);
        var (_, _, ancestry) = await context.CategoryTreeAsync(ct);
        IReadOnlyList<Guid> path = sample.CategoryId is { } c ? (ancestry.TryGetValue(c, out var p) ? p : [c]) : [];
        var owners = path.Count == 0 ? new HashSet<Guid>() : (await db.CategoryAssignments.AsNoTracking().Where(a => path.Contains(a.CategoryId)).ToListAsync(ct))
            .Where(a => a.IsActiveAt(clock.UtcNow)).Select(a => a.UserId).ToHashSet();
        var descriptor = new ResourceDescriptor("Contract", subject.TenantId, sample.OrganisationId, sample.BusinessUnitId, org.DivisionOfBusinessUnit(sample.BusinessUnitId), sample.SiteId,
            sample.CategoryId, path, sample.Classification, sample.Locked, owners, Id: "simulation");
        var matrix = PermissionMatrix.Instance;
        var results = new List<SimulationResult>();
        foreach (var permission in Permissions)
            results.Add(new SimulationResult(permission, matrix.Info(permission).Description, await access.SimulateAsync(subject, permission, descriptor, ct)));
        return results;
    }
}
