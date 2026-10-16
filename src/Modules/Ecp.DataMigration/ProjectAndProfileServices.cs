using Ecp.DataMigration.Domain;
using Ecp.DataMigration.Formats;
using Ecp.Formats;
using Ecp.DataMigration.Mapping;
using Ecp.Identity.Access;
using Ecp.Persistence;
using Ecp.SharedKernel;
using Ecp.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Ecp.DataMigration;

public interface IMigrationProjects
{
    Task<IReadOnlyList<ProjectSummary>> ListAsync(CancellationToken ct = default);
    /// <summary>The organisations the caller may stage migrations for (those their migration.stage permission covers).</summary>
    Task<IReadOnlyList<OrganisationOption>> ListOrganisationsAsync(CancellationToken ct = default);
    Task<ProjectDetail> GetAsync(Guid projectId, CancellationToken ct = default);
    Task<Guid> CreateAsync(CreateProjectInput input, CancellationToken ct = default);
    Task SetReferencePolicyAsync(Guid projectId, ReferencePolicy policy, CancellationToken ct = default);
    /// <summary>Allows legacy loads under a policy (needs migration.legacy_load): allowed target states, Registered Date import, locked-record overwrite, expiry.</summary>
    Task SetLegacyPolicyAsync(Guid projectId, LegacyPolicyInput input, CancellationToken ct = default);
    Task CloseLegacyPolicyAsync(Guid projectId, CancellationToken ct = default);
    /// <summary>Records the cutover freeze window and whether the target freeze is on (needs migration.commit: it stops people creating contracts).</summary>
    Task SetFreezeAsync(Guid projectId, DateTime startUtc, DateTime endUtc, bool targetFreezeEnabled, CancellationToken ct = default);
    Task CloseProjectAsync(Guid projectId, CancellationToken ct = default);
}

internal sealed class MigrationProjectService(MigrationKernel k, ITenantDirectory tenants, IOrganisationStructureService structure) : IMigrationProjects
{
    public async Task<IReadOnlyList<OrganisationOption>> ListOrganisationsAsync(CancellationToken ct = default)
    {
        var grants = await k.Access.GrantsAsync(MigrationPermissions.Stage, ct);
        if (grants.Count == 0) return [];
        var all = grants.Any(g => g.OrganisationIds is null);
        var allowed = all ? null : grants.SelectMany(g => g.OrganisationIds!).ToHashSet();
        return (await structure.ListOrganisationsAsync(ct)).Where(o => allowed == null || allowed.Contains(o.Id)).Select(o => new OrganisationOption(o.Id, o.Name)).ToList();
    }

    public async Task<IReadOnlyList<ProjectSummary>> ListAsync(CancellationToken ct = default)
    {
        var grants = await k.Access.GrantsAsync(MigrationPermissions.Stage, ct);
        if (grants.Count == 0) return [];
        var orgs = grants.Any(g => g.OrganisationIds is null) ? null : grants.SelectMany(g => g.OrganisationIds!).ToHashSet();
        var projects = await k.Db.Projects.AsNoTracking().Where(p => orgs == null || orgs.Contains(p.OrganisationId)).OrderByDescending(p => p.CreatedUtc).ToListAsync(ct);
        var counts = await k.Db.Batches.AsNoTracking().GroupBy(b => b.ProjectId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        return projects.Select(p => new ProjectSummary(p.Id, p.OrganisationId, p.Name, p.SourceSystem, p.SourceSystemVersion, p.DataOwner, p.ExtractionUtc, p.Status, counts.GetValueOrDefault(p.Id), p.CreatedUtc)).ToList();
    }

    public async Task<ProjectDetail> GetAsync(Guid projectId, CancellationToken ct = default)
    {
        var p = await k.ProjectAsync(projectId, MigrationPermissions.Stage, track: false, ct);
        var batches = await k.Db.Batches.AsNoTracking().Where(b => b.ProjectId == projectId).OrderByDescending(b => b.CreatedUtc)
            .Select(b => new BatchSummary(b.Id, b.ProjectId, b.Name, b.Kind, b.Status, b.IsLegacyLoad, b.DuplicateStrategy, b.CreatedBy, b.CreatedUtc, b.SourceRows, b.Created, b.Updated, b.Rejected, b.Skipped)).ToListAsync(ct);
        return new ProjectDetail(p.Id, p.OrganisationId, p.Name, p.SourceSystem, p.SourceSystemVersion, p.DataOwner, p.ExtractionUtc, p.FreezeStartUtc, p.FreezeEndUtc, p.TargetFreezeEnabled,
            p.LegacyLoadEnabled, p.LegacyAllowRegisteredDateImport, p.LegacyPolicyExpiresUtc, p.LegacyAllowedStatuses, p.LegacyAllowOverwriteLocked, p.LegacyOverwriteReason, p.ReferencePolicy,
            p.Status, p.CreatedBy, p.CreatedUtc, batches,
            new ProjectCapabilities(p.Status == ProjectStatus.Active, await k.HoldsAsync(MigrationPermissions.LegacyLoad, p, ct), await k.HoldsAsync(MigrationPermissions.Commit, p, ct), await k.HoldsAsync(MigrationPermissions.Accept, p, ct)));
    }

    public async Task<Guid> CreateAsync(CreateProjectInput r, CancellationToken ct = default)
    {
        // Cross-tenant and cross-organisation references are rejected (INV-48): other tenants' organisations are invisible.
        if (!await tenants.OrganisationExistsAsync(r.OrganisationId, ct)) throw new DomainException("organisation_not_found", "The organisation was not found.");
        await k.Access.RequireAsync(MigrationPermissions.Stage, MigrationKernel.Describe(k.TenantId, r.OrganisationId, "new"), ct);
        var project = MigrationProject.Create(k.TenantId, r.OrganisationId, r.Name, r.SourceSystem, r.SourceSystemVersion, r.DataOwner, r.ExtractionUtc, k.Clock.UtcNow, await k.ActorLabelAsync(ct));
        return await k.Uow.ExecuteAsync(async c =>
        {
            k.Db.Projects.Add(project);
            try { await k.Db.SaveChangesAsync(c); }
            catch (DbUpdateException ex) when (DbExceptions.IsUniqueViolation(ex)) { throw new DomainException("project_name_in_use", "A migration project with that name already exists."); }
            k.Record("migration.project.created", nameof(MigrationProject), project.Id.ToString(), project.OrganisationId, values: new { project.Name, project.SourceSystem, project.DataOwner });
            return project.Id;
        }, ct);
    }

    public async Task SetReferencePolicyAsync(Guid projectId, ReferencePolicy policy, CancellationToken ct = default)
    {
        var p = await k.ProjectAsync(projectId, MigrationPermissions.Stage, track: true, ct);
        if (await k.Db.Batches.AnyAsync(b => b.ProjectId == projectId && (b.Status == BatchStatus.Committing || b.Status == BatchStatus.Committed || b.Status == BatchStatus.Reconciled), ct))
            throw new DomainException("batch_in_flight", "The reference policy cannot change while a batch is committing or awaiting acceptance.");
        p.SetReferencePolicy(policy);
        await Save(p, "migration.project.reference_policy", new { policy = policy.ToString() }, ct);
    }

    public async Task SetLegacyPolicyAsync(Guid projectId, LegacyPolicyInput i, CancellationToken ct = default)
    {
        var p = await k.ProjectAsync(projectId, MigrationPermissions.LegacyLoad, track: true, ct);
        p.EnableLegacyLoad(i.AllowRegisteredDateImport, i.ExpiresUtc, k.Clock.UtcNow, i.AllowedStatusCodes, i.AllowOverwriteLocked, i.OverwriteReason);
        await Save(p, "migration.project.legacy_policy_set", new { i.AllowRegisteredDateImport, i.ExpiresUtc, AllowedStatuses = p.LegacyAllowedStatuses, i.AllowOverwriteLocked, i.OverwriteReason }, ct);
    }

    public async Task CloseLegacyPolicyAsync(Guid projectId, CancellationToken ct = default)
    {
        var p = await k.ProjectAsync(projectId, MigrationPermissions.LegacyLoad, track: true, ct);
        p.CloseLegacyLoad();
        await Save(p, "migration.project.legacy_policy_closed", new { }, ct);
    }

    public async Task SetFreezeAsync(Guid projectId, DateTime startUtc, DateTime endUtc, bool targetFreezeEnabled, CancellationToken ct = default)
    {
        var p = await k.ProjectAsync(projectId, MigrationPermissions.Commit, track: true, ct);
        p.SetFreezeWindow(startUtc, endUtc);
        p.SetTargetFreeze(targetFreezeEnabled);
        await Save(p, "migration.project.freeze_set", new { startUtc, endUtc, targetFreezeEnabled }, ct);
    }

    public async Task CloseProjectAsync(Guid projectId, CancellationToken ct = default)
    {
        var p = await k.ProjectAsync(projectId, MigrationPermissions.Accept, track: true, ct);
        if (await k.Db.Batches.AnyAsync(b => b.ProjectId == projectId && (b.Status == BatchStatus.Committing || b.Status == BatchStatus.Committed || b.Status == BatchStatus.Reconciled || b.Status == BatchStatus.Failed), ct))
            throw new DomainException("batch_in_flight", "Accept, roll back or compensate every open batch before closing the project.");
        p.Close();
        await Save(p, "migration.project.closed", new { }, ct);
    }

    private async Task Save(MigrationProject p, string action, object values, CancellationToken ct)
        => await k.Uow.ExecuteAsync(async c =>
        {
            try { await k.Db.SaveChangesAsync(c); }
            catch (DbUpdateConcurrencyException) { throw new DomainException("concurrency_conflict", "The project was changed by someone else. Reload and try again."); }
            k.Record(action, nameof(MigrationProject), p.Id.ToString(), p.OrganisationId, values: values);
        }, ct);
}

public interface IMappingProfiles
{
    Task<IReadOnlyList<ProfileSummary>> ListAsync(CancellationToken ct = default);
    Task<ProfileDetail> GetAsync(Guid profileId, CancellationToken ct = default);
    Task<ProfileVersionDetail> GetVersionAsync(Guid versionId, CancellationToken ct = default);
    Task<Guid> CreateAsync(Guid organisationId, string name, string sourceSystem, string? sourceSystemVersion, CancellationToken ct = default);
    /// <summary>The open draft of the profile, or a new draft copied from the latest version.</summary>
    Task<Guid> OpenDraftAsync(Guid profileId, CancellationToken ct = default);
    Task<IReadOnlyList<string>> ImportFieldMappingsAsync(Guid versionId, Stream csv, CancellationToken ct = default);
    Task<IReadOnlyList<string>> ImportLookupsAsync(Guid versionId, Stream csv, CancellationToken ct = default);
    Task SetOptionsAsync(Guid versionId, ProfileOptions options, CancellationToken ct = default);
    Task<IReadOnlyList<DefinitionIssue>> ValidateAsync(Guid versionId, CancellationToken ct = default);
    Task PublishAsync(Guid versionId, string? note, CancellationToken ct = default);
    Task<byte[]> ExportFieldMappingsAsync(Guid versionId, CancellationToken ct = default);
    Task<byte[]> ExportLookupsAsync(Guid versionId, CancellationToken ct = default);
}

internal sealed class MappingProfileService(MigrationKernel k, ITenantDirectory tenants) : IMappingProfiles
{
    public async Task<IReadOnlyList<ProfileSummary>> ListAsync(CancellationToken ct = default)
    {
        var grants = await k.Access.GrantsAsync(MigrationPermissions.Stage, ct);
        if (grants.Count == 0) return [];
        var orgs = grants.Any(g => g.OrganisationIds is null) ? null : grants.SelectMany(g => g.OrganisationIds!).ToHashSet();
        var profiles = await k.Db.Profiles.AsNoTracking().Where(p => orgs == null || orgs.Contains(p.OrganisationId)).OrderBy(p => p.Name).ToListAsync(ct);
        var published = await k.Db.ProfileVersions.AsNoTracking().Where(v => v.Status == ProfileVersionStatus.Published).GroupBy(v => v.ProfileId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        return profiles.Select(p => new ProfileSummary(p.Id, p.OrganisationId, p.Name, p.SourceSystem, p.SourceSystemVersion, p.LatestVersionNumber, published.GetValueOrDefault(p.Id), p.CreatedBy, p.CreatedUtc)).ToList();
    }

    public async Task<ProfileDetail> GetAsync(Guid profileId, CancellationToken ct = default)
    {
        var p = await ProfileAsync(profileId, track: false, ct);
        var versions = (await k.Db.ProfileVersions.AsNoTracking().Where(v => v.ProfileId == profileId).OrderByDescending(v => v.VersionNumber).ToListAsync(ct)).Select(ToSummary).ToList();
        var published = versions.Count(v => v.Status == ProfileVersionStatus.Published);
        return new ProfileDetail(new ProfileSummary(p.Id, p.OrganisationId, p.Name, p.SourceSystem, p.SourceSystemVersion, p.LatestVersionNumber, published, p.CreatedBy, p.CreatedUtc), versions);
    }

    public async Task<ProfileVersionDetail> GetVersionAsync(Guid versionId, CancellationToken ct = default)
    {
        var (profile, version) = await VersionAsync(versionId, track: false, ct);
        var definition = MappingDefinition.FromJson(version.DefinitionJson);
        return new ProfileVersionDetail(profile.Id, profile.Name, profile.OrganisationId, ToSummary(version), definition, MappingDefinitionValidator.Validate(definition));
    }

    public async Task<Guid> CreateAsync(Guid organisationId, string name, string sourceSystem, string? sourceSystemVersion, CancellationToken ct = default)
    {
        if (!await tenants.OrganisationExistsAsync(organisationId, ct)) throw new DomainException("organisation_not_found", "The organisation was not found.");
        await k.Access.RequireAsync(MigrationPermissions.Stage, MigrationKernel.Describe(k.TenantId, organisationId, "new-profile"), ct);
        var by = await k.ActorLabelAsync(ct);
        var profile = MappingProfile.Create(k.TenantId, organisationId, name, sourceSystem, sourceSystemVersion, by, k.Clock.UtcNow);
        var empty = MappingDefinition.Empty;
        var version = profile.NewVersion(empty.ToJson(), empty.Hash(), by, k.Clock.UtcNow, null);
        return await k.Uow.ExecuteAsync(async c =>
        {
            k.Db.Profiles.Add(profile);
            await k.Db.SaveChangesAsync(c);
            k.Db.ProfileVersions.Add(version);
            try { await k.Db.SaveChangesAsync(c); }
            catch (DbUpdateException ex) when (DbExceptions.IsUniqueViolation(ex)) { throw new DomainException("profile_name_in_use", "A mapping profile with that name already exists."); }
            k.Record("migration.profile.created", nameof(MappingProfile), profile.Id.ToString(), organisationId, values: new { profile.Name, profile.SourceSystem });
            return profile.Id;
        }, ct);
    }

    public async Task<Guid> OpenDraftAsync(Guid profileId, CancellationToken ct = default)
    {
        var profile = await ProfileAsync(profileId, track: true, ct);
        var versions = await k.Db.ProfileVersions.Where(v => v.ProfileId == profileId).OrderByDescending(v => v.VersionNumber).ToListAsync(ct);
        if (versions.FirstOrDefault(v => v.Status == ProfileVersionStatus.Draft) is { } open) return open.Id;
        var latest = versions[0];
        var next = profile.NewVersion(latest.DefinitionJson, latest.DefinitionHash, await k.ActorLabelAsync(ct), k.Clock.UtcNow, $"Based on version {latest.VersionNumber}");
        return await k.Uow.ExecuteAsync(async c =>
        {
            k.Db.ProfileVersions.Add(next);
            await k.Db.SaveChangesAsync(c);
            k.Record("migration.profile.draft_opened", nameof(MappingProfileVersion), next.Id.ToString(), profile.OrganisationId, values: new { profile.Name, next.VersionNumber });
            return next.Id;
        }, ct);
    }

    public async Task<IReadOnlyList<string>> ImportFieldMappingsAsync(Guid versionId, Stream csv, CancellationToken ct = default)
    {
        var (profile, version) = await VersionAsync(versionId, track: true, ct);
        IReadOnlyList<FieldMapping> fields; IReadOnlyList<string> problems;
        try { fields = ProfileCsv.ParseFieldMappings(csv, out problems); }
        catch (SourceFormatException ex) { throw new DomainException("profile_file_unreadable", $"{ex.Code}: {ex.Message}"); }
        var current = MappingDefinition.FromJson(version.DefinitionJson);
        await ReplaceAsync(profile, version, current with { Fields = fields }, "migration.profile.mappings_imported", new { Rows = fields.Count }, ct);
        return problems;
    }

    public async Task<IReadOnlyList<string>> ImportLookupsAsync(Guid versionId, Stream csv, CancellationToken ct = default)
    {
        var (profile, version) = await VersionAsync(versionId, track: true, ct);
        IReadOnlyList<LookupEntry> lookups; IReadOnlyList<string> problems;
        try { lookups = ProfileCsv.ParseLookups(csv, out problems); }
        catch (SourceFormatException ex) { throw new DomainException("profile_file_unreadable", $"{ex.Code}: {ex.Message}"); }
        var current = MappingDefinition.FromJson(version.DefinitionJson);
        await ReplaceAsync(profile, version, current with { Lookups = lookups }, "migration.profile.lookups_imported", new { Rows = lookups.Count }, ct);
        return problems;
    }

    public async Task SetOptionsAsync(Guid versionId, ProfileOptions options, CancellationToken ct = default)
    {
        var (profile, version) = await VersionAsync(versionId, track: true, ct);
        var current = MappingDefinition.FromJson(version.DefinitionJson);
        await ReplaceAsync(profile, version, current with { Options = options }, "migration.profile.options_set", new { options.CreateMissingCategories, HasNamingRule = options.NamingRule is not null }, ct);
    }

    public async Task<IReadOnlyList<DefinitionIssue>> ValidateAsync(Guid versionId, CancellationToken ct = default)
    {
        var (_, version) = await VersionAsync(versionId, track: false, ct);
        return MappingDefinitionValidator.Validate(MappingDefinition.FromJson(version.DefinitionJson));
    }

    public async Task PublishAsync(Guid versionId, string? note, CancellationToken ct = default)
    {
        var (profile, version) = await VersionAsync(versionId, track: true, ct);
        var issues = MappingDefinitionValidator.Validate(MappingDefinition.FromJson(version.DefinitionJson)).Where(i => i.IsError).ToList();
        if (issues.Count > 0) throw new DomainException("profile_invalid", $"The profile has {issues.Count} problem(s) to fix before it can be published: {issues[0].Field}: {issues[0].Message}");
        version.Publish(await k.ActorLabelAsync(ct), k.Clock.UtcNow);
        await k.Uow.ExecuteAsync(async c =>
        {
            try { await k.Db.SaveChangesAsync(c); }
            catch (DbUpdateConcurrencyException) { throw new DomainException("concurrency_conflict", "The profile was changed by someone else. Reload and try again."); }
            k.Record("migration.profile.published", nameof(MappingProfileVersion), version.Id.ToString(), profile.OrganisationId, values: new { profile.Name, version.VersionNumber, version.DefinitionHash }, reason: note);
        }, ct);
    }

    public async Task<byte[]> ExportFieldMappingsAsync(Guid versionId, CancellationToken ct = default)
    {
        var (_, version) = await VersionAsync(versionId, track: false, ct);
        return ProfileCsv.ExportFieldMappings(MappingDefinition.FromJson(version.DefinitionJson));
    }

    public async Task<byte[]> ExportLookupsAsync(Guid versionId, CancellationToken ct = default)
    {
        var (_, version) = await VersionAsync(versionId, track: false, ct);
        return ProfileCsv.ExportLookups(MappingDefinition.FromJson(version.DefinitionJson));
    }

    private async Task ReplaceAsync(MappingProfile profile, MappingProfileVersion version, MappingDefinition next, string action, object values, CancellationToken ct)
    {
        version.ReplaceDefinition(next.ToJson(), next.Hash());
        await k.Uow.ExecuteAsync(async c =>
        {
            try { await k.Db.SaveChangesAsync(c); }
            catch (DbUpdateConcurrencyException) { throw new DomainException("concurrency_conflict", "The profile was changed by someone else. Reload and try again."); }
            k.Record(action, nameof(MappingProfileVersion), version.Id.ToString(), profile.OrganisationId, values: values);
        }, ct);
    }

    private async Task<MappingProfile> ProfileAsync(Guid profileId, bool track, CancellationToken ct)
    {
        var profile = await (track ? k.Db.Profiles.AsTracking() : k.Db.Profiles.AsNoTracking()).FirstOrDefaultAsync(p => p.Id == profileId, ct) ?? throw new ResourceNotFoundException();
        await k.Access.RequireAsync(MigrationPermissions.Stage, MigrationKernel.Describe(profile.TenantId, profile.OrganisationId, profile.Id.ToString()), ct);
        return profile;
    }

    private async Task<(MappingProfile Profile, MappingProfileVersion Version)> VersionAsync(Guid versionId, bool track, CancellationToken ct)
    {
        var version = await (track ? k.Db.ProfileVersions.AsTracking() : k.Db.ProfileVersions.AsNoTracking()).FirstOrDefaultAsync(v => v.Id == versionId, ct) ?? throw new ResourceNotFoundException();
        return (await ProfileAsync(version.ProfileId, track, ct), version);
    }

    private static ProfileVersionSummary ToSummary(MappingProfileVersion v)
    {
        var d = MappingDefinition.FromJson(v.DefinitionJson);
        return new ProfileVersionSummary(v.Id, v.VersionNumber, v.Status, v.DefinitionHash, v.Note, v.CreatedBy, v.CreatedUtc, v.PublishedBy, v.PublishedUtc, d.Fields.Count, d.Lookups.Count);
    }
}
