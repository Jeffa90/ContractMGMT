using Ecp.Metadata;

namespace Ecp.Contracts.Register;

/// <summary>The operators of the filter grammar. A filter is always field + operator + typed values; there is no expression text anywhere.</summary>
public static class FilterOps
{
    public const string Eq = "Eq", NotEq = "NotEq", In = "In", Contains = "Contains", StartsWith = "StartsWith", Gte = "Gte", Lte = "Lte", Between = "Between", IsEmpty = "IsEmpty", IsNotEmpty = "IsNotEmpty", Is = "Is";
    public static readonly IReadOnlyList<string> All = [Eq, NotEq, In, Contains, StartsWith, Gte, Lte, Between, IsEmpty, IsNotEmpty, Is];

    public static IReadOnlyList<string> For(string type) => type switch
    {
        FieldTypes.Boolean => [Is],
        FieldTypes.Integer or FieldTypes.Decimal or FieldTypes.Currency or FieldTypes.Percentage => [Eq, NotEq, Gte, Lte, Between, IsEmpty, IsNotEmpty],
        FieldTypes.Date or FieldTypes.DateTime => [Eq, Gte, Lte, Between, IsEmpty, IsNotEmpty],
        FieldTypes.SingleChoice or FieldTypes.MultiChoice or FieldTypes.Lookup or FieldTypes.Person => [In, Eq, NotEq, IsEmpty, IsNotEmpty],
        _ => [Eq, NotEq, Contains, StartsWith, In, IsEmpty, IsNotEmpty]
    };
}

public sealed record FilterClause(string Field, string Op, IReadOnlyList<string> Values);
public sealed record SortClause(string Field, bool Descending = false);

/// <summary>A search of the register. Everything is typed: free text plus filter clauses and sorts over fields that exist in the catalogue the caller is allowed to use.</summary>
public sealed record ContractQuery(
    string? Text = null, IReadOnlyList<FilterClause>? Filters = null, IReadOnlyList<SortClause>? Sorts = null, int Page = 1, int PageSize = 25, IReadOnlyList<string>? Columns = null,
    Guid? OrganisationId = null, bool IncludeArchived = false);

/// <summary>A field the caller can show, filter or sort on in the register.</summary>
public sealed record RegisterField(string Key, string Label, string Type, bool IsCustom, bool Sortable, bool Filterable, bool Searchable, string Group, bool Retired = false, string? Section = null, bool Displayable = true, bool Reportable = true)
{
    public IReadOnlyList<string> Operators => Filterable ? FilterOps.For(Type) : [];
}

/// <summary>A column of a result. <see cref="Type"/> is the field type so a page can format dates, amounts and flags; cells are invariant text.</summary>
public sealed record RegisterColumn(string Key, string Label, string Type, bool IsCustom, bool Retired = false);

public sealed record RegisterRow(Guid Id, Guid OrganisationId, string AgreementReference, string Title, string Status, string Classification, IReadOnlyDictionary<string, string?> Cells);

public sealed record RegisterPage(IReadOnlyList<RegisterColumn> Columns, IReadOnlyList<RegisterRow> Rows, int Total, int Page, int PageSize, IReadOnlyList<string> Notes)
{
    public int Pages => PageSize <= 0 ? 0 : (int)Math.Ceiling(Total / (double)PageSize);
}

/// <summary>The thing a validation of a query or a view found wrong, by field.</summary>
public sealed record RegisterIssue(string Path, string Code, string Message);

public sealed class RegisterQueryException(IReadOnlyList<RegisterIssue> issues)
    : Ecp.SharedKernel.DomainException("invalid_query", "The search is not valid: " + string.Join(" ", issues.Take(3).Select(i => i.Message)))
{
    public IReadOnlyList<RegisterIssue> Issues { get; } = issues;
}

/// <summary>A source of contract ids for something the contracts module does not hold (legal review state, approvals waiting, variations, reminders). Registered by the module that does.</summary>
public interface IContractSetProvider
{
    string Key { get; }
    /// <summary>The contracts in the set for the signed-in person. The register still applies the caller's access to them; a provider never widens what can be seen.</summary>
    Task<IReadOnlySet<Guid>> IdsAsync(string? argument, CancellationToken ct = default);
}

/// <summary>A named group of workflow state codes ("live", "owner action"), supplied by the module that owns the workflow definition, so views never hard-code state names.</summary>
public interface IStatusGroupProvider
{
    string Key { get; }
    Task<IReadOnlyList<string>> StatusesAsync(Guid? organisationId, CancellationToken ct = default);
}

/// <summary>Values in a filter that stand for something worked out when the search runs.</summary>
public static class FilterTokens
{
    public const string Me = "@me", Today = "@today", Live = "@live";
    public static bool IsDateToken(string v) => v.StartsWith(Today, StringComparison.Ordinal);
}
