using Ecp.SharedKernel;

namespace Ecp.Configuration.Domain;

public static class PackSourceTypes
{
    public const string Shipped = "Shipped";
    public const string TenantUploaded = "TenantUploaded";
}

/// <summary>
/// A solution-pack package installed into a tenant. The verified package bytes are immutable and kept in package storage (referenced here by a relative name, never a public path); this row records what was
/// verified when it was installed. A tenant's pack is invisible to every other tenant. A version is never deleted while referenced; it can be retired so it is no longer offered.
/// </summary>
public sealed class InstalledPack : ITenantOwned
{
    private InstalledPack() { }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public string PackKey { get; private set; } = "";
    public string Version { get; private set; } = "";
    public string PackageId { get; private set; } = "";
    public string PublisherId { get; private set; } = "";
    public string PublisherName { get; private set; } = "";
    public string DisplayName { get; private set; } = "";
    public bool Synthetic { get; private set; }
    public string PackageSha256 { get; private set; } = "";
    public string ContentDigest { get; private set; } = "";
    public string ManifestJson { get; private set; } = "";
    /// <summary>How the signature was judged at installation (a <c>TrustStatus</c> name). Trust is judged again every time the pack is used, because keys can be revoked.</summary>
    public string SignatureStatus { get; private set; } = "";
    public string? SignatureAlgorithm { get; private set; }
    public string? TrustKeyId { get; private set; }
    public string VerificationStatus { get; private set; } = "";
    public string SourceType { get; private set; } = PackSourceTypes.TenantUploaded;
    public string StorageReference { get; private set; } = "";
    public string ValidationOutcome { get; private set; } = "";
    public string ValidationJson { get; private set; } = "[]";
    public string CompatibilityJson { get; private set; } = "[]";
    public string UploadedBy { get; private set; } = "";
    public DateTime UploadedUtc { get; private set; }
    public string Status { get; private set; } = Installed;
    public DateTime InstalledUtc { get; private set; }
    public string InstalledBy { get; private set; } = "";
    public DateTime? RetiredUtc { get; private set; }
    public string? RetiredBy { get; private set; }
    public string? RetiredReason { get; private set; }
    public string CorrelationId { get; private set; } = "";
    public byte[] RowVersion { get; private set; } = [];

    public const string Installed = "Installed";
    public const string Retired = "Retired";

    public bool IsRetired => Status == Retired;

    public static InstalledPack Create(Guid tenantId, string packKey, string version, string packageId, string publisherId, string publisherName, string displayName, bool synthetic,
        string packageSha256, string contentDigest, string manifestJson, string signatureStatus, string? signatureAlgorithm, string? trustKeyId, string verificationStatus,
        string storageReference, string validationOutcome, string validationJson, string compatibilityJson, string uploadedBy, DateTime uploadedUtc, string installedBy, DateTime installedUtc,
        string correlationId) => new()
    {
        Id = Ids.New(), TenantId = Guard.NotEmpty(tenantId, nameof(tenantId)), PackKey = Guard.NotBlank(packKey, "Pack key", 50), Version = Guard.NotBlank(version, "Version", 40),
        PackageId = Guard.NotBlank(packageId, "Package id", 120), PublisherId = Guard.NotBlank(publisherId, "Publisher id", 100), PublisherName = Guard.NotBlank(publisherName, "Publisher", 120),
        DisplayName = Guard.NotBlank(displayName, "Display name", 120), Synthetic = synthetic, PackageSha256 = Guard.NotBlank(packageSha256, "Package hash", 64),
        ContentDigest = Guard.NotBlank(contentDigest, "Content digest", 64), ManifestJson = manifestJson, SignatureStatus = Guard.NotBlank(signatureStatus, "Signature status", 40),
        SignatureAlgorithm = signatureAlgorithm, TrustKeyId = trustKeyId, VerificationStatus = Guard.NotBlank(verificationStatus, "Verification status", 40),
        StorageReference = Guard.NotBlank(storageReference, "Storage reference", 200), ValidationOutcome = Guard.NotBlank(validationOutcome, "Validation outcome", 40), ValidationJson = validationJson,
        CompatibilityJson = compatibilityJson, UploadedBy = Guard.NotBlank(uploadedBy, "Uploaded by", 200), UploadedUtc = Guard.Utc(uploadedUtc, nameof(uploadedUtc)), Status = Installed,
        InstalledBy = Guard.NotBlank(installedBy, "Installed by", 200), InstalledUtc = Guard.Utc(installedUtc, nameof(installedUtc)), CorrelationId = Guard.NotBlank(correlationId, "Correlation id", 100)
    };

    public void Retire(string by, string? reason, DateTime nowUtc)
    {
        if (IsRetired) return;
        Status = Retired; RetiredBy = Guard.NotBlank(by, "Retired by", 200); RetiredReason = Guard.OptionalBounded(reason, "Reason", 500); RetiredUtc = Guard.Utc(nowUtc, nameof(nowUtc));
    }

    public void Reinstate() { Status = Installed; RetiredBy = null; RetiredReason = null; RetiredUtc = null; }
}

/// <summary>Which pack version an organisation works from, who decided, and what was done with it. It is the provenance of the organisation's starting configuration and the baseline for any later upgrade.</summary>
public sealed class PackAssignment : ITenantOwned
{
    private PackAssignment() { }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid OrganisationId { get; private set; }
    public string PackKey { get; private set; } = "";
    public string Version { get; private set; } = "";
    public string PackageSha256 { get; private set; } = "";
    public string SourceType { get; private set; } = "";
    /// <summary><c>PackManager</c> (an administrator assigned it), <c>Bootstrap</c> (the first definitions were installed when first needed), or <c>Setup</c>.</summary>
    public string Origin { get; private set; } = "";
    public string Status { get; private set; } = Active;
    public string AssignedBy { get; private set; } = "";
    public DateTime AssignedUtc { get; private set; }
    /// <summary>What happened to each configuration kind: <c>{ "WorkflowContractLifecycle": "draft created" , ... }</c>. Documents only the outcome; the drafts and releases themselves are ordinary configuration.</summary>
    public string OutcomeJson { get; private set; } = "{}";
    public string CorrelationId { get; private set; } = "";
    public byte[] RowVersion { get; private set; } = [];

    public const string Active = "Active";
    public const string Superseded = "Superseded";
    public const string OriginPackManager = "PackManager";
    public const string OriginBootstrap = "Bootstrap";

    public static PackAssignment Create(Guid tenantId, Guid organisationId, string packKey, string version, string packageSha256, string sourceType, string origin, string assignedBy,
        DateTime nowUtc, string outcomeJson, string correlationId) => new()
    {
        Id = Ids.New(), TenantId = Guard.NotEmpty(tenantId, nameof(tenantId)), OrganisationId = Guard.NotEmpty(organisationId, nameof(organisationId)), PackKey = Guard.NotBlank(packKey, "Pack key", 50),
        Version = Guard.NotBlank(version, "Version", 40), PackageSha256 = Guard.NotBlank(packageSha256, "Package hash", 64), SourceType = Guard.NotBlank(sourceType, "Source", 30),
        Origin = Guard.NotBlank(origin, "Origin", 30), AssignedBy = Guard.NotBlank(assignedBy, "Assigned by", 200), AssignedUtc = Guard.Utc(nowUtc, nameof(nowUtc)), OutcomeJson = outcomeJson,
        CorrelationId = Guard.NotBlank(correlationId, "Correlation id", 100)
    };

    public void Supersede() => Status = Superseded;
}

/// <summary>A publisher's public key a tenant has chosen to trust. Public keys only: nothing here can sign. A key is revoked, never deleted, so earlier verification outcomes stay explicable.</summary>
public sealed class TrustedPublisherKey : ITenantOwned
{
    private TrustedPublisherKey() { }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public string KeyId { get; private set; } = "";
    public string Algorithm { get; private set; } = "";
    public string PublicKeyBase64 { get; private set; } = "";
    public string Fingerprint { get; private set; } = "";
    public string Label { get; private set; } = "";
    public DateTime? NotBeforeUtc { get; private set; }
    public DateTime? NotAfterUtc { get; private set; }
    public string Status { get; private set; } = Active;
    public string RegisteredBy { get; private set; } = "";
    public DateTime RegisteredUtc { get; private set; }
    public string? RevokedBy { get; private set; }
    public DateTime? RevokedUtc { get; private set; }
    public string? RevokedReason { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public const string Active = "Active";
    public const string Revoked = "Revoked";

    public static TrustedPublisherKey Create(Guid tenantId, string keyId, string algorithm, byte[] publicKey, string fingerprint, string label, DateTime? notBefore, DateTime? notAfter, string by, DateTime nowUtc) => new()
    {
        Id = Ids.New(), TenantId = Guard.NotEmpty(tenantId, nameof(tenantId)), KeyId = Guard.NotBlank(keyId, "Key id", 80), Algorithm = Guard.NotBlank(algorithm, "Algorithm", 20),
        PublicKeyBase64 = Convert.ToBase64String(publicKey), Fingerprint = Guard.NotBlank(fingerprint, "Fingerprint", 32), Label = Guard.NotBlank(label, "Label", 120),
        NotBeforeUtc = notBefore, NotAfterUtc = notAfter, RegisteredBy = Guard.NotBlank(by, "Registered by", 200), RegisteredUtc = Guard.Utc(nowUtc, nameof(nowUtc))
    };

    public void Revoke(string by, string? reason, DateTime nowUtc)
    {
        if (Status == Revoked) return;
        Status = Revoked; RevokedBy = Guard.NotBlank(by, "Revoked by", 200); RevokedReason = Guard.OptionalBounded(reason, "Reason", 500); RevokedUtc = Guard.Utc(nowUtc, nameof(nowUtc));
    }

    public byte[] PublicKey => Convert.FromBase64String(PublicKeyBase64);
}
