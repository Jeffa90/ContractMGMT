# Exports and the reporting projection

Status: **Implemented** for CSV and XLSX; **no Qlik or other reporting integration** in this release (`reporting.external` remains a later capability).

## Who and what

* Needs `records.export` (Procurement Administrator and Auditor by default). The person must also be able to read each record.
* **Authorised and reportable fields only.** A column is exported only if the person may see it and, for an additional field, only if its definition is marked reportable. A value above their classification is never included, and the column is left out rather than blanked.
* The export uses the **same query** as the register: the selected view's columns, filters, text and sort. There is no second filter model.
* **Metadata.** An XLSX workbook has a second sheet recording the time (UTC), tenant, organisation, who exported, the view, the search and filters, the row count, the reporting schema version and the field-definitions release. CSV is UTF-8 with a byte-order mark.
* **Formula injection.** A text cell starting with `=`, `+`, `-`, `@`, tab or line break is prefixed with an apostrophe in both formats; numbers stay numbers (a negative amount is not mistaken for a formula). Files are never opened as macros.
* **Audit.** Every export records `contracts.exported` with format, rows, total matching, the columns, the filters and the view. A large export also records its reason. File contents are never written to the audit trail.

## Limits and the large-export process

| | Default | Setting |
|---|---|---|
| Rows in a normal export | 10,000 | `Contracts:ExportRowLimit` |
| Rows in a large export | 100,000 | `Contracts:LargeExportRowLimit` |

A result over the limit is **refused, never cut short**, with a message saying how to proceed: narrow the search, or ask for a large export with a reason of at least ten characters (recorded). A result over the large limit is refused. Rows are read in pages of 1,000; the work is bounded by the limit before the first row is built.

## Stable reporting projection

`IContractExports.SchemaAsync` returns the columns a downstream tool can rely on: **`name`** (the key of a core field, or `cf_` plus the key of an additional field), label, type, source (core or custom) and classification. Rules: a name never changes meaning; a retired field keeps its column; a new field adds a column; the schema version changes only when a column is removed or its type changes (never so far: `1`). The schema is filtered for the person asking, so it never reveals a field they cannot export. A later reporting integration reads this contract; no Qlik view or connector exists yet.

## Not in this release

Scheduled exports, emailed files, PDF output, background export jobs (an export runs in the request), and streaming of very large files.
