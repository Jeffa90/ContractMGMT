# 07 Integrations

Added by repository review (GAP-16). Master requires provider interfaces for documents, email, reporting, e-signature, ERP, AI and monitoring, generic OpenID Connect and Microsoft Entra ID, and outbox/inbox patterns. This folder specifies them. Tags: `[CONFIRMED]` / `[CONFIRMED]` as in `requirements/README.md`.

| File | Content |
|---|---|
| `ADAPTER_CATALOGUE.md` | Every provider interface: purpose, default implementation, production implementations, failure behaviour |
| `IDENTITY_SPEC.md` | Demo identity, generic OIDC, Microsoft Entra ID, group-to-role mapping, step-up authentication |
| `ESIGNATURE_PROVIDER_SPEC.md` | E-signature provider contract (provider-neutral): signature request, provider status mapping, callbacks, inbox, executed document |
| `DOCUSIGN_ADAPTER_NOTES.md` | Notes for the future DocuSign adapter, in DocuSign's own vocabulary (provider-specific; Future) |
| `REPORTING_QLIK_SPEC.md` | External full analytics (Qlik for the JBS pack) without bypassing security |
| `API_AND_WEBHOOKS_SPEC.md` | REST API, OpenAPI, outbound and inbound webhooks |
| `NOTIFICATIONS_SPEC.md` | Email and in-app notifications, templates, reminders and escalations |

## Rules common to every adapter

1. **Optional.** No adapter is required for core operation. Each has the status `Disabled`, `Mock` or `Configured` (wizard stage 11), and the Demo/Test default is `Mock` or `Disabled`. `[CONFIRMED]`
2. **Interface in the core, implementation in the adapter.** Core code depends only on the interface; swapping an implementation changes configuration, not source. `[CONFIRMED]`
3. **Tenant-bound.** Adapter configuration, credentials references, queues, caches and logs are per tenant (and per organisation where relevant). No adapter call can use another tenant's configuration. `[CONFIRMED]`
4. **Secrets by reference.** Configuration stores a secret *reference* (`env:`, `file:`, `keyvault:`, `dpapi:`), never the value. The UI never reveals a stored secret; entry is write-only. Configuration packages exclude secrets. `[CONFIRMED]`
5. **Reliable.** Outbound effects go through the transactional outbox; inbound callbacks are recorded in the inbox keyed by provider and external id, making replays harmless. Handlers are idempotent. `[CONFIRMED]`
6. **Bounded.** Every call has timeouts, bounded retries with jittered back-off, a circuit breaker and a dead-letter state visible to administrators. Provider failure never blocks manual operation (for example manual supplier creation, manual executed-copy upload). `[CONFIRMED]`
7. **Egress-controlled.** Adapters may call only configured, https-only endpoints. Resolved addresses in private, loopback, link-local and metadata ranges are refused unless an administrator explicitly allowlists an on-premises host (SSRF control). Redirects are not followed across hosts. `[CONFIRMED]`
8. **Audited and observable.** Each call produces an audit event (without secrets or document bodies) and metrics; each adapter exposes a health check that the readiness dashboard reports.
9. **Contract-tested.** Every interface has a shared contract test suite that the mock and every real implementation must pass; real providers are exercised only in opt-in integration tests with synthetic data. `[CONFIRMED]`
10. **Data minimisation.** Payloads carry the minimum necessary data; AI and external analytics adapters respect classification and sensitivity (`RETENTION_AND_CLASSIFICATION_SPEC.md` section 6).
