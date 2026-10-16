using Ecp.Metadata;
using Ecp.SharedKernel;

namespace Ecp.Contracts;

/// <summary>
/// Controlled values for the core fields that hold a code (contract type, agreement type, risk level, function, supplier type). An administrator publishes a lookup list with the same name as the core field
/// (for example <c>riskLevel</c>) in the custom field definitions; from then on a new value must be an active member of the list. A value already stored stays valid, so adding a list never breaks existing
/// records. Without a list the field takes any code (up to its length limit), and the Create page offers the codes already in use as suggestions.
/// </summary>
public interface ICoreFieldLists
{
    /// <summary>The core fields that have a list for the organisation, with the members that can be chosen now (active members only).</summary>
    Task<IReadOnlyDictionary<string, IReadOnlyList<FieldChoice>>> ForOrganisationAsync(Guid organisationId, CancellationToken ct = default);
    /// <summary>Refuses a value that is not an active member of the field's list. <paramref name="unchanged"/> are values the record already holds. Returns the member's canonical code.</summary>
    Task<string?> CheckAsync(Guid organisationId, string key, string? value, string? unchanged, CancellationToken ct = default);
}

internal sealed class CoreFieldLists(ICustomFieldCatalogue catalogue) : ICoreFieldLists
{
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<FieldChoice>>> ForOrganisationAsync(Guid organisationId, CancellationToken ct = default)
    {
        var set = await catalogue.PublishedAsync(organisationId, ct);
        var result = new Dictionary<string, IReadOnlyList<FieldChoice>>(StringComparer.Ordinal);
        foreach (var key in CoreFields.ListBackedKeys)
            if (set.FindList(key) is { } list) result[key] = list.Members.Where(m => m.Active).ToList();
        return result;
    }

    public async Task<string?> CheckAsync(Guid organisationId, string key, string? value, string? unchanged, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value.Trim();
        if (unchanged is not null && string.Equals(unchanged, v, StringComparison.Ordinal)) return v;
        var set = await catalogue.PublishedAsync(organisationId, ct);
        if (set.FindList(key) is not { } list) return v;
        var member = list.Members.FirstOrDefault(m => m.Active && (string.Equals(m.Code, v, StringComparison.OrdinalIgnoreCase) || string.Equals(m.Label, v, StringComparison.OrdinalIgnoreCase)));
        return member?.Code ?? throw new DomainException("not_in_list", $"'{v}' is not one of the values allowed for {CoreFields.Find(key)?.Label ?? key}.");
    }
}
