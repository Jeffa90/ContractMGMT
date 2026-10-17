# DocuSign Adapter Notes (provider-specific, future)

Status: **Future. Not implemented, not verified against any account.** These notes are for whoever builds the DocuSign adapter. DocuSign is one optional signature provider; the first intended for the JBS Procurement pack (decision D2). Everything below uses DocuSign's own words and **must stay inside the adapter and its configuration page**: none of it may become a platform entity, column, interface, route, configuration key or label. The platform's contract is `requirements/07-integrations/ESIGNATURE_PROVIDER_SPEC.md`.

## 1. Vocabulary mapping

| DocuSign term | Platform term |
|---|---|
| Envelope | Signature Request |
| Envelope ID | Provider Request ID |
| Envelope status (`created`, `sent`, `delivered`, `completed`, `declined`, `voided`) | Provider status (kept as received) |
| DocuSign Connect | The provider's callback mechanism (Provider Callback) |
| Connect event id / HMAC signature header | Provider event id / verified by the adapter's `ISignatureCallbackVerifier` |
| Recipients, signing groups, routing order, tabs | Adapter configuration; not modelled by the platform |
| Certificate of Completion | Evidence document stored with the executed document (type `ApprovalEvidence`) |

## 2. Status mapping the adapter's `ISignatureStatusMapper` should use

| DocuSign status word | Platform status |
|---|---|
| `sent` | Sent |
| `delivered` | Delivered |
| `completed` | Completed |
| `declined` | Declined |
| `voided` | Voided |
| `created` | (not mapped: the request is not out yet) |
| anything else | Unmapped: kept visible, changes nothing |

Expiry and failure are reported by DocuSign through other means (envelope expiry, Connect failures); the adapter maps them to Expired and Failed.

## 3. Adapter responsibilities

- Authenticate with OAuth (JWT grant or authorisation code); the secret reference is held in the secret provider. The account id and base URI are tenant configuration.
- Create the envelope from the final documents' content (the platform passes document hashes and a reference, and the adapter fetches the content through its own authorised route), return the envelope id as the Provider Request ID.
- Verify Connect callbacks: HMAC over the raw body with the per-tenant secret (derive from `HmacCallbackVerifier`, overriding the header names and payload reading), refuse stale (more than 5 minutes) or unsigned requests, and reduce the payload to a `VerifiedProviderEvent`. DocuSign's XML or JSON payload never leaves the adapter.
- Poll `GetStatusAsync` as the fallback when Connect is not configured.
- Implement `IExecutedDocumentRetriever` to fetch the combined signed documents and the certificate.
- Never trust signer names or emails in a callback to attribute a platform approval.

## 4. Open points (Q-09)

Connect webhooks versus polling, envelope templates, signer roles and routing, and who may view Delegation of Authority evidence. Decide these when the adapter is built, record the decision in its own ADR, and keep the platform interfaces unchanged.
