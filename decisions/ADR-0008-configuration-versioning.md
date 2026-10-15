# ADR-0008: Configuration Versioning and Releases

Status: Proposed

## Decision
Business configuration is a set of canonical-JSON **definitions**, each versioned and immutable once published, published atomically in **releases**. Runtime reads immutable release snapshots. Workflow and approval-matrix versions are pinned on instances. Rollback publishes a new release equal to an earlier one.

## Context
CLAUDE.md and Master: draft, validation, preview/simulation, publish, version history, rollback and audit. Risks: editing definitions in use (RISK-06), concurrent drafts (RISK-19), broken releases (T-043).

## Decision details
Mechanics in `requirements/08-portability/CONFIGURATION_LIFECYCLE_SPEC.md` and `docs/DOMAIN_MODEL.md` section 5 (INV-20 to INV-28). Schema validation uses JSON Schema per kind plus semantic validators shared by the Studio and the pack loader. Snapshot cache is keyed by tenant and release id; publishing emits an invalidation message. Packages create drafts only (ADR-0014).

## Alternatives considered
- Mutable current-version rows with history table: invites partial publishes and cannot guarantee in-flight stability.
- Git-style repository of definitions: powerful diff but heavy and hard for administrators.
- Per-definition publish without releases: cross-definition references can break between publishes.

## Consequences
- **Security:** immutable history supports audit and evidence; closed schemas limit injection.
- **Operations:** release storage grows; old releases are retained while referenced.
- **Reversal:** moderate.

## Verification
NCD-008, NCD-009, NCD-007, WFL-007, PKG-001.
