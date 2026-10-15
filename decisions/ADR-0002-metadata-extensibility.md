# ADR-0002: Metadata Extensibility

Status: Proposed

## Decision
Use strongly typed core fields plus metadata-driven custom values.

## Context
Business administrators must add fields, forms, rules, workflows and approval matrices without code or per-field database migrations (`PORTABILITY_AND_NO_CODE.md`, Master "Field designer"). Master leaves open "typed-value or validated JSON model with indexed/reporting projections". Core contract fields (`contracts.csv`) must remain strongly typed so invariants (locks, uniqueness, workflow) are enforceable and fast.

## Drivers
No DDL on configuration change; search and reporting on custom values; safety (no code); portability between businesses; migration mapping to custom fields.

## Alternatives considered
- **JSON column per entity:** simple writes; weak indexing, weak per-value constraints, awkward typed search and reporting.
- **Table per custom field / dynamic DDL:** violates "no per-field migration".
- **Pure EAV with string values:** loses types and comparison semantics.
- **Chosen:** typed-value rows (ADR-0009) with core fields as real columns.

## Decision details
Core fields are columns. Custom fields are definitions (configuration kind `Field`) whose values live in `CustomFieldValue` typed rows (`docs/DOMAIN_MODEL.md` section 5.3). Definitions are versioned and validated against closed schemas. Forms, rules, workflows and approvals are configuration definitions interpreted at runtime (`docs/ARCHITECTURE.md` section 5).

## Consequences
- **Security:** values are typed and encoded on output; injection surface limited to closed vocabularies (NCD-002, NCD-003).
- **Operations:** static indexes serve every field; no migrations on configuration change (NCD-001, PRT-003).
- **Migration:** mapping targets are `custom.<code>` (`MIGRATION_SUPPLEMENT.md` section 3).
- **Reporting:** long-format view and generated pivot per release (`REPORTING_QLIK_SPEC.md`).
- **Reversal:** moving to JSON or another store is a data migration behind the field service; cost medium.

## Verification
NCD-001, NCD-012, PER-002, PRT-003.
