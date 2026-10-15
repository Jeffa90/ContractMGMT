---
name: migration-review
description: Review the bulk migration and attachment-ingestion implementation: source profiling, mapping, dry run, idempotency, deterministic matching, unmatched handling, legacy-load and Registered Date exception, reconciliation, rollback, provenance and cutover evidence. Use after any change under the Migration module or its samples.
---

# Migration Review

Migration is Phase 1 mandatory. Review against the authoritative files, then the supplement.

## Read first
1. `requirements/09-migration/MIGRATION_REQUIREMENTS.md`, `ATTACHMENT_INGESTION.md`, `SOURCE_TO_TARGET_MAPPING.md`, `MEDIUS_IMPORT_PROFILE.md`, `CUTOVER_AND_RECONCILIATION.md`, then `MIGRATION_SUPPLEMENT.md`
2. `requirements/03-data-dictionary/migration-entities.csv`, `documents.csv`, `contracts-extended.csv`
3. `migration-samples/` (original and `*-complete.csv`, `status-lookup-map.csv`)
4. `docs/TESTING.md` section 7 (MIG-001 to MIG-027), `docs/DOMAIN_MODEL.md` (INV-40 to INV-49)

## Procedure
1. Run the end-to-end sample (`MIG-018`): 2 contracts, 3 manifest rows, ZIP built by the fixture builder. Record exact commands and results.
2. Walk the nine Master critical tests (MIG-001 to MIG-009) and confirm each runs against real SQL Server where the claim depends on SQL behaviour. Anything skipped is **NOT VERIFIED**, never Passed.
3. Run the checklist; record Pass / Fail / Not verified.

## Checklist
- **Dry run (MIG-001, INV-40):** snapshot of every non-staging table, blob area, outbox, notifications and reminders is identical before and after; the dry-run code path cannot reference commit writers.
- **Idempotency (MIG-002, MIG-017, INV-42/43):** key `(TenantId, OrganisationId, SourceSystem, SourceRecordId)` unique; chunk and checkpoint commit atomically; kill and resume at each phase boundary creates no duplicates of records or files.
- **Matching (MIG-003):** order is SourceRecordId, AgreementReference, naming rule, folder name, manual queue; deterministic regardless of input order; each proposal shows method and reason; no probabilistic match without human confirmation; naming-rule regex is non-backtracking with a timeout (MIG-019).
- **Unmatched (MIG-005, INV-49):** unmatched and failed files remain visible in restricted staging; discard is an audited action; counts reconcile.
- **Duplicates (MIG-004, MIG-025):** each strategy behaves as specified; `Create new version` applies to documents only; generated references avoid collisions; contract `agreementReference` is unique per tenant; the original source reference is preserved separately in `sourceAgreementReference`.
- **Mapping (MIG-014, MIG-020):** profile versions immutable once used; preview shows before/after and affected-row counts; only catalogue transformations exist; no truncation (`MIG-E009`).
- **Legacy load and Registered Date (MIG-015):** needs `migration.legacy_load` and policy; imported Registered Date keeps provenance (batch id, source record id, verbatim source value, acting administrator audited, `registeredDateProvenance = Migration`); after acceptance users cannot edit it; locked records are never overwritten without explicit policy and audit.
- **Provenance (MIG-007, INV-41):** batch id on every row, file link and audit event; hash, source id, source status, extraction time and mapping profile version stored; configuration release captured (MIG-023).
- **Reconciliation (MIG-009, INV-45):** identities in `MIGRATION_SUPPLEMENT.md` section 11 hold, or exceptions are approved and recorded; business acceptance recorded.
- **Rollback (MIG-006, MIG-016, MIG-022, INV-44):** selects by batch provenance only; never touches pre-existing records, other batches, or shared suppliers/categories/reference data still referenced; restores before-images only if unchanged; blocked after acceptance; compensating archival path exists.
- **Safety (MIG-012/013, SEC-012 to SEC-015):** traversal, bombs, macros, encodings, BOM; formula-safe error workbooks (MIG-021).
- **Isolation (MIG-008, TEN-010):** cross-tenant and cross-organisation references rejected at validation and again at commit.
- **Cutover (MIG-026):** freeze window recorded; evidence for owner acceptance exportable.

## Severity
Critical: cross-tenant link, data loss on rollback, dry run writing target. High: duplicates on resume, silent drops, unaudited legacy load. Medium: unclear messages, profile gaps. Low: documentation.

## Output
Findings table, exact commands and results of the sample run, and the list of NOT VERIFIED items with reason.
