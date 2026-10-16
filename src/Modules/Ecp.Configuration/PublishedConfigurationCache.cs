using System.Collections.Concurrent;

namespace Ecp.Configuration;

/// <summary>Counters for the cache, so a measurement or a test can see whether a read was served from memory or from the database.</summary>
public sealed record ConfigurationCacheStatistics(long Hits, long Misses, long Fallbacks, long Invalidations, int Entries);

/// <summary>
/// Remembers the published release of a configuration kind for an organisation, and anything parsed from a release, so ordinary requests do not read and parse the same JSON again.
/// The key always starts with the tenant, so one tenant can never be served another's configuration. Only <em>published</em> releases are ever stored: a draft is never cached as published.
/// A publish or rollback drops the entry before and after its commit; a reader that started before the change cannot store a stale answer (a per-key generation is checked when it stores).
/// This is a single-process cache. A deployment with several instances must either keep the short expiry (the default) or replace this class with a distributed adapter behind the same interface.
/// Any failure here is swallowed by the caller, which then reads the authoritative database.
/// </summary>
public interface IPublishedConfigurationCache
{
    bool TryGetLatest(Guid tenantId, Guid organisationId, string kind, out ReleaseDto? release);
    /// <summary>The generation to pass back to <see cref="StoreLatest"/>: if the key was invalidated in between, the store is ignored.</summary>
    long Generation(Guid tenantId, Guid organisationId, string kind);
    void StoreLatest(Guid tenantId, Guid organisationId, string kind, ReleaseDto? release, long generation);
    void Invalidate(Guid tenantId, Guid organisationId, string kind);
    /// <summary>The parsed form of a release's content, kept per release number (a release never changes, so this never goes stale).</summary>
    T GetParsed<T>(Guid tenantId, ReleaseDto release, Func<ReleaseDto, T> parse) where T : class;
    ConfigurationCacheStatistics Statistics { get; }
    void Clear();
}

public sealed class PublishedConfigurationCache(TimeProvider? time = null, TimeSpan? timeToLive = null, int maxEntries = 5000) : IPublishedConfigurationCache
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly TimeSpan _ttl = timeToLive ?? TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<(Guid Tenant, Guid Org, string Kind), (ReleaseDto? Release, DateTimeOffset Stored)> _latest = new();
    private readonly ConcurrentDictionary<(Guid Tenant, Guid Org, string Kind), long> _generations = new();
    private readonly ConcurrentDictionary<(Guid Tenant, Guid Org, string Kind, int Number, Type Type), object> _parsed = new();
    private long _hits, _misses, _fallbacks, _invalidations;

    public ConfigurationCacheStatistics Statistics => new(Interlocked.Read(ref _hits), Interlocked.Read(ref _misses), Interlocked.Read(ref _fallbacks), Interlocked.Read(ref _invalidations), _latest.Count + _parsed.Count);

    public bool TryGetLatest(Guid tenantId, Guid organisationId, string kind, out ReleaseDto? release)
    {
        var key = (tenantId, organisationId, kind);
        if (_latest.TryGetValue(key, out var entry) && _time.GetUtcNow() - entry.Stored < _ttl) { Interlocked.Increment(ref _hits); release = entry.Release; return true; }
        Interlocked.Increment(ref _misses);
        release = null; return false;
    }

    public long Generation(Guid tenantId, Guid organisationId, string kind) => _generations.GetValueOrDefault((tenantId, organisationId, kind));

    public void StoreLatest(Guid tenantId, Guid organisationId, string kind, ReleaseDto? release, long generation)
    {
        var key = (tenantId, organisationId, kind);
        if (_generations.GetValueOrDefault(key) != generation) return;        // changed while this reader was reading
        if (_latest.Count >= maxEntries) _latest.Clear();                      // a bound, not a policy: the database is always the fallback
        _latest[key] = (release, _time.GetUtcNow());
    }

    public void Invalidate(Guid tenantId, Guid organisationId, string kind)
    {
        var key = (tenantId, organisationId, kind);
        _generations.AddOrUpdate(key, 1, (_, g) => g + 1);
        _latest.TryRemove(key, out _);
        Interlocked.Increment(ref _invalidations);
    }

    public T GetParsed<T>(Guid tenantId, ReleaseDto release, Func<ReleaseDto, T> parse) where T : class
    {
        var key = (tenantId, release.OrganisationId, release.Kind, release.Number, typeof(T));
        if (_parsed.TryGetValue(key, out var found)) { Interlocked.Increment(ref _hits); return (T)found; }
        Interlocked.Increment(ref _misses);
        var value = parse(release);
        if (_parsed.Count >= maxEntries) _parsed.Clear();
        _parsed[key] = value;
        return value;
    }

    /// <summary>Counts a read that could not use the cache and went to the database because the cache failed.</summary>
    internal void CountFallback() => Interlocked.Increment(ref _fallbacks);

    public void Clear() { _latest.Clear(); _parsed.Clear(); _generations.Clear(); }
}
