using Ecp.Lifecycle.Domain;
using Ecp.SharedKernel;

namespace Ecp.Lifecycle;

// The provider-neutral boundary for External Execution. Nothing here names a signature vendor, and nothing here carries a vendor's types: a provider's own payloads, status words and
// identifiers stay inside its adapter. A future adapter implements these interfaces and registers its classes; the lifecycle engine does not change.

/// <summary>What the platform asks a provider to collect signatures on.</summary>
public sealed record SignatureRequestSpec(Guid ContractId, string Reference, string Title, IReadOnlyList<string> DocumentHashes, string IdempotencyKey, string CorrelationId);

/// <summary>A provider accepted the request. <see cref="ProviderRequestId"/> is the provider's own identifier; <see cref="ProviderStatus"/> is its own word for where the request stands.</summary>
public sealed record ProviderAcceptance(string ProviderRequestId, SignatureRequestStatus Status, string? ProviderStatus);

/// <summary>Where the provider says a request stands, in its own words. The platform maps it with <see cref="ISignatureStatusMapper"/>; it never reads it directly.</summary>
public sealed record ProviderStatusReport(string ProviderStatus, DateTime? OccurredUtc);

public sealed record ExecutedContent(byte[] Content, string FileName, string MimeType);

public enum SignatureProviderKind { Manual, Mock, Live }

/// <summary>
/// A signature provider. Manual execution (signing outside the platform) and the mock are providers too. A provider never approves, registers or locks anything and never decides the contract's state:
/// it can only report on its own request.
/// </summary>
public interface ISignatureProvider
{
    string Key { get; }
    string Label { get; }
    SignatureProviderKind Kind { get; }
    /// <summary>How mature this provider is, for display: Implemented, Mocked, or Scaffolded, Not Verified.</summary>
    string Maturity { get; }
    string Note { get; }
    /// <summary>Creates the request. Null means the provider holds no request of its own (manual execution).</summary>
    Task<ProviderAcceptance?> CreateRequestAsync(SignatureRequestSpec spec, CancellationToken ct = default);
    /// <summary>Asks the provider where a request stands (the polling fallback). Null when the provider cannot say.</summary>
    Task<ProviderStatusReport?> GetStatusAsync(string providerRequestId, CancellationToken ct = default);
    /// <summary>Cancels the request at the provider.</summary>
    Task CancelAsync(string providerRequestId, string reason, CancellationToken ct = default);
}

/// <summary>Retrieves the executed document (and any evidence) from a provider that can supply it. A provider that cannot is simply not registered here, and the executed copy is uploaded by hand.</summary>
public interface IExecutedDocumentRetriever
{
    string ProviderKey { get; }
    Task<ExecutedContent?> RetrieveAsync(string providerRequestId, CancellationToken ct = default);
}

/// <summary>
/// The one place a provider's status words become platform statuses. Unknown words return null: they are kept for an administrator to see and change nothing.
/// </summary>
public interface ISignatureStatusMapper
{
    string ProviderKey { get; }
    SignatureRequestStatus? Map(string providerStatus);
}

/// <summary>A callback as it arrived: raw bytes and headers, exactly as received. Only the provider's own verifier may read them.</summary>
public sealed record ProviderCallback(string ProviderKey, IReadOnlyDictionary<string, string> Headers, byte[] Body, DateTime ReceivedUtc);

/// <summary>An event whose authenticity has been verified and whose content has been reduced to provider-neutral fields. Only verified events reach the inbox.</summary>
public sealed record VerifiedProviderEvent(string ProviderKey, string ProviderEventId, string ProviderRequestId, string ProviderStatus, DateTime OccurredUtc);

public sealed record CallbackVerification(VerifiedProviderEvent? Event, string? FailureCode)
{
    public bool Verified => Event is not null;
    public static CallbackVerification Ok(VerifiedProviderEvent e) => new(e, null);
    public static CallbackVerification Failed(string code) => new(null, code);
}

/// <summary>Checks that a callback really came from the provider (for example a signature over the raw body with a per-tenant secret, and a fresh timestamp) and returns the verified event.</summary>
public interface ISignatureCallbackVerifier
{
    string ProviderKey { get; }
    CallbackVerification Verify(ProviderCallback callback);
}

public sealed record ProviderEventResult(ProviderEventOutcome Outcome, bool Duplicate, Guid? SignatureRequestId, SignatureRequestStatus? Status, string? Detail);

public sealed record ProviderEventInfo(Guid Id, string Provider, string ProviderEventId, string? ProviderRequestId, string ProviderStatus, ProviderEventOutcome Outcome, string? OutcomeDetail, int Attempts, DateTime OccurredUtc, DateTime ReceivedUtc, DateTime? ProcessedUtc, Guid? SignatureRequestId);

/// <summary>
/// Where verified provider events arrive. Each provider event id is processed once; a repeat is acknowledged and changes nothing. An event for an unknown request, or with a status word nobody mapped, is
/// kept and visible to an administrator and changes nothing else. Not reachable from request input: the callback endpoint verifies first.
/// </summary>
public interface ISignatureEventInbox
{
    Task<ProviderEventResult> ReceiveAsync(VerifiedProviderEvent providerEvent, CancellationToken ct = default);
    /// <summary>Events that could not be applied (unmapped status, unknown request, failure), newest first. Requires <c>audit.read_admin</c>.</summary>
    Task<IReadOnlyList<ProviderEventInfo>> ListNeedingAttentionAsync(int max = 100, CancellationToken ct = default);
}

public sealed record ProviderInfo(string Key, string Label, string Status, bool Available, string Note, SignatureProviderKind Kind);

public interface ISignatureProviderFactory
{
    /// <summary>The provider, or a refusal. A mock provider is refused in Production.</summary>
    ISignatureProvider Resolve(string key, EcpMode mode);
    IReadOnlyList<ProviderInfo> Describe(EcpMode mode);
    ISignatureStatusMapper? StatusMapper(string providerKey);
    ISignatureCallbackVerifier CallbackVerifier(string providerKey, EcpMode mode);
    IExecutedDocumentRetriever? Retriever(string providerKey);
}

public sealed record SignatureRequestInfo(
    Guid Id, string SubjectKind, Guid SubjectId, string Provider, string ProviderLabel, string? ProviderRequestId, SignatureRequestStatus Status, string? ProviderStatus, Guid? ExecutedDocumentId, DateTime? CompletedUtc,
    string RequestedBy, DateTime RequestedUtc, DateTime StatusUpdatedUtc, DateTime? LastProviderEventUtc, string? FailureCode, string? FailureMessage, string? Detail, bool IsSimulated);

public sealed record RecordExecutedCopyRequest(string SubjectKind, Guid SubjectId, string FileName, string MimeType, Stream Content, string? Classification = null);

public interface ISignatureRequestService
{
    IReadOnlyList<ProviderInfo> Providers();
    Task<SignatureRequestInfo?> CurrentAsync(string subjectKind, Guid subjectId, CancellationToken ct = default);
    /// <summary>Sends the final document for signature (or records that signing is handled outside the platform) and moves the contract to the signature stage.</summary>
    Task<SignatureRequestInfo> RequestSignatureAsync(string subjectKind, Guid subjectId, string providerKey, string? clientRequestId = null, CancellationToken ct = default);
    /// <summary>Manual execution: attaches the executed copy. It is scanned like any upload and satisfies the executed-copy check only when it is clean.</summary>
    Task<SignatureRequestInfo> RecordExecutedCopyAsync(RecordExecutedCopyRequest request, CancellationToken ct = default);
    /// <summary>Asks the provider where the current request stands and applies the answer through the status mapper (the polling fallback). A provider that cannot say changes nothing.</summary>
    Task<SignatureRequestInfo> RefreshStatusAsync(string subjectKind, Guid subjectId, CancellationToken ct = default);
    /// <summary>Cancels the current request at the provider and ends it as Voided. The contract's state does not change: the owner takes the document back to drafting as a separate step.</summary>
    Task<SignatureRequestInfo> CancelAsync(string subjectKind, Guid subjectId, string reason, CancellationToken ct = default);
    /// <summary>Mock provider only: reports a signing event as the service would, through the same inbox path a real provider's event takes. Refused in Production.</summary>
    Task<SignatureRequestInfo> SimulateEventAsync(string subjectKind, Guid subjectId, SignatureRequestStatus status, CancellationToken ct = default);
}
