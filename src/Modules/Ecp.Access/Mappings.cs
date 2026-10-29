using Ecp.Configuration;
using Ecp.Contracts;
using Ecp.Identity;
using Ecp.Identity.Access;
using Ecp.Identity.Domain;
using Ecp.Identity.Providers;
using Ecp.SharedKernel;
using Ecp.Tenancy;

namespace Ecp.Access;

/// <summary>A kind of access an identity group may be given, in words. Only the closed safe catalogue is ever offered.</summary>
public sealed record MappingRole(string RoleCode, string Label, string Description, bool NarrowsByScope);

public sealed record ProviderChoice(string Key, string DisplayName);

public sealed record MappingRequest(string ProviderKey, string GroupName, string RoleCode, Guid OrganisationId, ScopeChoice Scope = ScopeChoice.WholeOrganisation, IReadOnlyList<Guid>? ScopeIds = null, string? Ceiling = null);

public sealed record MappingRow(Guid Id, string ProviderKey, string ProviderName, string Group, string Access, string Where, string Ceiling, bool Active, string Sentence);

public sealed record MappingPreview(bool CanSave, string Sentence, IReadOnlyList<string> Problems);

/// <summary>
/// Identity group mappings in words (Advanced; docs/UX_REDESIGN.md section 10). Authorised on the server: <c>rolemappings.manage</c> for the organisation, the delegation policy and scope containment, a recent
/// sign-in, a closed catalogue of roles, and no high classification ceiling. A mapping gives access automatically at sign-in to whoever the provider says is in the group, which is why it is kept narrow.
/// </summary>
public interface IGroupMappingAdministration
{
    IReadOnlyList<MappingRole> Roles { get; }
    Task<IReadOnlyList<ProviderChoice>> ProvidersAsync(CancellationToken ct = default);
    Task<IReadOnlyList<MappingRow>> ListAsync(CancellationToken ct = default);
    Task<MappingPreview> PreviewAsync(MappingRequest request, CancellationToken ct = default);
    Task<Guid> AddAsync(MappingRequest request, CancellationToken ct = default);
    Task EndAsync(Guid mappingId, string reason, CancellationToken ct = default);
}

internal sealed class GroupMappingAdministration(
    IGroupMappingService mappings, IProviderRegistry providers, IAccessService access, IOrganisationStructureService structure, ICategoryAdministration categoryAdmin, IClock clock,
    ReauthenticationContext reauth) : IGroupMappingAdministration
{
    private static readonly TimeSpan StepUpWindow = TimeSpan.FromMinutes(10);

    public IReadOnlyList<MappingRole> Roles { get; } =
    [
        new(RoleCodes.Viewer, "Read [[contract|plural|lower]]", "See [[contract|plural|lower]] and download documents, but change nothing.", true),
        new(RoleCodes.Legal, "Review ([[legal.review|lower]])", "Review [[contract|plural|lower]] and give a formal opinion.", false),
        new(RoleCodes.Auditor, "Audit", "Read [[contract|plural|lower]], the audit trail and exports.", false),
        new(RoleCodes.CategoryOwner, "Own [[category|plural|lower]]", "Gives the role that lets owners work. Which [[category|plural|lower]] they own is set separately, by name.", false),
        new(RoleCodes.ProcurementAdministrator, "Administer [[contract|plural|lower]]", "Create and manage [[contract|plural|lower]] in any [[category|lower]].", false),
        new(RoleCodes.DivisionSiteAdministrator, "Administer a [[division|lower]] or [[site|lower]]", "Give access to colleagues within one [[division|lower]] or [[site|lower]].", true),
    ];

    private async Task<IReadOnlyList<AccessGrant>> ManagerGrantsAsync(CancellationToken ct)
    {
        var grants = await access.GrantsAsync("rolemappings.manage", ct);
        if (grants.Count == 0) throw new ForbiddenException("You cannot manage identity group mappings.");
        return grants;
    }

    private static bool Covers(IReadOnlyList<AccessGrant> grants, IEnumerable<Guid> organisations)
        => organisations.All(o => grants.Any(g => g.OrganisationIds is null || g.OrganisationIds.Contains(o)));

    public async Task<IReadOnlyList<ProviderChoice>> ProvidersAsync(CancellationToken ct = default)
    {
        await ManagerGrantsAsync(ct);
        return (await providers.ListAsync(ct)).Where(p => p.Enabled).Select(p => new ProviderChoice(p.Key, p.DisplayName)).ToList();
    }

    public async Task<IReadOnlyList<MappingRow>> ListAsync(CancellationToken ct = default)
    {
        var grants = await ManagerGrantsAsync(ct);
        var names = await ScopeNames.LoadAsync(structure, categoryAdmin, ct);
        var providerNames = (await providers.ListAsync(ct)).ToDictionary(p => p.Key, p => p.DisplayName);
        var rows = new List<MappingRow>();
        foreach (var m in await mappings.ListAsync(ct))
        {
            // A caller sees only the mappings that sit wholly inside the organisations they manage; tenant-level mappings only a tenant-wide manager sees.
            var inside = m.Scope.Level == ScopeLevel.Organisation ? Covers(grants, m.Scope.OrganisationIds) : grants.Any(g => g.OrganisationIds is null);
            if (!inside) continue;
            var role = Roles.FirstOrDefault(r => r.RoleCode == m.RoleCode);
            var access = role?.Label ?? EffectiveAccessService.RoleToken(m.RoleCode);
            var where = names.Where(m.Scope);
            var provider = providerNames.GetValueOrDefault(m.ProviderKey, m.ProviderKey);
            rows.Add(new MappingRow(m.Id, m.ProviderKey, provider, m.GroupKey, access, where, m.Scope.ClassificationCeiling, m.IsActive, Sentence(provider, m.GroupKey, access, where, m.Scope.ClassificationCeiling)));
        }
        return rows;
    }

    private static string Sentence(string provider, string group, string access, string where, string ceiling)
        => $"People in the group “{group}” ({provider}) will be able to: {access.ToLowerInvariant()}, in {where}, up to {ceiling}. They get it at their next sign-in, and lose it when the provider no longer says they are in the group.";

    private async Task<(GrantRequest? Request, List<string> Problems, string Sentence)> BuildAsync(MappingRequest r, CancellationToken ct)
    {
        var problems = new List<string>();
        var grants = await ManagerGrantsAsync(ct);
        var scale = await access.ClassificationScaleAsync(ct);
        if (string.IsNullOrWhiteSpace(r.GroupName)) problems.Add("Enter the group name exactly as your identity provider sends it.");
        else if (r.GroupName.Length > 500) problems.Add("The group name is too long.");
        var role = Roles.FirstOrDefault(x => x.RoleCode == r.RoleCode);
        if (role is null || !GroupMappingPolicy.SafeRoles.Contains(r.RoleCode)) problems.Add("An identity group cannot be given that kind of access. Administrator and approval access is given to named people.");
        var provider = (await providers.ListAsync(ct)).FirstOrDefault(p => p.Key == r.ProviderKey && p.Enabled);
        if (provider is null) problems.Add("Choose one of the configured sign-in providers.");
        if (!Covers(grants, [r.OrganisationId])) problems.Add("You are not allowed to manage mappings for that organisation.");
        var ceiling = scale.Canonical(r.Ceiling) ?? (string.IsNullOrWhiteSpace(r.Ceiling) ? scale.DefaultLevel : null);
        if (ceiling is null) problems.Add("That classification level is not one of this organisation's levels.");
        else if (scale.Rank(ceiling) >= scale.HighCeilingThreshold) problems.Add("A group cannot be given a high classification level. Give that to a named person instead.");
        if (r.Scope != ScopeChoice.WholeOrganisation && (r.ScopeIds is null || r.ScopeIds.Count == 0)) problems.Add("Choose at least one place, or choose the whole organisation.");
        if (role is { NarrowsByScope: false } && r.Scope != ScopeChoice.WholeOrganisation) problems.Add($"{role.Label} applies to the whole organisation and cannot be narrowed.");
        if (problems.Count > 0) return (null, problems, "");

        var ids = r.Scope == ScopeChoice.WholeOrganisation ? [] : r.ScopeIds!;
        var scope = ScopeSet.ForOrganisations([r.OrganisationId], ceiling!,
            r.Scope == ScopeChoice.BusinessUnits ? ids : null, r.Scope == ScopeChoice.Divisions ? ids : null, r.Scope == ScopeChoice.Sites ? ids : null, r.Scope == ScopeChoice.Categories ? ids : null);
        ViewerKind? kind = r.RoleCode != RoleCodes.Viewer ? null : r.Scope switch
        {
            ScopeChoice.Categories => ViewerKind.Category, ScopeChoice.Sites => ViewerKind.Site, ScopeChoice.BusinessUnits or ScopeChoice.Divisions => ViewerKind.BusinessUnit, _ => ViewerKind.AllContracts
        };
        // The delegation policy decides, with the caller's own authority: you cannot map a group to access you could not give a person.
        var subject = await access.GetSubjectAsync(ct);
        var why = subject is null ? "Sign in first." : DelegationPolicy.WhyNot(PermissionMatrix.Instance, subject.Assignments, clock.UtcNow, r.RoleCode, scope, scale);
        if (why is not null) return (null, ["You are not allowed to give that access to a group. Ask a more senior administrator."], "");
        var names = await ScopeNames.LoadAsync(structure, categoryAdmin, ct);
        return (new GrantRequest(Guid.Empty, r.RoleCode, kind, scope), problems, Sentence(provider!.DisplayName, r.GroupName.Trim(), role!.Label, names.Where(scope), ceiling!));
    }

    public async Task<MappingPreview> PreviewAsync(MappingRequest request, CancellationToken ct = default)
    {
        var (built, problems, sentence) = await BuildAsync(request, ct);
        return new MappingPreview(built is not null, sentence, problems);
    }

    public async Task<Guid> AddAsync(MappingRequest request, CancellationToken ct = default)
    {
        reauth.Require(clock.UtcNow, StepUpWindow);
        var (built, problems, _) = await BuildAsync(request, ct);
        if (built is null) throw new DomainException("mapping_refused", string.Join(" ", problems));
        return await mappings.AddAsync(request.ProviderKey, request.GroupName.Trim(), request.RoleCode, built.ViewerKind, built.Scope, null, ct);
    }

    public async Task EndAsync(Guid mappingId, string reason, CancellationToken ct = default)
    {
        reauth.Require(clock.UtcNow, StepUpWindow);
        Guard.NotBlank(reason, "Reason", 1000);
        var row = (await ListAsync(ct)).FirstOrDefault(m => m.Id == mappingId) ?? throw new ResourceNotFoundException();
        _ = row;
        await mappings.DeactivateAsync(mappingId, ct);
    }
}
