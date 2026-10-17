using Ecp.Configuration;
using Ecp.Notifications;
using Ecp.Notifications.Policy;
using Ecp.SharedKernel;
using Ecp.SharedKernel.Localisation;
using Ecp.Tenancy;
using Xunit;

namespace Ecp.UnitTests;

/// <summary>
/// S10 (docs/REGIONAL_SETTINGS.md, docs/TAX_IDENTIFIERS.md): the platform assumes no country, locale, currency, zone or identifier format. These tests use several regions on purpose, none of them privileged.
/// </summary>
[Trait("Category", "Unit")]
public sealed class RegionalSettingsTests
{
    private static readonly DateOnly Sample = new(2026, 10, 16);

    // ----- the catalogues ----------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("AUD")] [InlineData("USD")] [InlineData("CAD")] [InlineData("EUR")] [InlineData("JPY")] [InlineData("INR")]
    public void The_currency_catalogue_offers_every_region_equally(string code) => Assert.True(RegionalCatalogue.IsCurrency(code));

    [Theory]
    [InlineData("")] [InlineData("AU")] [InlineData("aud")] [InlineData("XXX")] [InlineData("AUDD")] [InlineData("12$")]
    public void An_arbitrary_currency_code_is_refused(string code) => Assert.False(RegionalCatalogue.IsCurrency(code));

    [Theory]
    [InlineData("en-AU")] [InlineData("en-US")] [InlineData("fr-CA")] [InlineData("de-DE")] [InlineData("pt-BR")] [InlineData("ja-JP")]
    public void Locales_are_checked_against_the_host_culture_data_not_a_fixed_list(string tag) => Assert.True(RegionalCatalogue.IsLocale(tag));

    [Theory]
    [InlineData("")] [InlineData("english")] [InlineData("en_AU")] [InlineData("zz-ZZ-garbage-more")] [InlineData("<script>")]
    public void An_invalid_locale_is_refused(string tag) => Assert.False(RegionalCatalogue.IsLocale(tag));

    [Fact]
    public void A_time_zone_must_be_available_on_the_host()
    {
        Assert.True(RegionalCatalogue.IsTimeZone("Europe/Paris"));
        Assert.True(RegionalCatalogue.IsTimeZone("UTC"));
        Assert.False(RegionalCatalogue.IsTimeZone("Mars/Olympus_Mons"));
        Assert.False(RegionalCatalogue.IsTimeZone(""));
        Assert.False(RegionalCatalogue.IsTimeZone(new string('x', 200)));
    }

    [Fact]
    public void Countries_come_from_the_host_and_include_no_privileged_default()
    {
        Assert.True(RegionalCatalogue.IsCountry("NZ"));
        Assert.True(RegionalCatalogue.IsCountry("CA"));
        Assert.False(RegionalCatalogue.IsCountry("ZZ"));
        Assert.False(RegionalCatalogue.IsCountry("nz"));
        Assert.True(RegionalCatalogue.Countries.Count > 100);
    }

    // ----- the profile document ------------------------------------------------------------------------------------------------------------

    private static RegionalProfileDocument Doc(string json) => RegionalProfileDocument.Parse(json);

    [Fact]
    public void A_valid_profile_round_trips_and_has_no_errors()
    {
        var json = """
        { "schemaVersion": 1, "mode": "overrides", "country": "CA", "locale": "fr-CA", "supportedLocales": ["fr-CA", "en-CA"], "currency": "CAD", "supportedCurrencies": ["CAD", "USD"],
          "timeZone": "America/Toronto", "timeDisplay": "24h", "phoneRegion": "CA",
          "identifiers": [ { "key": "ca.bn", "label": "Business Number", "country": "CA", "required": true, "unique": true, "rule": "digits", "length": "9", "checksum": "luhn", "classification": "Internal", "order": 1 } ],
          "address": { "fields": [ { "key": "line1", "used": true, "required": true }, { "key": "region", "used": true, "required": false } ] } }
        """;
        var doc = Doc(json);
        Assert.DoesNotContain(doc.Check(), m => m.Severity == ValidationSeverity.Error);
        var again = Doc(doc.ToJson());
        Assert.Equal(doc.Locale, again.Locale);
        Assert.Equal(doc.TimeZone, again.TimeZone);
        Assert.Equal(doc.Identifiers!.Single().Key, again.Identifiers!.Single().Key);
        Assert.Equal(TimeDisplay.TwentyFourHour, again.TimeDisplay);
    }

    [Theory]
    [InlineData("""{ "mode": "overrides", "locale": "english" }""", "regional_locale")]
    [InlineData("""{ "mode": "overrides", "currency": "ZZZ" }""", "regional_currency")]
    [InlineData("""{ "mode": "overrides", "timeZone": "Mars/Olympus_Mons" }""", "regional_time_zone")]
    [InlineData("""{ "mode": "overrides", "country": "XX" }""", "regional_country")]
    [InlineData("""{ "mode": "overrides", "locale": "fr-CA", "supportedLocales": ["en-CA"] }""", "regional_default_supported")]
    [InlineData("""{ "mode": "overrides", "currency": "CAD", "supportedCurrencies": ["USD"] }""", "regional_default_supported")]
    [InlineData("""{ "mode": "overrides", "supportedCurrencies": ["USD", "USD"] }""", "regional_duplicate")]
    [InlineData("""{ "mode": "sideways" }""", "regional_mode")]
    [InlineData("""{ "mode": "defaults", "source": { "packKey": "x", "packVersion": "1.0.0" } }""", "regional_source")]
    [InlineData("""{ "mode": "overrides", "address": { "fields": [ { "key": "street", "used": true } ] } }""", "address_field")]
    [InlineData("""{ "mode": "overrides", "address": { "fields": [ { "key": "line1", "used": false, "required": true } ] } }""", "address_required_unused")]
    public void An_invalid_profile_is_refused_with_a_stable_code(string json, string code)
        => Assert.Contains(Doc(json).Check(), m => m.Code == code && m.Severity == ValidationSeverity.Error);

    [Theory]
    [InlineData("regex")]    // a customer cannot supply a pattern: the rule must be one of the closed list
    [InlineData("(a+)+$")]
    [InlineData("custom")]
    public void An_identifier_rule_outside_the_closed_catalogue_is_refused(string rule)
    {
        var doc = Doc($$"""{ "mode": "overrides", "identifiers": [ { "key": "xx.id", "label": "Id", "rule": "{{rule}}" } ] }""");
        Assert.Contains(doc.Check(), m => m.Code == "identifier_rule");
    }

    [Fact]
    public void An_identifier_label_cannot_carry_markup_or_template_syntax()
    {
        foreach (var label in new[] { "<b>ID</b>", "{{x}}", "${x}", "[[term]]", "a&amp;b" })
        {
            var doc = Doc($$"""{ "mode": "overrides", "identifiers": [ { "key": "xx.id", "label": "{{label}}", "rule": "free" } ] }""");
            Assert.Contains(doc.Check(), m => m.Severity == ValidationSeverity.Error);
        }
    }

    // ----- precedence ------------------------------------------------------------------------------------------------------------------

    private static PackRegional Pack(string locale, string currency, string zone, string country) => new("p", "1.0.0",
        Doc($$"""{ "mode": "defaults", "country": "{{country}}", "locale": "{{locale}}", "currency": "{{currency}}", "timeZone": "{{zone}}" }"""));

    private static OrganisationSummary Record(string zone, string locale, string currency) => new(Guid.NewGuid(), "Org", zone, locale, currency, "ORG", null);

    [Fact]
    public void A_published_profile_beats_the_pack_which_beats_the_stored_settings_which_beat_the_installation()
    {
        var profile = Doc("""{ "mode": "overrides", "locale": "de-DE" }""");
        var pack = Pack("en-NZ", "NZD", "Pacific/Auckland", "NZ");
        var record = Record("Europe/Paris", "fr-FR", "EUR");
        var installation = new LocalisationOptions { DefaultCulture = "en-IE", DefaultTimeZoneId = "Europe/Dublin", DefaultCurrencyCode = "EUR", SupportedCultures = ["en-IE"] };

        var s = RegionalComposer.Compose(profile, 3, pack, record, installation);
        Assert.Equal("de-DE", s.PrimaryLocale);                  // the organisation's own choice
        Assert.Equal("NZD", s.DefaultCurrency);                  // not chosen: the pack the profile is pinned to
        Assert.Equal("Pacific/Auckland", s.TimeZoneId);
        Assert.Equal("profile", s.SourceOf("locale"));
        Assert.StartsWith("pack", s.SourceOf("currency"));
    }

    [Fact]
    public void An_organisation_with_no_profile_keeps_its_stored_settings_first_so_installing_the_feature_changes_nothing()
    {
        var s = RegionalComposer.Compose(null, null, Pack("en-NZ", "NZD", "Pacific/Auckland", "NZ"), Record("Europe/Paris", "fr-FR", "EUR"), null);
        Assert.Equal("fr-FR", s.PrimaryLocale);
        Assert.Equal("EUR", s.DefaultCurrency);
        Assert.Equal("Europe/Paris", s.TimeZoneId);
        Assert.Equal("record", s.SourceOf("locale"));
        Assert.Equal("NZ", s.CountryCode);                       // the pack still proposes what the stored settings do not hold
    }

    [Fact]
    public void The_installation_fall_back_applies_only_where_there_is_no_organisation()
    {
        var installation = new LocalisationOptions { DefaultCulture = "en-IE", DefaultTimeZoneId = "Europe/Dublin", DefaultCurrencyCode = "EUR", SupportedCultures = ["en-IE"] };
        Assert.Equal("en-IE", RegionalComposer.Compose(null, null, null, null, installation).PrimaryLocale);
        var withOrganisation = RegionalComposer.Compose(null, null, null, Record("", "", ""), installation);
        Assert.Null(withOrganisation.PrimaryLocale);             // an organisation that decided nothing is not silently given the installation's country
        Assert.False(withOrganisation.IsComplete);
    }

    [Fact]
    public void Nothing_decided_anywhere_gives_the_invariant_settings_and_is_reported_incomplete()
    {
        var s = RegionalComposer.Compose(null, null, null, null, new LocalisationOptions());
        Assert.False(s.IsComplete);
        Assert.Null(s.PrimaryLocale); Assert.Null(s.DefaultCurrency); Assert.Null(s.TimeZoneId);
        var f = s.Formatter();
        Assert.Equal("2026-10-16", f.Date(Sample));              // ISO 8601, no country assumed
        Assert.Throws<DomainException>(() => f.Money(10m));      // an amount without a currency is not shown
        Assert.Equal("UTC", f.TimeZone.Id);
    }

    [Fact]
    public void A_pack_that_proposes_nothing_leaves_the_organisation_to_choose()
    {
        var blank = new PackRegional("blank", "1.6.0", Doc("""{ "mode": "defaults" }"""));
        var s = RegionalComposer.Compose(Doc("""{ "mode": "overrides" }"""), 1, blank, null, null);
        Assert.False(s.IsComplete);
        Assert.Empty(s.Identifiers);                             // no identifier type is assumed
    }

    // ----- formats: several regions, one code path -------------------------------------------------------------------------------------

    [Theory]
    [InlineData("en-AU", "16/10/2026")]
    [InlineData("en-US", "10/16/2026")]
    [InlineData("de-DE", "16.10.2026")]
    [InlineData("fr-FR", "16/10/2026")]
    [InlineData("ja-JP", "2026/10/16")]
    public void A_date_follows_the_organisations_locale(string locale, string expected)
        => Assert.Equal(expected, new LocaleFormatter(locale, "UTC", null).Date(Sample));

    [Theory]
    [InlineData("AUD", "en-AU")] [InlineData("USD", "en-US")] [InlineData("CAD", "en-CA")]
    public void An_amount_in_the_cultures_own_currency_uses_its_symbol_and_any_other_currency_uses_its_code(string currency, string locale)
    {
        var f = new LocaleFormatter(locale, "UTC", currency);
        Assert.StartsWith("$", f.Money(1234.5m));                // the culture's own dollar
        var other = currency == "USD" ? "CAD" : "USD";
        Assert.StartsWith(other, f.Money(1234.5m, other));       // never an ambiguous symbol for a different currency
        Assert.Contains("1,234.50", f.Money(1234.5m, other));
    }

    [Theory]
    [InlineData("AUD")] [InlineData("USD")] [InlineData("CAD")]
    public void An_amount_with_its_code_carries_the_iso_code_and_never_a_symbol(string currency)
    {
        var text = new LocaleFormatter("en-US", "UTC", null).MoneyWithCode(1234.5m, currency);
        Assert.Contains(currency, text);
        Assert.DoesNotContain("$", text);
    }

    [Fact]
    public void Amounts_and_numbers_follow_the_locale_separators_and_the_currencys_minor_units()
    {
        Assert.Equal("1.234,50", new LocaleFormatter("de-DE", "UTC", "EUR").Number(1234.5m));
        Assert.Contains("1,235", new LocaleFormatter("en-US", "UTC", "JPY").MoneyWithCode(1234.5m, "JPY").Replace(" ", "").Replace(" ", ""));   // yen has no minor unit
    }

    [Theory]
    [InlineData("America/Chicago", -6, -5)]
    [InlineData("Europe/London", 0, 1)]
    [InlineData("Australia/Sydney", 11, 10)]     // southern hemisphere: summer time in January
    [InlineData("Australia/Brisbane", 10, 10)]   // no daylight saving
    [InlineData("Asia/Kolkata", 5.5, 5.5)]
    public void Daylight_saving_follows_the_zone_in_both_halves_of_the_year(string zone, double january, double july)
    {
        TimeSpan At(int month) => TimeZones.Find(zone).GetUtcOffset(new DateTime(2026, month, 15, 12, 0, 0, DateTimeKind.Utc));
        Assert.Equal(TimeSpan.FromHours(january), At(1));
        Assert.Equal(TimeSpan.FromHours(july), At(7));
        // The formatter shows the offset it actually applied.
        var f = new LocaleFormatter("en-GB", zone, null);
        var winter = f.DateTime(new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc));
        var summer = f.DateTime(new DateTime(2026, 7, 15, 12, 0, 0, DateTimeKind.Utc));
        if (january != july) Assert.NotEqual(winter.Split(' ')[^1], summer.Split(' ')[^1]);
    }

    [Fact]
    public void A_date_only_value_is_never_shifted_by_a_time_zone()
    {
        var date = new DateOnly(2026, 3, 1);
        var expected = new LocaleFormatter("en-AU", "UTC", null).Date(date);
        Assert.Contains("2026", expected);
        foreach (var zone in new[] { "Pacific/Kiritimati", "Pacific/Pago_Pago", "America/Chicago", "Asia/Tokyo" })
            Assert.Equal(expected, new LocaleFormatter("en-AU", zone, null).Date(date));
    }

    // PRT-012: culture, zone and currency per organisation, across en-AU, en-NZ, a UTC+10 zone and a UTC-8 zone. The same UTC instant gives each organisation its own calendar day, which is what a reminder window,
    // an approval's date and a notification's "today" are judged on.
    [Theory]
    [InlineData("en-AU", "Australia/Brisbane", "AUD", "2026-10-16T14:30:00Z", "2026-10-17", 10)]    // UTC+10: already the next day
    [InlineData("en-NZ", "Pacific/Auckland", "NZD", "2026-10-16T14:30:00Z", "2026-10-17", 13)]       // UTC+13 in October (daylight saving)
    [InlineData("en-US", "America/Los_Angeles", "USD", "2026-01-16T06:30:00Z", "2026-01-15", -8)]    // UTC-8 in winter: still the previous day
    [InlineData("en-US", "America/Los_Angeles", "USD", "2026-07-16T06:30:00Z", "2026-07-15", -7)]    // the same zone in summer
    [InlineData("de-DE", "Europe/Berlin", "EUR", "2026-10-16T22:30:00Z", "2026-10-17", 2)]
    public void Each_organisation_judges_the_same_instant_on_its_own_calendar_day(string locale, string zone, string currency, string utc, string localDay, double offsetHours)
    {
        var instant = DateTime.Parse(utc, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);
        var f = new LocaleFormatter(locale, zone, currency);
        Assert.Equal(DateOnly.Parse(localDay, System.Globalization.CultureInfo.InvariantCulture), TimeZones.LocalDate(instant, f.TimeZone));
        Assert.Equal(TimeSpan.FromHours(offsetHours), f.TimeZone.GetUtcOffset(instant));
        Assert.Equal(f.Date(DateOnly.Parse(localDay, System.Globalization.CultureInfo.InvariantCulture)), f.Date(instant));
        Assert.False(string.IsNullOrWhiteSpace(f.Money(10m)));                                  // the organisation's own currency needs no guess
        Assert.Contains(currency, f.MoneyWithCode(10m, currency));
    }

    [Fact]
    public void The_same_instant_is_a_different_local_day_in_different_zones_but_one_utc_value()
    {
        var instant = new DateTime(2026, 10, 15, 22, 30, 0, DateTimeKind.Utc);
        Assert.Equal("15/10/2026", new LocaleFormatter("en-AU", "America/Chicago", null).Date(instant));
        Assert.Equal("16/10/2026", new LocaleFormatter("en-AU", "Australia/Brisbane", null).Date(instant));
        Assert.Equal("16/10/2026", new LocaleFormatter("en-AU", "Asia/Tokyo", null).Date(instant));
    }

    // ----- identifiers: nothing here is Australian or American --------------------------------------------------------------------------

    private static IdentifierTypeProfile Profile(string key, string rule, string? length = null, string? mask = null, string checksum = "none", bool unique = false)
        => new(key, "Label " + key, null, false, unique, rule, length, mask, checksum);

    [Theory]
    [InlineData("12345678901", true)]
    [InlineData("12 345 678 901", true)]
    [InlineData("1234567890", false)]
    [InlineData("123456789012", false)]
    [InlineData("1234567890A", false)]
    public void An_eleven_digit_type_is_just_one_configured_profile(string value, bool valid)
    {
        var abn = Profile("au.abn", IdentifierRuleKinds.Digits, "11");
        Assert.Equal(valid, IdentifierRules.Check(abn, value).Valid);
    }

    [Theory]
    [InlineData("12-3456789", true, "123456789")]
    [InlineData("123456789", true, "123456789")]
    [InlineData("1-23456789", true, "123456789")]
    [InlineData("12-345678", false, "")]
    [InlineData("AB-3456789", false, "")]
    public void A_masked_type_accepts_its_pattern_and_normalises_by_dropping_separators(string value, bool valid, string normalised)
    {
        var r = IdentifierRules.Check(Profile("us.ein", IdentifierRuleKinds.Mask, mask: "##-#######"), value);
        Assert.Equal(valid, r.Valid);
        Assert.Equal(normalised, r.Normalised);
    }

    [Theory]
    [InlineData("046454286", true)]       // the Luhn test value for nine digits
    [InlineData("046454287", false)]
    [InlineData("123456789", false)]
    public void A_checksum_comes_from_the_closed_catalogue_and_is_structural_only(string value, bool valid)
        => Assert.Equal(valid, IdentifierRules.Check(Profile("ca.bn", IdentifierRuleKinds.Digits, "9", checksum: IdentifierChecksums.Luhn), value).Valid);

    [Theory]
    [InlineData("DE123456789", true)]
    [InlineData("de 123-456-789", true)]
    [InlineData("DE12345", false)]
    [InlineData("DE_123456789", false)]
    public void An_alphanumeric_type_accepts_letters_and_digits_of_the_configured_length(string value, bool valid)
        => Assert.Equal(valid, IdentifierRules.Check(Profile("eu.vat", IdentifierRuleKinds.Alphanumeric, "11"), value).Valid);

    [Fact]
    public void The_weighted_modulus_checksum_is_available_but_is_only_applied_when_a_profile_selects_it()
    {
        var withChecksum = Profile("xx.n", IdentifierRuleKinds.Digits, "11", checksum: IdentifierChecksums.Mod89Weighted);
        var without = Profile("xx.n", IdentifierRuleKinds.Digits, "11");
        Assert.True(IdentifierRules.Check(without, "12345678901").Valid);
        Assert.False(IdentifierRules.Check(withChecksum, "12345678901").Valid);
        Assert.True(IdentifierRules.Check(withChecksum, "51824753556").Valid);   // a value that satisfies the weights
    }

    [Fact]
    public void A_free_type_rejects_markup_and_over_long_values_and_a_value_is_never_called_legally_valid()
    {
        var free = Profile("xx.ref", IdentifierRuleKinds.Free);
        Assert.True(IdentifierRules.Check(free, "REF 2026/001").Valid);
        Assert.False(IdentifierRules.Check(free, "<script>alert(1)</script>").Valid);
        Assert.False(IdentifierRules.Check(free, new string('9', 81)).Valid);
        Assert.False(IdentifierRules.Check(free, "").Valid);
        // The messages describe structure; none claims registration, validity in law or a registry result.
        foreach (var bad in new[] { "", "<>", new string('x', 90) })
        {
            var message = IdentifierRules.Check(free, bad).Message ?? "";
            Assert.DoesNotContain("registered", message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("legally", message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void A_profile_with_an_unusable_rule_is_refused_before_it_can_be_published()
    {
        Assert.NotEmpty(IdentifierRules.CheckProfile(Profile("Bad Key", IdentifierRuleKinds.Free)));
        Assert.NotEmpty(IdentifierRules.CheckProfile(Profile("xx.id", IdentifierRuleKinds.Digits)));                       // a digits rule needs a length
        Assert.NotEmpty(IdentifierRules.CheckProfile(Profile("xx.id", IdentifierRuleKinds.Mask, mask: "-----")));          // a mask needs a placeholder
        Assert.NotEmpty(IdentifierRules.CheckProfile(Profile("xx.id", IdentifierRuleKinds.Free, checksum: IdentifierChecksums.Luhn)));
        Assert.Empty(IdentifierRules.CheckProfile(Profile("xx.id", IdentifierRuleKinds.Digits, "8-9")));
    }

    [Fact]
    public void Identifier_values_that_differ_only_by_separators_normalise_to_the_same_duplicate_key()
    {
        var p = Profile("us.ein", IdentifierRuleKinds.Mask, mask: "##-#######", unique: true);
        Assert.Equal(IdentifierRules.Check(p, "12-3456789").Normalised, IdentifierRules.Check(p, "123456789").Normalised);
    }

    // ----- the address profile ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void An_address_is_free_text_checked_only_against_the_organisations_own_profile()
    {
        var profile = new AddressProfile([new("line1", true, true), new("locality", true, false), new("region", false, false), new("postal", true, true), new("country", true, false), new("line2", true, false)]);
        Assert.Empty(new SupplierAddress("1 Main Street", null, "Springfield", null, "90210", null).Check(profile));
        Assert.Contains(new SupplierAddress(null, null, "Springfield", null, "90210", null).Check(profile), p => p.Field == "line1" && p.Code == "address_required");
        Assert.Contains(new SupplierAddress("1 Main Street", null, null, "A region that is not used", "90210", null).Check(profile), p => p.Field == "region" && p.Code == "address_unused");
        // No country's postcode or ZIP format is checked: any bounded text is accepted for a used field.
        Assert.Empty(new SupplierAddress("1 Main Street", null, null, null, "SW1A 1AA", null).Check(profile));
        Assert.Empty(new SupplierAddress("1 Main Street", null, null, null, "not a postcode at all", null).Check(profile));
        Assert.Contains(new SupplierAddress(new string('x', 500), null, null, null, "1", null).Check(profile), p => p.Code == "address_length");
    }

    [Fact]
    public void The_neutral_address_profile_uses_every_field_and_requires_none()
    {
        Assert.All(AddressProfile.FieldKeys, k => { Assert.True(AddressProfile.Neutral.Spec(k).Used); Assert.False(AddressProfile.Neutral.Spec(k).Required); });
        Assert.Empty(new SupplierAddress(null, null, null, null, null, null).Check(AddressProfile.Neutral));
    }

    // ----- sample values for the preview and the onboarding form ---------------------------------------------------------------------

    [Fact]
    public void The_preview_shows_both_offsets_of_a_zone_with_daylight_saving_and_an_undecided_profile_says_so()
    {
        var chicago = RegionalComposer.Compose(Doc("""{ "mode": "overrides", "locale": "en-US", "currency": "USD", "timeZone": "America/Chicago" }"""), 1, null, null, null);
        var samples = RegionalSamples.Build(chicago).ToDictionary(x => x.Label, x => x.Value);
        Assert.Contains("-06:00", samples["Offset from UTC in January and July"]);
        Assert.Contains("-05:00", samples["Offset from UTC in January and July"]);
        Assert.Equal("10/16/2026", samples["Date"]);
        Assert.Contains("USD", samples["Amount with its currency code (exports and messages)"]);

        var undecided = RegionalSamples.Build(RegionalSettings.Unset).ToDictionary(x => x.Label, x => x.Value);
        Assert.Contains("not decided", undecided["Date and time, 15 January (UTC 14:30)"]);
        Assert.Contains("no default currency", undecided["Amount"]);
    }

    // ----- notification text: the organisation's formats, constrained tokens ----------------------------------------------------------

    private static readonly NotificationPolicy Policy = Parse();
    private static NotificationPolicy Parse() { var (p, m) = NotificationPolicyParser.Parse(PackFiles.Read("notifications/notification-policy.json")); Assert.Empty(m); return p!; }
    private static readonly MessageTemplate Template = new("Due {{due.date}}", "Due {{due.date}} / {{due.date.iso}}. Sent {{now.date}} | {{now.datetime}} | {{now.utc}}. Worth {{contract.value}} ({{contract.currency}}).");

    private static (string Subject, string Body) Render(string locale, string zone, string? currency = "CAD")
    {
        var context = new RenderContext("REF-1", "Title", "Internal", "Pat", "/x", new DateOnly(2026, 10, 16), 12, 30, null,
            new DateTime(2026, 10, 15, 22, 30, 0, DateTimeKind.Utc), 1234.5m, currency, string.IsNullOrEmpty(locale) ? null : new LocaleFormatter(locale, zone, null));
        return new NotificationRenderer().Render(Policy, Template, context, ClassificationScale.Default, TermSet.Neutral());
    }

    [Theory]
    [InlineData("en-AU", "Australia/Brisbane", "16/10/2026", "16/10/2026")]
    [InlineData("en-US", "America/Chicago", "10/16/2026", "10/15/2026")]
    [InlineData("de-DE", "Europe/Berlin", "16.10.2026", "16.10.2026")]
    public void A_notification_writes_its_dates_in_the_organisations_locale_and_zone_but_keeps_machine_values_invariant(string locale, string zone, string due, string sent)
    {
        var (subject, body) = Render(locale, zone);
        Assert.Equal("Due " + due, subject);
        Assert.Contains($"Due {due} / 2026-10-16.", body);                    // the ISO value is the same for every organisation
        Assert.Contains($"Sent {sent} |", body);                              // the send time is a local date in the organisation's zone
        Assert.Contains("| 2026-10-15T22:30:00Z.", body);                     // UTC ISO 8601 with a Z, never localised
        Assert.Matches(@"1[.,\s\u00A0]?234[.,]50\W*CAD", body);            // the amount is written in the locale's number format with its currency code
    }

    [Fact]
    public void With_no_organisation_formats_decided_a_notification_uses_invariant_machine_safe_text()
    {
        var (subject, body) = Render("", "");
        Assert.Equal("Due 2026-10-16", subject);
        Assert.Contains("Sent 2026-10-15 |", body);
        Assert.Contains("CAD", body);
    }

    [Fact]
    public void An_amount_placeholder_is_empty_when_the_contract_has_no_currency_rather_than_a_guessed_one()
    {
        var (_, body) = Render("en-US", "America/Chicago", currency: null);
        Assert.Contains("Worth  ()", body);
    }

    [Theory]
    [InlineData("{{due.date.local}}")] [InlineData("{{now.format}}")] [InlineData("{{contract.value.number}}")] [InlineData("{{due.date|yyyy-MM-dd}}")] [InlineData("{{now.utc.offset}}")]
    public void An_unknown_date_or_amount_token_fails_policy_validation(string token)
    {
        var json = PackFiles.Read("notifications/notification-policy.json");
        var broken = json.Replace("{{contract.reference}}", token);
        Assert.NotEqual(json, broken);
        var (policy, parsed) = NotificationPolicyParser.Parse(broken);
        var messages = parsed.Concat(policy is null ? [] : NotificationPolicyParser.Validate(policy, ClassificationScale.Default));
        Assert.Contains(messages, m => m.Severity == ValidationSeverity.Error && (m.Code == "placeholder_unknown" || m.Code == "template_syntax"));
    }

    [Fact]
    public void The_placeholder_vocabulary_is_closed_and_names_the_display_and_machine_forms()
    {
        foreach (var p in new[] { "due.date", "due.date.iso", "now.date", "now.datetime", "now.utc", "contract.value", "contract.currency" }) Assert.Contains(p, PolicyVocabulary.Placeholders);
        Assert.Equal(PolicyVocabulary.Placeholders.Count, PolicyVocabulary.Placeholders.Distinct().Count());
    }
}
