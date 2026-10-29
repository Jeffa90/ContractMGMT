using Ecp.Identity.Domain;

namespace Ecp.Access;

/// <summary>One ability in a person's effective-access report: whether they can do it, where, and why.</summary>
/// <param name="Where">Plain words on where it applies ("in Northwind Traders"; "in the categories they own: Energy, Packaging").</param>
/// <param name="Because">What gives it: the role held and how it came about (a profile, a direct grant, an identity group, set-up).</param>
/// <param name="Conditions">What must also be true ("only while a request for their approver group is waiting"), or why it is not available yet.</param>
/// <param name="GrantedBy">Access profiles that would give it, when the person cannot do it yet.</param>
public sealed record AbilityStatus(string Key, string Group, string Label, bool Can, string Where, IReadOnlyList<string> Because, string? Conditions, IReadOnlyList<string> GrantedBy);

public sealed record AttentionItem(string Code, string Message);

public sealed record OwnedCategory(Guid CategoryId, string Name, Guid OrganisationId, bool IsPrimary);

public sealed record GrantView(Guid AssignmentId, string RoleLabel, string RoleCode, string Where, string HighestClassification, string Source, string? ProfileLabel, DateTime EffectiveFromUtc, DateTime? EffectiveToUtc, bool Active, bool Future, bool Ended, string? ApproverGroupKey);

/// <summary>"Owns every current and future category in this organisation": the standing instruction behind an all-category ownership choice.</summary>
public sealed record StandingOwnership(Guid OrganisationId, string OrganisationName, bool Primary, string GivenBy, DateTime GivenUtc, string? ProfileLabel);

public sealed record ProfileBadge(string Key, string Label, Guid ApplicationId, DateTime AppliedUtc, string AppliedBy, Guid OrganisationId);

/// <summary>
/// What a person can actually do, computed on read from their real role grants, scopes, classification ceilings and the categories they own by the access evaluator's own rules. It is never derived from
/// profile records; those only explain where a grant came from.
/// </summary>
public sealed record EffectiveAccessReport(
    Guid UserId, string DisplayName, string Email, UserStatusView Status, IReadOnlyList<ProfileBadge> Profiles, string HighestClassification,
    IReadOnlyList<AbilityStatus> Abilities, IReadOnlyList<OwnedCategory> Owns, IReadOnlyList<GrantView> Grants, IReadOnlyList<AttentionItem> Attention, IReadOnlyList<string> ApproverGroups, IReadOnlyList<StandingOwnership>? StandingOwnerships = null)
{
    public IEnumerable<AbilityStatus> CanDo => Abilities.Where(a => a.Can);
    public IEnumerable<AbilityStatus> CannotDo => Abilities.Where(a => !a.Can);
    public bool HasAnyAccess => Abilities.Any(a => a.Can);
    public IReadOnlyList<StandingOwnership> Standing => StandingOwnerships ?? [];
}

public enum UserStatusView { Invited, Active, Disabled }

/// <summary>Computes effective access for a person. Authorised on the server: other people's reports need <c>users.manage</c> over the person; anyone may read their own.</summary>
public interface IEffectiveAccess
{
    Task<EffectiveAccessReport> ForUserAsync(Guid userId, CancellationToken ct = default);
    Task<EffectiveAccessReport> ForMeAsync(CancellationToken ct = default);
}

/// <summary>An access profile as offered to the person using the wizard.</summary>
public sealed record ProfileOption(string Key, string Label, string Summary, bool NeedsOwnership, bool NeedsApproverGroup, bool ViewerFromScope, bool WholeOrganisationOnly, string DefaultCeiling, bool Offered, string? WhyNot);

public sealed record PlannedStep(string Kind, string Text);

/// <summary>What saving would do, computed by really doing it inside a transaction that is then rolled back, so every rule of the real services applies.</summary>
public sealed record AccessPlan(bool CanApply, IReadOnlyList<PlannedStep> Steps, IReadOnlyList<string> Problems, EffectiveAccessReport? After, EffectiveAccessReport? Before);

public sealed record AccessResult(IReadOnlyList<Guid> ApplicationIds, int GrantsCreated, int OwnershipsCreated, bool OwnershipPending, IReadOnlyList<string> Notes);

public sealed record ProfileApplicationInfo(Guid Id, Guid OrganisationId, string ProfileKey, string ProfileLabel, DateTime AppliedUtc, string AppliedBy, string Summary, bool Open, string Origin);

/// <summary>Turns the wizard's plain-English choices into the platform's ordinary grants and ownerships, atomically, through the services that enforce the delegation rules.</summary>
public interface IAccessProfileService
{
    Task<IReadOnlyList<ProfileOption>> ProfilesForAsync(Guid organisationId, CancellationToken ct = default);
    Task<AccessPlan> PreviewAsync(AccessChange change, CancellationToken ct = default);
    Task<AccessResult> ApplyAsync(AccessChange change, CancellationToken ct = default);
    Task<IReadOnlyList<ProfileApplicationInfo>> ApplicationsOfAsync(Guid userId, bool includeEnded, CancellationToken ct = default);
    /// <summary>Ends the grants and ownerships one application created (and nothing else).</summary>
    Task EndApplicationAsync(Guid applicationId, string reason, CancellationToken ct = default);
}
