# Migration Supplement

Added by repository review. This file **adds to** the authoritative migration files; it does not override them. Where a statement here appears to conflict with `MIGRATION_REQUIREMENTS.md`, `ATTACHMENT_INGESTION.md`, `SOURCE_TO_TARGET_MAPPING.md`, `MEDIUS_IMPORT_PROFILE.md` or `CUTOVER_AND_RECONCILIATION.md`, those win and the conflict is a defect to record. It resolves CON-01, CON-08, CON-10, CON-11, CON-12, RISK-08, RISK-20 and GAP-20. Tags: `[CONFIRMED]` traceable to the authoritative files; `[CONFIRMED]` detail.

## 1. Precedence and scope

Migration is Phase 1 mandatory (`CONFIRMED_DECISIONS.md`). First migration slice (milestone M5): suppliers, taxonomy (categories, subcategories), contracts, documents (with manifest and ZIP), unmatched queue, commit, reconciliation, resume and rollback. Second slice (M5b, section 10): historical Legal artefacts, approvals and Variations, pending Q-12.

## 2. Identifiers and idempotency (CON-07, CON-10)

| Entity | Idempotency key | Notes |
|---|---|---|
| Contract | `(TenantId, OrganisationId, SourceSystem, SourceRecordId)` | `[CONFIRMED]` core (source system + source record id + target tenant); organisation added `[CONFIRMED]` ASM-04. `SourceSystem` comes from the migration project |
| Supplier | `(TenantId, OrganisationId, SourceSystem, VendorNumber)`; if no vendor number, normalised name | Normalised name = Unicode NFKC, trim, collapse internal whitespace, case-fold. Matching is exact on the normalised value. More than one candidate is an error (`MIG-E010`) resolved by a human; there is no automatic fuzzy matching |
| Category / subcategory | `(TenantId, OrganisationId, parent path of normalised names)` | Resolved through the category lookup map or existing taxonomy; unresolved is an error unless the profile enables "create missing categories" |
| Document | `(TenantId, OrganisationId, SourceSystem, DocumentSourceId)` | Manifest gains two **optional** columns: `DocumentSourceId` and `SourceSystem`. When `DocumentSourceId` is absent it defaults to `SourceRecordId + "/" + normalised RelativePath`. `ParentDocumentSourceId` refers to a `DocumentSourceId` |
| Document content | `(TenantId, SHA-256)` | De-duplication is **within one tenant only** (ASM-14) |

Uniqueness for `agreementReference` is `(TenantId, AgreementReference)` across the whole tenant (D3). The original reference from the source system is **always** preserved separately in `sourceAgreementReference`. Reference policy per project: `KeepSource` (the source reference becomes `agreementReference` if it is free in the tenant and acceptable to the organisation's numbering rule) or `GenerateNew` (the organisation's numbering rule allocates the target reference); a source reference that collides with another record anywhere in the tenant is `MIG-E006` and is resolved by the duplicate strategy or by generating a new reference. Generated references (strategy `CreateNewReference`) come from the organisation's numbering rule and are checked for collisions before commit. Error messages never disclose records outside the caller's scope. `[CONFIRMED]`

## 3. Mapping profiles and the transformation catalogue (CON-08)

**Row columns.** `SourceColumn, TargetField, Transformation, DefaultValue, Required, LookupMapKey, Validation, Notes`. The original sample has the first four plus `Required` and `Notes`; `LookupMapKey` and `Validation` are optional additions. A profile with all 18 sample columns mapped is `migration-samples/field-mapping-profile-complete.csv`.

**Target field syntax.**

- `camelCaseName`: a core field from `requirements/03-data-dictionary/`.
- `custom.<code>`: a published custom field.
- `entity.field` (for example `supplier.name`, `supplier.vendorNumber`, `category.name`, `subcategory.name`): *find-or-create the related entity by its idempotency key, then set the field*. This makes the sample's `supplier.name` valid.
- `staging.<name>`: a staging-only value used by matching or validation (for example `staging.documentFolder`) and **not** stored on the contract.

**Safe transformations** `[CONFIRMED]` list from `SOURCE_TO_TARGET_MAPPING.md`, with codes `[CONFIRMED]`:

| Code | Parameters | Behaviour |
|---|---|---|
| `Trim` | | Remove leading/trailing whitespace |
| `NormaliseWhitespace` | | Collapse runs of whitespace to one space |
| `Upper`, `Lower`, `TitleCase` | culture | Case normalisation |
| `ParseDate` | format, culture | Date-only. Ambiguous day/month formats require an explicit format; the wizard never guesses |
| `ParseDateTime` | format, culture, timeZone | Produces a UTC instant; time zone is explicit (default: organisation) |
| `ParseDecimal`, `ParseInteger` | culture, thousands/decimal separators | Rejects text that does not parse completely |
| `MapBoolean` | true-set, false-set | Unlisted values are errors |
| `Concat` | columns, separator | |
| `Split` | separator, index | |
| `FixedDefault` | value | Used when the source is empty |
| `LookupMap` | `LookupMapKey`, onMissing = `Error` \| `Default` \| `Skip` | Maps source values to target codes using the profile's lookup maps |

**Not allowed:** any truncation (a value longer than the target maximum is an error `MIG-E009`, never silently cut: "No silent truncation"), arbitrary expressions, scripts, or regular expressions with backtracking.

**Preview.** Before commit the wizard shows, per mapping row, original and transformed values for a sample and the **count of affected rows** `[CONFIRMED]`.

## 4. Staging and dry run

- **Staging** is the `migration` schema plus the quarantine blob area; **target** is everything else except the audit log: domain tables, committed document storage, outbox, notifications, reminders `[CONFIRMED]` definition.
- A dry run reads sources into staging, runs profiling, mapping, validation and matching, and produces the validation report, match proposals and reconciliation preview. It performs **no writes to target**, sends **no** notifications, emits **no** outbox messages and creates **no** reminders. It does write audit events (type `migration.dryrun.*`) and staging rows. `[CONFIRMED]` ("Dry run performs no target writes")
- A dry run is repeatable; each run is a `MigrationBatch` of kind `DryRun`. Re-running does not accumulate duplicate staging rows for the same batch.

## 5. Batch states and commit pipeline

States (`reference-lookups.csv` `MigrationBatchStatus`): Created, Profiled, Validated, Committing, Committed, Reconciled, Accepted, plus RolledBack, Compensated, Failed (resumable).

Commit phases, in order: (1) reference data (categories, suppliers), (2) contracts, (3) documents (blob first, then metadata), (4) links and match results, (5) reconciliation. Rules `[CONFIRMED]` detail:

1. Work proceeds in chunks (default 500 rows or files). Each chunk commits in **one transaction together with its checkpoint** (`phase`, `lastCompletedSequence`).
2. Resume reads the checkpoint and continues. Because every target row carries its idempotency key under a unique index, a replay of a completed chunk is a no-op, so **resume never duplicates records or files**.
3. A blob write is idempotent (content-addressed within the tenant). A sweeper removes blobs with no metadata row after a grace period; rollback and sweeper never remove a blob referenced by anything outside the batch.
4. Batch ID is written on every created or updated row, document, audit event and outbox suppression note.
5. Notifications and workflow side effects are suppressed for imported historical records; reminders are generated after reconciliation if enabled (`WORKFLOW_SPEC.md` section 8).
6. **Cutover freeze** `[CONFIRMED]`: the project records the freeze window; when `target freeze` is enabled the platform blocks creation of contracts in the affected organisation by non-migration users during the window `[CONFIRMED]`.
7. The batch captures the mapping profile version and configuration release at creation `[CONFIRMED]`.

## 6. Rollback and shared reference data (CON-12)

**Reversible** items are those created by the batch and untouched since: no workflow transition after load, no Variation, no edit by a user (the row version and audit show only batch events).

Algorithm (also shown as a **rollback preview** before execution):

1. Select provenance records for `BatchId = X` with action `Created` or `Updated`. Rollback never selects by anything else, so pre-existing records and other batches are untouched. `[CONFIRMED]`
2. For each `Created` contract/document: if reversible, remove (or archive, if the retention policy prohibits removal) and remove its document links and unreferenced blobs; if not reversible, apply **compensating archival** (state `Withdrawn`/archived with reason and batch id; documents archived) and report it. `[CONFIRMED]`
3. For each `Updated` record: restore the stored before-image only if the row is unchanged since the batch; otherwise report as not reversible.
4. **Shared reference data** (suppliers, categories) created by the batch: removed only when **no remaining record outside the batch references them**; otherwise retained, flagged "created by rolled-back batch", and reported.
5. Rollback is blocked after `Accepted` (finalised). Afterwards only compensating archival through normal processes is available. `[CONFIRMED]`
6. The rollback itself is a batch-linked, fully audited operation and is idempotent.

## 7. Legacy-load policy (CON-01, ASM-06)

Needed because `registeredDate` is normally stamped on authorisation and Effective records are locked, yet `MEDIUS_IMPORT_PROFILE.md` allows "Historical Effective contracts ... imported as locked legacy records". `[CONFIRMED]` mechanics:

- Project setting `legacyLoadPolicy`: allowed target states, whether `registeredDate`/`registeredBy` may be imported, whether existing locked legacy records may be overwritten, and an expiry date after which the policy is inert.
- Requires permission `migration.legacy_load` **and** second-person confirmation of the commit.
- Contracts land locked with `isLegacyLoad = true`, the verbatim `sourceStatus`, and source `registeredDate` if permitted (otherwise null, never fabricated). An imported Registered Date sets `registeredDateProvenance = Migration` and stores the verbatim source value in `sourceRegisteredValue`; the migration batch id, source record id, source value and the acting migration administrator are written to the audit event. After migration acceptance users cannot ordinarily edit Registered Date. `[CONFIRMED]` (DR-2026-10-15) They bypass workflow transitions; no notifications are sent.
- A legacy load that would overwrite an Effective locked contract is refused unless the policy explicitly allows it and an audit record states why. `[CONFIRMED]`
- After go-live the project closes the policy; subsequent loads of Effective-state data are refused.

## 8. Message catalogue

Stable codes appear in the error workbook and the API. Severity E = error (row rejected), W = warning.

| Code | Meaning |
|---|---|
| MIG-E001 | Required value missing |
| MIG-E002 | Date could not be parsed with the configured format |
| MIG-E003 | Number could not be parsed |
| MIG-E004 | Lookup value not mapped |
| MIG-E005 | Duplicate source record in the same file |
| MIG-E006 | Duplicate agreement reference (strategy decides: reject, skip, update, generate, manual) |
| MIG-E007 | Cross-tenant or cross-organisation reference |
| MIG-E008 | Status not valid for the target workflow or not allowed by the legacy-load policy |
| MIG-E009 | Value exceeds maximum length (never truncated) |
| MIG-E010 | Ambiguous supplier match (needs manual resolution) |
| MIG-E011 | Category or subcategory unresolved |
| MIG-E012 | Legacy-load policy violation |
| MIG-W001 | Value normalised (trimmed, case, whitespace) |
| MIG-W002 | Default applied |
| MIG-W003 | End date blank with Evergreen extension type (expected) |
| MIG-W004 | Registered date earlier than effective date |
| MIG-F001 | Archive path traversal or absolute path rejected |
| MIG-F002 | Extension not on allowlist |
| MIG-F003 | Content signature does not match declared type |
| MIG-F004 | Empty file |
| MIG-F005 | Corrupt or encrypted file |
| MIG-F006 | File, archive or entry count exceeds limits |
| MIG-F007 | Duplicate content hash (option decides) |
| MIG-F008 | Duplicate file name within a contract |
| MIG-F009 | Orphan manifest entry (no file) |
| MIG-F010 | Orphan file (no manifest entry or match) |
| MIG-F011 | No match found (goes to unmatched queue) |
| MIG-F012 | Ambiguous match (more than one contract; goes to unmatched queue) |
| MIG-F013 | Malware scan reported infection |
| MIG-F014 | Malware scanner unavailable (file held; Production does not proceed) |
| MIG-F015 | Target contract not found in this tenant/batch |
| MIG-F016 | Tenant mismatch |

## 9. Input handling (RISK-20)

- **CSV:** accept and strip a UTF-8 byte order mark (all sample CSVs begin with one); detect UTF-8, UTF-16 and, only with explicit confirmation, Windows-1252; configurable delimiter; RFC 4180 quoting; embedded newlines supported; ragged rows are errors, not silently padded.
- **XLSX:** read the chosen sheet; formulas are read as cached values; macro-enabled workbooks (`.xlsm`) rejected; hidden sheets and rows are flagged; Excel date serials converted with the workbook's date system; no external links followed. Use a permissively licensed library (RISK-10).
- **JSON / ZIP package:** schema validated; same archive safety rules as `ATTACHMENT_INGESTION.md`.
- **Limits** (configurable; defaults `[OPEN]` pending Q-13): 250,000 rows per file, 5 GB per archive, 20,000 entries per archive, 500 MB per file, compression ratio 100:1.
- **Regular expressions** for the "configured naming rule" matching method use a non-backtracking engine with a timeout (ASM-13).
- **Streaming:** large archives are processed as streams; extraction never buffers a whole archive in memory.
- **Matching** follows the order in `ATTACHMENT_INGESTION.md`; each proposal records method and reason; ties are never broken automatically.

## 10. Second slice: historical Legal artefacts, approvals and Variations (GAP-20)

`MIGRATION_REQUIREMENTS.md` lists these in its purpose but gives no lifecycle, mapping or sample for them. Proposed approach pending Q-12:

- Legal advice and review documents migrate as documents of type `LegalAdvice` linked to the contract; no `LegalReviewRound` rows are fabricated.
- Historical approvals migrate as `ApprovalRecord` with `legacySource` set and read-only.
- Historical Variations migrate as `Variation` with outcome `Applied` and linked documents; the parent's current values come from the contracts file, not by replaying variations.
- **In-flight** items (open reviews, pending approvals, open variations) are not recreated as live workflow instances. They are reported so the business can recreate them after go-live.
- Mapping profiles, manifests and samples for these entities are created when Q-12 is answered.

## 11. Reconciliation identities (CUTOVER_AND_RECONCILIATION.md)

For each batch the following must hold and are tested:

```
SourceRows            = Accepted + Rejected + Skipped
Accepted              = Created + Updated
ManifestRows          = Matched + OrphanManifest
FilesInArchive        = Imported + Failed + Invalid + DuplicateHandled + Unmatched + Discarded
Imported documents    = rows in Document/DocumentVersion with BatchId and action Created
```

Plus status and category distributions source versus target, financial totals by currency where meaningful, and the owner's sample record/document checks. A batch cannot move to `Reconciled` unless identities balance or each exception is approved and recorded. `[CONFIRMED]` ("No batch is complete until totals balance or documented exceptions are approved")

## 12. Sample assets

The original samples (2 contracts, 3 manifest rows) are the minimum. Additional assets added by repository review: `migration-samples/field-mapping-profile-complete.csv` and `migration-samples/status-lookup-map.csv`. Negative and edge fixtures (duplicates, traversal ZIP, unmatched, bad dates, cross-tenant, large volume) are **generated by test code** so no hostile file is stored in the repository; they are specified in `docs/TESTING.md` section 7.
