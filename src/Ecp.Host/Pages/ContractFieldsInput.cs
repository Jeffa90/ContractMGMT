using System.Globalization;
using Ecp.Contracts;
using Ecp.Metadata;
using Ecp.Tenancy;

namespace Ecp.Host.Pages;

/// <summary>The core fields of a contract as a form posts them (Create and Edit share it). Everything is text or a simple type; the contract rules decide what is acceptable.</summary>
public sealed class ContractFieldsInput
{
    public string? ContractType { get; set; }
    public string? AgreementType { get; set; }
    public string? RiskLevel { get; set; }
    public string? FunctionCode { get; set; }
    public string? SapOutlineAgreementNumber { get; set; }
    public string? ExtensionType { get; set; }
    public bool LegalReviewRequired { get; set; }
    public bool CriticalNotice { get; set; }
    public string? NoticePeriodValue { get; set; }
    public string? NoticePeriodUnit { get; set; }
    public string? CurrencyCode { get; set; }
    public string? EstimatedValue { get; set; }
    public Guid? BusinessUnitId { get; set; }
    public Guid? SiteId { get; set; }
    public Guid? LegalEntityId { get; set; }
    public string? StartDate { get; set; }
    public string? EffectiveDate { get; set; }
    public string? ReviewDate { get; set; }
    public string? EndDate { get; set; }

    /// <summary>The raw changes for the contract service. Blank means "clear".</summary>
    public Dictionary<string, string?> ToChanges() => new(StringComparer.Ordinal)
    {
        ["contractType"] = Blank(ContractType), ["agreementType"] = Blank(AgreementType), ["riskLevel"] = Blank(RiskLevel), ["functionCode"] = Blank(FunctionCode),
        ["sapOutlineAgreementNumber"] = Blank(SapOutlineAgreementNumber), ["extensionType"] = Blank(ExtensionType), ["legalReviewRequired"] = LegalReviewRequired ? "true" : "false",
        ["criticalNotice"] = CriticalNotice ? "true" : "false", ["noticePeriodValue"] = Blank(NoticePeriodValue), ["noticePeriodUnit"] = Blank(NoticePeriodUnit), ["currencyCode"] = Blank(CurrencyCode),
        ["estimatedValue"] = Blank(EstimatedValue), ["businessUnitId"] = BusinessUnitId?.ToString("D"), ["siteId"] = SiteId?.ToString("D"), ["legalEntityId"] = LegalEntityId?.ToString("D"),
        ["startDate"] = Blank(StartDate), ["effectiveDate"] = Blank(EffectiveDate), ["reviewDate"] = Blank(ReviewDate), ["endDate"] = Blank(EndDate)
    };

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    public static ContractFieldsInput From(ContractDetails c) => new()
    {
        ContractType = c.Classifiers?.ContractType, AgreementType = c.Classifiers?.AgreementType, RiskLevel = c.Classifiers?.RiskLevel, FunctionCode = c.Classifiers?.FunctionCode,
        SapOutlineAgreementNumber = c.Classifiers?.SapOutlineAgreementNumber, ExtensionType = c.ExtensionType, LegalReviewRequired = c.LegalReviewRequired,
        CriticalNotice = c.Reminders?.CriticalNotice ?? false, NoticePeriodValue = c.Classifiers?.NoticePeriodValue?.ToString(CultureInfo.InvariantCulture), NoticePeriodUnit = c.Classifiers?.NoticePeriodUnit,
        CurrencyCode = c.CurrencyCode, EstimatedValue = c.EstimatedValue?.ToString(CultureInfo.InvariantCulture), BusinessUnitId = c.BusinessUnitId, SiteId = c.SiteId, LegalEntityId = c.Classifiers?.LegalEntityId,
        StartDate = c.StartDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), EffectiveDate = c.EffectiveDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        ReviewDate = c.ReviewDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), EndDate = c.EndDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
    };
}

/// <summary>What the core field inputs offer to choose from.</summary>
public sealed record ContractFieldOptions(
    IReadOnlyList<BusinessUnitDto> BusinessUnits, IReadOnlyList<SiteDto> Sites, IReadOnlyList<LegalEntityDto> LegalEntities, IReadOnlyDictionary<string, IReadOnlyList<FieldChoice>> Lists,
    IReadOnlyList<string> ExtensionTypes)
{
    public static ContractFieldOptions Empty { get; } = new([], [], [], new Dictionary<string, IReadOnlyList<FieldChoice>>(), []);
    public IReadOnlyList<FieldChoice>? ListFor(string key) => Lists.TryGetValue(key, out var l) ? l : null;
}

/// <summary>Reads the configured field values out of a posted form. Each editable field posts a marker (<c>cfp.key</c>) and its values (<c>cf.key</c>), so an emptied multiple-choice field clears and a field not on the form is left alone.</summary>
public static class CustomFieldFormReader
{
    public const string ValuePrefix = "cf.", MarkerPrefix = "cfp.";

    public static CustomFieldInput Read(IFormCollection form)
    {
        var input = new CustomFieldInput();
        foreach (var key in form.Keys.Where(k => k.StartsWith(MarkerPrefix, StringComparison.Ordinal)))
        {
            var field = key[MarkerPrefix.Length..];
            if (field.Length is < 2 or > 63) continue;
            var values = form.TryGetValue(ValuePrefix + field, out var v) ? v.Select(x => (string?)x).ToList() : [];
            input[field] = values;
        }
        return input;
    }
}

public sealed record ContractFieldsView(ContractFieldsInput Input, ContractFieldOptions Options);

public static class ContractFieldOptionsLoader
{
    public static async Task<ContractFieldOptions> LoadAsync(Guid organisationId, IOrganisationStructureService organisations, ICoreFieldLists lists, CancellationToken ct)
    {
        var structure = await organisations.GetAsync(organisationId, ct);
        return new ContractFieldOptions(structure.BusinessUnits, structure.Sites, structure.LegalEntities, await lists.ForOrganisationAsync(organisationId, ct), [Ecp.Contracts.Domain.ExtensionTypes.Evergreen, "Fixed term", "Rolling", "Option to extend"]);
    }
}
