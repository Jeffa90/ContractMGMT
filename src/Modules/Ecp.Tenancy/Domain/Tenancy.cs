using System.Globalization;
using System.Text.RegularExpressions;
using Ecp.SharedKernel;

namespace Ecp.Tenancy.Domain;

public enum TenantStatus { Active, Suspended }
public enum TenancyMode { SingleOrganisation, MultiOrganisation, MultiTenant }

/// <summary>A tenant: the hard isolation boundary (ADR-0003). Installation-level registry entry; not itself tenant-owned.</summary>
public sealed class Tenant
{
    private Tenant() { }

    public Guid Id { get; private set; }
    public string Name { get; private set; } = "";
    public TenantStatus Status { get; private set; }
    public TenancyMode TenancyMode { get; private set; }
    public EcpMode ConfigurationMode { get; private set; }
    public DateTime CreatedUtc { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public static Tenant Create(string name, TenancyMode tenancyMode, EcpMode mode, DateTime nowUtc) => new()
    {
        Id = Ids.New(), Name = Guard.NotBlank(name, "Tenant name", 200), Status = TenantStatus.Active,
        TenancyMode = tenancyMode, ConfigurationMode = mode, CreatedUtc = Guard.Utc(nowUtc, nameof(nowUtc))
    };

    public void Suspend() => Status = TenantStatus.Suspended;
    public void Reactivate() => Status = TenantStatus.Active;
}

/// <summary>
/// Agreement Reference numbering for an organisation (decision D3): prefix + separator + zero-padded sequence.
/// Each organisation has its own prefix, unique within the tenant, so references cannot collide across organisations.
/// </summary>
public sealed record ReferenceNumberingRule(string Prefix, string Separator = "-", int Digits = 6)
{
    private static readonly Regex PrefixPattern = new("^[A-Z][A-Z0-9]{1,15}$", RegexOptions.Compiled | RegexOptions.NonBacktracking);

    public static ReferenceNumberingRule Create(string? prefix, string separator = "-", int digits = 6)
    {
        var p = Guard.NotBlank(prefix, "Reference prefix", 16).ToUpperInvariant();
        if (!PrefixPattern.IsMatch(p))
            throw new DomainException("invalid_prefix", "Reference prefix must be 2 to 16 letters or digits starting with a letter.");
        if (separator is not ("-" or "" or "/" or "."))
            throw new DomainException("invalid_separator", "Reference separator must be '-', '/', '.' or empty.");
        if (digits is < 3 or > 12)
            throw new DomainException("invalid_digits", "Reference sequence must have between 3 and 12 digits.");
        return new ReferenceNumberingRule(p, separator, digits);
    }

    public string Format(long sequence)
    {
        if (sequence < 1) throw new DomainException("invalid_sequence", "Sequence must be positive.");
        var number = sequence.ToString(CultureInfo.InvariantCulture);
        if (number.Length > Digits) throw new DomainException("sequence_overflow", "The reference sequence has exceeded its configured digits.");
        return Prefix + Separator + number.PadLeft(Digits, '0');
    }
}

/// <summary>An organisation within a tenant: the operating and policy scope (mandatory on contracts).</summary>
public sealed class Organisation : ITenantOwned
{
    private Organisation() { }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public string Name { get; private set; } = "";
    public string TimeZoneId { get; private set; } = "";
    public string CultureCode { get; private set; } = "";
    public string DefaultCurrencyCode { get; private set; } = "";
    public string ReferencePrefix { get; private set; } = "";
    public string ReferenceSeparator { get; private set; } = "-";
    public int ReferenceDigits { get; private set; } = 6;
    public string? SolutionPackKey { get; private set; }
    public DateTime CreatedUtc { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public ReferenceNumberingRule ReferenceRule => new(ReferencePrefix, ReferenceSeparator, ReferenceDigits);

    /// <summary>Records which solution pack the organisation works from (the pack's stable key). It changes no configuration; published releases and pinned contracts are untouched.</summary>
    public void AssignSolutionPack(string solutionPackKey) => SolutionPackKey = Guard.NotBlank(solutionPackKey, "[[solution.pack|sentence]]", 100);

    /// <summary>
    /// Records the organisation's effective locale, time zone and default currency (the values a published regional profile resolves to). Timestamps stay in UTC and existing amounts keep their own currency;
    /// only how the organisation reads and enters values changes.
    /// </summary>
    public void SetRegional(string timeZoneId, string cultureCode, string defaultCurrencyCode)
    {
        var tz = Guard.NotBlank(timeZoneId, "Time zone", 100);
        try { TimeZoneInfo.FindSystemTimeZoneById(tz); }
        catch (TimeZoneNotFoundException) { throw new DomainException("invalid_time_zone", $"Time zone '{tz}' was not recognised."); }
        var culture = Guard.NotBlank(cultureCode, "Culture", 20);
        try { _ = CultureInfo.GetCultureInfo(culture, predefinedOnly: true); }
        catch (CultureNotFoundException) { throw new DomainException("invalid_culture", $"Culture '{culture}' was not recognised."); }
        var currency = Guard.NotBlank(defaultCurrencyCode, "Default currency", 3).ToUpperInvariant();
        if (!Regex.IsMatch(currency, "^[A-Z]{3}$", RegexOptions.NonBacktracking)) throw new DomainException("invalid_currency", "Default currency must be a three-letter ISO 4217 code.");
        TimeZoneId = tz; CultureCode = culture; DefaultCurrencyCode = currency;
    }

    public static Organisation Create(Guid tenantId, string name, string timeZoneId, string cultureCode, string defaultCurrencyCode,
        ReferenceNumberingRule referenceRule, string? solutionPackKey, DateTime nowUtc)
    {
        Guard.NotEmpty(tenantId, nameof(tenantId));
        var tz = Guard.NotBlank(timeZoneId, "Time zone", 100);
        try { TimeZoneInfo.FindSystemTimeZoneById(tz); }
        catch (TimeZoneNotFoundException) { throw new DomainException("invalid_time_zone", $"Time zone '{tz}' was not recognised."); }
        var culture = Guard.NotBlank(cultureCode, "Culture", 20);
        try { _ = CultureInfo.GetCultureInfo(culture, predefinedOnly: true); }
        catch (CultureNotFoundException) { throw new DomainException("invalid_culture", $"Culture '{culture}' was not recognised."); }
        var currency = Guard.NotBlank(defaultCurrencyCode, "Default currency", 3).ToUpperInvariant();
        if (!Regex.IsMatch(currency, "^[A-Z]{3}$", RegexOptions.NonBacktracking))
            throw new DomainException("invalid_currency", "Default currency must be a three-letter ISO 4217 code.");

        return new Organisation
        {
            Id = Ids.New(), TenantId = tenantId, Name = Guard.NotBlank(name, "[[organisation]] name", 200), TimeZoneId = tz,
            CultureCode = culture, DefaultCurrencyCode = currency, ReferencePrefix = referenceRule.Prefix,
            ReferenceSeparator = referenceRule.Separator, ReferenceDigits = referenceRule.Digits,
            SolutionPackKey = Guard.OptionalBounded(solutionPackKey, "[[solution.pack|sentence]]", 100), CreatedUtc = Guard.Utc(nowUtc, nameof(nowUtc))
        };
    }
}
