using System.Linq.Expressions;
using Ecp.Contracts.Domain;
using Ecp.Identity.Access;

namespace Ecp.Contracts;

// Reviewed use of Expression.Lambda (ARC-007): composes a LINQ predicate from typed grant data for EF Core to translate. Nothing is parsed from text and nothing is compiled or executed in-process.
/// <summary>Turns an assignment's grant into a query predicate over contracts, mirroring <see cref="AccessEvaluator"/> so lists, counts and exports never show a record that a direct read would hide.</summary>
public static class ContractAccessFilter
{
    /// <summary>Everything the predicate needs that the grant itself does not carry.</summary>
    public sealed record Context(
        IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> DescendantsOf,      // category id -> itself and all descendants
        IReadOnlySet<Guid> OwnedCategoryIds,                               // categories (not expanded) the user currently owns
        IReadOnlyDictionary<Guid, Guid?> DivisionOfBusinessUnit);          // business unit id -> division id

    public static Expression<Func<Contract, bool>> Build(IReadOnlyList<AccessGrant> grants, Context ctx)
    {
        Expression<Func<Contract, bool>>? any = null;
        foreach (var grant in grants)
        {
            var p = ForGrant(grant, ctx);
            any = any is null ? p : Or(any, p);
        }
        return any ?? (c => false);
    }

    private static Expression<Func<Contract, bool>> ForGrant(AccessGrant g, Context ctx)
    {
        if (g.Relationship is GrantRelationship.AssignedApproval or GrantRelationship.PolicyAuthorised or GrantRelationship.TenantLevelDefinition)
            return c => false; // not listable: relationship facts for these do not exist as list-able data yet

        Expression<Func<Contract, bool>> result = c => true;
        var classes = g.AllowedClassifications.ToArray();
        result = And(result, c => classes.Contains(c.Classification));

        if (g.OrganisationIds is { } orgList)
        {
            var orgs = orgList.ToArray();
            result = And(result, c => orgs.Contains(c.OrganisationId));
        }

        if (g.BusinessUnitIds.Count > 0 || g.DivisionIds.Count > 0)
        {
            IEnumerable<Guid> bus = ctx.DivisionOfBusinessUnit.Keys;
            if (g.DivisionIds.Count > 0) bus = bus.Where(b => ctx.DivisionOfBusinessUnit[b] is { } d && g.DivisionIds.Contains(d));
            if (g.BusinessUnitIds.Count > 0) bus = bus.Where(g.BusinessUnitIds.Contains);
            var buSet = bus.ToArray();
            result = And(result, c => c.BusinessUnitId != null && buSet.Contains(c.BusinessUnitId.Value));
        }

        if (g.SiteIds.Count > 0)
        {
            var sites = g.SiteIds.ToArray();
            result = And(result, c => c.SiteId != null && sites.Contains(c.SiteId.Value));
        }

        IEnumerable<Guid>? categories = g.CategoryIds?.SelectMany(id => ctx.DescendantsOf.TryGetValue(id, out var d) ? d : [id]);
        if (g.Relationship == GrantRelationship.ActiveCategoryOwnership)
        {
            var owned = ctx.OwnedCategoryIds.SelectMany(id => ctx.DescendantsOf.TryGetValue(id, out var d) ? d : [id]);
            categories = categories is null ? owned : categories.Intersect(owned);
        }
        if (categories is not null)
        {
            var set = categories.Distinct().ToArray();
            result = And(result, c => set.Contains(c.CategoryId));
        }
        return result;
    }

    private static Expression<Func<Contract, bool>> And(Expression<Func<Contract, bool>> a, Expression<Func<Contract, bool>> b)
    {
        var p = Expression.Parameter(typeof(Contract), "c");
        return Expression.Lambda<Func<Contract, bool>>(Expression.AndAlso(Rebind(a, p), Rebind(b, p)), p);
    }

    private static Expression<Func<Contract, bool>> Or(Expression<Func<Contract, bool>> a, Expression<Func<Contract, bool>> b)
    {
        var p = Expression.Parameter(typeof(Contract), "c");
        return Expression.Lambda<Func<Contract, bool>>(Expression.OrElse(Rebind(a, p), Rebind(b, p)), p);
    }

    private static Expression Rebind(Expression<Func<Contract, bool>> e, ParameterExpression p) => new ParameterReplacer(e.Parameters[0], p).Visit(e.Body);

    private sealed class ParameterReplacer(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : base.VisitParameter(node);
    }
}
