# ADR-0018: Migration slice design (staging, provenance ledger, resumable commit, provenance-driven rollback)

Status: Proposed (accepted with the T0.3 pass)

## Context

The migration capability must load a legacy register and its attachments into a tenant safely, repeatably and reversibly, executable by a business administrator through the application, without code, and without ever weakening tenant, role, lifecycle or lock rules (`prompts/10-MIGRATION-VERTICAL-SLICE.md`, `requirements/09-migration/*`). The DataMigration module sits at tier 7 and may reference Identity, Configuration, Contracts and Documents but not their persistence.

## Decision

1. **Staging keeps the raw source.** Every source row, manifest row and archive file is stored exactly as read, tenant-owned and covered by RLS and composite foreign keys. Mapping, validation and planning recompute from staging; staging is never mutated by a dry run. Re-uploading identical content changes nothing; different content replaces the earlier file and withdraws any confirmation.
2. **Closed mapping language.** Profiles are versioned data (field mappings CSV, lookup CSV, options). Only the transformation catalogue exists; there is no expression, SQL, script or backtracking pattern. A published version is immutable; a batch pins a published version and the configuration release in force.
3. **Plan, then apply.** The dry run produces a plan per row (create, update, skip, reject, needs decision) and per file. The plan is fingerprinted. A legacy load is committed only when a second person has confirmed the fingerprint; any change to inputs or decisions invalidates it.
4. **Writes go through ports, not tables.** Contracts and Documents expose `IContractMigrationPort` and `IDocumentMigrationPort` (find, create, update with before-image, archive, remove, count, summarise). The migration never touches those schemas directly; each port call joins the migration's unit of work, so audit events (hash-chained, carrying the batch id) commit with the data.
5. **Resumable commit.** Work is chunked (default 500) with one transaction and one checkpoint per chunk, in two phases (`Contracts`, then `Documents`). A lease prevents concurrent runs; an expired lease allows a resume from the last checkpoint. Idempotency keys (source identity) make a repeated chunk a no-op. A failure inside a chunk rolls the chunk back (the unit of work gained an `IRollbackHook` to discard buffered audit and reset change trackers) and records the batch as Failed with the checkpoint kept.
6. **Provenance ledger.** One row per entity created, updated or linked by the batch, with action, target identity, before-image for updates, and a reversal state. Contracts and documents carry their batch id and source identity; contract details show them.
7. **Rollback by provenance only.** Rollback and compensation select strictly from the batch's own ledger. Untouched records created by the batch are removed; records that have moved on are archived with the reason (never deleted); shared suppliers and categories are retained; updates are restored from before-images only when unchanged since; children before parents. Acceptance ends rollback; compensation (archival) remains. Archived contracts disappear from the register but remain readable with their provenance.
8. **Deterministic attachment matching.** Fixed order (SourceRecordId, AgreementReference, naming rule, parent folder). Ties are never broken: candidates become proposals for a person. Everything unresolved is in a queue with reasons; nothing is silently dropped.
9. **Hand-written format readers.** CSV, XLSX and ZIP are read by in-repository code with explicit limits, so the safety rules (no DTDs, no macros, no extraction to disk, traversal and bomb checks) are testable and there is no new dependency in the supply chain.
10. **Authorisation.** Five permissions (`migration.stage|commit|legacy_load|rollback|accept`) held by Procurement Administrators; evaluated against the stored project's organisation. Out of scope or other tenant: not found; in scope without permission: forbidden. The committer cannot confirm a legacy load.
11. **Cutover freeze.** Contracts consult `ICutoverFreeze` (default: none); the Migration module replaces it so hand-keyed creation is refused during a project's freeze window, while the migration itself is unaffected.

## Consequences

* Idempotency and resume are proven by killing the commit at eight points (including chunk boundaries and the phase boundary) and re-running; counts, provenance and blob store equal an uninterrupted run.
* A rollback never deletes a record someone has worked on, at the cost of leaving archived records behind; this is deliberate.
* The planner and the commit use the same plan, so the dry run is a faithful preview; a decision after a dry run requires repeating it.
* Tests run on SQLite for speed and on SQL Server for proof (RLS, composite keys, ordering of GUID keys exposed a real rollback ordering issue that SQLite hid).
* Residual: commit is synchronous within the request (resumable); a worker is future work. Notifications and reminders are not emitted because none exist yet. No real malware scanner adapter exists. Attachments cannot be attached to a contract loaded by an earlier batch. JSON sources, folder upload and custom-field loading are future.
