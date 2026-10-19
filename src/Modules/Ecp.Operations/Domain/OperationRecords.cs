using Ecp.SharedKernel;

namespace Ecp.Operations.Domain;

/// <summary>
/// A time-limited claim on a singleton piece of work (a backup, a restore validation, a reminder scan) so that two instances of the application never do it at once (docs/OPERATION_LEASES.md).
/// A lease expires if its owner stops renewing it, so a crashed instance cannot block the work for ever. Every takeover bumps <see cref="Generation"/>, which a long-running owner can use as a fencing token.
/// </summary>
public sealed class OperationLease : ITenantOwned
{
    private OperationLease() { }
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; } = SystemTenant.Id;
    public string Name { get; private set; } = "";
    /// <summary>"installation" or the tenant id the lease is scoped to.</summary>
    public string ScopeKey { get; private set; } = "";
    public string OwnerId { get; private set; } = "";
    public string OwnerDescription { get; private set; } = "";
    public DateTime AcquiredUtc { get; private set; }
    public DateTime HeartbeatUtc { get; private set; }
    public DateTime ExpiresUtc { get; private set; }
    public DateTime? ReleasedUtc { get; private set; }
    public int Generation { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public bool IsActiveAt(DateTime nowUtc) => ReleasedUtc is null && ExpiresUtc > nowUtc;

    public static OperationLease Create(string name, string scopeKey, string ownerId, string ownerDescription, DateTime nowUtc, TimeSpan ttl)
    {
        var lease = new OperationLease { Id = Guid.NewGuid(), Name = name, ScopeKey = scopeKey };
        lease.Take(ownerId, ownerDescription, nowUtc, ttl);
        return lease;
    }

    /// <summary>Starts a new tenure for <paramref name="ownerId"/>. The caller has already decided the lease is free, expired, released or being taken over deliberately.</summary>
    public void Take(string ownerId, string ownerDescription, DateTime nowUtc, TimeSpan ttl)
    {
        OwnerId = ownerId; OwnerDescription = Clip(ownerDescription, 200);
        AcquiredUtc = Guard.Utc(nowUtc, nameof(nowUtc)); HeartbeatUtc = nowUtc; ExpiresUtc = nowUtc + ttl; ReleasedUtc = null; Generation++;
    }

    public void Renew(DateTime nowUtc, TimeSpan ttl) { HeartbeatUtc = Guard.Utc(nowUtc, nameof(nowUtc)); ExpiresUtc = nowUtc + ttl; }
    public void Release(DateTime nowUtc) { ReleasedUtc = Guard.Utc(nowUtc, nameof(nowUtc)); ExpiresUtc = nowUtc; }
    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max];
}

public static class MaintenanceCauses
{
    public const string Manual = "Manual";
    public const string RestoreInProgress = "RestoreInProgress";
    public const string RestoreValidationPending = "RestoreValidationPending";
    public const string RestoreFailed = "RestoreFailed";
    /// <summary>An upgrade is applying migrations (the schema is changing) or has stopped part-way and is waiting for an operator.</summary>
    public const string UpgradeInProgress = "UpgradeInProgress";
    public const string UpgradeFailed = "UpgradeFailed";
    public static bool IsRestoreRelated(string cause) => cause is RestoreInProgress or RestoreValidationPending or RestoreFailed;
    public static bool IsUpgradeRelated(string cause) => cause is UpgradeInProgress or UpgradeFailed;
    /// <summary>Causes that cannot be left by accident: leaving needs an explicit acknowledgement.</summary>
    public static bool IsGuarded(string cause) => IsRestoreRelated(cause) || IsUpgradeRelated(cause);
}

/// <summary>The single installation-wide maintenance flag. One row, replaced in place; every transition is audited.</summary>
public sealed class MaintenanceState : ITenantOwned
{
    public static readonly Guid RowId = new("00000000-0000-0000-0000-0000000000b1");

    private MaintenanceState() { }
    public Guid Id { get; private set; } = RowId;
    public Guid TenantId { get; private set; } = SystemTenant.Id;
    public bool Enabled { get; private set; }
    public string Cause { get; private set; } = MaintenanceCauses.Manual;
    public string Reason { get; private set; } = "";
    public DateTime? SinceUtc { get; private set; }
    public string ChangedBy { get; private set; } = "";
    public DateTime UpdatedUtc { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public static MaintenanceState Create(DateTime nowUtc) => new() { UpdatedUtc = Guard.Utc(nowUtc, nameof(nowUtc)) };

    public void Enable(string cause, string reason, string by, DateTime nowUtc)
    {
        Enabled = true; Cause = cause; Reason = Clip(reason, 500); ChangedBy = Clip(by, 200); SinceUtc = Guard.Utc(nowUtc, nameof(nowUtc)); UpdatedUtc = nowUtc;
    }

    public void Disable(string reason, string by, DateTime nowUtc)
    {
        Enabled = false; Cause = MaintenanceCauses.Manual; Reason = Clip(reason, 500); ChangedBy = Clip(by, 200); SinceUtc = null; UpdatedUtc = Guard.Utc(nowUtc, nameof(nowUtc));
    }

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max];
}

public static class OperationKinds
{
    public const string Backup = "Backup";
    public const string Restore = "Restore";
    public const string Validation = "Validation";
    public const string SupportBundle = "SupportBundle";
    public const string Evidence = "Evidence";
    public const string Upgrade = "Upgrade";
    public const string UpgradeRehearsal = "UpgradeRehearsal";
    public const string RollbackEvidence = "RollbackEvidence";
    public const string UpgradePreflight = "UpgradePreflight";
}

public static class OperationStatuses
{
    public const string Running = "Running";
    /// <summary>The operation finished and every check passed. For a backup this means the integrity check passed as well.</summary>
    public const string Completed = "Completed";
    public const string CompletedWithWarnings = "CompletedWithWarnings";
    public const string Failed = "Failed";
    /// <summary>A backup that did not finish. Its files are never a valid recovery point.</summary>
    public const string Incomplete = "Incomplete";
    public const string NotVerified = "NotVerified";
}

/// <summary>A history row for a backup, restore, validation or evidence export. Holds a sanitised summary only: never a secret, a connection string or business content.</summary>
public sealed class OperationRun : ITenantOwned
{
    private OperationRun() { }
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; } = SystemTenant.Id;
    public string Kind { get; private set; } = "";
    public string Status { get; private set; } = OperationStatuses.Running;
    /// <summary>The immutable backup id, or the target description of a restore.</summary>
    public string Reference { get; private set; } = "";
    public string Reason { get; private set; } = "";
    public string OperatorId { get; private set; } = "";
    public string OperatorName { get; private set; } = "";
    public string CorrelationId { get; private set; } = "";
    public DateTime StartedUtc { get; private set; }
    public DateTime? CompletedUtc { get; private set; }
    public string Summary { get; private set; } = "";
    public string DetailJson { get; private set; } = "{}";
    public byte[] RowVersion { get; private set; } = [];

    public static OperationRun Start(string kind, string reference, string reason, string operatorId, string operatorName, string correlationId, DateTime nowUtc) => new()
    {
        Id = Guid.NewGuid(), Kind = kind, Reference = Clip(reference, 200), Reason = Clip(reason, 500), OperatorId = Clip(operatorId, 200), OperatorName = Clip(operatorName, 200),
        CorrelationId = Clip(correlationId, 100), StartedUtc = Guard.Utc(nowUtc, nameof(nowUtc))
    };

    public void Finish(string status, string summary, string detailJson, DateTime nowUtc)
    {
        Status = status; Summary = Clip(summary, 1000); DetailJson = detailJson; CompletedUtc = Guard.Utc(nowUtc, nameof(nowUtc));
    }

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max];
}

/// <summary>
/// What the upgrade tooling knows about a migration it has verified or applied: its identifier and the checksum of the SQL it generates, so a migration that is later modified in place is detected. One row per module and
/// migration; rows are added and never changed (docs/UPGRADES.md, "Migration safety"). An installation upgraded before this table existed has none until the first upgrade adopts its history after checking it against the frozen baseline.
/// </summary>
public sealed class MigrationRecord : ITenantOwned
{
    private MigrationRecord() { }
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; } = SystemTenant.Id;
    public string Module { get; private set; } = "";
    public string MigrationId { get; private set; } = "";
    public string Checksum { get; private set; } = "";
    public string Generator { get; private set; } = "";
    public string PlatformVersion { get; private set; } = "";
    /// <summary>"applied" when the upgrade tooling applied it, "adopted" when it was already applied and was checked against the frozen baseline.</summary>
    public string Origin { get; private set; } = "";
    public Guid? UpgradeRunId { get; private set; }
    public DateTime RecordedUtc { get; private set; }

    public static MigrationRecord Create(string module, string migrationId, string checksum, string generator, string platformVersion, string origin, Guid? upgradeRunId, DateTime nowUtc) => new()
    {
        Id = Guid.NewGuid(), Module = module, MigrationId = migrationId, Checksum = checksum, Generator = generator, PlatformVersion = platformVersion, Origin = origin, UpgradeRunId = upgradeRunId,
        RecordedUtc = Guard.Utc(nowUtc, nameof(nowUtc))
    };
}

/// <summary>
/// A warning or limit that a named person has accepted on the record: an upgrade pre-flight warning, a readiness warning, a mock scanner. It is the explicit acceptance evidence that lets "Ready with warnings" count as
/// Passed (D14.13). It never removes the warning: the original outcome stays what it was.
/// </summary>
public sealed class AcceptedException : ITenantOwned
{
    private AcceptedException() { }
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; } = SystemTenant.Id;
    /// <summary>What kind of thing was accepted: <c>preflight.warning</c> or <c>readiness.warning</c>.</summary>
    public string Kind { get; private set; } = "";
    /// <summary>The stable code of the warning, for example <c>UPF-040</c>.</summary>
    public string Code { get; private set; } = "";
    /// <summary>Where it applies: <c>installation</c> or a short description such as an environment name.</summary>
    public string Scope { get; private set; } = "installation";
    public string Reason { get; private set; } = "";
    public string AcceptedBy { get; private set; } = "";
    public string AcceptedByName { get; private set; } = "";
    public DateTime AcceptedUtc { get; private set; }
    public DateTime ExpiresUtc { get; private set; }
    public DateTime? RevokedUtc { get; private set; }
    public string? RevokedBy { get; private set; }
    public string CorrelationId { get; private set; } = "";

    public bool IsActiveAt(DateTime nowUtc) => RevokedUtc is null && ExpiresUtc > nowUtc;

    public static AcceptedException Accept(string kind, string code, string scope, string reason, string by, string byName, DateTime nowUtc, DateTime expiresUtc, string correlationId)
        => new()
        {
            Id = Guid.NewGuid(), Kind = Guard.NotBlank(kind, nameof(kind), 40), Code = Guard.NotBlank(code, nameof(code), 60), Scope = Guard.NotBlank(scope, nameof(scope), 200),
            Reason = Guard.NotBlank(reason, nameof(reason), 500), AcceptedBy = Guard.NotBlank(by, nameof(by), 200), AcceptedByName = Guard.NotBlank(byName, nameof(byName), 200),
            AcceptedUtc = Guard.Utc(nowUtc, nameof(nowUtc)), ExpiresUtc = Guard.Utc(expiresUtc, nameof(expiresUtc)), CorrelationId = Guard.NotBlank(correlationId, nameof(correlationId), 100),
        };

    public void Revoke(string by, DateTime nowUtc) { RevokedUtc = Guard.Utc(nowUtc, nameof(nowUtc)); RevokedBy = Guard.NotBlank(by, nameof(by), 200); }
}

/// <summary>
/// The installation's email provider settings: non-secret values and secret <em>references</em> only, never a secret. One row; each save increases <see cref="Revision"/> and changes <see cref="Fingerprint"/>, which
/// is what a test result is tied to, so a changed setting can never inherit an old pass.
/// </summary>
public sealed class EmailConfiguration : ITenantOwned
{
    public static readonly Guid RowId = new("00000000-0000-0000-0000-0000000000e1");

    private EmailConfiguration() { }
    public Guid Id { get; private set; } = RowId;
    public Guid TenantId { get; private set; } = SystemTenant.Id;
    /// <summary>smtp, graph, dev-capture, demo-log, mock or disabled.</summary>
    public string ProviderKey { get; private set; } = "";
    public string? FromAddress { get; private set; }
    public string? Host { get; private set; }
    public int Port { get; private set; } = 587;
    public bool UseTls { get; private set; } = true;
    public string? UserRef { get; private set; }
    public string? PasswordRef { get; private set; }
    public int Revision { get; private set; }
    public string Fingerprint { get; private set; } = "";
    public DateTime UpdatedUtc { get; private set; }
    public string UpdatedBy { get; private set; } = "";
    public byte[] RowVersion { get; private set; } = [];

    public static EmailConfiguration Create() => new();

    public void Set(string providerKey, string? from, string? host, int port, bool useTls, string? userRef, string? passwordRef, string fingerprint, string by, DateTime nowUtc)
    {
        ProviderKey = Guard.NotBlank(providerKey, nameof(providerKey), 20); FromAddress = Clip(from, 320); Host = Clip(host, 255); Port = port; UseTls = useTls;
        UserRef = Clip(userRef, 300); PasswordRef = Clip(passwordRef, 300); Fingerprint = Guard.NotBlank(fingerprint, nameof(fingerprint), 64); Revision++;
        UpdatedBy = Guard.NotBlank(by, nameof(by), 200); UpdatedUtc = Guard.Utc(nowUtc, nameof(nowUtc));
    }

    private static string? Clip(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim().Length <= max ? s.Trim() : s.Trim()[..max];
}

/// <summary>
/// The outcome of one email test: a connectivity probe of the provider and a delivery attempt of a fixed synthetic message. Holds a masked recipient and safe diagnostics only: no secret, no server banner, no message content
/// beyond the fixed text. It is tied to the configuration fingerprint it tested.
/// </summary>
public sealed class EmailTestResult : ITenantOwned
{
    private EmailTestResult() { }
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; } = SystemTenant.Id;
    public string ConfigFingerprint { get; private set; } = "";
    public string ProviderKey { get; private set; } = "";
    public DateTime TestedUtc { get; private set; }
    public string TestedBy { get; private set; } = "";
    public string RecipientMasked { get; private set; } = "";
    /// <summary>Passed, Failed or NotVerified.</summary>
    public string ConnectivityOutcome { get; private set; } = "";
    public string ConnectivityCode { get; private set; } = "";
    public string ConnectivityDetail { get; private set; } = "";
    public string DeliveryOutcome { get; private set; } = "";
    public string DeliveryCode { get; private set; } = "";
    public string DeliveryDetail { get; private set; } = "";
    /// <summary>True when the provider delivers nothing to a real mailbox (development capture, demonstration log, mock outbox).</summary>
    public bool Simulated { get; private set; }
    public bool Succeeded { get; private set; }
    public string CorrelationId { get; private set; } = "";

    public static EmailTestResult Record(string fingerprint, string providerKey, DateTime nowUtc, string by, string recipientMasked, string connectivityOutcome, string connectivityCode, string connectivityDetail,
        string deliveryOutcome, string deliveryCode, string deliveryDetail, bool simulated, string correlationId) => new()
    {
        Id = Guid.NewGuid(), ConfigFingerprint = Guard.NotBlank(fingerprint, nameof(fingerprint), 64), ProviderKey = Guard.NotBlank(providerKey, nameof(providerKey), 20), TestedUtc = Guard.Utc(nowUtc, nameof(nowUtc)),
        TestedBy = Guard.NotBlank(by, nameof(by), 200), RecipientMasked = Clip(recipientMasked, 320), ConnectivityOutcome = Guard.NotBlank(connectivityOutcome, nameof(connectivityOutcome), 20),
        ConnectivityCode = Clip(connectivityCode, 40), ConnectivityDetail = Clip(connectivityDetail, 400), DeliveryOutcome = Guard.NotBlank(deliveryOutcome, nameof(deliveryOutcome), 20), DeliveryCode = Clip(deliveryCode, 40),
        DeliveryDetail = Clip(deliveryDetail, 400), Simulated = simulated, Succeeded = deliveryOutcome == "Passed" && (connectivityOutcome == "Passed" || simulated), CorrelationId = Guard.NotBlank(correlationId, nameof(correlationId), 100),
    };

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max];
}
