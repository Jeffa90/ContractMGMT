namespace Ecp.Packs;

public enum PackSeverity { Info = 0, Warning = 1, Error = 2 }

/// <summary>
/// One finding about a package: a stable code, a plain-language message, the package entry it concerns (never a filesystem path of the server), what to do about it, and how serious it is.
/// Messages never carry key material, secrets or stack traces.
/// </summary>
public sealed record PackIssue(string Code, PackSeverity Severity, string Message, string? Entry = null, string? Remediation = null)
{
    public bool IsError => Severity == PackSeverity.Error;
}

/// <summary>Stable issue codes. They are part of the contract with administrators and with the downloadable validation report (docs/PACK_SECURITY.md lists them).</summary>
public static class PackCodes
{
    // Archive safety
    public const string NotAnArchive = "PACK-A001";
    public const string TooLarge = "PACK-A002";
    public const string TooManyEntries = "PACK-A003";
    public const string EntryTooLarge = "PACK-A004";
    public const string ExpandedTooLarge = "PACK-A005";
    public const string CompressionRatio = "PACK-A006";
    public const string UnsafePath = "PACK-A007";
    public const string DuplicatePath = "PACK-A008";
    public const string UnsafeFeature = "PACK-A009";
    public const string UnlistedEntry = "PACK-A010";
    public const string DisallowedEntry = "PACK-A011";
    public const string EntryCorrupt = "PACK-A012";

    // Manifest
    public const string ManifestMissing = "PACK-M001";
    public const string ManifestInvalid = "PACK-M002";
    public const string FormatUnsupported = "PACK-M003";
    public const string ManifestField = "PACK-M004";
    public const string ContentDuplicate = "PACK-M005";
    public const string RequiredKindMissing = "PACK-M006";
    public const string KindListMismatch = "PACK-M007";
    public const string ManifestNotCanonical = "PACK-M008";

    // Content hashes
    public const string HashMismatch = "PACK-H001";
    public const string ContentMissing = "PACK-H002";

    // Signature and trust
    public const string SignatureMissing = "PACK-S001";
    public const string SignatureInvalid = "PACK-S002";
    public const string KeyUnknown = "PACK-S003";
    public const string KeyRevoked = "PACK-S004";
    public const string KeyExpired = "PACK-S005";
    public const string KeyNotYetValid = "PACK-S006";
    public const string AlgorithmUnsupported = "PACK-S007";
    public const string SignatureMalformed = "PACK-S008";
    public const string UnsignedSyntheticAllowed = "PACK-S009";
    public const string UnsignedNotPermitted = "PACK-S010";

    // Compatibility
    public const string PlatformTooOld = "PACK-C001";
    public const string PlatformTooNew = "PACK-C002";
    public const string SchemaUnsupported = "PACK-C003";
    public const string KindUnknownRequired = "PACK-C004";
    public const string KindUnknownOptional = "PACK-C005";
    public const string DependencyMissing = "PACK-C006";
    public const string VersionRule = "PACK-C007";

    // Duplicates and store
    public const string DuplicateIdentical = "PACK-D001";
    public const string DuplicateDifferent = "PACK-D002";

    // Content validation
    public const string ContentInvalid = "PACK-V001";
    public const string ContentUnsafe = "PACK-V002";
    public const string ContentMismatch = "PACK-V003";
    public const string ContentWarning = "PACK-V004";

    // Installation and assignment
    public const string NotInstalled = "PACK-I001";
    public const string Retired = "PACK-I002";
    public const string NotPermitted = "PACK-I003";
    public const string Conflict = "PACK-I004";
}
