using System.Globalization;

namespace Ecp.SharedKernel.Localisation;

public sealed record ProbeResult(string Id, bool Ok, string Message);

/// <summary>
/// Verifies at run time that the host really has culture data and time-zone data, not the invariant fallback (a container image without ICU silently formats every culture's dates month-first).
/// The probe asks about samples from several regions so that it proves the data are present without favouring any country; an installation fall-back locale or zone, when configured, is checked too.
/// Used by readiness check R-OPS-02 and by tests.
/// </summary>
public static class GlobalisationProbe
{
    public static IReadOnlyList<ProbeResult> Run(string? cultureName = null, string? timeZoneId = null)
    {
        var results = new List<ProbeResult>();

        var invariant = AppContext.TryGetSwitch("System.Globalization.Invariant", out var on) && on;
        results.Add(new("icu.mode", !invariant, invariant ? "Invariant globalisation mode is on; culture data is not used." : "Culture data (ICU/NLS) is in use."));

        // 16 October 2026 is unambiguous day-first vs month-first: a host without culture data prints the invariant 10/16/2026 for both.
        var dayFirst = LocaleFormatter.TryGetCulture("en-GB", out var gb) && new DateOnly(2026, 10, 16).ToString("d", gb) == "16/10/2026";
        var monthFirst = LocaleFormatter.TryGetCulture("en-US", out var us) && new DateOnly(2026, 10, 16).ToString("d", us) == "10/16/2026";
        var decimalComma = LocaleFormatter.TryGetCulture("de-DE", out var de) && 1234.5m.ToString("N2", de) == "1.234,50";
        results.Add(new("culture.samples", dayFirst && monthFirst && decimalComma, dayFirst && monthFirst && decimalComma ? "Day-first, month-first and decimal-comma cultures format as expected." : "Culture data are missing or wrong for the sample cultures."));

        // Daylight-saving rules must be present: Chicago is -6 in January and -5 in July, London is 0 and +1, Kolkata is +5:30 all year.
        static TimeSpan At(string zone, int month) => TimeZones.TryFind(zone, out var z) ? z.GetUtcOffset(new DateTime(2026, month, 15, 0, 0, 0, DateTimeKind.Utc)) : TimeSpan.MinValue;
        var zonesOk = At("America/Chicago", 1) == TimeSpan.FromHours(-6) && At("America/Chicago", 7) == TimeSpan.FromHours(-5)
            && At("Europe/London", 1) == TimeSpan.Zero && At("Europe/London", 7) == TimeSpan.FromHours(1)
            && At("Asia/Kolkata", 1) == new TimeSpan(5, 30, 0) && At("Asia/Kolkata", 7) == new TimeSpan(5, 30, 0);
        results.Add(new("tz.samples", zonesOk, zonesOk ? "Time-zone rules are present (daylight saving and fixed offsets)." : "Time-zone rules are missing or wrong for the sample zones."));

        if (!string.IsNullOrWhiteSpace(cultureName))
            results.Add(LocaleFormatter.TryGetCulture(cultureName, out var culture) ? new("culture.resolve", true, $"Culture {culture.Name} resolves.") : new("culture.resolve", false, $"Culture '{cultureName}' is not available."));

        if (!string.IsNullOrWhiteSpace(timeZoneId))
        {
            if (!TimeZones.TryFind(timeZoneId, out var zone)) results.Add(new("tz.resolve", false, $"Time zone '{timeZoneId}' is not available."));
            else
            {
                results.Add(new("tz.resolve", true, $"Time zone {zone.Id} resolves."));
                results.Add(new("tz.rules", true, $"Offsets resolve (January {zone.GetUtcOffset(new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc)):hh\\:mm}, July {zone.GetUtcOffset(new DateTime(2026, 7, 15, 0, 0, 0, DateTimeKind.Utc)):hh\\:mm})."));
            }
        }
        return results;
    }
}
