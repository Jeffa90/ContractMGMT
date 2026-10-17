using System.Globalization;

namespace Ecp.Metadata;

/// <summary>The stable type codes of custom fields (the same codes as <c>requirements/03-data-dictionary/custom-field-types.csv</c>).</summary>
public static class FieldTypes
{
    public const string Text = "TEXT", LongText = "LONG_TEXT", Integer = "INTEGER", Decimal = "DECIMAL", Currency = "CURRENCY", Percentage = "PERCENTAGE", Date = "DATE", DateTime = "DATETIME",
        Boolean = "BOOLEAN", SingleChoice = "SINGLE_CHOICE", MultiChoice = "MULTI_CHOICE", Lookup = "LOOKUP", Person = "PERSON", Email = "EMAIL", Url = "URL", FileReference = "FILE_REFERENCE",
        Calculated = "CALCULATED_DISPLAY";

    public static readonly IReadOnlyList<string> All = [Text, LongText, Integer, Decimal, Currency, Percentage, Date, DateTime, Boolean, SingleChoice, MultiChoice, Lookup, Person, Email, Url, FileReference, Calculated];

    public static bool IsKnown(string? type) => type is not null && All.Contains(type, StringComparer.Ordinal);
    public static bool IsNumeric(string type) => type is Integer or Decimal or Currency or Percentage;
    public static bool IsChoice(string type) => type is SingleChoice or MultiChoice;
    public static bool IsMulti(string type) => type == MultiChoice;
    public static bool UsesList(string type) => type is SingleChoice or MultiChoice or Lookup;

    public static string Label(string type) => type switch
    {
        Text => "Single-line text", LongText => "Long text", Integer => "Integer", Decimal => "Decimal", Currency => "Currency amount", Percentage => "Percentage", Date => "Date", DateTime => "Date and time",
        Boolean => "Yes/No", SingleChoice => "Single choice", MultiChoice => "Multiple choice", Lookup => "Lookup", Person => "Person", Email => "Email address", Url => "Web address", FileReference => "File reference",
        Calculated => "Calculated (display only)", _ => type
    };

    public static StorageKind StorageOf(string type) => type switch
    {
        Text or SingleChoice or MultiChoice or Lookup or Email => StorageKind.Text,
        LongText or Url => StorageKind.LongText,
        Integer or Decimal or Currency or Percentage => StorageKind.Number,
        Date => StorageKind.Date,
        DateTime => StorageKind.DateTime,
        Boolean => StorageKind.Bool,
        Person or FileReference => StorageKind.Ref,
        _ => StorageKind.None
    };
}

/// <summary>Which typed column of the value table holds a field's value.</summary>
public enum StorageKind { None, Text, LongText, Number, Date, DateTime, Bool, Ref }

public static class FieldStatuses
{
    public const string Active = "Active", Retired = "Retired";
}

public sealed record FieldChoice(string Code, string Label, bool Active = true);
public sealed record LookupList(string Name, string Label, IReadOnlyList<FieldChoice> Members);
public sealed record FieldSection(string Key, string Label, int Order);

/// <summary>The predefined checks a field can carry. Each applies to some types only; a check on a type it does not belong to is refused when the definition is published.</summary>
public sealed record FieldValidation(
    int? MinLength = null, int? MaxLength = null, string? Pattern = null, string? PatternMessage = null, decimal? Min = null, decimal? Max = null, int? Decimals = null,
    DateOnly? MinDate = null, DateOnly? MaxDate = null, int? MinItems = null, int? MaxItems = null, string? Currency = null)
{
    public static readonly FieldValidation None = new();
}

public static class CalculationKinds
{
    public const string Template = "Template", Sum = "Sum", Difference = "Difference", Product = "Product", Ratio = "Ratio", DaysBetween = "DaysBetween";
    public static readonly IReadOnlyList<string> All = [Template, Sum, Difference, Product, Ratio, DaysBetween];
}

/// <summary>A display-only value worked out from other fields by one of a closed set of operations. There is no expression language: nothing here can run code.</summary>
public sealed record CalculatedSpec(string Kind, IReadOnlyList<string> Operands, string? Template = null, int? Decimals = null);

public sealed record FieldDefinition(
    string Key, string Label, string Description, string HelpText, string Placeholder, string Type, string Section, int Order, bool Required, IReadOnlyList<string> Default,
    FieldValidation Validation, IReadOnlyList<FieldChoice> Choices, string? Lookup, CalculatedSpec? Calculated, bool Searchable, bool Filterable, bool Sortable, bool Reportable,
    string Classification, IReadOnlyList<string> VisibleToRoles, IReadOnlyList<string> EditableByRoles, IReadOnlyList<string> VisibleInStates, IReadOnlyList<string> EditableInStates,
    IReadOnlyList<string> ContractTypes, string Status, DateOnly? EffectiveFrom, DateOnly? EffectiveTo, bool UsableInApprovals = false)
{
    public bool IsRetired => string.Equals(Status, FieldStatuses.Retired, StringComparison.Ordinal);
    public bool IsCalculated => Type == FieldTypes.Calculated;
    /// <summary>The value can be used in the conditions of an approval matrix. Off unless an administrator turns it on, because a matrix explanation shows the values it judged.</summary>
    public bool CanFeedApprovals => UsableInApprovals && !IsRetired && Type is not (FieldTypes.MultiChoice or FieldTypes.LongText or FieldTypes.FileReference or FieldTypes.Url or FieldTypes.Calculated);
}

/// <summary>The published custom field definitions of an organisation, with the release they came from (the configuration version).</summary>
public sealed class CustomFieldSet
{
    public int SchemaVersion { get; init; } = 1;
    public IReadOnlyList<FieldSection> Sections { get; init; } = [];
    public IReadOnlyList<LookupList> Lookups { get; init; } = [];
    public IReadOnlyList<FieldDefinition> Fields { get; init; } = [];
    /// <summary>The release number this set was read from; zero for a set that has not been published.</summary>
    public int ReleaseNumber { get; init; }

    public static readonly CustomFieldSet Empty = new();

    public FieldDefinition? Find(string key) => Fields.FirstOrDefault(f => string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase));
    public LookupList? FindList(string? name) => name is null ? null : Lookups.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The choices of a choice or lookup field (the field's own, or its named list's), active and retired.</summary>
    public IReadOnlyList<FieldChoice> ChoicesOf(FieldDefinition d)
        => d.Type == FieldTypes.Lookup ? FindList(d.Lookup)?.Members ?? [] : d.Choices;

    /// <summary>Resolves what a person or a source file wrote to the stored code of a choice: the code, or the label, ignoring case. Null when nothing matches.</summary>
    public FieldChoice? FindChoice(FieldDefinition d, string? text)
    {
        var t = text?.Trim();
        if (string.IsNullOrEmpty(t)) return null;
        var all = ChoicesOf(d);
        return all.FirstOrDefault(c => string.Equals(c.Code, t, StringComparison.OrdinalIgnoreCase)) ?? all.FirstOrDefault(c => string.Equals(c.Label, t, StringComparison.OrdinalIgnoreCase));
    }

    public IEnumerable<FieldSection> OrderedSections => Sections.OrderBy(s => s.Order).ThenBy(s => s.Label, StringComparer.Ordinal);
}

/// <summary>The roles and rules that decide whether a field is shown or editable for a person on a contract. Pure: the caller supplies the facts.</summary>
public sealed record FieldContext(IReadOnlySet<string> Roles, string Status, string? ContractType, DateOnly Today, bool HasValue);

public static class FieldRules
{
    public static bool AppliesToType(FieldDefinition d, string? contractType)
        => d.ContractTypes.Count == 0 || (contractType is not null && d.ContractTypes.Contains(contractType, StringComparer.OrdinalIgnoreCase));

    /// <summary>In force today: active and inside its effective dates. A field that is not current keeps its stored values but takes no new ones.</summary>
    public static bool IsCurrent(FieldDefinition d, DateOnly today)
        => !d.IsRetired && (d.EffectiveFrom is null || d.EffectiveFrom <= today) && (d.EffectiveTo is null || today <= d.EffectiveTo);

    private static bool RoleOk(IReadOnlyList<string> allowed, IReadOnlySet<string> roles) => allowed.Count == 0 || allowed.Any(roles.Contains);
    private static bool StateOk(IReadOnlyList<string> allowed, string status) => allowed.Count == 0 || allowed.Contains(status, StringComparer.Ordinal);

    /// <summary>Shown on the contract. A value already stored stays visible when the field has since been retired or no longer applies to the contract's type.</summary>
    public static bool Visible(FieldDefinition d, FieldContext c)
        => (c.HasValue || (IsCurrent(d, c.Today) && AppliesToType(d, c.ContractType))) && RoleOk(d.VisibleToRoles, c.Roles) && StateOk(d.VisibleInStates, c.Status);

    /// <summary>Open to change now. Calculated fields never are.</summary>
    public static bool Editable(FieldDefinition d, FieldContext c)
        => !d.IsCalculated && IsCurrent(d, c.Today) && AppliesToType(d, c.ContractType) && Visible(d, c) && RoleOk(d.EditableByRoles, c.Roles) && StateOk(d.EditableInStates, c.Status);
}

/// <summary>The core contract fields, by stable key. Custom fields may not reuse these keys; list views, exports and calculations refer to them.</summary>
/// <remarks>Labels and groups may carry <c>[[term]]</c> tokens; whoever shows one expands it with the organisation's terminology (the register catalogue and the exports do).</remarks>
public sealed record CoreField(string Key, string Label, string Type, bool Sortable, bool Filterable, string Group)
{
    public bool IsNumeric => FieldTypes.IsNumeric(Type);
    public bool IsDate => Type is FieldTypes.Date or FieldTypes.DateTime;
}

public static class CoreFields
{
    public static readonly IReadOnlyList<CoreField> All =
    [
        new("agreementReference", "[[contract.reference]]", FieldTypes.Text, true, true, "[[field.group.identity]]"),
        new("contractTitle", "[[contract]] Title", FieldTypes.Text, true, true, "[[field.group.identity]]"),
        new("description", "[[field.description]]", FieldTypes.LongText, false, false, "[[field.group.identity]]"),
        new("supplier", "[[supplier]]", FieldTypes.Text, true, true, "[[supplier]]"),
        new("supplierVendorNumber", "[[supplier.number]]", FieldTypes.Text, true, true, "[[supplier]]"),
        new("supplierAbn", "[[supplier.tax.id]]", FieldTypes.Text, true, true, "[[supplier]]"),
        new("supplierType", "[[supplier]] Type", FieldTypes.SingleChoice, true, true, "[[supplier]]"),
        new("category", "[[category]]", FieldTypes.Lookup, true, true, "[[field.group.ownership]]"),
        new("owner", "[[field.owner]]", FieldTypes.Person, false, true, "[[field.group.ownership]]"),
        new("status", "[[field.status]]", FieldTypes.SingleChoice, true, true, "[[field.group.lifecycle]]"),
        new("legalReviewStatus", "[[legal.review]] Status", FieldTypes.SingleChoice, false, true, "[[field.group.lifecycle]]"),
        new("classification", "[[field.classification]]", FieldTypes.SingleChoice, true, true, "[[field.group.governance]]"),
        new("contractType", "[[contract.type]]", FieldTypes.SingleChoice, true, true, "[[field.group.classification]]"),
        new("agreementType", "[[agreement]] Type", FieldTypes.SingleChoice, true, true, "[[field.group.classification]]"),
        new("riskLevel", "[[field.risk.level]]", FieldTypes.SingleChoice, true, true, "[[field.group.classification]]"),
        new("functionCode", "[[field.function]]", FieldTypes.SingleChoice, true, true, "[[field.group.classification]]"),
        new("legalEntity", "[[legal.entity]]", FieldTypes.Lookup, true, true, "[[field.group.structure]]"),
        new("businessUnit", "[[business.unit]]", FieldTypes.Lookup, true, true, "[[field.group.structure]]"),
        new("site", "[[site]]", FieldTypes.Lookup, true, true, "[[field.group.structure]]"),
        new("currencyCode", "[[field.currency]]", FieldTypes.Text, true, true, "[[field.group.commercial]]"),
        new("estimatedValue", "[[field.estimated.value]]", FieldTypes.Currency, true, true, "[[field.group.commercial]]"),
        new("extensionType", "[[field.extension.type]]", FieldTypes.SingleChoice, true, true, "[[field.group.commercial]]"),
        new("evergreen", "[[field.evergreen]]", FieldTypes.Boolean, false, true, "[[field.group.commercial]]"),
        new("noticePeriodValue", "[[field.notice.period]]", FieldTypes.Integer, true, true, "[[field.group.commercial]]"),
        new("noticePeriodUnit", "[[field.notice.period]] Unit", FieldTypes.SingleChoice, false, false, "[[field.group.commercial]]"),
        new("criticalNotice", "[[field.critical.notice]]", FieldTypes.Boolean, true, true, "[[field.group.commercial]]"),
        new("legalReviewRequired", "[[legal.review]] Required", FieldTypes.Boolean, true, true, "[[field.group.lifecycle]]"),
        new("sapOutlineAgreementNumber", "[[erp.reference]]", FieldTypes.Text, true, true, "[[field.group.commercial]]"),
        new("startDate", "[[field.start.date]]", FieldTypes.Date, true, true, "[[field.group.dates]]"),
        new("effectiveDate", "[[field.effective.date]]", FieldTypes.Date, true, true, "[[field.group.dates]]"),
        new("registeredDate", "[[registered.date]]", FieldTypes.DateTime, true, true, "[[field.group.dates]]"),
        new("reviewDate", "[[field.review.date]]", FieldTypes.Date, true, true, "[[field.group.dates]]"),
        new("endDate", "[[field.end.date]]", FieldTypes.Date, true, true, "[[field.group.dates]]"),
        new("recordLocked", "[[field.locked]]", FieldTypes.Boolean, false, true, "[[field.group.lifecycle]]"),
        new("createdUtc", "[[field.created]]", FieldTypes.DateTime, true, true, "[[field.group.audit]]"),
        new("updatedUtc", "[[field.updated]]", FieldTypes.DateTime, true, true, "[[field.group.audit]]")
    ];

    public static CoreField? Find(string key) => All.FirstOrDefault(f => string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase));
    public static bool IsCore(string key) => Find(key) is not null;
    /// <summary>The core fields that a core field list on a contract is validated against (reference lists a pack may define by these names).</summary>
    public static readonly IReadOnlyList<string> ListBackedKeys = ["contractType", "agreementType", "riskLevel", "functionCode", "supplierType", "extensionType"];
    public static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
}
