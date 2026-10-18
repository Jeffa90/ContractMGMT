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
