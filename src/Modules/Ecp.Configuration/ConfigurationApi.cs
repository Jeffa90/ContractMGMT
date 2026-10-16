using System.Text.Json;
using Ecp.Audit;
using Ecp.Configuration.Domain;
using Ecp.Configuration.Persistence;
using Ecp.Identity;
using Ecp.Identity.Access;
using Ecp.Persistence;
using Ecp.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ecp.Configuration;

public enum ValidationSeverity { Error, Warning }
public sealed record ValidationMessage(ValidationSeverity Severity, string Path, string Code, string Message);

public sealed record ValidationReport(IReadOnlyList<ValidationMessage> Messages)
{
    public bool IsValid => Messages.All(m => m.Severity != ValidationSeverity.Error);
    public static ValidationReport Valid() => new([]);
}

public sealed record DraftDto(Guid Id, Guid OrganisationId, string Kind, string ContentJson, DraftStatus Status, bool IsPublishable, int? BasedOnReleaseNumber, string LastEditedBy,
    DateTime UpdatedUtc, ValidationReport? Validation);

public sealed record ReleaseDto(Guid Id, Guid OrganisationId, string Kind, int Number, string ContentJson, string ContentHash, int? RollbackOfNumber, string Note, string PublishedBy, DateTime PublishedUtc);

/// <summary>
/// A kind of configuration document (for example organisation onboarding). The Configuration module owns the lifecycle (draft, validate,
/// publish, version, rollback, audit); a kind supplies its own validation and how a published release takes effect. Kinds live in the
/// modules that own the data, so the lifecycle contains no business or pack specifics.
/// </summary>
public interface IConfigurationKind
{
    string Kind { get; }
    Task<ValidationReport> ValidateAsync(Guid organisationId, string contentJson, CancellationToken ct);
    /// <summary>
    /// Makes a release effective. Called inside the publishing unit of work, so failure rolls the release back.
    /// <paramref name="previousContentJson"/> is the content of the release that was current before this one (null for the first release), so a kind can end what the previous release introduced.
    /// </summary>
    Task ApplyAsync(Guid organisationId, string contentJson, string? previousContentJson, int releaseNumber, Guid releaseId, CancellationToken ct);
}

public interface IConfigurationService
{
    Task<DraftDto> GetOrCreateDraftAsync(string kind, Guid organisationId, string? initialJson = null, CancellationToken ct = default);
    Task<DraftDto?> FindOpenDraftAsync(string kind, Guid organisationId, CancellationToken ct = default);
    Task<DraftDto> SaveDraftAsync(Guid draftId, string contentJson, CancellationToken ct = default);
    Task<DraftDto> ValidateAsync(Guid draftId, CancellationToken ct = default);
    Task<ReleaseDto> PublishAsync(Guid draftId, string note, CancellationToken ct = default);
    /// <summary>Publishes an earlier release's content as a new release (history is never rewritten).</summary>
    Task<ReleaseDto> RollbackAsync(string kind, Guid organisationId, int toReleaseNumber, string reason, CancellationToken ct = default);
    Task<ReleaseDto?> LatestReleaseAsync(string kind, Guid organisationId, CancellationToken ct = default);
    /// <summary>The most recent release of any kind for an organisation (organisation-level and tenant-level), for recording which configuration a long-running operation relied on.</summary>
    Task<ReleaseDto?> LatestReleaseOfAnyKindAsync(Guid organisationId, CancellationToken ct = default);
    Task<IReadOnlyList<ReleaseDto>> ReleasesAsync(string kind, Guid organisationId, CancellationToken ct = default);
    Task DiscardDraftAsync(Guid draftId, CancellationToken ct = default);
}

/// <summary>Trusted lifecycle for installation, setup and tests: the same validation and versioning, with no signed-in user.</summary>
public interface IConfigurationBootstrap
{
    Task<ReleaseDto> DraftValidatePublishAsync(string kind, Guid organisationId, string contentJson, string publishedBy, string note, CancellationToken ct = default);
}

public sealed class ConfigurationOptions
{
    /// <summary>Second-person publish: the publisher must differ from the last editor of the draft (tenant setting; on by default in Production).</summary>
    public bool RequireSecondPersonPublish { get; set; }
}

internal sealed class ConfigurationService(
    ConfigurationDbContext db, IUnitOfWork uow, IAuditWriter audit, ITenantContext tenant, IClock clock, IAccessService access, CurrentUser currentUser,
    IEnumerable<IConfigurationKind> kinds, ConfigurationOptions options, ActorContext actor) : IConfigurationService, IConfigurationBootstrap
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private IConfigurationKind KindOf(string kind) => kinds.FirstOrDefault(k => k.Kind == kind) ?? throw new DomainException("unknown_configuration_kind", $"Configuration kind '{kind}' is not recognised.");

    private ResourceDescriptor Describe(Guid organisationId, string kind)
        => organisationId == Guid.Empty
            ? new ResourceDescriptor("Configuration", tenant.RequireTenantId(), IsTenantLevelDefinition: true, Id: kind, Dimensions: ScopeDimensions.None)
            : new ResourceDescriptor("Configuration", tenant.RequireTenantId(), organisationId, Id: kind, Dimensions: ScopeDimensions.OrganisationOnly);

    private async Task<string> EditorLabelAsync(CancellationToken ct)
        => currentUser.UserId?.ToString() ?? actor.ActorId ?? "system";

    public async Task<DraftDto?> FindOpenDraftAsync(string kind, Guid organisationId, CancellationToken ct = default)
    {
        await access.RequireAsync("config.draft", Describe(organisationId, kind), ct);
        var d = await db.Drafts.AsNoTracking().Where(x => x.OrganisationId == organisationId && x.Kind == kind && (x.Status == DraftStatus.Draft || x.Status == DraftStatus.Validated))
            .OrderByDescending(x => x.UpdatedUtc).FirstOrDefaultAsync(ct);
        return d is null ? null : ToDto(d);
    }

    public async Task<DraftDto> GetOrCreateDraftAsync(string kind, Guid organisationId, string? initialJson = null, CancellationToken ct = default)
    {
        KindOf(kind);
        await access.RequireAsync("config.draft", Describe(organisationId, kind), ct);
        var existing = await db.Drafts.Where(x => x.OrganisationId == organisationId && x.Kind == kind && (x.Status == DraftStatus.Draft || x.Status == DraftStatus.Validated))
            .OrderByDescending(x => x.UpdatedUtc).FirstOrDefaultAsync(ct);
        if (existing is not null) return ToDto(existing);
        return await CreateDraftCoreAsync(kind, organisationId, initialJson, await EditorLabelAsync(ct), ct);
    }

    private async Task<DraftDto> CreateDraftCoreAsync(string kind, Guid organisationId, string? initialJson, string editor, CancellationToken ct)
    {
        var latest = await db.Releases.AsNoTracking().Where(r => r.OrganisationId == organisationId && r.Kind == kind).OrderByDescending(r => r.Number).FirstOrDefaultAsync(ct);
        var draft = ConfigurationDraft.Create(tenant.RequireTenantId(), organisationId, kind, initialJson ?? latest?.ContentJson ?? "{}", latest?.Number, editor, clock.UtcNow);
        await uow.ExecuteAsync(async c =>
        {
            db.Drafts.Add(draft);
            await db.SaveChangesAsync(c);
            audit.Record(new AuditEntry("config.draft.created", kind, draft.Id.ToString(), OrganisationId: organisationId == Guid.Empty ? null : organisationId));
        }, ct);
        return ToDto(draft);
    }

    public async Task<DraftDto> SaveDraftAsync(Guid draftId, string contentJson, CancellationToken ct = default)
    {
        var draft = await db.Drafts.FirstOrDefaultAsync(d => d.Id == draftId, ct) ?? throw new ResourceNotFoundException();
        await access.RequireAsync("config.draft", Describe(draft.OrganisationId, draft.Kind), ct);
        EnsureJsonObject(contentJson);
        var editor = await EditorLabelAsync(ct);
        await uow.ExecuteAsync(async c =>
        {
            draft.Edit(contentJson, editor, clock.UtcNow);
            try { await db.SaveChangesAsync(c); }
            catch (DbUpdateConcurrencyException) { throw new DomainException("concurrency_conflict", "The draft was changed by someone else. Reload and try again."); }
            audit.Record(new AuditEntry("config.draft.saved", draft.Kind, draft.Id.ToString(), OrganisationId: draft.OrganisationId == Guid.Empty ? null : draft.OrganisationId,
                NewValuesJson: JsonSerializer.Serialize(new { hash = draft.ContentHash })));
        }, ct);
        return ToDto(draft);
    }

    public async Task<DraftDto> ValidateAsync(Guid draftId, CancellationToken ct = default)
    {
        var draft = await db.Drafts.FirstOrDefaultAsync(d => d.Id == draftId, ct) ?? throw new ResourceNotFoundException();
        await access.RequireAsync("config.draft", Describe(draft.OrganisationId, draft.Kind), ct);
        return await ValidateCoreAsync(draft, ct);
    }

    private async Task<DraftDto> ValidateCoreAsync(ConfigurationDraft draft, CancellationToken ct)
    {
        ValidationReport report;
        try { EnsureJsonObject(draft.ContentJson); report = await KindOf(draft.Kind).ValidateAsync(draft.OrganisationId, draft.ContentJson, ct); }
        catch (DomainException ex) { report = new ValidationReport([new ValidationMessage(ValidationSeverity.Error, "$", ex.Code, ex.Message)]); }
        await uow.ExecuteAsync(async c =>
        {
            draft.RecordValidation(report.IsValid, JsonSerializer.Serialize(report, Json), clock.UtcNow);
            await db.SaveChangesAsync(c);
            audit.Record(new AuditEntry("config.draft.validated", draft.Kind, draft.Id.ToString(), OrganisationId: draft.OrganisationId == Guid.Empty ? null : draft.OrganisationId,
                NewValuesJson: JsonSerializer.Serialize(new { valid = report.IsValid, errors = report.Messages.Count(m => m.Severity == ValidationSeverity.Error) })));
        }, ct);
        return ToDto(draft);
    }

    public async Task<ReleaseDto> PublishAsync(Guid draftId, string note, CancellationToken ct = default)
    {
        var draft = await db.Drafts.FirstOrDefaultAsync(d => d.Id == draftId, ct) ?? throw new ResourceNotFoundException();
        await access.RequireAsync("config.publish", Describe(draft.OrganisationId, draft.Kind), ct);
        var publisher = await EditorLabelAsync(ct);
        if (options.RequireSecondPersonPublish && string.Equals(draft.LastEditedBy, publisher, StringComparison.Ordinal))
            throw new DomainException("second_person_required", "A second person must publish this configuration; the last editor cannot publish it.");
        return await PublishCoreAsync(draft, null, note, publisher, ct);
    }

    private async Task<ReleaseDto> PublishCoreAsync(ConfigurationDraft draft, int? rollbackOf, string note, string publisher, CancellationToken ct)
    {
        if (!draft.IsPublishable) throw new DomainException("not_validated", "The draft must be validated, and unchanged since validation, before it is published.");
        var kind = KindOf(draft.Kind);
        return await uow.ExecuteAsync(async c =>
        {
            var previous = await db.Releases.AsNoTracking().Where(r => r.OrganisationId == draft.OrganisationId && r.Kind == draft.Kind).OrderByDescending(r => r.Number)
                .Select(r => new { r.Number, r.ContentJson }).FirstOrDefaultAsync(c);
            var number = (previous?.Number ?? 0) + 1;
            var release = ConfigurationRelease.Create(draft.TenantId, draft.OrganisationId, draft.Kind, number, draft.ContentJson, draft.Id, rollbackOf, note, publisher, clock.UtcNow);
            db.Releases.Add(release);
            draft.MarkPublished(clock.UtcNow);
            try { await db.SaveChangesAsync(c); }
            catch (DbUpdateException ex) when (DbExceptions.IsUniqueViolation(ex)) { throw new DomainException("concurrency_conflict", "Another release was published at the same time. Reload and try again."); }
            await kind.ApplyAsync(draft.OrganisationId, draft.ContentJson, previous?.ContentJson, number, release.Id, c);
            audit.Record(new AuditEntry(rollbackOf is null ? "config.published" : "config.rolled_back", draft.Kind, release.Id.ToString(),
                OrganisationId: draft.OrganisationId == Guid.Empty ? null : draft.OrganisationId, Reason: note, ConfigReleaseId: release.Id,
                NewValuesJson: JsonSerializer.Serialize(new { number, hash = release.ContentHash, rollbackOf })));
            return ToDto(release);
        }, ct);
    }

    public async Task<ReleaseDto> RollbackAsync(string kind, Guid organisationId, int toReleaseNumber, string reason, CancellationToken ct = default)
    {
        await access.RequireAsync("config.rollback", Describe(organisationId, kind), ct);
        var why = Guard.NotBlank(reason, "Reason", 1000);
        var target = await db.Releases.AsNoTracking().FirstOrDefaultAsync(r => r.OrganisationId == organisationId && r.Kind == kind && r.Number == toReleaseNumber, ct)
            ?? throw new DomainException("release_not_found", "That release was not found.");
        var editor = await EditorLabelAsync(ct);
        var created = await CreateDraftCoreAsync(kind, organisationId, target.ContentJson, editor, ct);
        var draft = await db.Drafts.FirstAsync(d => d.Id == created.Id, ct);
        await ValidateCoreAsync(draft, ct); // the earlier content is re-validated against today's rules before it takes effect again
        return await PublishCoreAsync(draft, toReleaseNumber, $"Rollback to release {toReleaseNumber}: {why}", editor, ct);
    }

    public async Task<ReleaseDto> DraftValidatePublishAsync(string kind, Guid organisationId, string contentJson, string publishedBy, string note, CancellationToken ct = default)
    {
        KindOf(kind); EnsureJsonObject(contentJson);
        var label = Guard.NotBlank(publishedBy, "Published by", 200);
        var created = await CreateDraftCoreAsync(kind, organisationId, contentJson, label, ct);
        var draft = await db.Drafts.FirstAsync(d => d.Id == created.Id, ct);
        var validated = await ValidateCoreAsync(draft, ct);
        if (!validated.IsPublishable)
            throw new DomainException("validation_failed", "The configuration did not validate: " + string.Join("; ", validated.Validation!.Messages.Where(m => m.Severity == ValidationSeverity.Error).Select(m => m.Message)));
        return await PublishCoreAsync(draft, null, note, label, ct);
    }

    public async Task<ReleaseDto?> LatestReleaseAsync(string kind, Guid organisationId, CancellationToken ct = default)
    {
        var r = await db.Releases.AsNoTracking().Where(x => x.OrganisationId == organisationId && x.Kind == kind).OrderByDescending(x => x.Number).FirstOrDefaultAsync(ct);
        return r is null ? null : ToDto(r);
    }

    public async Task<ReleaseDto?> LatestReleaseOfAnyKindAsync(Guid organisationId, CancellationToken ct = default)
    {
        var r = await db.Releases.AsNoTracking().Where(x => x.OrganisationId == organisationId || x.OrganisationId == Guid.Empty)
            .OrderByDescending(x => x.PublishedUtc).ThenByDescending(x => x.Number).FirstOrDefaultAsync(ct);
        return r is null ? null : ToDto(r);
    }

    public async Task<IReadOnlyList<ReleaseDto>> ReleasesAsync(string kind, Guid organisationId, CancellationToken ct = default)
    {
        await access.RequireAsync("config.history.read", Describe(organisationId, kind), ct);
        return (await db.Releases.AsNoTracking().Where(x => x.OrganisationId == organisationId && x.Kind == kind).OrderByDescending(x => x.Number).ToListAsync(ct)).Select(ToDto).ToList();
    }

    public async Task DiscardDraftAsync(Guid draftId, CancellationToken ct = default)
    {
        var draft = await db.Drafts.FirstOrDefaultAsync(d => d.Id == draftId, ct) ?? throw new ResourceNotFoundException();
        await access.RequireAsync("config.draft", Describe(draft.OrganisationId, draft.Kind), ct);
        await uow.ExecuteAsync(async c =>
        {
            draft.Discard(clock.UtcNow);
            await db.SaveChangesAsync(c);
            audit.Record(new AuditEntry("config.draft.discarded", draft.Kind, draft.Id.ToString(), OrganisationId: draft.OrganisationId == Guid.Empty ? null : draft.OrganisationId));
        }, ct);
    }

    private static void EnsureJsonObject(string json)
    {
        try { using var doc = JsonDocument.Parse(json); if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new DomainException("invalid_json", "Configuration must be a JSON object."); }
        catch (JsonException) { throw new DomainException("invalid_json", "Configuration is not valid JSON."); }
    }

    private static DraftDto ToDto(ConfigurationDraft d) => new(d.Id, d.OrganisationId, d.Kind, d.ContentJson, d.Status, d.IsPublishable, d.BasedOnReleaseNumber, d.LastEditedBy, d.UpdatedUtc,
        d.ValidationJson is null ? null : JsonSerializer.Deserialize<ValidationReport>(d.ValidationJson, Json));

    private static ReleaseDto ToDto(ConfigurationRelease r) => new(r.Id, r.OrganisationId, r.Kind, r.Number, r.ContentJson, r.ContentHash, r.RollbackOfNumber, r.Note, r.PublishedBy, r.PublishedUtc);
}

/// <summary>Tenant classification policy kind: an ordered list of levels and a default. Applying it needs no further action; the scale provider reads the latest release.</summary>
internal sealed class ClassificationPolicyKind : IConfigurationKind
{
    public const string KindName = "ClassificationPolicy";
    public string Kind => KindName;

    public Task<ValidationReport> ValidateAsync(Guid organisationId, string contentJson, CancellationToken ct)
    {
        var messages = new List<ValidationMessage>();
        try
        {
            var policy = ClassificationPolicyDocument.Parse(contentJson);
            _ = new ClassificationScale(policy.Levels, policy.DefaultLevel);
            if (policy.Levels.Count < 2) messages.Add(new(ValidationSeverity.Warning, "$.levels", "few_levels", "A single classification level cannot restrict anything."));
        }
        catch (DomainException ex) { messages.Add(new(ValidationSeverity.Error, "$", ex.Code, ex.Message)); }
        return Task.FromResult(new ValidationReport(messages));
    }

    public Task ApplyAsync(Guid organisationId, string contentJson, string? previousContentJson, int releaseNumber, Guid releaseId, CancellationToken ct) => Task.CompletedTask;
}

public sealed record ClassificationPolicyDocument(IReadOnlyList<string> Levels, string DefaultLevel)
{
    public static ClassificationPolicyDocument Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var levels = root.TryGetProperty("levels", out var l) && l.ValueKind == JsonValueKind.Array ? l.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : [];
            var def = root.TryGetProperty("defaultLevel", out var d) ? d.GetString() ?? "" : "";
            return new ClassificationPolicyDocument(levels, def);
        }
        catch (JsonException) { throw new DomainException("invalid_json", "The classification policy is not valid JSON."); }
    }

    public string ToJson() => JsonSerializer.Serialize(new { levels = Levels, defaultLevel = DefaultLevel });
}

/// <summary>Reads the tenant's published classification policy; falls back to the placeholder scale until one is published.</summary>
internal sealed class ConfiguredClassificationScaleProvider(ConfigurationDbContext db) : IClassificationScaleProvider
{
    private ClassificationScale? _cached;

    public async Task<ClassificationScale> GetAsync(CancellationToken ct = default)
    {
        if (_cached is not null) return _cached;
        var json = await db.Releases.AsNoTracking().Where(r => r.OrganisationId == Guid.Empty && r.Kind == ClassificationPolicyKind.KindName)
            .OrderByDescending(r => r.Number).Select(r => r.ContentJson).FirstOrDefaultAsync(ct);
        if (json is null) return _cached = ClassificationScale.Default;
        var policy = ClassificationPolicyDocument.Parse(json);
        return _cached = new ClassificationScale(policy.Levels, policy.DefaultLevel);
    }
}

public static class ConfigurationModule
{
    public const string Name = "Configuration";
    public const string Schema = "configuration";
    public const int Tier = 5;

    public static IServiceCollection AddConfigurationModule(this IServiceCollection services, Action<ConfigurationOptions>? configure = null)
    {
        var options = new ConfigurationOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);
        services.AddModuleDbContext<ConfigurationDbContext>(Name, Tier, Schema);
        services.AddScoped<ConfigurationService>();
        services.AddScoped<IConfigurationService>(sp => sp.GetRequiredService<ConfigurationService>());
        services.AddScoped<IConfigurationBootstrap>(sp => sp.GetRequiredService<ConfigurationService>());
        services.AddScoped<IConfigurationKind, ClassificationPolicyKind>();
        services.AddSingleton<ISolutionPackCatalogue, EmbeddedPackCatalogue>();
        services.AddScoped<IConfigurationKind, SolutionPackKind>();
        services.AddScoped<IConfigurationKind, BrandingKind>();
        services.AddScoped<IBrandingProvider, ConfiguredBrandingProvider>();
        // Replace the default scale with the configured one (registered after the Identity module).
        services.AddScoped<IClassificationScaleProvider, ConfiguredClassificationScaleProvider>();
        return services;
    }
}
