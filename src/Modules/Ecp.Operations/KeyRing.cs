namespace Ecp.Operations;

/// <summary>What the host can say about its data-protection key ring without exposing a key: how many keys are usable and when the nearest one expires. Never key material or identifiers.</summary>
public sealed record KeyRingState(bool Readable, int UsableKeys, int RevokedKeys, DateTime? NextExpiryUtc, string Note);

/// <summary>Lets the operations dashboard read the key ring's health. Implemented by the host (which owns data protection); a host without one leaves it unregistered and the dashboard reports Not Verified.</summary>
public interface IKeyRingInspector
{
    KeyRingState Inspect(DateTime nowUtc);
}
