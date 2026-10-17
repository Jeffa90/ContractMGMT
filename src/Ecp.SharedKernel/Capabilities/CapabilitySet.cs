namespace Ecp.SharedKernel;

/// <summary>Which layer decided whether a capability is on. Informative only.</summary>
public enum CapabilityLayer { Organisation, Pack, Platform }

/// <summary>One statement in a capability document: a capability key and whether it is on, with an optional reason.</summary>
public sealed record CapabilityEntry(string Key, bool Enabled, string? Note = null);

public sealed record ResolvedCapability(string Key, bool Enabled, CapabilityLayer Layer, string? Note);

/// <summary>An enabled capability whose required capability is off.</summary>
public sealed record UnmetRequirement(string Capability, string Requires);

/// <summary>
/// Which optional capabilities are on for one organisation, resolved once. The order is: the organisation's published capability document, then the defaults of the solution pack it was assigned, then the platform default,
/// which is that everything is on (how the platform behaved before capabilities were configurable). A set is immutable, so it can be shared and cached. A capability is operating-model configuration, not authority: turning
/// one off removes a step from the business's process; it never grants, widens or removes anyone's permission.
/// </summary>
public sealed class CapabilitySet
{
    private readonly IReadOnlyDictionary<string, ResolvedCapability> _items;

    private CapabilitySet(string fingerprint, IReadOnlyDictionary<string, ResolvedCapability> items) { Fingerprint = fingerprint; _items = items; }

    /// <summary>Identifies which organisation release and pack version this set was built from (cache keys and diagnostics).</summary>
    public string Fingerprint { get; }
    public IReadOnlyDictionary<string, ResolvedCapability> Items => _items;

    /// <summary>A capability the platform does not know is never on.</summary>
    public bool IsEnabled(string key) => _items.TryGetValue(key, out var c) && c.Enabled;
    public ResolvedCapability? Find(string key) => _items.GetValueOrDefault(key);
    public CapabilityLayer LayerOf(string key) => _items.TryGetValue(key, out var c) ? c.Layer : CapabilityLayer.Platform;
    public IReadOnlyList<string> Enabled => CapabilityKeys.All.Where(IsEnabled).ToList();
    public IReadOnlyList<string> Disabled => CapabilityKeys.All.Where(k => !IsEnabled(k)).ToList();
    public bool AllEnabled => Disabled.Count == 0;

    /// <summary>The platform default: everything on.</summary>
    public static CapabilitySet Default { get; } = Build(null, null, "default");

    public static CapabilitySet Build(IEnumerable<CapabilityEntry>? organisation, IEnumerable<CapabilityEntry>? pack, string fingerprint)
    {
        var org = (organisation ?? []).GroupBy(e => e.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
        var pk = (pack ?? []).GroupBy(e => e.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
        var items = new Dictionary<string, ResolvedCapability>(StringComparer.Ordinal);
        foreach (var def in CapabilityCatalogue.All)
        {
            if (org.TryGetValue(def.Key, out var o)) items[def.Key] = new(def.Key, o.Enabled, CapabilityLayer.Organisation, o.Note);
            else if (pk.TryGetValue(def.Key, out var p)) items[def.Key] = new(def.Key, p.Enabled, CapabilityLayer.Pack, p.Note);
            else items[def.Key] = new(def.Key, def.DefaultEnabled, CapabilityLayer.Platform, null);
        }
        return new CapabilitySet(fingerprint, items);
    }

    /// <summary>Capabilities that are on although a capability they need is off. A set with any of these is not coherent and cannot be published.</summary>
    public IReadOnlyList<UnmetRequirement> Unmet()
        => CapabilityCatalogue.All.Where(d => IsEnabled(d.Key)).SelectMany(d => d.Requires.Where(r => !IsEnabled(r)).Select(r => new UnmetRequirement(d.Key, r))).ToList();
}
