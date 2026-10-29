using System.Text.Json;
using Ecp.Access.Domain;
using Ecp.Access.Persistence;
using Ecp.Audit;
using Ecp.Configuration;
using Ecp.Contracts;
using Ecp.Identity;
using Ecp.Identity.Domain;
using Ecp.Persistence;
using Ecp.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Ecp.Access;

/// <summary>Thrown inside a preview's transaction to roll everything back after the result has been read.</summary>
internal sealed class RolledBackForPreview : Exception;

/// <summary>
/// Applies an access change as ordinary grants and ownerships in <b>one database transaction</b>, so a person never ends up half configured: the delegated services (which enforce the delegation policy,
/// scope containment, ceiling rules, self-grant refusal and last-administrator protection) are called in a fixed order inside the unit of work, and any refusal rolls all of it back. Previewing runs the
/// same code and rolls back at the end, so what the review screen shows is what saving would do.
/// </summary>
internal sealed class AccessProfileService(
    IAccessService access, IPeopleDirectory people, IRoleAssignmentService roles, ISecuredCategoryService categories, IUserService users, IUnitOfWork uow, IAuditWriter audit,
    ITenantContext tenant, IClock clock, ActorContext actor, ReauthenticationContext reauth, AccessDbContext db, IEffectiveAccess effective, ICapabilityResolver capabilities,
    ICategoryAdministration categoryAdmin, IOwnershipPolicyEngine ownershipPolicy) : IAccessProfileService
{
    private static readonly TimeSpan StepUpWindow = TimeSpan.FromMinutes(10);

    public async Task<IReadOnlyList<ProfileOption>> ProfilesForAsync(Guid organisationId, CancellationToken ct = default)
    {
        var scale = await access.ClassificationScaleAsync(ct);
        var result = new List<ProfileOption>();
        foreach (var p in AccessProfiles.All)
        {
            string? whyNot = null;
            if (p.RequiresCapability is { } cap && !await capabilities.IsEnabledAsync(organisationId, cap, ct)) whyNot = "This organisation does not use this step.";
            else if (!await MayGrantAsync(p, organisationId, ct)) whyNot = "You are not allowed to give this access.";
            result.Add(new ProfileOption(p.Key, p.Label, p.Summary, p.OwnsCategories, p.NeedsApproverGroup, p.ViewerFromScope, p.WholeOrganisationOnly, AccessProfiles.DefaultCeiling(p, scale), whyNot is null, whyNot));
        }
        return result;
    }

    /// <summary>Whether the signed-in person's own grants would let them give every role of the profile in the organisation (the delegation policy has the final say at save time).</summary>
    private async Task<bool> MayGrantAsync(AccessProfile profile, Guid organisationId, CancellationToken ct)
    {
        var subject = await access.GetSubjectAsync(ct);
        if (subject is null) return false;
        var now = clock.UtcNow;
        var scale = await access.ClassificationScaleAsync(ct);
        foreach (var role in profile.RoleCodes)
        {
            var grantable = subject.Assignments.Where(a => a.IsActiveAt(now)).Any(a =>
                Ecp.Identity.Access.DelegationPolicy.GrantableBy(a.RoleCode).Contains(role)
                && (a.Scope.Level == ScopeLevel.Tenant || (a.Scope.Level == ScopeLevel.Organisation && a.Scope.OrganisationIds.Contains(organisationId))));
            if (!grantable) return false;
        }
        _ = scale;
        return true;
    }

    // -- preview and apply ----------------------------------------------------------------------------------------------------------------------------------

    public async Task<AccessPlan> PreviewAsync(AccessChange change, CancellationToken ct = default)
    {
        await people.GetManageableUserAsync(change.UserId, ct);   // not found unless the caller may administer this person
        var before = await effective.ForUserAsync(change.UserId, ct);
        var steps = new List<PlannedStep>(); var problems = new List<string>(); EffectiveAccessReport? after = null;
        try
        {
            await uow.ExecuteAsync<bool>(async c =>
            {
                try
                {
                    var result = await ApplyCoreAsync(change, steps, c, validateOnly: true);
                    after = await effective.ForUserAsync(change.UserId, c);
                    _ = result;
                }
                catch (Exception ex) when (ex is DomainException or ForbiddenException or ResourceNotFoundException)
                {
                    problems.Add(Explain(ex));
                }
                throw new RolledBackForPreview();   // nothing the preview did is kept, whether it succeeded or not
            }, ct);
        }
        catch (RolledBackForPreview) { /* expected */ }
        return new AccessPlan(problems.Count == 0, steps, problems, problems.Count == 0 ? after : null, before);
    }

    public async Task<AccessResult> ApplyAsync(AccessChange change, CancellationToken ct = default)
    {
        await people.GetManageableUserAsync(change.UserId, ct);
        reauth.Require(clock.UtcNow, StepUpWindow);   // changing roles, scope, ceiling, ownership or approver membership needs a recent sign-in
        var steps = new List<PlannedStep>();
        return await uow.ExecuteAsync(c => ApplyCoreAsync(change, steps, c, validateOnly: false), ct);
    }

    private async Task<AccessResult> ApplyCoreAsync(AccessChange change, List<PlannedStep> steps, CancellationToken ct, bool validateOnly)
    {
        var tenantId = tenant.RequireTenantId();
        var scale = await access.ClassificationScaleAsync(ct);
        if (change.ProfileKeys.Count == 0) throw new DomainException("profile_required", "Choose what this person will do.");
        var profiles = change.ProfileKeys.Distinct().Select(k => AccessProfiles.Find(k) ?? throw new DomainException("profile_unknown", "That kind of access is not recognised.")).ToList();
        var person = await people.GetManageableUserAsync(change.UserId, ct);
        var me = access.CurrentUserId;
        var byLabel = me is { } id ? (await users.GetAsync(id, ct))?.DisplayName ?? "an administrator" : "setup";
        var existing = await people.AssignmentsOfAsync(change.UserId, includeEnded: false, ct);
        var notes = new List<string>(); var applications = new List<Guid>(); var grantsCreated = 0; var ownershipsCreated = 0; var ownershipPending = false;

        foreach (var profile in profiles)
        {
            if (profile.NeedsApproverGroup && string.IsNullOrWhiteSpace(change.ApproverGroupKey)) throw new DomainException("approver_group_required", $"Choose the approver group for {profile.Label}.");
            if (change.Scope != ScopeChoice.WholeOrganisation && !profile.WholeOrganisationOnly && change.ScopeIdList.Count == 0)
                throw new DomainException("scope_required", "Choose at least one place this person may work in, or choose the whole organisation.");
            var ceiling = scale.Canonical(change.Ceiling) ?? (string.IsNullOrWhiteSpace(change.Ceiling) ? AccessProfiles.DefaultCeiling(profile, scale) : throw new DomainException("invalid_classification", "That classification level is not one of this organisation's levels."));
            var explicitHigh = scale.Rank(ceiling) >= scale.HighCeilingThreshold;
            var grantIds = new List<Guid>(); var ownershipIds = new List<Guid>();

            foreach (var role in profile.RoleCodes)
            {
                var request = BuildGrant(change, profile, role, ceiling, explicitHigh);
                var held = existing.FirstOrDefault(a => a.IsActive && a.RoleCode == role && SameScope(a, request));
                if (held is not null) { steps.Add(new PlannedStep("keep", $"{RoleName(role)} is already held with the same scope and classification.")); continue; }
                grantIds.Add(await roles.GrantAsync(request, ct));
                grantsCreated++;
                steps.Add(new PlannedStep("grant", $"Give {RoleName(role)} ({Where(request.Scope)}, up to {ceiling}) as part of the {profile.Label} profile."));
            }

            Guid? policyId = null;
            if (profile.OwnsCategories && change.Ownership == OwnershipChoice.All)
            {
                // "All current and future": a standing policy, not a copy of today's list. Today's categories are covered now; every category created later is covered in the same transaction as the category.
                var outcome = await ownershipPolicy.SetAsync(new PolicyRequest(change.OrganisationId, change.UserId, Primary: true, "Guided", null, byLabel, me, change.Reason ?? $"Access profile: {profile.Label}"), trusted: false, ct);
                policyId = outcome.PolicyId; ownershipsCreated += outcome.Assigned;
                steps.Add(new PlannedStep("ownership-policy", $"Make {person.DisplayName} the owner of every current and future [[category|lower]] in this [[organisation|lower]]. Any [[category|lower]] added later is recognised automatically."));
                steps.AddRange(outcome.Steps.Select(t => new PlannedStep("ownership", t.Replace("Make them", $"Make {person.DisplayName}"))));
                if (outcome.AlreadyOwned > 0) steps.Add(new PlannedStep("keep", $"{person.DisplayName} already owns {outcome.AlreadyOwned} [[category|plural|lower]]; that ownership is left as it is."));
            }
            else if (profile.OwnsCategories && change.Ownership == OwnershipChoice.Selected)
            {
                // The list is read trusted so the choice is not limited by what the administrator can see; every ownership is still given through the secured service, which checks the caller's own authority.
                var all = (await categoryAdmin.ListAllAsync(ct)).Where(c => c.IsActive && c.OrganisationId == change.OrganisationId).ToList();
                var targets = all.Where(c => change.OwnedCategories.Contains(c.Id)).ToList();
                if (targets.Count == 0) throw new DomainException("ownership_required", "Choose at least one category to own, or choose none.");
                foreach (var cat in targets)
                {
                    if (cat.Owners.Any(o => o.UserId == change.UserId)) { steps.Add(new PlannedStep("keep", $"{person.DisplayName} already owns {cat.Name}.")); continue; }
                    var primary = !cat.Owners.Any(o => o.IsPrimary);
                    ownershipIds.Add(await categories.AssignOwnerAsync(cat.Id, change.UserId, primary, change.Reason ?? $"Access profile: {profile.Label}", ct));
                    ownershipsCreated++;
                    steps.Add(new PlannedStep("ownership", $"Make {person.DisplayName} the {(primary ? "primary" : "backup")} owner of {cat.Name}."));
                }
            }

            if (grantIds.Count > 0 || ownershipIds.Count > 0 || policyId is not null)
            {
                var summary = $"{profile.Label}: {string.Join(", ", profile.RoleCodes.Select(RoleName))}{(policyId is not null ? "; owns every current and future category" : ownershipIds.Count > 0 ? $"; owns {ownershipIds.Count} categor{(ownershipIds.Count == 1 ? "y" : "ies")}" : "")}";
                var row = ProfileApplication.Record(tenantId, change.OrganisationId, change.UserId, profile.Key, AccessProfile.Version, "Guided", byLabel, me, clock.UtcNow, grantIds, ownershipIds,
                    summary.Length > 300 ? summary[..300] : summary, change.Reason, actor.CorrelationId);
                db.ProfileApplications.Add(row);
                await db.SaveChangesAsync(ct);
                applications.Add(row.Id);
                if (policyId is { } pid) await ownershipPolicy.LinkAsync(pid, row.Id, ct);
                audit.Record(new AuditEntry("access.profile.applied", "User", change.UserId.ToString(), OrganisationId: change.OrganisationId, Reason: change.Reason,
                    NewValuesJson: JsonSerializer.Serialize(new { profile = profile.Key, version = AccessProfile.Version, grants = grantIds, ownerships = ownershipIds, ceiling })));
            }
        }
        _ = validateOnly;
        return new AccessResult(applications, grantsCreated, ownershipsCreated, ownershipPending, notes);
    }

    private static string RoleName(string role) => RoleLabels.Label(role);

    private static string Where(ScopeSet scope) => scope.Level == ScopeLevel.Organisation ? $"{scope.OrganisationIds.Count} organisation(s)" + (scope.NarrowsBusinessUnit || scope.NarrowsDivision || scope.NarrowsSite || scope.NarrowsCategory ? ", narrowed" : "") : scope.Level.ToString();

    private static bool SameScope(AssignmentDto a, GrantRequest r)
        => a.ViewerKind == r.ViewerKind && a.ApproverGroupKey == r.ApproverGroupKey && a.Scope.ClassificationCeiling == r.Scope.ClassificationCeiling
           && a.Scope.OrganisationIds.OrderBy(x => x).SequenceEqual(r.Scope.OrganisationIds.OrderBy(x => x))
           && a.Scope.BusinessUnitIds.OrderBy(x => x).SequenceEqual(r.Scope.BusinessUnitIds.OrderBy(x => x))
           && a.Scope.DivisionIds.OrderBy(x => x).SequenceEqual(r.Scope.DivisionIds.OrderBy(x => x))
           && a.Scope.SiteIds.OrderBy(x => x).SequenceEqual(r.Scope.SiteIds.OrderBy(x => x))
           && (a.Scope.CategoryIds is null ? r.Scope.CategoryIds is null : r.Scope.CategoryIds is not null && a.Scope.CategoryIds.OrderBy(x => x).SequenceEqual(r.Scope.CategoryIds.OrderBy(x => x)));

    /// <summary>Translates the plain choices into one ordinary grant request. Nothing here is a decision about access; the grant service decides.</summary>
    internal static GrantRequest BuildGrant(AccessChange change, AccessProfile profile, string role, string ceiling, bool explicitHigh)
    {
        var narrow = !profile.WholeOrganisationOnly && change.Scope != ScopeChoice.WholeOrganisation;
        IEnumerable<Guid>? bus = null, divs = null, sites = null, cats = null;
        ViewerKind? kind = null;
        if (narrow)
        {
            switch (change.Scope)
            {
                case ScopeChoice.Divisions: divs = change.ScopeIdList; if (profile.ViewerFromScope) kind = ViewerKind.BusinessUnit; break;
                case ScopeChoice.BusinessUnits: bus = change.ScopeIdList; if (profile.ViewerFromScope) kind = ViewerKind.BusinessUnit; break;
                case ScopeChoice.Sites: sites = change.ScopeIdList; if (profile.ViewerFromScope) kind = ViewerKind.Site; break;
                case ScopeChoice.Categories: cats = change.ScopeIdList; if (profile.ViewerFromScope) kind = ViewerKind.Category; break;
            }
        }
        if (profile.ViewerFromScope && kind is null) kind = ViewerKind.AllContracts;
        var scope = ScopeSet.ForOrganisations([change.OrganisationId], ceiling, bus, divs, sites, cats);
        var approver = role is RoleCodes.Approver or RoleCodes.BackupApprover ? change.ApproverGroupKey : null;
        return new GrantRequest(change.UserId, role, role == RoleCodes.Viewer ? kind : null, scope, approver, Reason: change.Reason ?? $"Access profile: {profile.Label}",
            ExplicitHighCeiling: explicitHigh, PrivilegedAccess: change.PrivilegedAccess && role == RoleCodes.Legal);
    }

    private static string Explain(Exception ex) => ex switch
    {
        DomainException d when d.Code == "self_grant" => "You cannot give yourself access. Ask another administrator.",
        DomainException d when d.Code == "last_administrator" => "That would remove the only administrator. Add another administrator first.",
        DomainException d => d.RawMessage,
        ForbiddenException => "You are not allowed to give this access. Ask an administrator who can.",   // never echo the engine's message: it names role and permission codes
        ResourceNotFoundException => "That person or place could not be found.",
        _ => ex.Message
    };

    // -- provenance and undo --------------------------------------------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<ProfileApplicationInfo>> ApplicationsOfAsync(Guid userId, bool includeEnded, CancellationToken ct = default)
    {
        await people.GetManageableUserAsync(userId, ct);
        var rows = await db.ProfileApplications.AsNoTracking().Where(a => a.UserId == userId && (includeEnded || a.EndedUtc == null)).OrderBy(a => a.AppliedUtc).ToListAsync(ct);
        return rows.Select(a => new ProfileApplicationInfo(a.Id, a.OrganisationId, a.ProfileKey, AccessProfiles.Find(a.ProfileKey)?.Label ?? a.ProfileKey, a.AppliedUtc, a.AppliedByLabel, a.Summary, a.IsOpen, a.Origin)).ToList();
    }

    public async Task EndApplicationAsync(Guid applicationId, string reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException("reason_required", "Say why access is being ended.");
        reauth.Require(clock.UtcNow, StepUpWindow);
        var row = await db.ProfileApplications.FirstOrDefaultAsync(a => a.Id == applicationId, ct) ?? throw new ResourceNotFoundException();
        await people.GetManageableUserAsync(row.UserId, ct);
        if (!row.IsOpen) return;
        await uow.ExecuteAsync(async c =>
        {
            foreach (var g in row.GrantIds) { try { await roles.RevokeAsync(g, reason, c); } catch (DomainException ex) when (ex.Code is "already_ended" or "not_active") { /* already ended by hand */ } }
            foreach (var o in row.OwnershipIds) { try { await categories.EndOwnershipAsync(o, reason, c); } catch (DomainException) { /* already ended by hand */ } }
            var by = access.CurrentUserId is { } me ? (await users.GetAsync(me, c))?.DisplayName ?? "an administrator" : "an administrator";
            await ownershipPolicy.EndForApplicationAsync(row.Id, reason, by, c);   // the standing policy goes, and only the ownership it created
            row.End(by, clock.UtcNow);
            await db.SaveChangesAsync(c);
            audit.Record(new AuditEntry("access.profile.ended", "User", row.UserId.ToString(), OrganisationId: row.OrganisationId, Reason: reason,
                NewValuesJson: JsonSerializer.Serialize(new { profile = row.ProfileKey, grants = row.GrantIds, ownerships = row.OwnershipIds })));
        }, ct);
    }
}

/// <summary>Role names for plain sentences in plans (the screens use terminology; these are service-side fallbacks).</summary>
internal static class RoleLabels
{
    public static string Label(string role) => role switch
    {
            RoleCodes.CategoryOwner => "[[contract|lower]] ownership role",
            RoleCodes.ProcurementAdministrator => "[[contract|lower]] administration role",
            RoleCodes.Legal => "review role",
            RoleCodes.Approver => "approval role",
            RoleCodes.Viewer => "read-only role",
            RoleCodes.OrganisationAdministrator => "[[organisation|lower]] administration role",
            RoleCodes.BusinessConfigurationAdministrator => "business configuration role",
            RoleCodes.Auditor => "audit role",
            _ => "role"
        };
}
