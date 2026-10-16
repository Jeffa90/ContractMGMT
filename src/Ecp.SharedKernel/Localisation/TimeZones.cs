namespace Ecp.SharedKernel.Localisation;

/// <summary>How an ambiguous local time (clocks going back) is resolved when converting to UTC.</summary>
public enum AmbiguousTimePolicy { EarlierInstant, LaterInstant }

/// <summary>
/// Time-zone conversion. Instants are stored and exchanged in UTC; local times exist only for display and for user input,
/// converted with the organisation's (or site's) time zone. Zone ids are stored as IANA identifiers; on Windows the
/// equivalent Windows id is resolved when the IANA id is not available (ASM / TENANCY_AND_ORGANISATION_SPEC).
/// </summary>
public static class TimeZones
{
    public static bool TryFind(string? id, out TimeZoneInfo zone)
    {
        zone = TimeZoneInfo.Utc;
        if (string.IsNullOrWhiteSpace(id)) return false;
        var candidate = id.Trim();
        if (TryFindExact(candidate, out zone)) return true;
        if (OperatingSystem.IsWindows() && TimeZoneInfo.TryConvertIanaIdToWindowsId(candidate, out var windows) && windows is not null)
            return TryFindExact(windows, out zone);
        if (!OperatingSystem.IsWindows() && TimeZoneInfo.TryConvertWindowsIdToIanaId(candidate, out var iana) && iana is not null)
            return TryFindExact(iana, out zone);
        return false;
    }

    public static TimeZoneInfo Find(string? id)
        => TryFind(id, out var zone) ? zone : throw new DomainException("invalid_time_zone", $"Time zone '{id}' was not recognised.");

    private static bool TryFindExact(string id, out TimeZoneInfo zone)
    {
        try { zone = TimeZoneInfo.FindSystemTimeZoneById(id); return true; }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException) { zone = TimeZoneInfo.Utc; return false; }
    }

    /// <summary>Converts a UTC instant to the zone's local date and time (Kind Unspecified).</summary>
    public static DateTime ToLocal(DateTime utc, TimeZoneInfo zone)
        => DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(Guard.Utc(utc, nameof(utc)), zone), DateTimeKind.Unspecified);

    public static DateOnly LocalDate(DateTime utc, TimeZoneInfo zone) => DateOnly.FromDateTime(ToLocal(utc, zone));

    /// <summary>
    /// Converts a local wall-clock time to a UTC instant. A time that does not exist (clocks going forward) is refused;
    /// an ambiguous time (clocks going back) is resolved by <paramref name="policy"/>.
    /// </summary>
    public static DateTime ToUtc(DateTime local, TimeZoneInfo zone, AmbiguousTimePolicy policy = AmbiguousTimePolicy.EarlierInstant)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(unspecified))
            throw new DomainException("invalid_local_time", $"{local:yyyy-MM-dd HH:mm} does not exist in time zone {zone.Id} (clocks go forward).");
        if (zone.IsAmbiguousTime(unspecified))
        {
            var offsets = zone.GetAmbiguousTimeOffsets(unspecified).OrderByDescending(o => o).ToArray(); // larger offset = earlier instant
            var offset = policy == AmbiguousTimePolicy.EarlierInstant ? offsets[0] : offsets[^1];
            return DateTime.SpecifyKind(unspecified - offset, DateTimeKind.Utc);
        }
        return DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeToUtc(unspecified, zone), DateTimeKind.Utc);
    }

    /// <summary>The UTC instant at which the given local calendar date begins in the zone (used for due-date and reminder boundaries).</summary>
    public static DateTime StartOfLocalDayUtc(DateOnly date, TimeZoneInfo zone)
    {
        var midnight = date.ToDateTime(TimeOnly.MinValue);
        // Some zones skip midnight on a DST change day: use the first valid instant of that day.
        for (var minutes = 0; minutes < 24 * 60; minutes += 30)
        {
            var candidate = midnight.AddMinutes(minutes);
            if (!zone.IsInvalidTime(candidate)) return ToUtc(candidate, zone, AmbiguousTimePolicy.EarlierInstant);
        }
        throw new DomainException("invalid_local_time", $"No valid local time exists on {date:yyyy-MM-dd} in {zone.Id}.");
    }
}
