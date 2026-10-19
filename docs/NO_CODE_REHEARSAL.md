# No-code implementation rehearsal

Status: **Implemented and Verified** for the journey below, over real HTTP: on the lightweight provider (labelled `SqliteNotSqlServerProof`) and on real SQL Server. **Not Verified**: a run by an unaided non-developer following the guides; a restore with real data inside this rehearsal (carried by the SQL Server recovery tests); a real malware scanner; a real identity provider; a real mail service (step 23 uses a loopback test mail relay inside the test, which proves the platform's SMTP client and readiness logic, not a customer's mail service); Microsoft Graph.

## What it proves

A synthetic company ("Rehearsal Company", Australian settings, on the reference procurement pack) is set up from nothing, and its register is loaded, by the people who would do it, using only the application's pages. The test signs in as real demo users through the real sign-in page and posts to the real pages with anti-forgery tokens. It does not call a service to do a business step, edit a source file, run SQL, run Git or type JSON for a business role. Where a form needs a value the person would type or tick, the test supplies the fields the form would post.

The rehearsal runs in Test mode with `Ecp:Governance:SecondPerson=Enforced`, which applies exactly the Production second-person rule (ADR-0038). Self-publication is attempted and refused; a legacy migration load is refused until a different person confirms it. In Demonstration mode the setting is ignored and governance stays simplified, so a Demonstration run does not prove these steps.

Role separation is asserted, not assumed: the ledger records the role and the page for every step, and the test fails if (a) a step fails, (b) a business-role step names anything other than an application page, (c) any step needs a technical role other than steps 17 and 21 to 23, or (d) a business role is a System Administrator.

## How the ledger is judged (evidence semantics, ADR-0038 D14.12)

The ledger keeps the operation's own outcome in one column and what it counts as in another, and never converts one into the other.

| Operation reported | Counts as |
|---|---|
| Ready, Verified, Passed, Completed | Passed |
| Ready with warnings | Passed only when every warning was explicitly accepted on the record (who, reason, expiry); otherwise Warning (not accepted) |
| Blocked, Failed | Failed |
| Not Verified | Not Verified |

Version 1 of this rehearsal recorded a Blocked upgrade pre-flight as a pass (finding G-37). Steps 21 to 23 now assert the real outcome, and a unit test (`EvidenceSemanticsTests`) and a ledger test (`RehearsalLedgerSemanticsTests`) hold the rule.

## Where the evidence is

The test writes the ledger as a table to the file rehearsal-evidence/no-code-rehearsal-sqlite.md or no-code-rehearsal-sqlserver.md beside the test binaries; the table below is the SQL Server run of this slice. Tests: `NoCodeRehearsalSqliteTests` and `NoCodeRehearsalSqlServerTests` in the API test project (`NoCodeRehearsalTests.cs`, `NoCodeRehearsal.cs`); the SQL Server host is `DemoApp.StartSqlServer()`.

## The twenty-three steps (SQL Server run)

- Steps: 23 (23 Passed, 0 Failed, 0 Not Verified, 0 Warning not accepted).
- Four steps need a technical role (17, 21, 22, 23); the other nineteen are business-role steps that name the page they used.

| # | Step | Who | Role | Technical role | Screen | Operational outcome | Verdict | Result |
|---|------|-----|------|----------------|--------|---------------------|---------|--------|
| 1 | Allow several organisations (only if the installation was set up for one) | Tenant Administrator | TenantAdministrator | no | /Admin/Organisations | Completed | Passed | Already supports several organisations. |
| 2 | Create the organisation: check the form, fix what it flags, create | Tenant Administrator | TenantAdministrator | no | /Admin/Organisations/New | Completed | Passed | Organisation RH72D2 created with structure, an initial administrator and the pack choice; nothing published. |
| 3 | Give people their roles in the new organisation | Tenant Administrator | TenantAdministrator | no | /Admin/Users/Details/{person} | Completed | Passed | One second configuration administrator and two procurement administrators were given their roles for this organisation only. |
| 4 | Read the set-up checklist and prepare the drafts from the pack | Organisation Administrator | OrganisationAdministrator | no | /Admin/Organisations/Setup, /Admin/Packs/Assign | Completed | Passed | Drafts prepared from the pack: WorkflowContractLifecycle, WorkflowVariation, NotificationPolicy, RegionalProfile |
| 5 | Regional settings: review, check and submit | Organisation Administrator | OrganisationAdministrator | no | /Admin/Regional | Completed | Passed | Submitted for review (not in force). |
| 6 | Terminology: keep the pack's wording, submit | Organisation Administrator | OrganisationAdministrator | no | /Admin/Terminology/Edit | Completed | Passed | Submitted for review (not in force). |
| 7 | Capabilities: confirm the pack's choices, submit | Organisation Administrator | OrganisationAdministrator | no | /Admin/Capabilities/Edit | Completed | Passed | Submitted for review (not in force). |
| 8 | Approver group: register it and add its people | Organisation Administrator | OrganisationAdministrator | no | /Admin/ApproverGroups, /Admin/ApproverGroups/Details | Completed | Passed | Group RehearsalRH72D2 registered with one approver, after a preview of who could approve before and after. |
| 9 | Contract workflow: change a button label in the form, check, submit | Organisation Administrator | OrganisationAdministrator | no | /Admin/Forms/Edit?kind=WorkflowContractLifecycle | Completed | Passed | Edited with fields and buttons; submitted for review. |
| 10 | Approval matrix: build one in the form (a matrix, a row, a step), check, submit | Organisation Administrator | OrganisationAdministrator | no | /Admin/Forms/Edit?kind=ApprovalMatrix | Completed | Passed | A matrix with one row and one step built entirely from fields; submitted for review. |
| 11 | Notification policy: reword a message in the form, read the preview, submit | Organisation Administrator | OrganisationAdministrator | no | /Admin/Forms/Edit?kind=NotificationPolicy | Completed | Passed | Message reworded in the form; sample shown before submitting; submitted for review. |
| 12 | Review inbox: read each plain-language summary and publish; the author cannot | Business Configuration Administrator | BusinessConfigurationAdministrator | no | /Admin/Reviews, /Admin/Reviews/Details | Completed | Passed | 5 submissions reviewed and published by a second person; authors were refused for their own work; the checklist shows them done. |
| 13 | Migration: create the project and the mapping profile from the sample files | Procurement Administrator | ProcurementAdministrator | no | /Migration, /Migration/Profiles | Completed | Passed | Project and an unchangeable mapping profile created from two spreadsheet files. |
| 14 | Migration: load a file with a mistake, read the error report, see that commit is refused | Procurement Administrator | ProcurementAdministrator | no | /Migration/Batches, error report | Completed | Passed | The error report names the row and the problem; the batch cannot be committed; the error report downloads as a workbook. |
| 15 | Migration: fix the source file, load it again in a new batch, dry-run it clean | Procurement Administrator | ProcurementAdministrator | no | /Migration/Batches | Completed | Passed | Dry run: nothing written; the page says what would load. |
| 16 | Data handling: say the file is masked company data; commit is refused until the declaration exists | Procurement Administrator | ProcurementAdministrator | no | /Migration/MaskedData, /Migration/Batches | Completed | Passed | Commit was refused in turn for no classification, a revoked declaration, a declaration that does not name the committer and, with a good declaration linked, the unaccepted demonstration scanner; each gave a code and a sentence. The declaration records owner, source, method, fields, approval and expiry, and holds no data. |
| 17 | Operations: accept the demonstration scanner for this masked-data rehearsal, on the record | System Administrator | SystemAdministrator (technical) | yes | /Admin/Operations/Scanner | Completed | Passed | The demonstration scanner was accepted for 14 days with a reason and a scope; the page still labels it TEST MODE and Not Verified. |
| 18 | Migration: a second person confirms the legacy load, then it is committed | Procurement Administrator (second person) | ProcurementAdministrator | no | /Migration/Batches | Completed | Passed | Committed after a different person confirmed; the committer alone was refused. |
| 19 | Migration: reconcile the counts and record acceptance | Procurement Administrator | ProcurementAdministrator | no | /Migration/Batches/Reconciliation | Completed | Passed | Counts balanced; acceptance recorded with a note. |
| 20 | UAT: find the loaded contracts, open one, see its documents and where it came from | Procurement Administrator (second person) | ProcurementAdministrator | no | /Contracts, /Contracts/Details, /Suppliers | Completed | Passed | Both contracts, the counterparties and the attachments are visible; a document downloads; provenance is shown. |
| 21 | Operations: open the dashboard and take a verified backup | System Administrator | SystemAdministrator (technical) | yes | /Admin/Operations, /Admin/Operations/Backups | Verified | Passed | Backup bk-20261019T023617Z-62f3a387 was taken and read back; its state is 'Verified'. |
| 22 | Operations: run the upgrade pre-flight against the verified backup | System Administrator | SystemAdministrator (technical) | yes | /Admin/Operations/UpgradeReadiness | Ready with warnings | Passed | Pre-flight outcome 'Ready with warnings' (0 blocker(s); 2 warning(s): UPF-040, UPF-056, each explicitly accepted with a recorded reason); counted as Passed (EVD-010). The evidence report downloaded. |
| 23 | Operations: set up email, send the test message, read the readiness | System Administrator | SystemAdministrator (technical) | yes | /Admin/Operations/Email | Passed | Passed | Email readiness 'Healthy': the loopback mail relay received one synthetic test message with no contract information; the page shows its reference, masked recipient and when a pass expires. |


## What the rehearsal does not cover

| Not covered | Why | Where it is carried |
|---|---|---|
| Installing the platform, the identity provider, secrets, proxy and TLS | Technical tasks for the System Administrator | `docs/INSTALLATION_GUIDE.md`, `docs/REVERSE_PROXY.md`; Not Verified end to end by a non-developer |
| A backup and restore with real data | Needs the operator's backup folder | `BackupRestoreSqlServerTests`; step 21 takes and verifies a backup, restore is carried by those tests |
| Signing a pack | Done by the Pack Publisher outside the application, with the standalone `ecp-pack` tool | `docs/PACK_SIGNING.md`; `PackToolArtefactTests` |
| A real malware scanner | None is integrated; step 17 accepts the labelled demonstration scanner for a masked-data rehearsal in Test only | `docs/DOCUMENT_SCANNING.md` |
| A real mail service or Microsoft Graph | Step 23 uses a loopback relay; both are Not Verified against real services | `docs/OPERATIONS_QUICK_GUIDE.md` |
| Business judgement: which groups, thresholds and wording are right | Decisions, not screens | `docs/IMPLEMENTATION_PLAYBOOK.md` |
| User acceptance by real users | A human activity | `docs/UAT_AND_CUTOVER_GUIDE.md` |

## Reading the result honestly

Passing this test says the supported pages, together, let the roles named above do the journey with no code. It does not say a company can do it without training, that the guides are complete for a particular company, or that the platform is approved for production. Production approval is external governance.
