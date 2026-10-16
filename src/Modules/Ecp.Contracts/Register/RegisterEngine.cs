using System.Globalization;
using Ecp.Contracts.Domain;
using Ecp.Contracts.Persistence;
using Ecp.Identity;
using Ecp.Identity.Access;
using Ecp.Identity.Domain;
using Ecp.Metadata;
using Ecp.SharedKernel;
using Ecp.SharedKernel.Localisation;
using Ecp.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Ecp.Contracts.Register;

public sealed record RegisterCatalogue(IReadOnlyList<RegisterField> Fields, Guid? OrganisationId, int CustomFieldRelease, IReadOnlyList<string> Notes)
{
    public RegisterField? Find(string key) => Fields.FirstOrDefault(f => string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Secure, typed, paginated search over the contract register. Order of work: the caller's access to contracts (organisation, business unit, site, category and classification, exactly as the list and
/// the detail page apply it) is applied first, then the typed filters, then sorting and paging in the database. A configured field takes part in a search only where the caller could see its value, so
/// a filter, a sort or a text match cannot be used to read a value that is hidden from them.
/// </summary>
public interface IContractRegister
{
    /// <summary>The fields the signed-in person can show, filter and sort on (core fields, and the configured fields of the organisation they may see). With several organisations in reach and none chosen, only core fields.</summary>
    Task<RegisterCatalogue> CatalogueAsync(Guid? organisationId = null, CancellationToken ct = default);
    /// <summary>Every field an organisation's shared views may name, for someone authoring them (<c>config.draft</c> in the organisation). It describes definitions, not data: each person who opens a shared view still sees only the columns and rows their own access allows.</summary>
    Task<RegisterCatalogue> DefinitionCatalogueAsync(Guid organisationId, CancellationToken ct = default);
    Task<RegisterPage> SearchAsync(ContractQuery query, CancellationToken ct = default);
    /// <summary>The same search for an export: also limited to what the person may export (<c>records.export</c>) and, for configured fields, to those marked reportable.</summary>
    Task<RegisterPage> SearchForExportAsync(ContractQuery query, CancellationToken ct = default);
    /// <summary>Checks a query against the catalogue without running it.</summary>
    Task<IReadOnlyList<RegisterIssue>> ValidateAsync(ContractQuery query, CancellationToken ct = default);
}

internal sealed partial class RegisterQueryEngine(
    ContractsDbContext db, IAccessService access, ContractAccessContext context, ICustomFieldCatalogue customCatalogue, IClock clock, IOrganisationStructureService structure,
    IEnumerable<IContractSetProvider> setProviders, IEnumerable<IStatusGroupProvider> statusGroups, IPeopleDirectory people) : IContractRegister
{
    public const int MaxPageSize = 200, DefaultPageSize = 25, MaxFilters = 20, MaxSorts = 3, MaxValues = 50, MaxColumns = 30, MaxTokens = 5, MaxText = 200;

    private sealed class Row { public Contract C { get; init; } = null!; public Supplier S { get; init; } = null!; public Category G { get; init; } = null!; }

    // ---- the catalogue ---------------------------------------------------------------------------------------------------------------------------------

    private async Task<(IReadOnlyList<AccessGrant> Grants, IReadOnlyList<Guid> Orgs)> ReachAsync(CancellationToken ct)
    {
        var grants = await access.GrantsAsync("contracts.read", ct);
        if (grants.Count == 0) return (grants, []);
        var orgs = (await structure.ListOrganisationsAsync(ct)).Where(o => grants.Any(g => g.OrganisationIds is null || g.OrganisationIds.Contains(o.Id))).Select(o => o.Id).ToList();
        return (grants, orgs);
    }

    private async Task<IReadOnlySet<string>> RolesAsync(Guid organisationId, CancellationToken ct)
    {
        var subject = await access.GetSubjectAsync(ct);
        var now = clock.UtcNow;
        return subject is null ? new HashSet<string>() : subject.Assignments.Where(a => a.IsActiveAt(now) && (a.Scope.Level is ScopeLevel.Tenant or ScopeLevel.Installation || a.Scope.OrganisationIds.Contains(organisationId))).Select(a => a.RoleCode).ToHashSet(StringComparer.Ordinal);
    }

    public async Task<RegisterCatalogue> CatalogueAsync(Guid? organisationId = null, CancellationToken ct = default)
    {
        var (grants, orgs) = await ReachAsync(ct);
        var notes = new List<string>();
        Guid? org = organisationId is { } o && orgs.Contains(o) ? o : organisationId is null && orgs.Count == 1 ? orgs[0] : null;
        var fields = CoreFields.All.Select(f => new RegisterField(f.Key, f.Label, f.Type, false, f.Sortable, f.Filterable, f.Key is "agreementReference" or "contractTitle" or "supplier" or "supplierVendorNumber" or "supplierAbn" or "sapOutlineAgreementNumber", f.Group, Displayable: f.Key != "legalReviewStatus")).ToList();
        var release = 0;
        if (org is { } id)
        {
            var set = await customCatalogue.PublishedAsync(id, ct);
            release = set.ReleaseNumber;
            var roles = await RolesAsync(id, ct);
            // A field held above every level the person may read in this organisation is not offered at all, so its name and existence are not revealed.
            var readable = grants.Where(g => g.OrganisationIds is null || g.OrganisationIds.Contains(id)).SelectMany(g => g.AllowedClassifications).ToHashSet(StringComparer.Ordinal);
            foreach (var d in set.Fields)
                if ((d.VisibleToRoles.Count == 0 || d.VisibleToRoles.Any(roles.Contains)) && readable.Contains(d.Classification))
                    fields.Add(new RegisterField(d.Key, d.Label, d.Type, true, d.Sortable && !d.IsCalculated, d.Filterable && !d.IsCalculated, d.Searchable, set.Sections.FirstOrDefault(s => s.Key == d.Section)?.Label ?? "Additional details", d.IsRetired, d.Section, Displayable: !d.IsCalculated, Reportable: d.Reportable));
        }
        else if (orgs.Count > 1) notes.Add("Choose an organisation to use its additional fields.");
        return new RegisterCatalogue(fields, org, release, notes);
    }

    public async Task<RegisterCatalogue> DefinitionCatalogueAsync(Guid organisationId, CancellationToken ct = default)
    {
        if (!(await access.GrantsAsync("config.draft", ct)).Any(g => g.OrganisationIds is null || g.OrganisationIds.Contains(organisationId))) throw new ResourceNotFoundException();
        var fields = CoreFields.All.Select(f => new RegisterField(f.Key, f.Label, f.Type, false, f.Sortable, f.Filterable, f.Key is "agreementReference" or "contractTitle" or "supplier" or "supplierVendorNumber" or "supplierAbn" or "sapOutlineAgreementNumber", f.Group, Displayable: f.Key != "legalReviewStatus")).ToList();
        var set = await customCatalogue.PublishedAsync(organisationId, ct);
        foreach (var d in set.Fields)
            fields.Add(new RegisterField(d.Key, d.Label, d.Type, true, d.Sortable && !d.IsCalculated, d.Filterable && !d.IsCalculated, d.Searchable, set.Sections.FirstOrDefault(s => s.Key == d.Section)?.Label ?? "Additional details", d.IsRetired, d.Section, Displayable: !d.IsCalculated, Reportable: d.Reportable));
        return new RegisterCatalogue(fields, organisationId, set.ReleaseNumber, []);
    }

    // ---- validation of a query -------------------------------------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<RegisterIssue>> ValidateAsync(ContractQuery query, CancellationToken ct = default)
    {
        var catalogue = await CatalogueAsync(query.OrganisationId, ct);
        var (_, orgs) = await ReachAsync(ct);
        return Validate(query, catalogue, orgs);
    }

    internal IReadOnlyList<RegisterIssue> Validate(ContractQuery q, RegisterCatalogue catalogue, IReadOnlyList<Guid> orgs)
    {
        var issues = new List<RegisterIssue>();
        if (q.OrganisationId is { } oid && !orgs.Contains(oid)) issues.Add(new("organisationId", "organisation", "That organisation is not available to you."));
        if (q.Text is { Length: > MaxText }) issues.Add(new("text", "text_length", $"Search text is limited to {MaxText} characters."));
        if ((q.Filters?.Count ?? 0) > MaxFilters) issues.Add(new("filters", "too_many", $"At most {MaxFilters} filters can be used together."));
        if ((q.Sorts?.Count ?? 0) > MaxSorts) issues.Add(new("sorts", "too_many", $"At most {MaxSorts} sort fields can be used."));
        if ((q.Columns?.Count ?? 0) > MaxColumns) issues.Add(new("columns", "too_many", $"At most {MaxColumns} columns can be shown."));
        for (var i = 0; i < (q.Filters?.Count ?? 0); i++)
        {
            var f = q.Filters![i]; var p = $"filters[{i}]";
            if (f.Field == "set")
            {
                if (f.Op != FilterOps.Is || f.Values.Count is < 1 or > 2 || setProviders.All(s => !string.Equals(s.Key, f.Values[0], StringComparison.Ordinal))) issues.Add(new(p, "set", "That list is not available."));
                continue;
            }
            var field = catalogue.Find(f.Field);
            if (field is null) { issues.Add(new(p, "field", $"'{f.Field}' is not a field you can filter on.")); continue; }
            if (!field.Filterable) { issues.Add(new(p, "not_filterable", $"{field.Label} cannot be filtered.")); continue; }
            if (!field.Operators.Contains(f.Op, StringComparer.Ordinal)) { issues.Add(new(p, "operator", $"{field.Label} cannot be filtered with '{f.Op}'.")); continue; }
            var needs = f.Op switch { FilterOps.IsEmpty or FilterOps.IsNotEmpty => 0, FilterOps.Between => 2, FilterOps.In => -1, _ => 1 };
            if (needs >= 0 && f.Values.Count != needs) issues.Add(new(p, "values", $"{field.Label} {f.Op} needs {needs} value(s)."));
            if (needs < 0 && (f.Values.Count is < 1 or > MaxValues)) issues.Add(new(p, "values", $"Give between 1 and {MaxValues} values."));
            foreach (var v in f.Values)
            {
                if (v is null || v.Length > MaxText) { issues.Add(new(p, "value_length", "A value is too long.")); break; }
                if (!ValueFits(field, f.Op, v)) { issues.Add(new(p, "value", $"'{(v.Length > 30 ? v[..30] + "..." : v)}' is not a valid value for {field.Label}.")); break; }
            }
        }
        for (var i = 0; i < (q.Sorts?.Count ?? 0); i++)
        {
            var s = q.Sorts![i];
            var field = catalogue.Find(s.Field);
            if (field is null || !field.Sortable) issues.Add(new($"sorts[{i}]", "sort", $"'{s.Field}' cannot be used to sort."));
        }
        foreach (var c in q.Columns ?? [])
            if (catalogue.Find(c) is null) { /* an unavailable column is dropped with a note when the search runs; it is not an error */ }
        if (catalogue.OrganisationId is null && q.Text is not null) { /* core fields only: fine */ }
        return issues;
    }

    private static bool ValueFits(RegisterField f, string op, string v)
    {
        if (v.StartsWith(FilterTokens.Today, StringComparison.Ordinal)) return f.Type is FieldTypes.Date or FieldTypes.DateTime && TryRelative(v, DateOnly.MinValue, out _);
        if (v == FilterTokens.Me || v == FilterTokens.Live) return f.Key is "owner" or "status";
        return f.Type switch
        {
            FieldTypes.Integer or FieldTypes.Decimal or FieldTypes.Currency or FieldTypes.Percentage => decimal.TryParse(v, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out _),
            FieldTypes.Date or FieldTypes.DateTime => DateOnly.TryParseExact(v, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
            FieldTypes.Boolean => v is "true" or "false",
            FieldTypes.Person or FieldTypes.FileReference => Guid.TryParse(v, out _),
            _ => !f.Key.EndsWith("Id", StringComparison.Ordinal) && (f.Key is not ("category" or "legalEntity" or "businessUnit" or "site") || Guid.TryParse(v, out _)) && !v.Any(char.IsControl)
        };
    }

    internal static bool TryRelative(string token, DateOnly today, out DateOnly date)
    {
        date = today;
        var rest = token[FilterTokens.Today.Length..];
        if (rest.Length == 0) return true;
        if (rest[0] is not ('+' or '-') || !int.TryParse(rest[1..], NumberStyles.None, CultureInfo.InvariantCulture, out var days) || days > 36500) return false;
        date = today.AddDays(rest[0] == '+' ? days : -days);
        return true;
    }

    // ---- running a query -------------------------------------------------------------------------------------------------------------------------------

    public Task<RegisterPage> SearchAsync(ContractQuery q, CancellationToken ct = default) => SearchCoreAsync(q, forExport: false, ct);
    public Task<RegisterPage> SearchForExportAsync(ContractQuery q, CancellationToken ct = default) => SearchCoreAsync(q, forExport: true, ct);

    private async Task<RegisterPage> SearchCoreAsync(ContractQuery q, bool forExport, CancellationToken ct)
    {
        var (grants, orgs) = await ReachAsync(ct);
        IReadOnlyList<AccessGrant>? exportGrants = null;
        if (forExport)
        {
            exportGrants = await access.GrantsAsync("records.export", ct);
            if (exportGrants.Count == 0) throw new ForbiddenException("You cannot export records.");
        }
        var catalogue = await CatalogueAsync(q.OrganisationId, ct);
        var issues = Validate(q, catalogue, orgs);
        if (issues.Count > 0) throw new RegisterQueryException(issues);
        var page = Math.Max(1, q.Page); var size = Math.Clamp(q.PageSize <= 0 ? DefaultPageSize : q.PageSize, 1, forExport ? 1000 : MaxPageSize);
        var notes = new List<string>(catalogue.Notes);
        var columns = new List<RegisterColumn>();
        foreach (var key in q.Columns is { Count: > 0 } ? q.Columns : DefaultColumns)
        {
            if (catalogue.Find(key) is { } f && (!forExport || !f.IsCustom || f.Reportable)) columns.Add(new RegisterColumn(f.Key, f.Label, f.Type, f.IsCustom, f.Retired));
            else notes.Add($"The column '{key}' is not available to you and is not shown.");
        }
        if (grants.Count == 0) return new RegisterPage(columns, [], 0, page, size, notes);

        var listCtx = await context.ListContextAsync(access.CurrentUserId, ct);
        var visible = ContractAccessFilter.Build(grants, listCtx);
        var cq = db.Contracts.AsNoTracking().Where(visible);
        if (exportGrants is not null) cq = cq.Where(ContractAccessFilter.Build(exportGrants, listCtx));       // an export is also limited to the records the person may export
        if (!q.IncludeArchived) cq = cq.Where(c => !c.IsArchived);
        if (q.OrganisationId is { } onlyOrg) cq = cq.Where(c => c.OrganisationId == onlyOrg);
        var sq = db.Suppliers.AsNoTracking();

        var org = catalogue.OrganisationId;
        var set = org is { } oid ? await customCatalogue.PublishedAsync(oid, ct) : CustomFieldSet.Empty;
        var scale = await access.ClassificationScaleAsync(ct);
        var vis = new LevelVisibility(db, grants, listCtx, scale);
        var now = clock.UtcNow;
        var today = TodayFor(org is { } o2 ? await structure.GetOrganisationAsync(o2, ct) : null);

        // Typed filters are applied to the contracts and suppliers before they are joined, so each is one narrow predicate the database can use an index for.
        foreach (var f in q.Filters ?? [])
            (cq, sq) = await ApplyFilterAsync(cq, sq, f, catalogue, set, listCtx, vis, today, now, ct);

        var rows = from c in cq
                   join s in sq on new { c.TenantId, Id = c.SupplierId } equals new { s.TenantId, s.Id }
                   join g in db.Categories.AsNoTracking() on new { c.TenantId, Id = c.CategoryId } equals new { g.TenantId, g.Id }
                   select new Row { C = c, S = s, G = g };

        // Free text: every word must match somewhere readable (reference, title, supplier, vendor number, ABN, SAP number, or a searchable configured field the caller may see).
        // Without a searchable configured field the match is a plain predicate on the row. With one it is "id in (ids that match in the row, ids that match in a configured field)": the two sets are
        // uncorrelated, so the database reads each once and joins once. Written as "row match OR id in (configured match)" it ran the configured match once for every contract (docs/PERFORMANCE_REVIEW.md).
        var unfiltered = rows;
        static IQueryable<Row> RowMatch(IQueryable<Row> q, string t, string upper)
            => q.Where(r => r.C.AgreementReference.Contains(upper) || r.C.ContractTitle.ToUpper().Contains(upper) || r.S.NormalisedName.Contains(upper) || (r.S.VendorNumber != null && r.S.VendorNumber.ToUpper().Contains(upper))
                || (r.S.Abn != null && r.S.Abn.Contains(t)) || (r.C.SapOutlineAgreementNumber != null && r.C.SapOutlineAgreementNumber.ToUpper().Contains(upper)));
        foreach (var token in (q.Text ?? "").Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).Take(MaxTokens))
        {
            var t = token.Trim(); var upper = t.ToUpperInvariant();
            var custom = SearchableMatches(set, catalogue, t, vis);
            if (custom is null) { rows = RowMatch(rows, t, upper); continue; }
            var matching = RowMatch(unfiltered, t, upper).Select(r => r.C.Id).Concat(custom);
            rows = rows.Where(r => matching.Contains(r.C.Id));
        }

        var total = await rows.CountAsync(ct);
        var ordered = ApplySorts(rows, q.Sorts ?? [], catalogue, set, vis);
        var pageRows = await ordered.Skip((page - 1) * size).Take(size).ToListAsync(ct);
        var cells = await BuildRowsAsync(pageRows, columns, set, scale, vis, ct);
        return new RegisterPage(columns, cells, total, page, size, notes);
    }

    public static readonly IReadOnlyList<string> DefaultColumns = ["agreementReference", "contractTitle", "supplier", "category", "status", "classification", "endDate"];

    private DateOnly TodayFor(OrganisationSummary? org) => TimeZones.LocalDate(clock.UtcNow, org is null ? TimeZoneInfo.Utc : TimeZones.Find(org.TimeZoneId));

    // ---- text over configured fields ---------------------------------------------------------------------------------------------------------------------

    /// <summary>Ids of contracts whose searchable configured fields (that the caller may see) contain the text. Null when there are none.</summary>
    private IQueryable<Guid>? SearchableMatches(CustomFieldSet set, RegisterCatalogue catalogue, string text, LevelVisibility vis)
    {
        var fields = set.Fields.Where(d => d.Searchable && !d.IsCalculated && catalogue.Find(d.Key) is not null && d.Type is FieldTypes.Text or FieldTypes.LongText or FieldTypes.Email or FieldTypes.Url or FieldTypes.SingleChoice or FieldTypes.MultiChoice or FieldTypes.Lookup).ToList();
        IQueryable<Guid>? union = null;
        foreach (var group in fields.GroupBy(d => (d.Classification, States: string.Join("|", d.VisibleInStates.Order(StringComparer.Ordinal)))))
        {
            var keys = group.Select(d => d.Key).ToArray(); var upper = text.ToUpperInvariant();
            var states = group.First().VisibleInStates.ToArray();
            // Choice fields hold codes: search the label as well as the code by matching the codes whose label contains the text.
            var codes = group.Where(d => FieldTypes.UsesList(d.Type)).SelectMany(d => set.ChoicesOf(d)).Where(c => c.Label.Contains(text, StringComparison.OrdinalIgnoreCase)).Select(c => c.Code).Distinct().ToArray();
            var values = db.CustomValues.AsNoTracking().Where(v => keys.Contains(v.FieldKey) && ((v.TextValue != null && (v.TextValue.ToUpper().Contains(upper) || codes.Contains(v.TextValue))) || (v.LongTextValue != null && v.LongTextValue.ToUpper().Contains(upper))));
            // The contracts here are already limited to what the caller may read. When every grant also lets them read this field's level, no further access test is needed (see LevelVisibility); otherwise the values
            // of contracts the caller could not see this field on are left out. A state limit needs the contract's status, so only then is the contract joined.
            IQueryable<Guid> ids;
            var open = vis.IsOpen(group.Key.Classification);
            if (open && states.Length == 0) ids = values.Select(v => v.ContractId);
            else if (open)
                ids = (from v in values
                       join c in db.Contracts.AsNoTracking() on new { v.TenantId, Id = v.ContractId } equals new { c.TenantId, c.Id }
                       where states.Contains(c.Status)
                       select c.Id);
            else
            {
                var allowedIds = vis.Ids(group.Key.Classification); var higher = vis.Above(group.Key.Classification);
                var matches = from v in values
                              join c in db.Contracts.AsNoTracking() on new { v.TenantId, Id = v.ContractId } equals new { c.TenantId, c.Id }
                              where higher.Contains(c.Classification) || allowedIds.Contains(c.Id)
                              select new { c.Id, c.Status };
                ids = states.Length == 0 ? matches.Select(x => x.Id) : matches.Where(x => states.Contains(x.Status)).Select(x => x.Id);
            }
            union = union is null ? ids : union.Union(ids);
        }
        return union;
    }

}

/// <summary>
/// Which contracts a person may see a configured field on, by the field's classification level. A value is visible on a contract the person can read when the contract is held above the field's level, or the
/// person has a grant that covers the contract and allows that level. The query already holds only the contracts the person can read, so when <b>every</b> grant allows the level the second test is true for each
/// of those contracts and adds nothing: <see cref="IsOpen"/> says so, and callers leave the test out. Evidence (docs/PERFORMANCE_REVIEW.md): the test written as "level above OR id in (access subquery)" made
/// SQL Server estimate one row, choose a nested loop and re-scan the contracts for each row; leaving it out where it is redundant removes that.
/// </summary>
internal sealed class LevelVisibility(ContractsDbContext db, IReadOnlyList<AccessGrant> grants, ContractAccessFilter.Context listCtx, ClassificationScale scale)
{
    private readonly Dictionary<string, IQueryable<Guid>> ids = new(StringComparer.Ordinal);

    /// <summary>True when every grant that lets the person read contracts also allows this classification level.</summary>
    public bool IsOpen(string level) => IsOpen(grants, level);

    internal static bool IsOpen(IReadOnlyList<AccessGrant> grants, string level) => grants.Count > 0 && grants.All(g => g.AllowedClassifications.Contains(level, StringComparer.Ordinal));

    /// <summary>Ids of the contracts the person may read through grants that allow this level.</summary>
    public IQueryable<Guid> Ids(string level)
    {
        if (!ids.TryGetValue(level, out var q))
            ids[level] = q = db.Contracts.AsNoTracking().Where(ContractAccessFilter.Build(grants.Where(g => g.AllowedClassifications.Contains(level)).ToList(), listCtx)).Select(c => c.Id);
        return q;
    }

    /// <summary>The classification levels above this one: a contract held at one of them is readable for the field by anyone who can read the contract.</summary>
    public string[] Above(string level) => scale.Levels.Where(l => scale.Rank(l) > scale.Rank(level)).ToArray();
}
