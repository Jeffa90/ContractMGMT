using System.Text.Json;
using Ecp.Access.Domain;
using Ecp.Access.Persistence;
using Ecp.Audit;
using Ecp.Configuration;
using Ecp.Contracts;
using Ecp.Identity;
using Ecp.Identity.Access;
using Ecp.Identity.Domain;
using Ecp.Persistence;
using Ecp.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Ecp.Access;

/// <summary>What the first administrator will do in the organisation, beyond administering it (docs/UX_REDESIGN.md section 7).</summary>
public sealed record BootstrapChoices(bool ManageContracts, bool OwnAllCategories, bool Review, bool Approve, bool ReadOnly, string? ApproverGroupKey = null)
{
    public static readonly BootstrapChoices None = new(false, false, false, false, false);
    public bool Any => ManageContracts || Review || Approve || ReadOnly;
}

public sealed record BootstrapResult(int GrantsCreated, int OwnershipsCreated, IReadOnlyList<string> Notes);

/// <summary>What "Give me full Test-mode access" will grant, said before it is applied. Nothing in it is a secret or a code.</summary>
public sealed record TestAccessPlan(bool Available, string? WhyNot, IReadOnlyList<string> WillGrant, IReadOnlyList<string> NotGranted, bool AlreadyHeld, EffectiveAccessReport? After);

/// <summary>
/// The first administrator's route to operational use. At set-up the administrator names what they will do and the platform issues exactly those profiles through the trusted path set-up already uses.
/// After set-up, in Demo and Test only, "Give me full Test-mode access" lets a person give themselves a broad but bounded set of profiles: it is the one narrow exception to the self-grant rule, it never
/// grants System or Tenant Administrator, it is audited, and it can be removed again. Production has neither shortcut.
/// </summary>
public interface IAdministratorBootstrap
{
    /// <summary>False in Production, where the self-service shortcut does not exist.</summary>
    bool TestModeAvailable { get; }

    /// <summary>Trusted, for set-up (no signed-in person): grants the chosen profiles to the named administrator in the organisation.</summary>
    Task<BootstrapResult> ApplyAtSetupAsync(Guid userId, Guid organisationId, BootstrapChoices choices, string byLabel, CancellationToken ct = default);

    Task<TestAccessPlan> PlanTestAccessAsync(Guid organisationId, CancellationToken ct = default);

    /// <summary>Demo and Test only: the signed-in administrator gives themselves full Test-mode access in the organisation. Needs a recent sign-in.</summary>
    Task<BootstrapResult> GiveFullTestAccessAsync(Guid organisationId, string reason, CancellationToken ct = default);

    /// <summary>Ends exactly the access "Give me full Test-mode access" created for the signed-in person (and nothing else).</summary>
    Task<int> EndTestAccessAsync(string reason, CancellationToken ct = default);
}

internal sealed class AdministratorBootstrap(
    IRoleAssignmentService roles, ICategoryAdministration categoryAdmin, IUserService users, IAccessService access, IAccessDirectory directory, IApproverGroupService groups,
    ICapabilityResolver capabilities, IEffectiveAccess effective, IUnitOfWork uow, IAuditWriter audit, ITenantContext tenant, IClock clock, ActorContext actor, ReauthenticationContext reauth,
    IdentityOptions identity, AccessDbContext db) : IAdministratorBootstrap
{
    public const string TestApproverGroup = "TEST-APPROVERS";
    public const string SetupApproverGroup = "INITIAL-APPROVERS";
    private static readonly TimeSpan StepUpWindow = TimeSpan.FromMinutes(10);

    public bool TestModeAvailable => identity.Mode != EcpMode.Production;

    // -- set-up ---------------------------------------------------------------------------------------------------------------------------------------------

    public async Task<BootstrapResult> ApplyAtSetupAsync(Guid userId, Guid organisationId, BootstrapChoices choices, string byLabel, CancellationToken ct = default)
    {
        if (!choices.Any) return new BootstrapResult(0, 0, ["The administrator chose to administer only. Someone else must be given the profile that works on records."]);
        var keys = new List<string>();
        if (choices.ManageContracts) keys.Add(AccessProfiles.ContractManager);
        if (choices.Review) keys.Add(AccessProfiles.Reviewer);
        if (choices.Approve) keys.Add(AccessProfiles.ApprovalAuthority);
        if (choices.ReadOnly) keys.Add(AccessProfiles.ReadOnly);
        var group = choices.Approve ? choices.ApproverGroupKey ?? SetupApproverGroup : null;
        return await uow.ExecuteAsync(c => ApplyTrustedAsync(userId, organisationId, keys, choices.OwnAllCategories ? OwnershipChoice.All : OwnershipChoice.None, null, group, AssignmentSource.Setup, "Setup", byLabel, "Chosen during set-up", c), ct);
    }

    // -- Test-mode access -----------------------------------------------------------------------------------------------------------------------------------

    private async Task<(List<string> Keys, List<string> Skipped)> TestProfilesAsync(Guid organisationId, CancellationToken ct)
    {
        var keys = new List<string> { AccessProfiles.ContractAdministrator };
        var skipped = new List<string>();
        if (await capabilities.IsEnabledAsync(organisationId, CapabilityKeys.LegalReview, ct)) keys.Add(AccessProfiles.Reviewer); else skipped.Add("Reviewer: the organisation does not use this step.");
        if (await capabilities.IsEnabledAsync(organisationId, CapabilityKeys.RegisterAuthorisation, ct)) keys.Add(AccessProfiles.ApprovalAuthority); else skipped.Add("Approval Authority: the organisation does not use this step.");
        return (keys, skipped);
    }

    private async Task<string?> WhyNotAsync(Guid organisationId, CancellationToken ct)
    {
        if (!TestModeAvailable) return "Test-mode access is available only in Demo and Test.";
        if (access.CurrentUserId is not { } me) return "Sign in first.";
        var subject = await directory.LoadSubjectAsync(me, ct);
        if (subject is null) return "Your account is not active.";
        var evaluator = AccessEvaluator.Default;
        var scale = await access.ClassificationScaleAsync(ct);
        var manages = evaluator.GrantsFor(subject, scale, clock.UtcNow, "users.manage").Any(g => g.OrganisationIds is null || g.OrganisationIds.Contains(organisationId));
        if (!manages) return "Only an administrator of this organisation can give themselves Test-mode access.";
        return null;
    }

    public async Task<TestAccessPlan> PlanTestAccessAsync(Guid organisationId, CancellationToken ct = default)
    {
        var why = await WhyNotAsync(organisationId, ct);
        if (why is not null) return new TestAccessPlan(false, why, [], [], false, null);
        var me = access.CurrentUserId!.Value;
        var (keys, skipped) = await TestProfilesAsync(organisationId, ct);
        var held = await db.ProfileApplications.AsNoTracking().AnyAsync(a => a.UserId == me && a.OrganisationId == organisationId && a.Origin == "TestBootstrap" && a.EndedUtc == null, ct);
        var will = keys.Select(k => AccessProfiles.Find(k)!).Select(p => $"{p.Label}: {p.Summary}").ToList();
        if (keys.Contains(AccessProfiles.ContractAdministrator)) will.Add("Ownership of every [[category|lower]] that exists now, as primary or backup owner.");
        var notGranted = new List<string> { "System Administrator or Tenant Administrator (never granted by this).", "The ability to approve or publish your own work: separation of duties still applies. Add a second test person to rehearse approvals." };
        notGranted.AddRange(skipped);
        EffectiveAccessReport? after = null;
        try
        {
            await uow.ExecuteAsync<bool>(async c =>
            {
                await ApplyTrustedAsync(me, organisationId, keys, OwnershipChoice.All, "Confidential", keys.Contains(AccessProfiles.ApprovalAuthority) ? TestApproverGroup : null, AssignmentSource.TestBootstrap, "TestBootstrap", "test-mode preview", "Preview", c);
                after = await effective.ForMeAsync(c);
                throw new RolledBackForPreview();
            }, ct);
        }
        catch (RolledBackForPreview) { }
        return new TestAccessPlan(true, null, will, notGranted, held, after);
    }

    public async Task<BootstrapResult> GiveFullTestAccessAsync(Guid organisationId, string reason, CancellationToken ct = default)
    {
        if (!TestModeAvailable) throw new DomainException("test_only", "Test-mode access is available only in Demo and Test. Ask another administrator to give you access.");
        var why = Guard.NotBlank(reason, "Reason", 500);
        reauth.Require(clock.UtcNow, StepUpWindow);
        var refusal = await WhyNotAsync(organisationId, ct);
        if (refusal is not null) throw new ForbiddenException(refusal);
        var me = access.CurrentUserId!.Value;
        var (keys, _) = await TestProfilesAsync(organisationId, ct);
        var byLabel = (await users.GetAsync(me, ct))?.DisplayName ?? "an administrator";
        if (keys.Contains(AccessProfiles.ApprovalAuthority))
        {
            try { await groups.CreateAsync(TestApproverGroup, "Test approvers (created by Test-mode access)", organisationId, allowDelegation: true, ct); }
            catch (DomainException) { /* it already exists */ }
        }
        return await uow.ExecuteAsync(async c =>
        {
            var result = await ApplyTrustedAsync(me, organisationId, keys, OwnershipChoice.All, "Confidential", keys.Contains(AccessProfiles.ApprovalAuthority) ? TestApproverGroup : null, AssignmentSource.TestBootstrap, "TestBootstrap", byLabel, why, c);
            audit.Record(new AuditEntry("access.test_bootstrap", "User", me.ToString(), OrganisationId: organisationId, Reason: why,
                NewValuesJson: JsonSerializer.Serialize(new { profiles = keys, mode = identity.Mode.ToString(), grants = result.GrantsCreated, ownerships = result.OwnershipsCreated })));
            return result;
        }, ct);
    }

    public async Task<int> EndTestAccessAsync(string reason, CancellationToken ct = default)
    {
        if (!TestModeAvailable) throw new DomainException("test_only", "Test-mode access exists only in Demo and Test.");
        var why = Guard.NotBlank(reason, "Reason", 500);
        reauth.Require(clock.UtcNow, StepUpWindow);
        var me = access.CurrentUserId ?? throw new ForbiddenException("Sign in first.");
        var rows = await db.ProfileApplications.Where(a => a.UserId == me && a.Origin == "TestBootstrap" && a.EndedUtc == null).ToListAsync(ct);
        var ended = 0;
        await uow.ExecuteAsync(async c =>
        {
            foreach (var row in rows)
            {
                foreach (var g in row.GrantIds) { await roles.RevokeTrustedAsync(g, AssignmentSource.TestBootstrap, why, c); ended++; }
                foreach (var o in row.OwnershipIds) { try { await categoryAdmin.EndOwnerTrustedAsync(o, why, c); } catch (DomainException) { /* already ended */ } }
                row.End("the person themselves", clock.UtcNow);
            }
            await db.SaveChangesAsync(c);
            if (rows.Count > 0) audit.Record(new AuditEntry("access.test_bootstrap.ended", "User", me.ToString(), Reason: why, NewValuesJson: JsonSerializer.Serialize(new { applications = rows.Count, grantsEnded = ended })));
        }, ct);
        return ended;
    }

    // -- the trusted application ----------------------------------------------------------------------------------------------------------------------------

    /// <summary>Issues the profiles' roles and ownerships through the trusted grant path (no signed-in person is needed, and the self-grant rule does not apply), recording provenance.</summary>
    private async Task<BootstrapResult> ApplyTrustedAsync(Guid userId, Guid organisationId, IReadOnlyList<string> profileKeys, OwnershipChoice ownership, string? ceilingOverride, string? approverGroup,
        AssignmentSource source, string origin, string byLabel, string reason, CancellationToken ct)
    {
        var tenantId = tenant.RequireTenantId();
        var scale = await access.ClassificationScaleAsync(ct);
        var existing = await roles.ListForUserAsync(userId, includeEnded: false, ct);
        var notes = new List<string>(); var grants = 0; var ownerships = 0;
        var categories = (await categoryAdmin.ListAllAsync(ct)).Where(c => c.IsActive && c.OrganisationId == organisationId).ToList();
        var topLevel = categories.Where(c => c.ParentCategoryId is null || categories.All(p => p.Id != c.ParentCategoryId)).ToList();
        var me = access.CurrentUserId;
        foreach (var key in profileKeys)
        {
            var profile = AccessProfiles.Find(key) ?? throw new DomainException("profile_unknown", "That kind of access is not recognised.");
            var ceiling = scale.Canonical(ceilingOverride) ?? AccessProfiles.DefaultCeiling(profile, scale);
            var change = new AccessChange(userId, organisationId, [key], ScopeChoice.WholeOrganisation, null, ceiling, false, ownership, null, profile.NeedsApproverGroup ? approverGroup : null, reason);
            var grantIds = new List<Guid>(); var ownershipIds = new List<Guid>();
            foreach (var role in profile.RoleCodes)
            {
                var request = AccessProfileService.BuildGrant(change, profile, role, ceiling, scale.Rank(ceiling) >= scale.HighCeilingThreshold);
                if (existing.Any(a => a.IsActive && a.RoleCode == role && a.ViewerKind == request.ViewerKind && a.ApproverGroupKey == request.ApproverGroupKey && a.Scope.OrganisationIds.SequenceEqual(request.Scope.OrganisationIds)
                    && a.Scope.ClassificationCeiling == request.Scope.ClassificationCeiling && !a.Scope.NarrowsBusinessUnit && !a.Scope.NarrowsDivision && !a.Scope.NarrowsSite && !a.Scope.NarrowsCategory)) continue;
                grantIds.Add(await roles.GrantTrustedAsync(request, source, byLabel, null, ct));
                grants++;
            }
            if (profile.OwnsCategories && ownership == OwnershipChoice.All)
            {
                if (topLevel.Count == 0) notes.Add($"There are no categories yet, so {profile.Label} owns nothing until you give it ownership. The set-up checklist lists it as work to do.");
                foreach (var cat in topLevel.Where(c => c.Owners.All(o => o.UserId != userId || o.EffectiveToUtc is not null)))
                {
                    var primary = !cat.Owners.Any(o => o.IsPrimary && (o.EffectiveToUtc is null || o.EffectiveToUtc > clock.UtcNow));
                    ownershipIds.Add(await categoryAdmin.AssignOwnerTrustedAsync(cat.Id, userId, primary, clock.UtcNow, null, byLabel, reason, ct));
                    ownerships++;
                }
            }
            if (grantIds.Count > 0 || ownershipIds.Count > 0 || (profile.OwnsCategories && ownership == OwnershipChoice.All && topLevel.Count == 0))
            {
                var summary = $"{profile.Label}: {string.Join(", ", profile.RoleCodes.Select(RoleLabels.Label))}" + (ownershipIds.Count > 0 ? $"; owns {ownershipIds.Count} categor{(ownershipIds.Count == 1 ? "y" : "ies")}" : profile.OwnsCategories && topLevel.Count == 0 ? "; ownership waits for categories" : "");
                db.ProfileApplications.Add(ProfileApplication.Record(tenantId, organisationId, userId, key, AccessProfile.Version, origin, byLabel, me, clock.UtcNow, grantIds, ownershipIds,
                    summary.Length > 300 ? summary[..300] : summary, reason, actor.CorrelationId));
                await db.SaveChangesAsync(ct);
            }
        }
        audit.Record(new AuditEntry(origin == "Setup" ? "access.bootstrap.setup" : "access.bootstrap.applied", "User", userId.ToString(), OrganisationId: organisationId, Reason: reason,
            NewValuesJson: JsonSerializer.Serialize(new { profiles = profileKeys, source = source.ToString(), grants, ownerships })));
        return new BootstrapResult(grants, ownerships, notes);
    }
}
