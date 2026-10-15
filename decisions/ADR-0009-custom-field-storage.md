# ADR-0009: Custom-Field Value Storage and Reporting Projections

Status: Proposed

## Decision
Store custom values as typed rows in `CustomFieldValue` (one populated typed column per row, check-constrained; child rows for multi-choice; currency code beside amounts). Use static composite indexes `(TenantId, FieldDefinitionId, <typed value>)`. Never issue DDL when fields change. Provide reporting via a long-format view and a generated pivot per release for fields flagged reportable.

## Context
Resolves CON-16 and refines ADR-0002. `custom-field-types.csv` lists 17 types with storage kinds; `CURRENCY` lacked a currency code; per-field indexing conflicts with no-DDL.

## Alternatives considered
- JSON documents with computed columns per field: needs DDL for indexing.
- Sparse columns / wide table: schema churn.
- Separate typed tables per type: more joins, little gain at expected scale.

## Decision details
- Types map: strings → `ValueString` (length-limited), numbers/currency/percentage → `ValueDecimal` (+ `CurrencyCode`), date → `ValueDate`, datetime → `ValueDateTime` (UTC), boolean → `ValueBool`, lookup/person/file → `ValueGuid`, multi-choice → items table.
- Search enabled only for fields flagged `searchable`; queries use the typed index; text search uses a bounded `LIKE` or full-text (decided at CFG-03 with PER-002 evidence).
- Budgets: maximum fields per entity default 200; plan-check test ensures index usage.
- `CALCULATED_DISPLAY` is computed on read; materialised only into reporting projections.

## Consequences
- **Performance:** extra joins for filtered lists; mitigated by indexes and projections; measured in PER-001/002.
- **Security:** typed values reduce injection and parsing risk; sensitive fields masked per definition.
- **Migration:** mapping to `custom.<code>` writes typed rows with validation.
- **Reversal:** medium; value access is behind a field service.

## Verification
NCD-001, NCD-012, PER-002, SEC-005, SEC-040 to SEC-045.
