# No-code implementation rehearsal

Status: **Implemented and Verified** for the journey below, over real HTTP: on the lightweight provider (labelled `SqliteNotSqlServerProof`) and on real SQL Server. **Not Verified**: a run by an unaided non-developer following the guides; a restore with real data inside this rehearsal (carried by the SQL Server recovery tests); a real malware scanner; a real identity provider; real e-mail delivery.

## What it proves

A synthetic company ("Rehearsal Company", Australian settings, on the reference procurement pack) is set up from nothing, and its register is loaded, by the people who would do it, using only the application's pages. The test signs in as real demo users through the real sign-in page and posts to the real pages with anti-forgery tokens. It does not call a service to do a business step, edit a source file, run SQL, run Git or type JSON for a business role. Where a form needs a value the person would type or tick, the test supplies the fields the form would post.

The rehearsal runs with the second-person rule on, exactly as Production does. Self-publication is attempted and refused; a legacy migration load is refused until a different person confirms it.

Role separation is asserted, not assumed: the ledger records the role and the page for every step, and the test fails if (a) a step fails, (b) a business-role step names anything other than an application page, (c) any step other than steps 19 and 20 needs a technical role, or (d) a business role is a System Administrator.

## Where the evidence is

The test writes the ledger as a table to the file rehearsal-evidence/no-code-rehearsal-sqlite.md or no-code-rehearsal-sqlserver.md beside the test binaries; the table below is the SQL Server run of this slice. Tests: `NoCodeRehearsalSqliteTests` and `NoCodeRehearsalSqlServerTests` in the API test project (`NoCodeRehearsalTests.cs`, `NoCodeRehearsal.cs`); the SQL Server host is `DemoApp.StartSqlServer()`.

## The twenty steps (SQL Server run)

| # | Step | Who | Role | Technical role | Screen | Result |
|---|------|-----|------|----------------|--------|--------|
| 1 | Allow several organisations (only if the installation was set up for one) | Tenant Administrator | TenantAdministrator | no | /Admin/Organisations | Passed: Already supports several organisations. |
| 2 | Create the organisation: check the form, fix what it flags, create | Tenant Administrator | TenantAdministrator | no | /Admin/Organisations/New | Passed: Organisation RH0308 created with structure, an initial administrator and the pack choice; nothing published. |
| 3 | Give people their roles in the new organisation | Tenant Administrator | TenantAdministrator | no | /Admin/Users/Details/{person} | Passed: One second configuration administrator and two procurement administrators were given their roles for this organisation only. |
| 4 | Read the set-up checklist and prepare the drafts from the pack | Organisation Administrator | OrganisationAdministrator | no | /Admin/Organisations/Setup, /Admin/Packs/Assign | Passed: Drafts prepared from the pack: WorkflowContractLifecycle, WorkflowVariation, NotificationPolicy, RegionalProfile |
| 5 | Regional settings: review, check and submit | Organisation Administrator | OrganisationAdministrator | no | /Admin/Regional | Passed: Submitted for review (not in force). |
| 6 | Terminology: keep the pack's wording, submit | Organisation Administrator | OrganisationAdministrator | no | /Admin/Terminology/Edit | Passed: Submitted for review (not in force). |
| 7 | Capabilities: confirm the pack's choices, submit | Organisation Administrator | OrganisationAdministrator | no | /Admin/Capabilities/Edit | Passed: Submitted for review (not in force). |
| 8 | Approver group: register it and add its people | Organisation Administrator | OrganisationAdministrator | no | /Admin/ApproverGroups, /Admin/ApproverGroups/Details | Passed: Group RehearsalRH0308 registered with one approver, after a preview of who could approve before and after. |
| 9 | Contract workflow: change a button label in the form, check, submit | Organisation Administrator | OrganisationAdministrator | no | /Admin/Forms/Edit?kind=WorkflowContractLifecycle | Passed: Edited with fields and buttons; submitted for review. |
| 10 | Approval matrix: build one in the form (a matrix, a row, a step), check, submit | Organisation Administrator | OrganisationAdministrator | no | /Admin/Forms/Edit?kind=ApprovalMatrix | Passed: A matrix with one row and one step built entirely from fields; submitted for review. |
| 11 | Notification policy: reword a message in the form, read the preview, submit | Organisation Administrator | OrganisationAdministrator | no | /Admin/Forms/Edit?kind=NotificationPolicy | Passed: Message reworded in the form; sample shown before submitting; submitted for review. |
| 12 | Review inbox: read each plain-language summary and publish; the author cannot | Business Configuration Administrator | BusinessConfigurationAdministrator | no | /Admin/Reviews, /Admin/Reviews/Details | Passed: 5 submissions reviewed and published by a second person; authors were refused for their own work; the checklist shows them done. |
| 13 | Migration: create the project and the mapping profile from the sample files | Procurement Administrator | ProcurementAdministrator | no | /Migration, /Migration/Profiles | Passed: Project and an unchangeable mapping profile created from two spreadsheet files. |
| 14 | Migration: load a file with a mistake, read the error report, see that commit is refused | Procurement Administrator | ProcurementAdministrator | no | /Migration/Batches, error report | Passed: The error report names the row and the problem; the batch cannot be committed; the error report downloads as a workbook. |
| 15 | Migration: fix the source file, load it again in a new batch, dry-run it clean | Procurement Administrator | ProcurementAdministrator | no | /Migration/Batches | Passed: Dry run: nothing written; the page says what would load. |
| 16 | Migration: a second person confirms the legacy load, then it is committed | Procurement Administrator (second person) | ProcurementAdministrator | no | /Migration/Batches | Passed: Committed after a different person confirmed; the committer alone was refused. |
| 17 | Migration: reconcile the counts and record acceptance | Procurement Administrator | ProcurementAdministrator | no | /Migration/Batches/Reconciliation | Passed: Counts balanced; acceptance recorded with a note. |
| 18 | UAT: find the loaded contracts, open one, see its documents and where it came from | Procurement Administrator (second person) | ProcurementAdministrator | no | /Contracts, /Contracts/Details, /Suppliers | Passed: Both contracts, the counterparties and the attachments are visible; a document downloads; provenance is shown. |
| 19 | Operations: open the dashboard, run the upgrade pre-flight, take the evidence | System Administrator | SystemAdministrator (technical) | yes | /Admin/Operations, /Admin/Operations/UpgradeReadiness, /Admin/Operations/Evidence | Passed: Pre-flight verdict shown as 'Blocked'; the evidence report downloaded; business administrators are refused these pages. |
| 20 | Operations: backups and restore are the System Administrator's; the page says what this database allows | System Administrator | SystemAdministrator (technical) | yes | /Admin/Operations/Backups | Passed: Backup page opened on SQL Server; backup and restore with real data are proved by the SQL Server recovery tests (BackupRestoreSqlServerTests), not repeated here. |


## What the rehearsal does not cover

| Not covered | Why | Where it is carried |
|---|---|---|
| Installing the platform, the identity provider, secrets, mail | Technical tasks for the System Administrator | `docs/INSTALLATION_GUIDE.md`; Not Verified end to end by a non-developer |
| A backup and restore with real data | Needs the operator's backup folder | `BackupRestoreSqlServerTests`; step 20 only opens the page |
| Signing a pack | Done by the Pack Publisher outside the application | `docs/PACK_SIGNING.md`; architecture test that the application holds no private key |
| A real malware scanner | None is integrated | `docs/DOCUMENT_SCANNING.md` |
| Business judgement: which groups, thresholds and wording are right | Decisions, not screens | `docs/IMPLEMENTATION_PLAYBOOK.md` |
| User acceptance by real users | A human activity | `docs/UAT_AND_CUTOVER_GUIDE.md` |

## Reading the result honestly

Passing this test says the supported pages, together, let the roles named above do the journey with no code. It does not say a company can do it without training, that the guides are complete for a particular company, or that the platform is approved for production. Production approval is external governance.
