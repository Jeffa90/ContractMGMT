using Ecp.Audit;
using Ecp.Identity.Access;
using Ecp.Identity.Domain;
using Ecp.Identity.Persistence;
using Ecp.Persistence;
using Ecp.SharedKernel;
using Ecp.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Ecp.Identity;

public sealed record OpenElevationRequest(Guid TenantId, ElevationKind Kind, string Reason, TimeSpan Duration, string? ClassificationCeiling = null);

public sealed record ElevationDto(
    Guid Id, Guid TenantId, ElevationKind Kind, string InitiatorLabel, string ActingLabel, string Reason, string ClassificationCeiling, IReadOnlyList<string> Permissions,
    ElevationState State, DateTime RequestedUtc, DateTime? StartUtc, DateTime? ExpiresUtc, string? ConfirmedByLabel, DateTime? EndedUtc, string? EndedByLabel, string? EndReason,
    bool ConfirmationRequired);

public sealed record ElevationAction(DateTime OccurredUtc, string Action, string EntityType, string EntityId, string Outcome);

/// <summary>An active elevation as the host and the access layer see it.</summary>
public sealed record ElevationGrant(Guid ElevationId, Guid TenantId, Guid UserId, ElevationKind Kind, DateTime ExpiresUtc, string Reason, string InitiatorLabel, string Ceiling,
    IReadOnlySet<string> Permissions);

/// <summary>The elevation the current request runs under, set by the host from the signed session after the elevation has been re-validated (never from request input).</summary>
public sealed class ElevationContext
{
    public ElevationGrant? Current { get; private set; }
    public void Activate(ElevationGrant grant) => Current = grant;
    public void Clear() => Current = null;
}

/// <summary>Support and emergency access (IDN-03). See <see cref="Elevation"/> for the rules.</summary>
public interface IElevationService
{
    /// <summary>
    /// Requests an elevation into one tenant. The caller must hold <c>support.elevate</c> at installation level and must have authenticated recently. An Emergency elevation
    /// waits for a second installation administrator where the installation requires it.
    /// </summary>
    Task<ElevationDto> OpenAsync(OpenElevationRequest request, CancellationToken ct = default);

    /// <summary>A second installation administrator confirms a pending elevation (recent authentication required).</summary>
    Task<ElevationDto> ConfirmAsync(Guid tenantId, Guid elevationId, CancellationToken ct = default);

    /// <summary>Ends an elevation now. Allowed to the person it was opened for, to any installation administrator, and to the target tenant's own administrators.</summary>
    Task EndAsync(Guid tenantId, Guid elevationId, string? reason, CancellationToken ct = default);

    /// <summary>The elevations of the current tenant, active and past, for its administrators (<c>audit.read_admin</c>).</summary>
    Task<IReadOnlyList<ElevationDto>> ListForCurrentTenantAsync(bool activeOnly, CancellationToken ct = default);

    /// <summary>What was done under an elevation: the audit events that carry its id. Visible to the target tenant's administrators and to the installation administrator who used it.</summary>
    Task<IReadOnlyList<ElevationAction>> ActionsAsync(Guid elevationId, CancellationToken ct = default);

    /// <summary>Trusted: re-validates an elevation named by a session, in the target tenant's scope. Returns null when it is not active, not for this person, or the tenant is not active.</summary>
    Task<ElevationGrant?> ResolveActiveAsync(Guid tenantId, Guid elevationId, Guid userId, CancellationToken ct = default);
}

internal sealed class ElevationService(
    IdentityDbContext db, IUnitOfWork uow, IAuditWriter audit, TenantContext tenant, IClock clock, CurrentUser currentUser, IAccessService access, IClassificationScaleProvider scales,
    ITenantDirectory tenants, IAuditEventSource events, ReauthenticationContext reauth, IdentityOptions options) : IElevationService
{
    private static readonly TimeSpan ReauthWindow = TimeSpan.FromMinutes(10);
    private const string SupportPermission = "support.elevate";

    private async Task<(Guid UserId, string Label)> RequireInstallationAdministratorAsync(CancellationToken ct)
    {
        var id = currentUser.UserId ?? throw new ForbiddenException("Sign in to request access.");
        if (!await access.HoldsAnywhereAsync(SupportPermission, ct)) throw new ForbiddenException("Only an installation administrator can open support or emergency access.");
        var label = await db.Users.AsNoTracking().Where(u => u.Id == id).Select(u => u.DisplayName).FirstOrDefaultAsync(ct) ?? id.ToString();
        return (id, label);
    }

    public async Task<ElevationDto> OpenAsync(OpenElevationRequest r, CancellationToken ct = default)
    {
        var (userId, label) = await RequireInstallationAdministratorAsync(ct);
        reauth.Require(clock.UtcNow, ReauthWindow);
        var home = tenant.RequireTenantId();
        var scale = await scales.GetAsync(ct);

        TenantInfo target;
        using (tenant.UseInstallationScope()) target = await tenants.FindAsync(r.TenantId, ct) ?? throw new DomainException("tenant_not_found", "That tenant was not found.");
        if (target.Status != Tenancy.Domain.TenantStatus.Active) throw new DomainException("tenant_not_active", "Access can be opened only to an active tenant.");

        // The ceiling an elevation can see is bounded: by default the tenant's default level, and never above Confidential (a privileged or Restricted record is not support material).
        var ceiling = scale.Canonical(r.ClassificationCeiling) ?? scale.DefaultLevel;
        var confidential = scale.Rank("Confidential");
        if (scale.Rank(ceiling) > (confidential >= 0 ? confidential : scale.Rank(scale.DefaultLevel)))
            throw new DomainException("ceiling_too_high", "Support access cannot see records above Confidential.");

        var confirmationRequired = r.Kind == ElevationKind.Emergency && options.RequireEmergencyConfirmation;
        var elevation = Elevation.Open(r.TenantId, r.Kind, userId, label, r.Reason, r.Duration, ceiling, confirmationRequired, clock.UtcNow);
        using (tenant.Use(r.TenantId))
        {
            await uow.ExecuteAsync(async c =>
            {
                db.Elevations.Add(elevation);
                await db.SaveChangesAsync(c);
                audit.Record(new AuditEntry("elevation.opened", nameof(Elevation), elevation.Id.ToString(), Reason: elevation.Reason, ElevationId: elevation.Id,
                    NewValuesJson: IdentityJson.Of(new { kind = r.Kind.ToString(), initiator = label, homeTenant = home, ceiling, minutes = elevation.DurationMinutes, confirmationRequired, expires = elevation.ExpiresUtc })));
            }, ct);
        }
        return ToDto(elevation);
    }

    public async Task<ElevationDto> ConfirmAsync(Guid tenantId, Guid elevationId, CancellationToken ct = default)
    {
        var (userId, label) = await RequireInstallationAdministratorAsync(ct);
        reauth.Require(clock.UtcNow, ReauthWindow);
        using (tenant.Use(tenantId))
        {
            Elevation elevation = null!;
            await uow.ExecuteAsync(async c =>
            {
                elevation = await db.Elevations.FirstOrDefaultAsync(e => e.Id == elevationId, c) ?? throw new ResourceNotFoundException();
                elevation.Confirm(userId, label, clock.UtcNow);
                await db.SaveChangesAsync(c);
                audit.Record(new AuditEntry("elevation.confirmed", nameof(Elevation), elevation.Id.ToString(), ElevationId: elevation.Id,
                    NewValuesJson: IdentityJson.Of(new { confirmedBy = label, expires = elevation.ExpiresUtc })));
            }, ct);
            return ToDto(elevation);
        }
    }

    public async Task EndAsync(Guid tenantId, Guid elevationId, string? reason, CancellationToken ct = default)
    {
        var callerHome = tenant.RequireTenantId();
        var installationAdministrator = callerHome == SystemTenant.Id && await access.HoldsAnywhereAsync(SupportPermission, ct);
        var caller = currentUser.UserId ?? throw new ForbiddenException("Sign in to end access.");
        string callerLabel;
        if (installationAdministrator) callerLabel = await db.Users.AsNoTracking().Where(u => u.Id == caller).Select(u => u.DisplayName).FirstOrDefaultAsync(ct) ?? caller.ToString();
        else
        {
            if (callerHome != tenantId || !await access.HoldsAnywhereAsync("audit.read_admin", ct)) throw new ForbiddenException("You cannot end this access.");
            callerLabel = await db.Users.AsNoTracking().Where(u => u.Id == caller).Select(u => u.DisplayName).FirstOrDefaultAsync(ct) ?? caller.ToString();
        }
        using (tenant.Use(tenantId))
        {
            await uow.ExecuteAsync(async c =>
            {
                var elevation = await db.Elevations.FirstOrDefaultAsync(e => e.Id == elevationId, c) ?? throw new ResourceNotFoundException();
                if (elevation.EndedUtc is not null) return;
                elevation.End(callerLabel, reason, clock.UtcNow);
                await db.SaveChangesAsync(c);
                audit.Record(new AuditEntry("elevation.ended", nameof(Elevation), elevation.Id.ToString(), Reason: reason, ElevationId: elevation.Id,
                    NewValuesJson: IdentityJson.Of(new { endedBy = callerLabel, early = elevation.ExpiresUtc is { } e && clock.UtcNow < e })));
            }, ct);
        }
    }

    public async Task<IReadOnlyList<ElevationDto>> ListForCurrentTenantAsync(bool activeOnly, CancellationToken ct = default)
    {
        if (!await access.HoldsAnywhereAsync("audit.read_admin", ct)) throw new ForbiddenException("You cannot see elevations.");
        var now = clock.UtcNow;
        var rows = await db.Elevations.AsNoTracking().OrderByDescending(e => e.RequestedUtc).Take(200).ToListAsync(ct);
        return rows.Where(e => !activeOnly || e.StateAt(now) is ElevationState.Active or ElevationState.PendingConfirmation).Select(ToDto).ToList();
    }

    public async Task<IReadOnlyList<ElevationAction>> ActionsAsync(Guid elevationId, CancellationToken ct = default)
    {
        var elevation = await db.Elevations.AsNoTracking().FirstOrDefaultAsync(e => e.Id == elevationId, ct) ?? throw new ResourceNotFoundException();
        var caller = currentUser.UserId;
        if (!await access.HoldsAnywhereAsync("audit.read_admin", ct) && caller != elevation.InitiatorUserId) throw new ForbiddenException("You cannot see this access.");
        return (await events.Events.Where(e => e.ElevationId == elevationId).OrderBy(e => e.Sequence).Select(e => new { e.OccurredUtc, e.Action, e.EntityType, e.EntityId, e.Outcome }).Take(1000).ToListAsync(ct))
            .Select(e => new ElevationAction(e.OccurredUtc, e.Action, e.EntityType, e.EntityId, e.Outcome)).ToList();
    }

    public async Task<ElevationGrant?> ResolveActiveAsync(Guid tenantId, Guid elevationId, Guid userId, CancellationToken ct = default)
    {
        using var _ = tenant.Use(tenantId);
        var info = await tenants.FindAsync(tenantId, ct);
        if (info is not { Status: Tenancy.Domain.TenantStatus.Active }) return null;
        var e = await db.Elevations.AsNoTracking().FirstOrDefaultAsync(x => x.Id == elevationId, ct);
        if (e is null || e.ActingUserId != userId || !e.IsActiveAt(clock.UtcNow)) return null;
        return new ElevationGrant(e.Id, e.TenantId, userId, e.Kind, e.ExpiresUtc!.Value, e.Reason, e.InitiatorLabel, e.ClassificationCeiling, e.Permissions);
    }

    private ElevationDto ToDto(Elevation e) => new(e.Id, e.TenantId, e.Kind, e.InitiatorLabel, e.ActingLabel, e.Reason, e.ClassificationCeiling, e.Permissions.OrderBy(p => p, StringComparer.Ordinal).ToList(),
        e.StateAt(clock.UtcNow), e.RequestedUtc, e.StartUtc, e.ExpiresUtc, e.ConfirmedByLabel, e.EndedUtc, e.EndedByLabel, e.EndReason, e.ConfirmationRequired);
}
