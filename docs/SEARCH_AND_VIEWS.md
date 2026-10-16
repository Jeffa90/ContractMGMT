# Typed search, list views and saved views (DOM-03)

Status: **Implemented**. Module `Ecp.Contracts` (folder `src/Modules/Ecp.Contracts/Register`), pages `/Contracts` and `/Admin/Studio/Views`.

## The query

A search is **typed**: free text, filter clauses and sorts over fields that exist in the catalogue the caller may use. A filter is `field` + `operator` + `values`; there is no expression text, SQL or script anywhere, and the page's controls are plain selects.

| Operator | Types |
|---|---|
| `Eq`, `NotEq`, `Contains`, `StartsWith`, `In`, `IsEmpty`, `IsNotEmpty` | text, long text, email, web address (long text: `Contains`) |
| `Eq`, `NotEq`, `Gte`, `Lte`, `Between`, `IsEmpty`, `IsNotEmpty` | integer, decimal, currency, percentage |
| `Eq`, `Gte`, `Lte`, `Between`, `IsEmpty`, `IsNotEmpty` | date, date and time |
| `In`, `Eq`, `NotEq`, `IsEmpty`, `IsNotEmpty` | single/multiple choice, lookup, person, category, status |
| `Is` | yes/no |

Dates accept `yyyy-mm-dd` or `@today`, `@today+30`, `@today-7`. `@me` stands for the signed-in person (owner, person fields). `@live` in a status filter means the live states of the workflow, supplied by the workflow definition, so no state name is hard-coded. A `set` filter (`Is`) names a list supplied by another module (awaiting my action, awaiting Legal, pending approval, variations in progress, renewals in progress, reminded but unacknowledged); a set only narrows what the caller may already read.

Limits: 20 filters, 3 sorts, 50 values per filter, 200-character text, 30 columns, page size at most 200 (25 by default). Anything else is refused with a message that names the problem.

## Order of work and security

1. The caller's access to contracts (organisation, business unit, site, category, classification, ownership) is applied **first**, in the database, exactly as the list and the detail page apply it.
2. Typed filters, then ordering, then paging, all in the database; no register is loaded into memory.
3. A configured field takes part only where the caller could see its value. A field the caller may not use is not in their catalogue at all: filtering or sorting on it is refused as an unknown field, the free-text search never reaches it, and a hidden column is left out with a note (`The column 'x' is not available to you`). Requests built by hand cannot read a hidden value.
4. Free text matches reference, title, supplier name and the configured fields marked searchable that the caller may see; every word must match.

## Indexes and cost

* Core search uses the existing contract and supplier indexes (organisation, status, dates, reference).
* Configured values have one narrow, filtered index per typed column: `(TenantId, FieldKey, TextValue | NumberValue | DateValue | DateTimeValue | BoolValue | RefValue)` plus `(TenantId, OrganisationId, FieldKey)` and `(TenantId, MigrationBatchId)`. A configured filter compiles to an **uncorrelated `ContractId IN (matching values)`** subquery so the database seeks the matching values through the field's index and joins once. (A correlated lookup per contract was found to cost 27 s on 10,000 contracts and replaced; see PERFORMANCE.md.)
* Ordering by a configured field is a keyed lookup per surviving row; ordering by a core field uses the contract columns.
* Paging is by offset. Deep pages cost more; the page size cap and the total count bound the work. Keyset paging is a possible later change.
* Limits and trade-offs are documented here rather than hidden: `Contains` on text cannot use an index (it scans the matching field's values, not the register); the total count runs one count query per page.

## Views

A view is columns, sort, typed filters, search text, rows per page, date and currency formats, optional grouping and status chips, referring to fields by stable key. A retired or removed field degrades gracefully (shown with a note, or dropped with a note).

* **Shipped (11):** All Contracts, My Contracts, Awaiting My Action, Awaiting Legal, Pending Approval, Expiring Soon, Reviews Due, Critical Notice, Variations in Progress, Renewals in Progress, Reminded but Unacknowledged (`src/Modules/Ecp.Contracts/Register/system-views.json`, embedded). They cannot be edited.
* **Shared** views are published by an administrator through the configuration lifecycle (`ContractListView` kind: draft, validate, publish, versions, rollback, audit). They are checked as definitions against all the organisation's fields; anyone in the organisation can open them and sees only what their own access allows.
* **Personal** views are saved by the person (at most 50, unique names), private to them, validated against the fields they may use at save time and again at run time, and audited.
* **Exports** share the same query: the register's export button exports exactly the view, filters and sort on screen (EXPORTS.md).
* A saved filter can name a classification only if the person can read it; a view never names a field its author cannot use.

## Not in this release

Saved views per team, view sharing between individuals, pinned default view, full-text search across documents, and cross-organisation configured-field filtering (choose one organisation to use its additional fields).
