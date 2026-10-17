using Ecp.Configuration;
using Ecp.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Ecp.Metadata;

/// <summary>Everything the Studio shows for one organisation: what is in force, the open draft (if any), its checks and what publishing it would change.</summary>
public sealed record StudioState(Guid OrganisationId, CustomFieldSet Published, ReleaseDto? Release, DraftDto? Draft, CustomFieldSet? DraftSet, IReadOnlyList<ValidationMessage> DraftMessages,
    IReadOnlyList<ImpactItem> Impact, FieldUsage Usage, IReadOnlyList<ReleaseDto>? Releases, string? ReleasesDenied)
{
    public bool HasDraft => Draft is not null;
    public CustomFieldSet Working => DraftSet ?? Published;
}

/// <summary>
/// The form-based editing of custom field definitions. Every edit changes the typed model of the open draft and saves it through the configuration service, so the draft, validation, publication, history,
/// rollback and audit are exactly those of every other configuration document, and the rights are <c>config.draft</c>, <c>config.publish</c>, <c>config.rollback</c> and <c>config.history.read</c>.
/// </summary>
public interface ICustomFieldStudio
{
    Task<StudioState> LoadAsync(Guid organisationId, bool includeHistory = false, CancellationToken ct = default);
    Task StartDraftAsync(Guid organisationId, CancellationToken ct = default);
    /// <summary>Adds the field, or replaces the draft's field with the same key.</summary>
    Task SaveFieldAsync(Guid organisationId, FieldDefinition field, CancellationToken ct = default);
    /// <summary>Moves a field one place earlier (-1) or later (1) within its section.</summary>
    Task MoveFieldAsync(Guid organisationId, string key, int direction, CancellationToken ct = default);
    Task SetRetiredAsync(Guid organisationId, string key, bool retired, CancellationToken ct = default);
    /// <summary>Removes a field from the draft. Only a field that has never been published can be removed; a published field is retired.</summary>
    Task RemoveDraftFieldAsync(Guid organisationId, string key, CancellationToken ct = default);
    Task SaveSectionAsync(Guid organisationId, FieldSection section, CancellationToken ct = default);
    Task SaveListAsync(Guid organisationId, LookupList list, CancellationToken ct = default);
    Task<ValidationReport> ValidateAsync(Guid organisationId, CancellationToken ct = default);
    Task<ReleaseDto> PublishAsync(Guid organisationId, string note, CancellationToken ct = default);
    Task RollbackAsync(Guid organisationId, int toRelease, string reason, CancellationToken ct = default);
    Task DiscardDraftAsync(Guid organisationId, CancellationToken ct = default);
}

internal sealed class CustomFieldStudio(IConfigurationService configuration, IServiceProvider services) : ICustomFieldStudio
{
    private const string Kind = MetadataModule.CustomFieldKind;

    private async Task<FieldUsage> UsageAsync(Guid organisationId, CancellationToken ct)
    {
        foreach (var u in services.GetServices<ICustomFieldUsage>()) return await u.LoadAsync(organisationId, ct);
        return FieldUsage.None;
    }

    private static CustomFieldSet ParseOrEmpty(string? json)
        => json is null ? CustomFieldSet.Empty : FieldParser.Parse(json).Set ?? CustomFieldSet.Empty;

    public async Task<StudioState> LoadAsync(Guid organisationId, bool includeHistory = false, CancellationToken ct = default)
    {
        var release = await configuration.LatestReleaseAsync(Kind, organisationId, ct);
        var published = ParseOrEmpty(release?.ContentJson);
        // Someone who may read the history but not edit has no draft to see.
        DraftDto? draft = null;
        try { draft = await configuration.FindOpenDraftAsync(Kind, organisationId, ct); } catch (Ecp.SharedKernel.ForbiddenException) { }
        CustomFieldSet? draftSet = null;
        var messages = new List<ValidationMessage>();
        if (draft is not null)
        {
            var (set, parse) = FieldParser.Parse(draft.ContentJson);
            draftSet = set;
            messages.AddRange(draft.Validation?.Messages ?? parse);
        }
        var usage = await UsageAsync(organisationId, ct);
        var impact = draftSet is null ? [] : FieldImpact.Analyse(release is null ? null : published, draftSet, usage);
        IReadOnlyList<ReleaseDto>? releases = null; string? denied = null;
        if (includeHistory)
        {
            try { releases = await configuration.ReleasesAsync(Kind, organisationId, ct); }
            catch (Ecp.SharedKernel.ForbiddenException) { denied = "You cannot read the version history."; }
        }
        return new StudioState(organisationId, published, release, draft, draftSet, messages, impact, usage, releases, denied);
    }

    private async Task<(DraftDto Draft, CustomFieldSet Set)> OpenAsync(Guid organisationId, CancellationToken ct)
    {
        var draft = await configuration.FindOpenDraftAsync(Kind, organisationId, ct) ?? throw new Ecp.SharedKernel.DomainException("no_draft", "Start a draft before making changes.");
        var (set, messages) = FieldParser.Parse(draft.ContentJson);
        return (draft, set ?? throw new Ecp.SharedKernel.DomainException("draft_unreadable", "The draft could not be read: " + messages.FirstOrDefault()?.Message));
    }

    private static CustomFieldSet With(CustomFieldSet s, IReadOnlyList<FieldDefinition>? fields = null, IReadOnlyList<FieldSection>? sections = null, IReadOnlyList<LookupList>? lookups = null)
        => new() { SchemaVersion = s.SchemaVersion == 0 ? 1 : s.SchemaVersion, Sections = sections ?? s.Sections, Lookups = lookups ?? s.Lookups, Fields = fields ?? s.Fields };

    private Task SaveAsync(DraftDto draft, CustomFieldSet set, CancellationToken ct) => configuration.SaveDraftAsync(draft.Id, FieldWriter.ToJson(set), ct);

    public async Task StartDraftAsync(Guid organisationId, CancellationToken ct = default)
    {
        var latest = await configuration.LatestReleaseAsync(Kind, organisationId, ct);
        await configuration.GetOrCreateDraftAsync(Kind, organisationId, latest?.ContentJson ?? FieldWriter.ToJson(CustomFieldSet.Empty), ct);
    }

    public async Task SaveFieldAsync(Guid organisationId, FieldDefinition field, CancellationToken ct = default)
    {
        var (draft, set) = await OpenAsync(organisationId, ct);
        var fields = set.Fields.ToList();
        var i = fields.FindIndex(f => string.Equals(f.Key, field.Key, StringComparison.OrdinalIgnoreCase));
        if (i >= 0) fields[i] = field with { Key = fields[i].Key };
        else fields.Add(field with { Order = field.Order == 0 ? (fields.Where(f => f.Section == field.Section).Select(f => f.Order).DefaultIfEmpty(0).Max() + 10) : field.Order });
        await SaveAsync(draft, With(set, fields), ct);
    }

    public async Task MoveFieldAsync(Guid organisationId, string key, int direction, CancellationToken ct = default)
    {
        var (draft, set) = await OpenAsync(organisationId, ct);
        var target = set.Find(key) ?? throw new Ecp.SharedKernel.ResourceNotFoundException();
        // Orders are renumbered 10, 20, 30 within the section first, so moving never leaves two fields tied.
        var inSection = set.Fields.Where(f => f.Section == target.Section).OrderBy(f => f.Order).ThenBy(f => f.Label, StringComparer.Ordinal).ToList();
        var at = inSection.FindIndex(f => string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase));
        var to = at + Math.Sign(direction);
        if (to < 0 || to >= inSection.Count) return;
        (inSection[at], inSection[to]) = (inSection[to], inSection[at]);
        var renumbered = inSection.Select((f, n) => f with { Order = (n + 1) * 10 }).ToDictionary(f => f.Key, StringComparer.OrdinalIgnoreCase);
        await SaveAsync(draft, With(set, set.Fields.Select(f => renumbered.GetValueOrDefault(f.Key) ?? f).ToList()), ct);
    }

    public async Task SetRetiredAsync(Guid organisationId, string key, bool retired, CancellationToken ct = default)
    {
        var (draft, set) = await OpenAsync(organisationId, ct);
        var f = set.Find(key) ?? throw new Ecp.SharedKernel.ResourceNotFoundException();
        await SaveAsync(draft, With(set, set.Fields.Select(x => ReferenceEquals(x, f) ? f with { Status = retired ? FieldStatuses.Retired : FieldStatuses.Active } : x).ToList()), ct);
    }

    public async Task RemoveDraftFieldAsync(Guid organisationId, string key, CancellationToken ct = default)
    {
        var (draft, set) = await OpenAsync(organisationId, ct);
        var latest = await configuration.LatestReleaseAsync(Kind, organisationId, ct);
        if (latest is not null && ParseOrEmpty(latest.ContentJson).Find(key) is not null)
            throw new Ecp.SharedKernel.DomainException("field_published", "A published field cannot be deleted because [[agreement|plural|lower]] may hold values for it. Retire it instead; every stored value is kept.");
        await SaveAsync(draft, With(set, set.Fields.Where(f => !string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase)).ToList()), ct);
    }

    public async Task SaveSectionAsync(Guid organisationId, FieldSection section, CancellationToken ct = default)
    {
        var (draft, set) = await OpenAsync(organisationId, ct);
        var sections = set.Sections.ToList();
        var i = sections.FindIndex(s => s.Key == section.Key);
        if (i >= 0) sections[i] = section; else sections.Add(section);
        await SaveAsync(draft, With(set, sections: sections), ct);
    }

    public async Task SaveListAsync(Guid organisationId, LookupList list, CancellationToken ct = default)
    {
        var (draft, set) = await OpenAsync(organisationId, ct);
        var lists = set.Lookups.ToList();
        var i = lists.FindIndex(l => string.Equals(l.Name, list.Name, StringComparison.OrdinalIgnoreCase));
        if (i >= 0) lists[i] = list with { Name = lists[i].Name }; else lists.Add(list);
        await SaveAsync(draft, With(set, lookups: lists), ct);
    }

    public async Task<ValidationReport> ValidateAsync(Guid organisationId, CancellationToken ct = default)
    {
        var (draft, _) = await OpenAsync(organisationId, ct);
        var result = await configuration.ValidateAsync(draft.Id, ct);
        return result.Validation ?? ValidationReport.Valid();
    }

    public async Task<ReleaseDto> PublishAsync(Guid organisationId, string note, CancellationToken ct = default)
    {
        var (draft, _) = await OpenAsync(organisationId, ct);
        await configuration.ValidateAsync(draft.Id, ct);
        return await configuration.PublishAsync(draft.Id, note, ct);
    }

    public Task RollbackAsync(Guid organisationId, int toRelease, string reason, CancellationToken ct = default) => configuration.RollbackAsync(Kind, organisationId, toRelease, reason, ct);

    public async Task DiscardDraftAsync(Guid organisationId, CancellationToken ct = default)
    {
        var (draft, _) = await OpenAsync(organisationId, ct);
        await configuration.DiscardDraftAsync(draft.Id, ct);
    }
}
