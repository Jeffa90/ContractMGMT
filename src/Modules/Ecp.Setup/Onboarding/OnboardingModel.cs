using System.Text.Json;
using System.Text.Json.Serialization;
using Ecp.Identity.Domain;
using Ecp.SharedKernel;

namespace Ecp.Setup.Onboarding;

public sealed record StructureEntry(string Key, string Name, string? ParentKey = null, string? TimeZoneId = null, string? RegistrationNumber = null);
public sealed record CategoryEntry(string Code, string Name, string? ParentCode = null);
public sealed record ContractTypeEntry(string Code, string Name);
public sealed record OwnerEntry(string CategoryCode, string Email, bool IsPrimary = true);

public sealed record ViewerEntry(string Email, ViewerKind ViewerKind, IReadOnlyList<string> CategoryCodes, IReadOnlyList<string> BusinessUnitKeys, IReadOnlyList<string> SiteKeys, string Ceiling);

public sealed record MappingEntry(string ProviderKey, string GroupKey, string RoleCode, ViewerKind? ViewerKind, IReadOnlyList<string> CategoryCodes,
    IReadOnlyList<string> BusinessUnitKeys, IReadOnlyList<string> SiteKeys, string Ceiling);

public sealed record StructureSection(IReadOnlyList<StructureEntry> LegalEntities, IReadOnlyList<StructureEntry> Divisions, IReadOnlyList<StructureEntry> BusinessUnits, IReadOnlyList<StructureEntry> Sites)
{
    public static readonly StructureSection Empty = new([], [], [], []);
    public int Count => LegalEntities.Count + Divisions.Count + BusinessUnits.Count + Sites.Count;
}

/// <summary>
/// The content of an organisation onboarding release (schema version 1). It names things by business key and e-mail address, never by internal
/// identifier, so the same document can be previewed, validated, published and rolled back against any organisation. It is data: nothing in it is executed.
/// </summary>
public sealed record OnboardingDocument(
    int SchemaVersion, string? PackId, StructureSection Structure, IReadOnlyList<CategoryEntry> Categories, IReadOnlyList<ContractTypeEntry> ContractTypes,
    IReadOnlyList<OwnerEntry> CategoryOwners, IReadOnlyList<ViewerEntry> ViewerScopes, IReadOnlyList<MappingEntry> GroupMappings)
{
    public const int CurrentSchema = 1;

    public static OnboardingDocument Empty { get; } = new(CurrentSchema, null, StructureSection.Empty, [], [], [], [], []);

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static OnboardingDocument Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Trim() == "{}") return Empty;
        try
        {
            var doc = JsonSerializer.Deserialize<OnboardingDocument>(json, Options) ?? throw new DomainException("invalid_json", "The onboarding document is empty.");
            return Normalise(doc);
        }
        catch (JsonException ex) { throw new DomainException("invalid_json", "The onboarding document is not valid: " + ex.Message.Split('.')[0] + "."); }
    }

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    // Missing lists in older or hand-written documents become empty lists, so the rest of the code never meets null.
    private static OnboardingDocument Normalise(OnboardingDocument d) => d with
    {
        Structure = d.Structure is null ? StructureSection.Empty : new StructureSection(d.Structure.LegalEntities ?? [], d.Structure.Divisions ?? [], d.Structure.BusinessUnits ?? [], d.Structure.Sites ?? []),
        Categories = d.Categories ?? [], ContractTypes = d.ContractTypes ?? [], CategoryOwners = d.CategoryOwners ?? [],
        ViewerScopes = (d.ViewerScopes ?? []).Select(v => v with { CategoryCodes = v.CategoryCodes ?? [], BusinessUnitKeys = v.BusinessUnitKeys ?? [], SiteKeys = v.SiteKeys ?? [] }).ToList(),
        GroupMappings = (d.GroupMappings ?? []).Select(m => m with { CategoryCodes = m.CategoryCodes ?? [], BusinessUnitKeys = m.BusinessUnitKeys ?? [], SiteKeys = m.SiteKeys ?? [] }).ToList(),
    };

    public static string Key(string? value) => (value ?? "").Trim().ToUpperInvariant();
}

/// <summary>Step identifiers of the onboarding wizard, in presentation order.</summary>
public static class OnboardingSteps
{
    public static readonly IReadOnlyList<(string Id, string Title)> All =
    [
        ("pack", "Solution pack and classification"), ("structure", "Organisation structure"), ("categories", "Categories (taxonomy)"), ("types", "Contract types"),
        ("owners", "Category owners"), ("viewers", "Viewer scopes"), ("mappings", "Identity group mappings"), ("review", "Review, simulate and publish"),
    ];
}
