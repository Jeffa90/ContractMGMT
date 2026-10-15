# ADR-0004: Migration Staging

Status: Proposed

## Decision
Stage and validate source data before target commit.

## Context
Migration is Phase 1 mandatory (`CONFIRMED_DECISIONS.md`) with non-negotiable controls: dry run performs no target writes; resumable checkpoints; deterministic matching; source values retained; reconciliation; safe rollback (`MIGRATION_REQUIREMENTS.md`). The review added identifier, mapping, rollback and legacy-load clarifications (`MIGRATION_SUPPLEMENT.md`).

## Decision details
- Separate `migration` schema and quarantine blob area; dry run has no code path to target writers (INV-40).
- Staging rows keep raw values, mapped values, row hash and an idempotency key.
- Commit in chunks; each chunk and its checkpoint commit in one transaction; unique idempotency keys make replay a no-op (INV-43).
- Provenance records (with before-images for updates) are the only basis for rollback; shared reference data is reference-counted (INV-44).
- Migration calls Contracts and Documents through public contracts; legacy-load exceptions are explicit parameters (ASM-06).
- Streaming parsers and extraction; explicit limits; formula-safe error workbooks.

## Alternatives considered
- Direct import with transactions: cannot support dry run, review or unmatched queue.
- Staging in memory/files only: not resumable across restarts or IIS recycles.
- Bulk-copy straight into target with later cleanup: violates tenant, audit and lock invariants.

## Consequences
- **Security:** hostile archives handled before any target write; cross-tenant checks at validation and commit.
- **Operations:** staging data volume and retention need policy; discard is audited (T-039).
- **Performance:** extra write amplification; budgeted in PER-003.
- **Reversal:** low; staging is internal.

## Verification
MIG-001 to MIG-027.
