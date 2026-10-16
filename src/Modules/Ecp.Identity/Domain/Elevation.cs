using Ecp.SharedKernel;

namespace Ecp.Identity.Domain;

public enum ElevationKind { Support, Emergency }

public enum ElevationState { PendingConfirmation, Active, Ended, Expired }

/// <summary>
/// The closed sets of permissions an elevation may carry. Support access is read-only. Emergency access adds only the account and role recovery actions an installation
/// administrator needs to restore a tenant's own administrators; it never includes a write to a contract, a Variation, an approval, an unlock or configuration publication,
/// so an Effective record cannot be changed under it (ACCESS_CONTROL_SPEC section 7).
/// </summary>
public static class ElevationPermissions
{
    private static readonly string[] SupportSet = ["contracts.read", "documents.download", "suppliers.read", "audit.read", "config.history.read"];
    private static readonly string[] EmergencyExtra = ["users.manage", "roles.delegate", "rolemappings.manage", "audit.read_admin"];

    public static IReadOnlySet<string> For(ElevationKind kind)
        => new HashSet<string>(kind == ElevationKind.Emergency ? SupportSet.Concat(EmergencyExtra) : SupportSet, StringComparer.Ordinal);

    /// <summary>Permissions no elevation may ever carry (checked by tests and by <see cref="Elevation.Open"/>).</summary>
    public static readonly IReadOnlySet<string> NeverGranted = new HashSet<string>(StringComparer.Ordinal)
    {
        "contracts.edit_owned", "contracts.create", "contracts.unlock_emergency", "contracts.close", "contracts.suspend", "contracts.submit_legal", "contracts.submit_approval",
        "approvals.action", "variations.create", "renewals.create", "documents.upload", "legal.review", "legal.clear", "legal.markup", "disposition.approve", "disposition.request",
        "records.read_privileged", "classification.lower", "migration.commit", "migration.legacy_load", "migration.rollback", "config.publish", "secrets.manage", "records.export"
    };
}

/// <summary>
/// A reason-based, time-limited grant that lets an installation administrator work in one tenant (ACCESS_CONTROL_SPEC section 7). It is stored in the tenant it targets, so the tenant's
/// own administrators can see it while active and afterwards. It never impersonates a user and never carries the person's credentials: the person acts as themselves, bounded by the
/// permissions named here. Every audit event written under it carries its id.
/// </summary>
public sealed class Elevation : ITenantOwned
{
    public const int MaxDurationMinutes = 240;
    public static readonly TimeSpan ConfirmationWindow = TimeSpan.FromMinutes(30);

    private Elevation() { }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public ElevationKind Kind { get; private set; }
    /// <summary>The installation administrator who asked for the elevation (a user of the system tenant).</summary>
    public Guid InitiatorUserId { get; private set; }
    public string InitiatorLabel { get; private set; } = "";
    /// <summary>The person who acts under it. Today the same person; recorded separately so a future hand-over is explicit.</summary>
    public Guid ActingUserId { get; private set; }
    public string ActingLabel { get; private set; } = "";
    public string Reason { get; private set; } = "";
    public string ClassificationCeiling { get; private set; } = "";
    public string PermissionsCsv { get; private set; } = "";
    public int DurationMinutes { get; private set; }
    public bool ConfirmationRequired { get; private set; }
    public DateTime RequestedUtc { get; private set; }
    public DateTime? StartUtc { get; private set; }
    public DateTime? ExpiresUtc { get; private set; }
    public Guid? ConfirmedByUserId { get; private set; }
    public string? ConfirmedByLabel { get; private set; }
    public DateTime? EndedUtc { get; private set; }
    public string? EndedByLabel { get; private set; }
    public string? EndReason { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public IReadOnlySet<string> Permissions => new HashSet<string>(PermissionsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);

    public static Elevation Open(Guid tenantId, ElevationKind kind, Guid initiatorUserId, string initiatorLabel, string reason, TimeSpan duration, string ceiling,
        bool confirmationRequired, DateTime nowUtc)
    {
        var why = Guard.NotBlank(reason, "Reason", 1000);
        if (why.Length < 10) throw new DomainException("reason_too_short", "Explain why access is needed (at least ten characters).");
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromMinutes(MaxDurationMinutes))
            throw new DomainException("invalid_duration", $"An elevation lasts at most {MaxDurationMinutes / 60} hours. Extending it needs a new reasoned request.");
        if (tenantId == SystemTenant.Id) throw new DomainException("invalid_target", "An elevation targets a customer tenant, not the installation itself.");
        var permissions = ElevationPermissions.For(kind);
        if (permissions.Overlaps(ElevationPermissions.NeverGranted)) throw new InvalidOperationException("An elevation may never carry a write to business records.");
        var e = new Elevation
        {
            Id = Ids.New(), TenantId = Guard.NotEmpty(tenantId, nameof(tenantId)), Kind = kind, InitiatorUserId = Guard.NotEmpty(initiatorUserId, nameof(initiatorUserId)),
            InitiatorLabel = Guard.NotBlank(initiatorLabel, "Initiator", 200), ActingUserId = initiatorUserId, ActingLabel = initiatorLabel, Reason = why,
            ClassificationCeiling = Guard.NotBlank(ceiling, "Classification ceiling", 50), PermissionsCsv = string.Join(',', permissions.OrderBy(p => p, StringComparer.Ordinal)),
            DurationMinutes = (int)Math.Ceiling(duration.TotalMinutes), ConfirmationRequired = confirmationRequired, RequestedUtc = Guard.Utc(nowUtc, nameof(nowUtc))
        };
        if (!confirmationRequired) e.Start(nowUtc);
        return e;
    }

    private void Start(DateTime nowUtc) { StartUtc = nowUtc; ExpiresUtc = nowUtc.AddMinutes(DurationMinutes); }

    public ElevationState StateAt(DateTime nowUtc)
    {
        if (EndedUtc is not null) return ElevationState.Ended;
        if (StartUtc is null) return nowUtc < RequestedUtc + ConfirmationWindow ? ElevationState.PendingConfirmation : ElevationState.Expired;
        return nowUtc < ExpiresUtc ? ElevationState.Active : ElevationState.Expired;
    }

    public bool IsActiveAt(DateTime nowUtc) => StateAt(nowUtc) == ElevationState.Active;

    /// <summary>A second installation administrator confirms an elevation that needs it. The person who asked cannot confirm their own request.</summary>
    public void Confirm(Guid confirmerUserId, string confirmerLabel, DateTime nowUtc)
    {
        if (StateAt(nowUtc) != ElevationState.PendingConfirmation) throw new DomainException("not_pending", "This elevation is not waiting for confirmation.");
        if (confirmerUserId == InitiatorUserId) throw new DomainException("second_person_required", "A second installation administrator must confirm this request; you cannot confirm your own.");
        ConfirmedByUserId = confirmerUserId; ConfirmedByLabel = Guard.NotBlank(confirmerLabel, "Confirmed by", 200);
        Start(nowUtc);
    }

    public void End(string endedBy, string? reason, DateTime nowUtc)
    {
        if (EndedUtc is not null) return;
        EndedUtc = Guard.Utc(nowUtc, nameof(nowUtc)); EndedByLabel = Guard.NotBlank(endedBy, "Ended by", 200); EndReason = Guard.OptionalBounded(reason, "Reason", 1000);
    }
}
