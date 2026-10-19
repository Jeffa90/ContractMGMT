# Migration execution guide: loading a legacy register through the screens

**Purpose.** Take a business administrator through a complete migration cycle using only the application's Data migration pages: create the project, build and publish a mapping profile, set the legacy-load policy, create a batch, load the source files and the attachment archive, run a dry run, read and fix the errors, have a second person confirm a legacy load, commit, reconcile, accept, check provenance and, if needed, roll back. No JSON editing, SQL, source change or Git is involved. Preparing the data (the discovery work, the decisions about scope) is in docs/IMPLEMENTATION_PLAYBOOK.md section 14; this guide is the execution that follows.

**Who it is for.** A trained non-developer: the Procurement Administrator who runs the migration, a second Procurement Administrator who confirms a legacy load, the named data owner who answers for the source data, and the Project Manager who keeps the evidence.

**Prerequisites.**

* An organisation exists, its contract lifecycle is **published** (otherwise the dry run stops with MIG-E026), and any additional (custom) fields you will load are published.
* You hold the Procurement Administrator role for that organisation. All five migration permissions (stage, commit, legacy load, rollback, accept) belong to that role only; the Tenant Administrator, Organisation Administrator and Business Configuration Administrator roles hold none of them.
* For a legacy load (contracts that are already Effective or later) a **second person** holds the legacy-load permission too, so two separate Procurement Administrator accounts are needed.
* A recent backup exists around the final load (a **[Technical task: System Administrator]**, see docs/OPERATIONS_RUNBOOK.md and docs/BACKUP_AND_RESTORE.md) and the installation is not in maintenance mode (commit is refused during maintenance).
* Rehearsals use synthetic data, or company data your organisation has masked and declared (section 8.5). Never load live confidential data into a test installation. In Test mode the platform checks that a declaration exists before it lets masked company data be committed; it does not mask, detect or de-identify anything, and it never says the data is safe.

**Status label.** Contracts, suppliers, categories, additional fields, documents, dry run, reconciliation, provenance, rollback: **Implemented** (docs/MIGRATION.md; a scripted run of the real screens and automated tests). Masked-data declarations and the commit checkpoint (/Migration/MaskedData, Data handling on the batch page): **Implemented**, Test mode only, proved by automated tests and not by an unaided person; **Not Verified** that any particular masking method is adequate (that is the customer's responsibility). Malware scanning of attachments: **Mocked** outside Production (recognises only the standard test string) and **Disabled** as a real scanner; in Production files that cannot be scanned are held and not imported (**Not Verified / Future adapter**). Mapping legal entity, business unit, site, owners and users; JSON or ZIP-package sources; a background worker for commits: **Future**. Nothing here declares production approval.

Proof that the journey works without code: docs/NO_CODE_REHEARSAL.md, steps 13 to 18 (run over real HTTP on SQLite and on SQL Server by automated tests; not by an unaided non-developer).

---

## 1. Roles and governance

| Who | Does | Permission |
|---|---|---|
| Procurement Administrator | Everything on the Data migration pages: project, profiles, batches, uploads, dry runs, decisions, commit, reconcile, accept, roll back | migration.stage, migration.commit, migration.legacy_load, migration.rollback, migration.accept |
| Second Procurement Administrator | Confirms a legacy load. Cannot also be the person who commits it | migration.legacy_load |
| Data owner (a named business person, need not be a user) | Answers for the source data; their sample check is recorded in the acceptance note | none needed; named in the project |
| System Administrator | Backups, maintenance mode, restore | technical role, **[Technical task: System Administrator]** |

Governance that applies:

* **Second person for a legacy load.** The batch must be confirmed by a different person with the legacy-load permission, against exactly what was validated. Any change to the inputs (a file replaced, a decision, a profile or option change, another dry run) withdraws the confirmation.
* **Everything is audited**, with the batch identifier on each entry (Administration, Audit events, filtered by batch).
* **Server-side checks.** Organisation, tenant and permission are enforced by the server on every action; the page only offers what you may do. A person outside the organisation sees "not found".
* The Data migration pages do not ask for a recent sign-in. Publishing the configuration the migration depends on (workflow, fields) does (docs/PACK_IMPLEMENTATION_GUIDE.md section 9).

## 2. The cycle in one page

| Stage | Page | Result |
|---|---|---|
| 1. Project | Data migration (**/Migration**) | A project for one organisation |
| 2. Mapping profile | Mapping profiles (**/Migration/Profiles**) | A published, unchangeable profile version |
| 3. Project settings | Project page | Reference policy, legacy-load policy, cutover freeze |
| 4. Batch | Project page, then the batch page (**/Migration/Batches/{id}**) | Kind: Dry run, Mock load (rehearsal) or Final load |
| 5. Source files | Batch page, section 1 | Contracts file, manifest, archive staged |
| 6. Dry run | Batch page, section 3 | Nothing written; counts and errors |
| 7. Review and fix | Messages, Rows, Files pages | Errors fixed at source; decisions recorded |
| 7a. Data handling (Test mode) | Batch page, Data handling; **/Migration/MaskedData** | Batch marked Synthetic or Masked Company Data, linked to a declaration |
| 8. Confirm and commit | Batch page, section 5 | Records written in checked chunks |
| 9. Reconcile | Batch page, section 6 | Totals balance or exceptions approved |
| 10. Accept | Batch page, section 7 | Final for rollback |
| 11. Provenance and evidence | Provenance and Reconciliation pages | Exports kept |
| 12. Roll back or compensate | Batch page, section 8 | Before or after acceptance |

Run at least two rehearsals (a dry run to clean, then a mock load that writes and can be rolled back) before the final load. A dry run never commits; the Commit button is not offered on one.

---

## 3. Prepare the files

Sample files to copy from (all synthetic) are in migration-samples: `migration-samples/contracts-import.csv`, `migration-samples/attachment-manifest.csv`, `migration-samples/sample-attachments.zip`, `migration-samples/field-mapping-profile-complete.csv` and `migration-samples/status-lookup-map.csv`. For additional (custom) fields use `migration-samples/contracts-import-custom-fields.csv`, `migration-samples/field-mapping-profile-custom-fields.csv` and `migration-samples/tier-lookup-map.csv`. **Use the "complete" mapping sample as your starting point.** The short `migration-samples/field-mapping-profile.csv` does not map every field a profile must have (source record id, category) and will not publish.

| File | Format | Notes |
|---|---|---|
| Contracts file | CSV (UTF-8 best; UTF-16 detected; Windows-1252 only if you choose it) or Excel .xlsx (never .xlsm) | One row per contract. A stable, unique **SourceRecordId** per row is its identity: loading the same row again is recognised, not duplicated. Limits (defaults): 250,000 rows, 500 columns, 32,768 characters per cell, 500 MB. A value longer than its field is rejected, never shortened |
| Attachment manifest (optional) | CSV or .xlsx | Columns: SourceRecordId, AgreementReference, RelativePath, FileName, DocumentType, DocumentDate, VersionLabel, IsExecutedCopy, ParentDocumentSourceId, Sha256, Notes. May instead sit at the root of the ZIP as attachment-manifest.csv |
| Attachment archive (optional) | ZIP | Folder per contract, for example DEMO-AGR-0001/DEMO-AGR-0001_executed.pdf. Only allowed file types whose content matches their type; unsafe paths, encrypted or corrupt archives and decompression bombs are refused per entry. Files are read in memory and held in restricted staging; nothing is extracted to disk or imported until commit |
| Field mapping file | CSV | Exact columns: SourceColumn, TargetField, Transformation, DefaultValue, Required, LookupMapKey, Validation, Notes |
| Lookup file | CSV | Exact columns: LookupMapKey, SourceValue, TargetCode (and optional Notes) |

A mapping must set at least: sourceRecordId, agreementReference, contractTitle, status, supplier.name and category.name. The transformations are a closed list (Trim, NormaliseWhitespace, Upper, Lower, TitleCase, ParseDate, ParseDateTime, ParseDecimal, ParseInteger, MapBoolean, Concat, Split, FixedDefault, LookupMap; chain with plus). There are no formulas or scripts; anything that needs logic is done in the spreadsheet beforehand. Dates always need an explicit format, for example Format=yyyy-MM-dd in the Validation column; the platform never guesses day and month order.

### Examples for the three cases

| | **Blank** (neutral) | **JBS-style** (jbs-procurement; Australian English, AUD, Australia/Sydney) | **Harborline-style** (construction-subcontracts; US, USD, America/Chicago, en-US) |
|---|---|---|---|
| Source columns | AgreementRef, Title, Counterparty, Class, State, Start, End, Value | The sample contracts-import.csv columns (SourceRecordId, AgreementReference, ContractTitle, SupplierName, Category, Subcategory, Status, dates, Currency, EstimatedValue) | SubcontractNo, Description, Subcontractor, TradePackage, Status, ExecutedOn, FinishOn, ContractSum |
| Date row in the mapping | ParseDate, Validation Format=yyyy-MM-dd | ParseDate, Validation Format=yyyy-MM-dd (as the sample) | ParseDate, Validation Format=MM/dd/yyyy (the format letters are .NET-style; only yyyy-MM-dd is covered by the shipped sample, so confirm on the dry run's sample of original and transformed values) |
| Currency | Currency column, or DefaultValue set to the organisation's currency | DefaultValue AUD | DefaultValue USD |
| Status lookup (source to workflow state code) | Active to Active; Closed to Closed; Cancelled to Cancelled | Effective to Effective; Expired to Ended; Terminated to Ended (as the sample) | Executed to Active; Complete to Completed; Terminated to Terminated |
| Allowed legacy states (section 6.2) | Active, Closed | Effective, Ended | Active, Completed |
| Category on a new organisation | Not yet created: tick "Create missing categories" | Pack categories arrive through onboarding; tick the option if they are not there yet | Eight trade packages arrive through onboarding or the option; the label on the screen reads "trade packages" |

The state codes above are those in the packs' lifecycles (Blank: Open, Active, Amending, Closed, Cancelled; JBS-style: Draft ... Effective ... Ended; Harborline-style: Draft ... Active ... Completed). **A status lookup must map every old status to a state that exists in the organisation's published workflow**, otherwise MIG-E008 or MIG-E004 appears. Wording on the screens follows the organisation's terminology (for example "supplier" or "subcontractor", "category" or "trade package", "batch"); this guide uses the neutral words.

---

## 4. Create the migration project

**Navigate:** menu **Data migration**, page **/Migration**. **Who:** Procurement Administrator. **Permission:** migration.stage. **Prerequisites:** section 3 decisions made.

**Purpose.** A project holds one organisation's migration: its source system, data owner, extraction date and the batches.

Section "Start a migration project":

| Field | Required | Enter | Blank | JBS-style | Harborline-style | Rule and consequence |
|---|---|---|---|---|---|---|
| Organisation | Yes | The receiving organisation (only organisations you may stage for are listed) | Example Co | Example Foods Pty Ltd | Harborline Construction | Wrong organisation means records land in the wrong place; stop and roll back |
| Migration project name | Yes | Source system and year, up to 200 characters | Example Co register migration 2026 | JBS reference register cutover 2026 (synthetic) | Harborline subcontract register cutover 2026 | Label only |
| Source system | Yes | The old system's plain name, up to 100 characters | Legacy Spreadsheet Register | Legacy Contract Register | Legacy Subcontract Tracker | Part of every record's identity (with the source record id). **Changing it later makes the same rows look new**, so decide once |
| Source system version (optional) | No | If known | v1 | v1 | v1 | Provenance only |
| Data owner | Yes | A named business person, up to 200 characters | Head of Operations | Group Head of Procurement (role holder) | Commercial Administrator lead | The person accountable for the source data and for accepting the result |
| Date the data was extracted (UTC) | Yes | The real extraction date | 2026-11-01 | 2026-11-01 | 2026-11-01 | Misleads the freeze and reconciliation story if wrong |

**Create migration project** saves it and opens the project page with: "The migration project was created. Next, set the reference policy and, if needed, the legacy-load policy, then create a batch." The table above the form lists existing projects (Source system, Data owner, Extracted, Batches, Status).

**Common errors.** A missing required field is shown beside the form; choose the organisation again if the list is empty (you may stage for none, ask the Tenant Administrator to give you the Procurement Administrator role for that organisation). **Evidence to keep:** the project page.

---

## 5. Mapping profiles

### 5.1 Create a profile

**Navigate:** **Data migration**, then **Mapping profiles**, page **/Migration/Profiles**. **Who/permission:** Procurement Administrator, migration.stage.

The list shows Profile, Source system, Latest version, Published versions. Section "Create a profile":

| Field | Required | Enter | Blank | JBS-style | Harborline-style | Consequence |
|---|---|---|---|---|---|---|
| Organisation | Yes | The receiving organisation | Example Co | Example Foods Pty Ltd | Harborline Construction | A profile for the wrong organisation cannot be used on the project |
| Profile name | Yes | Source to platform and a version, up to 200 | Spreadsheet register to platform v1 | Contract register to platform v1 | Subcontract tracker to platform v1 | Unclear names cause wrong choices later |
| Source system | Yes | Same wording as the project | Legacy Spreadsheet Register | Legacy Contract Register | Legacy Subcontract Tracker | Provenance reads wrongly if different |
| Source system version (optional) | No | | v1 | v1 | v1 | |

**Create profile** creates the profile with an **empty draft** and opens its page: "The profile was created with an empty draft. Load your field mappings and lookups into it."

### 5.2 The profile page: load, set options, check, publish

**Page:** **/Migration/Profiles/{id}** (open from the list). The page shows the **Versions** table (Version, Status Draft or Published, Field mappings, Lookup entries, Created, Published) and the selected version. A published version says "(published, unchangeable)". If no draft exists, **Start a new draft from the latest version** copies it into a new draft.

In a draft, in this order:

1. **Load the field mappings.** Choose the CSV (section 3) in "Field mapping file (.csv)" and press **Load field mappings**. Loading **replaces** the mappings in this draft. Up to 5 MB. Success: "The field mappings were loaded into the draft."
2. **Load the lookups.** Choose the lookup CSV in "Lookup file (.csv)" and press **Load lookups**. Replaces the lookups in this draft. Success: "The lookups were loaded into the draft." Up to five notes about the file may be added to the message.
3. **Options** (press **Save options**):

| Option | Enter | Blank | JBS-style | Harborline-style | Consequence |
|---|---|---|---|---|---|
| Create categories that do not exist yet (otherwise an unknown category is an error) | Tick or leave clear | **Tick** for a new organisation with no categories | Tick only while the categories are not yet set up; clear for the final load | Tick for the first rehearsals if trade packages are not yet set up; clear for the final load | Clear: an unknown category rejects the row with **MIG-E011**. Ticked: a new top-level category or subcategory is created (the dry run shows "will be created with code ..."). Its **name is the mapped value** (after any lookup) and its **code is that value in capitals with other characters replaced by dashes** (for example "Flexible Packaging" gives FLEXIBLE-PACKAGING). A lookup that turns "Packaging" into PACKAGING therefore creates a category named PACKAGING, so omit the category lookup rows when you want source names kept. Typos become new categories, so clear the option for the final load. A rolled-back batch removes the categories it created |
| Extension types that legitimately have no end date (comma separated) | Names of evergreen types | Evergreen | Evergreen | Evergreen | A blank end date for these types is an expected note (MIG-W003) instead of a problem |
| Attachment naming rule (optional pattern) | Leave empty unless file or folder names carry the id | empty | empty | empty | A pattern with a group named sourceRecordId or agreementReference, tried only when the manifest does not say which contract a file belongs to; it runs on a safe engine with a 250 ms limit. A wrong pattern leaves files unmatched |

4. **Read the Checks.** Problems appear as "Fix" (blocks publication) or "Note", each naming the mapping row and field, for example a transformation not on the safe list, a target field that does not exist, a date mapping with no Format, a duplicate target, a lookup key with no entries, a required target not mapped. When clear it says "All checks pass".
5. **Publish this version.** Optional **Note** (up to 1,000 characters), then **Publish**. The button is disabled while any Fix remains. Success: "The version was published. It can no longer be changed; a batch can now use it." A mistake is corrected by **Start a new draft**, fixing, and publishing a new version; batches already pinned to the old one keep it.

Tables below show **Field mappings** and **Lookups** with **Download as CSV** buttons to keep a copy as evidence.

**Evidence to keep:** the downloaded mappings and lookups CSV, the version number, the publish note.

---

## 6. Project settings

**Navigate:** **Data migration**, then the project's name (page **/Migration/Projects/{id}**). The top shows the Source system, Data owner, Extracted, Status, Reference policy, Legacy-load policy and Cutover freeze, then the Batches table (Batch, Kind, Status, Created, Rows, Created / updated; a "Legacy load" badge marks those batches).

### 6.1 Reference policy

Select "Reference policy" and press **Save reference policy** (needs migration.stage).

| Choice | When | Consequence |
|---|---|---|
| Keep the source reference where it is free | Normal; the old reference is already unique | A reference already in use triggers the duplicate rule of the batch |
| Generate a new reference from the numbering rule | Old references collide or the business wants new ones | The organisation's numbering rule allocates references (for example the pack's prefix JBSAUS or HBL followed by digits) |

In both cases the original reference is kept separately on each record.

### 6.2 Legacy-load policy (only for contracts already Effective or later)

A legacy load creates **locked** records, in a state other than the initial one. It needs this policy, the legacy-load permission, a batch marked as a legacy load, and a second person's confirmation. The form appears only to a person who holds both the stage and legacy-load permissions.

| Field | Required | Enter | Blank | JBS-style | Harborline-style | Consequence of a wrong value |
|---|---|---|---|---|---|---|
| Target states a legacy load may create (comma separated) | Yes | The smallest list of state codes | Active, Closed | Effective, Ended | Active, Completed | A record in any other state is rejected (MIG-E008) |
| Expires (UTC date) | Yes | A few days after the planned final load | 2026-11-30 | 2026-11-30 | 2026-11-30 | An open-ended policy is a standing way to create locked records; it also lapses on its own |
| Keep the source registered date | No | Tick only if the old date is authoritative | clear | tick if authoritative | clear | Clear: the platform's own registration time is used and warning MIG-W005 is recorded. The source date is never made up when the source has none |
| Allow an update to overwrite locked records already in the platform | No | Leave clear | clear | clear | clear | Ticked: a load may change locked contracts (an audited exception, not a routine) |
| Reason for allowing overwrites | Only if the above is ticked | Why | blank | blank | blank | Needed when ticked |

Press **Save legacy-load policy**: "The legacy-load policy was saved. A legacy load also needs a second person to confirm it before it is committed." After go-live press **Close the legacy-load policy** so no more locked records can be loaded.

### 6.3 Cutover freeze

Form "Cutover freeze" (needs migration.commit; project Active). **Starts (UTC)** and **Ends (UTC)** are entered in **UTC**, so convert from local time and check twice.

| | Blank | JBS-style (Australia/Sydney, UTC+11 in November) | Harborline-style (America/Chicago, UTC-6 in November) |
|---|---|---|---|
| Freeze starts local | your zone | 18:00 on 20 November 2026 | 18:00 on 20 November 2026 |
| Enter in UTC | convert | 2026-11-20 07:00 | 2026-11-21 00:00 |

Tick **Block contract creation by people during the window** to stop hand-keyed contracts in this organisation during the window (the migration itself is not blocked). **Save freeze window** confirms whether the target freeze is ON or off.

### 6.4 Close the project

When every batch is accepted, rolled back or compensated, press **Close the migration project**. This also closes the legacy-load policy and lifts any freeze (needs migration.accept).

---

## 7. Create a batch

**Navigate:** the project page, section "Create a batch". **Who/permission:** migration.stage (and legacy-load for the legacy tick).

| Field | Required | Enter | Blank | JBS-style | Harborline-style | Consequence |
|---|---|---|---|---|---|---|
| Batch name | No | A clear label, up to 200 | Rehearsal 1 | Rehearsal 1 | Rehearsal 1 | Unnamed batches show the start of their identifier |
| Kind | Yes | Dry run (validate only), Mock load (rehearsal), Final load | Dry run first | Dry run first | Dry run first | A dry run never writes and never commits. A mock load writes and can be rolled back. A final load is the cutover and is reversible only until accepted |
| Mapping profile version | No (choose later) | A published version, shown as "name, version n" | Spreadsheet register to platform v1, version 1 | Contract register to platform v1, version 1 | Subcontract tracker to platform v1, version 1 | Only published versions are offered. The batch keeps the version; later changes to the profile do not alter it |
| When a source record has been loaded before, or its reference is in use | Yes | Duplicate rule | Skip existing | Skip existing | Skip existing | See the table below |
| When a document's content is already in the platform | Yes | Document duplicate rule | Link the existing content | Link the existing content | Link the existing content | Add a new version, link the existing content, reject, keep separate with a warning, or hold for a person |
| This is a legacy load (tick) | Only for Effective or later | Appears only if the project has an active legacy policy and you hold the permission | tick | tick | tick | A legacy batch needs a second person's confirmation before commit |

Duplicate rules: **Skip existing** (re-running the same file changes nothing; normal), **Update mapped fields** (changes only mapped fields, keeps before-images, refuses locked records with MIG-E012 unless the policy allows overwriting), **Reject** (every overlap is an error), **Create with a new reference** (the numbering rule allocates one; the old one is kept), **Manual review** (every duplicate waits for a person; the batch cannot commit until decided).

Press **Create batch**: "The batch was created. Load the source files next." The batch page opens.

---

## 8. The batch page (**/Migration/Batches/{id}**)

Heading shows the batch name, status badge (Created, Sources loaded, Validated, Committing, Committed, Reconciled, Accepted, Rolled back, Compensated, "Failed (can be resumed)"), kind and, for a legacy load, "Legacy load: needs a second person to confirm". A step list links to eight sections.

### 8.1 Source files (section 1)

The table lists each loaded file: Role, File, Rows or entries, SHA-256 (first part), Loaded by whom and when. Each file is kept exactly as supplied. **Loading a file writes nothing to the platform.**

| Form | Field | What it does | Consequence |
|---|---|---|---|
| Contracts file | "CSV or Excel (.xlsx) file"; under **Reading options**: Delimiter (Detect, Comma, Semicolon, Tab, Pipe), Text encoding (Detect UTF-8 or UTF-16, or Windows-1252 only if confirmed), Excel sheet (blank for the first visible sheet) | **Load contracts file** (or **Replace contracts file**). Success: "The contracts file was read: n row(s) are in staging with their source values kept. Next, make sure a profile is chosen and run the dry run." | Same file twice changes nothing; a different file replaces the earlier one and withdraws any confirmation. A wrong delimiter shows as ragged rows (MIG-E013) |
| Attachment manifest | CSV or .xlsx | **Load manifest** / **Replace manifest**. Optional if the ZIP holds attachment-manifest.csv at its root | A manifest row that names a file not in the ZIP is reported (MIG-F009) |
| Attachment archive (ZIP) | ZIP | **Load archive** / **Replace archive**. Every entry is checked for unsafe paths, size, type, content and malware before being held. Success: "The archive was checked and its n entries are held in restricted staging. Nothing has been imported." | Unsafe, encrypted, oversize or mismatched entries are marked invalid individually, not the whole batch |

Examples for the sample set: contracts-import.csv gives 2 rows; sample-attachments.zip holds the manifest (3 rows) and 3 files for DEMO-AGR-0001 and DEMO-AGR-0002. **Blank, JBS-style and Harborline-style** use their own files with the same method.

Loading is possible only while the batch is Created, Sources loaded or Validated; after the commit starts the inputs are frozen ("A batch in state ... can no longer take new source files").

### 8.2 Mapping profile and duplicate options (section 2)

Shows the chosen Mapping profile and version, the Configuration release captured with the batch, the Reference policy and both duplicate rules. **Choose a published profile version** and **Use this version** to pin it (message: "Run the dry run again to see its effect"). **Manage mapping profiles** opens section 5. The second form changes the duplicate rules (**Save**); run the dry run again after any change.

### 8.3 Dry run (section 3)

Press **Run the dry run** (**Run the dry run again** afterwards). It needs the contracts file and a chosen profile version. It reads the staged files, maps and checks every row, resolves suppliers and categories, applies the duplicate rules and the legacy policy, and matches attachments. **It writes nothing to the platform, sends no notifications and creates no reminders**; run it as often as you like. If it cannot start it lists the blockers (for example no profile, no contracts file, or the organisation has no published lifecycle, MIG-E026).

Success message: "The dry run is complete: n row(s) would load, n would be rejected, n skipped. Nothing was written to the platform."

Read the result cards: source rows; would load (new and updated); rejected with errors; skipped; waiting for a decision; suppliers to create (existing linked); categories to create (existing linked); documents to import (matched, unmatched, invalid, duplicates); manifest rows (paired, naming a missing file, invalid). Then read **What each mapping does** (per mapping: rows, changed, default used, problems) and the **Sample of original and transformed values** for the first three rows. **Check the dates and amounts here:** this is where a wrong date format shows before anything is loaded.

### 8.4 Review: read the errors and fix them (section 4)

Buttons: **Errors and warnings** (with counts), **Source rows**, **Attachments and the unmatched queue**, **Provenance**, **Download the error report (Excel)** and **(CSV)**.

**Errors and warnings** (**/Migration/Batches/{id}/Messages**). Filter by Severity (All, Errors, Warnings) and by Code (for example MIG-E004), then **Filter**. Columns: Severity, Code, Applies to (the source record or file), Where (row or file), Field, Message. **Download Excel** / **Download CSV** save the whole report (also named on the batch page), which includes the source values of rejected rows; cells that begin with a formula character are defused so a spreadsheet never evaluates them. An **error rejects the row or file it concerns; a warning is information.**

**How to fix: always at the source.** Do not try to repair the data inside the platform. Fix the contracts file (or the profile), then either use **Replace contracts file** and **Run the dry run again** on the same batch (allowed until the commit starts), or create a new batch for a clean evidence trail per cycle (what the rehearsal does). If the fix is in the mapping profile, publish a new profile version, **Use this version** on the batch and dry-run again.

| Code | What it means | Fix |
|---|---|---|
| MIG-E001 | A required value is missing | Fill the cell in the source |
| MIG-E002 | A date could not be read with the mapping's format | Correct the cell, or the Format in the mapping (new profile version) |
| MIG-E003 | A number could not be read | Remove text, thousands marks or currency symbols from the cell |
| MIG-E004 | A value has no lookup entry (for example a status) | Add the entry to the lookup file and publish a new profile version, or correct the cell |
| MIG-E005 | The same source record id appears twice in the file | Remove or renumber the duplicate row |
| MIG-E006 | Reference or source record already loaded | Choose a different duplicate rule, or change the reference policy |
| MIG-E008 | The status is not valid for the organisation's workflow or not allowed by the legacy policy | Correct the status lookup, or add the state to the legacy policy |
| MIG-E009 | A value is longer than its field allows (never shortened) | Shorten it in the source |
| MIG-E010 | A supplier name matches an existing supplier with a different supplier number | Correct the number or name in the source, or resolve the supplier first |
| MIG-E011 | The category or subcategory does not exist (or is inactive or not under the stated parent) | Create it under Categories, correct the lookup, or tick "Create categories that do not exist yet" in the profile options (new version), as section 5.2 describes |
| MIG-E012 | Legacy-policy violation, or an update would overwrite a locked record | Check the policy dates and states; do not allow overwrites except by a recorded decision |
| MIG-E013 | A ragged row (more fields than headers) or a duplicate column header | Check the delimiter choice and quoting; remove duplicate headers |
| MIG-E014 | The mapping or lookup file lacks a required column | Use the exact column names in section 3 |
| MIG-E020 to E025 | An additional (custom) field problem: unknown key, retired field, value not a defined choice, type or limit broken, field type not loadable | Correct the key in the mapping or publish the field first; use a LookupMap for choice wording |
| MIG-E026 | The organisation has no published contract lifecycle | Publish the lifecycle first (Pack Manager guide, section 9), then dry-run again |
| MIG-E027, E028 | A tax identifier does not fit the organisation's rule, or none is set up | Correct the value, or set up the organisation's tax identifier type first (docs/TAX_IDENTIFIERS.md) |
| MIG-W001 to W009 | Information: value normalised, default applied or row skipped (nothing would change), blank end date for an evergreen type, registered date earlier than effective date, source registered date not kept, manifest read from the archive, hidden spreadsheet row, reader notices, missing parent document | Read them; no action is required, but a surprising warning is worth a look |
| MIG-F001 to F006 | A file or archive entry is unsafe, of a disallowed type or mismatched content, empty, corrupt or encrypted, or over a limit | Replace the file with a clean copy; remove it from the archive |
| MIG-F007, F008 | Duplicate file content, or a repeated file name within a contract | Governed by the document duplicate rule |
| MIG-F009 to F012, F015 | Manifest row with no file, file with no manifest row or no match, ambiguous match, target contract rejected | Use the Attachments page below |
| MIG-F013, F014 | The scan found an infection, or no scanner could scan the file | Infected files are never imported. Unscanned files are held; in Production they stay held until a scanner adapter exists |
| MIG-F017 to F019 | Declared checksum differs, duplicate archive path, invalid manifest row | Correct the manifest or archive |

The full list is in docs/MIGRATION.md section 11.

**Source rows** (**/Migration/Batches/{id}/Rows**). Filter by Outcome and by "Only rows that need a decision". Columns: Row, Source record, Reference, Outcome, What will happen, Problems, Decision. For a row that waits for a person (from the Manual review rule) choose Skip, Update the existing record, Create with a new reference or Reject, then **Decide**: "Your decision was recorded. Run the dry run again to apply it." The batch cannot commit while any row is waiting.

**Attachments and the unmatched queue** (**/Migration/Batches/{id}/Files**). Each file is matched in a fixed order: the manifest's SourceRecordId, the manifest's AgreementReference, the profile's naming rule, then the exact folder name; the reason is shown and **the platform never breaks a tie**. For a file in the queue (Unmatched, with its reason) you can: choose a contract from the list and press **Match to this contract**; press **Import it as a separate document** (only for a file held as a duplicate); or enter a reason and press **Discard** (recorded; the file stays in staging marked discarded). Filters: State and "Only files waiting for a person". Each decision changes the plan, so run the dry run again. The manifest table is shown below the files.

**Provenance** (**/Migration/Batches/{id}/Provenance**): what the batch created, updated or linked (see section 8.9). Available at any time.

### 8.5 Data handling and the masked-data checkpoint (Test mode only)

**Navigation.** Declarations: **Data migration**, then **Masked-data declarations** (**/Migration/MaskedData**). Classification: the **Data handling** section of the batch page (**/Migration/Batches/{id}**). **Who / permission.** A Procurement Administrator with `migration.stage` for the organisation. **Status.** Implemented, Test mode only. Demonstration has no checkpoint (synthetic data only). In Production the section says that Production data follows Production governance, and a batch marked Masked Company Data is refused.

**Customer responsibility.** The **customer masks the data, outside the platform**. The platform does not mask, detect or de-identify, and it never states that data is safe. A declaration is a record that your organisation did the masking and that a named person approved loading it; it is not evidence that the masking was good. Nothing here is automatic: no field is scanned, nothing is classified for you, and nothing is blocked or allowed unless a person recorded a classification.

1. **Make a declaration** on **/Migration/MaskedData** (choose the organisation, then **Make a declaration**). The fields are: dataset owner, source system, how it was masked, names of the fields masked, whether attachments, personal data and confidential commercial data remain, your approval reference, approved by, approval date, **Expires (review by) on**, and the people who may load it (email addresses; empty means anyone who may commit a batch). Press **Record the declaration**. The list shows each declaration as Current, Expired or Revoked with the number of batches using it.
2. **Classify the batch** in **Data handling**: choose **Synthetic data (made up for the rehearsal)** or **Masked Company Data (needs a declaration)**, choose the declaration, press **Save classification**. The batch shows who set it and when, and the declaration's state. A batch that has started committing cannot be re-classified. Some installations require every batch to be classified before it is committed; the page says so.
3. **If the scanner is the demonstration scanner**, a person with the System Administrator role must first accept its use for the rehearsal on the record (who, reason, scope and an expiry of at most 90 days) on **Administration, Operations, Scanner** (**/Admin/Operations/Scanner**; see docs/OPERATIONS_QUICK_GUIDE.md section 11.1). The mock checks nothing real and is no protection. Ask them; a Procurement Administrator cannot do it.
4. **Commit.** In Test mode a batch marked Masked Company Data is refused unless the declaration is current, approved and not revoked, permits this environment and, if it lists people, lists you, and the mock scanner (if that is all there is) has been accepted. A refusal is recorded on the audit trail and shown as a code and a sentence:

| Code | Meaning | What to do |
|---|---|---|
| MDD-001 | The declaration cannot be found, or belongs to another organisation | Choose or make a declaration for this organisation |
| MDD-002 | The declaration has expired or been revoked | Make a new declaration and link it |
| MDD-003 | The installation requires a classification and the batch has none | Choose Synthetic, or Masked Company Data with a declaration |
| MDD-004 | The declaration does not permit this environment | Make a declaration that permits Test |
| MDD-005 | The declaration limits who may load, and you are not listed | Ask the dataset owner to make a declaration that names you |
| MDD-006 | Only the demonstration scanner is connected and its use has not been accepted | The System Administrator records the acceptance, or a real scanner is connected |
| MDD-010 | Masked company data is a Test classification and this is Production | Remove the classification; Production data follows Production governance |
| MDD-011 | The batch has started committing and cannot be re-classified | Create a new batch |

5. **Revoke** a declaration on the same list (a **Reason** is required, up to 500 characters). Revoking blocks every batch that depends on it from committing, with MDD-002. A legacy load still needs its second-person confirmation as in section 8.6; the declaration does not replace it.

**Evidence to keep.** The declaration (screenshot), the batch's Data handling section, the scanner acceptance record, and any MDD refusal code with its time.

### 8.6 Confirm and commit (section 5)

For a dry run the page says it never commits. For a mock or final load:

1. **Legacy load only.** The page shows "Not confirmed" until a **different person** with the legacy-load permission opens the same batch and presses **Confirm this legacy load** ("You confirmed this legacy load. Someone else must now commit it."). The badge then reads "Confirmed by ... on ...". The person who confirms cannot commit.
2. The committer presses **Commit this batch**. The text above it tells you how many rejected and skipped rows will not be loaded and how many unmatched files will stay in the queue. The button is offered only when: the batch is Validated, nothing is waiting for a decision, the legacy confirmation is current, and the installation is not in maintenance mode. In Test mode a batch marked Masked Company Data also needs the checkpoint in section 8.5; if it refuses, you get an MDD code and a sentence. If you cannot see the button, the page says why.
3. The commit runs in chunks of 500 with a checkpoint each, so a stopped commit carries on and never creates a record or file twice. A progress table shows each stage (Contracts, then Documents), the last completed item and whether it is done.
4. If it stops, the status reads "Failed (can be resumed)" and "The commit stopped" gives the reason. Fix the cause and press **Resume the commit**. Nothing is lost or duplicated.

Success: "The batch was committed. Reconcile the totals next."

### 8.7 Reconcile (section 6)

Available once Committed. The page shows Balanced or "Does not balance" with the arithmetic: source rows = accepted + rejected + skipped; accepted = created + updated. **Open the reconciliation report** (**/Migration/Batches/{id}/Reconciliation**) for rows, manifest and files totals, source against target by status, category and value by currency, outstanding items, and **Download Excel** / **Download CSV**. Press **Save the reconciliation** (needs migration.commit).

If it does not balance, fix the cause if you can. Otherwise a person with migration.accept enters **Reason for approving these exceptions (recorded)**, ticks "I approve the exceptions listed and accept that the batch is reconciled with them", and presses **Save the reconciliation**. Success: "The totals balance. The batch is reconciled." or "The exceptions were approved and recorded. The batch is reconciled."

### 8.8 Business acceptance (section 7)

Available once Reconciled, to a person with migration.accept. Field **What the business owner checked (recorded)** (required, at least a sentence; ten characters minimum): say which records and documents were compared with the source and by whom, naming the data owner. Tick "I accept any rejected rows and unresolved files as exceptions" if any remain. Press **Accept this batch**: "Business acceptance was recorded. The batch is final and can no longer be rolled back."

Examples of an acceptance note: Blank, "Head of Operations compared 20 records and their documents with the old spreadsheet on 2026-11-22; no differences." JBS-style, "Group Head of Procurement's delegate compared 25 contracts across all categories; two rejected rows accepted as exceptions." Harborline-style, "Commercial Administrator lead compared 15 subcontracts across all trade packages; no differences."

**Acceptance is final for rollback.** After it only compensating archival is possible (section 8.10).

### 8.9 Provenance and evidence

**Provenance** (**/Migration/Batches/{id}/Provenance**) lists every supplier, category, contract and document the batch created, updated or linked, with the source system and source record id, a content hash, and the rollback state, with **Download Excel**. Contract record names link to the contract; a contract's own page shows "Migration provenance" (source system, source record id, batch, and whether the registered date came from the source system or from this platform). The audit screen can be filtered by batch (**/Admin/Audit**).

### 8.10 Roll back or compensate (section 8)

* **Roll back (before acceptance).** The panel "What a rollback would do" lists, per kind of record, the action, the count and why: untouched records the batch created are removed; contracts or documents that have since moved on (workflow activity, later versions, attachments) are **archived, never deleted**; suppliers and categories other records use are **kept**; updates are restored from their before-images only if nothing changed since. Records that were there before and other batches are never touched. Enter **Reason (recorded)** and press **Roll back this batch**: "The batch was rolled back: n reversed, n archived, n kept, n could not be reversed."
* **Compensating archival (after acceptance).** Enter a reason and press **Archive this batch's records**. Nothing is deleted; the batch's contracts and documents are archived with the reason and the batch identifier; shared suppliers and categories stay.
* **Delete unneeded staged copies** (a finished batch only) removes restricted staging copies of files that were never imported. Files still in the unmatched queue are kept until someone resolves or discards them.

---

## 9. A worked cycle (synthetic)

1. Create the project (section 4) and a profile; load the complete mapping and lookup samples, tick "Create categories that do not exist yet" (new organisation), check, publish version 1.
2. Create batch "Rehearsal 1", kind Dry run; load contracts, manifest (inside the ZIP) and archive; **Run the dry run**. Say the status lookup lacks "Expired": Messages shows MIG-E004 on the affected rows.
3. Add the lookup row, publish version 2, **Use this version** (or create "Rehearsal 2"), dry-run again until the counts read as intended: rejected 0, waiting for a decision 0, unmatched 0.
4. Create "Rehearsal 3" as a Mock load (tick the legacy option if the rows are Effective or later), dry-run, have the second person confirm, commit, reconcile. Check a sample in Contracts. Roll back with a reason if you want a clean slate.
5. Create "Final" as a Final load, repeat, then **Accept this batch**. Close the legacy-load policy and the project when every batch is final.

In the scripted demonstration of the real screens, the two sample contracts and three documents loaded, reconciliation read source rows 2 = accepted 2 (created 2), manifest rows 3 paired 3, files 3 imported 3, and the batch was accepted (docs/MIGRATION.md section 12).

## 10. Evidence to keep

For each batch, keep with the migration runbook (docs/MIGRATION_RUNBOOK_TEMPLATE.md): source file names and SHA-256 values; the profile version and its downloaded mappings and lookups; the dry-run counts (screenshot); the error report (Excel or CSV) for every dry run; each decision on rows and files with reasons; the legacy-load policy and who confirmed; the commit progress and times; the saved reconciliation report (Excel or CSV) and any approved exceptions with the reason; the acceptance note and who recorded it; the Provenance export; any rollback or compensation reason. Never keep real customer data in rehearsal evidence.

## 11. If something goes wrong

| Situation | What to do |
|---|---|
| "Run the dry run" is missing or blocked | Load a contracts file and choose a published profile version; if MIG-E026 appears, publish the organisation's lifecycle first |
| A mapping will not publish | Read the Checks list on the profile page; every "Fix" names the row and field |
| Most rows rejected with the same code | Fix the common cause once (a lookup, a date Format, the category option) and re-run |
| Wrong file loaded | Use **Replace** (before the commit starts); the confirmation is withdrawn. After commit, roll back and start a new batch |
| Wrong organisation or profile | Roll back before acceptance; create the batch in the right project |
| The commit stopped | Read "The commit stopped", fix the cause, **Resume the commit** |
| Commit button missing | The page says why: not validated, a row waits for a decision, a legacy load is unconfirmed, the confirmer is also the committer, or maintenance mode is on |
| Totals do not balance | Fix the source and re-run; otherwise approve the listed exceptions with a reason |
| Commit refused with an MDD code (Test mode) | Section 8.5: read the code, fix the declaration, classification or scanner acceptance, and try again |
| Attachments held in Production | No real malware scanner is supplied (**Not Verified / Future adapter**); a Production decision is needed before attachments can be imported |
| Accepted a batch by mistake | Rollback is no longer possible; use compensating archival with a reason, then load correctly in a new batch |
| "Forbidden" or "not found" | Your role lacks the permission or the organisation is outside your scope; ask the Tenant Administrator |

## 12. What was verified and what was not

* **Verified by reading the code and samples:** every screen, field, button, label, message and permission above was checked against src/Ecp.Host/Pages/Migration (the project, profile, batch, messages, rows, files, provenance and reconciliation pages), the batch capability rules in src/Modules/Ecp.DataMigration, docs/MIGRATION.md, section 14 and 15 of docs/IMPLEMENTATION_PLAYBOOK.md, and the files under migration-samples. The category-creation behaviour (name from the mapped value, code derived from it) and the commit gating were read in the module code.
* **Not verified:** nobody has followed this guide unaided in a browser; no assistive-technology test; the Harborline-style date format MM/dd/yyyy is not covered by a shipped sample (only yyyy-MM-dd is), so test it on a dry run; the JBS-style and Harborline-style values in the examples are synthetic illustrations, not tested files; attachments through a real malware scanner (none is supplied); that any customer's masking method is adequate (the platform neither checks nor claims it); the masked-data pages driven by an unaided person; SQL Server proof depends on the disposable test container (docs/MIGRATION.md section 12.2); the exact wording of some messages generated by the service is paraphrased.
