# 03 Data Dictionary

`contracts.csv` and `custom-field-types.csv` are the original dictionary. They are unchanged. The other files were added by repository review to cover entities and fields the rest of the pack already assumes (GAP-11, CON-09).

| File | Entities | Notes |
|---|---|---|
| `contracts.csv` | Contract (15 core fields) | Original |
| `custom-field-types.csv` | Custom field type codes | Original. `CURRENCY` stores amount and ISO 4217 currency code together (CON-16) |
| `contracts-extended.csv` | Contract (additional core fields) | Fields required by the migration sample, Medius profile, tenancy and concurrency |
| `suppliers.csv` | Supplier | |
| `categories.csv` | Category, CategoryAssignment | Drives ownership |
| `org-structure.csv` | Tenant, Organisation, LegalEntity, BusinessUnit, Division, Site | |
| `documents.csv` | Document, DocumentVersion | |
| `lifecycle-records.csv` | LegalReviewRound, LegalComment, ApprovalRecord, Variation, RecordUnlock, ReminderSchedule | |
| `audit-event.csv` | AuditEvent | |
| `migration-entities.csv` | Migration project, profile, batch, staging, matching and provenance entities | |

## Column conventions (same as `contracts.csv`, plus `Entity`)

| Column | Meaning |
|---|---|
| `Entity` | Owning entity |
| `FieldName` | Stable camelCase code; never renamed (display names are terminology aliases) |
| `Type` | `string`, `long text`, `integer`, `decimal`, `boolean`, `date` (organisation time zone), `datetime` (UTC instant), `uuid`, `lookup`, `choice`, `person`, `json` |
| `Required` | `Yes`/`No` at the persistence layer |
| `Core` | `Yes` = strongly typed column; custom fields are never `Core` |
| `EditableAfterEffective` | `No` = immutable; `Variation only` = changed only by an applied Variation; `Admin unlock only` = changed only through an audited emergency unlock; `System` = written only by the platform; `n/a` = entity is not locked with the contract |
| `Notes` | Rules, sources, and tags `[CONFIRMED]` or `[PROPOSED]` |

## Rules that apply to every tenant-owned entity

1. `tenantId` is required, set server-side from the authenticated context, and never accepted from a client payload. `[CONFIRMED]` (Master: TenantId on owned entities)
2. `organisationId` is required wherever the entity is organisation-scoped. `[PROPOSED]` ASM-04
3. `rowVersion` is the optimistic-concurrency token on every mutable entity. `[CONFIRMED]` (Master: optimistic concurrency)
4. `createdUtc`, `createdBy`, `updatedUtc`, `updatedBy` are system-written. Timestamps are UTC. `[CONFIRMED]` (CLAUDE.md)
5. Every migrated row also carries `sourceSystem`, `sourceRecordId` and `migrationBatchId`. `[CONFIRMED]`
6. Custom field values are not columns of these entities; they live in the metadata-driven value store (ADR-0002, ADR-0009).
