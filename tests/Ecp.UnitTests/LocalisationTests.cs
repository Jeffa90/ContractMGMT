using System.Globalization;
using Ecp.SharedKernel;
using Ecp.SharedKernel.Localisation;
using Xunit;

namespace Ecp.UnitTests;

[Trait("Category", "Unit")]
public sealed class TimeZoneTests
{
    private static DateTime Utc(int y, int m, int d, int h = 0, int min = 0) => new(y, m, d, h, min, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("Australia/Brisbane", 1, 10)]   // no daylight saving
    [InlineData("Australia/Brisbane", 7, 10)]
    [InlineData("Australia/Sydney", 1, 11)]     // daylight saving in January
    [InlineData("Australia/Sydney", 7, 10)]
    [InlineData("Australia/Melbourne", 1, 11)]
    [InlineData("Australia/Perth", 1, 8)]
    [InlineData("Australia/Perth", 7, 8)]
    [InlineData("Australia/Adelaide", 1, 10.5)]
    [InlineData("Australia/Adelaide", 7, 9.5)]
    [InlineData("Australia/Darwin", 1, 9.5)]
    public void Australian_zones_use_the_correct_offsets(string zone, int month, double expectedHours)
    {
        var tz = TimeZones.Find(zone);
        Assert.Equal(TimeSpan.FromHours(expectedHours), tz.GetUtcOffset(Utc(2026, month, 15, 12)));
    }

    [Fact]
    public void Brisbane_local_date_rolls_over_at_ten_hours_ahead_of_utc()
    {
        var bne = TimeZones.Find("Australia/Brisbane");
        Assert.Equal(new DateOnly(2026, 10, 15), TimeZones.LocalDate(Utc(2026, 10, 15, 13, 59), bne));
        Assert.Equal(new DateOnly(2026, 10, 16), TimeZones.LocalDate(Utc(2026, 10, 15, 14, 0), bne));
        Assert.Equal(Utc(2026, 10, 15, 14), TimeZones.StartOfLocalDayUtc(new DateOnly(2026, 10, 16), bne));
    }

    [Fact]
    public void Sydney_and_Brisbane_differ_by_an_hour_in_summer_and_agree_in_winter()
    {
        var sydney = TimeZones.Find("Australia/Sydney");
        var brisbane = TimeZones.Find("Australia/Brisbane");
        var summer = Utc(2026, 1, 15, 12);
        var winter = Utc(2026, 7, 15, 12);
        Assert.Equal(TimeSpan.FromHours(1), TimeZones.ToLocal(summer, sydney) - TimeZones.ToLocal(summer, brisbane));
        Assert.Equal(TimeSpan.Zero, TimeZones.ToLocal(winter, sydney) - TimeZones.ToLocal(winter, brisbane));
    }

    [Fact]
    public void Windows_and_IANA_identifiers_resolve_to_the_same_zone()
    {
        var iana = TimeZones.Find("Australia/Brisbane");
        var windows = TimeZones.Find("E. Australia Standard Time");
        var instant = Utc(2026, 3, 1, 5);
        Assert.Equal(iana.GetUtcOffset(instant), windows.GetUtcOffset(instant));
    }

    [Fact]
    public void An_unknown_zone_is_refused_with_a_stable_code()
    {
        Assert.False(TimeZones.TryFind("Mars/Olympus_Mons", out _));
        Assert.False(TimeZones.TryFind("", out _));
        var ex = Assert.Throws<DomainException>(() => TimeZones.Find("Mars/Olympus_Mons"));
        Assert.Equal("invalid_time_zone", ex.Code);
    }

    [Fact]
    public void Local_to_utc_round_trips_for_ordinary_times()
    {
        var bne = TimeZones.Find("Australia/Brisbane");
        var utc = TimeZones.ToUtc(new DateTime(2026, 10, 16, 9, 30, 0), bne);
        Assert.Equal(DateTimeKind.Utc, utc.Kind);
        Assert.Equal(Utc(2026, 10, 15, 23, 30), utc);
        Assert.Equal(new DateTime(2026, 10, 16, 9, 30, 0), TimeZones.ToLocal(utc, bne));
    }

    [Fact]
    public void A_time_skipped_by_the_clocks_going_forward_is_refused()
    {
        // Sydney clocks go forward at 02:00 on Sunday 4 October 2026: 02:30 does not exist.
        var sydney = TimeZones.Find("Australia/Sydney");
        var ex = Assert.Throws<DomainException>(() => TimeZones.ToUtc(new DateTime(2026, 10, 4, 2, 30, 0), sydney));
        Assert.Equal("invalid_local_time", ex.Code);
    }

    [Fact]
    public void An_ambiguous_time_is_resolved_by_the_chosen_policy()
    {
        // Sydney clocks go back at 03:00 on Sunday 5 April 2026: 02:30 happens twice (+11 then +10).
        var sydney = TimeZones.Find("Australia/Sydney");
        var local = new DateTime(2026, 4, 5, 2, 30, 0);
        var earlier = TimeZones.ToUtc(local, sydney, AmbiguousTimePolicy.EarlierInstant);
        var later = TimeZones.ToUtc(local, sydney, AmbiguousTimePolicy.LaterInstant);
        Assert.Equal(Utc(2026, 4, 4, 15, 30), earlier);
        Assert.Equal(Utc(2026, 4, 4, 16, 30), later);
    }

    [Fact]
    public void Daylight_saving_change_days_still_have_a_start_of_day()
    {
        var sydney = TimeZones.Find("Australia/Sydney");
        Assert.Equal(Utc(2026, 10, 3, 14), TimeZones.StartOfLocalDayUtc(new DateOnly(2026, 10, 4), sydney)); // 00:00 AEST (+10), before the 02:00 change
        Assert.Equal(Utc(2026, 4, 4, 13), TimeZones.StartOfLocalDayUtc(new DateOnly(2026, 4, 5), sydney));   // 00:00 AEDT (+11)
    }

    [Fact]
    public void Only_utc_instants_are_accepted_for_conversion()
    {
        var bne = TimeZones.Find("Australia/Brisbane");
        Assert.Throws<DomainException>(() => TimeZones.ToLocal(new DateTime(2026, 10, 16, 1, 0, 0, DateTimeKind.Local), bne));
        Assert.Throws<DomainException>(() => TimeZones.ToLocal(new DateTime(2026, 10, 16, 1, 0, 0, DateTimeKind.Unspecified), bne));
    }
}

[Trait("Category", "Unit")]
public sealed class LocaleFormatterTests
{
    private static LocaleFormatter Au() => new("en-AU", "Australia/Brisbane", "AUD");
    private static LocaleFormatter Us() => new("en-US", "America/New_York", "USD");

    [Fact]
    public void Dates_are_day_first_in_en_AU_and_month_first_in_en_US()
    {
        var date = new DateOnly(2026, 10, 16);
        Assert.Equal("16/10/2026", Au().Date(date));
        Assert.Equal("10/16/2026", Us().Date(date));
    }

    [Fact]
    public void An_instant_is_shown_in_the_organisation_zone_with_its_offset()
    {
        var text = Au().DateTime(new DateTime(2026, 10, 16, 3, 5, 0, DateTimeKind.Utc));
        Assert.StartsWith("16/10/2026", text);
        Assert.EndsWith("(UTC+10:00)", text);
        var melbourne = new LocaleFormatter("en-AU", "Australia/Melbourne", "AUD").DateTime(new DateTime(2026, 10, 16, 3, 5, 0, DateTimeKind.Utc));
        Assert.EndsWith("(UTC+11:00)", melbourne); // daylight saving since 4 October 2026
        var newYork = Us().DateTime(new DateTime(2026, 1, 16, 3, 5, 0, DateTimeKind.Utc));
        Assert.EndsWith("(UTC-05:00)", newYork);
    }

    [Fact]
    public void The_cultures_own_currency_uses_its_symbol_and_other_currencies_use_the_iso_code()
    {
        Assert.Equal("$1,234.50", Au().Money(1234.5m));
        Assert.Equal("$1,234.50", Au().Money(1234.5m, "AUD"));
        Assert.Equal("-$5.00", Au().Money(-5m));
        Assert.Equal("USD 1,234.50", Au().Money(1234.5m, "USD"));
        Assert.Equal("NZD 1,000.00", Au().Money(1000m, "nzd"));
        Assert.Equal("$1,234.50", Us().Money(1234.5m));
    }

    [Theory]
    [InlineData("JPY", 1235, "JPY 1,235")]
    [InlineData("KWD", 1.5, "KWD 1.500")]
    [InlineData("EUR", 99.999, "EUR 100.00")]
    public void Currency_minor_units_follow_iso_4217(string currency, double amount, string expected)
        => Assert.Equal(expected, Au().Money((decimal)amount, currency));

    [Theory]
    [InlineData("")]
    [InlineData("AU")]
    [InlineData("AUDD")]
    [InlineData("A1D")]
    public void An_invalid_currency_code_is_refused(string code)
        => Assert.Equal("invalid_currency", Assert.Throws<DomainException>(() => Au().Money(1m, code)).Code);

    [Fact]
    public void Numbers_use_the_culture_separators()
    {
        Assert.Equal("1,234,567.50", Au().Number(1234567.5m));
        var german = new LocaleFormatter("de-DE", "Europe/Berlin", "EUR"); // an additional locale needs no code change
        Assert.Equal("1.234.567,50", german.Number(1234567.5m));
        Assert.Equal("16.10.2026", german.Date(new DateOnly(2026, 10, 16)));
    }

    [Fact]
    public void Unknown_cultures_and_zones_are_refused()
    {
        Assert.Throws<DomainException>(() => new LocaleFormatter("xx-NOPE", "Australia/Brisbane", "AUD"));
        Assert.Throws<DomainException>(() => new LocaleFormatter("en-AU", "Nowhere/Land", "AUD"));
        Assert.Throws<DomainException>(() => new LocaleFormatter("en-AU", "Australia/Brisbane", "dollars"));
    }

    [Fact]
    public void Formatting_is_independent_of_the_threads_current_culture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            Assert.Equal("16/10/2026", Au().Date(new DateOnly(2026, 10, 16)));
            Assert.Equal("$1,234.50", Au().Money(1234.5m));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }
}

[Trait("Category", "Unit")]
public sealed class LocalisationOptionsAndProbeTests
{
    [Fact]
    public void The_platform_assumes_no_country_locale_currency_or_zone_and_the_empty_configuration_is_valid()
    {
        var options = new LocalisationOptions();
        options.Validate();
        Assert.Equal("", options.DefaultCulture);
        Assert.Equal("", options.DefaultTimeZoneId);
        Assert.Equal("", options.DefaultCurrencyCode);
        Assert.False(options.HasInstallationDefaults);
        // The page language is a separate matter from the regional settings.
        Assert.Equal("en", options.FallbackLanguage);
    }

    [Fact]
    public void An_installation_may_name_fall_backs_for_output_no_organisation_owns()
    {
        var options = new LocalisationOptions { DefaultCulture = "en-NZ", DefaultTimeZoneId = "Pacific/Auckland", DefaultCurrencyCode = "NZD", SupportedCultures = ["en-NZ", "en-AU", "mi-NZ"] };
        options.Validate();
        Assert.True(options.HasInstallationDefaults);
    }

    [Fact]
    public void Invalid_configuration_is_refused()
    {
        Assert.Throws<DomainException>(() => new LocalisationOptions { DefaultCulture = "zz-ZZ", SupportedCultures = ["zz-ZZ"] }.Validate());
        Assert.Throws<DomainException>(() => new LocalisationOptions { DefaultTimeZoneId = "Nowhere/Land" }.Validate());
        Assert.Throws<DomainException>(() => new LocalisationOptions { DefaultCurrencyCode = "XX" }.Validate());
        Assert.Throws<DomainException>(() => new LocalisationOptions { DefaultCulture = "en-GB", SupportedCultures = ["en-US"] }.Validate()); // must include the default
    }

    [Fact]
    public void This_host_has_real_culture_and_time_zone_data()
    {
        var results = GlobalisationProbe.Run("en-AU", "Australia/Brisbane");
        Assert.All(results, r => Assert.True(r.Ok, $"{r.Id}: {r.Message}"));
        Assert.Contains(results, r => r.Id == "icu.mode");
        Assert.Contains(results, r => r.Id == "culture.samples");
        Assert.Contains(results, r => r.Id == "tz.samples");
    }

    [Fact]
    public void The_probe_needs_no_configured_locale_or_zone()
    {
        var results = GlobalisationProbe.Run();
        Assert.All(results, r => Assert.True(r.Ok, $"{r.Id}: {r.Message}"));
        Assert.DoesNotContain(results, r => r.Id is "culture.resolve" or "tz.resolve");
    }

    [Fact]
    public void The_probe_reports_a_missing_zone_or_culture()
    {
        var results = GlobalisationProbe.Run("xx-NOPE", "Nowhere/Land");
        Assert.Contains(results, r => r is { Id: "culture.resolve", Ok: false });
        Assert.Contains(results, r => r is { Id: "tz.resolve", Ok: false });
    }

    [Fact]
    public void Timestamps_must_be_utc_in_the_domain()
    {
        Assert.Throws<DomainException>(() => Guard.Utc(new DateTime(2026, 10, 16, 1, 0, 0, DateTimeKind.Local), "x"));
        Assert.Equal(DateTimeKind.Utc, Guard.Utc(new DateTime(2026, 10, 16, 1, 0, 0, DateTimeKind.Utc), "x").Kind);
    }
}
