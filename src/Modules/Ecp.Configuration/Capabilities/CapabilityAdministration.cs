using Ecp.Audit;
using Ecp.Identity;
using Ecp.Identity.Access;
using Ecp.SharedKernel;
using Ecp.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ecp.Configuration;

/// <summary>One capability as the overview shows it: what is in force, where that comes from, the pack's default beneath it, and the organisation's own published and drafted choice.</summary>
public sealed record CapabilityRow(string Key, string Label, string Description, string TurnsOffEffect, IReadOnlyList<string> Requires, bool Enabled, CapabilityLayer Layer, bool? PackDefault, string? PackNote,
    bool? Published, bool? Draft, int InFlight);

public sealed record CapabilityOverview(Guid OrganisationId, string OrganisationName, string? PackKey, string? PackVersion, bool PackHasCapabilities, int? ReleaseNumber, DraftDto? Draft,
    IReadOnlyList<CapabilityRow> Rows, IReadOnlyList<CapabilityFinding> Findings, bool CanDraft, bool CanPublish, bool CanRollback, bool CanReadHistory, bool CanReadAudit);

/// <summary>What the edit form shows for one capability: the organisation's own choice (if any) and what it inherits.</summary>
public sealed record CapabilityEditRow(string Key, string Label, string Description, IReadOnlyList<string> Requires, bool InheritedEnabled, CapabilityLayer InheritedFrom, bool? Choice, string? Note);
public sealed record CapabilityEditModel(Guid OrganisationId, string OrganisationName, DraftDto? Draft, int? ReleaseNumber, IReadOnlyList<CapabilityEditRow> Rows);
/// <summary><see cref="Choice"/> is <c>inherit</c>, <c>on</c> or <c>off</c>.</summary>
public sealed record CapabilityInput(string Key, string Choice, string? Note);

public sealed record CapabilityChange(string Key, string Label, bool Now, bool After, string Effect);
public sealed record CapabilityPreviewResult(Guid OrganisationId, bool HasDraft, IReadOnlyList<CapabilityChange> Changes, IReadOnlyList<CapabilityFinding> Findings);
public sealed record CapabilityReleaseView(ReleaseDto Release, IReadOnlyList<string> Changes);
/// <summary>A pack's capability defaults, to show before the pack is chosen: which optional steps it would switch off.</summary>
public sealed record PackCapabilityExample(string PackKey, string PackVersion, IReadOnlyList<(string Key, string Label, bool Enabled, string? Note)> Capabilities)
{
    public IReadOnlyList<(string Key, string Label, bool Enabled, string? Note)> Off => Capabilities.Where(c => !c.Enabled).ToList();
}
public sealed record CapabilityAuditItem(DateTime OccurredUtc, string Action, string? Actor, string Outcome, string Subject, string? Reason, string? Details);

/// <summary>
/// The form-based administration of capabilities, as services. Every call checks the capability permissions for the organisation; drafting, validating, publishing and rolling back go through the ordinary configuration
/// lifecycle (validation includes the compatibility checks, second-person publication and audit apply). A capability switches an optional step of the process on or off; it never changes who may do anything.
/// </summary>
public interface ICapabilityAdministration
{
    Task<CapabilityOverview> OverviewAsync(Guid organisationId, CancellationToken ct = default);
    Task<CapabilityEditModel> EditModelAsync(Guid organisationId, CancellationToken ct = default);
    /// <summary>Replaces the organisation's choices in its open draft (creating the draft from the published release if there is none). Nothing is published.</summary>
    Task<DraftDto> SaveAsync(Guid organisationId, IReadOnlyList<CapabilityInput> inputs, CancellationToken ct = default);
    Task<CapabilityPreviewResult> PreviewAsync(Guid organisationId, CancellationToken ct = default);
    Task<IReadOnlyList<CapabilityReleaseView>> HistoryAsync(Guid organisationId, CancellationToken ct = default);
    Task<PagedResult<CapabilityAuditItem>> AuditAsync(Guid organisationId, int page = 1, CancellationToken ct = default);
    /// <summary>What a pack's capability defaults are (every capability is listed; one the pack does not mention is on). Null when the pack gives none.</summary>
    Task<PackCapabilityExample?> ExampleOfPackAsync(string packKey, CancellationToken ct = default);
}

internal sealed class CapabilityAdministration(IConfigurationService configuration, ICapabilityResolver resolver, IOrganisationStructureService organisations, IAccessService access, ITenantContext tenant,
    IAuditEventSource auditEvents, IServiceProvider services, IPackLoader packs) : ICapabilityAdministration
{
    public async Task<PackCapabilityExample?> ExampleOfPackAsync(string packKey, CancellationToken ct = default)
    {
        var entry = await packs.FindAsync(packKey, null, usableOnly: true, ct);
        var json = entry is null ? null : await packs.GetDocumentAsync(entry.PackKey, entry.Version, Kind, ct);
        var doc = SafeParse(json);
        if (entry is null || doc is null) return null;
        var set = doc.AsSet();
        return new PackCapabilityExample(entry.PackKey, entry.Version, CapabilityCatalogue.All.Select(d => (d.Key, d.Label, set.IsEnabled(d.Key), doc.Capabilities.FirstOrDefault(c => c.Key == d.Key)?.Note)).ToList());
    }

    private const string Kind = CapabilityKinds.Name;
    private const int AuditPageSize = 50;

    private ResourceDescriptor Resource(Guid organisationId) => new("Configuration", tenant.RequireTenantId(), organisationId, Id: Kind, Dimensions: ScopeDimensions.OrganisationOnly);
    private async Task<OrganisationSummary> OrganisationAsync(Guid organisationId, CancellationToken ct) => await organisations.GetOrganisationAsync(organisationId, ct) ?? throw new ResourceNotFoundException();
    private async Task<bool> HoldsAsync(string permission, Guid organisationId, CancellationToken ct) => (await access.CheckSilentAsync(permission, Resource(organisationId), ct)).Allowed;
    private static CapabilityDocument? SafeParse(string? json) { if (json is null) return null; try { return CapabilityDocument.Parse(json); } catch (DomainException) { return null; } }
    private async Task<DraftDto?> OpenDraftIfAllowedAsync(Guid organisationId, CancellationToken ct) => await HoldsAsync("capabilities.draft", organisationId, ct) ? await configuration.FindOpenDraftAsync(Kind, organisationId, ct) : null;

    private async Task<IReadOnlyList<CapabilityFinding>> FindingsAsync(Guid organisationId, CapabilitySet proposed, CapabilitySet current, CancellationToken ct)
    {
        var found = proposed.Unmet().Select(u => new CapabilityFinding(ValidationSeverity.Error, u.Capability, "capability_requires", $"'{u.Capability}' needs '{u.Requires}', which is off.")).ToList();
        foreach (var check in services.GetServices<ICapabilityCompatibility>()) found.AddRange(await check.CheckAsync(organisationId, proposed, current, ct));
        return found;
    }

    public async Task<CapabilityOverview> OverviewAsync(Guid organisationId, CancellationToken ct = default)
    {
        await access.RequireAsync("capabilities.read", Resource(organisationId), ct);
        var org = await OrganisationAsync(organisationId, ct);
        var effective = await resolver.GetAsync(organisationId, ct);
        var pack = await resolver.PackLayerAsync(organisationId, ct);
        var packSet = pack?.Document.Capabilities.ToDictionary(c => c.Key, StringComparer.Ordinal);
        var release = await configuration.LatestReleaseAsync(Kind, organisationId, ct);
        var draft = await OpenDraftIfAllowedAsync(organisationId, ct);
        var published = SafeParse(release?.ContentJson); var drafted = SafeParse(draft?.ContentJson);
        var findings = await FindingsAsync(organisationId, effective, effective, ct);
        var rows = CapabilityCatalogue.All.Select(d =>
        {
            var found = effective.Find(d.Key);
            var inFlight = findings.Where(f => f.Capability == d.Key && f.Code == "capability_off_work_in_flight").Select(f => 1).Count();
            return new CapabilityRow(d.Key, d.Label, d.Description, d.TurnsOffEffect, d.Requires, found?.Enabled ?? true, found?.Layer ?? CapabilityLayer.Platform,
                packSet is not null && packSet.TryGetValue(d.Key, out var p) ? p.Enabled : null, packSet is not null && packSet.TryGetValue(d.Key, out var pn) ? pn.Note : null,
                published?.Capabilities.FirstOrDefault(c => c.Key == d.Key)?.Enabled, drafted?.Capabilities.FirstOrDefault(c => c.Key == d.Key)?.Enabled, inFlight);
        }).ToList();
        return new CapabilityOverview(organisationId, org.Name, pack?.PackKey, pack?.PackVersion, pack is not null, release?.Number, draft, rows, findings,
            await HoldsAsync("capabilities.draft", organisationId, ct), await HoldsAsync("capabilities.publish", organisationId, ct), await HoldsAsync("capabilities.rollback", organisationId, ct),
            await HoldsAsync("capabilities.read", organisationId, ct), await HoldsAsync("capabilities.audit.read", organisationId, ct));
    }

    public async Task<CapabilityEditModel> EditModelAsync(Guid organisationId, CancellationToken ct = default)
    {
        await access.RequireAsync("capabilities.draft", Resource(organisationId), ct);
        var org = await OrganisationAsync(organisationId, ct);
        var draft = await configuration.FindOpenDraftAsync(Kind, organisationId, ct);
        var release = await configuration.LatestReleaseAsync(Kind, organisationId, ct);
        var doc = SafeParse(draft?.ContentJson ?? release?.ContentJson);
        var pack = await resolver.PackLayerAsync(organisationId, ct);
        var inherited = CapabilitySet.Build(null, pack?.Document.Capabilities, "inherited");
        var rows = CapabilityCatalogue.All.Select(d =>
        {
            var mine = doc?.Capabilities.FirstOrDefault(c => c.Key == d.Key);
            return new CapabilityEditRow(d.Key, d.Label, d.Description, d.Requires, inherited.IsEnabled(d.Key), inherited.LayerOf(d.Key), mine?.Enabled, mine?.Note);
        }).ToList();
        return new CapabilityEditModel(organisationId, org.Name, draft, release?.Number, rows);
    }

    public async Task<DraftDto> SaveAsync(Guid organisationId, IReadOnlyList<CapabilityInput> inputs, CancellationToken ct = default)
    {
        await access.RequireAsync("capabilities.draft", Resource(organisationId), ct);
        _ = await OrganisationAsync(organisationId, ct);
        var open = await configuration.FindOpenDraftAsync(Kind, organisationId, ct);
        var existing = SafeParse(open?.ContentJson);
        // Keep statements the form does not know (a newer platform's capability) so saving never silently drops them.
        var kept = (existing?.Capabilities ?? []).Where(c => !CapabilityCatalogue.IsKnown(c.Key)).ToList();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var entries = new List<CapabilityEntry>(kept);
        foreach (var input in inputs)
        {
            if (input.Choice is not ("on" or "off") || !seen.Add(input.Key)) continue;
            entries.Add(new CapabilityEntry(input.Key.Trim(), input.Choice == "on", string.IsNullOrWhiteSpace(input.Note) ? null : input.Note.Trim()));
        }
        var json = new CapabilityDocument(CapabilityDocument.Overrides, entries).ToJson();
        var draft = open ?? await configuration.GetOrCreateDraftAsync(Kind, organisationId, json, ct);
        return await configuration.SaveDraftAsync(draft.Id, json, ct);
    }

    public async Task<CapabilityPreviewResult> PreviewAsync(Guid organisationId, CancellationToken ct = default)
    {
        await access.RequireAsync("capabilities.read", Resource(organisationId), ct);
        var now = await resolver.GetAsync(organisationId, ct);
        var draft = await OpenDraftIfAllowedAsync(organisationId, ct);
        var doc = SafeParse(draft?.ContentJson);
        var after = doc is null ? now : await resolver.PreviewAsync(organisationId, doc, ct);
        var changes = CapabilityCatalogue.All.Select(d => new CapabilityChange(d.Key, d.Label, now.IsEnabled(d.Key), after.IsEnabled(d.Key),
            now.IsEnabled(d.Key) == after.IsEnabled(d.Key) ? "No change." : after.IsEnabled(d.Key) ? $"{d.Label} is available again." : d.TurnsOffEffect)).ToList();
        return new CapabilityPreviewResult(organisationId, draft is not null, changes, doc is null ? [] : await FindingsAsync(organisationId, after, now, ct));
    }

    public async Task<IReadOnlyList<CapabilityReleaseView>> HistoryAsync(Guid organisationId, CancellationToken ct = default)
    {
        var releases = (await configuration.ReleasesAsync(Kind, organisationId, ct)).OrderBy(r => r.Number).ToList();     // requires capabilities.read
        var views = new List<CapabilityReleaseView>();
        for (var i = 0; i < releases.Count; i++)
        {
            var current = SafeParse(releases[i].ContentJson)?.Capabilities.ToDictionary(c => c.Key, StringComparer.Ordinal) ?? [];
            var before = i == 0 ? [] : SafeParse(releases[i - 1].ContentJson)?.Capabilities.ToDictionary(c => c.Key, StringComparer.Ordinal) ?? [];
            var changes = new List<string>();
            foreach (var key in current.Keys.Union(before.Keys).Order(StringComparer.Ordinal))
            {
                current.TryGetValue(key, out var a); before.TryGetValue(key, out var b);
                if (a?.Enabled == b?.Enabled) continue;
                changes.Add(a is null ? $"{key}: choice removed (inherits)" : $"{key}: {(a.Enabled ? "on" : "off")}");
            }
            views.Add(new CapabilityReleaseView(releases[i], changes));
        }
        return views.OrderByDescending(v => v.Release.Number).ToList();
    }

    public async Task<PagedResult<CapabilityAuditItem>> AuditAsync(Guid organisationId, int page = 1, CancellationToken ct = default)
    {
        await access.RequireAsync("capabilities.audit.read", Resource(organisationId), ct);
        var query = auditEvents.Events.Where(e => e.EntityType == Kind && e.OrganisationId == organisationId);
        var total = await query.CountAsync(ct);
        var p = Math.Max(1, page);
        var rows = await query.OrderByDescending(e => e.Sequence).Skip((p - 1) * AuditPageSize).Take(AuditPageSize).ToListAsync(ct);
        return new PagedResult<CapabilityAuditItem>(rows.Select(e => new CapabilityAuditItem(e.OccurredUtc, e.Action, e.ActorId, e.Outcome, e.EntityId, e.Reason, e.NewValuesJson)).ToList(), total, p, AuditPageSize);
    }
}
