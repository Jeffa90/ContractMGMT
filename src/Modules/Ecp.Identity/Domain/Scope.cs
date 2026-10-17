using Ecp.SharedKernel;

namespace Ecp.Identity.Domain;

/// <summary>
/// The scope elements of one role assignment (ACCESS_CONTROL_SPEC section 2). Empty business unit, division and site lists mean
/// "not narrowed on that dimension"; a null category list means all categories. Access is the intersection of every element.
/// </summary>
public sealed record ScopeSet(
    ScopeLevel Level,
    IReadOnlyList<Guid> OrganisationIds,
    IReadOnlyList<Guid> BusinessUnitIds,
    IReadOnlyList<Guid> DivisionIds,
    IReadOnlyList<Guid> SiteIds,
    IReadOnlyList<Guid>? CategoryIds,
    string ClassificationCeiling)
{
    public static ScopeSet ForOrganisations(IEnumerable<Guid> organisations, string ceiling, IEnumerable<Guid>? businessUnits = null,
        IEnumerable<Guid>? divisions = null, IEnumerable<Guid>? sites = null, IEnumerable<Guid>? categories = null) => new(
        ScopeLevel.Organisation, Distinct(organisations), Distinct(businessUnits ?? []), Distinct(divisions ?? []), Distinct(sites ?? []),
        categories is null ? null : Distinct(categories), ceiling);

    public static ScopeSet Tenant(string ceiling, IEnumerable<Guid>? categories = null)
        => new(ScopeLevel.Tenant, [], [], [], [], categories is null ? null : Distinct(categories), ceiling);

    public static ScopeSet Installation(string ceiling = "Public") => new(ScopeLevel.Installation, [], [], [], [], null, ceiling);

    private static Guid[] Distinct(IEnumerable<Guid> ids) => ids.Distinct().ToArray();

    public bool NarrowsBusinessUnit => BusinessUnitIds.Count > 0;
    public bool NarrowsDivision => DivisionIds.Count > 0;
    public bool NarrowsSite => SiteIds.Count > 0;
    public bool NarrowsCategory => CategoryIds is not null;

    /// <summary>Structural validation independent of any user: an organisation-level assignment without organisations is refused.</summary>
    public void Validate(ClassificationScale scale)
    {
        if (!scale.IsKnown(ClassificationCeiling))
            throw new DomainException("invalid_classification", $"Classification ceiling '{ClassificationCeiling}' is not one of the configured levels.");
        switch (Level)
        {
            case ScopeLevel.Organisation when OrganisationIds.Count == 0:
                throw new DomainException("organisation_scope_required", "An assignment without an organisational scope is refused.");
            case ScopeLevel.Tenant or ScopeLevel.Installation when OrganisationIds.Count > 0:
                throw new DomainException("invalid_scope", "Tenant-level and installation-level assignments do not name [[organisation|plural|lower]].");
        }
        if (Level == ScopeLevel.Installation && (NarrowsBusinessUnit || NarrowsDivision || NarrowsSite || NarrowsCategory))
            throw new DomainException("invalid_scope", "Installation-level assignments cannot be narrowed.");
        if (CategoryIds is { Count: 0 })
            throw new DomainException("invalid_scope", "[[category|lower|a|cap]] scope must name at least one [[category|lower]]; use no [[category|lower]] scope for all [[category|plural|lower]].");
    }

    /// <summary>True when every element of <paramref name="inner"/> lies within this scope (used to stop a grantor granting above their own scope). <paramref name="categoryAncestry"/> returns a category and its ancestors.</summary>
    public bool Contains(ScopeSet inner, ClassificationScale scale, Func<Guid, IReadOnlyCollection<Guid>>? categoryAncestry = null, bool checkCeiling = true)
    {
        if (Level == ScopeLevel.Installation) return false; // installation scope grants nothing in tenants
        if (inner.Level == ScopeLevel.Installation) return false;
        if (Level == ScopeLevel.Organisation && (inner.Level == ScopeLevel.Tenant || !inner.OrganisationIds.All(OrganisationIds.Contains))) return false;
        if (NarrowsBusinessUnit && (!inner.NarrowsBusinessUnit || !inner.BusinessUnitIds.All(BusinessUnitIds.Contains))) return false;
        if (NarrowsDivision && (!inner.NarrowsDivision || !inner.DivisionIds.All(DivisionIds.Contains))) return false;
        if (NarrowsSite && (!inner.NarrowsSite || !inner.SiteIds.All(SiteIds.Contains))) return false;
        if (NarrowsCategory)
        {
            // A parent category covers its subcategories: each inner category must be, or sit under, one of this scope's categories.
            var ancestry = categoryAncestry ?? (c => [c]);
            if (!inner.NarrowsCategory || !inner.CategoryIds!.All(c => ancestry(c).Any(CategoryIds!.Contains))) return false;
        }
        return !checkCeiling || scale.Rank(inner.ClassificationCeiling) <= scale.Rank(ClassificationCeiling);
    }
}
