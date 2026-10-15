# Migration Requirements

## Purpose
Move legacy contract metadata, suppliers, taxonomies, documents, Legal artefacts, approvals and Variations into the target platform with repeatable validation, reconciliation, provenance and controlled rollback.

## Sources
- CSV, XLSX and JSON
- ZIP migration package
- SharePoint/list/library export adapter
- Medius flat export profile
- Generic Ariba/Icertis-style flat export profiles
- Future API connectors through adapters

## Migration lifecycle
1. Create migration project and identify tenant/organisation
2. Record source system, extraction date and data owner
3. Upload/register source files
4. Profile headers, types, counts, duplicates and missing values
5. Choose or create mapping profile
6. Map lookups and transformations
7. Upload/import attachment manifest and archive
8. Run dry validation
9. Review errors, warnings, matches and unmatched files
10. Correct source or override through audited mappings
11. Run controlled batch import
12. Reconcile source, accepted, rejected, skipped and created/updated totals
13. Business-owner sample validation and acceptance
14. Finalise batch, retain provenance and prevent unsafe rollback

## Non-negotiable controls
- Dry run performs no target writes
- Batch ID on every imported record/file/audit event
- Idempotency key from source system + source record ID + target tenant
- Resumable checkpoints
- Deterministic matching
- Duplicate strategy selected before commit
- No silent truncation or ignored attachments
- Source values retained in staging and provenance records
- Errors exportable to XLSX/CSV
- Cross-tenant references rejected
- Configuration version captured with the batch
- Business acceptance recorded

## Duplicate strategies
- Reject
- Skip existing
- Update mapped fields
- Create new version
- Create new record with generated target reference
- Manual review

Never overwrite an Effective locked contract unless an explicit authorised migration policy and audit record allow a controlled legacy load before go-live.

## Rollback
Before acceptance, reversible batch-created records and file links may be removed or archived by batch. After downstream workflow activity, use compensating archival rather than destructive deletion. Rollback must never affect pre-existing records or another migration batch.
