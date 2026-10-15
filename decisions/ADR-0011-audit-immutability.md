# ADR-0011: Audit Immutability

Status: Proposed

## Decision
`audit.Event` is append-only: the runtime principal has INSERT and SELECT only; events carry a per-tenant gap-free sequence and a SHA-256 hash chain; events are written in the same transaction as the change; evidence exports are HMAC-signed.

## Context
Master: structured immutable audit events, old/new values where safe, correlation IDs, actor/source/outcome, evidence export with hashes. Risks: tampering (T-070), sensitive values in audit (T-071), erasure vs immutability (RISK-07).

## Decision details
- Chain: `hash = SHA256(canonicalContent || previousHash)`; writer takes a short per-tenant `sp_getapplock` so the chain cannot fork.
- Redaction: sensitive field values replaced by a marker and keyed hash; secrets never recorded.
- Privacy: erasure requests tombstone identity references via a mapping table; events are never edited.
- Verification: nightly job and operator command; chain anchors included in exports and archive partitions.
- Option, not default: SQL Server ledger tables as an additional tamper-evidence layer where the edition supports it; evaluated in M1 spike.
- Partitioning monthly planned (not initial).

## Alternatives considered
External write-once store only (adds dependency and breaks transactional atomicity); signing each event with an asymmetric key (key management cost; chain plus signed exports suffices initially).

## Consequences
- **Security:** a DBA can still alter data; the chain detects it. Documented as a limitation in the threat model.
- **Performance:** per-tenant append contention (PER-006).
- **Reversal:** low.

## Verification
AUD-001 to AUD-007.
