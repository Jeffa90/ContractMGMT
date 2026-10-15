# Data Export Specification

Master: "Support records, documents, metadata, audit evidence and configuration exports subject to permission." This file defines the export types and the round-trip guarantee that proves portability (GAP-15). `[CONFIRMED]` requirement, `[PROPOSED]` detail.

## 1. Export types

| Type | Format | Permission | Notes |
|---|---|---|---|
| Records list | CSV, XLSX, JSON | `records.export` | Honours the caller's visibility and classification; sensitive fields masked |
| Documents | ZIP of files plus `attachment-manifest.csv` in the same format as `requirements/09-migration/ATTACHMENT_INGESTION.md`, with SHA-256 | `records.export` and `documents.download` | The manifest can be re-ingested by the migration framework (round trip) |
| Audit evidence | Package per `AUDIT_SPEC.md` section 4 | `audit.export` | Signed manifest |
| Configuration | `.ecpkg` per `CONFIGURATION_PACKAGE_SPEC.md` | `packages.export` | Excludes secrets, users, assignments |
| Full tenant export | One archive: configuration package, records (JSON Lines per entity with a published schema), documents plus manifest, audit evidence, and a top-level manifest with counts and hashes | `records.export`, `packages.export`, `audit.export` all required, step-up authentication | Used for offboarding and tenant moves |

## 2. Rules

1. Exports run as background jobs with progress and cancellation, in an explicit tenant scope, writing to a tenant-prefixed area.
2. The downloaded file is delivered by an authorised, short-lived, user-bound token; there is no public or guessable URL. Export files expire (default 7 days) and are then deleted.
3. Every export is audited (who, what filter, counts, hash).
4. Spreadsheet formula injection is neutralised in CSV and XLSX output (cell text beginning `=`, `+`, `-`, `@`, tab or carriage return is prefixed).
5. Records above the caller's clearance are excluded, not masked, and the export states that exclusion occurred without revealing identifiers.
6. An export's totals (rows, files, bytes) equal the manifest, and the manifest is hash-protected.
7. Optional archive passphrase protection (AES-256) with the passphrase shown once and never stored.

## 3. Round-trip guarantee (portability proof)

A full tenant export from tenant A can be loaded into an empty tenant B through the migration framework (JSON/ZIP package profile) and the configuration import. Acceptance (PRT-011): record counts, per-entity content hashes (after id remapping), document SHA-256 values, configuration release hashes and reconciliation totals match; provenance fields record the original ids and the source installation; the import is a normal migration batch with dry run and rollback.

## 4. Not exported

Secrets and secret references' values, password hashes (none are stored for external identity), live sessions, other tenants' data, the installation-level data and the audit hash-chain keys.
