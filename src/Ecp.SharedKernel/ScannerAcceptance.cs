namespace Ecp.SharedKernel;

/// <summary>
/// Whether a person with the authority to do so has accepted, on the record, that the demonstration (mock) malware scanner is used for a masked-data rehearsal in a Test environment (D14.6). Provided by the Operations module so
/// that other modules can ask without depending on it. The acceptance records who, why and for what scope; it never makes the mock scanner a real one.
/// </summary>
public interface IMockScannerAcceptance
{
    /// <summary>The current acceptance, or null when there is none (or it expired or was revoked).</summary>
    Task<MockScannerAcceptanceInfo?> CurrentAsync(CancellationToken ct = default);
}

public sealed record MockScannerAcceptanceInfo(string AcceptedBy, string Reason, string Scope, DateTime AcceptedUtc, DateTime ExpiresUtc);
