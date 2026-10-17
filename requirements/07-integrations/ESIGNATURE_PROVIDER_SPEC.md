# E-signature Provider Specification (provider-neutral)

Confirmed (decision record DR-2026-10-15 D2, resolving Q-01): for the JBS Procurement pack, the signature provider handles Delegation of Authority, routing for execution and signatures; DocuSign is that pack's chosen provider. **The platform does not replace or duplicate the provider's Delegation of Authority.** After execution the signed agreement is uploaded, or retrieved through a provider adapter, and the platform performs a separate register-authorisation step by the configured approver group (for the JBS pack, the Group Head of Procurement or the configured backup approver). This file defines the contract between the platform and **any** signature provider (GAP-16, CON-02). The platform's vocabulary is provider-neutral (ADR-0027); a provider's own terms live only in its adapter and in its adapter notes (`requirements/07-integrations/DOCUSIGN_ADAPTER_NOTES.md`).

Status: the manual provider and the mock provider are **implemented**; the provider-neutral interfaces, status mapper, callback verification helper, event inbox and executed-document retrieval are **implemented** (S3). A live adapter (DocuSign is the first intended) is **future**; the callback HTTP endpoint is **future**. Open point: Q-09 (callbacks vs polling, templates).

## 1. Vocabulary

| Platform term | Meaning |
|---|---|
| Signature Request | A request to a provider to collect signatures on a contract's (or Variation's) final document. One is current per subject; an earlier one is superseded |
| Signature Provider | The service (or the manual route) that collects the signatures |
| Provider Request ID | The provider's own identifier for the request. Empty until the provider accepts it (and always empty for manual execution) |
| Signature Request Status | Platform words only: **Sent** (shown as "Sent for signature"), **Delivered**, **Completed**, **Declined**, **Voided**, **Expired**, **Failed**. Completed, Declined, Voided, Expired and Failed are final |
| Provider status | The provider's own word, kept separately and informational. It never controls the platform status |
| Executed Document | The signed copy, stored as an executed-copy document through the normal document rules |
| Provider Event | One verified notification from a provider about one request |
| Provider Callback | The provider's HTTP delivery of a Provider Event, before verification |
| External Execution | The lifecycle step during which a signature request is out. Legal clearance, External Execution and register authorisation are three separate events |

Not platform terms: envelope, envelope id, recipient tabs, signing ceremony, signature tabs, Connect. They may appear only in an adapter and its notes.

## 2. Responsibilities

| Concern | Owner |
|---|---|
| Delegation of Authority, signing routing and collecting signatures | The provider `[CONFIRMED]`; the platform does not replace or duplicate it |
| Holding the executed copy and any certificate of completion | Platform document store (copied from the provider, through the normal document rules) |
| Final register authorisation (`Approve`), stamping Registered Date, becoming Effective, locking | Platform, by an authenticated approver `[CONFIRMED]`; the acting individual and decision are retained |
| Who may perform register authorisation | The approver group in the organisation's workflow definition (for the JBS pack, `GroupHeadProcurement` with the configured backup approver) `[CONFIRMED]`. No financial approval tiers are inferred |

A completed signature request **never** approves, registers, locks or makes Effective a contract by itself; it satisfies the executed-copy guard only (`ExecutedCopyExists`).

## 3. Interfaces (`Ecp.Lifecycle`)

| Interface | Purpose |
|---|---|
| `ISignatureProvider` | `CreateRequestAsync(spec)` returns the provider's acceptance (Provider Request ID, initial status, provider status) or null for the manual route; `GetStatusAsync(providerRequestId)` is the polling fallback; `CancelAsync(providerRequestId, reason)`. Also carries `Key`, `Label`, `Kind` (Manual, Mock, Live), `Maturity` and `Note` for honest display |
| `IExecutedDocumentRetriever` | Returns the executed document bytes, file name and type for a Provider Request ID. A provider that cannot supply them simply does not register one; the owner uploads the copy by hand |
| `ISignatureStatusMapper` | The only place a provider's words become platform statuses. An unknown word returns nothing |
| `ISignatureCallbackVerifier` | Verifies a `ProviderCallback` (raw body and headers) and returns a `VerifiedProviderEvent` (provider key, provider event id, Provider Request ID, provider status, occurred UTC). A base class, `HmacCallbackVerifier`, supplies the common checks: HMAC-SHA256 over timestamp and raw body, constant-time compare, timestamp freshness (5 minutes) |
| `ISignatureEventInbox` | `ReceiveAsync(verifiedEvent)` and `ListNeedingAttentionAsync()` (needs `audit.read_admin`). Each provider event id is processed once |
| `ISignatureRequestService` | `RequestSignatureAsync`, `RecordExecutedCopyAsync` (manual), `RefreshStatusAsync` (polling), `CancelAsync`, `SimulateEventAsync` (mock only), `CurrentAsync`, `Providers` |

No interface requires a vendor type. A provider's payloads stay inside its adapter. A new adapter registers its provider, mapper, verifier and retriever; the lifecycle engine does not change (proved by `A_new_adapter_with_its_own_words_runs_end_to_end_with_no_change_to_the_lifecycle`).

## 4. Flow

1. The owner chooses **Send for signature** (guard `FinalDocumentExists`). The service asks the provider to create the request (or, for the manual route, records that signing happens outside) and the contract moves to the signature stage (`Out for Signature`). The request records the provider key, Provider Request ID (if any), status Sent, who requested it and when, the correlation id and the client request id (the idempotency key).
2. The provider reports progress by callback (future endpoint `POST /api/v1/integrations/signature/{providerKey}/callback`) or by polling. A callback is verified first; only then is it reduced to a `VerifiedProviderEvent` and given to the inbox.
3. The inbox records the event (unique per tenant, provider and provider event id), finds the request by Provider Request ID within the tenant, maps the provider's word, and applies the result: Delivered, Declined, Voided, Expired or Failed update the request and are audited. An event that is older than what is recorded, for a request that has ended, or for a superseded request is **Ignored**. An unknown word is **Unmapped**: the request's platform status is unchanged, the provider's word is kept, and an administrator can see it. An event for no known request is **Unknown request** and reveals nothing about other tenants.
4. On Completed, the executed document is retrieved and stored through `IDocumentService` (type, size, scan, hash, classification and storage rules), the request becomes Completed and links to it, and the owner can submit for register authorisation. If the document is refused or unavailable the request records a failure code and sanitised message, stays where it was, and the event is marked **Failed** and visible; the provider's repeat delivery is processed again.
5. Declined, Voided, Expired or Failed raise guard `SignatureVoidedOrDeclined` (the name is kept for pack compatibility; it now also covers Expired and Failed, which did not exist before), enabling the `Return to drafting` transition. Cancelling a request ends it as Voided and changes nothing else.
6. Approval proceeds in the platform: the approver sees the executed copy and performs `Approve` or `Reject` as an authenticated user.

## 5. Security requirements

- A callback is accepted only with a valid signature over the raw body using a per-tenant secret held by reference (never in a request or a log), a fresh timestamp, and an event id not seen before. Unsigned, stale or replayed events are refused and audited. `[CONFIRMED]` (Master: approval spoofing)
- The platform **never** trusts signer names, emails or approver identities in a callback to attribute a platform approval.
- The Provider Request ID to contract mapping is stored server-side per tenant; an event for an unknown request or another tenant's request is recorded without revealing which.
- Provider-native payloads and raw callbacks are not stored by the platform; the inbox keeps only neutral fields and a sanitised outcome.
- Provider text is bounded and stripped of control characters before it is stored; a provider status is never rendered as markup.
- Executed content is scanned and hashed before it can satisfy any guard.
- The mock provider, its callback verifier and simulated events are refused in Production.

## 6. Mock provider (Demo/Test default)

Simulates sending, delivery, completion (generating a synthetic executed PDF), decline, void, expiry and failure on demand from the UI or tests, through the same inbox and mapper a real provider's events take. Its words are its own (`mock-sent`, `mock-completed` and so on) so the mapper is exercised. It is labelled "Mock" and passes the same contract tests as any adapter.

## 7. Open points

- Q-01 is resolved (D2): the platform neither replaces nor duplicates the provider's Delegation of Authority; evidence from the provider may be attached as a document but is not re-evaluated by the platform.
- Q-09 `[OPEN]`: callbacks versus polling for the first live adapter, request templates, signer roles and routing, and who may view Delegation of Authority evidence.
