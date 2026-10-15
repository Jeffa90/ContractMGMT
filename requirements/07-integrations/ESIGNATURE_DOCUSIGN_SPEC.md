# E-signature and DocuSign Specification

Confirmed: "DocuSign initially handles execution and Delegation of Authority." The Group Head or backup performs final register authorisation after execution. This file defines the contract between the platform and e-signature providers (GAP-16, CON-02). `[PROPOSED]` detail unless tagged; open points are Q-01 and Q-09.

## 1. Responsibilities

| Concern | Owner |
|---|---|
| Collecting signatures, signing order and Delegation of Authority evidence | DocuSign (initially) `[CONFIRMED]` |
| Holding the executed copy and certificate of completion | Platform document store (copied from DocuSign) |
| Final register authorisation (`Approve`), stamping Registered Date, locking | Platform, by an authenticated approver `[CONFIRMED]` |
| Who may approve | Platform approval matrix (approver groups) |

DocuSign completion **never** approves, registers or locks a contract by itself; it satisfies guard `ExecutedCopyExists` only.

## 2. Provider interface `IESignatureProvider`

| Operation | Behaviour |
|---|---|
| `CreateEnvelope(contractId, documentVersionIds, signers, options)` | Creates and sends an envelope from final documents; returns provider envelope id; stored with the contract |
| `GetStatus(envelopeId)` | Polling fallback |
| `FetchExecuted(envelopeId)` | Returns executed documents and certificate of completion as streams |
| `Void(envelopeId, reason)` | Voids an envelope (audited) |
| `VerifyCallback(request)` | Verifies authenticity and returns a normalised event (`Sent`, `Delivered`, `Completed`, `Declined`, `Voided`) |

## 3. Flow

1. Owner triggers `Send for signature` (guard `FinalDocumentExists`). The adapter creates the envelope (outbox message) and the contract moves to `Out for Signature`.
2. DocuSign Connect (or polling) delivers events to `POST /api/v1/integrations/esignature/{providerKey}/callback`. The callback is authenticated by the provider's HMAC signature, checked for timestamp freshness, written to the **inbox** keyed by envelope id and event id, then processed idempotently.
3. On `Completed`, a background job fetches executed documents and the certificate, stores them as new document versions (`isExecutedCopy = true`; certificate as `ApprovalEvidence`), computes SHA-256, and runs malware scanning like any upload. The owner is notified that the contract is ready to `Submit approval`.
4. `Declined` or `Voided` raises guard `SignatureVoidedOrDeclined`, enabling the `Return to drafting` supplemental transition and a notification.
5. Approval proceeds in the platform: the approver sees the executed copy and DoA evidence and performs `Approve` or `Reject` as an authenticated user.

## 4. Security requirements

- The callback endpoint accepts only requests with a valid HMAC over the raw body using a per-tenant secret reference; unsigned, stale (older than 5 minutes) or replayed events are rejected and audited. `[CONFIRMED]` (Master: approval spoofing)
- The platform **never** trusts signer names, emails or approver identities in a callback to attribute a platform approval.
- Envelope-to-contract mapping is stored server-side per tenant; a callback for an unknown envelope or another tenant's envelope is rejected without revealing which.
- The adapter uses OAuth (JWT grant or authorization code) with the secret reference held in the secret provider. The DocuSign account id and base URI are tenant configuration.
- Executed content is scanned and hashed before it can satisfy any guard.

## 5. Mock provider (Demo/Test default)

Simulates sending, delivery, completion (generating a synthetic executed PDF and certificate), decline and void on demand from the UI or tests. It passes the same contract tests as the DocuSign adapter and is labelled "Mock".

## 6. Open points

- Q-01: whether the platform approval step replaces, supplements or mirrors DocuSign Delegation of Authority (ASM-08 assumes supplements: DoA evidence is attached; Group Head authorisation is platform-side).
- Q-09: Connect webhooks vs polling, envelope templates, signer roles and routing, and who may view DoA evidence.
