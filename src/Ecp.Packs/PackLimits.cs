namespace Ecp.Packs;

/// <summary>Size and shape limits applied to every package before anything is parsed further. Defaults are deliberately small: a pack is a handful of JSON documents.</summary>
public sealed class PackLimits
{
    public long MaxPackageBytes { get; set; } = 8L * 1024 * 1024;
    public int MaxEntries { get; set; } = 64;
    public long MaxEntryBytes { get; set; } = 2L * 1024 * 1024;
    public long MaxTotalExpandedBytes { get; set; } = 16L * 1024 * 1024;
    /// <summary>The largest allowed ratio of expanded to stored size. Applies to entries above <see cref="RatioFloorBytes"/>, so a small repetitive document is not mistaken for a bomb.</summary>
    public int MaxCompressionRatio { get; set; } = 50;
    public long RatioFloorBytes { get; set; } = 64 * 1024;
    public int MaxJsonDepth { get; set; } = 32;
    public int MaxPathLength { get; set; } = 120;
    public int MaxPathDepth { get; set; } = 3;

    public static PackLimits Default => new();
}
