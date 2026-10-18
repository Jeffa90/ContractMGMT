using Ecp.Operations;
using Microsoft.AspNetCore.DataProtection.KeyManagement;

namespace Ecp.Host;

/// <summary>Reads the expiry of the data-protection keys (never the keys themselves) for the operations dashboard.</summary>
internal sealed class DataProtectionKeyRingInspector(IKeyManager keys) : IKeyRingInspector
{
    public KeyRingState Inspect(DateTime nowUtc)
    {
        try
        {
            var all = keys.GetAllKeys();
            var usable = all.Where(k => !k.IsRevoked && k.ExpirationDate.UtcDateTime > nowUtc).ToList();
            return new KeyRingState(true, usable.Count, all.Count(k => k.IsRevoked), usable.Count == 0 ? null : usable.Min(k => k.ExpirationDate.UtcDateTime),
                "Keys roll automatically; a key near expiry is replaced the next time the key ring is used.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new KeyRingState(false, 0, 0, null, "The key ring could not be read (" + ex.GetType().Name + ").");
        }
    }
}
