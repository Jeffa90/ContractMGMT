using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ecp.Lifecycle.Definitions;
using Ecp.Lifecycle.Domain;
using Ecp.SharedKernel;
using Microsoft.Extensions.Options;

namespace Ecp.Lifecycle;

/// <summary>Manual execution: signing happens outside the platform and the owner uploads the executed copy. Implemented.</summary>
internal sealed class ManualSignatureProvider : ISignatureProvider
{
    public string Key => "manual";
    public string Label => "Manual (upload the executed copy)";
    public SignatureProviderKind Kind => SignatureProviderKind.Manual;
    public string Maturity => "Implemented";
    public string Note => "Signing happens outside the platform. Upload the executed copy when it is ready.";
    public Task<ProviderAcceptance?> CreateRequestAsync(SignatureRequestSpec spec, CancellationToken ct = default) => Task.FromResult<ProviderAcceptance?>(null);
    public Task<ProviderStatusReport?> GetStatusAsync(string providerRequestId, CancellationToken ct = default) => Task.FromResult<ProviderStatusReport?>(null);
    public Task CancelAsync(string providerRequestId, string reason, CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>The mock provider's own status words. They are the mock's vocabulary, not the platform's: <see cref="MockStatusMapper"/> maps them.</summary>
internal static class MockStatusWords
{
    public const string Sent = "mock-sent", Delivered = "mock-delivered", Completed = "mock-completed", Declined = "mock-declined", Voided = "mock-voided", Expired = "mock-expired", Failed = "mock-failed";

    public static string For(SignatureRequestStatus s) => s switch
    {
        SignatureRequestStatus.Sent => Sent, SignatureRequestStatus.Delivered => Delivered, SignatureRequestStatus.Completed => Completed, SignatureRequestStatus.Declined => Declined,
        SignatureRequestStatus.Voided => Voided, SignatureRequestStatus.Expired => Expired, _ => Failed
    };
}

internal sealed class MockStatusMapper : ISignatureStatusMapper
{
    public string ProviderKey => "mock";
    public SignatureRequestStatus? Map(string providerStatus) => providerStatus switch
    {
        MockStatusWords.Sent => SignatureRequestStatus.Sent, MockStatusWords.Delivered => SignatureRequestStatus.Delivered, MockStatusWords.Completed => SignatureRequestStatus.Completed,
        MockStatusWords.Declined => SignatureRequestStatus.Declined, MockStatusWords.Voided => SignatureRequestStatus.Voided, MockStatusWords.Expired => SignatureRequestStatus.Expired,
        MockStatusWords.Failed => SignatureRequestStatus.Failed, _ => null
    };
}

/// <summary>Mock provider for Demo and Test: simulates sending, delivery, completion (with a synthetic executed copy), decline, void, expiry and failure. Delivers nothing anywhere. Refused in Production.</summary>
internal sealed class MockSignatureProvider : ISignatureProvider, IExecutedDocumentRetriever
{
    public string Key => "mock";
    string IExecutedDocumentRetriever.ProviderKey => Key;
    public string Label => "Mock signature service (Demo and Test only)";
    public SignatureProviderKind Kind => SignatureProviderKind.Mock;
    public string Maturity => "Mocked";
    public string Note => "Simulates sending, completion, decline and void. Sends nothing.";
    public Task<ProviderAcceptance?> CreateRequestAsync(SignatureRequestSpec spec, CancellationToken ct = default)
        => Task.FromResult<ProviderAcceptance?>(new ProviderAcceptance("MOCK-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant(), SignatureRequestStatus.Sent, MockStatusWords.Sent));
    public Task<ProviderStatusReport?> GetStatusAsync(string providerRequestId, CancellationToken ct = default) => Task.FromResult<ProviderStatusReport?>(null);
    public Task CancelAsync(string providerRequestId, string reason, CancellationToken ct = default) => Task.CompletedTask;

    public Task<ExecutedContent?> RetrieveAsync(string providerRequestId, CancellationToken ct = default)
    {
        // A minimal synthetic document; it stands in for the signed copy and is labelled as such.
        var text = $"%PDF-1.4\n% SYNTHETIC EXECUTED COPY (mock signature service) request {providerRequestId}\n%%EOF\n";
        return Task.FromResult<ExecutedContent?>(new ExecutedContent(Encoding.ASCII.GetBytes(text), $"executed-{providerRequestId}.pdf", "application/pdf"));
    }
}

/// <summary>
/// Placeholder for the first live signature service. Scaffolded, Not Verified: there is no live integration and no credentials in this release, so every call fails closed with a clear message.
/// A real adapter replaces this class (it implements <see cref="ISignatureProvider"/>, and, where the service supports them, <see cref="IExecutedDocumentRetriever"/>, <see cref="ISignatureStatusMapper"/> and
/// <see cref="ISignatureCallbackVerifier"/>); the lifecycle does not change.
/// </summary>
internal sealed class UnconnectedSignatureProvider : ISignatureProvider
{
    public string Key => "external";
    public string Label => "External signature service (scaffold; not connected)";
    public SignatureProviderKind Kind => SignatureProviderKind.Live;
    public string Maturity => "Scaffolded, Not Verified";
    public string Note => "No live credentials or connection in this release.";
    private static DomainException NotConnected() => new("provider_not_configured", "The external signature service is not connected in this release. Use manual execution and upload the executed copy.");
    public Task<ProviderAcceptance?> CreateRequestAsync(SignatureRequestSpec spec, CancellationToken ct = default) => throw NotConnected();
    public Task<ProviderStatusReport?> GetStatusAsync(string providerRequestId, CancellationToken ct = default) => throw NotConnected();
    public Task CancelAsync(string providerRequestId, string reason, CancellationToken ct = default) => throw NotConnected();
}

/// <summary>
/// The common checks for a provider callback signed with a shared secret: a signature over the timestamp and the raw body, compared in constant time, and a timestamp no older (or newer) than the tolerance.
/// An adapter supplies the secret (from the secret provider, never from the request) and reads its own payload format. Replays are stopped by the inbox, which processes each provider event id once.
/// </summary>
public abstract class HmacCallbackVerifier : ISignatureCallbackVerifier
{
    public static readonly TimeSpan DefaultTolerance = TimeSpan.FromMinutes(5);
    public abstract string ProviderKey { get; }
    protected virtual string SignatureHeader => "X-Signature";
    protected virtual string TimestampHeader => "X-Signature-Timestamp";
    protected virtual TimeSpan Tolerance => DefaultTolerance;
    /// <summary>The shared secret for this provider and tenant, or null when none is configured (every callback is then refused).</summary>
    protected abstract byte[]? Secret();
    /// <summary>Reads the provider's own payload into neutral fields. Return null when the payload is not understood.</summary>
    protected abstract VerifiedProviderEvent? Parse(ProviderCallback callback);

    public CallbackVerification Verify(ProviderCallback callback)
    {
        var secret = Secret();
        if (secret is null || secret.Length == 0) return CallbackVerification.Failed("secret_not_configured");
        string? Header(string name) => callback.Headers.FirstOrDefault(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
        var signature = Header(SignatureHeader);
        var timestamp = Header(TimestampHeader);
        if (string.IsNullOrWhiteSpace(signature)) return CallbackVerification.Failed("signature_missing");
        if (string.IsNullOrWhiteSpace(timestamp) || !long.TryParse(timestamp, out var seconds)) return CallbackVerification.Failed("timestamp_missing");
        DateTime sentUtc;
        try { sentUtc = DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime; } catch (ArgumentOutOfRangeException) { return CallbackVerification.Failed("timestamp_missing"); }
        if ((callback.ReceivedUtc - sentUtc).Duration() > Tolerance) return CallbackVerification.Failed("timestamp_stale");

        byte[] provided;
        try { provided = Convert.FromHexString(signature.Trim()); } catch (FormatException) { return CallbackVerification.Failed("signature_invalid"); }
        var expected = Sign(secret, timestamp.Trim(), callback.Body);
        if (!CryptographicOperations.FixedTimeEquals(provided, expected)) return CallbackVerification.Failed("signature_invalid");

        var parsed = Parse(callback);
        return parsed is null ? CallbackVerification.Failed("payload_invalid") : CallbackVerification.Ok(parsed);
    }

    /// <summary>The signature a sender computes: HMAC-SHA256 over the timestamp, a full stop and the raw body.</summary>
    public static byte[] Sign(byte[] secret, string timestamp, byte[] body)
    {
        using var hmac = new HMACSHA256(secret);
        hmac.TransformBlock(Encoding.ASCII.GetBytes(timestamp + "."), 0, timestamp.Length + 1, null, 0);
        hmac.TransformFinalBlock(body, 0, body.Length);
        return hmac.Hash!;
    }
}

/// <summary>Verifies the mock provider's callbacks with a key set for Demo and Test (<see cref="LifecycleOptions.MockSignatureCallbackKey"/>). Without a key every mock callback is refused. Never resolved in Production.</summary>
internal sealed class MockCallbackVerifier(IOptions<LifecycleOptions> options) : HmacCallbackVerifier
{
    public override string ProviderKey => "mock";
    protected override byte[]? Secret() => string.IsNullOrEmpty(options.Value.MockSignatureCallbackKey) ? null : Encoding.UTF8.GetBytes(options.Value.MockSignatureCallbackKey);

    protected override VerifiedProviderEvent? Parse(ProviderCallback callback)
    {
        try
        {
            using var doc = JsonDocument.Parse(callback.Body);
            var r = doc.RootElement;
            string? Text(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var eventId = Text("eventId"); var requestId = Text("requestId"); var status = Text("status"); var at = Text("occurredUtc");
            if (eventId is null || requestId is null || status is null || at is null || !DateTime.TryParse(at, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var when)) return null;
            return new VerifiedProviderEvent(ProviderKey, eventId, requestId, status, when);
        }
        catch (JsonException) { return null; }
    }
}

internal sealed class SignatureProviderFactory(IEnumerable<ISignatureProvider> providers, IEnumerable<ISignatureStatusMapper> mappers, IEnumerable<ISignatureCallbackVerifier> verifiers,
    IEnumerable<IExecutedDocumentRetriever> retrievers) : ISignatureProviderFactory
{
    public ISignatureProvider Resolve(string key, EcpMode mode)
    {
        var provider = providers.FirstOrDefault(p => p.Key == key) ?? throw new DomainException("unknown_provider", "That signature provider is not recognised.");
        if (provider.Kind == SignatureProviderKind.Mock && mode == EcpMode.Production) throw new DomainException("mock_in_production", "The mock signature service is refused in Production.");
        return provider;
    }

    public IReadOnlyList<ProviderInfo> Describe(EcpMode mode) => providers.OrderBy(p => p.Kind).ThenBy(p => p.Key, StringComparer.Ordinal).Select(p =>
    {
        var refused = p.Kind == SignatureProviderKind.Mock && mode == EcpMode.Production;
        var available = !refused && !(p.Kind == SignatureProviderKind.Live && p.Maturity != "Implemented");   // a live provider that is only scaffolded cannot be chosen
        return new ProviderInfo(p.Key, p.Label, refused ? "Disabled in Production" : p.Maturity, available, p.Note, p.Kind);
    }).ToList();

    public ISignatureStatusMapper? StatusMapper(string providerKey) => mappers.FirstOrDefault(m => m.ProviderKey == providerKey);

    public ISignatureCallbackVerifier CallbackVerifier(string providerKey, EcpMode mode)
    {
        Resolve(providerKey, mode);                                                              // a mock provider's callbacks are refused in Production like the provider itself
        return verifiers.FirstOrDefault(v => v.ProviderKey == providerKey) ?? throw new DomainException("callbacks_not_supported", "That signature provider does not send callbacks to the platform.");
    }

    public IExecutedDocumentRetriever? Retriever(string providerKey) => retrievers.FirstOrDefault(r => r.ProviderKey == providerKey);
}
