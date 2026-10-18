# User acceptance testing, cutover and hypercare: click-by-click guide

**Purpose.** Prove, through the application's own pages, that a configured installation matches how the business works; collect the evidence for a go/no-go decision; move from the old register on a planned date with a way back; and watch the first weeks. This guide turns sections 16 to 18 of `docs/IMPLEMENTATION_PLAYBOOK.md` into steps, and every screen named here was checked against its page in the code (see "What was and was not verified" at the end).

**Who it is for.** The Business Lead (decides and signs), the Project Manager (runs the plan), the testers (one per role), the Business Administrator and Organisation Administrator (fix configuration), and the System Administrator (the technical steps, each labelled **[Technical task: System Administrator]** where it is not on a screen a business role can open). No programming, SQL, JSON editing, source change or Git is needed for anything in the main body.

**Prerequisites.**
- A non-production installation (Demo or Test mode, or a restored rehearsal copy) set up by the same steps as production, loaded with synthetic or approved data. Never use live confidential data in a Demo or Test installation.
- Configuration for every organisation published (by a second person), so that the pages below show it as "in force". See `docs/NO_CODE_REHEARSAL.md` for the 20-step journey that is the proof this set-up can be done through pages alone.
- A named tester for each role (section 2) and the "expected results" documents (approval design, workflow list, notification list, terminology list, regional settings).
- A way to record results and defects outside the application (section 3). The application has no defect tracker and no test-result page.

**Status label.** UAT scripts, cutover runbook and hypercare checklist: **Implemented as a process** using **Implemented** screens. Document scanning in Production: **Future** (no real scanner is supplied; see `docs/DOCUMENT_SCANNING.md`). IIS and Azure deployments: **Scaffolded / Not Verified**. Nothing in this guide, and nothing the application shows, is production approval.

> **Production approval is external governance.** Passing every script, a green dashboard, a verified backup and a signed UAT report are *evidence* for the people who decide. They do not approve, certify or authorise go-live. The decision belongs to your organisation's own governance (for example your change board, security and risk owners); the platform never declares readiness and neither does this guide.

---

## 1. Rules that apply to all testing

| Rule | What it means for a tester |
|---|---|
| Second-person publication | The person who last edited or submitted a configuration draft cannot publish it. Changing a draft after it was submitted voids the submission; a submitted change can be published only if its content hash still matches. A defect fix in configuration therefore needs two people: one to correct and submit, a different one to approve. |
| Recent sign-in (step-up) | Publishing, rolling back, creating organisations, emergency unlock, backups, restores, maintenance, upgrades and the support bundle ask you to confirm it is you (a **Confirm it is you** page at **/Reauth**), then return you. This is normal; the default window is 10 minutes. |
| Audit | Every key action is in **Audit events** (**/Admin/Audit**). Capture the entry for each action you test. |
| Times | Stored in UTC; pages show the organisation's configured time zone. Note both when you record a time. |
| Synthetic data | Use invented suppliers, people and contract titles. Never paste live confidential data into this environment or into a defect record. |

---

## 2. Roles and testers

Menu entries appear only for people who hold the permission, so what a tester sees is itself a test result.

| Tester | Role | Menu entries expected (from the page menu) | Notes |
|---|---|---|---|
| Contract owner | Category Owner (or the pack's term) | Contracts, Notifications, Dashboards, Suppliers | Holds contract create and read |
| Legal reviewer | Legal | Contracts, Legal (if the capability is on) | |
| Approver | Approver (one per approver group) | Contracts, Approvals (if register authorisation is on) | Cannot open the approval simulator |
| Registrar | Procurement Administrator | Contracts, Data migration, Notification delivery, Document scanning, Approval matrices (simulator), Categories | Holds `notifications.administer` |
| Business Configuration Administrator | Business Configuration Administrator | Configuration reviews, Workflow configuration, Configuration Studio, Regional settings, Terminology, Capabilities, Packs, Approval matrices | Cannot publish what they submitted |
| Organisation Administrator | Organisation Administrator | Users and roles, Approver groups, Audit events, Organisation onboarding, Configuration reviews and the configuration pages (within scope) | |
| Tenant Administrator | Tenant Administrator | Organisations, Users and roles, Approver groups, Audit events, Notification delivery, Document scanning, Configuration reviews and pages (only within the scope granted), Packs | Holds no contract read permission in the role matrix |
| Auditor | Auditor | Contracts (read), Audit events | Sees events within scope |
| System Administrator | System Administrator | Health and readiness, Operations, Support access | Cannot see business pages, Audit events or Document scanning (see `docs/DOCUMENT_SCANNING.md`) |
| Outsider | A signed-in person with no role in the organisation | Home, Notifications only | Proves access limits |

### Synthetic test data, three cases

Pick the case that matches your configuration; the point is that the examples below are only examples and the screens must show *your* published wording and settings.

| Item | Blank (neutral pack) | JBS-style (`jbs-procurement`) | Harborline-style (`construction-subcontracts`) |
|---|---|---|---|
| Reference prefix | CON | JBSAUS | HBL |
| Country, locale, currency, time zone | your decisions (the Blank pack proposes none) | Australia, en-AU, AUD, Australia/Sydney | United States, en-US, USD, America/Chicago |
| Word for the other party | Counterparty (or your choice) | Supplier | Subcontractor |
| Example contract title | Test agreement 001 (synthetic) | Test supply agreement 001 (synthetic) | Test framing subcontract 001 (synthetic) |
| Example approver group | Approver group A | Group Head of Procurement and backup | Package Manager, Commercial Administrator, Project Director |
| Example date shown on screen | as your Regional setting | day first, for example 18/10/2026 | month first, for example 10/18/2026 |

---

## 3. How to record results and defects

### 3.1 The test result log (one row per script step)

Keep this in your UAT workbook (data-collection workbook sheet 18 in the playbook), not in the application.

| Column | Example (synthetic) |
|---|---|
| Script and step | U3.4 |
| Tester and role | Tester 3, Approver |
| Date and time (UTC and local) | 2026-10-20 03:12 UTC / 14:12 Australia/Sydney |
| Environment and platform version | Test, version shown on **/Admin/Health** |
| Configuration release numbers in force | Workflow 4, Approval matrix 3 (see section 6) |
| Expected result | Plan shows two approval steps in order |
| Actual result | As expected |
| Outcome | Passed / Failed / Blocked / Not Verified |
| Evidence | Screenshot name, audit event number, correlation id |

**Not Verified is not a pass.** Use it when a step could not be run (for example no real e-mail provider, no real scanner, no real identity provider). A step marked Not Verified counts as open in the go/no-go table.

### 3.2 Recording a defect

There is no defect form in the application. Record each defect in your own tracker or the workbook, with:

| Field | What to write |
|---|---|
| Id and title | UAT-014, "Approval plan omits second approver" |
| Script step | U3.4 |
| Severity | 1 Blocker (cannot use, or wrong access or approval), 2 Major (wrong result, workaround exists), 3 Minor (wording, layout), 4 Cosmetic |
| Class | **Configuration** (a Business Administrator fixes it by publishing a new release), **Data** (fix the source file and load again in a new batch), **Product** (raise with the supplier) |
| Steps to reproduce | Page path, what you clicked, what you typed (synthetic values only) |
| Expected and actual | One line each |
| Evidence | Screenshot, audit event number, correlation id from the page or error |
| Owner and date raised | |
| Fix and retest | Release number of the fix and the retest result |

Take the correlation id from the page where it is shown (for example the Operations dashboard) or from an error message. Never put a password, token, connection string or live customer data in a defect.

### 3.3 Fixing a Configuration defect

1. The Business Configuration Administrator (or Organisation Administrator) opens the relevant page (for example **/Admin/Forms/Edit?kind=ApprovalMatrix**), starts a draft, makes the change, uses **Save and check**, reads the "Preview and impact" panel and chooses **Submit for review**.
2. A different person opens **Configuration reviews** (**/Admin/Reviews**), tab **Waiting for my decision**, opens the item, reads **What would change**, and chooses **Approve and publish** (recent sign-in required). The author never sees the approve button.
3. Record the new release number against the defect and retest the step.

Where only one administrator exists, the draft stays "Submitted" and nothing is published: arrange the second person *before* UAT, not during it.

---

## 4. Test scripts

For every script: sign in as the tester, run the steps, record the outcome (section 3.1). Routes are page addresses; where a page needs an id, open it from the list page.

### U1. Sign-in and access

| | |
|---|---|
| Who and permission | Every tester; no extra permission |
| Pages | **/SignIn**, **/** (Home), **/Contracts**, and one page each tester should not reach |
| Prerequisite | Users created and given roles by the Tenant Administrator (**/Admin/Users**) |

| Step | Do | Expect |
|---|---|---|
| 1 | Sign in as each tester | The menu shows only the entries in the table in section 2 |
| 2 | As the Outsider, open **/Contracts** | No register entries (empty, forbidden or not found) |
| 3 | As the Contract owner, type **/Admin/Operations** in the address bar | Forbidden or not found. Operations are refused even to a tenant administrator, by design |
| 4 | As the Viewer who may not see the highest classification, search for a restricted test contract | It is absent from the list and from search |
| 5 | As a user of organisation A, search for a contract of organisation B | Not visible |
| 6 | As the Tenant Administrator, open **/Admin/Simulation** ("Access simulation"), choose a person and a category and click the show button | The what-if result matches steps 2 to 5 |

Evidence: screenshot of each menu; the Access simulation result. If any person sees something they should not, that is a **Severity 1** defect.

### U2. Contract lifecycle

| | |
|---|---|
| Who and permission | Contract owner (`contracts.read` plus the pack's create and submit permissions) |
| Pages | **/Contracts**, **/Contracts/Create**, **/Contracts/Details/{id}**, **/Contracts/Lifecycle/{id}**, **/Contracts/Unlock/{id}** |

| Step | Do | Expect |
|---|---|---|
| 1 | **Contracts**, then **Create** (the button reads with your word for contract), choose the organisation and type, **Continue**, fill the fields, **Create draft** | A draft with a reference using your prefix (CON, JBSAUS or HBL) |
| 2 | Open the contract, **Edit details**, change a field, **Save changes** | Saved; **Change history** on the details page shows before and after |
| 3 | Open **Open the lifecycle: next steps and history** | The stage list, the workflow release it is pinned to, and the buttons the workflow allows for your role |
| 4 | Use each allowed button in turn | Each moves the contract to the state the workflow design says |
| 5 | Try an action the workflow should refuse (for example submit while a required field is empty) | Refused with a plain message naming the problem |
| 6 | When the contract is Effective (or your pack's locked state), try **Edit details** | Locked. Change only through a variation |
| 7 | As a user holding the unlock permission, open **/Contracts/Unlock/{id}**, enter a written reason and a time limit, name the fields | Needs a recent sign-in; only the named fields open; **Close the unlock now** ends it; the audit shows open, change and close |

Evidence: the status history table on the lifecycle page; audit events for the unlock.

### U3. Approvals

| | |
|---|---|
| Who and permission | Approver (`approvals.action`); Business Configuration Administrator or tester with `approvals.simulate` for the simulator |
| Pages | **/Admin/Approvals/Simulate**, **/Approvals**, **/Approvals/Details/{id}**, **/Admin/Approvals/Delegations** |

| Step | Do | Expect |
|---|---|---|
| 1 | For each row of the approval matrix, open **Admin, Approval matrices, Simulator**, choose the organisation, type the matrix code, fill the sample submission, **Run the simulation** | **Resolved steps** and **Why each person is in or out** match the approval design |
| 2 | Submit a real test contract that meets that row's conditions | **/Approvals** shows it to the right approver in the right order |
| 3 | The submitter opens **/Approvals/Details/{id}** for their own contract | No **Approve** button: a submitter cannot approve their own |
| 4 | The approver clicks **Approve** (or **Reject** with a comment) | The decision appears in **Decision history for this agreement** |
| 5 | An approver opens **Delegations**, uses **Delegate**, then a delegate opens **/Approvals** | The delegate sees the item; **End it** removes it |
| 6 | Check the simulator against the actual plan | Same steps. If not, Severity 1 |

Worked example, three cases:
- Blank: one approver group, no value tiers; one step.
- JBS-style: Group Head of Procurement (or backup) at the registration step; test with the primary absent and the backup present.
- Harborline-style: value tiers send a mid-value subcontract to the Commercial Administrator and a high-value one also to the Project Director. The tier amounts in the pack are *invented examples*; confirm they have been replaced with your finance owners' real thresholds before testing against them.

### U4. Variations and renewals

| Step | Do | Expect |
|---|---|---|
| 1 | On an Effective contract, **Open the lifecycle**, choose the start-variation button (it uses your word for variation), then **Draft the variation** | A variation draft on **/Variations/Details/{id}** |
| 2 | **Edit the variation**, **Save**, attach a document with **Add the document** | Saved |
| 3 | Take the variation through the workflow | The contract is unlocked only through the variation, and changes are recorded |
| 4 | **Start a renewal** from the lifecycle page (**Note** optional) | A successor draft with only the configured fields and document types; the original is unchanged |
| 5 | **Complete the renewal**, or **Cancel the renewal** (reason required) | Status shown on both contracts |

### U5. Legal review (only if the capability is on; if off, the step must be absent everywhere)

| Step | Do | Expect |
|---|---|---|
| 1 | Contract owner submits for legal review | Item in **/Legal** |
| 2 | Legal: **Begin review**, **Assign**, **Post** comments, **Attach** a marked-up file, **Return for rework** or **Clear: reviewer is satisfied** | Rounds and Timeline record each action |
| 3 | If the capability is off (check **/Admin/Capabilities**) | No Legal menu entry, no legal button on any page |

### U6. Notifications

| | |
|---|---|
| Who and permission | Tenant Administrator or Procurement Administrator (`notifications.administer`) for delivery; any user for their own inbox |
| Pages | **/Notifications**, **/Admin/Notifications** |

| Step | Do | Expect |
|---|---|---|
| 1 | Cause one event of each kind in the notification list (submit, approve, return, reminder and so on) | An in-app message arrives for the right recipient with the right wording and link |
| 2 | **/Admin/Notifications**: **Run the background work now** | Events become messages; **Summary** counts change |
| 3 | Filter **Deliveries** by "Failures and dead letters" | A failed delivery is listed with its last error; **Retry** works once the provider is fixed |
| 4 | Check mandatory messages | They arrive in-app even if e-mail is off. Note if the e-mail provider is shown as "Simulated: delivers nothing"; then e-mail delivery is **Not Verified** |
| 5 | Open an e-mail or message and check language, date format and links | Matches the organisation's locale (en-AU or en-US and so on) |

### U7. Reminders

| Step | Do | Expect |
|---|---|---|
| 1 | Create test contracts whose end date is 30, 7 and 1 days away | Reminders appear in **/Notifications** and on **Dashboards** |
| 2 | On **Contracts, Details**, **Reminders**: **Acknowledge the reminder**, **Save the lead time** | Acknowledged state shown; the lead time is saved |
| 3 | Create a contract in a category with no owner | The configured fallback applies (JBS-style pack: organisation administrators). The Blank pack has **no** fallback; the test should show that honestly |

### U8. Terminology

Spot-check ten screens and two exports (the contract list, a lifecycle page, an approval page, a notification, a Legal page, the register export and so on). Every user-facing word must be *your* published wording (Counterparty, Supplier or Subcontractor, and your role names), not the pack's. Compare with **/Admin/Terminology** ("in force"). A wrong word is a **Configuration** defect.

### U9. Regional

| Step | Do | Expect |
|---|---|---|
| 1 | Open **/Admin/Regional**, choose the organisation, read **In force for** | Country, locale, currency, time zone and tax identifier as designed |
| 2 | Check dates, numbers, currency, time zone and the tax identifier on a contract page, on a notification and in an export | JBS-style: day-month-year and AUD, times in Australia/Sydney. Harborline-style: month-day-year and USD, times in America/Chicago. Blank: whatever you decided |
| 3 | Confirm stored times: in an export set to the machine-readable style, times are in UTC | Yes |

### U10. Search, views and exports

| Step | Do | Expect |
|---|---|---|
| 1 | **/Contracts**: type in the search box, **Apply filters** | Results within your access only |
| 2 | **Save view**, reopen it, **Delete my view** | Works |
| 3 | Open **Export what this search finds**, choose CSV, then Excel (XLSX); choose a Values style; for a large export tick the box and give a reason | Only the columns you may see; recorded in the audit history |
| 4 | Open the file in a spreadsheet | Cells that begin with a formula character are neutralised (formula-safe); no warning prompts |

### U11. Document attachments and the malware gate

| | |
|---|---|
| Who and permission | Contract owner uploads; Tenant Administrator or Procurement Administrator (`notifications.administer`) reviews held files |
| Pages | **/Contracts/Documents/{id}**, **/Admin/Documents/Scanning**, **/Admin/Health** |

| Step | Do | Expect |
|---|---|---|
| 1 | Upload an ordinary synthetic file to a test contract | Demo and Test (mock scanner): released and downloadable. Production without a real scanner: held, cannot be downloaded |
| 2 | Open **/Admin/Documents/Scanning** | The Scanner panel states what is connected ("Demonstration scanner", "No scanner connected" or the provider); counts by result; **Files that are held** |
| 3 | Demo or Test only: upload a harmless text file that contains the standard antivirus test text (see `docs/DOCUMENT_SCANNING.md`) | Quarantined (infected); **Scan again** is not offered for it; the audit shows `document.quarantined` |
| 4 | **/Admin/Health** | Check R-DOC-01. In Production it is Unhealthy until a real scanner is connected |

**Production decision.** No real scanner adapter exists; attachments cannot be used in Production until one is supplied. Record this as an open item in the go/no-go table (section 6). A Demo pass of this script proves the gate, not protection.

### U12. Migration (mock load)

| | |
|---|---|
| Who and permission | Procurement Administrator or the migration role (`migration.stage`); a second person with the legacy-load permission for effective contracts |
| Pages | **/Migration**, a project, then **/Migration/Batches/{id}** and its Rows, Messages, Files, Reconciliation and Provenance pages |

| Step | Do | Expect |
|---|---|---|
| 1 | Create a mock batch from the migration project with a small representative synthetic file | A batch page showing the stages |
| 2 | **Run the dry run** | Rows validated; errors listed with row, field and code. **Download the error report (Excel)** or **(CSV)** |
| 3 | If a row says MIG-E011, either create the missing category or set the profile option "Create missing categories", then fix the source file and load again in a **new** batch | Dry run clean |
| 4 | Legacy (already effective) contracts: the first person clicks nothing to commit; a *different* person with the legacy-load permission uses **Confirm this legacy load** | **Commit this batch** is available only after confirmation, and not to the person who confirmed |
| 5 | **Commit this batch** (not while maintenance mode is on) | Records written in stages; every record and audit entry carries the batch id |
| 6 | **Open the reconciliation report**, then **Download Excel** or **Download CSV**; the data owner checks a sample against the source | Counts agree or the exceptions are listed |
| 7 | **Save the reconciliation** (tick the exceptions approval) | Business acceptance recorded |
| 8 | Roll back or compensate once, on a mock batch | Records removed or archived as designed; audit records it |

### U13. Audit

| Step | Do | Expect |
|---|---|---|
| 1 | **Audit events** (**/Admin/Audit**). Filter "Action starts with" for what you tested (for example approvals, document, configuration) | Each key action is present with the right actor and outcome |
| 2 | Expand **Values** on a configuration publication | Before and after are shown |
| 3 | If you hold `audit.export`: **Export evidence (JSON with hashes)** | A file you can attach to the UAT report |

### U14. Operations (System Administrator only)

Run these in the installation tenant. The full click-by-click is in `docs/OPERATIONS_QUICK_GUIDE.md`.

| Step | Do | Expect |
|---|---|---|
| 1 | **Operations, Backups**: reason, **Create backup**; then **Verify again** | Verified; record the manifest hash from the backup page |
| 2 | **Operations, Restore**: choose the backup and a **new** database name, **Preview the restore**, read blockers and warnings, give a reason, type the exact phrase, **Restore into the new database** | Succeeded or Succeeded with warnings; the live database untouched |
| 3 | **Operations, Post-restore validation**: **Run validation on the running installation** | Passed (or warnings explained) |
| 4 | **Operations, Maintenance mode**: enter with a reason, confirm a change is refused, leave with a reason | Banner on every page while on; off afterwards |
| 5 | **Operations, Operations dashboard** | Each panel Verified, Warning, Failed or Not Verified; keep the JSON download |
| 6 | **Health and readiness** | Read every check, especially R-DOC-01 and the backup check |

---

## 5. Exit rule for UAT

Agree these *before* testing starts, and write them in the UAT report:

- Every access scenario (U1) and every approval scenario (U3) passed.
- No open Severity 1 or Severity 2 defects.
- Every accepted deviation has an owner, a date and the Business Lead's initials.
- Steps marked Not Verified are listed, with the reason and who accepts the gap.

A signed UAT report, the evidence folder, the accepted-deviations list and the release numbers below are the exit package. Freeze configuration until cutover; any change after the freeze needs the Business Lead's approval and a new release (one change per release, so rollback means something).

---

## 6. Go/no-go evidence

The application shows facts; people decide. Gather each item, record where it came from and whether it is **Verified**, **Not Verified** or **Not applicable**.

| # | Evidence | Where to get it | Who | Notes |
|---|---|---|---|---|
| 1 | Signed UAT report and results log | Your workbook | Business Lead | Section 5 |
| 2 | Configuration release numbers and checksums in force, per kind and organisation | **/Admin/Configuration**, "Release history" (Release, Published, By, Note, Checksum shows the first 12 characters). Terminology, Capabilities, Regional and Studio have their own History pages | Business Configuration Administrator | Note the highest release number for each kind. The configuration is frozen at these numbers |
| 3 | No change waiting for a decision | **/Admin/Reviews**, tab **Waiting for my decision**, and **Put forward by me** | Business Configuration Administrator | An unreviewed submission is not in force |
| 4 | Organisation set-up checklist complete | **/Admin/Organisations/Setup** for each organisation | Organisation Administrator | |
| 5 | Pack installed, trusted and assigned | **/Admin/Packs** | Business Administrator | Signing is done by the Pack Publisher outside the application (`docs/PACK_SIGNING.md`); the System Administrator registers only the public key |
| 6 | Health and readiness | **/Admin/Health** | System Administrator | Read every check. A Production installation with R-DOC-01 Unhealthy cannot use attachments |
| 7 | Operations dashboard saved as JSON | **/Admin/Operations/Dashboard**, "Download this dashboard as JSON" | System Administrator | Nothing Failed that you cannot explain |
| 8 | Verified backup and manifest hash | **/Admin/Operations/Backups** | System Administrator | Within the agreed frequency |
| 9 | Restore rehearsal and validation | **/Admin/Operations/Restore**, **/Admin/Operations/Validation** | System Administrator | Record time taken (your measured recovery time) |
| 10 | Upgrade rehearsal, if a newer build is expected | **/Admin/Operations/UpgradeReadiness**, **UpgradePreview**, **RollbackEvidence**, **UpgradeHistory** | System Administrator | See `docs/OPERATIONS_QUICK_GUIDE.md` |
| 11 | Evidence report (and support bundle if support asked) | **/Admin/Operations/Evidence** | System Administrator | Contains no secrets or record content |
| 12 | Migration reconciliation and acceptance | Batch page and Reconciliation page | Data owner | Rollback tested once |
| 13 | Document scanning decision | **/Admin/Documents/Scanning**, R-DOC-01 | Business Lead and security owner | Real scanner **Future / Not Verified** |
| 14 | Open Not Verified items, with owners | UAT log | Project Manager | e-mail provider, identity provider, scanner, IIS or Azure if used |
| 15 | Decisions the platform does not make | Your records | Business Lead | Backup frequency, retention, recovery objectives, who may read backups |

**Go/no-go rule (suggested).** *Go* only when rows 1 to 4, 6 to 9 and 12 are complete and Verified, there are no open Severity 1 or 2 defects, and every Not Verified item has a named owner and a written acceptance. This rule is yours to set; the platform does not check it. The result is a recommendation to your governance body, **not** production approval.

---

## 7. Cutover

**Purpose.** Move from the old register to the platform on a planned date with a way back.

### 7.1 Two weeks before

- UAT signed; configuration frozen; final mapping profile version used in a successful rehearsal.
- Production installation in Production mode with single sign-on working for every group, a mail provider, the backup folder and backup account configured, and a verified restore rehearsal. **[Technical task: System Administrator]** for installation, identity provider, secrets and mail settings (see `docs/OPERATIONS_RUNBOOK.md`).
- All organisation configuration published through **Configuration reviews** by a second person. Check **/Admin/Reviews**: nothing waiting.
- A decision recorded on attachments (section 4, U11).
- Users told of the freeze window, training and who to call.
- A written back-out decision tree and the "point of no return" (usually business acceptance of the final batch).

### 7.2 Cutover runbook

| # | Who | Action | Screen |
|---|---|---|---|
| 1 | Business Lead | Confirm go. All items in section 6 are in hand. This is a business decision, not an application setting | |
| 2 | Source owner | Stop changes in the old system; take the final extract; record its hash | Outside the application |
| 3 | Business Administrator | Freeze starts (times in UTC) | Outside the application |
| 4 | System Administrator | Create a verified backup. This is the way back. Reason, optionally hold maintenance, then **Create backup**; confirm Verified | **/Admin/Operations/Backups** |
| 5 | System Administrator | Make sure maintenance mode is **off**; a migration commit is refused while it is on | **/Admin/Operations/Maintenance** |
| 6 | Procurement Administrator | Final batch: dry run, review, (legacy loads: a second person confirms), **Commit this batch** | **/Migration/Batches/{id}** |
| 7 | Procurement Administrator and data owner | **Open the reconciliation report**, sample against the source, **Save the reconciliation** (accept) or roll back | Reconciliation page |
| 8 | System Administrator | **Run validation on the running installation**; read the Operations dashboard | **/Admin/Operations/Validation**, **/Admin/Operations/Dashboard** |
| 9 | System Administrator | Create a second verified backup (the post-load recovery point) | **/Admin/Operations/Backups** |
| 10 | Business Lead | Decide: open to users. The freeze ends and users sign in | |

### 7.3 Things people get wrong

- Maintenance mode blocks the migration commit, configuration publication and configuration rollback. Do not leave it on by accident. If a restore failed, the installation stays in maintenance mode on purpose and leaving it needs an explicit acknowledgement.
- Do not "roll back" after business acceptance; use compensating archival, which keeps records.
- If the platform is rolled back by restore, everything done since the backup is discarded; tell users the time window. Restore into the live database is a command-line disaster-recovery action: **[Technical task: System Administrator]** (see `docs/DISASTER_RECOVERY.md` and `docs/ROLLBACK_BY_RESTORE.md`).
- The old system stays read-only for the agreed retention period. The platform does not import the old system's comment and approval history.
- Rolling back *configuration* is separate: on **/Admin/Configuration**, "Release history", type a reason and choose **Publish again**. It needs a recent sign-in, in Production it goes to a second person, and it creates a **new** release; it never edits history.

---

## 8. Hypercare

**Purpose.** The first weeks after go-live, when problems appear that tests did not find. Usually two to four weeks.

### 8.1 Roster

A daily owner (Business Administrator), a technical owner (System Administrator) and a decision-maker (Business Lead), with agreed response times.

### 8.2 Daily checks, System Administrator

Open **Operations, Operations dashboard** (**/Admin/Operations/Dashboard**). Each line is Verified, Warning, Failed or Not Verified (the worst wins; an empty area is Not Verified, never green). It never shows business content.

| Area | What you want | If not |
|---|---|---|
| Overall and health (including R-DOC-01) | Verified | Open the check and fix the setting. A Failed readiness check in Production blocks nothing by itself but must be explained |
| Background jobs | Recent runs, none overdue | Ask the Tenant or Procurement Administrator to use **Run the background work now** on **/Admin/Notifications**; read the failure register |
| Notification outbox | Nothing waiting for hours; none dead-lettered | Fix the provider; a tenant administrator uses **Retry** |
| Backups | A verified backup inside the agreed frequency | **Create backup** now. There is no built-in schedule; the organisation runs the backup command from its own scheduler **[Technical task: System Administrator]** |
| Certificates and keys | None near expiry | Plan renewal **[Technical task: System Administrator]** |
| Migrations and upgrades | Nothing pending unless planned | Do not run anything unplanned |
| Maintenance mode | Off | Find out who turned it on and why |

Not Verified is not a pass.

### 8.3 Daily checks, Business Administrator

- Tenant Administrator or Procurement Administrator: **/Admin/Notifications** (delivery failures) and **/Admin/Documents/Scanning** (held files and their ages).
- Tenant or Organisation Administrator: **/Admin/Users** (new users and role changes) and **/Admin/Audit** (configuration changes in the last day).
- **/Admin/Reviews**: nothing stuck waiting for a decision.
- A change log of questions about wording (terminology gaps) and about who approves what (approval gaps).

### 8.4 Changing configuration during hypercare

The loop is always: draft, **Save and check**, preview, **Submit for review**, a second person **Approve and publish**, observe. One change per release. To go back, use **Publish again** from the release history (recent sign-in; second person in Production; new release).

### 8.5 Exit criteria

No open top-severity issues; two consecutive weeks of green dashboards; a verified backup and a restored-and-validated copy within the agreed period; a lessons-learned list; handover to business-as-usual support; the Business Lead signs. This sign-off ends hypercare. It is not production approval.

---

## 9. Evidence to keep

- The signed UAT report, results log, defect list and accepted deviations.
- Configuration release numbers and checksums at go-live, with the Configuration reviews history.
- The Operations dashboard JSON before and after cutover; the evidence report; backup ids and manifest hashes; restore, validation and rehearsal results (and the evidence files they name).
- Migration reconciliation download and acceptance record.
- Audit export for the test and cutover period.
- The Document scanning page counts and the R-DOC-01 result on cutover day.
- The cutover record: who did each step, with times (UTC), and the back-out decision.
- Hypercare log and sign-off.

Store these with the project records. They show what ran; they do not approve anything.

## 10. If something goes wrong

| Symptom | What to do |
|---|---|
| A tester sees something they should not | Stop that script; record Severity 1 with a screenshot; the Tenant Administrator checks the person's roles on **/Admin/Users/Details/{id}** and **/Admin/Simulation**; retest after the fix |
| The **Approve and publish** button is missing | You are the author or last editor of that draft. A different person must approve |
| A submitted change will not publish ("changed after submission") | Editing after submission voided it. Resubmit, then have a second person approve |
| "Confirm it is you" appears | Click **Confirm**; you return to the page. Repeat the action |
| A page says forbidden | You do not hold the permission, or you are outside the installation tenant for an Operations page. Ask the Tenant Administrator (business pages) or confirm with the System Administrator |
| Commit refused | Check maintenance mode is off, the batch is validated, nothing is waiting for a decision, and a legacy load is confirmed by a different person |
| Attachments are held | Expected where no real scanner is connected. See `docs/DOCUMENT_SCANNING.md` |
| Backup, restore or upgrade failed | Do not retry blindly. Follow `docs/OPERATIONS_QUICK_GUIDE.md`, section "If something goes wrong", and `docs/OPERATIONS_RUNBOOK.md` |
| Cutover cannot proceed | Use the written back-out tree. Before business acceptance: roll back the batch. After: compensate. Platform-wide: restore the pre-cutover backup (data since then is lost) |

---

## What was and was not verified

- Verified by reading the code: every page route, button label, field, permission and menu entry named above (Operations, Document scanning, Configuration reviews, Contracts, Approvals, Legal, Variations, Notifications, Migration batch, Audit, Health, Regional, Configuration release history).
- Not run: no live installation was driven for this guide. Specific screen wording in other business pages (for example the exact buttons the workflow offers on a lifecycle page) depends on your published workflow and terminology and is shown by the page itself.
- Not Verified by anyone: a run of these scripts by an unaided non-developer; a real malware scanner; a real identity provider; real e-mail delivery; IIS and Azure deployments.
- The 20-step journey in `docs/NO_CODE_REHEARSAL.md` is the project's own automated proof that the set-up journey works over real HTTP; it is not a substitute for your UAT.
