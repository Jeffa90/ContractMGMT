using System.Text.Json;
using System.Text.RegularExpressions;
using Ecp.Audit;
using Ecp.Configuration;
using Ecp.Identity;
using Ecp.Identity.Domain;
using Ecp.Persistence;
using Ecp.SharedKernel;
using Ecp.Tenancy;

namespace Ecp.Access;

public sealed record AuthorityMember(Guid UserId, Guid AssignmentId, string Name);

/// <summary>An approval authority as an administrator sees it: a name, what it approves, and who is in it. The stable machine key is for Advanced use only.</summary>
public sealed record ApprovalAuthority(string Key, string DisplayName, string UsedFor, bool Registered, bool FromPack, IReadOnlyList<AuthorityMember> Members, IReadOnlyList<AuthorityMember> Backups);

public sealed record AuthorityPerson(Guid UserId, string Name);

public sealed record AuthoritiesView(Guid OrganisationId, string OrganisationName, IReadOnlyList<ApprovalAuthority> Authorities, IReadOnlyList<AuthorityPerson> People);

/// <summary>What the administrator chose, by names. <paramref name="Key"/> is the existing authority being changed, or null for a new one that <paramref name="DisplayName"/> names.</summary>
public sealed record AuthorityChange(Guid OrganisationId, string? Key, string? DisplayName, IReadOnlyList<Guid> MemberIds, IReadOnlyList<Guid> BackupIds, string? Reason = null);

public sealed record AuthorityPlan(bool CanApply, string DisplayName, string Key, bool IsNew, IReadOnlyList<string> Steps, IReadOnlyList<string> Problems);

/// <summary>
/// Approval authority administration for people who do not think in group keys (docs/UX_REDESIGN.md section 11). The authorities offered are the ones the organisation's workflows already name (from its
/// published workflow, or from its pack when nothing is published yet) plus any already registered, so choosing the people is all that is left to do. A new authority is registered under a key the platform
/// generates from its name. Membership changes go through the approver-group service, so its delegation rules, empty-group protection and audit apply unchanged.
/// </summary>
public interface IApprovalAuthorities
{
    Task<AuthoritiesView> ViewAsync(Guid organisationId, CancellationToken ct = default);
    /// <summary>What saving would do, by a dry run of the real services that is rolled back.</summary>
    Task<AuthorityPlan> PreviewAsync(AuthorityChange change, CancellationToken ct = default);
    Task<AuthorityPlan> SaveAsync(AuthorityChange change, CancellationToken ct = default);
    /// <summary>The key of the authority the organisation's register-authorisation workflow names (from its published workflow, else its pack), or null. Read-only and trusted, for set-up.</summary>
    Task<string?> PackAuthorityKeyAsync(Guid organisationId, CancellationToken ct = default);
}

internal sealed class ApprovalAuthorityService(
    IApproverGroupService groups, IConfigurationReleases releases, IOrganisationPackContent packContent, IOrganisationStructureService structure, ITerminologyResolver terminology,
    IPeopleDirectory people, IAccessService access, IUnitOfWork uow, IAuditWriter audit, ReauthenticationContext reauth, IClock clock) : IApprovalAuthorities
{
    private static readonly TimeSpan StepUpWindow = TimeSpan.FromMinutes(10);
    private static readonly (string Kind, string UsedFor)[] WorkflowKinds =
    [
        ("WorkflowContractLifecycle", "Approving the registration of executed [[contract|plural|lower]]"),
        ("WorkflowVariation", "Approving [[variation|plural|lower]]"),
    ];

    /// <summary>The approver-group keys the organisation's workflows name, with what each is used for: from the published workflow, else from the pack's.</summary>
    private async Task<IReadOnlyList<(string Key, string UsedFor, bool FromPack)>> NamedByWorkflowsAsync(OrganisationSummary org, CancellationToken ct)
    {
        var found = new List<(string, string, bool)>();
        foreach (var (kind, usedFor) in WorkflowKinds)
        {
            string? json = null; var fromPack = false;
            if (await releases.LatestAsync(kind, org.Id, ct) is { } release) json = release.ContentJson;
            else if (org.SolutionPackKey is { } packKey)
            {
                try { json = (await packContent.GetStartingDocumentAsync(org.Id, packKey, kind, ct)).Json; fromPack = true; }
                catch (DomainException) { /* the pack is unavailable or a draft is waiting: nothing to offer from it */ }
            }
            if (json is null) continue;
            var key = ApproverGroupKeyIn(json);
            if (key is not null && found.All(f => f.Item1 != key)) found.Add((key, usedFor, fromPack));
            else if (key is not null) { var i = found.FindIndex(f => f.Item1 == key); found[i] = (key, found[i].Item2 + "; " + usedFor.ToLowerInvariant(), found[i].Item3); }
        }
        return found;
    }

    private static string? ApproverGroupKeyIn(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            string? Find(JsonElement e)
            {
                if (e.ValueKind == JsonValueKind.Object)
                    foreach (var p in e.EnumerateObject())
                    {
                        if (string.Equals(p.Name, "approverGroupKey", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(p.Value.GetString())) return p.Value.GetString();
                        if (Find(p.Value) is { } inner) return inner;
                    }
                return null;
            }
            return Find(doc.RootElement);
        }
        catch (JsonException) { return null; }
    }

    private async Task<OrganisationSummary> OrganisationAsync(Guid organisationId, CancellationToken ct)
        => await structure.GetOrganisationAsync(organisationId, ct) ?? throw new ResourceNotFoundException();

    public async Task<AuthoritiesView> ViewAsync(Guid organisationId, CancellationToken ct = default)
    {
        var org = await OrganisationAsync(organisationId, ct);
        var terms = await terminology.GetAsync(organisationId, null, ct);
        var named = await NamedByWorkflowsAsync(org, ct);
        var registered = (await groups.ListAsync(ct)).Where(g => g.IsActive && (g.OrganisationId is null || g.OrganisationId == organisationId)).ToList();
        var keys = named.Select(n => n.Key).Concat(registered.Select(r => r.Key)).Distinct(StringComparer.Ordinal).ToList();
        var result = new List<ApprovalAuthority>();
        foreach (var key in keys)
        {
            var reg = registered.FirstOrDefault(r => r.Key == key);
            var info = named.FirstOrDefault(n => n.Key == key);
            var members = new List<AuthorityMember>(); var backups = new List<AuthorityMember>();
            if (reg is not null)
            {
                var detail = await groups.GetAsync(key, ct);
                foreach (var m in detail.Members.Where(m => m.IsActive && (m.OrganisationId is null || m.OrganisationId == organisationId)))
                    (m.IsBackup ? backups : members).Add(new AuthorityMember(m.UserId, m.AssignmentId, m.DisplayName));
            }
            var name = terms.HasGroupName(key) ? terms.Group(key) : !string.IsNullOrWhiteSpace(reg?.Description) ? reg!.Description : key;
            result.Add(new ApprovalAuthority(key, name, info.UsedFor ?? "Approving work sent to this group", reg?.Registered ?? false, info.UsedFor is not null && info.Item3, members, backups));
        }
        var choices = (await people.ManageableUsersAsync(ct)).Where(p => p.Status != UserStatus.Disabled).OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase).Select(p => new AuthorityPerson(p.Id, p.DisplayName)).ToList();
        return new AuthoritiesView(organisationId, org.Name, result.OrderByDescending(a => a.FromPack).ThenBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase).ToList(), choices);
    }

    private static string KeyFrom(string displayName, IReadOnlyCollection<string> taken)
    {
        var slug = Regex.Replace(displayName.Trim().ToUpperInvariant(), "[^A-Z0-9]+", "-").Trim('-');
        if (slug.Length == 0) slug = "APPROVERS";
        if (slug.Length > 90) slug = slug[..90].Trim('-');
        var key = slug; var n = 2;
        while (taken.Contains(key, StringComparer.OrdinalIgnoreCase)) key = $"{slug}-{n++}";
        return key;
    }

    public async Task<AuthorityPlan> PreviewAsync(AuthorityChange change, CancellationToken ct = default)
    {
        AuthorityPlan? plan = null;
        try
        {
            await uow.ExecuteAsync<bool>(async c =>
            {
                try { plan = await ApplyAsync(change, c); }
                catch (Exception ex) when (ex is DomainException or ForbiddenException or ResourceNotFoundException)
                { plan = new AuthorityPlan(false, change.DisplayName ?? change.Key ?? "", change.Key ?? "", change.Key is null, [], [ex is DomainException d ? d.RawMessage : "You are not allowed to change this approval authority."]); }
                throw new RolledBackForPreview();
            }, ct);
        }
        catch (RolledBackForPreview) { }
        return plan!;
    }

    public async Task<AuthorityPlan> SaveAsync(AuthorityChange change, CancellationToken ct = default)
    {
        reauth.Require(clock.UtcNow, StepUpWindow);   // giving people approval authority needs a recent sign-in
        return await uow.ExecuteAsync(c => ApplyAsync(change, c), ct);
    }

    public async Task<string?> PackAuthorityKeyAsync(Guid organisationId, CancellationToken ct = default)
    {
        var org = await OrganisationAsync(organisationId, ct);
        return (await NamedByWorkflowsAsync(org, ct)).Select(n => n.Key).FirstOrDefault();
    }

    private async Task<AuthorityPlan> ApplyAsync(AuthorityChange change, CancellationToken ct)
    {
        var org = await OrganisationAsync(change.OrganisationId, ct);
        var steps = new List<string>();
        var registered = (await groups.ListAsync(ct)).ToList();
        string key; string display; var isNew = false;
        if (!string.IsNullOrWhiteSpace(change.Key)) key = change.Key.Trim();
        else
        {
            display = Guard.NotBlank(change.DisplayName, "Name", 200);
            key = KeyFrom(display, registered.Select(r => r.Key).ToList());
            isNew = true;
        }
        var terms = await terminology.GetAsync(change.OrganisationId, null, ct);
        var existing = registered.FirstOrDefault(r => r.Key == key);
        display = terms.HasGroupName(key) ? terms.Group(key) : !string.IsNullOrWhiteSpace(change.DisplayName) ? change.DisplayName!.Trim() : !string.IsNullOrWhiteSpace(existing?.Description) ? existing!.Description : key;
        if (existing is null || !existing.Registered)
        {
            await groups.CreateAsync(key, terms.HasGroupName(key) ? null : display, change.OrganisationId, allowDelegation: true, ct);
            steps.Add($"Set up {display} as an approval authority.");
            isNew = existing is null;
        }
        var selected = change.MemberIds.Distinct().ToList();
        var backupSelected = change.BackupIds.Distinct().Where(b => !selected.Contains(b)).ToList();
        if (change.BackupIds.Distinct().Any(selected.Contains)) throw new DomainException("both_roles", "A person is either a member or a backup, not both.");
        var detail = existing is null ? null : await groups.GetAsync(key, ct);
        var current = (detail?.Members ?? []).Where(m => m.IsActive && (m.OrganisationId is null || m.OrganisationId == change.OrganisationId)).ToList();
        var names = (await people.ManageableUsersAsync(ct)).ToDictionary(p => p.Id, p => p.DisplayName);
        string Name(Guid id) => names.GetValueOrDefault(id, "that person");
        var scale = await access.ClassificationScaleAsync(ct);
        var ceiling = AccessProfiles.DefaultCeiling(AccessProfiles.Find(AccessProfiles.ApprovalAuthority)!, scale);
        var why = string.IsNullOrWhiteSpace(change.Reason) ? "Chosen on the approval authorities page" : change.Reason.Trim();

        foreach (var m in current.Where(m => (m.IsBackup ? !backupSelected.Contains(m.UserId) : !selected.Contains(m.UserId))))
        {
            await groups.EndMemberAsync(key, m.AssignmentId, why, ct);
            steps.Add($"Remove {m.DisplayName} as {(m.IsBackup ? "a backup" : "a member")} of {display}.");
        }
        foreach (var id in selected.Where(id => !current.Any(m => !m.IsBackup && m.UserId == id)))
        {
            await groups.AddMemberAsync(key, id, isBackup: false, change.OrganisationId, ceiling, null, null, why, ct);
            steps.Add($"Make {Name(id)} a member of {display}, who can approve.");
        }
        foreach (var id in backupSelected.Where(id => !current.Any(m => m.IsBackup && m.UserId == id)))
        {
            await groups.AddMemberAsync(key, id, isBackup: true, change.OrganisationId, ceiling, null, null, why, ct);
            steps.Add($"Make {Name(id)} a backup for {display}, who can approve when the members are away.");
        }
        if (steps.Count == 0) steps.Add("Nothing changes: the people chosen are already in place.");
        audit.Record(new AuditEntry("access.approval_authority.saved", "ApproverGroup", key, OrganisationId: change.OrganisationId, Reason: why,
            NewValuesJson: JsonSerializer.Serialize(new { key, members = selected.Count, backups = backupSelected.Count, created = existing is null || !existing.Registered })));
        _ = org;
        return new AuthorityPlan(true, display, key, isNew, steps, []);
    }
}
