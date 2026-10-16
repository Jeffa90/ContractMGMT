using System.Text.Json;
using Ecp.Audit;
using Ecp.Configuration;
using Ecp.Identity;
using Ecp.Identity.Domain;
using Ecp.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace Ecp.Metadata;

public static class MetadataModule
{
    public const string Name = "Metadata";
    public const int Tier = 5;
    /// <summary>The configuration kind that carries custom field definitions, sections and lookup lists.</summary>
    public const string CustomFieldKind = "CustomField";

    public static IServiceCollection AddMetadataModule(this IServiceCollection services)
    {
        services.AddScoped<IConfigurationKind, CustomFieldConfigurationKind>();
        services.AddScoped<ICustomFieldCatalogue, CustomFieldCatalogue>();
        services.AddScoped<ICustomFieldStudio, CustomFieldStudio>();
        return services;
    }
}

/// <summary>How much existing data depends on the definitions. Implemented by the module that stores the values; without one, nothing is considered in use.</summary>
public interface ICustomFieldUsage
{
    Task<FieldUsage> LoadAsync(Guid organisationId, CancellationToken ct = default);
}

/// <summary>The workflow states a field's rules may name. Implemented by the lifecycle module; without it a state name is not checked.</summary>
public interface IWorkflowStateSource
{
    Task<IReadOnlySet<string>?> StatesAsync(Guid organisationId, CancellationToken ct = default);
}

public interface ICustomFieldCatalogue
{
    /// <summary>The definitions in force for the organisation (empty when none has been published). Served from the published-configuration cache.</summary>
    Task<CustomFieldSet> PublishedAsync(Guid organisationId, CancellationToken ct = default);
}

internal sealed class CustomFieldCatalogue(IConfigurationReleases releases, IPublishedConfigurationCache cache, ITenantContext tenant) : ICustomFieldCatalogue
{
    public async Task<CustomFieldSet> PublishedAsync(Guid organisationId, CancellationToken ct = default)
    {
        var release = await releases.LatestAsync(MetadataModule.CustomFieldKind, organisationId, ct);
        if (release is null) return CustomFieldSet.Empty;
        static CustomFieldSet Parse(ReleaseDto r)
        {
            var (set, messages) = FieldParser.Parse(r.ContentJson);
            if (set is null || messages.Any(m => m.Severity == ValidationSeverity.Error)) return CustomFieldSet.Empty;     // a published release was validated; this is only a guard
            return new CustomFieldSet { SchemaVersion = set.SchemaVersion, Sections = set.Sections, Lookups = set.Lookups, Fields = set.Fields, ReleaseNumber = r.Number };
        }
        // The remembered parse is an optimisation: if the cache fails, the release is parsed again and the answer is the same.
        try { return cache.GetParsed(tenant.RequireTenantId(), release, Parse); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return Parse(release); }
    }
}

internal sealed class CustomFieldConfigurationKind(IClassificationScaleProvider scales, IServiceProvider services, IAuditWriter audit) : IConfigurationKind
{
    // The configuration service builds the list of kinds, and what a kind reads (releases, usage, workflow states) leads back to it, so those are resolved on use.
    public string Kind => MetadataModule.CustomFieldKind;

    public async Task<ValidationReport> ValidateAsync(Guid organisationId, string contentJson, CancellationToken ct)
    {
        var (set, messages) = FieldParser.Parse(contentJson);
        if (set is null) return new ValidationReport(messages);
        var scale = await scales.GetAsync(ct);
        CustomFieldSet? previous = null;
        var releases = services.GetRequiredService<IConfigurationReleases>();
        if (await releases.LatestAsync(Kind, organisationId, ct) is { } latest)
        {
            var (p, pm) = FieldParser.Parse(latest.ContentJson);
            if (p is not null && pm.All(x => x.Severity != ValidationSeverity.Error)) previous = p;
        }
        IReadOnlySet<string>? known = null;
        foreach (var source in services.GetServices<IWorkflowStateSource>()) if (await source.StatesAsync(organisationId, ct) is { } s) { known = s; break; }
        var use = FieldUsage.None;
        foreach (var u in services.GetServices<ICustomFieldUsage>()) { use = await u.LoadAsync(organisationId, ct); break; }
        messages.AddRange(FieldParser.Validate(set, new FieldValidationContext(scale.Levels, RoleCodes.All.ToHashSet(StringComparer.Ordinal), known, previous, use)));
        return new ValidationReport(messages);
    }

    public Task ApplyAsync(Guid organisationId, string contentJson, string? previousContentJson, int releaseNumber, Guid releaseId, CancellationToken ct)
    {
        // The definitions are read from the published release itself; applying one is recording what changed, so the history says it in words.
        var (now, _) = FieldParser.Parse(contentJson);
        var (before, _) = previousContentJson is null ? (null, null) : FieldParser.Parse(previousContentJson);
        static string Sig(FieldDefinition f) => JsonSerializer.Serialize(f);
        var added = new List<string>(); var changed = new List<string>(); var retired = new List<string>();
        foreach (var f in now?.Fields ?? [])
        {
            var old = before?.Find(f.Key);
            if (old is null) added.Add(f.Key);
            else if (Sig(old) != Sig(f)) { changed.Add(f.Key); if (!old.IsRetired && f.IsRetired) retired.Add(f.Key); }
        }
        audit.Record(new AuditEntry("customfield.definitions.published", "CustomFieldDefinitions", releaseId.ToString(), OrganisationId: organisationId,
            Reason: $"{added.Count} added, {changed.Count} changed, {retired.Count} retired.", NewValuesJson: JsonSerializer.Serialize(new { release = releaseNumber, added, changed, retired })));
        return Task.CompletedTask;
    }
}
