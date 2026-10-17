using Ecp.Audit;
using Ecp.Identity.Access;
using Ecp.Identity.Domain;
using Ecp.Identity.Persistence;
using Ecp.Identity.Providers;
using Ecp.Persistence;
using Ecp.SharedKernel;
using Ecp.SharedKernel.Secrets;
using Ecp.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Text.Json;

namespace Ecp.Identity;

public sealed class IdentityOptions
{
    /// <summary>Configuration mode. Production refuses the Demo provider.</summary>
    public EcpMode Mode { get; set; } = EcpMode.Production;
    /// <summary>Unknown identities are refused unless invited. Self-provisioning is not offered.</summary>
    public bool AllowSelfProvisioning { get; set; }
    /// <summary>An emergency elevation waits for a second installation administrator (on by default in Production; ACCESS_CONTROL_SPEC section 7).</summary>
    public bool RequireEmergencyConfirmation { get; set; }
}

public sealed record UserDto(Guid Id, string DisplayName, string Email, UserStatus Status, DateTime? LastSignInUtc);

public sealed record AssignmentDto(
    Guid Id, Guid UserId, string RoleCode, ViewerKind? ViewerKind, ScopeSet Scope, string? ApproverGroupKey, DateTime EffectiveFromUtc,
    DateTime? EffectiveToUtc, string GrantedByLabel, string? Reason, AssignmentSource Source, bool IsActive, bool PrivilegedAccess = false);

public sealed record GrantRequest(
    Guid UserId, string RoleCode, ViewerKind? ViewerKind, ScopeSet Scope, string? ApproverGroupKey = null, DateTime? EffectiveFromUtc = null,
    DateTime? EffectiveToUtc = null, string? Reason = null, bool ExplicitHighCeiling = false, bool PrivilegedAccess = false);

public sealed record LoginResolution(bool Succeeded, Guid? UserId, string? DenialCode, string? Message)
{
    public static LoginResolution Success(Guid userId) => new(true, userId, null, null);
    public static LoginResolution Denied(string code, string message) => new(false, null, code, message);
}

public sealed record GroupMappingDto(Guid Id, string ProviderKey, string GroupKey, string RoleCode, ViewerKind? ViewerKind, ScopeSet Scope, string? ApproverGroupKey, bool IsActive);

/// <summary>Supplies the tenant's classification scale. The default is the placeholder scale; the Configuration module replaces it with the published tenant policy.</summary>
public interface IClassificationScaleProvider { Task<ClassificationScale> GetAsync(CancellationToken ct = default); }

internal sealed class DefaultClassificationScaleProvider : IClassificationScaleProvider
{
    public Task<ClassificationScale> GetAsync(CancellationToken ct = default) => Task.FromResult(ClassificationScale.Default);
}

public sealed record ApproverInfo(Guid UserId, string DisplayName, string RoleCode, Guid AssignmentId);

/// <summary>Trusted lookups of people by what they hold, for modules that route work (approvals, notifications). Not reachable from request input.</summary>
public interface IAccessDirectory
{
    /// <summary>The access facts of any user of the current tenant (as the evaluator would see them), or null if the user does not exist or is disabled.</summary>
    Task<AccessSubject?> LoadSubjectAsync(Guid userId, CancellationToken ct = default);
    /// <summary>People currently holding an Approver or Backup Approver assignment for the approver group.</summary>
    Task<IReadOnlyList<ApproverInfo>> ApproversAsync(string approverGroupKey, CancellationToken ct = default);
    /// <summary>People who hold (or, for a future date, will hold) an Approver or Backup Approver assignment for the group at the given instant, so a rule can be tried on a date other than today.</summary>
    Task<IReadOnlyList<ApproverInfo>> ApproversAsync(string approverGroupKey, DateTime asOfUtc, CancellationToken ct = default);
    /// <summary>People currently holding the role in the organisation (organisation-level assignments naming it, and tenant-level assignments).</summary>
    Task<IReadOnlyList<Guid>> HoldersOfRoleAsync(string roleCode, Guid organisationId, CancellationToken ct = default);
    Task<string?> DisplayNameAsync(Guid userId, CancellationToken ct = default);
}

/// <summary>Optional: lets delegation treat a parent category as covering its subcategories. Implemented by the Contracts module.</summary>
public interface ICategoryHierarchy
{
    /// <summary>For each category id, the category itself followed by its ancestors.</summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<Guid>>> AncestryAsync(CancellationToken ct = default);
}

/// <summary>The authenticated user for this scope, set by the host from the verified session (never from request input).</summary>
public sealed class CurrentUser
{
    public Guid? UserId { get; private set; }
    public void Set(Guid userId) => UserId = Guard.NotEmpty(userId, nameof(userId));
    public void Clear() => UserId = null;
}

public interface IUserService
{
    Task<Guid> InviteAsync(string displayName, string email, CancellationToken ct = default);
    Task<UserDto?> GetAsync(Guid id, CancellationToken ct = default);
    Task<UserDto?> FindByEmailAsync(string email, CancellationToken ct = default);
    Task<IReadOnlyList<UserDto>> ListAsync(CancellationToken ct = default);
    Task DisableAsync(Guid id, string reason, CancellationToken ct = default);
    Task ReactivateAsync(Guid id, CancellationToken ct = default);

    /// <summary>Links a verified external login to an account (invite-only) and applies identity-group mappings.</summary>
    Task<LoginResolution> ResolveLoginAsync(ExternalLogin login, CancellationToken ct = default);

    /// <summary>Trusted creation of an active user for installation, setup and Demo seeding (no invitation round trip). Linked to a provider identity when given.</summary>
    Task<Guid> CreateActiveTrustedAsync(string displayName, string email, string? providerKey, string? issuer, string? subject, string source, CancellationToken ct = default);
}

public interface IRoleAssignmentService
{
    /// <summary>Grants within the caller's own authority (delegation rules). The caller is the current user.</summary>
    Task<Guid> GrantAsync(GrantRequest request, CancellationToken ct = default);

    /// <summary>Grants on behalf of setup, onboarding publication, Demo seeding or group mapping. Not reachable from request input.</summary>
    Task<Guid> GrantTrustedAsync(GrantRequest request, AssignmentSource source, string grantedByLabel, Guid? mappingId = null, CancellationToken ct = default);

    /// <summary>Ends an assignment. Subject to delegation and last-administrator protection.</summary>
    Task RevokeAsync(Guid assignmentId, string reason, CancellationToken ct = default);

    /// <summary>Ends an assignment that was created by <paramref name="expectedSource"/> (onboarding rollback). Refuses assignments from any other source and administrator roles. Not reachable from request input.</summary>
    Task RevokeTrustedAsync(Guid assignmentId, AssignmentSource expectedSource, string reason, CancellationToken ct = default);

    Task<IReadOnlyList<AssignmentDto>> ListForUserAsync(Guid userId, bool includeEnded = false, CancellationToken ct = default);
    Task<IReadOnlyList<AssignmentDto>> ListAsync(bool activeOnly = true, CancellationToken ct = default);
}

public interface IGroupMappingService
{
    Task<Guid> AddAsync(string providerKey, string groupKey, string roleCode, ViewerKind? viewerKind, ScopeSet scope, string? approverGroupKey, CancellationToken ct = default);
    Task<IReadOnlyList<GroupMappingDto>> ListAsync(CancellationToken ct = default);
    Task DeactivateAsync(Guid id, CancellationToken ct = default);
}

/// <summary>Notified after identity-provider settings are saved, so the host can (re)build its sign-in handlers without a restart.</summary>
public interface IIdentityProviderListener
{
    Task ChangedAsync(Guid tenantId, IdentityProviderSettings settings, CancellationToken ct);
}

public interface IProviderRegistry
{
    Task<IReadOnlyList<IdentityProviderSettings>> ListAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ConfigurationIssue>> SaveAsync(IdentityProviderSettings settings, CancellationToken ct = default);
    Task RecordTestSignInAsync(string key, CancellationToken ct = default);
    Task<DateTime?> TestSignInAsync(string key, CancellationToken ct = default);
}

/// <summary>Authorisation for the current user. Denials are audited. Application services call this; UI hiding is cosmetic.</summary>
public interface IAccessService
{
    Guid? CurrentUserId { get; }
    Task<AccessSubject?> GetSubjectAsync(CancellationToken ct = default);
    Task<AccessDecision> CheckAsync(string permission, ResourceDescriptor resource, CancellationToken ct = default);

    /// <summary>Same decision without auditing a denial (for deciding which buttons to show).</summary>
    Task<AccessDecision> CheckSilentAsync(string permission, ResourceDescriptor resource, CancellationToken ct = default);

    /// <summary>Throws <see cref="ResourceNotFoundException"/> (404) or <see cref="ForbiddenException"/> (403) when not allowed.</summary>
    Task RequireAsync(string permission, ResourceDescriptor resource, CancellationToken ct = default);

    Task<IReadOnlyList<AccessGrant>> GrantsAsync(string permission, CancellationToken ct = default);
    Task<bool> HoldsAnywhereAsync(string permission, CancellationToken ct = default);
    Task<ClassificationScale> ClassificationScaleAsync(CancellationToken ct = default);

    /// <summary>What-if evaluation for access simulation: decides for an arbitrary subject without auditing a denial.</summary>
    Task<AccessDecision> SimulateAsync(AccessSubject subject, string permission, ResourceDescriptor resource, CancellationToken ct = default);
}

internal static class IdentityJson
{
    public static string Of(object value) => JsonSerializer.Serialize(value);
}

internal sealed class UserService(
    IdentityDbContext db, IUnitOfWork uow, IAuditWriter audit, ITenantContext tenant, ActorContext actor, IClock clock,
    IServiceProvider services) : IUserService
{
    public async Task<Guid> InviteAsync(string displayName, string email, CancellationToken ct = default)
    {
        var tenantId = tenant.RequireTenantId();
        var user = UserProfile.Invite(tenantId, displayName, email, clock.UtcNow);
        return await uow.ExecuteAsync(async c =>
        {
            db.Users.Add(user);
            try { await db.SaveChangesAsync(c); }
            catch (DbUpdateException ex) when (DbExceptions.IsUniqueViolation(ex)) { throw new DomainException("user_exists", "A user with that email already exists."); }
            audit.Record(new AuditEntry("user.invited", nameof(UserProfile), user.Id.ToString(), NewValuesJson: IdentityJson.Of(new { user.EmailNormalised })));
            return user.Id;
        }, ct);
    }

    public Task<UserDto?> GetAsync(Guid id, CancellationToken ct = default)
        => db.Users.AsNoTracking().Where(u => u.Id == id).Select(u => new UserDto(u.Id, u.DisplayName, u.Email, u.Status, u.LastSignInUtc)).FirstOrDefaultAsync(ct);

    public Task<UserDto?> FindByEmailAsync(string email, CancellationToken ct = default)
    {
        var n = UserProfile.NormaliseEmail(email);
        return db.Users.AsNoTracking().Where(u => u.EmailNormalised == n).Select(u => new UserDto(u.Id, u.DisplayName, u.Email, u.Status, u.LastSignInUtc)).FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<UserDto>> ListAsync(CancellationToken ct = default)
        => await db.Users.AsNoTracking().OrderBy(u => u.DisplayName).Select(u => new UserDto(u.Id, u.DisplayName, u.Email, u.Status, u.LastSignInUtc)).ToListAsync(ct);

    public async Task DisableAsync(Guid id, string reason, CancellationToken ct = default)
    {
        var why = Guard.NotBlank(reason, "Reason", 1000);
        await uow.ExecuteAsync(async c =>
        {
            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, c) ?? throw new DomainException("user_not_found", "The user was not found.");
            await LastAdministrator.EnsureNotLastAsync(db, user.Id, null, clock.UtcNow, c, "disable this account");
            user.Disable();
            await db.SaveChangesAsync(c);
            audit.Record(new AuditEntry("user.disabled", nameof(UserProfile), id.ToString(), Reason: why));
        }, ct);
    }

    public async Task ReactivateAsync(Guid id, CancellationToken ct = default)
    {
        await uow.ExecuteAsync(async c =>
        {
            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, c) ?? throw new DomainException("user_not_found", "The user was not found.");
            user.Reactivate();
            await db.SaveChangesAsync(c);
            audit.Record(new AuditEntry("user.reactivated", nameof(UserProfile), id.ToString()));
        }, ct);
    }

    public async Task<Guid> CreateActiveTrustedAsync(string displayName, string email, string? providerKey, string? issuer, string? subject, string source, CancellationToken ct = default)
    {
        var tenantId = tenant.RequireTenantId();
        var now = clock.UtcNow;
        var user = UserProfile.Invite(tenantId, displayName, email, now);
        user.Activate(now);
        return await uow.ExecuteAsync(async c =>
        {
            db.Users.Add(user);
            if (providerKey is not null && issuer is not null && subject is not null)
                db.ExternalIdentities.Add(ExternalIdentity.Link(tenantId, user.Id, providerKey, issuer, subject, now));
            try { await db.SaveChangesAsync(c); }
            catch (DbUpdateException ex) when (DbExceptions.IsUniqueViolation(ex)) { throw new DomainException("user_exists", "A user with that email or identity already exists."); }
            audit.Record(new AuditEntry("user.created", nameof(UserProfile), user.Id.ToString(), NewValuesJson: IdentityJson.Of(new { source, user.EmailNormalised })));
            return user.Id;
        }, ct);
    }

    public async Task<LoginResolution> ResolveLoginAsync(ExternalLogin login, CancellationToken ct = default)
    {
        var tenantId = tenant.RequireTenantId();
        var now = clock.UtcNow;
        var link = await db.ExternalIdentities.FirstOrDefaultAsync(x => x.Issuer == login.Issuer && x.Subject == login.Subject, ct);
        UserProfile? user = link is null ? null : await db.Users.FirstOrDefaultAsync(u => u.Id == link.UserId, ct);

        if (user is null)
        {
            // Never link by email alone: only an invited, not-yet-linked account, and only when the provider vouches for the address.
            if (login.Email is not null && login.EmailTrusted)
            {
                var normalised = UserProfile.NormaliseEmail(login.Email);
                var invited = await db.Users.FirstOrDefaultAsync(u => u.EmailNormalised == normalised && u.Status == UserStatus.Invited, ct);
                if (invited is not null && !await db.ExternalIdentities.AnyAsync(x => x.UserId == invited.Id, ct))
                {
                    user = invited;
                    link = ExternalIdentity.Link(tenantId, invited.Id, login.ProviderKey, login.Issuer, login.Subject, now);
                    db.ExternalIdentities.Add(link);
                    invited.Activate(now);
                }
            }
            if (user is null)
                return LoginResolution.Denied("not_invited", "No account has been invited for this identity. Ask an administrator to invite you.");
        }
        else if (user.Status == UserStatus.Disabled)
        {
            return LoginResolution.Denied("user_disabled", "This account is disabled.");
        }

        actor.Type = ActorType.User; actor.ActorId = user.Id.ToString(); actor.Source = "SignIn";
        user.RecordSignIn(now);
        link!.Seen(now);
        var activated = user.Status == UserStatus.Active && user.ActivatedUtc == now;
        await uow.ExecuteAsync(async c =>
        {
            await db.SaveChangesAsync(c);
            audit.Record(new AuditEntry(activated ? "user.activated" : "user.signed_in", nameof(UserProfile), user.Id.ToString(),
                NewValuesJson: IdentityJson.Of(new { provider = login.ProviderKey })));
        }, ct);

        await ApplyGroupMappingsAsync(user.Id, login, ct);
        return LoginResolution.Success(user.Id);
    }

    private async Task ApplyGroupMappingsAsync(Guid userId, ExternalLogin login, CancellationToken ct)
    {
        var mappings = await db.GroupMappings.AsNoTracking().Where(m => m.ProviderKey == login.ProviderKey).ToListAsync(ct);
        if (mappings.Count == 0) return;
        var assignments = services.GetRequiredService<IRoleAssignmentService>();
        var existing = await db.RoleAssignments.AsNoTracking()
            .Where(a => a.UserId == userId && a.Source == AssignmentSource.GroupMapping && a.SourceMappingId != null).ToListAsync(ct);
        var now = clock.UtcNow;
        foreach (var mapping in mappings)
        {
            var inGroup = login.Groups.Contains(mapping.GroupKey, StringComparer.Ordinal);
            var current = existing.FirstOrDefault(a => a.SourceMappingId == mapping.Id && a.IsActiveAt(now));
            if (inGroup && mapping.IsActive && current is null)
                await assignments.GrantTrustedAsync(new GrantRequest(userId, mapping.RoleCode, mapping.ViewerKind, mapping.Scope, mapping.ApproverGroupKey,
                    Reason: $"Identity group mapping for {mapping.GroupKey}"), AssignmentSource.GroupMapping, "group-mapping", mapping.Id, ct);
            else if ((!inGroup || !mapping.IsActive) && current is not null && !login.GroupsOverage)
                await ((RoleAssignmentService)assignments).EndMappedAsync(current.Id, ct); // user left the group: authority ends with membership
        }
    }
}

/// <summary>Last-administrator protection (ACCESS_CONTROL_SPEC 6): the platform refuses to remove the last holder of a top-level administrative role.</summary>
internal static class LastAdministrator
{
    private static readonly string[] Protected = [RoleCodes.SystemAdministrator, RoleCodes.TenantAdministrator];

    /// <summary>Refuses when ending <paramref name="assignmentId"/> (or disabling <paramref name="userId"/>) would leave a protected role with no active, enabled holder.</summary>
    public static async Task EnsureNotLastAsync(IdentityDbContext db, Guid? userId, Guid? assignmentId, DateTime nowUtc, CancellationToken ct, string action)
    {
        foreach (var role in Protected)
        {
            var holders = await db.RoleAssignments.AsNoTracking().Where(a => a.RoleCode == role).ToListAsync(ct);
            var activeUsers = holders.Where(a => a.IsActiveAt(nowUtc)).Select(a => a.UserId).Distinct().ToList();
            if (activeUsers.Count == 0) continue;
            var disabled = await db.Users.AsNoTracking().Where(u => activeUsers.Contains(u.Id) && u.Status == UserStatus.Disabled).Select(u => u.Id).ToListAsync(ct);
            var remaining = holders.Where(a => a.IsActiveAt(nowUtc) && !disabled.Contains(a.UserId)
                && a.Id != assignmentId && (userId is null || a.UserId != userId)).Select(a => a.UserId).Distinct().Count();
            var affected = holders.Any(a => a.IsActiveAt(nowUtc) && ((assignmentId is not null && a.Id == assignmentId) || (userId is not null && a.UserId == userId)));
            if (affected && remaining == 0)
                throw new DomainException("last_administrator", $"You cannot {action}: it would leave the platform without an active {role}.");
        }
    }
}

internal sealed class RoleAssignmentService(
    IdentityDbContext db, IUnitOfWork uow, IAuditWriter audit, ITenantContext tenant, IClock clock, CurrentUser currentUser,
    IClassificationScaleProvider scales, IOrganisationStructureService structure, IServiceProvider services, IAccessService access, ElevationContext elevation) : IRoleAssignmentService
{
    private async Task<string> ActorLabelAsync(Guid userId, CancellationToken ct)
        => elevation.Current is { } g && g.UserId == userId ? $"{g.InitiatorLabel} (emergency access)"
            : await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.DisplayName).FirstAsync(ct);

    public Task<Guid> GrantAsync(GrantRequest r, CancellationToken ct = default) => CreateAsync(r, AssignmentSource.Manual, null, null, delegated: true, ct);

    public Task<Guid> GrantTrustedAsync(GrantRequest r, AssignmentSource source, string grantedByLabel, Guid? mappingId = null, CancellationToken ct = default)
        => CreateAsync(r, source, Guard.NotBlank(grantedByLabel, "Granted by", 200), mappingId, delegated: false, ct);

    private async Task<Guid> CreateAsync(GrantRequest r, AssignmentSource source, string? trustedLabel, Guid? mappingId, bool delegated, CancellationToken ct)
    {
        var tenantId = tenant.RequireTenantId();
        var now = clock.UtcNow;
        var scale = await scales.GetAsync(ct);
        var from = r.EffectiveFromUtc ?? now;

        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == r.UserId, ct) ?? throw new DomainException("user_not_found", "The user was not found.");
        if (user.Status == UserStatus.Disabled) throw new DomainException("user_disabled", "A role cannot be granted to a disabled account.");

        await ValidateScopeReferencesAsync(r.Scope, ct);

        Guid? grantor = null; string label;
        if (delegated)
        {
            grantor = currentUser.UserId ?? throw new ForbiddenException("Only a signed-in user can grant roles.");
            if (grantor == r.UserId) throw new DomainException("self_grant", "You cannot grant a role to yourself.");
            var subject = await access.GetSubjectAsync(ct) ?? throw new ForbiddenException("The caller is not a known user.");
            ClassificationScale effective = scale;
            Func<Guid, IReadOnlyCollection<Guid>>? ancestry = await AncestryFuncAsync(ct);
            var why = DelegationPolicy.WhyNot(AccessEvaluator.Default.Matrix, subject.Assignments, now, r.RoleCode, r.Scope, effective, ancestry);
            if (why is not null) throw new ForbiddenException(why);
            if (r.PrivilegedAccess && !DelegationPolicy.MayGrantPrivilegedAccess(subject.Assignments, now))
                throw new ForbiddenException("Only a Tenant Administrator can grant access to records at a privileged classification level.");
            label = await ActorLabelAsync(grantor.Value, ct);
        }
        else label = trustedLabel!;

        var assignment = RoleAssignment.Create(tenantId, r.UserId, r.RoleCode, r.ViewerKind, r.Scope, r.ApproverGroupKey, from, r.EffectiveToUtc,
            grantor, label, r.Reason, source, mappingId, scale, now, r.ExplicitHighCeiling, r.PrivilegedAccess);

        return await uow.ExecuteAsync(async c =>
        {
            db.RoleAssignments.Add(assignment);
            await db.SaveChangesAsync(c);
            audit.Record(new AuditEntry("role.granted", nameof(RoleAssignment), assignment.Id.ToString(),
                OrganisationId: assignment.OrganisationIds.Count == 1 ? assignment.OrganisationIds[0] : null, Reason: r.Reason,
                NewValuesJson: IdentityJson.Of(new
                {
                    user = r.UserId, role = r.RoleCode, viewerKind = r.ViewerKind?.ToString(), level = r.Scope.Level.ToString(), organisations = r.Scope.OrganisationIds,
                    businessUnits = r.Scope.BusinessUnitIds, divisions = r.Scope.DivisionIds, sites = r.Scope.SiteIds, categories = r.Scope.CategoryIds,
                    ceiling = r.Scope.ClassificationCeiling, privilegedAccess = r.PrivilegedAccess, source = source.ToString(), grantedBy = label
                })));
            return assignment.Id;
        }, ct);
    }

    public async Task RevokeAsync(Guid assignmentId, string reason, CancellationToken ct = default)
    {
        var why = Guard.NotBlank(reason, "Reason", 1000);
        var now = clock.UtcNow;
        var actorUser = currentUser.UserId ?? throw new ForbiddenException("Only a signed-in user can revoke roles.");
        await uow.ExecuteAsync(async c =>
        {
            var assignment = await db.RoleAssignments.FirstOrDefaultAsync(a => a.Id == assignmentId, c) ?? throw new ResourceNotFoundException("The assignment was not found.");
            var subject = await access.GetSubjectAsync(c) ?? throw new ForbiddenException("The caller is not a known user.");
            var scale = await scales.GetAsync(c);
            var denied = DelegationPolicy.WhyNot(AccessEvaluator.Default.Matrix, subject.Assignments, now, assignment.RoleCode, assignment.Scope, scale, await AncestryFuncAsync(c));
            if (denied is not null) throw new ForbiddenException(denied);
            await LastAdministrator.EnsureNotLastAsync(db, null, assignment.Id, now, c, "end this role assignment");
            assignment.End(now);
            await db.SaveChangesAsync(c);
            audit.Record(new AuditEntry("role.revoked", nameof(RoleAssignment), assignment.Id.ToString(), Reason: why,
                OrganisationId: assignment.OrganisationIds.Count == 1 ? assignment.OrganisationIds[0] : null,
                NewValuesJson: IdentityJson.Of(new { user = assignment.UserId, role = assignment.RoleCode })));
        }, ct);
    }

    public async Task RevokeTrustedAsync(Guid assignmentId, AssignmentSource expectedSource, string reason, CancellationToken ct = default)
    {
        var why = Guard.NotBlank(reason, "Reason", 1000);
        await uow.ExecuteAsync(async c =>
        {
            var assignment = await db.RoleAssignments.FirstOrDefaultAsync(a => a.Id == assignmentId && a.Source == expectedSource, c);
            if (assignment is null || !assignment.IsActiveAt(clock.UtcNow)) return;
            if (assignment.RoleCode is RoleCodes.SystemAdministrator or RoleCodes.TenantAdministrator)
                throw new DomainException("administrator_revoke_refused", "Administrator roles are never ended by configuration; use user administration.");
            assignment.End(clock.UtcNow);
            await db.SaveChangesAsync(c);
            audit.Record(new AuditEntry("role.revoked", nameof(RoleAssignment), assignment.Id.ToString(), Reason: why,
                OrganisationId: assignment.OrganisationIds.Count == 1 ? assignment.OrganisationIds[0] : null,
                NewValuesJson: IdentityJson.Of(new { user = assignment.UserId, role = assignment.RoleCode, source = expectedSource.ToString() })));
        }, ct);
    }

    /// <summary>Ends an assignment created by a group mapping because the user left the group. Trusted internal path.</summary>
    internal async Task EndMappedAsync(Guid assignmentId, CancellationToken ct)
    {
        await uow.ExecuteAsync(async c =>
        {
            var assignment = await db.RoleAssignments.FirstOrDefaultAsync(a => a.Id == assignmentId && a.Source == AssignmentSource.GroupMapping, c);
            if (assignment is null) return;
            assignment.End(clock.UtcNow);
            await db.SaveChangesAsync(c);
            audit.Record(new AuditEntry("role.revoked", nameof(RoleAssignment), assignment.Id.ToString(), Reason: "User left the mapped identity group",
                NewValuesJson: IdentityJson.Of(new { user = assignment.UserId, role = assignment.RoleCode, source = "GroupMapping" })));
        }, ct);
    }

    public async Task<IReadOnlyList<AssignmentDto>> ListForUserAsync(Guid userId, bool includeEnded = false, CancellationToken ct = default)
    {
        var now = clock.UtcNow;
        var rows = await db.RoleAssignments.AsNoTracking().Where(a => a.UserId == userId).OrderBy(a => a.CreatedUtc).ToListAsync(ct);
        return rows.Where(a => includeEnded || a.IsActiveAt(now)).Select(a => ToDto(a, now)).ToList();
    }

    public async Task<IReadOnlyList<AssignmentDto>> ListAsync(bool activeOnly = true, CancellationToken ct = default)
    {
        var now = clock.UtcNow;
        var rows = await db.RoleAssignments.AsNoTracking().OrderBy(a => a.CreatedUtc).ToListAsync(ct);
        return rows.Where(a => !activeOnly || a.IsActiveAt(now)).Select(a => ToDto(a, now)).ToList();
    }

    private static AssignmentDto ToDto(RoleAssignment a, DateTime now) => new(a.Id, a.UserId, a.RoleCode, a.ViewerKind, a.Scope, a.ApproverGroupKey,
        a.EffectiveFromUtc, a.EffectiveToUtc, a.GrantedByLabel, a.Reason, a.Source, a.IsActiveAt(now), a.PrivilegedAccess);

    private async Task<Func<Guid, IReadOnlyCollection<Guid>>?> AncestryFuncAsync(CancellationToken ct)
    {
        var hierarchy = services.GetService<ICategoryHierarchy>();
        if (hierarchy is null) return null;
        var map = await hierarchy.AncestryAsync(ct);
        return id => map.TryGetValue(id, out var path) ? path : [id];
    }

    /// <summary>Every organisation, business unit, division and site named by a scope must exist in this tenant (and in a named organisation).</summary>
    private async Task ValidateScopeReferencesAsync(ScopeSet scope, CancellationToken ct)
    {
        if (scope.Level != ScopeLevel.Organisation) return;
        var orgs = (await structure.ListOrganisationsAsync(ct)).Select(o => o.Id).ToHashSet();
        foreach (var org in scope.OrganisationIds)
            if (!orgs.Contains(org)) throw new DomainException("organisation_not_found", "A named [[organisation|lower]] was not found.");
        if (!scope.NarrowsBusinessUnit && !scope.NarrowsDivision && !scope.NarrowsSite) return;
        var bus = new HashSet<Guid>(); var divs = new HashSet<Guid>(); var sites = new HashSet<Guid>();
        foreach (var org in scope.OrganisationIds)
        {
            var s = await structure.GetAsync(org, ct);
            foreach (var x in s.BusinessUnits) bus.Add(x.Id);
            foreach (var x in s.Divisions) divs.Add(x.Id);
            foreach (var x in s.Sites) sites.Add(x.Id);
        }
        if (scope.BusinessUnitIds.Any(x => !bus.Contains(x))) throw new DomainException("business_unit_not_found", "A named [[business.unit|lower]] was not found in the named [[organisation|plural|lower]].");
        if (scope.DivisionIds.Any(x => !divs.Contains(x))) throw new DomainException("division_not_found", "A named [[division|lower]] was not found in the named [[organisation|plural|lower]].");
        if (scope.SiteIds.Any(x => !sites.Contains(x))) throw new DomainException("site_not_found", "A named [[site|lower]] was not found in the named [[organisation|plural|lower]].");
    }
}

internal sealed class GroupMappingService(IdentityDbContext db, IUnitOfWork uow, IAuditWriter audit, ITenantContext tenant, IClock clock, IClassificationScaleProvider scales) : IGroupMappingService
{
    public async Task<Guid> AddAsync(string providerKey, string groupKey, string roleCode, ViewerKind? viewerKind, ScopeSet scope, string? approverGroupKey, CancellationToken ct = default)
    {
        var mapping = GroupRoleMapping.Create(tenant.RequireTenantId(), providerKey, groupKey, roleCode, viewerKind, scope, approverGroupKey, await scales.GetAsync(ct), clock.UtcNow);
        return await uow.ExecuteAsync(async c =>
        {
            db.GroupMappings.Add(mapping);
            await db.SaveChangesAsync(c);
            audit.Record(new AuditEntry("rolemapping.created", nameof(GroupRoleMapping), mapping.Id.ToString(),
                NewValuesJson: IdentityJson.Of(new { providerKey, groupKey, roleCode })));
            return mapping.Id;
        }, ct);
    }

    public async Task<IReadOnlyList<GroupMappingDto>> ListAsync(CancellationToken ct = default)
        => (await db.GroupMappings.AsNoTracking().OrderBy(m => m.ProviderKey).ThenBy(m => m.GroupKey).ToListAsync(ct))
            .Select(m => new GroupMappingDto(m.Id, m.ProviderKey, m.GroupKey, m.RoleCode, m.ViewerKind, m.Scope, m.ApproverGroupKey, m.IsActive)).ToList();

    public async Task DeactivateAsync(Guid id, CancellationToken ct = default)
    {
        await uow.ExecuteAsync(async c =>
        {
            var m = await db.GroupMappings.FirstOrDefaultAsync(x => x.Id == id, c) ?? throw new DomainException("mapping_not_found", "The mapping was not found.");
            m.Deactivate();
            await db.SaveChangesAsync(c);
            audit.Record(new AuditEntry("rolemapping.deactivated", nameof(GroupRoleMapping), id.ToString()));
        }, ct);
    }
}

internal sealed class ProviderRegistry(IdentityDbContext db, IUnitOfWork uow, IAuditWriter audit, ITenantContext tenant, IClock clock, IdentityOptions options, ISecretProvider secrets,
    IEnumerable<IIdentityProviderListener> listeners) : IProviderRegistry
{
    public async Task<IReadOnlyList<IdentityProviderSettings>> ListAsync(CancellationToken ct = default)
        => (await db.Providers.AsNoTracking().OrderBy(p => p.Key).ToListAsync(ct)).Select(p => p.ToSettings()).ToList();

    public async Task<IReadOnlyList<ConfigurationIssue>> SaveAsync(IdentityProviderSettings settings, CancellationToken ct = default)
    {
        var issues = IdentityProviderValidator.Validate(settings, options.Mode, secrets);
        if (!IdentityProviderValidator.IsValid(issues)) return issues;
        var tenantId = tenant.RequireTenantId();
        await uow.ExecuteAsync(async c =>
        {
            var existing = await db.Providers.FirstOrDefaultAsync(p => p.Key == settings.Key, c);
            if (existing is null) db.Providers.Add(IdentityProviderRegistration.Create(tenantId, settings, clock.UtcNow));
            else existing.Apply(settings, clock.UtcNow);
            await db.SaveChangesAsync(c);
            // Never audit the secret reference's target, only that one is set.
            audit.Record(new AuditEntry(existing is null ? "identityprovider.created" : "identityprovider.updated", nameof(IdentityProviderRegistration), settings.Key,
                NewValuesJson: IdentityJson.Of(new { settings.Key, kind = settings.Kind.ToString(), settings.Enabled, secretReferenceSet = !string.IsNullOrWhiteSpace(settings.ClientSecretRef) })));
        }, ct);
        foreach (var listener in listeners) await listener.ChangedAsync(tenantId, settings, ct);
        return issues;
    }

    public async Task RecordTestSignInAsync(string key, CancellationToken ct = default)
    {
        await uow.ExecuteAsync(async c =>
        {
            var p = await db.Providers.FirstOrDefaultAsync(x => x.Key == key, c) ?? throw new DomainException("provider_not_found", "The identity provider was not found.");
            p.RecordTestSignIn(clock.UtcNow);
            await db.SaveChangesAsync(c);
            audit.Record(new AuditEntry("identityprovider.tested", nameof(IdentityProviderRegistration), key));
        }, ct);
    }

    public Task<DateTime?> TestSignInAsync(string key, CancellationToken ct = default)
        => db.Providers.AsNoTracking().Where(p => p.Key == key).Select(p => p.TestSignInUtc).FirstOrDefaultAsync(ct);
}

internal sealed class AccessService(
    IdentityDbContext db, IUnitOfWork uow, IAuditWriter audit, IClock clock, CurrentUser currentUser, IClassificationScaleProvider scales, AccessEvaluator evaluator,
    ElevationContext elevation) : IAccessService
{
    private AccessSubject? _subject;
    private bool _loaded;

    public Guid? CurrentUserId => currentUser.UserId;

    public static AssignmentView ToView(RoleAssignment a) => new(a.Id, a.RoleCode, a.ViewerKind, a.Scope, a.ApproverGroupKey, a.EffectiveFromUtc, a.EffectiveToUtc, a.PrivilegedAccess);

    public async Task<AccessSubject?> GetSubjectAsync(CancellationToken ct = default)
    {
        if (_loaded) return _subject;
        if (currentUser.UserId is not { } id) { _loaded = true; return null; }
        if (elevation.Current is { } grant)
        {
            // Support or emergency access: the person acts as themselves, with only the permissions the elevation names, in the one tenant it targets (never their own assignments there).
            _loaded = true;
            if (grant.ExpiresUtc <= clock.UtcNow || grant.UserId != id) return _subject = null;
            return _subject = ElevatedSubject(grant);
        }
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is not null)
        {
            var rows = await db.RoleAssignments.AsNoTracking().Where(a => a.UserId == id).ToListAsync(ct);
            _subject = new AccessSubject(user.Id, user.TenantId, user.Status, rows.Select(ToView).ToList());
        }
        _loaded = true;
        return _subject;
    }

    internal static AccessSubject ElevatedSubject(ElevationGrant grant)
    {
        var views = new List<AssignmentView>
        {
            new(DeterministicId(grant.ElevationId, 1), RoleCodes.Auditor, null, ScopeSet.Tenant(grant.Ceiling), null, DateTime.MinValue.AddYears(1), grant.ExpiresUtc)
        };
        if (grant.Kind == ElevationKind.Emergency)
            views.Add(new(DeterministicId(grant.ElevationId, 2), RoleCodes.TenantAdministrator, null, ScopeSet.Tenant(grant.Ceiling), null, DateTime.MinValue.AddYears(1), grant.ExpiresUtc));
        return new AccessSubject(grant.UserId, grant.TenantId, UserStatus.Active, views, grant.ElevationId, grant.Permissions);
    }

    private static Guid DeterministicId(Guid seed, byte n) { var b = seed.ToByteArray(); b[15] = n; return new Guid(b); }

    public Task<ClassificationScale> ClassificationScaleAsync(CancellationToken ct = default) => scales.GetAsync(ct);

    public async Task<AccessDecision> CheckAsync(string permission, ResourceDescriptor resource, CancellationToken ct = default)
    {
        var subject = await GetSubjectAsync(ct);
        var decision = subject is null
            ? AccessDecision.Forbidden(1, "Not authenticated.")
            : evaluator.Evaluate(subject, await scales.GetAsync(ct), clock.UtcNow, permission, resource);
        if (!decision.Allowed && subject is not null) await RecordDenialAsync(permission, resource, decision, ct);
        return decision;
    }

    public async Task<AccessDecision> CheckSilentAsync(string permission, ResourceDescriptor resource, CancellationToken ct = default)
    {
        var subject = await GetSubjectAsync(ct);
        return subject is null ? AccessDecision.Forbidden(1, "Not authenticated.") : evaluator.Evaluate(subject, await scales.GetAsync(ct), clock.UtcNow, permission, resource);
    }

    public async Task RequireAsync(string permission, ResourceDescriptor resource, CancellationToken ct = default)
    {
        var d = await CheckAsync(permission, resource, ct);
        if (d.Allowed) return;
        if (d.Deny == DenyKind.NotFound) throw new ResourceNotFoundException();
        throw new ForbiddenException(d.Reason);
    }

    public async Task<AccessDecision> SimulateAsync(AccessSubject subject, string permission, ResourceDescriptor resource, CancellationToken ct = default)
        => evaluator.Evaluate(subject, await scales.GetAsync(ct), clock.UtcNow, permission, resource);

    public async Task<IReadOnlyList<AccessGrant>> GrantsAsync(string permission, CancellationToken ct = default)
    {
        var subject = await GetSubjectAsync(ct);
        return subject is null ? [] : evaluator.GrantsFor(subject, await scales.GetAsync(ct), clock.UtcNow, permission);
    }

    public async Task<bool> HoldsAnywhereAsync(string permission, CancellationToken ct = default)
    {
        var subject = await GetSubjectAsync(ct);
        return subject is not null && evaluator.HoldsAnywhere(subject, clock.UtcNow, permission);
    }

    private async Task RecordDenialAsync(string permission, ResourceDescriptor r, AccessDecision d, CancellationToken ct)
    {
        try
        {
            await uow.ExecuteAsync(c =>
            {
                audit.Record(new AuditEntry("authorisation.denied", r.Type, r.Id ?? "-", AuditOutcome.Denied, r.OrganisationId,
                    Reason: $"{permission}: layer {d.Layer}: {d.Reason}"));
                return Task.CompletedTask;
            }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Auditing a denial must never turn a denial into an error page; the denial itself still stands.
        }
    }
}

internal sealed class AccessDirectory(IdentityDbContext db, IClock clock) : IAccessDirectory
{
    public async Task<AccessSubject?> LoadSubjectAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null || user.Status != UserStatus.Active) return null;
        var rows = await db.RoleAssignments.AsNoTracking().Where(a => a.UserId == userId).ToListAsync(ct);
        return new AccessSubject(user.Id, user.TenantId, user.Status, rows.Select(AccessService.ToView).ToList());
    }

    public Task<IReadOnlyList<ApproverInfo>> ApproversAsync(string approverGroupKey, CancellationToken ct = default) => ApproversAsync(approverGroupKey, clock.UtcNow, ct);

    public async Task<IReadOnlyList<ApproverInfo>> ApproversAsync(string approverGroupKey, DateTime asOfUtc, CancellationToken ct = default)
    {
        var now = asOfUtc;
        var rows = await (from a in db.RoleAssignments.AsNoTracking()
                          join u in db.Users.AsNoTracking() on new { a.TenantId, Id = a.UserId } equals new { u.TenantId, u.Id }
                          where a.ApproverGroupKey == approverGroupKey && (a.RoleCode == RoleCodes.Approver || a.RoleCode == RoleCodes.BackupApprover) && u.Status == UserStatus.Active
                          select new { a, u.DisplayName }).ToListAsync(ct);
        return rows.Where(r => r.a.IsActiveAt(now)).Select(r => new ApproverInfo(r.a.UserId, r.DisplayName, r.a.RoleCode, r.a.Id)).ToList();
    }

    public async Task<IReadOnlyList<Guid>> HoldersOfRoleAsync(string roleCode, Guid organisationId, CancellationToken ct = default)
    {
        var now = clock.UtcNow;
        var rows = await (from a in db.RoleAssignments.AsNoTracking()
                          join u in db.Users.AsNoTracking() on new { a.TenantId, Id = a.UserId } equals new { u.TenantId, u.Id }
                          where a.RoleCode == roleCode && u.Status == UserStatus.Active
                          select a).ToListAsync(ct);
        return rows.Where(a => a.IsActiveAt(now) && (a.ScopeLevel == ScopeLevel.Tenant || a.OrganisationIds.Contains(organisationId))).Select(a => a.UserId).Distinct().ToList();
    }

    public Task<string?> DisplayNameAsync(Guid userId, CancellationToken ct = default)
        => db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => (string?)u.DisplayName).FirstOrDefaultAsync(ct);
}

public static class IdentityModule
{
    public const string Name = "Identity";
    public const string Schema = "identity";
    public const int Tier = 3;

    public static IServiceCollection AddIdentityModule(this IServiceCollection services, Action<IdentityOptions> configure)
    {
        var options = new IdentityOptions();
        configure(options);
        services.AddSingleton(options);
        services.AddSingleton(AccessEvaluator.Default);
        services.TryAddSingleton<ISecretProvider>(new EnvironmentAndFileSecretProvider());
        services.AddModuleDbContext<IdentityDbContext>(Name, Tier, Schema);
        services.AddScoped<CurrentUser>();
        services.AddScoped<IUserService, UserService>();
        services.AddScoped<IRoleAssignmentService, RoleAssignmentService>();
        services.AddScoped<IGroupMappingService, GroupMappingService>();
        services.AddScoped<IProviderRegistry, ProviderRegistry>();
        services.AddScoped<ElevationContext>();
        services.AddScoped<IElevationService, ElevationService>();
        services.AddScoped<IAccessService, AccessService>();
        services.AddScoped<IAccessDirectory, AccessDirectory>();
        services.AddScoped<IAuditBrowser, AuditBrowser>();
        services.AddScoped<IPeopleDirectory, PeopleDirectory>();
        services.AddScoped<IUserAdministration, UserAdministration>();
        services.AddScoped<IClassificationScaleProvider, DefaultClassificationScaleProvider>();
        return services;
    }
}
