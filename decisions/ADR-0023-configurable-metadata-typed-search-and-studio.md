# ADR-0023: Configurable contract metadata, typed search, list views and the form-based Studio

Status: Accepted (CFG-03, DOM-03, STU-01; DR-2026-10-16)

## Context

The register was a fixed set of columns and filters. JBS is a reference configuration, so administrators must add and change fields, views and exports without code, while access, classification and lock rules stay enforced on the server. The Studio had no pages.

## Decision

1. **`Ecp.Metadata` (tier 5)** owns the custom field model: the 17 types, the definition document and its strict parser and validator, value parsing and formatting (`ValueCodec`), the closed calculator, the impact analysis and the Studio service. It references SharedKernel, Persistence, Audit, Identity and Configuration (a same-tier edge to Configuration, recorded in the architecture tests). Contracts (tier 6) stores and serves the values; Lifecycle and Approvals consume only what the Contracts API exposes. A new `Ecp.Formats` library (tier 0) now holds the CSV and XLSX readers and writers shared by migration and exports.
2. **Definitions are configuration** (`CustomField` kind), so draft, validation, publication, version history, rollback, audit and the second-person rule are the existing configuration lifecycle. A published field is retired, never deleted. No customer text becomes SQL, code, a script, a template or an expression; patterns use the non-backtracking engine; calculations are a closed list.
3. **Values are typed rows** in one table with filtered indexes, not JSON and not a column per field: no migration per field, direct seeks for filters and sorts, a composite tenant foreign key and row-level security like the contract. Several values of a multiple-choice field are several rows.
4. **Visibility is decided by the server per request**, after the contract's access rules and from the field's role, state, type, classification and dates. A hidden field is absent everywhere (not blanked) and is answered as non-existent. A value is held at the more sensitive of the field's and the contract's classification. Audit events are held at the most sensitive level changed.
5. **One fact service for approvals.** `ApprovalFactService` builds the facts for a live submission and for the simulator; additional fields feed a matrix only when marked usable; unavailable facts are refused at publication.
6. **Search is typed and database-side.** Operators per field type, a closed grammar, hard limits, access first, offset paging with a page-size cap. Configured-field conditions are uncorrelated `IN` subqueries (a correlated per-row lookup was found by measurement to cost 27 s at 10,000 contracts). Views are data: shipped (embedded JSON), shared (configuration kind, versioned) and personal (a table). Exports reuse the same query and column model.
7. **Exports** are authorised, reportable-only, formula-safe, limited (normal and large with a reason), audited and described by a stable reporting schema (`cf_` plus key). No reporting integration is built.
8. **Migration** loads configured fields through the existing batch engine (target `custom.<key>`), checking each value against the published definition, never guessing a choice, refusing retired fields, keeping the source value and batch as provenance and the before-image under `cf.<key>`.
9. **Studio** is forms over the typed model written back through `FieldWriter` (never string concatenation); no drag and drop.
10. **Published-configuration cache.** Latest release per (tenant, organisation, kind) with a five-minute bound and a generation guard, plus parsed definitions per release number. Invalidated before and after commit (`IAfterCommit`, run only after a successful commit and discarded on rollback). A failing read is bypassed; a failing invalidation fails the publish (a stale definition must not stay in force silently).

## Consequences

* A pack can add fields, lists, views and exports with documents and pages only. JBS adds none by default.
* New core fields are nullable columns; existing data stays valid; Contract Type, Risk Level, Supplier Type and Legal Entity now reach the approval resolver.
* The Configuration Studio is form-based; richer designers are future work.
* SQL Server proof of the new tables, indexes and the performance of the register is pending a run of the SQL Server suites (see TESTING.md and PERFORMANCE.md); the SQLite results are not evidence for it.

## Assumptions and reversible choices

* Core code fields (contract type, risk level and so on) stay free codes until a list of the same name exists. A dedicated reference-data kind could replace this.
* An emergency unlock does not change additional fields (Variations do).
* `cf.*` was added to the JBS Variation workflow's changeable fields and four core fields (legal entity, risk level, notice period value/unit); a pack can remove them.
