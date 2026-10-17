using System.Collections.Concurrent;
using Ecp.Packs;

namespace Ecp.Configuration;

/// <summary>
/// Remembers verified, immutable tenant packages by tenant and content hash so they are not re-read and re-parsed on every request. The tenant is part of every key, so one tenant's package is never
/// served for another. It is only an optimisation: a miss, a failure or an eviction falls back to authoritative storage, and the entry's status (retired or not) is never cached.
/// </summary>
public sealed class PackPackageCache
{
    private readonly ConcurrentDictionary<(Guid Tenant, string Sha256), OpenedPackage> _items = new();
    private const int MaxItems = 64;

    public bool TryGet(Guid tenant, string sha256, out OpenedPackage package)
    {
        try { return _items.TryGetValue((tenant, sha256), out package!); }
        catch { package = null!; return false; }
    }

    public void Set(Guid tenant, string sha256, OpenedPackage package)
    {
        try
        {
            if (_items.Count >= MaxItems) _items.Clear();
            _items[(tenant, sha256)] = package;
        }
        catch { /* the cache is an optimisation; storage is the authority */ }
    }

    public void Invalidate(Guid tenant, string sha256) { try { _items.TryRemove((tenant, sha256), out _); } catch { } }
    public void InvalidateTenant(Guid tenant) { try { foreach (var key in _items.Keys.Where(k => k.Tenant == tenant).ToList()) _items.TryRemove(key, out _); } catch { } }
    public int Count(Guid tenant) => _items.Keys.Count(k => k.Tenant == tenant);
    public int Count() => _items.Count;
}
