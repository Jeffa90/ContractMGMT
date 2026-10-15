# API and Webhooks Specification

Master requires tenant-aware APIs, OpenAPI, optimistic concurrency, outbox/inbox patterns and webhooks. This file specifies them (GAP-16). `[CONFIRMED]` detail.

## 1. REST API

| Topic | Requirement |
|---|---|
| Style | JSON over HTTPS, base path `/api/v1`, resources for each module. OpenAPI 3 document generated from code and published at `/api/v1/openapi.json`; a CI check fails on undocumented or breaking changes |
| Authentication | Bearer tokens from the configured identity provider (users) or OAuth client credentials (service principals). Cookies are used only by the UI, with antiforgery protection |
| Tenant | Derived from the token; never accepted from a path, header or body. Cross-tenant ids return 404 |
| Authorisation | Same access evaluator as the UI (`ACCESS_CONTROL_SPEC.md`). There is no "internal" API that skips it |
| Errors | RFC 9457 problem details with stable `code`, human message, correlation id; no stack traces, SQL or paths; validation errors list field codes |
| Concurrency | Resources return `ETag` (row version); updates require `If-Match`; mismatch returns 412 |
| Idempotency | `POST` operations that create or transition accept `Idempotency-Key`; repeats return the original result |
| Pagination, filtering | Cursor pagination with a maximum page size; only allowlisted filter and sort fields; no client-supplied SQL or expression text |
| Rate limiting | Per principal and per IP; 429 with `Retry-After`; stricter limits on authentication, setup, import and export endpoints |
| Uploads | Streaming; size and type limits per document type; content-type sniffing; antivirus hook; resumable chunked uploads for migration archives |
| Versioning | Additive changes within `v1`; breaking changes introduce `v2` with a deprecation window |
| Tracing | `X-Correlation-Id` accepted (validated) or generated, and returned |
| Setup | `/setup` endpoints exist only until installation completes (SETUP_AND_READINESS_SPEC.md) |
| Health | `/health/live`, `/health/ready` (dependency detail only to authorised operators), `/health/startup` |

## 2. Outbound webhooks

- Tenant-configured subscriptions: target URL (https only), event types, secret reference, organisation scope.
- Events (stable codes): `contract.created`, `contract.status_changed`, `contract.locked`, `document.added`, `approval.decided`, `variation.applied`, `reminder.due`, `migration.batch_completed`, `configuration.published`.
- Payload: event id, type, occurred time (UTC), tenant id, minimal entity reference and changed field codes; no document content and no sensitive field values by default; consumers fetch details through the API under their own authorisation.
- Delivery: via the outbox; `POST` with headers `X-ECP-Event-Id`, `X-ECP-Timestamp`, `X-ECP-Signature` (HMAC-SHA256 over timestamp + body); retries with exponential back-off and jitter up to a configured limit; then dead-letter, visible to administrators with replay; delivery log retained.
- Egress controls: DNS resolution pinned per request; private/loopback/link-local/metadata ranges refused unless allowlisted; redirects not followed; response size and time limited.

## 3. Inbound webhooks

Used for e-signature and similar callbacks. Each inbound endpoint verifies a provider signature over the raw body, rejects stale timestamps, records the event in the **inbox** keyed by `(provider, external event id)`, responds quickly, and processes asynchronously and idempotently. Failures never expose which verification step failed.

## 4. Integration documentation (deliverable DOCS-API)

API reference generated from OpenAPI, authentication guide, webhook guide with signature verification samples, error catalogue, rate limits, and the Qlik guide.
