using System.Text.RegularExpressions;

namespace Ecp.Packs;

/// <summary>Semantic version (major.minor.patch with an optional pre-release tag). Build metadata is ignored for ordering.</summary>
public sealed partial record SemVer(int Major, int Minor, int Patch, string? PreRelease) : IComparable<SemVer>
{
    [GeneratedRegex(@"^(0|[1-9]\d{0,5})\.(0|[1-9]\d{0,5})\.(0|[1-9]\d{0,5})(?:-([0-9A-Za-z][0-9A-Za-z.-]{0,30}))?(?:\+[0-9A-Za-z.-]{1,40})?$", RegexOptions.NonBacktracking)]
    private static partial Regex Pattern();

    public static bool TryParse(string? text, out SemVer version)
    {
        version = default!;
        if (string.IsNullOrEmpty(text)) return false;
        var m = Pattern().Match(text);
        if (!m.Success) return false;
        version = new SemVer(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value), m.Groups[4].Success ? m.Groups[4].Value : null);
        return true;
    }

    public static SemVer Parse(string text) => TryParse(text, out var v) ? v : throw new FormatException($"'{text}' is not a semantic version.");

    public int CompareTo(SemVer? other)
    {
        if (other is null) return 1;
        var c = Major.CompareTo(other.Major); if (c != 0) return c;
        c = Minor.CompareTo(other.Minor); if (c != 0) return c;
        c = Patch.CompareTo(other.Patch); if (c != 0) return c;
        if (PreRelease is null && other.PreRelease is null) return 0;
        if (PreRelease is null) return 1;          // a release is later than any pre-release of it
        if (other.PreRelease is null) return -1;
        return string.CompareOrdinal(PreRelease, other.PreRelease);
    }

    public static bool operator <(SemVer a, SemVer b) => a.CompareTo(b) < 0;
    public static bool operator >(SemVer a, SemVer b) => a.CompareTo(b) > 0;
    public static bool operator <=(SemVer a, SemVer b) => a.CompareTo(b) <= 0;
    public static bool operator >=(SemVer a, SemVer b) => a.CompareTo(b) >= 0;

    public override string ToString() => $"{Major}.{Minor}.{Patch}" + (PreRelease is null ? "" : "-" + PreRelease);
}
