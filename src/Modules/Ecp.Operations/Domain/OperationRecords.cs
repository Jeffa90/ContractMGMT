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
    public static bool IsRestoreRelated(string cause) => cause is RestoreInProgress or RestoreValidationPending or RestoreFailed;
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
