# Configuration Studio (STU-01, initial form-based release)

Status: **Implemented** as forms. There is **no** graphical or drag-and-drop designer in this release. Pages live under `/Admin/Studio`. Each page needs the configuration rights named below; every edit is a draft until validated and published.

## Rights

| Action | Permission |
|---|---|
| Open the Studio, start, edit, validate, discard a draft | `config.draft` |
| Publish | `config.publish` (second-person publishing applies where the tenant requires it) |
| Roll back | `config.rollback` |
| Read version history | `config.history.read` |

Organisations are offered only where the person holds one of these rights; naming any other organisation is a plain not-found. The Studio uses the same configuration service as every other configuration kind, so draft, validation, publication, release history, rollback and audit are not re-implemented.

## Pages

| Page | What it does |
|---|---|
| `/Admin/Studio` | Overview for an organisation: published fields and views, drafts open, links to history and audit. |
| `/Admin/Studio/Fields` | Lists the fields in section and order (the draft if one is open, otherwise what is in force). Start a draft; move a field up or down within its section; retire or reinstate; delete a field that was never published; see **checks** (validation messages) and **impact** (what publishing would change, with stored-value counts); validate; publish with a note; discard. |
| `/Admin/Studio/Field` | Create or edit one field: identity, type, section, help, required, default; the **option editor** (`CODE | Label | inactive`) and lookup choice; the **validation editor** (the predefined checks for the type); the calculated editor (a closed operation list); **role, state and contract-type visibility** and editability; classification; searchable, filterable, sortable, reportable, usable in approvals; effective dates; status. **Preview** shows the field in five places without saving: the create form, the edit form with a value, the contract details page, the register column and filter, and the export. |
| `/Admin/Studio/Lists` | Sections (key, label, order) and lookup lists (members as `CODE | Label | inactive`). A list named `contractType`, `agreementType`, `riskLevel`, `functionCode` or `supplierType` also controls that core field. |
| `/Admin/Studio/Views` and `/Admin/Studio/View` | Shared list views: columns (only fields you may use), sorting, typed filters, search text, rows per page, formats, grouping; **Try it** runs the draft as you; draft, validate, publish, discard. |
| `/Admin/Studio/History` | Releases of the field definitions or the shared views with a summary of what each changed, who published it and why; **restore** an earlier release as a new release (reason required); links to the audit trail. |

## Safe by construction

* The form edits the **typed model** and writes the document through `FieldWriter`; what a person types is data in a strict document, never concatenated into one, and the document is read back by the same strict parser that validates and publishes it (unknown members are errors).
* Publication fails safely for: duplicate keys; invalid defaults or choices; unsupported checks for the type; in-use choices, lists and fields; unsafe calculated definitions; a draft changed by someone else (conflict); and missing permissions (not-found or forbidden as for any configuration).
* A published field cannot be deleted; its key and type cannot change while values exist.
* Moving a field renumbers the orders of its section (10, 20, 30...) so two fields are never tied.
* Shared views are checked as definitions against every field of the organisation (by someone with `config.draft`), then each person who opens the view sees only the columns, filters and rows their own access allows; anything dropped is listed in a note.

## Audit

`config.draft.created/saved/validated`, `config.published`, `config.rolled_back`, `customfield.definitions.published` (added, changed and retired keys) and `contracts.views.published` are recorded with the release id. The Studio links to the audit browser filtered by entity type.

## Not in this release

* Graphical form, workflow or approval-matrix designers; conditional required/visible rules beyond role, state and contract type; per-section ordering controls other than the numeric order; bulk import of definitions (a package import is a later capability).
* The Studio does not edit the workflow, notification policy or approval matrices; those keep their document pages.
