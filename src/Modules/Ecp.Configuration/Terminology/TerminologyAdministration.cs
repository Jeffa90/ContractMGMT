using System.Globalization;
using Ecp.Audit;
using Ecp.Identity;
using Ecp.Identity.Access;
using Ecp.Identity.Domain;
using Ecp.SharedKernel;
using Ecp.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Ecp.Configuration;

/// <summary>One row of the overview: a stable key, the wording in force and where it comes from, the pack's wording beneath it, and whether the organisation overrides it.</summary>
public sealed record TermRowView(string Key, string Group, string Description, bool Required, string Singular, string Plural, string? Short, TermLayer Layer,
    string? PackSingular, string? PackPlural, string NeutralSingular, string NeutralPlural, TermEntry? Published, TermEntry? Draft)
{
    public bool HasPublishedOverride => Published is not null && Published.Source is null;
    public bool IsPackCopy => Published?.Source is not null;
}

public sealed record TerminologyOverview(Guid OrganisationId, string OrganisationName, string Locale, string? PackKey, string? PackVersion, bool PackHasTerminology, int? ReleaseNumber, DraftDto? Draft,
    IReadOnlyList<TermRowView> Rows, bool CanDraft, bool CanPublish, bool CanRollback, bool CanReadHistory, bool CanReadAudit)
{
    /// <summary>The approver-group display names in force and which layer gave each.</summary>
    public IReadOnlyList<(string Key, string Name, TermLayer Layer)> Groups { get; init; } = [];
    /// <summary>Roles hidden from assignment pickers in force (presentation only).</summary>
    public IReadOnlyList<string> RolesNotOffered { get; init; } = [];
}

/// <summary>What the edit form shows for one locale: every platform key, with the organisation's current draft (or published) entry if it has one, and the wording it would otherwise inherit.</summary>
public sealed record TermEditRow(string Key, string Group, string Description, string InheritedSingular, string InheritedPlural, TermLayer InheritedFrom, TermEntry? Entry);
public sealed record TerminologyEditModel(Guid OrganisationId, string OrganisationName, string Locale, DraftDto? Draft, int? ReleaseNumber, IReadOnlyList<TermEditRow> Rows, IReadOnlyList<string> Locales)
{
    /// <summary>Approver groups the form already knows a name for (the organisation's own or its pack's), for the locale being edited.</summary>
    public IReadOnlyList<GroupEditRow> Groups { get; init; } = [];
    /// <summary>One row per platform role: whether it is offered when people assign roles, and the organisation's own choice if it has one.</summary>
    public IReadOnlyList<RoleEditRow> Roles { get; init; } = [];
}

/// <summary>An approver group's display name for the locale being edited. The key is the stable identifier written in workflows and on role assignments and is never changed here.</summary>
public sealed record GroupEditRow(string Key, string? InheritedName, string? InheritedDescription, TermLayer InheritedFrom, GroupDisplayName? Entry);
/// <summary><see cref="Choice"/> null means the organisation has no choice of its own (it inherits the pack's, otherwise the role is offered).</summary>
public sealed record RoleEditRow(string Code, string Label, bool Hideable, bool InheritedOffered, TermLayer InheritedFrom, bool? Choice);
public sealed record GroupInput(string Key, string? Name, string? Description);
/// <summary><see cref="Choice"/> is <c>inherit</c>, <c>offered</c> or <c>hidden</c>.</summary>
public sealed record RoleInput(string Code, string Choice);

/// <summary>A row submitted by the edit form. <see cref="Override"/> false means "inherit": no entry is kept for that key.</summary>
public sealed record TermInput(string Key, bool Override, string? Singular, string? Plural, string? Short, string? Description, string? Help);

public sealed record TermSample(string Area, string Template, string Now, string After);
public sealed record TerminologyPreviewResult(Guid OrganisationId, string Locale, bool HasDraft, IReadOnlyList<TermSample> Samples, IReadOnlyList<(string Role, string Code, string Now, string After)> Roles)
{
    public IReadOnlyList<(string Key, string Now, string After)> Groups { get; init; } = [];
    public IReadOnlyList<string> RolesNotOfferedNow { get; init; } = [];
    public IReadOnlyList<string> RolesNotOfferedAfter { get; init; } = [];
}
public sealed record TerminologyReleaseView(ReleaseDto Release, int Added, int Changed, int Removed);
/// <summary>What happened when an organisation's pack wording was prepared as a draft.</summary>
public enum PackWordingOutcome { DraftCreated, DraftAlreadyOpen, OrganisationKeepsItsOwn, PackHasNoWording, PackNotAvailable }
public sealed record PackWordingResult(PackWordingOutcome Outcome, string Message, string? PackKey = null, string? PackVersion = null, int Terms = 0);
public sealed record PackWordingExample(string PackKey, string PackVersion, IReadOnlyList<(string Key, string Label)> Examples, int TermCount);
public sealed record TerminologyAuditItem(DateTime OccurredUtc, string Action, string? Actor, string Outcome, string Subject, string? Reason, string? Details);

/// <summary>
/// The form-based administration of terminology, as services: what the overview shows, what the edit form holds, saving a draft, a preview, the version history and the audit trail. The pages make no decision of their own:
/// every call checks the terminology permissions for the organisation, and drafting, validating, publishing and rolling back go through the ordinary configuration lifecycle (so second-person publication, versioning and audit apply).
/// </summary>
public interface ITerminologyAdministration
{
    Task<TerminologyOverview> OverviewAsync(Guid organisationId, string? locale = null, CancellationToken ct = default);
    Task<TerminologyEditModel> EditModelAsync(Guid organisationId, string? locale = null, CancellationToken ct = default);
    /// <summary>Replaces the organisation's entries for one locale in its open draft (creating the draft from the published release if there is none). Other locales are kept. Nothing is published.</summary>
    Task<DraftDto> SaveAsync(Guid organisationId, string locale, IReadOnlyList<TermInput> inputs, CancellationToken ct = default);
    /// <summary>
    /// As above, and also replaces the approver-group display names for the locale and the role visibility choices in the same draft. A null list keeps what the draft already holds. Role visibility hides a role from pickers
    /// only: it revokes nothing, and the administrative roles cannot be hidden.
    /// </summary>
    Task<DraftDto> SaveAsync(Guid organisationId, string locale, IReadOnlyList<TermInput> inputs, IReadOnlyList<GroupInput>? groups, IReadOnlyList<RoleInput>? roles, CancellationToken ct = default);
    Task<TerminologyPreviewResult> PreviewAsync(Guid organisationId, string? locale = null, CancellationToken ct = default);
    /// <summary>
    /// Prepares a terminology draft from a solution pack for an organisation: the pack's wording, stamped with the pack and version it came from. Never publishes, never replaces a draft that is open and never touches terminology
    /// the organisation has already published. Used by onboarding after a pack is chosen.
    /// </summary>
    Task<PackWordingResult> PrepareFromPackAsync(Guid organisationId, string packKey, CancellationToken ct = default);
    /// <summary>A few headline labels from a pack's wording, to show before the pack is chosen. Needs no permission beyond seeing the pack catalogue (the caller supplies the pack).</summary>
    Task<PackWordingExample?> ExampleOfPackAsync(string packKey, CancellationToken ct = default);
    Task<IReadOnlyList<TerminologyReleaseView>> HistoryAsync(Guid organisationId, CancellationToken ct = default);
    Task<PagedResult<TerminologyAuditItem>> AuditAsync(Guid organisationId, int page = 1, CancellationToken ct = default);
}

internal sealed class TerminologyAdministration(IConfigurationService configuration, ITerminologyResolver resolver, IOrganisationStructureService organisations, IAccessService access, ITenantContext tenant,
    IAuditEventSource auditEvents, IPackLoader packs) : ITerminologyAdministration
{
    private static readonly string[] ExampleKeys = ["application.name", "contract", "supplier", "category", "contract.owner", "legal.review", "registration.approval", "primary.approver"];

    public async Task<PackWordingExample?> ExampleOfPackAsync(string packKey, CancellationToken ct = default)
    {
        var entry = await packs.FindAsync(packKey, null, usableOnly: true, ct);
        if (entry is null) return null;
        var json = await packs.GetDocumentAsync(entry.PackKey, entry.Version, Kind, ct);
        if (json is null) return null;
        var doc = SafeParse(json);
        if (doc is null) return null;
        var set = TermSet.Build("en", null, doc.Terms, null, null);
        return new PackWordingExample(entry.PackKey, entry.Version, ExampleKeys.Select(k => (k, set.Singular(k))).ToList(), doc.Terms.Count(t => t.Locale == (doc.Terms.GroupBy(x => x.Locale).OrderByDescending(g => g.Count()).First().Key)));
    }

    public async Task<PackWordingResult> PrepareFromPackAsync(Guid organisationId, string packKey, CancellationToken ct = default)
    {
        await access.RequireAsync("terminology.draft", Resource(organisationId), ct);
        var entry = await packs.FindAsync(packKey, null, usableOnly: true, ct);
        if (entry is null) return new PackWordingResult(PackWordingOutcome.PackNotAvailable, "That pack is not available here, so no wording was prepared.");
        var json = await packs.GetDocumentAsync(entry.PackKey, entry.Version, Kind, ct);
        var doc = json is null ? null : SafeParse(json);
        if (doc is null) return new PackWordingResult(PackWordingOutcome.PackHasNoWording, "This pack carries no wording of its own, so the platform's neutral wording applies.", entry.PackKey, entry.Version);
        if (await configuration.LatestReleaseAsync(Kind, organisationId, ct) is not null)
            return new PackWordingResult(PackWordingOutcome.OrganisationKeepsItsOwn, "This organisation already publishes its own wording, which stays in force. The pack's wording applies only where it has none.", entry.PackKey, entry.Version, doc.Terms.Count);
        if (await configuration.FindOpenDraftAsync(Kind, organisationId, ct) is not null)
            return new PackWordingResult(PackWordingOutcome.DraftAlreadyOpen, "A terminology draft is already open and was kept.", entry.PackKey, entry.Version, doc.Terms.Count);
        var seeded = doc.WithSource(entry.PackKey, entry.Version, TerminologyDocument.Overrides);
        await configuration.GetOrCreateDraftAsync(Kind, organisationId, seeded.ToJson(), ct);
        return new PackWordingResult(PackWordingOutcome.DraftCreated, "The pack's wording is prepared as a terminology draft. It applies as the default now; review it under Administration, Terminology and publish it only if you want it to become this organisation's own.", entry.PackKey, entry.Version, doc.Terms.Count);
    }

    private const int AuditPageSize = 50;
    private const string Kind = TerminologyKind.KindName;

    /// <summary>Sample texts that stand for the real pages and messages, written with the same tokens the pages use. The preview shows each before and after.</summary>
    private static readonly (string Area, string Template)[] Samples =
    [
        ("Navigation", "[[contract|plural]]"), ("Navigation", "[[legal.review|sentence]]"), ("Navigation", "[[approval|plural]]"), ("Navigation", "[[supplier|plural]]"), ("Navigation", "[[category|plural]]"),
        ("Page heading", "[[contract.register|sentence]]"), ("Page heading", "[[legal.review|sentence]] queue"), ("Page heading", "Add [[supplier|a|lower]]"),
        ("Column heading", "[[contract.reference|sentence]]"), ("Column heading", "[[supplier]]"), ("Column heading", "[[category]]"),
        ("Message", "Choose [[category|a|lower]]."), ("Message", "Approved. The [[agreement|lower]] is registered, Effective and locked."), ("Message", "Only [[reviewer]] can add [[document|plural|lower]] while the review is under way."),
        ("Notification", "[[renewal|sentence]] due for this [[agreement|lower]]"), ("Notification", "[[registration.approval|sentence]] is waiting for you"),
        ("Export heading", "[[supplier]]"), ("Export heading", "[[contract.reference]]"),
    ];

    private static readonly string[] SampleRoles = ["ProcurementAdministrator", "CategoryOwner", "Legal", "Approver", "BackupApprover"];

    private ResourceDescriptor Resource(Guid organisationId)
        => new("Configuration", tenant.RequireTenantId(), organisationId, Id: Kind, Dimensions: ScopeDimensions.OrganisationOnly);

    private static string LocaleOrDefault(string? locale, OrganisationSummary org)
        => string.IsNullOrWhiteSpace(locale) ? (org.CultureCode is { Length: > 0 } c ? c : "en") : locale.Trim();

    private async Task<OrganisationSummary> OrganisationAsync(Guid organisationId, CancellationToken ct)
        => await organisations.GetOrganisationAsync(organisationId, ct) ?? throw new ResourceNotFoundException();

    private async Task<bool> HoldsAsync(string permission, Guid organisationId, CancellationToken ct)
        => (await access.CheckSilentAsync(permission, Resource(organisationId), ct)).Allowed;

    /// <summary>Reading terminology does not include seeing a draft: a person without the drafting permission simply sees no draft.</summary>
    private async Task<DraftDto?> OpenDraftIfAllowedAsync(Guid organisationId, CancellationToken ct)
        => await HoldsAsync("terminology.draft", organisationId, ct) ? await configuration.FindOpenDraftAsync(Kind, organisationId, ct) : null;

    private static TermEntry? Pick(TerminologyDocument? doc, string key, string locale)
    {
        if (doc is null) return null;
        foreach (var l in TermSet.LocaleChain(locale))
            if (doc.Terms.FirstOrDefault(t => t.Active && t.Key == key && t.Locale == l) is { } e) return e;
        return null;
    }

    public async Task<TerminologyOverview> OverviewAsync(Guid organisationId, string? locale = null, CancellationToken ct = default)
    {
        await access.RequireAsync("terminology.read", Resource(organisationId), ct);
        var org = await OrganisationAsync(organisationId, ct);
        var loc = LocaleOrDefault(locale, org);
        var effective = await resolver.GetAsync(organisationId, loc, ct);
        var pack = await resolver.PackLayerAsync(organisationId, ct);
        var release = await configuration.LatestReleaseAsync(Kind, organisationId, ct);
        var draft = await OpenDraftIfAllowedAsync(organisationId, ct);
        TerminologyDocument? published = release is null ? null : TerminologyDocument.Parse(release.ContentJson);
        TerminologyDocument? draftDoc = null;
        if (draft is not null) try { draftDoc = TerminologyDocument.Parse(draft.ContentJson); } catch (DomainException) { /* an unreadable draft is shown as a draft with no readable rows */ }
        var neutral = TermSet.Neutral(loc);
        var packSet = pack is null ? null : TermSet.Build(loc, null, pack.Document.Terms, null, null);

        var rows = new List<TermRowView>();
        foreach (var def in TermCatalogue.All)
        {
            var found = effective.Find(def.Key);
            rows.Add(new TermRowView(def.Key, def.Group, def.Description, def.Required, found?.Singular ?? def.Singular, found?.Plural ?? def.Plural, found?.Short, found?.Layer ?? TermLayer.Platform,
                packSet?.Find(def.Key) is { Layer: TermLayer.Pack } p ? p.Singular : null, packSet?.Find(def.Key) is { Layer: TermLayer.Pack } p2 ? p2.Plural : null,
                neutral.Singular(def.Key), neutral.Plural(def.Key), Pick(published, def.Key, loc), Pick(draftDoc, def.Key, loc)));
        }
        var groups = effective.GroupNames.Values.OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => (g.Key, g.Name, published?.Groups?.Any(x => x.Key == g.Key) == true ? TermLayer.Organisation : TermLayer.Pack)).ToList();
        return new TerminologyOverview(organisationId, org.Name, loc, pack?.PackKey, pack?.PackVersion, pack is not null, release?.Number, draft, rows,
            await HoldsAsync("terminology.draft", organisationId, ct), await HoldsAsync("terminology.publish", organisationId, ct), await HoldsAsync("terminology.rollback", organisationId, ct),
            await HoldsAsync("terminology.read", organisationId, ct), await HoldsAsync("terminology.audit.read", organisationId, ct))
        { Groups = groups, RolesNotOffered = effective.HiddenRoles.Order(StringComparer.Ordinal).ToList() };
    }

    public async Task<TerminologyEditModel> EditModelAsync(Guid organisationId, string? locale = null, CancellationToken ct = default)
    {
        await access.RequireAsync("terminology.draft", Resource(organisationId), ct);
        var org = await OrganisationAsync(organisationId, ct);
        var loc = LocaleOrDefault(locale, org);
        var draft = await configuration.FindOpenDraftAsync(Kind, organisationId, ct);
        var release = await configuration.LatestReleaseAsync(Kind, organisationId, ct);
        var source = draft?.ContentJson ?? release?.ContentJson;
        var doc = source is null ? null : SafeParse(source);
        var pack = await resolver.PackLayerAsync(organisationId, ct);
        var inherited = TermSet.Build(loc, null, pack?.Document.Terms, null, null);
        var locales = new SortedSet<string>(StringComparer.Ordinal) { "en", loc };
        if (org.CultureCode is { Length: > 0 } c) locales.Add(c);
        foreach (var t in doc?.Terms ?? []) locales.Add(t.Locale);
        foreach (var t in pack?.Document.Terms ?? []) locales.Add(t.Locale);
        var rows = TermCatalogue.All.Select(def =>
        {
            var inheritedTerm = inherited.Find(def.Key);
            var entry = doc?.Terms.FirstOrDefault(t => t.Key == def.Key && t.Locale == loc);
            return new TermEditRow(def.Key, def.Group, def.Description, inheritedTerm?.Singular ?? def.Singular, inheritedTerm?.Plural ?? def.Plural, inheritedTerm?.Layer ?? TermLayer.Platform, entry);
        }).ToList();
        var packExtras = pack?.Document.Extras;
        var inheritedGroups = TermSet.Build(loc, null, null, null, null, null, packExtras);
        var groupKeys = new SortedSet<string>((doc?.Groups ?? []).Where(g => g.Locale == loc).Select(g => g.Key).Concat((pack?.Document.Groups ?? []).Select(g => g.Key)), StringComparer.Ordinal);
        foreach (var l in locales) foreach (var g in doc?.Groups ?? []) if (g.Locale == l) groupKeys.Add(g.Key);
        var groups = groupKeys.Select(k => new GroupEditRow(k, inheritedGroups.HasGroupName(k) ? inheritedGroups.Group(k) : null, inheritedGroups.GroupDescription(k), inheritedGroups.HasGroupName(k) ? TermLayer.Pack : TermLayer.Platform,
            doc?.Groups?.FirstOrDefault(g => g.Key == k && g.Locale == loc))).ToList();
        var inheritedRoles = TermSet.Build(loc, null, null, null, null, null, packExtras);
        var roleRows = RoleCodes.All.Select(code => new RoleEditRow(code, inherited.Singular(TermCatalogue.KeyForRole(code)), !TermSet.RolesNeverHidden.Contains(code), inheritedRoles.IsRoleOffered(code),
            pack?.Document.Roles?.Any(r => r.Code == code) == true ? TermLayer.Pack : TermLayer.Platform, doc?.Roles?.FirstOrDefault(r => r.Code == code)?.Offered)).ToList();
        return new TerminologyEditModel(organisationId, org.Name, loc, draft, release?.Number, rows, locales.ToList()) { Groups = groups, Roles = roleRows };
    }

    private static TerminologyDocument? SafeParse(string json) { try { return TerminologyDocument.Parse(json); } catch (DomainException) { return null; } }

    public Task<DraftDto> SaveAsync(Guid organisationId, string locale, IReadOnlyList<TermInput> inputs, CancellationToken ct = default)
        => SaveAsync(organisationId, locale, inputs, null, null, ct);

    public async Task<DraftDto> SaveAsync(Guid organisationId, string locale, IReadOnlyList<TermInput> inputs, IReadOnlyList<GroupInput>? groupInputs, IReadOnlyList<RoleInput>? roleInputs, CancellationToken ct = default)
    {
        await access.RequireAsync("terminology.draft", Resource(organisationId), ct);
        _ = await OrganisationAsync(organisationId, ct);
        var loc = (locale ?? "").Trim();
        var open = await configuration.FindOpenDraftAsync(Kind, organisationId, ct);
        var release = open is null ? await configuration.LatestReleaseAsync(Kind, organisationId, ct) : null;
        var existing = SafeParse(open?.ContentJson ?? release?.ContentJson ?? "") ?? TerminologyDocument.Empty;

        var kept = existing.Terms.Where(t => t.Locale != loc).ToList();                       // other locales, and unknown keys of other locales, are untouched
        var previousByKey = existing.Terms.Where(t => t.Locale == loc).ToDictionary(t => t.Key, StringComparer.Ordinal);
        var unknownKeptForLocale = previousByKey.Values.Where(t => !TermCatalogue.IsKnown(t.Key)).ToList();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var entries = new List<TermEntry>(kept);
        entries.AddRange(unknownKeptForLocale);
        foreach (var input in inputs)
        {
            if (!input.Override || !seen.Add(input.Key)) continue;                           // an unticked row inherits; a repeated row is ignored here and caught by validation if it ever reaches the document
            var entry = new TermEntry(input.Key, loc, (input.Singular ?? "").Trim(), (input.Plural ?? "").Trim(), Blank(input.Short), Blank(input.Description), Blank(input.Help));
            // A copy of a pack term keeps its provenance only while it is still exactly the pack's wording.
            if (previousByKey.TryGetValue(input.Key, out var old) && old.Source is not null && SameWording(old, entry)) entry = entry with { Source = old.Source };
            entries.Add(entry);
        }
        // Approver-group names: other locales are kept; this locale is replaced by what the form sent (a blank name means "inherit", so no entry). Role choices are not per locale.
        var groups = groupInputs is null ? existing.Groups : (existing.Groups ?? []).Where(g => g.Locale != loc)
            .Concat(groupInputs.Where(g => !string.IsNullOrWhiteSpace(g.Key) || !string.IsNullOrWhiteSpace(g.Name))
                .Select(g => new GroupDisplayName((g.Key ?? "").Trim(), loc, (g.Name ?? "").Trim(), Blank(g.Description)))).ToList();
        var roles = roleInputs is null ? existing.Roles : roleInputs.Where(r => r.Choice is "offered" or "hidden").Select(r => new RoleOffer(r.Code.Trim(), r.Choice == "offered")).ToList();
        var document = new TerminologyDocument(TerminologyDocument.Overrides, entries, groups is { Count: > 0 } ? groups : null, roles is { Count: > 0 } ? roles : null);
        var json = document.ToJson();
        var draft = open ?? await configuration.GetOrCreateDraftAsync(Kind, organisationId, json, ct);
        return await configuration.SaveDraftAsync(draft.Id, json, ct);
    }

    private static string? Blank(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    private static bool SameWording(TermEntry a, TermEntry b) => a.Singular == b.Singular && a.Plural == b.Plural && a.Short == b.Short && a.Description == b.Description && a.Help == b.Help;

    public async Task<TerminologyPreviewResult> PreviewAsync(Guid organisationId, string? locale = null, CancellationToken ct = default)
    {
        await access.RequireAsync("terminology.read", Resource(organisationId), ct);
        var org = await OrganisationAsync(organisationId, ct);
        var loc = LocaleOrDefault(locale, org);
        var now = await resolver.GetAsync(organisationId, loc, ct);
        var draft = await OpenDraftIfAllowedAsync(organisationId, ct);
        var doc = draft is null ? null : SafeParse(draft.ContentJson);
        var after = doc is null ? now : await resolver.PreviewAsync(organisationId, doc, loc, ct);
        var samples = Samples.Select(s => new TermSample(s.Area, s.Template, now.Expand(s.Template), after.Expand(s.Template))).ToList();
        var roles = SampleRoles.Select(r => (Role: TermCatalogue.KeyForRole(r), Code: r)).Select(r => (r.Role, r.Code, now.Singular(r.Role), after.Singular(r.Role))).ToList();
        var groupKeys = now.GroupNames.Keys.Union(after.GroupNames.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        return new TerminologyPreviewResult(organisationId, loc, draft is not null, samples, roles)
        {
            Groups = groupKeys.Select(k => (k, now.Group(k), after.Group(k))).ToList(),
            RolesNotOfferedNow = now.HiddenRoles.Order(StringComparer.Ordinal).ToList(),
            RolesNotOfferedAfter = after.HiddenRoles.Order(StringComparer.Ordinal).ToList()
        };
    }

    public async Task<IReadOnlyList<TerminologyReleaseView>> HistoryAsync(Guid organisationId, CancellationToken ct = default)
    {
        var releases = await configuration.ReleasesAsync(Kind, organisationId, ct);           // requires terminology.read
        var ordered = releases.OrderBy(r => r.Number).ToList();
        var views = new List<TerminologyReleaseView>();
        for (var i = 0; i < ordered.Count; i++)
        {
            var current = SafeParse(ordered[i].ContentJson)?.Terms ?? [];
            var previous = i == 0 ? [] : SafeParse(ordered[i - 1].ContentJson)?.Terms ?? [];
            var before = previous.ToDictionary(t => (t.Key, t.Locale));
            var now = current.ToDictionary(t => (t.Key, t.Locale));
            var added = now.Keys.Count(k => !before.ContainsKey(k));
            var removed = before.Keys.Count(k => !now.ContainsKey(k));
            var changed = now.Count(kv => before.TryGetValue(kv.Key, out var old) && !SameWording(old, kv.Value));
            views.Add(new TerminologyReleaseView(ordered[i], added, changed, removed));
        }
        return views.OrderByDescending(v => v.Release.Number).ToList();
    }

    public async Task<PagedResult<TerminologyAuditItem>> AuditAsync(Guid organisationId, int page = 1, CancellationToken ct = default)
    {
        await access.RequireAsync("terminology.audit.read", Resource(organisationId), ct);
        var query = auditEvents.Events.Where(e => e.EntityType == Kind && e.OrganisationId == organisationId);
        var total = await query.CountAsync(ct);
        var p = Math.Max(1, page);
        var rows = await query.OrderByDescending(e => e.Sequence).Skip((p - 1) * AuditPageSize).Take(AuditPageSize).ToListAsync(ct);
        return new PagedResult<TerminologyAuditItem>(rows.Select(e => new TerminologyAuditItem(e.OccurredUtc, e.Action, e.ActorId, e.Outcome, e.EntityId, e.Reason, e.NewValuesJson)).ToList(), total, p, AuditPageSize);
    }
}
