using System.Globalization;
using System.Text.RegularExpressions;

namespace Ecp.SharedKernel.Localisation;

/// <summary>
/// The platform's closed catalogues of regional codes (docs/REGIONAL_SETTINGS.md). A regional profile names a country or region (ISO 3166-1 alpha-2), locales (BCP 47), currencies (ISO 4217), a time zone (IANA) and
/// a phone region; each code is checked against these catalogues, never against free text. Nothing here is a default: the platform assumes no country, locale, currency or zone.
/// </summary>
public static class RegionalCatalogue
{
    private static readonly Regex LocaleShape = new(@"^[A-Za-z]{2,3}(-[A-Za-z0-9]{2,8}){0,3}$", RegexOptions.Compiled | RegexOptions.NonBacktracking);
    private static readonly Regex CountryShape = new("^[A-Z]{2}$", RegexOptions.Compiled | RegexOptions.NonBacktracking);

    // ISO 4217 alphabetic codes of currencies in circulation (funds and precious-metal codes are not offered). A closed list: a code outside it is refused.
    private static readonly HashSet<string> CurrencyCodes = new(StringComparer.Ordinal)
    {
        "AED","AFN","ALL","AMD","ANG","AOA","ARS","AUD","AWG","AZN","BAM","BBD","BDT","BGN","BHD","BIF","BMD","BND","BOB","BRL","BSD","BTN","BWP","BYN","BZD","CAD","CDF","CHF","CLP","CNY","COP","CRC","CUP","CVE","CZK",
        "DJF","DKK","DOP","DZD","EGP","ERN","ETB","EUR","FJD","FKP","GBP","GEL","GHS","GIP","GMD","GNF","GTQ","GYD","HKD","HNL","HRK","HTG","HUF","IDR","ILS","INR","IQD","IRR","ISK","JMD","JOD","JPY","KES","KGS","KHR",
        "KMF","KPW","KRW","KWD","KYD","KZT","LAK","LBP","LKR","LRD","LSL","LYD","MAD","MDL","MGA","MKD","MMK","MNT","MOP","MRU","MUR","MVR","MWK","MXN","MYR","MZN","NAD","NGN","NIO","NOK","NPR","NZD","OMR","PAB","PEN",
        "PGK","PHP","PKR","PLN","PYG","QAR","RON","RSD","RUB","RWF","SAR","SBD","SCR","SDG","SEK","SGD","SHP","SLE","SOS","SRD","SSP","STN","SVC","SYP","SZL","THB","TJS","TMT","TND","TOP","TRY","TTD","TWD","TZS","UAH",
        "UGX","USD","UYU","UZS","VES","VND","VUV","WST","XAF","XCD","XOF","XPF","YER","ZAR","ZMW","ZWL"
    };

    private static readonly Lazy<IReadOnlyDictionary<string, string>> CountryNames = new(() =>
    {
        var found = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var culture in CultureInfo.GetCultures(CultureTypes.SpecificCultures))
        {
            try
            {
                var region = new RegionInfo(culture.Name);
                if (CountryShape.IsMatch(region.TwoLetterISORegionName) && !found.ContainsKey(region.TwoLetterISORegionName)) found[region.TwoLetterISORegionName] = region.EnglishName;
            }
            catch (ArgumentException) { /* a culture with no region */ }
        }
        return found;
    });

    private static readonly Lazy<IReadOnlyList<string>> Zones = new(() =>
        TimeZoneInfo.GetSystemTimeZones().Select(z => z.Id).Where(id => id.Contains('/') || id == "UTC").Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList());

    /// <summary>ISO 4217 codes the platform offers.</summary>
    public static IReadOnlyCollection<string> Currencies => CurrencyCodes;

    public static bool IsCurrency(string? code) => code is { Length: 3 } && CurrencyCodes.Contains(code);

    /// <summary>Country or region codes (ISO 3166-1 alpha-2) with an English name, from the platform's culture data.</summary>
    public static IReadOnlyDictionary<string, string> Countries => CountryNames.Value;

    public static bool IsCountry(string? code) => code is { Length: 2 } && CountryNames.Value.ContainsKey(code);

    /// <summary>A BCP 47 tag the platform's culture data knows (a language such as <c>en</c> or a language and region such as <c>fr-CA</c>).</summary>
    public static bool IsLocale(string? tag) => tag is { Length: > 0 and <= 35 } && LocaleShape.IsMatch(tag) && LocaleFormatter.TryGetCulture(tag, out _);

    public static bool IsTimeZone(string? id) => id is { Length: > 0 and <= 80 } && TimeZones.TryFind(id, out _);

    /// <summary>The IANA zone identifiers this host knows, for pickers.</summary>
    public static IReadOnlyList<string> TimeZoneIds => Zones.Value;

    /// <summary>Locale tags the host knows, for an input suggestion list. A suggestion is not a default; any tag the catalogue accepts may be typed.</summary>
    public static IReadOnlyList<string> SuggestedLocales => Locales.Value;

    private static readonly Lazy<IReadOnlyList<string>> Locales = new(() =>
        CultureInfo.GetCultures(CultureTypes.SpecificCultures).Select(c => c.Name).Where(n => n.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList());

    /// <summary>A phone region is a country or region code.</summary>
    public static bool IsPhoneRegion(string? code) => IsCountry(code);

    public static string CountryName(string code) => CountryNames.Value.TryGetValue(code, out var n) ? n : code;
}

/// <summary>How a time of day is shown: the locale's own habit, or an explicit 24-hour or 12-hour clock.</summary>
public enum TimeDisplay { Locale, TwentyFourHour, TwelveHour }

/// <summary>
/// How a counterparty identifier is checked. A closed list of structural rules: none of them is a regular expression a customer supplies, and none of them says that an identifier is legally valid
/// or registered anywhere (docs/TAX_IDENTIFIERS.md).
/// </summary>
public static class IdentifierRuleKinds
{
    /// <summary>Any value of 1 to 64 letters, digits, spaces, hyphens, dots and slashes.</summary>
    public const string Free = "free";
    /// <summary>Digits only (spaces and hyphens ignored); parameter <c>length</c> is a number or a range such as 8-9.</summary>
    public const string Digits = "digits";
    /// <summary>Letters and digits (spaces and hyphens ignored); parameter <c>length</c> as above.</summary>
    public const string Alphanumeric = "alphanumeric";
    /// <summary>A mask of <c>#</c> (digit), <c>A</c> (letter) and <c>*</c> (letter or digit); any other character is a separator, for example <c>##-#######</c>.</summary>
    public const string Mask = "mask";

    public static readonly IReadOnlyList<string> All = [Free, Digits, Alphanumeric, Mask];
}

/// <summary>Checksums an identifier rule may add. A closed catalogue; <c>none</c> means the structure alone is checked.</summary>
public static class IdentifierChecksums
{
    public const string None = "none";
    /// <summary>The Luhn check over all digits.</summary>
    public const string Luhn = "luhn";
    /// <summary>The weighted modulus-89 check used by one national business-number scheme. Offered as a validator; a profile switches it on only if it wants it.</summary>
    public const string Mod89Weighted = "mod89-weighted";

    public static readonly IReadOnlyList<string> All = [None, Luhn, Mod89Weighted];
}

/// <summary>Where a counterparty identifier stands. <c>ExternallyVerified</c> and <c>VerificationFailed</c> are reserved for a later registry integration that S10 does not build; nothing sets them.</summary>
public static class IdentifierVerification
{
    public const string NotChecked = "NotChecked";
    public const string FormatValid = "FormatValid";
    public const string FormatInvalid = "FormatInvalid";
    public const string ExternallyVerified = "ExternallyVerified";
    public const string VerificationFailed = "VerificationFailed";
    public static readonly IReadOnlyList<string> All = [NotChecked, FormatValid, FormatInvalid, ExternallyVerified, VerificationFailed];
}

/// <summary>One kind of counterparty identifier an organisation records: its key, its label, the country it applies to, and the structural rule applied to values.</summary>
public sealed record IdentifierTypeProfile(string Key, string Label, string? Country, bool Required, bool Unique, string Rule, string? Length, string? Mask, string Checksum, string Classification = "Internal", int Order = 0)
{
    /// <summary>What people read. A type with no label of its own is called by the organisation's word for a tax identifier (the terminology key <c>supplier.tax.id</c>), so a pack that only renames that word renames the field.</summary>
    public string LabelOr(string fallback) => string.IsNullOrWhiteSpace(Label) ? fallback : Label;

    public bool IsPrimary(IReadOnlyList<IdentifierTypeProfile> all) => all.OrderBy(p => p.Order).ThenBy(p => p.Key, StringComparer.Ordinal).FirstOrDefault()?.Key == Key;
}

/// <summary>The result of checking one identifier value against its profile.</summary>
public sealed record IdentifierCheck(bool Valid, string Normalised, string? Code, string? Message);

/// <summary>Applies an <see cref="IdentifierTypeProfile"/> to a value. Structural only.</summary>
public static class IdentifierRules
{
    private static readonly Regex FreeShape = new(@"^[A-Za-z0-9][A-Za-z0-9 .\-/]{0,63}$", RegexOptions.Compiled | RegexOptions.NonBacktracking);
    private static readonly Regex KeyShape = new(@"^[a-z][a-z0-9]{0,11}(\.[a-z0-9][a-z0-9\-]{0,23}){0,2}$", RegexOptions.Compiled | RegexOptions.NonBacktracking);
    private static readonly Regex LengthShape = new(@"^[1-9][0-9]?(-[1-9][0-9]?)?$", RegexOptions.Compiled | RegexOptions.NonBacktracking);
    private static readonly Regex MaskShape = new(@"^[#A*\- /.]{1,40}$", RegexOptions.Compiled | RegexOptions.NonBacktracking);

    public static bool IsKey(string? key) => key is { Length: > 0 and <= 40 } && KeyShape.IsMatch(key);

    /// <summary>Problems with the profile itself (an unknown rule, a missing length, a mask with no placeholder). Empty when the profile can be used.</summary>
    public static IReadOnlyList<(string Code, string Message)> CheckProfile(IdentifierTypeProfile p)
    {
        var found = new List<(string, string)>();
        if (!IsKey(p.Key)) found.Add(("identifier_key", "An identifier key is lower-case letters and digits with dots, for example 'us.ein'."));
        if (!IdentifierRuleKinds.All.Contains(p.Rule)) found.Add(("identifier_rule", $"The rule '{p.Rule}' is not one of: {string.Join(", ", IdentifierRuleKinds.All)}."));
        if (!IdentifierChecksums.All.Contains(p.Checksum)) found.Add(("identifier_checksum", $"The checksum '{p.Checksum}' is not one of: {string.Join(", ", IdentifierChecksums.All)}."));
        if (p.Rule is IdentifierRuleKinds.Digits or IdentifierRuleKinds.Alphanumeric && (p.Length is null || !LengthShape.IsMatch(p.Length) || !RangeOk(p.Length)))
            found.Add(("identifier_length", "A digits or alphanumeric rule needs a length such as 9 or 8-9 (1 to 64 characters)."));
        if (p.Rule == IdentifierRuleKinds.Mask && (p.Mask is null || !MaskShape.IsMatch(p.Mask) || !p.Mask.Any(c => c is '#' or 'A' or '*')))
            found.Add(("identifier_mask", "A mask uses # for a digit, A for a letter and * for either, with spaces, hyphens, dots or slashes as separators, for example ##-#######."));
        if (p.Checksum != IdentifierChecksums.None && p.Rule is IdentifierRuleKinds.Free or IdentifierRuleKinds.Alphanumeric)
            found.Add(("identifier_checksum_rule", "A checksum needs a digits or mask rule: it is computed over digits."));
        if (p.Checksum == IdentifierChecksums.Mod89Weighted && Range(p) is not (11, 11))
            found.Add(("identifier_checksum_length", "The weighted modulus-89 checksum applies to eleven digits."));
        if (p.Country is not null && !RegionalCatalogue.IsCountry(p.Country)) found.Add(("identifier_country", $"'{p.Country}' is not a country or region code."));
        if (!string.IsNullOrEmpty(p.Label) && (p.Label.Length > 60 || p.Label.Any(char.IsControl) || p.Label.IndexOfAny(['<', '>', '{', '}', '$', '@', '[', ']', '&']) >= 0))
            found.Add(("identifier_label", "A label is plain text of up to 60 characters. Leave it empty to use the organisation's word for a tax identifier."));
        return found;
    }

    private static bool RangeOk(string length) { var (min, max) = ParseRange(length); return min >= 1 && max <= 64 && min <= max; }
    private static (int Min, int Max) ParseRange(string length) { var parts = length.Split('-'); var min = int.Parse(parts[0], CultureInfo.InvariantCulture); return (min, parts.Length > 1 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : min); }

    private static (int Min, int Max) Range(IdentifierTypeProfile p)
    {
        if (p.Rule == IdentifierRuleKinds.Mask && p.Mask is not null) { var n = p.Mask.Count(c => c is '#' or 'A' or '*'); return (n, n); }
        return p.Length is not null && LengthShape.IsMatch(p.Length) ? ParseRange(p.Length) : (0, 0);
    }

    /// <summary>Checks a value. The normalised form drops separators and upper-cases letters; it is what duplicate checks and searches compare.</summary>
    public static IdentifierCheck Check(IdentifierTypeProfile p, string? value)
    {
        var raw = (value ?? "").Trim();
        if (raw.Length == 0) return new(false, "", "identifier_empty", "A value is needed.");
        if (raw.Length > 80) return new(false, "", "identifier_length", "The value is too long.");
        string normalised;
        switch (p.Rule)
        {
            case IdentifierRuleKinds.Free:
                if (!FreeShape.IsMatch(raw)) return new(false, "", "identifier_chars", "Use letters, digits, spaces, hyphens, dots or slashes (up to 64 characters).");
                normalised = new string(raw.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
                return normalised.Length == 0 ? new(false, "", "identifier_chars", "Use letters or digits.") : Checksum(p, normalised);
            case IdentifierRuleKinds.Digits:
            {
                if (raw.Any(c => !char.IsAsciiDigit(c) && c is not (' ' or '-'))) return new(false, "", "identifier_chars", "Digits only (spaces and hyphens are ignored).");
                normalised = new string(raw.Where(char.IsAsciiDigit).ToArray());
                var (min, max) = Range(p);
                return normalised.Length < min || normalised.Length > max ? new(false, "", "identifier_length", min == max ? $"Exactly {min} digits are needed." : $"Between {min} and {max} digits are needed.") : Checksum(p, normalised);
            }
            case IdentifierRuleKinds.Alphanumeric:
            {
                if (raw.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not (' ' or '-'))) return new(false, "", "identifier_chars", "Letters and digits only (spaces and hyphens are ignored).");
                normalised = new string(raw.Where(char.IsAsciiLetterOrDigit).ToArray()).ToUpperInvariant();
                var (min, max) = Range(p);
                return normalised.Length < min || normalised.Length > max ? new(false, "", "identifier_length", min == max ? $"Exactly {min} characters are needed." : $"Between {min} and {max} characters are needed.") : Checksum(p, normalised);
            }
            case IdentifierRuleKinds.Mask:
            {
                var slots = (p.Mask ?? "").Where(c => c is '#' or 'A' or '*').ToArray();
                var chars = raw.Where(c => c is not (' ' or '-' or '.' or '/')).ToArray();
                if (chars.Length != slots.Length) return new(false, "", "identifier_length", $"The value needs {slots.Length} characters in the pattern {p.Mask}.");
                for (var i = 0; i < slots.Length; i++)
                {
                    var ok = slots[i] switch { '#' => char.IsAsciiDigit(chars[i]), 'A' => char.IsAsciiLetter(chars[i]), _ => char.IsAsciiLetterOrDigit(chars[i]) };
                    if (!ok) return new(false, "", "identifier_chars", $"Position {i + 1} does not fit the pattern {p.Mask}.");
                }
                return Checksum(p, new string(chars).ToUpperInvariant());
            }
            default:
                return new(false, "", "identifier_rule", "The identifier rule is not recognised.");
        }
    }

    private static IdentifierCheck Checksum(IdentifierTypeProfile p, string normalised)
    {
        var digits = normalised.Where(char.IsAsciiDigit).Select(c => c - '0').ToArray();
        switch (p.Checksum)
        {
            case IdentifierChecksums.Luhn:
            {
                var sum = 0; var alt = false;
                for (var i = digits.Length - 1; i >= 0; i--) { var d = digits[i]; if (alt) { d *= 2; if (d > 9) d -= 9; } sum += d; alt = !alt; }
                return digits.Length > 1 && sum % 10 == 0 ? new(true, normalised, null, null) : new(false, "", "identifier_checksum", "The check digit does not match.");
            }
            case IdentifierChecksums.Mod89Weighted:
            {
                if (digits.Length != 11) return new(false, "", "identifier_checksum", "The check digits do not match.");
                int[] weights = [10, 1, 3, 5, 7, 9, 11, 13, 15, 17, 19];
                var sum = 0;
                for (var i = 0; i < 11; i++) sum += (i == 0 ? digits[i] - 1 : digits[i]) * weights[i];
                return sum % 89 == 0 ? new(true, normalised, null, null) : new(false, "", "identifier_checksum", "The check digits do not match.");
            }
            default:
                return new(true, normalised, null, null);
        }
    }

    /// <summary>A value laid out for people: a mask puts its separators back; every other rule shows the value as entered, trimmed.</summary>
    public static string Display(IdentifierTypeProfile p, string normalised)
    {
        if (p.Rule != IdentifierRuleKinds.Mask || p.Mask is null) return normalised;
        var sb = new System.Text.StringBuilder(); var i = 0;
        foreach (var c in p.Mask) sb.Append(c is '#' or 'A' or '*' ? (i < normalised.Length ? normalised[i++] : '?') : c);
        return sb.ToString();
    }
}

/// <summary>One field of an address as an organisation uses it. Labels come from terminology (the keys <c>address.line1</c> to <c>address.country</c>), never from here.</summary>
public sealed record AddressFieldSpec(string Key, bool Used, bool Required);

/// <summary>Which address fields an organisation uses and which it requires. Not postal validation: a value is free text of bounded length.</summary>
public sealed record AddressProfile(IReadOnlyList<AddressFieldSpec> Fields)
{
    public static readonly IReadOnlyList<string> FieldKeys = ["line1", "line2", "locality", "region", "postal", "country"];

    /// <summary>Every field used and none required: the neutral starting point.</summary>
    public static AddressProfile Neutral { get; } = new(FieldKeys.Select(k => new AddressFieldSpec(k, true, false)).ToList());

    public AddressFieldSpec Spec(string key) => Fields.FirstOrDefault(f => f.Key == key) ?? new AddressFieldSpec(key, true, false);
}

/// <summary>A counterparty address as free text of bounded length. Not postal validation: no format is assumed for any country.</summary>
public sealed record SupplierAddress(string? Line1, string? Line2, string? Locality, string? Region, string? Postal, string? Country)
{
    public static readonly IReadOnlyDictionary<string, int> MaxLengths = new Dictionary<string, int> { ["line1"] = 200, ["line2"] = 200, ["locality"] = 100, ["region"] = 100, ["postal"] = 30, ["country"] = 100 };

    public string? Get(string key) => key switch { "line1" => Line1, "line2" => Line2, "locality" => Locality, "region" => Region, "postal" => Postal, "country" => Country, _ => null };
    public bool IsEmpty => AddressProfile.FieldKeys.All(k => string.IsNullOrWhiteSpace(Get(k)));

    /// <summary>Trims every value; an empty one becomes null.</summary>
    public SupplierAddress Cleaned()
    {
        static string? C(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        return new(C(Line1), C(Line2), C(Locality), C(Region), C(Postal), C(Country));
    }

    /// <summary>Problems against an organisation's address profile: a required field left empty, a field the organisation does not use, a value too long or with control characters. Keys are the field keys.</summary>
    public IReadOnlyList<(string Field, string Code, string Message)> Check(AddressProfile profile)
    {
        var found = new List<(string, string, string)>();
        foreach (var key in AddressProfile.FieldKeys)
        {
            var spec = profile.Spec(key);
            var value = Get(key);
            if (string.IsNullOrWhiteSpace(value)) { if (spec.Used && spec.Required) found.Add((key, "address_required", "This address field is required.")); continue; }
            if (!spec.Used) found.Add((key, "address_unused", "This organisation does not use this address field."));
            else if (value.Length > MaxLengths[key]) found.Add((key, "address_length", $"At most {MaxLengths[key]} characters."));
            else if (value.Any(char.IsControl)) found.Add((key, "address_chars", "Control characters are not allowed."));
        }
        return found;
    }
}

/// <summary>
/// An organisation's effective regional settings (docs/REGIONAL_SETTINGS.md): where each value came from is in <see cref="Source"/>. <see cref="IsComplete"/> is false while a business-facing value (locale, currency or
/// time zone) is missing; pages then show invariant formats and Production readiness reports the organisation.
/// </summary>
public sealed record RegionalSettings(string? CountryCode, string? PrimaryLocale, IReadOnlyList<string> SupportedLocales, string? DefaultCurrency, IReadOnlyList<string> SupportedCurrencies, string? TimeZoneId,
    TimeDisplay TimeDisplay, IReadOnlyList<IdentifierTypeProfile> Identifiers, AddressProfile Address, string? PhoneRegion, string Source, int? ReleaseNumber, string? PackKey, string? PackVersion,
    IReadOnlyDictionary<string, string>? Sources = null)
{
    public bool IsComplete => !string.IsNullOrWhiteSpace(PrimaryLocale) && !string.IsNullOrWhiteSpace(DefaultCurrency) && !string.IsNullOrWhiteSpace(TimeZoneId);

    /// <summary>Nothing decided: the invariant formats a diagnostic uses (ISO dates, UTC, no currency).</summary>
    public static RegionalSettings Unset { get; } = new(null, null, [], null, [], null, TimeDisplay.Locale, [], AddressProfile.Neutral, null, "none", null, null, null, new Dictionary<string, string>());

    /// <summary>Where each value came from: <c>profile</c> (the organisation's published profile), <c>pack</c>, <c>record</c> (the organisation's stored settings, the compatibility layer), <c>installation</c> or <c>unset</c>.</summary>
    public string SourceOf(string field) => Sources is not null && Sources.TryGetValue(field, out var s) ? s : "unset";

    public LocaleFormatter Formatter() => new(PrimaryLocale ?? "", TimeZoneId ?? "UTC", DefaultCurrency, TimeDisplay);

    public IdentifierTypeProfile? Identifier(string key) => Identifiers.FirstOrDefault(i => string.Equals(i.Key, key, StringComparison.Ordinal));

    /// <summary>The identifier type an organisation records first (the lowest order), shown in lists and used for the single-value form.</summary>
    public IdentifierTypeProfile? PrimaryIdentifier => Identifiers.OrderBy(i => i.Order).ThenBy(i => i.Key, StringComparer.Ordinal).FirstOrDefault();
}
