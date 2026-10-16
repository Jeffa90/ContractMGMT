using System.Text.RegularExpressions;
using Ecp.SharedKernel;
using Ecp.SharedKernel.Localisation;

namespace Ecp.Tenancy.Domain;

/// <summary>Stable keys are upper-case codes used to refer to structure items from configuration, packs and imports.</summary>
public static partial class StructureKey
{
    public static string Normalise(string? key, string field = "Key")
    {
        var k = Guard.NotBlank(key, field, 50).ToUpperInvariant();
        if (!Pattern().IsMatch(k)) throw new DomainException("invalid_key", $"{field} may contain only letters, digits, '-' and '_' and must start with a letter or digit.");
        return k;
    }

    [GeneratedRegex(@"^[A-Z0-9][A-Z0-9_\-]*$", RegexOptions.NonBacktracking)]
    private static partial Regex Pattern();
}

/// <summary>Common shape of organisation structure items. All are tenant-owned and belong to one organisation.</summary>
public abstract class StructureItem : ITenantOwned
{
    public Guid Id { get; protected set; }
    public Guid TenantId { get; protected set; }
    public Guid OrganisationId { get; protected set; }
    public string Key { get; protected set; } = "";
    public string Name { get; protected set; } = "";
    public bool IsActive { get; protected set; } = true;
    public DateTime CreatedUtc { get; protected set; }
    public byte[] RowVersion { get; protected set; } = [];

    public void Deactivate() => IsActive = false;
    public void Activate() => IsActive = true;
}

public sealed class LegalEntity : StructureItem
{
    private LegalEntity() { }
    public string? RegistrationNumber { get; private set; }

    public static LegalEntity Create(Guid tenantId, Guid organisationId, string key, string name, string? registrationNumber, DateTime nowUtc) => new()
    {
        Id = Ids.New(), TenantId = Guard.NotEmpty(tenantId, nameof(tenantId)), OrganisationId = Guard.NotEmpty(organisationId, nameof(organisationId)),
        Key = StructureKey.Normalise(key), Name = Guard.NotBlank(name, "Legal entity name", 200),
        RegistrationNumber = Guard.OptionalBounded(registrationNumber, "Registration number", 50), CreatedUtc = Guard.Utc(nowUtc, nameof(nowUtc))
    };

    public void Update(string name, string? registrationNumber)
    {
        Name = Guard.NotBlank(name, "Legal entity name", 200);
        RegistrationNumber = Guard.OptionalBounded(registrationNumber, "Registration number", 50);
    }
}

public sealed class Division : StructureItem
{
    private Division() { }

    public static Division Create(Guid tenantId, Guid organisationId, string key, string name, DateTime nowUtc) => new()
    {
        Id = Ids.New(), TenantId = Guard.NotEmpty(tenantId, nameof(tenantId)), OrganisationId = Guard.NotEmpty(organisationId, nameof(organisationId)),
        Key = StructureKey.Normalise(key), Name = Guard.NotBlank(name, "Division name", 200), CreatedUtc = Guard.Utc(nowUtc, nameof(nowUtc))
    };

    public void Rename(string name) => Name = Guard.NotBlank(name, "Division name", 200);
}

public sealed class BusinessUnit : StructureItem
{
    private BusinessUnit() { }
    public Guid? DivisionId { get; private set; }

    public static BusinessUnit Create(Guid tenantId, Guid organisationId, string key, string name, Guid? divisionId, DateTime nowUtc) => new()
    {
        Id = Ids.New(), TenantId = Guard.NotEmpty(tenantId, nameof(tenantId)), OrganisationId = Guard.NotEmpty(organisationId, nameof(organisationId)),
        Key = StructureKey.Normalise(key), Name = Guard.NotBlank(name, "Business unit name", 200), DivisionId = divisionId, CreatedUtc = Guard.Utc(nowUtc, nameof(nowUtc))
    };

    public void Update(string name, Guid? divisionId) { Name = Guard.NotBlank(name, "Business unit name", 200); DivisionId = divisionId; }
}

public sealed class Site : StructureItem
{
    private Site() { }
    public string? TimeZoneId { get; private set; }

    /// <summary>The site <see cref="StructureItem.Key"/> is its code (stable, unique per organisation).</summary>
    public static Site Create(Guid tenantId, Guid organisationId, string code, string name, string? timeZoneId, DateTime nowUtc) => new()
    {
        Id = Ids.New(), TenantId = Guard.NotEmpty(tenantId, nameof(tenantId)), OrganisationId = Guard.NotEmpty(organisationId, nameof(organisationId)),
        Key = StructureKey.Normalise(code, "Site code"), Name = Guard.NotBlank(name, "Site name", 200), TimeZoneId = NormaliseZone(timeZoneId),
        CreatedUtc = Guard.Utc(nowUtc, nameof(nowUtc))
    };

    public void Update(string name, string? timeZoneId) { Name = Guard.NotBlank(name, "Site name", 200); TimeZoneId = NormaliseZone(timeZoneId); }

    private static string? NormaliseZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        TimeZones.Find(id); // throws invalid_time_zone
        return id.Trim();
    }
}
