using System.Globalization;

namespace Ecp.SharedKernel.Localisation;

/// <summary>
/// Installation-level fall-backs for the places where no organisation exists (a diagnostic, the first page of setup). They are optional and ship empty: no country, locale, currency or zone is assumed
/// (docs/REGIONAL_SETTINGS.md). An organisation's own regional profile decides everything a person reads about that organisation. <see cref="SupportedCultures"/> are the languages the page text is translated into,
/// not the locales organisations may choose.
/// </summary>
public sealed class LocalisationOptions
{
    public string DefaultCulture { get; set; } = "";
    public string DefaultTimeZoneId { get; set; } = "";
    public string DefaultCurrencyCode { get; set; } = "";
    public List<string> SupportedCultures { get; set; } = ["en"];

    /// <summary>The culture the page text falls back to when a person has chosen none. A language, not a regional setting.</summary>
    public string FallbackLanguage => "en";

    public void Validate()
    {
        if (!string.IsNullOrWhiteSpace(DefaultCulture) && !LocaleFormatter.TryGetCulture(DefaultCulture, out _)) throw new DomainException("invalid_culture", $"Culture '{DefaultCulture}' was not recognised.");
        if (!string.IsNullOrWhiteSpace(DefaultTimeZoneId) && !TimeZones.TryFind(DefaultTimeZoneId, out _)) throw new DomainException("invalid_time_zone", $"Time zone '{DefaultTimeZoneId}' was not recognised.");
        if (!string.IsNullOrWhiteSpace(DefaultCurrencyCode)) LocaleFormatter.ValidateCurrency(DefaultCurrencyCode);
        if (SupportedCultures.Count == 0) throw new DomainException("invalid_culture", "At least one supported language is needed.");
        if (!string.IsNullOrWhiteSpace(DefaultCulture) && !SupportedCultures.Contains(DefaultCulture, StringComparer.OrdinalIgnoreCase))
            throw new DomainException("invalid_culture", "The supported cultures must include the default culture.");
        foreach (var c in SupportedCultures)
            if (!LocaleFormatter.TryGetCulture(c, out _)) throw new DomainException("invalid_culture", $"Culture '{c}' was not recognised.");
    }

    /// <summary>True when the installation names all three fall-back values.</summary>
    public bool HasInstallationDefaults => !string.IsNullOrWhiteSpace(DefaultCulture) && !string.IsNullOrWhiteSpace(DefaultTimeZoneId) && !string.IsNullOrWhiteSpace(DefaultCurrencyCode);
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
    private readonly string? _defaultCurrency;
    private readonly TimeDisplay _timeDisplay;

    /// <summary>A locale of <c>""</c> is the invariant culture (the machine-safe form a diagnostic uses); a null default currency means amounts must name their own.</summary>
    public LocaleFormatter(string cultureName, string timeZoneId, string? defaultCurrencyCode, TimeDisplay timeDisplay = TimeDisplay.Locale)
    {
        if (string.IsNullOrWhiteSpace(cultureName)) _culture = CultureInfo.InvariantCulture;
        else if (!TryGetCulture(cultureName, out _culture)) throw new DomainException("invalid_culture", $"Culture '{cultureName}' was not recognised.");
        _zone = TimeZones.Find(timeZoneId);
        _defaultCurrency = string.IsNullOrWhiteSpace(defaultCurrencyCode) ? null : ValidateCurrency(defaultCurrencyCode);
        _timeDisplay = timeDisplay;
    }

    /// <summary>Invariant culture, UTC, no default currency: for diagnostics and for output no organisation owns.</summary>
    public static LocaleFormatter Invariant { get; } = new("", "UTC", null);

    public CultureInfo Culture => _culture;
    public TimeZoneInfo TimeZone => _zone;
    public string? DefaultCurrency => _defaultCurrency;

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

    /// <summary>Short date in the culture (day-first, month-first or year-first as the locale says; the invariant culture gives ISO 8601).</summary>
    public string Date(DateOnly date) => _culture.Name.Length == 0 ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : date.ToString("d", _culture);

    /// <summary>The calendar date of a UTC instant in the organisation's zone.</summary>
    public string Date(DateTime utc) => Date(TimeZones.LocalDate(Guard.Utc(utc, nameof(utc)), _zone));

    /// <summary>A UTC instant as local date and time in the organisation's zone, with the offset so it is never ambiguous.</summary>
    public string DateTime(DateTime utc)
    {
        var local = TimeZones.ToLocal(utc, _zone);
        var offset = _zone.GetUtcOffset(Guard.Utc(utc, nameof(utc)));
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        var date = _culture.Name.Length == 0 ? local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : local.ToString("d", _culture);
        var time = _timeDisplay switch
        {
            TimeDisplay.TwentyFourHour => local.ToString("HH:mm", CultureInfo.InvariantCulture),
            TimeDisplay.TwelveHour => local.ToString("h:mm tt", _culture.Name.Length == 0 ? CultureInfo.InvariantCulture : _culture),
            _ => _culture.Name.Length == 0 ? local.ToString("HH:mm", CultureInfo.InvariantCulture) : local.ToString("t", _culture)
        };
        return string.Create(CultureInfo.InvariantCulture, $"{date} {time} (UTC{sign}{offset.Duration():hh\\:mm})");
    }

    public string Number(decimal value, int decimals = 2) => value.ToString("N" + decimals.ToString(CultureInfo.InvariantCulture), _culture);

    /// <summary>
    /// Money in the given currency, or the organisation's default currency when none is named. There is no fall-back currency: with neither, the amount cannot be shown, because "100" without a currency is not an amount.
    /// The culture's own currency uses its symbol; any other currency is shown with its ISO code (a code followed by the amount) so a symbol such as "$" is never ambiguous in a contract register.
    /// </summary>
    public string Money(decimal amount, string? currencyCode = null)
    {
        var code = currencyCode is null ? _defaultCurrency ?? throw new DomainException("missing_currency", "An amount needs a currency.") : ValidateCurrency(currencyCode);
        var digits = MinorUnitsFor(code);
        var number = amount.ToString("N" + digits.ToString(CultureInfo.InvariantCulture), _culture);
        return CultureCurrency(_culture) == code
            ? (amount < 0 ? "-" : "") + _culture.NumberFormat.CurrencySymbol + Math.Abs(amount).ToString("N" + digits.ToString(CultureInfo.InvariantCulture), _culture)
            : string.Concat(code, "\u00A0", number);
    }

    /// <summary>The amount in the locale's number format followed by the ISO code, never a symbol: for exports and reports a person reads but a spreadsheet user may sort.</summary>
    public string MoneyWithCode(decimal amount, string currencyCode)
    {
        var code = ValidateCurrency(currencyCode);
        return amount.ToString("N" + MinorUnitsFor(code).ToString(CultureInfo.InvariantCulture), _culture) + "\u00A0" + code;
    }

    private static string? CultureCurrency(CultureInfo culture)
    {
        try { return culture.IsNeutralCulture || culture.Name.Length == 0 ? null : new RegionInfo(culture.Name).ISOCurrencySymbol; }
        catch (ArgumentException) { return null; }
    }
}
