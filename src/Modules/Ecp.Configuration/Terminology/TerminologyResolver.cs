using System.Collections.Concurrent;
using System.Globalization;
using Ecp.Configuration.Domain;
using Ecp.Configuration.Persistence;
using Ecp.Persistence;
using Ecp.SharedKernel;
using Ecp.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Ecp.Configuration;

/// <summary>
/// The one place generic pages, messages and exports ask what to call a business concept. It reads the organisation's published terminology, the terminology of the solution pack the organisation works from and
/// the platform's neutral wording, in that order, and returns a set that is resolved once for a whole page. It never changes what anyone may do: a label is text, not authority. It fails safe: if anything cannot be
/// read it answers with the neutral wording, never an error and never another tenant's labels.
/// </summary>
public interface ITerminologyResolver
{
    /// <summary>The terminology for an organisation (or, with no organisation, for the installation default pack) in a locale (default: the current UI culture).</summary>
    Task<TermSet> GetAsync(Guid? organisationId, string? locale = null, CancellationToken ct = default);

    /// <summary>Several terms in one operation: the keys are looked up in a single resolved set, never one query per label.</summary>
    Task<IReadOnlyDictionary<string, ResolvedTerm>> ResolveManyAsync(Guid? organisationId, IEnumerable<string> keys, string? locale = null, CancellationToken ct = default);

    /// <summary>What an organisation would see if a draft were published: the same layering, with the draft in place of the published release. Nothing is cached.</summary>
    Task<TermSet> PreviewAsync(Guid organisationId, TerminologyDocument draft, string? locale = null, CancellationToken ct = default);

    /// <summary>The pack-provided terminology for an organisation (the layer beneath its own), or null if the pack carries none. Used by the administration pages to show what is inherited.</summary>
    Task<PackTerminology?> PackLayerAsync(Guid? organisationId, CancellationToken ct = default);
}

public sealed record PackTerminology(string PackKey, string PackVersion, TerminologyDocument Document);

public sealed record TerminologyCacheStatistics(long Hits, long Misses, long Fallbacks, int Entries);

/// <summary>
/// Remembers resolved sets. The key carries the tenant, the organisation, the locale and the published version (the release and the pack identity), so a different tenant, organisation, locale or release can never
/// be served another's labels, and a publish or rollback is seen at once because it changes the release in the key. A failure of the cache is never an error: the caller resolves from persistence.
/// This is a single-process cache with a bound; a deployment with several instances relies on the release in the key and the short expiry.
/// </summary>
public interface ITerminologyCache
{
    bool TryGet(Guid tenantId, Guid organisationId, string locale, string version, out TermSet? set);
    void Store(Guid tenantId, Guid organisationId, string locale, string version, TermSet set);
    void Invalidate(Guid tenantId, Guid organisationId);
    TerminologyCacheStatistics Statistics { get; }
    void CountFallback();
    void Clear();
}

public sealed class TerminologyCache(TimeProvider? time = null, TimeSpan? timeToLive = null, int maxEntries = 2000) : ITerminologyCache
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly TimeSpan _ttl = timeToLive ?? TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<(Guid Tenant, Guid Org, string Locale, string Version), (TermSet Set, DateTimeOffset Stored)> _sets = new();
    private long _hits, _misses, _fallbacks;

    public TerminologyCacheStatistics Statistics => new(Interlocked.Read(ref _hits), Interlocked.Read(ref _misses), Interlocked.Read(ref _fallbacks), _sets.Count);

    public bool TryGet(Guid tenantId, Guid organisationId, string locale, string version, out TermSet? set)
    {
        if (_sets.TryGetValue((tenantId, organisationId, locale, version), out var e) && _time.GetUtcNow() - e.Stored < _ttl) { Interlocked.Increment(ref _hits); set = e.Set; return true; }
        Interlocked.Increment(ref _misses); set = null; return false;
    }

    public void Store(Guid tenantId, Guid organisationId, string locale, string version, TermSet set)
    {
        if (_sets.Count >= maxEntries) _sets.Clear();               // a bound, not a policy: persistence is always the fallback
        _sets[(tenantId, organisationId, locale, version)] = (set, _time.GetUtcNow());
    }

    public void Invalidate(Guid tenantId, Guid organisationId)
    {
        foreach (var key in _sets.Keys.Where(k => k.Tenant == tenantId && k.Org == organisationId).ToList()) _sets.TryRemove(key, out _);
    }

    public void CountFallback() => Interlocked.Increment(ref _fallbacks);
    public void Clear() => _sets.Clear();
}

internal sealed class TerminologyResolver(IConfigurationReleases releases, IOrganisationStructureService organisations, IPackLoader packs, ConfigurationDbContext db, ITenantContext tenant,
    ITerminologyCache cache, ConfigurationOptions options, ILogger<TerminologyResolver> logger) : ITerminologyResolver
{
    private static string LocaleOrCurrent(string? locale) => string.IsNullOrWhiteSpace(locale) ? CultureInfo.CurrentUICulture.Name is { Length: > 0 } n ? n : "en" : locale;

    public async Task<TermSet> GetAsync(Guid? organisationId, string? locale = null, CancellationToken ct = default)
    {
        var loc = LocaleOrCurrent(locale);
        if (tenant.TenantId is not { } tenantId) return TermSet.Neutral(loc);
        var org = organisationId ?? Guid.Empty;
        try
        {
            var release = org == Guid.Empty ? null : await releases.LatestAsync(TerminologyKind.KindName, org, ct);
            var pack = await PackLayerAsync(organisationId, ct);
            var version = $"{release?.Id.ToString("N") ?? "-"}|{pack?.PackKey ?? "-"}@{pack?.PackVersion ?? "-"}";
            try { if (cache.TryGet(tenantId, org, loc, version, out var hit) && hit is not null) return hit; }
            catch (Exception ex) { cache.CountFallback(); logger.LogWarning(ex, "The terminology cache could not be read; resolving from persistence."); }
            var set = TermSet.Build(loc, release is null ? null : TerminologyDocument.Parse(release.ContentJson).Terms, pack?.Document.Terms, release is null ? null : $"r{release.Number}", pack is null ? null : $"{pack.PackKey}@{pack.PackVersion}");
            try { cache.Store(tenantId, org, loc, version, set); }
            catch (Exception ex) { cache.CountFallback(); logger.LogWarning(ex, "The terminology cache could not be written; the set was resolved from persistence."); }
            return set;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Terminology is presentation only. If it cannot be read, say so in the log (no labels, no identifiers beyond the correlation) and show the neutral wording.
            logger.LogWarning(ex, "Terminology could not be resolved; the neutral wording is used.");
            return TermSet.Neutral(loc);
        }
    }

    public async Task<IReadOnlyDictionary<string, ResolvedTerm>> ResolveManyAsync(Guid? organisationId, IEnumerable<string> keys, string? locale = null, CancellationToken ct = default)
    {
        var set = await GetAsync(organisationId, locale, ct);
        var result = new Dictionary<string, ResolvedTerm>(StringComparer.Ordinal);
        foreach (var key in keys.Distinct(StringComparer.Ordinal))
            result[key] = set.Find(key) ?? new ResolvedTerm(key, key, key, null, null, null, TermLayer.Unknown, null);
        return result;
    }

    public async Task<TermSet> PreviewAsync(Guid organisationId, TerminologyDocument draft, string? locale = null, CancellationToken ct = default)
    {
        var loc = LocaleOrCurrent(locale);
        var pack = await PackLayerAsync(organisationId, ct);
        return TermSet.Build(loc, draft.Terms, pack?.Document.Terms, "preview", pack is null ? null : $"{pack.PackKey}@{pack.PackVersion}");
    }

    public async Task<PackTerminology?> PackLayerAsync(Guid? organisationId, CancellationToken ct = default)
    {
        try
        {
            string? packKey = null;
            if (organisationId is { } o && o != Guid.Empty)
            {
                var summary = await organisations.GetOrganisationAsync(o, ct);
                packKey = summary?.SolutionPackKey;
            }
            if (string.IsNullOrWhiteSpace(packKey)) packKey = options.DefaultPackId;
            if (string.IsNullOrWhiteSpace(packKey)) return null;
            string? version = null;
            if (organisationId is { } org && org != Guid.Empty)
                version = await db.PackAssignments.AsNoTracking().Where(a => a.OrganisationId == org && a.PackKey == packKey && a.Status == PackAssignment.Active).Select(a => a.Version).FirstOrDefaultAsync(ct);
            var entry = await packs.FindAsync(packKey, version, usableOnly: version is null, ct);
            if (entry is null || entry.Unavailable || !entry.IsCompatible || !entry.Trust.IsAcceptable) return null;
            var json = await packs.GetDocumentAsync(entry.PackKey, entry.Version, TerminologyKind.KindName, ct);
            if (json is null) return null;
            var doc = TerminologyDocument.Parse(json);
            return new PackTerminology(entry.PackKey, entry.Version, doc);
        }
        catch (Exception ex) when (ex is DomainException or System.Text.Json.JsonException)
        {
            logger.LogWarning(ex, "The solution pack's terminology could not be read; the platform wording is used beneath the organisation's own.");
            return null;
        }
    }
}

/// <summary>The terminology configuration kind: versioned, validated, previewed, published, rolled back and audited by the ordinary configuration lifecycle. It has its own permissions so it can be delegated on its own.</summary>
internal sealed class TerminologyKind(ITerminologyCache cache, ITenantContext tenant) : IConfigurationKind
{
    public const string KindName = TerminologyKinds.Name;
    public string Kind => KindName;
    public string DraftPermission => "terminology.draft";
    public string PublishPermission => "terminology.publish";
    public string RollbackPermission => "terminology.rollback";
    public string HistoryPermission => "terminology.read";

    public Task<ValidationReport> ValidateAsync(Guid organisationId, string contentJson, CancellationToken ct)
    {
        try { return Task.FromResult(new ValidationReport(TerminologyDocument.Parse(contentJson).Check().ToList())); }
        catch (DomainException ex) { return Task.FromResult(new ValidationReport([new ValidationMessage(ValidationSeverity.Error, "$", ex.Code, ex.Message)])); }
    }

    /// <summary>Publishing changes only what the cache holds; the release is the authority, and the cache key carries it.</summary>
    public Task ApplyAsync(Guid organisationId, string contentJson, string? previousContentJson, int releaseNumber, Guid releaseId, CancellationToken ct)
    {
        if (tenant.TenantId is { } t) { try { cache.Invalidate(t, organisationId); } catch { /* a failed invalidation is harmless: the key carries the release */ } }
        return Task.CompletedTask;
    }
}
