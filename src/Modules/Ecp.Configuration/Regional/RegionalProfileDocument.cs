using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Ecp.SharedKernel;
using Ecp.SharedKernel.Localisation;

namespace Ecp.Configuration;

/// <summary>The configuration kind name for an organisation's regional profile (docs/REGIONAL_SETTINGS.md).</summary>
public static class RegionalKinds
{
    public const string Name = "RegionalProfile";
}

/// <summary>Which pack version an organisation's profile inherits from for the values it does not set itself. Pinned when the draft is made, so a later pack version changes nothing until the organisation publishes again.</summary>
public sealed record RegionalSource(string PackKey, string PackVersion);

/// <summary>
/// The content of the <c>RegionalProfile</c> configuration kind and of a pack's regional document: country or region, locales, currencies, time zone, time display, phone region, counterparty identifier profiles and the
/// address profile. Every value is optional. In a pack (<c>defaults</c>) a value is that pack's proposal; in an organisation (<c>overrides</c>) a value is that organisation's own choice and a value left out is inherited from
/// <see cref="Source"/>. Plain data checked against closed catalogues: no format strings, patterns or expressions are accepted, and nothing here is executed.
/// </summary>
public sealed record RegionalProfileDocument(
    string Mode, RegionalSource? Source, string? Country, string? Locale, IReadOnlyList<string>? SupportedLocales, string? Currency, IReadOnlyList<string>? SupportedCurrencies, string? TimeZone, TimeDisplay? TimeDisplay,
    string? PhoneRegion, IReadOnlyList<IdentifierTypeProfile>? Identifiers, AddressProfile? Address)
{
    public const string Defaults = "defaults";
    public const string Overrides = "overrides";
    public const int MaxLocales = 40;
    public const int MaxCurrencies = 60;
    public const int MaxIdentifiers = 12;

    private static readonly JsonSerializerOptions Write = new(JsonSerializerDefaults.Web) { WriteIndented = false, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
    private static readonly Regex Markup = new(@"<|>|&[#a-zA-Z0-9]+;|javascript\s*:|vbscript\s*:|\bon[a-z]+\s*=|\{\{|\}\}|\$\{|<%|%>|@\(|\bexpr\s*:", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking);
    private static readonly Regex PackKeyShape = new("^[a-z][a-z0-9\\-]{0,63}$", RegexOptions.Compiled | RegexOptions.NonBacktracking);
    private static readonly Regex VersionShape = new(@"^[0-9]{1,5}\.[0-9]{1,5}\.[0-9]{1,5}(-[A-Za-z0-9.\-]{1,20})?$", RegexOptions.Compiled | RegexOptions.NonBacktracking);

    public static RegionalProfileDocument Empty => new(Overrides, null, null, null, null, null, null, null, null, null, null, null);

    public static RegionalProfileDocument Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) throw new DomainException("invalid_json", "The regional profile must be a JSON object.");
            string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            bool Bool(JsonElement e, string name, bool otherwise) => e.TryGetProperty(name, out var v) ? v.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => otherwise } : otherwise;
            IReadOnlyList<string>? List(string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? [.. v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!)] : null;

            RegionalSource? source = null;
            if (r.TryGetProperty("source", out var s) && s.ValueKind == JsonValueKind.Object && Str(s, "packKey") is { } pk && Str(s, "packVersion") is { } pv) source = new RegionalSource(pk, pv);

            TimeDisplay? display = null;
            if (Str(r, "timeDisplay") is { } td)
                display = td switch { "locale" => Ecp.SharedKernel.Localisation.TimeDisplay.Locale, "24h" => Ecp.SharedKernel.Localisation.TimeDisplay.TwentyFourHour, "12h" => Ecp.SharedKernel.Localisation.TimeDisplay.TwelveHour,
                    _ => throw new DomainException("invalid_time_display", "The time display must be 'locale', '24h' or '12h'.") };

            List<IdentifierTypeProfile>? identifiers = null;
            if (r.TryGetProperty("identifiers", out var ids) && ids.ValueKind == JsonValueKind.Array)
            {
                identifiers = [];
                foreach (var e in ids.EnumerateArray())
                {
                    if (e.ValueKind != JsonValueKind.Object) throw new DomainException("invalid_identifiers", "Each identifier profile must be an object.");
                    identifiers.Add(new IdentifierTypeProfile(Str(e, "key") ?? "", Str(e, "label") ?? "", Str(e, "country"), Bool(e, "required", false), Bool(e, "unique", false), Str(e, "rule") ?? IdentifierRuleKinds.Free,
                        Str(e, "length"), Str(e, "mask"), Str(e, "checksum") ?? IdentifierChecksums.None, Str(e, "classification") ?? "Internal", e.TryGetProperty("order", out var o) && o.TryGetInt32(out var oi) ? oi : 0));
                }
            }

            AddressProfile? address = null;
            if (r.TryGetProperty("address", out var a) && a.ValueKind == JsonValueKind.Object && a.TryGetProperty("fields", out var fs) && fs.ValueKind == JsonValueKind.Array)
                address = new AddressProfile([.. fs.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object).Select(x => new AddressFieldSpec(Str(x, "key") ?? "", Bool(x, "used", true), Bool(x, "required", false)))]);

            return new RegionalProfileDocument(Str(r, "mode") ?? Overrides, source, Str(r, "country"), Str(r, "locale"), List("supportedLocales"), Str(r, "currency"), List("supportedCurrencies"), Str(r, "timeZone"), display,
                Str(r, "phoneRegion"), identifiers, address);
        }
        catch (JsonException) { throw new DomainException("invalid_json", "The regional profile is not valid JSON."); }
    }

    public string ToJson()
    {
        var node = new JsonObject { ["schemaVersion"] = 1, ["mode"] = Mode };
        if (Source is not null) node["source"] = new JsonObject { ["packKey"] = Source.PackKey, ["packVersion"] = Source.PackVersion };
        if (Country is not null) node["country"] = Country;
        if (Locale is not null) node["locale"] = Locale;
        if (SupportedLocales is not null) node["supportedLocales"] = new JsonArray([.. SupportedLocales.Select(x => (JsonNode?)JsonValue.Create(x))]);
        if (Currency is not null) node["currency"] = Currency;
        if (SupportedCurrencies is not null) node["supportedCurrencies"] = new JsonArray([.. SupportedCurrencies.Select(x => (JsonNode?)JsonValue.Create(x))]);
        if (TimeZone is not null) node["timeZone"] = TimeZone;
        if (TimeDisplay is { } td) node["timeDisplay"] = td switch { Ecp.SharedKernel.Localisation.TimeDisplay.TwentyFourHour => "24h", Ecp.SharedKernel.Localisation.TimeDisplay.TwelveHour => "12h", _ => "locale" };
        if (PhoneRegion is not null) node["phoneRegion"] = PhoneRegion;
        if (Identifiers is not null)
            node["identifiers"] = new JsonArray([.. Identifiers.OrderBy(i => i.Order).ThenBy(i => i.Key, StringComparer.Ordinal).Select(i =>
            {
                var o = new JsonObject { ["key"] = i.Key, ["label"] = i.Label };
                if (i.Country is not null) o["country"] = i.Country;
                o["required"] = i.Required; o["unique"] = i.Unique; o["rule"] = i.Rule;
                if (i.Length is not null) o["length"] = i.Length;
                if (i.Mask is not null) o["mask"] = i.Mask;
                o["checksum"] = i.Checksum; o["classification"] = i.Classification; o["order"] = i.Order;
                return (JsonNode?)o;
            })]);
        if (Address is not null)
            node["address"] = new JsonObject { ["fields"] = new JsonArray([.. Address.Fields.Select(f => (JsonNode?)new JsonObject { ["key"] = f.Key, ["used"] = f.Used, ["required"] = f.Required })]) };
        return node.ToJsonString(Write);
    }

    /// <summary>A pack's document as the starting point of an organisation's draft: it decides nothing itself and inherits everything from the pack version it came from.</summary>
    public RegionalProfileDocument InheritingFrom(string packKey, string packVersion) => Empty with { Source = new RegionalSource(packKey, packVersion) };

    /// <summary>Checks the document on its own against the platform's catalogues.</summary>
    public IReadOnlyList<ValidationMessage> Check()
    {
        var found = new List<ValidationMessage>();
        void Err(string path, string code, string text) => found.Add(new(ValidationSeverity.Error, path, code, text));
        void Warn(string path, string code, string text) => found.Add(new(ValidationSeverity.Warning, path, code, text));

        if (Mode is not (Defaults or Overrides)) Err("$.mode", "regional_mode", "The mode must be 'defaults' or 'overrides'.");
        if (Source is { } src)
        {
            if (Mode == Defaults) Err("$.source", "regional_source", "A pack's regional document does not name a source: it is the source.");
            if (!PackKeyShape.IsMatch(src.PackKey) || !VersionShape.IsMatch(src.PackVersion)) Err("$.source", "regional_source", "The source is a pack key and a version such as 1.2.0.");
        }
        if (Country is not null && !RegionalCatalogue.IsCountry(Country)) Err("$.country", "regional_country", $"'{Trim(Country)}' is not a country or region code the platform knows (two capital letters, for example NZ).");
        if (Locale is not null && !RegionalCatalogue.IsLocale(Locale)) Err("$.locale", "regional_locale", $"'{Trim(Locale)}' is not a locale the platform knows (a language and region such as pt-BR).");
        if (Currency is not null && !RegionalCatalogue.IsCurrency(Currency)) Err("$.currency", "regional_currency", $"'{Trim(Currency)}' is not an ISO 4217 currency code the platform offers.");
        if (TimeZone is not null && !RegionalCatalogue.IsTimeZone(TimeZone)) Err("$.timeZone", "regional_time_zone", $"'{Trim(TimeZone)}' is not an IANA time zone this host knows. It must be available before the profile can be published.");
        if (PhoneRegion is not null && !RegionalCatalogue.IsPhoneRegion(PhoneRegion)) Err("$.phoneRegion", "regional_phone_region", $"'{Trim(PhoneRegion)}' is not a country or region code.");

        if (SupportedLocales is not null)
        {
            if (SupportedLocales.Count > MaxLocales) Err("$.supportedLocales", "regional_count", $"At most {MaxLocales} supported locales.");
            foreach (var l in SupportedLocales.Take(MaxLocales)) if (!RegionalCatalogue.IsLocale(l)) Err("$.supportedLocales", "regional_locale", $"'{Trim(l)}' is not a locale the platform knows.");
            if (SupportedLocales.Distinct(StringComparer.OrdinalIgnoreCase).Count() != SupportedLocales.Count) Err("$.supportedLocales", "regional_duplicate", "A locale is listed more than once.");
            if (Locale is not null && SupportedLocales.Count > 0 && !SupportedLocales.Contains(Locale, StringComparer.OrdinalIgnoreCase)) Err("$.supportedLocales", "regional_default_supported", "The supported locales must include the primary locale.");
        }
        if (SupportedCurrencies is not null)
        {
            if (SupportedCurrencies.Count > MaxCurrencies) Err("$.supportedCurrencies", "regional_count", $"At most {MaxCurrencies} supported currencies.");
            foreach (var c in SupportedCurrencies.Take(MaxCurrencies)) if (!RegionalCatalogue.IsCurrency(c)) Err("$.supportedCurrencies", "regional_currency", $"'{Trim(c)}' is not an ISO 4217 currency code the platform offers.");
            if (SupportedCurrencies.Distinct(StringComparer.Ordinal).Count() != SupportedCurrencies.Count) Err("$.supportedCurrencies", "regional_duplicate", "A currency is listed more than once.");
            if (Currency is not null && SupportedCurrencies.Count > 0 && !SupportedCurrencies.Contains(Currency, StringComparer.Ordinal)) Err("$.supportedCurrencies", "regional_default_supported", "The supported currencies must include the default currency.");
        }

        if (Identifiers is not null)
        {
            if (Identifiers.Count > MaxIdentifiers) Err("$.identifiers", "regional_count", $"At most {MaxIdentifiers} identifier types.");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < Identifiers.Count && i < MaxIdentifiers; i++)
            {
                var p = Identifiers[i];
                var path = $"$.identifiers[{i}]";
                foreach (var (code, message) in IdentifierRules.CheckProfile(p)) Err(path, code, message);
                if (!seen.Add(p.Key)) Err(path, "identifier_duplicate", $"The identifier type '{Trim(p.Key)}' appears more than once.");
                if (Markup.IsMatch(p.Label)) Err(path + ".label", "identifier_label", "A label is plain text: no HTML, script, template or expression syntax.");
            }
        }
        if (Address is not null)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var f in Address.Fields)
            {
                if (!AddressProfile.FieldKeys.Contains(f.Key)) { Err("$.address.fields", "address_field", $"'{Trim(f.Key)}' is not an address field. The fields are: {string.Join(", ", AddressProfile.FieldKeys)}."); continue; }
                if (!seen.Add(f.Key)) Err("$.address.fields", "address_duplicate", $"The address field '{f.Key}' appears more than once.");
                if (f.Required && !f.Used) Err("$.address.fields", "address_required_unused", $"The address field '{f.Key}' cannot be required and unused.");
            }
        }
        if (Mode == Defaults && Identifiers is { Count: > 0 } && Identifiers.Any(i => i.Required) && Identifiers.Count(i => i.Required) > 1) Warn("$.identifiers", "identifier_several_required", "More than one identifier type is required.");
        return found;
    }

    private static string Trim(string value) => value.Length > 30 ? value[..30] + "..." : value;
}
