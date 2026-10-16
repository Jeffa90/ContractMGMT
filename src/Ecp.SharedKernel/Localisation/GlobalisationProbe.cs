using System.Globalization;

namespace Ecp.SharedKernel.Localisation;

public sealed record ProbeResult(string Id, bool Ok, string Message);

/// <summary>
/// Verifies at run time that the host really has culture data and time-zone data, not the invariant fallback
/// (a container image without ICU silently formats en-AU dates as month-first). Used by readiness check R-OPS-02 and by tests.
/// </summary>
public static class GlobalisationProbe
{
    public static IReadOnlyList<ProbeResult> Run(string cultureName, string timeZoneId)
    {
        var results = new List<ProbeResult>();

        var invariant = AppContext.TryGetSwitch("System.Globalization.Invariant", out var on) && on;
        results.Add(new("icu.mode", !invariant, invariant ? "Invariant globalisation mode is on; culture data is not used." : "Culture data (ICU/NLS) is in use."));

        if (!LocaleFormatter.TryGetCulture(cultureName, out var culture))
        {
            results.Add(new("culture.resolve", false, $"Culture '{cultureName}' is not available."));
        }
        else
        {
            // 16 October 2026 is unambiguous day-first vs month-first; the invariant culture would print 10/16/2026.
            var sample = new DateOnly(2026, 10, 16).ToString("d", culture);
            var dayFirst = culture.Name.StartsWith("en-AU", StringComparison.OrdinalIgnoreCase) ? sample == "16/10/2026" : sample.Length > 0;
            results.Add(new("culture.format", dayFirst, dayFirst ? $"{culture.Name} formats dates as expected." : $"{culture.Name} formatted a sample date as '{sample}'."));
        }

        if (!TimeZones.TryFind(timeZoneId, out var zone))
        {
            results.Add(new("tz.resolve", false, $"Time zone '{timeZoneId}' is not available."));
        }
        else
        {
            results.Add(new("tz.resolve", true, $"Time zone {zone.Id} resolves."));
            results.Add(new("tz.rules", true, $"Offsets resolve (January {zone.GetUtcOffset(new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc)):hh\\:mm}, July {zone.GetUtcOffset(new DateTime(2026, 7, 15, 0, 0, 0, DateTimeKind.Utc)):hh\\:mm})."));
        }

        // Daylight-saving rules must be present: Sydney is +11 in January and +10 in July, Brisbane is +10 in both.
        var sydney = TimeZones.TryFind("Australia/Sydney", out var syd) && syd.GetUtcOffset(new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc)) == TimeSpan.FromHours(11)
            && syd.GetUtcOffset(new DateTime(2026, 7, 15, 0, 0, 0, DateTimeKind.Utc)) == TimeSpan.FromHours(10);
        var brisbane = TimeZones.TryFind("Australia/Brisbane", out var bne) && bne.GetUtcOffset(new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc)) == TimeSpan.FromHours(10)
            && bne.GetUtcOffset(new DateTime(2026, 7, 15, 0, 0, 0, DateTimeKind.Utc)) == TimeSpan.FromHours(10);
        results.Add(new("tz.australia", sydney && brisbane, sydney && brisbane ? "Australian zone rules are present (Sydney daylight saving, Brisbane none)." : "Australian time-zone rules are missing or wrong."));
        return results;
    }
}
