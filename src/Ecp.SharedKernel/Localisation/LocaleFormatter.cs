using System.Globalization;

namespace Ecp.SharedKernel.Localisation;

/// <summary>Locale configuration for display. The reference configuration uses en-AU; other cultures are configuration, not code.</summary>
public sealed class LocalisationOptions
{
    public string DefaultCulture { get; set; } = "en-AU";
    public string DefaultTimeZoneId { get; set; } = "Australia/Brisbane";
    public string DefaultCurrencyCode { get; set; } = "AUD";
    public List<string> SupportedCultures { get; set; } = ["en-AU", "en-US"];

    public void Validate()
    {
        if (!LocaleFormatter.TryGetCulture(DefaultCulture, out _)) throw new DomainException("invalid_culture", $"Culture '{DefaultCulture}' was not recognised.");
        if (!TimeZones.TryFind(DefaultTimeZoneId, out _)) throw new DomainException("invalid_time_zone", $"Time zone '{DefaultTimeZoneId}' was not recognised.");
        LocaleFormatter.ValidateCurrency(DefaultCurrencyCode);
        if (SupportedCultures.Count == 0 || !SupportedCultures.Contains(DefaultCulture, StringComparer.OrdinalIgnoreCase))
            throw new DomainException("invalid_culture", "The supported cultures must include the default culture.");
        foreach (var c in SupportedCultures)
            if (!LocaleFormatter.TryGetCulture(c, out _)) throw new DomainException("invalid_culture", $"Culture '{c}' was not recognised.");
    }
}

/// <summary>
/// Formats dates, instants and money for one organisation's culture and time zone. Domain values stay invariant (UTC instants,
/// ISO 4217 codes, <see cref="DateOnly"/>); only presentation uses the culture.
/// </summary>
public sealed class LocaleFormatter
{
    // ISO 4217 minor units that are not two. Every other currency uses two decimal places.
    private static readonly Dictionary<string, int> MinorUnits = new(StringComparer.Ordinal)
    {
        ["JPY"] = 0, ["KRW"] = 0, ["VND"] = 0, ["CLP"] = 0, ["ISK"] = 0, ["UGX"] = 0, ["XAF"] = 0, ["XOF"] = 0, ["XPF"] = 0,
        ["BHD"] = 3, ["KWD"] = 3, ["OMR"] = 3, ["JOD"] = 3, ["TND"] = 3
    };

    private readonly CultureInfo _culture;
    private readonly TimeZoneInfo _zone;
    private readonly string _defaultCurrency;

    public LocaleFormatter(string cultureName, string timeZoneId, string defaultCurrencyCode)
    {
        if (!TryGetCulture(cultureName, out _culture)) throw new DomainException("invalid_culture", $"Culture '{cultureName}' was not recognised.");
        _zone = TimeZones.Find(timeZoneId);
        _defaultCurrency = ValidateCurrency(defaultCurrencyCode);
    }

    public CultureInfo Culture => _culture;
    public TimeZoneInfo TimeZone => _zone;

    public static bool TryGetCulture(string? name, out CultureInfo culture)
    {
        culture = CultureInfo.InvariantCulture;
        if (string.IsNullOrWhiteSpace(name)) return false;
        try { culture = CultureInfo.GetCultureInfo(name.Trim(), predefinedOnly: true); return !string.IsNullOrEmpty(culture.Name); }
        catch (CultureNotFoundException) { return false; }
    }

    public static string ValidateCurrency(string? code)
    {
        var c = (code ?? "").Trim().ToUpperInvariant();
        if (c.Length != 3 || c.Any(ch => ch is < 'A' or > 'Z')) throw new DomainException("invalid_currency", "Currency must be a three-letter ISO 4217 code.");
        return c;
    }

    public static int MinorUnitsFor(string currencyCode) => MinorUnits.TryGetValue(ValidateCurrency(currencyCode), out var n) ? n : 2;

    /// <summary>Short date in the culture (en-AU: 16/10/2026).</summary>
    public string Date(DateOnly date) => date.ToString("d", _culture);

    /// <summary>A UTC instant as local date and time in the organisation's zone, with the offset so it is never ambiguous.</summary>
    public string DateTime(DateTime utc)
    {
        var local = TimeZones.ToLocal(utc, _zone);
        var offset = _zone.GetUtcOffset(Guard.Utc(utc, nameof(utc)));
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        return string.Create(_culture, $"{local:g} (UTC{sign}{offset.Duration():hh\\:mm})");
    }

    public string Number(decimal value, int decimals = 2) => value.ToString("N" + decimals.ToString(CultureInfo.InvariantCulture), _culture);

    /// <summary>
    /// Money in the given currency (organisation default when null). The culture's own currency uses its symbol (AUD in en-AU: $1,234.50);
    /// any other currency is shown with its ISO code (USD 1,234.50) so a symbol such as "$" is never ambiguous in a contract register.
    /// </summary>
    public string Money(decimal amount, string? currencyCode = null)
    {
        var code = currencyCode is null ? _defaultCurrency : ValidateCurrency(currencyCode);
        var digits = MinorUnitsFor(code);
        var number = amount.ToString("N" + digits.ToString(CultureInfo.InvariantCulture), _culture);
        return CultureCurrency(_culture) == code
            ? (amount < 0 ? "-" : "") + _culture.NumberFormat.CurrencySymbol + Math.Abs(amount).ToString("N" + digits.ToString(CultureInfo.InvariantCulture), _culture)
            : string.Concat(code, " ", number);
    }

    private static string? CultureCurrency(CultureInfo culture)
    {
        try { return culture.IsNeutralCulture || culture.Name.Length == 0 ? null : new RegionInfo(culture.Name).ISOCurrencySymbol; }
        catch (ArgumentException) { return null; }
    }
}
