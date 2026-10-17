using System.Collections.Concurrent;
using Ecp.Configuration.Domain;
using Ecp.Configuration.Persistence;
using Ecp.Identity;
using Ecp.Identity.Domain;
using Ecp.SharedKernel;
using Ecp.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ecp.Configuration;

/// <summary>The defaults a solution pack gives for capabilities (the layer beneath the organisation's own).</summary>
public sealed record PackCapabilities(string PackKey, string PackVersion, CapabilityDocument Document);

public sealed record CapabilityCacheStatistics(long Hits, long Misses, long Fallbacks, int Entries);

/// <summary>
/// Remembers resolved capability sets. The key carries the tenant, the organisation and the version (the organisation's release plus the pack identity), so one tenant's or organisation's switches can never be served to another,
/// and a publish or rollback is seen at once because it changes the release in the key. A failure of the cache is never an error: the caller resolves from persistence.
/// </summary>
public interface ICapabilityCache
{
    bool TryGet(Guid tenantId, Guid organisationId, string version, out CapabilitySet? set);
    void Store(Guid tenantId, Guid organisationId, string version, CapabilitySet set);
    void Invalidate(Guid tenantId, Guid organisationId);
    /// <summary>The defaults of one pack version (null inside a hit means the pack gives none). A pack version's content never changes, so this is shared by tenants; "latest usable" lookups (no version) expire with the time to live.</summary>
    bool TryGetPack(string packKey, string? version, out PackCapabilities? pack);
    void StorePack(string packKey, string? version, PackCapabilities? pack);
    CapabilityCacheStatistics Statistics { get; }
    /// <summary>Changes whenever a set is invalidated or the cache is cleared, so a caller that remembers a set for a short time (one request) knows to look again.</summary>
    long Generation { get; }
    void CountFallback();
    void Clear();
}

public sealed class CapabilityCache(TimeProvider? time = null, TimeSpan? timeToLive = null, int maxEntries = 2000) : ICapabilityCache
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly TimeSpan _ttl = timeToLive ?? TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<(Guid Tenant, Guid Org, string Version), (CapabilitySet Set, DateTimeOffset Stored)> _sets = new();
    private readonly ConcurrentDictionary<(string Key, string Version), (PackCapabilities? Pack, DateTimeOffset Stored)> _packs = new();
    private long _hits, _misses, _fallbacks, _generation;

    public long Generation => Interlocked.Read(ref _generation);

    public CapabilityCacheStatistics Statistics => new(Interlocked.Read(ref _hits), Interlocked.Read(ref _misses), Interlocked.Read(ref _fallbacks), _sets.Count);

    public bool TryGet(Guid tenantId, Guid organisationId, string version, out CapabilitySet? set)
    {
        if (_sets.TryGetValue((tenantId, organisationId, version), out var e) && _time.GetUtcNow() - e.Stored < _ttl) { Interlocked.Increment(ref _hits); set = e.Set; return true; }
        Interlocked.Increment(ref _misses); set = null; return false;
    }

    public void Store(Guid tenantId, Guid organisationId, string version, CapabilitySet set)
    {
        if (_sets.Count >= maxEntries) _sets.Clear();
        _sets[(tenantId, organisationId, version)] = (set, _time.GetUtcNow());
    }

    public void Invalidate(Guid tenantId, Guid organisationId)
    {
        foreach (var key in _sets.Keys.Where(k => k.Tenant == tenantId && k.Org == organisationId).ToList()) _sets.TryRemove(key, out _);
        Interlocked.Increment(ref _generation);
    }

    public bool TryGetPack(string packKey, string? version, out PackCapabilities? pack)
    {
        if (_packs.TryGetValue((packKey, version ?? "*"), out var e) && _time.GetUtcNow() - e.Stored < _ttl) { pack = e.Pack; return true; }
        pack = null; return false;
    }

    public void StorePack(string packKey, string? version, PackCapabilities? pack)
    {
        if (_packs.Count >= maxEntries) _packs.Clear();
        _packs[(packKey, version ?? "*")] = (pack, _time.GetUtcNow());
    }

    public void CountFallback() => Interlocked.Increment(ref _fallbacks);
    public void Clear() { _sets.Clear(); _packs.Clear(); Interlocked.Increment(ref _generation); }
}

/// <summary>A problem or note about a capability combination for one organisation. An error means the combination is incoherent and must not be published; a warning is reported.</summary>
public sealed record CapabilityFinding(ValidationSeverity Severity, string Capability, string Code, string Message);

/// <summary>
/// Looks at the parts of an organisation's configuration and records that a capability set has to be coherent with: the published workflows (they must not use a capability that is off), and work already under way (a capability
/// must not be turned off from under it). It is implemented where those things live (the lifecycle module) and used by the capability kind, the readiness checks and the pack assignment preview.
/// </summary>
public interface ICapabilityCompatibility
{
    /// <summary>Findings for <paramref name="proposed"/>. <paramref name="current"/> is what is in force now (null when unknown): only a capability that is on now and off in the proposal is checked for work in flight as an error.</summary>
    Task<IReadOnlyList<CapabilityFinding>> CheckAsync(Guid organisationId, CapabilitySet proposed, CapabilitySet? current, CancellationToken ct = default);
}

/// <summary>
/// The one place anything asks whether a capability is on. It reads the organisation's published capability document, then the defaults of the solution pack the organisation works from, then the platform default (everything
/// on). It fails to the platform default: if the configuration cannot be read, the platform behaves as it did before capabilities were configurable, and says so in the log. A capability is operating-model configuration, never
/// authority: the permission checks that guard every step are not affected by it.
/// </summary>
public interface ICapabilityResolver
{
    Task<CapabilitySet> GetAsync(Guid organisationId, CancellationToken ct = default);
    /// <summary>What the organisation would have if <paramref name="draft"/> replaced its published document. Nothing is cached.</summary>
    Task<CapabilitySet> PreviewAsync(Guid organisationId, CapabilityDocument draft, CancellationToken ct = default);
    /// <summary>What the organisation would have if it were assigned the pack (its own published choice, if any, still on top).</summary>
    Task<CapabilitySet> AfterPackAsync(Guid organisationId, string packKey, string packVersion, CancellationToken ct = default);
    Task<PackCapabilities?> PackLayerAsync(Guid organisationId, CancellationToken ct = default);
    Task<bool> IsEnabledAsync(Guid organisationId, string key, CancellationToken ct = default);
    /// <summary>The capabilities that are on in at least one organisation the signed-in person can reach: what navigation and shared pages use. A person with no organisation (an installation administrator) sees everything.</summary>
    Task<IReadOnlySet<string>> EnabledForCurrentPersonAsync(CancellationToken ct = default);
}

internal sealed class CapabilityResolver(IServiceProvider services, ConfigurationDbContext db, ITenantContext tenant, ICapabilityCache cache, ConfigurationOptions options, IClock clock, ILogger<CapabilityResolver> logger) : ICapabilityResolver
{
    // The services this resolver reads are resolved on first use, not at construction. The configuration service builds every configuration kind, and kinds in other modules (onboarding, list views, workflows) depend on
    // things that ask the resolver; resolving them eagerly would make the dependency graph circular.
    private IConfigurationReleases releases => services.GetRequiredService<IConfigurationReleases>();
    private IOrganisationStructureService organisations => services.GetRequiredService<IOrganisationStructureService>();
    private IPackLoader packs => services.GetRequiredService<IPackLoader>();
    private IAccessService access => services.GetRequiredService<IAccessService>();

    // One request asks about the same organisation many times (navigation, the page, the register, the dashboard). The resolver is scoped, so it remembers each answer for the life of the scope and drops them when the
    // cache is invalidated (a publish or rollback anywhere). Working out which version applies costs several reads, so this is what keeps a page's statement count flat.
    private readonly Dictionary<(Guid Tenant, Guid Organisation), CapabilitySet> _remembered = new();
    private IReadOnlySet<string>? _reach;
    private long _generation = -1;

    private void Refresh()
    {
        var generation = cache.Generation;
        if (generation == _generation) return;
        _remembered.Clear(); _reach = null; _generation = generation;
    }

    public Task<CapabilitySet> GetAsync(Guid organisationId, CancellationToken ct = default) => GetCoreAsync(organisationId, null, ct);

    private async Task<CapabilitySet> GetCoreAsync(Guid organisationId, Preloaded? preloaded, CancellationToken ct)
    {
        if (tenant.TenantId is not { } tenantId || organisationId == Guid.Empty) return CapabilitySet.Default;
        Refresh();
        if (_remembered.TryGetValue((tenantId, organisationId), out var remembered)) return remembered;
        var resolved = await ResolveAsync(tenantId, organisationId, preloaded, ct);
        if (!ReferenceEquals(resolved, CapabilitySet.Default)) _remembered[(tenantId, organisationId)] = resolved;     // a fall-back answer is never remembered
        return resolved;
    }

    private async Task<CapabilitySet> ResolveAsync(Guid tenantId, Guid organisationId, Preloaded? preloaded, CancellationToken ct)
    {
        try
        {
            var release = await releases.LatestAsync(CapabilityKinds.Name, organisationId, ct);
            var pack = await PackLayerCoreAsync(organisationId, preloaded, ct);
            var version = $"{release?.Id.ToString("N") ?? "-"}|{pack?.PackKey ?? "-"}@{pack?.PackVersion ?? "-"}";
            try { if (cache.TryGet(tenantId, organisationId, version, out var hit) && hit is not null) return hit; }
            catch (Exception ex) { cache.CountFallback(); logger.LogWarning(ex, "The capability cache could not be read; resolving from persistence."); }
            var set = CapabilitySet.Build(release is null ? null : CapabilityDocument.Parse(release.ContentJson).Capabilities, pack?.Document.Capabilities, $"{release?.Number.ToString() ?? "-"}|{pack?.PackKey ?? "-"}@{pack?.PackVersion ?? "-"}");
            try { cache.Store(tenantId, organisationId, version, set); }
            catch (Exception ex) { cache.CountFallback(); logger.LogWarning(ex, "The capability cache could not be written; the set was resolved from persistence."); }
            return set;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Capabilities only ever switch optional steps off. If the configuration cannot be read, the platform behaves as it did before capabilities were configurable.
            logger.LogWarning(ex, "Capabilities could not be resolved; every capability is treated as on.");
            return CapabilitySet.Default;
        }
    }

    public async Task<bool> IsEnabledAsync(Guid organisationId, string key, CancellationToken ct = default) => (await GetAsync(organisationId, ct)).IsEnabled(key);

    public async Task<CapabilitySet> PreviewAsync(Guid organisationId, CapabilityDocument draft, CancellationToken ct = default)
    {
        var pack = await PackLayerAsync(organisationId, ct);
        return CapabilitySet.Build(draft.Capabilities, pack?.Document.Capabilities, "preview");
    }

    public async Task<CapabilitySet> AfterPackAsync(Guid organisationId, string packKey, string packVersion, CancellationToken ct = default)
    {
        var release = await releases.LatestAsync(CapabilityKinds.Name, organisationId, ct);
        var json = await packs.GetDocumentAsync(packKey, packVersion, CapabilityKinds.Name, ct);
        var doc = json is null ? null : SafeParse(json);
        return CapabilitySet.Build(release is null ? null : CapabilityDocument.Parse(release.ContentJson).Capabilities, doc?.Capabilities, "after-pack");
    }

    /// <summary>What a caller that already holds the organisation list and its active pack assignments can hand over, so a request that resolves several organisations reads them once.</summary>
    private sealed record Preloaded(OrganisationSummary? Organisation, IReadOnlyDictionary<(Guid Organisation, string PackKey), string> Versions);

    public Task<PackCapabilities?> PackLayerAsync(Guid organisationId, CancellationToken ct = default) => PackLayerCoreAsync(organisationId, null, ct);

    private async Task<PackCapabilities?> PackLayerCoreAsync(Guid organisationId, Preloaded? preloaded, CancellationToken ct)
    {
        try
        {
            if (organisationId == Guid.Empty) return null;
            var summary = preloaded is not null ? preloaded.Organisation : await organisations.GetOrganisationAsync(organisationId, ct);
            var packKey = summary?.SolutionPackKey;
            if (string.IsNullOrWhiteSpace(packKey)) packKey = options.DefaultPackId;
            if (string.IsNullOrWhiteSpace(packKey)) return null;
            var version = preloaded is not null
                ? preloaded.Versions.GetValueOrDefault((organisationId, packKey))
                : await db.PackAssignments.AsNoTracking().Where(a => a.OrganisationId == organisationId && a.PackKey == packKey && a.Status == PackAssignment.Active).Select(a => a.Version).FirstOrDefaultAsync(ct);
            if (cache.TryGetPack(packKey, version, out var known)) return known;
            var entry = await packs.FindAsync(packKey, version, usableOnly: version is null, ct);
            if (entry is null || entry.Unavailable || !entry.IsCompatible || !entry.Trust.IsAcceptable) { cache.StorePack(packKey, version, null); return null; }
            var json = await packs.GetDocumentAsync(entry.PackKey, entry.Version, CapabilityKinds.Name, ct);
            var doc = json is null ? null : SafeParse(json);
            var layer = doc is null ? null : new PackCapabilities(entry.PackKey, entry.Version, doc);
            cache.StorePack(packKey, version, layer);
            return layer;
        }
        catch (Exception ex) when (ex is DomainException or System.Text.Json.JsonException)
        {
            logger.LogWarning(ex, "The solution pack's capability defaults could not be read; the platform default applies beneath the organisation's own choice.");
            return null;
        }
    }

    private static CapabilityDocument? SafeParse(string json) { try { return CapabilityDocument.Parse(json); } catch (DomainException) { return null; } }

    public async Task<IReadOnlySet<string>> EnabledForCurrentPersonAsync(CancellationToken ct = default)
    {
        var all = new HashSet<string>(CapabilityKeys.All, StringComparer.Ordinal);
        if (tenant.TenantId is null) return all;
        Refresh();
        if (_reach is not null) return _reach;
        try
        {
            var subject = await access.GetSubjectAsync(ct);
            if (subject is null) return all;
            var now = clock.UtcNow;
            var active = subject.Assignments.Where(a => a.IsActiveAt(now)).ToList();
            if (active.Count == 0) return all;
            // One read of the organisations and one of their pack assignments serve every organisation in reach.
            var known = (await organisations.ListOrganisationsAsync(ct)).ToDictionary(o => o.Id);
            var reach = new HashSet<Guid>(active.Where(a => a.Scope.Level == ScopeLevel.Organisation).SelectMany(a => a.Scope.OrganisationIds));
            if (active.Any(a => a.Scope.Level != ScopeLevel.Organisation)) foreach (var id in known.Keys) reach.Add(id);
            if (reach.Count == 0) return all;
            var enabled = new HashSet<string>(StringComparer.Ordinal);
            var ids = reach.ToList();
            var assigned = await db.PackAssignments.AsNoTracking().Where(a => ids.Contains(a.OrganisationId) && a.Status == PackAssignment.Active).Select(a => new { a.OrganisationId, a.PackKey, a.Version }).ToListAsync(ct);
            var versions = assigned.GroupBy(a => (a.OrganisationId, a.PackKey)).ToDictionary(g => g.Key, g => g.First().Version);
            foreach (var org in ids)
                foreach (var key in (await GetCoreAsync(org, new Preloaded(known.GetValueOrDefault(org), versions), ct)).Enabled) enabled.Add(key);
            return _reach = enabled;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "The capabilities in reach of the signed-in person could not be determined; every capability is treated as on.");
            return all;
        }
    }
}

/// <summary>The capability configuration kind: versioned, validated (including for coherence with the organisation's workflows and work in flight), previewed, published, rolled back and audited by the ordinary lifecycle.</summary>
internal sealed class CapabilityKind(ICapabilityCache cache, ITenantContext tenant, IServiceProvider services) : IConfigurationKind
{
    public string Kind => CapabilityKinds.Name;
    public string DraftPermission => "capabilities.draft";
    public string PublishPermission => "capabilities.publish";
    public string RollbackPermission => "capabilities.rollback";
    public string HistoryPermission => "capabilities.read";

    public async Task<ValidationReport> ValidateAsync(Guid organisationId, string contentJson, CancellationToken ct)
    {
        CapabilityDocument doc;
        try { doc = CapabilityDocument.Parse(contentJson); }
        catch (DomainException ex) { return new ValidationReport([new ValidationMessage(ValidationSeverity.Error, "$", ex.Code, ex.Message)]); }
        var messages = doc.Check().ToList();
        if (messages.Any(m => m.Severity == ValidationSeverity.Error)) return new ValidationReport(messages);

        // A pack's document (validated with no organisation) has to be coherent on its own. It is data for many organisations, so nothing about any one organisation's workflows or work in flight is checked here.
        if (organisationId == Guid.Empty) { messages.AddRange(doc.CheckCoherence()); return new ValidationReport(messages); }

        if (doc.Mode != CapabilityDocument.Overrides) { messages.Add(new(ValidationSeverity.Error, "$.mode", "capability_mode", "An organisation's capability document has the mode 'overrides'.")); return new ValidationReport(messages); }

        // The organisation's choice sits on top of its pack's defaults; the result has to be coherent on its own and with the organisation's workflows and work in flight.
        var resolver = services.GetRequiredService<ICapabilityResolver>();
        var proposed = await resolver.PreviewAsync(organisationId, doc, ct);
        foreach (var u in proposed.Unmet())
            messages.Add(new(ValidationSeverity.Error, $"$.capabilities['{u.Capability}']", "capability_requires", $"'{u.Capability}' needs '{u.Requires}', which is off. Turn '{u.Requires}' on, or turn '{u.Capability}' off."));
        var current = await resolver.GetAsync(organisationId, ct);
        foreach (var check in services.GetServices<ICapabilityCompatibility>())
            foreach (var f in await check.CheckAsync(organisationId, proposed, current, ct))
                messages.Add(new(f.Severity, $"$.capabilities['{f.Capability}']", f.Code, f.Message));
        return new ValidationReport(messages);
    }

    public Task ApplyAsync(Guid organisationId, string contentJson, string? previousContentJson, int releaseNumber, Guid releaseId, CancellationToken ct)
    {
        if (tenant.TenantId is { } t) { try { cache.Invalidate(t, organisationId); } catch { /* the cache key carries the release, so a failed invalidation is harmless */ } }
        return Task.CompletedTask;
    }
}
