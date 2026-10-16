# Configurable contract metadata and custom fields (CFG-03)

Status: **Implemented** (module `Ecp.Metadata`, tier 5; values and forms in `Ecp.Contracts`). Decisions: ADR-0023. Business administrators define additional fields in the Configuration Studio (see CONFIGURATION_STUDIO.md); this page describes what exists, the rules, and the limits.

## Core metadata

The contract carries typed core fields, each with one place in the data model: Agreement Reference, Contract Title, Description, Supplier (with Vendor Number, ABN and Supplier Type, which belong to the supplier), Category, Status, Data Classification, **Contract Type, Agreement Type, Risk Level, Function, Legal Entity**, Business Unit, Site, Currency, Estimated Agreement Value, Extension Type, Evergreen (derived from Extension Type), **Notice Period and its unit** (the days used for reminders are derived), Critical Notice, Legal Review Required, **SAP Outline Agreement Number**, Start, Effective, Registered, Review and End dates. The stable key of every core field is listed in `src/Modules/Ecp.Metadata/FieldModel.cs` (`CoreFields`); a custom field can never reuse one.

* **Provenance is preserved.** Source system, source record id, source reference, source status, extraction time and batch id stay on the record; a migrated Registered Date keeps its provenance. A new optional column never becomes mandatory for existing records: the migrations add nullable columns and backfill derived values (`NoticePeriodValue`/`Unit` from the older day count).
* **Controlled values.** Contract Type, Agreement Type, Risk Level, Function and Supplier Type are free codes (up to 100 characters) until an administrator publishes a lookup list of the same name (for example `riskLevel`); from then on a *new* value must be an active member. A value already stored stays valid.
* **Approval resolver.** Contract Type, Supplier Type, Risk Level and Legal Entity feed the approval matrix, along with value, category, business unit, site and classification. One `ApprovalFactService` builds the facts for a live submission and for the simulator (parity tests: `ApprovalFactScenarios`). A matrix condition on a fact the platform does not supply is refused at publication (`fact_unavailable`). Dimensions that have no live source are not offered.
* **Variations** can change Business Unit, Site, Legal Entity, Risk Level, Currency, Value, Extension Type, Notice Period, Critical Notice, Review and End dates, and (with `cf.*`) additional fields.

## The additional-field engine

No source change and no physical migration is needed for a new field. Definitions are a configuration document of kind `CustomField` (draft, validate, publish, version, rollback, audit, tenant and organisation aware). Values are stored in one table, `ContractCustomValue`, in typed columns with narrow filtered indexes, one row per value (several for a multiple-choice field), keyed by tenant, contract, field key and ordinal, with a composite foreign key to the contract and the same row-level security as the contract.

### Types (17)

Single-line text, long text, integer, decimal, currency amount, percentage, date, date and time, yes/no, single choice, multiple choice, lookup, person, email, web address, file reference, calculated (display only). Type codes are those of `requirements/03-data-dictionary/custom-field-types.csv`.

### Definition properties

Key (stable, `^[a-z][a-z0-9_]{1,62}$`, never reused for a core field, cannot change), label, description, help text, placeholder, type, section, order, required, default, validation, choices or lookup list, calculated specification, searchable, filterable, sortable, reportable, usable in approvals, classification, visible-to roles, editable-by roles, visible-in states, editable-in states, applicable contract types, status (Active/Retired), effective-from and effective-to.

### Validation by type

Predefined checks only; a check that does not belong to the type is refused when the definition is published.

| Check | Types |
|---|---|
| minimum/maximum length, pattern and its message | text, long text, email, web address |
| minimum/maximum value, decimal places | integer, decimal, currency, percentage |
| currency code | currency |
| earliest/latest date | date, date and time |
| minimum/maximum selections | multiple choice |

Patterns run in the .NET non-backtracking engine with a time limit; a pattern the engine cannot run safely is refused at publication. There is no expression language, no script, no SQL and no template engine. A **calculated** field is one of `Template`, `Sum`, `Difference`, `Product`, `Ratio` or `DaysBetween` over other fields; it is never stored, never typed into, and shown only when every operand is visible to the reader.

### Choices and lookups

A choice field lists its own `code | label | active` entries; a lookup field names a list that several fields (and the matching core field) can share. A choice in use cannot be removed: make it inactive. Inactive and retired choices keep showing on records that hold them, and are not offered for new entry. A source value is matched to a choice by code or label (case-insensitive, exact); nothing is guessed.

### Lifecycle of a definition

* **Never destructively deleted.** A published field cannot be removed or change type while values exist; retire it. A retired field keeps its stored values, shows them read-only, accepts no new ones, and keeps its column in views and exports.
* **Publish checks:** duplicate keys; unknown types, roles, classifications, sections and lists; invalid defaults or choices; unsupported checks for the type; unsafe patterns; unsafe calculated definitions (unknown operation, text operand, a retired or calculated operand, cycles); choices and lists in use; fields in use. Warnings (not errors) cover states the workflow does not have and similar. Publication is refused while any error remains, and a failed publish leaves the published definitions and the cache unchanged.
* **Impact analysis** (Studio) shows, before publishing, what is new, retired, blocked or held by data (`FieldImpact`).
* **History and rollback.** Every release is kept; rolling back publishes an earlier release's content as a new release and is refused if it would remove something now in use.

### Who sees and changes a value

Server-side only, and always after the contract's own access rules.

* **Visibility**: role, workflow state, contract type, effective dates and **classification**. A value is held at the more sensitive of the field's classification and the contract's. A person needs the right to read the contract at that level (for Legal Privileged, the explicit privileged permission). A field a person cannot see is not blanked: it is not present in the form, details, register, filters, sorts, free-text search, exports, Variation pages, approval snapshots, change history or reporting schema. A request that names such a field is answered as for a field that does not exist.
* **Editing**: the field's editable roles and states and the contract's own edit rules; a locked or frozen record refuses changes except by an authorised Variation or an audited emergency unlock. Required fields are enforced on create and on the next edit, never in bulk.
* **Audit**: every change records who, when, before and after, the definitions release and the authority (user, Variation, unlock, migration batch); the event is held at the most sensitive level among the fields changed.

### Approvals

A field can feed an approval matrix only when an administrator marks it usable in approvals (multiple-choice, long text, web address, file reference and calculated fields cannot). A matrix explanation shows the values it judged, so this is a deliberate choice.

## Limits and known gaps

* Person and file-reference fields cannot be loaded by a migration (refused with MIG-E025). A person field offers the colleagues in the contract's organisation.
* An emergency unlock changes core fields only; additional fields change by Variation.
* The Studio edits definitions in a form; it is not a graphical form designer (no drag and drop, no conditional required rules, one section order per organisation).
* Search over a multiple-choice field matches any of its values; ordering by one uses the first.
* SQL Server behaviour (indexes, row-level security, concurrency) is covered by tests tagged `SqlServer`; see TESTING.md for what has and has not been verified.
