using System.Reflection;

namespace Ecp.Packs;

/// <summary>The single authoritative platform version that package compatibility ranges are checked against. It comes from the build (<c>Version</c> in Directory.Build.props).</summary>
public static class PlatformVersion
{
    public static SemVer Current { get; } = Resolve();

    private static SemVer Resolve()
    {
        var text = typeof(PlatformVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (text is not null)
        {
            var plus = text.IndexOf('+', StringComparison.Ordinal);
            if (plus >= 0) text = text[..plus];
            if (SemVer.TryParse(text, out var v)) return v;
        }
        return new SemVer(1, 0, 0, null);
    }
}
