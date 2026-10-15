# Audit Specification

Added by repository review (GAP-17). Field list: `requirements/03-data-dictionary/audit-event.csv`. Design: ADR-0011, `docs/SECURITY.md` section 8.

## 1. What is audited `[CONFIRMED]` unless tagged

Everything that changes state or grants access, with actor, source, outcome and correlation ID:

- Authentication events, role and delegation changes, elevations, unlocks.
- Every contract/supplier/document/category change (create, update, status transition, lock, unlock, archive).
- Every **authorisation denial** (rate-limited to avoid flooding). `[PROPOSED]`
- Every document download and export, including who and which classification.
- Configuration lifecycle: draft saved, validated, previewed, simulated, published, rolled back, package imported or exported.
- Migration: project, batch, dry run, mapping change, commit checkpoints, match overrides, discards, rollback, acceptance.
- Setup and onboarding wizard actions; installation re-authentication.
- Integration events: outbound sent, inbound received (signature verified or rejected), AI suggestions generated and accepted or rejected.
- Retention: legal hold placed or released, disposition approved.
- Audit access itself: Audit Explorer queries and evidence exports. `[PROPOSED]`

## 2. Properties

| Property | Requirement |
|---|---|
| Immutable | No code path updates or deletes an audit event. The application database principal has INSERT and SELECT only on the audit table. `[CONFIRMED]` |
| Tamper-evident | Per-tenant gap-free sequence and SHA-256 hash chain. A verification job and an operator command re-verify the chain and report breaks. `[PROPOSED]` |
| Time | `occurredUtc` from the platform clock in UTC; display in the viewer's configured locale/time zone. `[CONFIRMED]` |
| Old/new values | Recorded where safe. Fields whose definition is marked sensitive are replaced by a redaction marker and a hash. Secrets are never recorded. `[CONFIRMED]` |
| Correlation | Every event carries a correlation ID spanning request, background job, outbox message and integration callback. `[CONFIRMED]` |
| Batch/ release | Events from migration carry `migrationBatchId`; events under a configuration release carry `configReleaseId`. `[CONFIRMED]` |
| Completeness | An operation and its audit event are committed in the same transaction. If the audit write fails, the operation fails. `[PROPOSED]` |
| Separation | Reading audit requires `audit.read`; exporting requires `audit.export`. Audit data is tenant-scoped and subject to the same isolation as everything else. `[CONFIRMED]` |

## 3. Audit Explorer `[CONFIRMED]`

Search and filter by time range, actor, action, entity, outcome, correlation ID, batch and release; open an event with before/after values (redacted per sensitivity); follow a correlation ID across components; export results.

## 4. Evidence export `[CONFIRMED]` with `[PROPOSED]` format

Export produces a package containing: the events (JSON Lines and CSV), a manifest listing counts, time range, filters, and per-file SHA-256, the first and last chain hash with the verification result, and the exporter identity and time. The package manifest is signed (HMAC with a key from the secret provider) so tampering after export is detectable. Exports exclude data above the exporter's classification clearance and are themselves audited.

## 5. Retention of audit data `[PROPOSED]`

Audit events are retained at least as long as the longest retention policy of the entities they describe, and indefinitely until formal retention is confirmed (Confirmed Decision: keep indefinitely pending confirmation). Partitioning by month supports archive. Archiving audit data preserves the hash chain by retaining a signed chain anchor per archived partition.

## 6. Support bundles

Bundles contain logs and diagnostics only after automatic redaction; they never contain secrets, document content or business field values. A redaction test seeded with known secrets must find none in the bundle (test SEC-030).
