# Implementation rehearsal: a greenfield customer, start to finish

Status: **desk rehearsal with live protocol checks, 2026-10-18, against the branch claude/stoic-volta-dvszm5 at commit d6920b0** (platform version 1.1.0), **re-run on 2026-10-19 after the Company Rehearsal Enablement slice (ADR-0038); sections 1 and 9 hold the current verdicts, and sections 5 and 6 are kept as the record of the original review**. It is evidence for a go or no-go conversation about the first real customer implementation. It is **not** a production approval: production approval is external governance and nothing here declares it.

## 0. What this document is, and is not

**What it is.** One synthetic customer is taken from a blank machine to a working, loaded, tested and recoverable register, in thirteen steps. For each step it names who does it, what skill that takes, where on screen it happens, how complex it is, what must exist first and what evidence it leaves. It then lists every remaining technical, manual, code and infrastructure dependency, and answers three questions: can the customer be implemented in Demo/Test, with masked production data, and in Production, today.

**How it was produced.** The nine documents named below were read in full. Their key claims were then checked against the code and, where possible, against a running host:

| Reviewed | Role in this rehearsal |
|---|---|
| `docs/IMPLEMENTATION_PLAYBOOK.md` | Decisions, sequencing, ratings, the original gap list (Appendix D) |
| `docs/IMPLEMENTATION_READINESS.md` | Gap register G-01 to G-30 and what the readiness slice changed |
| `docs/INSTALLATION_GUIDE.md` | Technical installation (System Administrator) |
| `docs/SETUP_WIZARD_GUIDE.md` | The 14-stage first-run wizard |
| `docs/ORGANISATION_ONBOARDING_GUIDE.md` | Creating an organisation, roles, checklist, onboarding wizard |
| `docs/BUSINESS_ADMIN_GUIDE.md` | Day-to-day configuration: users, groups, terminology, capabilities, regional, form editors, reviews, rollback |
| `docs/PACK_IMPLEMENTATION_GUIDE.md` | Choosing, trusting, installing and assigning a pack |
| `docs/MIGRATION_EXECUTION_GUIDE.md` | Loading a legacy register |
| `docs/UAT_AND_CUTOVER_GUIDE.md` | UAT scripts, go/no-go evidence, cutover, hypercare |

`docs/OPERATIONS_QUICK_GUIDE.md` and `docs/NO_CODE_REHEARSAL.md` were also read, because steps 12 and 13 and the automated evidence depend on them.

**Evidence grades used in every step** (a grade is never upgraded by wording):

| Grade | Meaning |
|---|---|
| **LIVE** | Observed in this review, on 2026-10-18, by driving the real host over HTTP: a Production-mode `Ecp.Host` against SQL Server 2022 (Docker), `migrate` run first, TLS simulated by sending `X-Forwarded-Proto: https` as a reverse proxy does. Not a browser; not a person. |
| **AUTO** | Passed in an automated test. The 23-step no-code rehearsal (`docs/NO_CODE_REHEARSAL.md`) runs over real HTTP on SQLite and on real SQL Server, in **Test mode with `Ecp:Governance:SecondPerson=Enforced`**, which applies the Production second-person rule (it was switched on inside the test before the slice). Totals: unit 895, architecture 113, API 157, integration 634, 0 failed, 0 skipped. |
| **CI** | Green in CI run 69 (commit `5985995`), all three jobs, including the container image build and smoke test and the N-1 to N upgrade rehearsal on SQL Server. |
| **READ** | Read from the guides and the code. Not executed. |
| **NOT VERIFIED** | Nobody has checked it, or it cannot be checked here. Never a pass. |

**Reading the steps in section 3.** They record what the 2026-10-18 review observed, including since-resolved gaps (marked in section 9.1); where section 9 differs, section 9 is current.

**What this rehearsal cannot show.** No person followed any guide in a browser. No assistive-technology test was run. No real identity provider, mail server, malware scanner, IIS or Azure host was available. A trained non-developer working unaided has not been observed. Those gaps are carried into the verdicts.

**Scale words.**

*Skill level*

| Level | Who | Can do |
|---|---|---|
| S1 | Trained Business Administrator (Organisation Administrator, Business Configuration Administrator, Procurement Administrator) | Everything on the application's pages for their organisation. No command line |
| S2 | Business Lead, data owner, Legal lead, tester | Decides and signs; uses the pages that match their role |
| S3 | System Administrator | Command line, server, container, SQL Server logins, secret files, proxy, identity-provider portal |
| S4 | Pack Publisher / key custodian | Ran the pack command-line tool with a .NET SDK and a source checkout at the time of the review (now the standalone `ecp-pack` tool, section 9); guards a private key |
| S5 | Developer | Changes and rebuilds the product |

*Complexity* (replaces time estimates)

| Rating | Meaning |
|---|---|
| **Low** | One or two screens, a handful of fields, no decision beyond the workbook, easily reversed |
| **Medium** | Several screens or prepared data; one or two decisions; reversible by discarding a draft or publishing an earlier release |
| **High** | Many fields or screens, or business judgement, or two people who must coordinate; a wrong answer is costly or partly permanent |
| **Very high** | Needs a technical role and tools outside the application, or is irreversible, or failure leaves the installation unusable until fixed |

---

## 1. Verdicts at a glance

Current verdicts after the Company Rehearsal Enablement slice. Each is **Supported**, **Supported with conditions**, **Blocked** or **Not Verified**; none is inferred from another and none is a production approval.

| # | Question | Verdict | One-line reason |
|---|---|---|---|
| 1 | A synthetic company in **Demo** mode | **Supported with conditions** | Works end to end, but Demo keeps simplified governance (second-person rule not enforced), sends no real email and uses the demonstration scanner (section 9.3) |
| 2 | A synthetic company in **Test** mode | **Supported with conditions** | All 23 steps pass on real SQL Server with Production-equivalent governance (`Ecp:Governance:SecondPerson=Enforced`); a real mail service, identity provider and scanner are Not Verified, and no unaided person has followed the guides |
| 3 | A **masked-data** company in Test mode | **Supported with conditions** | Needs an approved masked-data declaration, the mock scanner accepted on the record, and the customer's own masking; the platform cannot verify masking |
| 4 | **Production-mode setup** | **Blocked** | No connected, healthy malware scanner exists, so a Production installation cannot report ready or finish setup; live email, identity and signed packs are also Not Verified |
| 5 | **Production use with confidential contracts** | **Blocked** | Blocked by the same scanner gap and by Production-mode rehearsal that has not been run; governance approval is external |

The reasons, conditions and evidence are in section 9. Sections 5 and 6 record the original review of 2026-10-18 and are superseded by section 9 where they differ.

---

## 2. The customer and the cast

**The customer (synthetic).** "Northwind Traders", a company with one legal entity, one division and two sites, a legacy register of about twenty contracts in a spreadsheet with a ZIP of attachments, and a need for two-step approval above a value. Starting pack: **Blank** (neutral wording, five-state workflow). Where the choice of pack changes a step, the JBS-style pack (`jbs-procurement`: Legal review, Group Head approval) and the Harborline-style pack (`construction-subcontracts`: value-tier matrices) are noted. All names are invented.

**The cast.**

| Actor | Skill | Product role |
|---|---|---|
| Project Manager | S2 | none inside the product |
| Business Lead | S2 | none required; signs UAT and cutover |
| System Administrator | S3 | System Administrator (installation scope, no business rights) |
| Identity-provider administrator | S3 | outside the product |
| Pack Publisher / key custodian | S4 | outside the product |
| Tenant Administrator | S1 | Tenant Administrator (creates organisations, grants first roles, registers trusted keys) |
| Organisation Administrator A | S1 | Organisation Administrator (drafts and submits configuration) |
| Business Configuration Administrator B | S1 | Business Configuration Administrator (the **second person** who publishes what A drafts) |
| Procurement Administrator 1 and 2 | S1 | Contract Administrator, labelled Procurement Administrator in the reference pack (migration; two people for a legacy load) |
| Testers, one per role | S2 | Contract Owner, Legal, Approver, Auditor, Viewer, and an Outsider with no role |
| Data owner | S2 | none; named in the migration project |

Two Organisation-level administrators are needed from the start: in Production the person who drafted or submitted a change can never publish it.

---

## 3. The thirteen steps

Each step has the same rows. **Observed** says what this review saw. **Remaining dependencies** points to the numbered items in section 4 (T = technical role, M = manual, C = code, I = infrastructure).

### Step 1. Blank installation

| | |
|---|---|
| **Actor** | 1a installation: System Administrator, identity-provider administrator, Pack Publisher. 1b setup wizard: System Administrator with a Business Administrator at the same screen |
| **Skill level** | 1a S3 (and S4 for signed packs). 1b S3 plus S1 |
| **UI path** | 1a: no screen; command line and files (`INSTALLATION_GUIDE.md` sections 4 to 9), then `/health/live`, `/health/startup`, `/health/ready`, `/Admin/Health`. 1b: `/Setup`, then `/Setup/Step/mode`, `database`, `storage`, `tenant`, `pack`, `identity`, `admin`, `organisation`, `branding`, `classification`, `notifications`, `demo` (not in Production), `readiness`, `complete`; report at `/Setup/Report` and `/Admin/Setup` |
| **Complexity** | 1a **Very high**. 1b **High** (14 stages, three of which need the System Administrator; a wrong tenant name, reference prefix or provider key is permanent through the screens) |
| **Prerequisites** | Docker host with Compose; empty SQL Server 2022 database and two logins; secret files owned by user 1654; about fifteen environment settings (`Ecp__Mode`, `Ecp__DemoSeed`, `Database__*`, `Notifications__*`, `Ecp__Packs__*`, `Ecp__Recovery__*`); a TLS-terminating proxy that forwards scheme and host; an identity-provider application registration with a groups claim; signed pack files and the publisher's public key (Production); the one-time setup token from the server |
| **Evidence produced** | Technical sheet (no secrets); the composition file; `migrate` output ("Migrations applied."); the three health responses; a saved `/Admin/Health`; registered redirect addresses; the setup report (JSON); a screenshot of each stage's success message; reasons typed for acknowledged warnings; audit events `setup.tenant.created`, `setup.organisation.created`, `setup.completed` |
| **Evidence grade** | Container build and smoke test **CI**. Wizard end to end in Test mode **AUTO**. Production composition, Entra, SMTP, proxy and the Production wizard to its end **NOT VERIFIED** apart from the **LIVE** observations below |

**Observed (LIVE, Production mode, SQL Server 2022, TLS simulated by a header).**

| Observation | Result |
|---|---|
| `migrate` against an empty database | "Migrations applied." After first creating the database by hand: without it the command fails with a connection error (the guide says to create it) |
| `/health/live`, `/health/startup` | 200 `{"status":"Live"}`, 200 `{"status":"Started"}` |
| `/health/ready` on a fresh Production installation | **503 `{"status":"Unhealthy"}`**, still 503 after signed packs were trusted |
| The same database run in **Test** mode | `/health/ready` **200 Healthy**, all eight checks Healthy, including R-DOC-01 "The demonstration scanner is connected (trial only)" |
| `setup-token` | Prints a 43-character token once; the token text was **not** in the host log |
| `/Setup` over plain HTTP in Production | **500 "Something went wrong"**; the log says the antiforgery cookie requires an SSL request. Behind the simulated proxy header it worked (see finding G-33) |
| Stages 1 to 4 (mode, database, storage, tenant) | Passed. The Production guard accepted the configuration |
| Stage 5, solution pack, with only the shipped **unsigned** packs | **The pack list is empty**: "No solution pack is available… in Production only packs signed by a trusted publisher are offered." The customer cannot continue |
| Stage 5 after signing the three packs with an ephemeral key (`Ecp.PackTool`) and trusting its public key by host configuration | The list shows Blank, Construction Subcontracts and JBS Procurement, "signed". Blank chosen |
| Stage 6, identity, with a secret reference that does not resolve | Refused: "The secret reference env:… does not resolve in this environment." Passed once the variable existed (a synthetic placeholder, not a real secret) |
| Stages 7 to 10 (administrator, organisation, branding, classification) | Passed. Classification fields arrive pre-filled from the pack |
| Stage 11, email, with no mail server | **"The test message could not be sent: The mail server did not accept the message."** Production offers only the SMTP provider; Mock and Disabled are not offered. Stages 12 to 14 stay locked ("Complete first: Email…") |
| Progress | **10 of 13 stages complete.** Readiness review and Finish setup were not reachable |
| Raw text on setup pages | `[[solution.pack|sentence]]` in the browser title of the pack stage, and `[[notification|plural|lower]]` in "Complete first: Email [[notification|plural|lower]]" (finding G-32) |

**Not reached, therefore not observed:** the readiness stage with R-DOC-01 and R-ID-02, and Finish setup. The statement that Finish is refused in Production rests on code: `PlatformCheck` (R-OPS-01) treats any Unhealthy platform check as blocking, R-DOC-01 is Unhealthy in Production without a real scanner (unit-tested in `DocumentScanningUiTests`), and the live host reports exactly that overall state.

**Remaining dependencies.** T1 to T10, T13; M15, M16, M20; C1; I1 to I9.

---

### Step 2. New organisation

| | |
|---|---|
| **Actor** | Tenant Administrator creates it. Organisation Administrator A takes over |
| **Skill level** | S1 |
| **UI path** | First organisation: wizard stage 8 (`/Setup/Step/organisation`). Further organisations: `/Admin/Organisations` (widen once if the installation was set up for one), `/Admin/Organisations/New` (button **Check**, then **Create the organisation**), `/Admin/Users` and `/Admin/Users/Details/{person}` for roles, `/Admin/Organisations/Setup?id=` for the checklist |
| **Complexity** | **Medium**. About twelve fields and a structure box; the key, separator and digit count are permanent; the Check button must pass first |
| **Prerequisites** | Setup finished; the initial administrator has **signed in once** (only Active people are offered); the pack, if chosen, is installed and trusted; a recent sign-in (a page called **Confirm it is you** appears otherwise, and the form comes back empty) |
| **Evidence produced** | The Check panel text; the "organisation was created" panel; role-assignments table; audit events `organisation.setup.created`, `tenant.tenancy_widened`, role grants; the set-up checklist at three moments |
| **Evidence grade** | **AUTO** (rehearsal steps 1 to 4, SQLite and SQL Server). Onboarding wizard (`/Onboarding`) **READ only**: it is not among the 20 rehearsal steps. Not run LIVE in Production because Production setup could not finish |

**Remaining dependencies.** M3 (an invitation is only recorded; no message is sent), M4 (roles are granted person by person), M5, M17; C2 (contract types recorded but used by nothing yet).

---

### Step 3. New pack assignment

| | |
|---|---|
| **Actor** | Organisation Administrator A assigns. Tenant Administrator (or the System Administrator through host configuration) trusts the publisher's key. Pack Publisher signs |
| **Skill level** | S1 to assign; S4 to sign; S1 (Tenant Administrator) or S3 to trust a key |
| **UI path** | `/Admin/Packs` (catalogue), `/Admin/Packs/Upload`, `/Admin/Packs/Review`, `/Admin/Packs/Details`, `/Admin/Packs/TrustKeys`, `/Admin/Packs/Assign` (choose, **Preview what would change**, **Assign this pack and prepare drafts**), `/Admin/Packs/Checks`, `/Admin/Packs/Audit` |
| **Complexity** | **Medium** in Test. **Very high** in Production, because the pack must first be built and signed outside the application with a private key that the application never sees |
| **Prerequisites** | In Production a signed pack and a trusted active public key; in Test the shipped unsigned synthetic packs are allowed; the organisation exists; a second person able to publish the drafts |
| **Evidence produced** | Decision note; the publisher's checksum and fingerprint statements (received by a different channel); downloaded validation report (CSV); Trusted-publishers row; the Result list of the assignment; audit events `pack.installed`, `pack.assigned`, `pack.trust.registered`; the drafts that appear in the editors |
| **Evidence grade** | Assignment and draft preparation **AUTO** (rehearsal step 4). Signing, trusting by host configuration and pack listing **LIVE** (above). Upload and install screens, and a real publisher key, **READ / NOT VERIFIED** |

**Observed (LIVE).** Signing is `dotnet run --project src/Ecp.PackTool -- keygen | build-all | sign` and needs the .NET SDK and a checkout of the repository: the pack tool is **not** in the container image (the Dockerfile has no mention of it) (finding G-34). Trusting a key by `Ecp__Packs__TrustFile` worked and survived a host restart.

**Remaining dependencies.** T8; M15, M18; C8; I10, I11. The Pack guide's note that the trusted-publisher screen is held by the Tenant Administrator, not the System Administrator, was confirmed; the Packs page wording was corrected to match in the readiness slice.

---

### Step 4. Regional setup

| | |
|---|---|
| **Actor** | Organisation Administrator A drafts and submits; Business Configuration Administrator B publishes |
| **Skill level** | S1 |
| **UI path** | `/Admin/Regional` (organisation selector, form, **Save and validate**, **Submit for review**), then `/Admin/Reviews` for the second person; history and rollback at the foot of the page |
| **Complexity** | **Low to Medium**. Locale, currency and time zone are required decisions the Blank pack never fills in; tax-identifier masks need care |
| **Prerequisites** | Pack drafts prepared (step 3); the Business Lead has decided country, locale, currency, time zone, tax identifier type; a second person with config.publish |
| **Evidence produced** | The Preview of the form as it stands; the release row; the review page (What would change, History); audit entries for draft, submission, decision, publication |
| **Evidence grade** | **AUTO** (rehearsal steps 5 and 12). Second-person rule enforced because the test switches it on |

**Remaining dependencies.** M5, M17. In **Test mode the second-person rule is off** (finding G-31), so a human rehearsal in Test mode publishes at once and does not exercise this step's governance.

---

### Step 5. Terminology setup

| | |
|---|---|
| **Actor** | Organisation Administrator A; second person publishes |
| **Skill level** | S1 |
| **UI path** | `/Admin/Terminology` (overview), `/Admin/Terminology/Edit`, Preview, Version history, Audit history |
| **Complexity** | **Medium**. Each label has a stable key; only the catalogue's keys can be reworded; wording applies to pages, notifications and exports |
| **Prerequisites** | Pack chosen; the Business Lead has the wording list; a second person |
| **Evidence produced** | The Preview page (Now against After publishing); the Version-history row; the review entry |
| **Evidence grade** | **AUTO** (rehearsal step 6 keeps the pack's wording and submits; wording changes themselves are covered by terminology tests). Raw `[[…]]` placeholders still appear on some setup pages (G-32) |

**Remaining dependencies.** C2 (a new term key needs a developer, G-18); M5.

---

### Step 6. Approver groups

| | |
|---|---|
| **Actor** | Organisation Administrator A (or Tenant Administrator for several organisations) |
| **Skill level** | S1 |
| **UI path** | `/Admin/ApproverGroups` (register), `/Admin/ApproverGroups/Details?key=` (add and end members with **Preview the effect**), `/Admin/Users/Details/{person}` for the Approver or Backup Approver role |
| **Complexity** | **Medium**. The group key is permanent; a group cannot be retired while relied on; the last member of a relied-on group cannot be ended |
| **Prerequisites** | People exist, are Active (have signed in), and have the right role and classification ceiling; the Business Lead has decided groups, primaries and backups |
| **Evidence produced** | The group's Members table before and after; the preview text; audit entries |
| **Evidence grade** | **AUTO** (rehearsal step 8) |

**Remaining dependencies.** M3, M4 (people must be invited, told and must sign in before they can be members).

---

### Step 7. Workflow configuration

| | |
|---|---|
| **Actor** | Organisation Administrator A edits; B publishes |
| **Skill level** | S1 with a designed workflow from the Business Lead |
| **UI path** | `/Admin/Configuration` (tab, **Edit with the form**) or the checklist link, then `/Admin/Forms/Edit?kind=WorkflowContractLifecycle` (and `WorkflowVariation`); **Save and check**, **Preview and impact**, **Submit for review**; `/Admin/Capabilities` for the optional steps |
| **Complexity** | **High**. Stages, moves, guards and permissions are chosen from closed lists, with no code; but the design is real business judgement, stage codes must not change once records use them, and add/remove/move buttons act on the screen only until **Save** |
| **Prerequisites** | The workflow design (Playbook section 12); approver groups registered (step 6); capabilities decided; a second person |
| **Evidence produced** | The check result; the Preview and impact panel (new and removed stages and moves, changed permissions, approvals in progress); the review page; the release number |
| **Evidence grade** | **AUTO** (rehearsal step 9 relabels one button; capability and form-editor tests cover more). A full redesign of a workflow through the form has not been walked by a person |

**Remaining dependencies.** C2: the special moves, guard checks and recipient kinds are closed lists; behaviour outside them needs a developer. The impact panel counts records affected but **there is no simulation of a draft against records** (Not built). M5, M17.

---

### Step 8. Approval matrices

| | |
|---|---|
| **Actor** | Organisation Administrator A edits; B publishes; the Finance owner supplies thresholds |
| **Skill level** | S1 with the approval design from the Business Lead and Finance |
| **UI path** | `/Admin/Forms/Edit?kind=ApprovalMatrix`, or `/Admin/Approvals` tab **Edit (draft and publish)**; the simulator at `/Admin/Approvals/Simulate`; delegations at `/Admin/Approvals/Delegations`; history at `/Admin/Approvals/History` |
| **Complexity** | **High**. Rows, priorities, value ranges (lower bound included, upper excluded), currency and exchange-rate tables, exceptions; a gap or tie blocks a submission; the workflow must name the matrix and the register-authorisation capability must be on for the pages to show |
| **Prerequisites** | Groups registered with members (step 6); workflow ready (step 7); the thresholds in real figures (the Harborline-style tiers are invented examples); a second person |
| **Evidence produced** | The simulator output (**Resolved steps**, **Why each person is in or out**) per row; the check result; the impact panel; the review entry |
| **Evidence grade** | **AUTO** (rehearsal step 10 builds one matrix, one row, one step entirely from fields). Multi-tier matrices and exchange-rate cases are covered by approval tests, not by the rehearsal |

**Remaining dependencies.** M5; Finance sign-off on thresholds is a business decision, not a screen.

---

### Step 9. Notifications

| | |
|---|---|
| **Actor** | Organisation Administrator A configures; Tenant Administrator or Procurement Administrator watches delivery |
| **Skill level** | S1; S3 to make e-mail actually deliver |
| **UI path** | `/Admin/Forms/Edit?kind=NotificationPolicy` (reminders, privacy levels, delivery, who is told what, wording), `/Notifications` (a user's inbox), `/Admin/Notifications` (summary, deliveries, **Run the background work now**, retry) |
| **Complexity** | **Medium to High**. Many fields but all from closed lists; the real difficulty is deciding recipients, privacy levels and reminder windows. E-mail delivery depends on host settings the wizard does not set |
| **Prerequisites** | Workflow and groups in place; the Business Lead and Legal have decided events and wording; for e-mail, `Notifications__EmailProvider=smtp`, `Notifications__Smtp__Provider=Smtp` and the SMTP settings (System Administrator) |
| **Evidence produced** | The Preview of "What would be sent" for a sample contract; delivery summary and failures list; U6 and U7 results in the UAT log |
| **Evidence grade** | Policy editing **AUTO** (rehearsal step 11). In-app delivery and the outbox **AUTO** (notification tests). **Live SMTP delivery NOT VERIFIED**: stage 11 in the LIVE Production run failed for lack of a mail server |

**Remaining dependencies.** T7 (G-12: the setup page and the live delivery setting can disagree, and the readiness check can pass while mail fails); I8 (SMTP with STARTTLS on 587; implicit TLS on 465 is probably unsupported); M5. There is no "send me a test of this event" (G-29) and escalation of an unacknowledged reminder is recorded but does nothing (Future).

---

### Step 10. Migration

| | |
|---|---|
| **Actor** | Procurement Administrator 1 runs it; Procurement Administrator 2 confirms a legacy load; data owner answers for the source; Project Manager keeps the evidence |
| **Skill level** | S1 for the screens; S2 for the data owner. The preparation of the files is spreadsheet work |
| **UI path** | `/Migration` (project), `/Migration/Profiles` and `/Migration/Profiles/{id}` (load mappings and lookups, options, checks, publish), `/Migration/Projects/{id}` (reference policy, legacy-load policy, cutover freeze), `/Migration/Batches/{id}` (source files, dry run, errors, commit, reconcile, accept, roll back), and under each batch the pages Messages, Rows, Files, Reconciliation and Provenance |
| **Complexity** | **High**. Seven stages per batch, a closed transformation list, at least two rehearsals before the final load, and a second person for contracts that are already Effective |
| **Prerequisites** | The organisation's lifecycle is **published** (otherwise MIG-E026); fields to be loaded are published; two separate Procurement Administrator accounts; a verified backup around the final load; not in maintenance mode; a mapping CSV, a lookup CSV, the contracts file and optionally a manifest and ZIP, all prepared outside the application |
| **Evidence produced** | Source file SHA-256 values; the downloaded mappings and lookups; the dry-run counts; the error report (Excel or CSV) per dry run; row and file decisions with reasons; who confirmed the legacy load; the reconciliation report; the acceptance note; the provenance export; any rollback reason; audit events carrying the batch id |
| **Evidence grade** | **AUTO** on SQLite and SQL Server (rehearsal steps 13 to 18, including a deliberately wrong file, the refusal to commit, the clean re-run, second-person confirmation, reconciliation and acceptance). **Attachments in Production NOT VERIFIED and blocked**: no real scanner, so unscanned files are held |

**Remaining dependencies.** M7 to M10; C2 (owners, legal entity, business unit, site and users cannot be migrated, G-23; no mapping designer, G-24; JSON and ZIP-package sources are Future); C1 (attachments in Production); freeze times are entered in UTC (G-26); a non-ISO date format such as month/day/year is **not covered by any shipped sample**, so it must be confirmed on the dry run.

---

### Step 11. UAT

| | |
|---|---|
| **Actor** | Business Lead signs; Project Manager runs; one tester per role; Business Configuration Administrator and A fix configuration defects; B publishes the fixes |
| **Skill level** | S2 for testers; S1 to fix configuration |
| **UI path** | Scripts U1 to U14 in `UAT_AND_CUTOVER_GUIDE.md` section 4: `/SignIn`, `/Contracts`, `/Contracts/Create`, `/Contracts/Lifecycle/{id}`, `/Approvals`, `/Admin/Approvals/Simulate`, `/Variations`, `/Legal`, `/Notifications`, `/Admin/Notifications`, `/Admin/Terminology`, `/Admin/Regional`, `/Contracts` (search, views, exports), `/Admin/Documents/Scanning`, `/Migration/Batches/{id}`, `/Admin/Audit`, `/Admin/Operations/*`, `/Admin/Simulation` (access what-if) |
| **Complexity** | **High**. Fourteen scripts, ten testers, defect handling outside the application, an exit rule agreed beforehand, and a configuration freeze |
| **Prerequisites** | A non-production installation set up like production; configuration published by a second person; users created and given roles; the expected-results documents (approval design, workflow list, notification list, terminology list, regional settings); somewhere to keep the results log and defect list, because the application has no defect tracker |
| **Evidence produced** | Signed UAT report; the results log (Passed, Failed, Blocked, Not Verified); defect list with class (Configuration, Data, Product); configuration release numbers in force; screenshots; audit export; the 15-row go/no-go table |
| **Evidence grade** | The pages the scripts use are **AUTO** (rehearsal step 18 finds migrated contracts, documents and provenance). U1 access, U2 lifecycle, U3 approvals and the rest are covered by feature tests. **No human has run the scripts** |

**Remaining dependencies.** M11, M14; G-31: in Test mode U3's second-person parts cannot be shown, because the rule is off; U11 shows the gate, not protection; U6 e-mail is Not Verified without a real provider.

---

### Step 12. Backup

| | |
|---|---|
| **Actor** | System Administrator |
| **Skill level** | S3 for the one-time setup; S3 for each backup and restore (the pages are S1-friendly, the role is not given to business roles) |
| **UI path** | `/Admin/Operations` (Recovery readiness), `/Admin/Operations/Backups` (reason, **Create backup**, **Verify again**), `/Admin/Operations/Backups/{id}`, `/Admin/Operations/Restore` (choose, **Preview the restore**, confirm with a typed phrase, restore into a **new** database), `/Admin/Operations/Validation`, `/Admin/Operations/Maintenance`, `/Admin/Operations/Evidence`, `/Admin/Operations/Dashboard` |
| **Complexity** | **Very high** to set up (shared backup folder between the database server and the application, dedicated backup login, restore directory); **Medium** to run once set up |
| **Prerequisites** | SQL Server (SQLite refuses backup); `Ecp__Recovery__*` settings and a backup account secret; the organisation's decisions on frequency, retention, recovery objectives, who may read backups (the platform decides none of them); a recent sign-in |
| **Evidence produced** | Backup id and **manifest SHA-256 written into your own records**; outcome and warnings; the restore result card, the 16-check post-restore validation counts, the evidence file in the backup folder; measured recovery time; audit events |
| **Evidence grade** | Backup, verify, restore into a new database and validation on real SQL Server **AUTO and CI** (`BackupRestoreSqlServerTests` and siblings). The rehearsal's step 20 only **opens** the page. Not run LIVE in this review |

**Remaining dependencies.** T10, T11 (there is no built-in schedule, G-27), T12 (key-ring loss has no documented recovery, G-11), M12, M13, M19; I4 to I6. Restore over the live database is a command-line disaster-recovery action. Azure SQL has no backup provider.

---

### Step 13. Upgrade pre-flight

| | |
|---|---|
| **Actor** | System Administrator; Business Lead reads breaking changes and warnings |
| **Skill level** | S3 |
| **UI path** | `/Admin/Operations/UpgradeReadiness` (**Run the pre-flight**), `/Admin/Operations/UpgradePreview` (preview, rehearse on a restored copy, apply), `/Admin/Operations/MigrationReport`, `/Admin/Operations/UpgradeHistory`, `/Admin/Operations/PostUpgradeValidation`, `/Admin/Operations/RollbackEvidence` |
| **Complexity** | **High**. The pre-flight itself is one click; the real upgrade needs the new build deployed beside the old one, a verified pre-upgrade backup, maintenance mode, a rehearsal on a restored copy, and there is **no downgrade**: the only way back is restore |
| **Prerequisites** | A verified backup (step 12); a newer build to upgrade to; the previous release kept; a quiet period (the pre-flight reads every document and many tables) |
| **Evidence produced** | Pre-flight JSON and CSV (Ready, Ready with warnings, Blocked, Not Verified); manifest SHA-256; rehearsal result and migration durations; warnings accepted by code and by whom; post-upgrade validation; rollback proof; audit events |
| **Evidence grade** | The N-1 to N rehearsal on real SQL Server **CI**. In the 20-step rehearsal, step 19 records a pre-flight verdict of **"Blocked"** on the fresh synthetic installation, and the test only asserts that a verdict word appears; the reason is not recorded, so **that step does not prove a Ready pre-flight** (finding G-37) |

**Remaining dependencies.** T13; M12; I12. A "previous-release binary as the source" is Not Verified.

---

## 4. Dependency inventories

"Blocks" says which verdict the item stands in the way of: **P** Production, **M** masked-data rehearsal in Test, **D** Demo/Test, **none** it is a recorded limit only.

### 4.1 Remaining technical dependencies (need a technical role; not on any screen)

| ID | Dependency | Who | Blocks | Source |
|---|---|---|---|---|
| T1 | Install Docker and Compose; build or obtain the image (`ecp:local`); run `compose up`; keep the supplied hardening | S3 | D | Installation guide 7 |
| T2 | Create an empty SQL Server 2022 database and two logins (application; backup/restore). A least-privilege application login is **not documented or verified** (G-16): the tested login was a full administrator | S3 | P | 4.1, G-16 |
| T3 | Secret files outside any repository, owned by user id 1654, mode 0400; never paste a connection string | S3 | P | 4.2 |
| T4 | Set about fifteen environment settings correctly; a **missing or unrecognised `Ecp__Mode` silently becomes Production**; `Ecp__DemoSeed` true skips the wizard | S3 | D, P | 5 |
| T5 | Reverse proxy that ends TLS and forwards `Host`, `X-Forwarded-For`, `X-Forwarded-Proto`. Without the scheme, `/Setup` returns a bare 500 (observed) | S3 | P | 8.1, G-33 |
| T6 | Register the application with the identity provider; build the redirect addresses by hand (the wizard does not show them, G-10); make the provider emit a short `groups` claim; keep the client secret as `env:` or `file:` | S3 and IdP admin | P | 8.2 |
| T7 | Set `Notifications__EmailProvider`, `Notifications__Smtp__Provider` and SMTP settings in the environment; the wizard's e-mail stage does not change live delivery (G-12) | S3 | P | 5, 11 |
| T8 | Trust the Pack Publisher's **public** key (host configuration, or the Tenant Administrator on `/Admin/Packs/TrustKeys`) and mount signed pack files | S3 / S1 | P | Pack guide 4, Installation 6 |
| T9 | Issue the one-time setup token on the server; allow remote setup temporarily; remove the setting afterwards | S3 | D, P | 10 |
| T10 | Create the shared backup folder, the restore directory and the backup login; set the `Ecp__Recovery__*` settings | S3 | M, P | Operations 4 |
| T11 | Schedule backups and rehearsals in the organisation's own scheduler and alert on failure (no built-in schedule, G-27) | S3 | P | Operations 4.2 |
| T12 | Back up the key ring with the data folder; loss makes every page except `/health/live` answer 503 and there is no documented recovery (G-11) | S3 | P | Installation 11, 13 |
| T13 | Upgrade logistics: deploy the new build beside the old, keep the old, restore-in-place disaster recovery, take over a stuck lease; command-line verbs | S3 | P | Operations 12, 14 |
| T14 | Certificate and key expiry watching; log and metric shipping (OpenTelemetry, syslog, SIEM exporters are not built) | S3 | P | Dashboard docs |

### 4.2 Remaining manual steps (people doing things no screen does for them)

| ID | Step | Who | Why it is manual |
|---|---|---|---|
| M1 | Run the discovery workshop; record decisions D-01 to D-30 | Project Manager, Business Lead | Business judgement |
| M2 | Fill the data-collection workbook (20 sheets) | Business Administrator, data owner | No import of the workbook |
| M3 | Tell each invited person; an invitation is only recorded and **no e-mail is sent** (G-06) | Tenant / Organisation Administrator | Product limit |
| M4 | Grant each person's roles one at a time on their page; administrator and approver roles cannot be mapped from identity groups | Tenant / Organisation Administrator | No bulk |
| M5 | Arrange a **second person** for every publication before work begins | Business Lead | A single administrator can never publish in Production |
| M6 | Type or paste lists in the onboarding wizard (structure, categories, owners, viewers, mappings); there is no file upload there; one bad line saves nothing | Organisation Administrator | Design |
| M7 | Author the mapping CSV, lookup CSV, contracts file, manifest and ZIP in a spreadsheet; logic is done before loading, not in the platform | Procurement Administrator, data owner | Closed transformation list |
| M8 | Extract and clean legacy data; **mask it** if it is a masked-data rehearsal: the platform has no masking aid | Data owner | Outside the product (G-35) |
| M9 | Confirm date formats other than ISO on the dry-run sample | Procurement Administrator | Not covered by a shipped sample |
| M10 | Convert the cutover freeze times to UTC by hand (G-26) | Business Administrator | Product limit |
| M11 | Keep the UAT results log and defect list; there is no tracker or result page | Project Manager | Product limit |
| M12 | Copy evidence files (restore, upgrade, rehearsal) out of the backup folder into the change record | S3 | Files sit in the backup folder |
| M13 | Write each backup's manifest SHA-256 into your own records (it detects a replaced manifest) | S3 | By design |
| M14 | Obtain production approval from the organisation's own governance | Business Lead | External by design |
| M15 | Compare the publisher's package checksum and key fingerprint by a **separate channel** | Tenant Administrator | Trust decision |
| M16 | Find the identity-provider group names for role mapping (a wrong name silently does nothing) | IdP admin | Outside the product |
| M17 | Save before any action that may send you to **Confirm it is you**: unsaved form input is lost | Every administrator | Step-up behaviour |
| M18 | Key custody: generate, store, rotate, back up and revoke the private signing key | Security / S4 | The application never holds it |
| M19 | Decide and run the backup frequency, retention and restore-rehearsal cadence | Business Lead, S3 | Decisions the platform does not make |
| M20 | Register the redirect and sign-out addresses with the provider and keep them in the technical sheet | S3 | G-10 |

### 4.3 Remaining code dependencies (a developer changes and rebuilds the product)

| ID | Dependency | Blocks | Notes |
|---|---|---|---|
| C1 | **A real malware-scanner adapter** (an implementation of `IMalwareScanner`, registered in the host, with tests). None exists. Without one R-DOC-01 is Unhealthy in Production, `/health/ready` answers 503 and the setup wizard is expected to refuse to finish. `Documents__AllowSkippedScan=true` does not help (also Unhealthy) | **P** | G-25; `DOCUMENT_SCANNING.md`; code: `DocumentScanReadinessCheck`, `PlatformCheck` |
| C2 | Anything outside the closed vocabularies: a new workflow special move, guard check or recipient kind; a new capability; a new terminology key (G-18); contract types that drive behaviour (G-22); new migration targets such as owners, legal entity, business unit, site and users (G-23); JSON or ZIP-package migration sources; a mapping designer (G-24) | none today | Customer-specific needs beyond the shipped lists |
| C3 | A switch to turn the second-person rule on outside Production (the option exists in `ConfigurationOptions.RequireSecondPersonPublish` and is set from the mode only) | **M, D** | G-31 |
| C4 | Key Vault and DPAPI secret notations are accepted but never resolved (G-14); implicit-TLS SMTP on 465 (G-12); Microsoft Graph e-mail is a scaffold that fails every send | P (if the customer needs them) | |
| C5 | Live e-signature adapter (INT-03), reporting or BI, public API, AI features, marketplace, other identity protocols (only OpenID Connect exists) | none today | Future by design |
| C6 | Azure SQL backup provider and a blob document store (Azure model); IIS and Azure deployments are Scaffolded | none unless chosen | |
| C7 | Defects found in this review: raw terminology placeholders on setup pages (G-32); a bare 500 instead of an explanation when the proxy does not forward the scheme (G-33) | none (cosmetic / diagnostic) | |

### 4.4 Remaining infrastructure dependencies

| ID | Dependency | Blocks |
|---|---|---|
| I1 | A container host with Docker Engine and Compose and a persistent volume for the data folder (documents, packs, key ring, setup state) | D |
| I2 | SQL Server 2022 (a licensed edition for real use; the tested one was Developer); SQLite is for demonstration and refused in Production | D, P |
| I3 | A public HTTPS address, a certificate and DNS | P |
| I4 | A backup folder visible to **both** the database server and the application, a second folder for restore rehearsals, and space for a full copy of the document store | M, P |
| I5 | A scheduler for backups (cron, a job server) and alerting | P |
| I6 | Off-host, access-controlled storage for backup copies and for evidence files | P |
| I7 | An identity provider: Microsoft Entra ID or another standard OpenID Connect provider with a groups claim | P (and M if real sign-in is wanted) |
| I8 | An SMTP relay offering STARTTLS on port 587 and a sender address | P |
| I9 | A secret store the host can read as files or environment variables | P |
| I10 | A build host with the .NET 10 SDK and a checkout of the repository, to run the pack tool; a private-key store (hardware module or vault) for the signing key | P |
| I11 | A channel to the Pack Publisher that is separate from the one that delivers the file | P |
| I12 | A second, non-production installation (or a restored copy) for UAT, restore rehearsal and upgrade rehearsal | M, P |
| I13 | A malware-scanning service for C1 | P |
| I14 | Monitoring and a log sink (the platform exposes health pages and a local reader only) | P |

---

## 5. Findings from this review (new gaps G-31 to G-37)

They extend the register in `docs/IMPLEMENTATION_READINESS.md` (section 8). None was fixed by the review itself, which produced a document; the Company Rehearsal Enablement slice then closed them (status table in section 9.1).

| ID | Finding | Severity | Evidence | Suggested resolution |
|---|---|---|---|---|
| **G-31** | The second-person publication rule is on only in Production (`RequireSecondPersonPublish = production`) and has no setting. A Demo or Test rehearsal publishes immediately, so U3's governance, the review inbox and the author-cannot-approve behaviour cannot be exercised by a person; the automated rehearsal turns it on inside the test | **High** for any rehearsal | Code `PlatformComposition.cs`; `NoCodeRehearsalTests.cs` line setting the option; `BUSINESS_ADMIN_GUIDE.md` 1.3 ("Production does; Demonstration and Test do not") | Expose the option as a setting honoured in Demo and Test; label its state on `/Admin/Health` |
| **G-32** | Terminology tokens are shown raw on setup pages: `[[solution.pack|sentence]]` in the browser title (stage 5); `Email [[notification|plural|lower]]` in "Complete first: …" | Low | LIVE; `Step.cshtml` sets the title without `Terms.Expand`; `SetupService.cs` builds the "Complete first" text from raw titles | Expand tokens in titles and in the message |
| **G-33** | `/Setup` in Production behind a proxy that does not forward the scheme answers a bare 500 "Something went wrong"; the cause (antiforgery cookie needs SSL) is only in the log, and the guide says the symptom is a sign-in redirect mismatch | Medium | LIVE | Detect the plain-HTTP case and say so on the page; add the symptom to the installation guide's "If something goes wrong" |
| **G-34** | Signing a pack needs `dotnet run --project src/Ecp.PackTool` from a repository checkout and the SDK. The tool is not in the container image or any release artefact | Medium | Dockerfile; `PACK_SIGNING.md` | Ship the tool as a release artefact; or record the source checkout as a Pack Publisher prerequisite |
| **G-35** | The platform has no aid for masking data and cannot tell masked from real. The Test-mode acknowledgement is a tick box saying the installation must not hold real confidential data | Medium | Source search; wizard stage 1 | A written masking standard in the migration guide; a sample masking script is optional |
| **G-36** | Stale statements remain in the playbook outside the banner: section 1.1 ("the Publish button cannot succeed in Production today"; changes need a pack author), Appendix B rows B-20 to B-23, B-26, B-31, Appendix E ratings D for matrix, workflow and notification authoring. The banner says the guides are authoritative, but a reader of Appendix E alone is misled. Classification levels: the setup guide gives them to a Business Administrator at stage 10; the onboarding guide says they are set at installation by a System Administrator | Low | Read | Update Appendices B and E; align the two statements |
| **G-37** | Rehearsal step 19 asserts only that a verdict word (Ready, Blocked, Not verified) appears, so it records "Blocked" as a pass and does not prove a Ready pre-flight; the N-1 to N rehearsal in CI is the real proof | Low | `NoCodeRehearsalTests.cs` lines around 316 to 330 | Assert the reason, or label the step as "page opens" |

Also confirmed rather than new: the Pack guide's recorded contradiction about who registers a trusted key (the Tenant Administrator holds `packs.trust.manage`, not the System Administrator); the JBS pack proposes Australia/Brisbane while the guides' examples use Australia/Sydney.

---

## 6. Original verdicts of 2026-10-18 (superseded by section 9)

These are kept unchanged as the record of the first review. Where they say the second-person rule is off in Test, a rehearsal needs a switch for it, a scanner is a mock or a pack tool needs a source checkout, section 9 states what changed.

### 6.1 Can a new customer be implemented in Demo/Test today?

**Yes, with conditions.**

*Evidence for.*

| Evidence | Grade |
|---|---|
| The business journey (new organisation, pack drafts, regional, terminology, capabilities, approver group, workflow, approval matrix, notification policy, reviews by a second person, migration with a refused bad file and a confirmed legacy load, reconciliation, acceptance, finding the loaded contracts) passes over real HTTP on SQLite and on **real SQL Server** | AUTO (20 steps, 18 by business roles) |
| Role separation holds in that run: self-publication was attempted and refused; the committer alone was refused; business roles were refused the Operations pages | AUTO |
| The whole suite is green: 1,799 passed, 0 failed, 0 skipped | AUTO |
| CI run 69: build, unit, architecture, API, SQL Server integration (migrations, constraints, row-level security, N-1 to N upgrade rehearsal, backup and restore), container image build and smoke test | CI |
| A Test-mode host on SQL Server reports Healthy on every check, including the demonstration scanner | LIVE |
| Backup, verify, restore into a new database and validation on real SQL Server | AUTO, CI |

*Conditions.*

1. A technical lead (S3) does the installation and the Test-mode settings, and a Pack Publisher is not needed (unsigned synthetic packs are allowed in Test).
2. Treat the guides as untested by a person: record every place a participant hesitates, which is the evidence Demo/Test exists to collect.
3. Use synthetic data only.
4. Do not read a Demo/Test pass of the second-person steps as proof of Production governance (G-31).
5. Accept that live e-mail, a real identity provider and a real scanner are Not Verified.

### 6.2 Can a new customer be implemented with masked production data today?

**Yes, with conditions, and only in Test mode.** Real data shapes and volumes exercise the migration, the reconciliation and the recovery path. They do not make Test mode equal to Production.

*Evidence for.*

| Evidence | Grade |
|---|---|
| Migration up to 250,000 rows per file and 500 MB, chunked commit with checkpoints, resume after a stop, idempotent re-run, second-person legacy confirmation, reconciliation, acceptance, rollback before acceptance and compensating archival after | AUTO (scenarios on SQL Server), READ for limits |
| Backup, verify and restore of a populated synthetic installation on SQL Server, with post-restore validation | AUTO, CI |
| Performance is acceptable at 10,000 contracts and **unknown at 100,000** (`docs/PERFORMANCE_REVIEW.md`; `docs/PROJECT_STATUS.md`) | READ |
| The mock scanner releases ordinary files and quarantines the standard test string, so masked attachments can be loaded in Test | AUTO |

*Conditions (all must hold).*

1. **Masking is the customer's own work and responsibility** (M8, G-35). The platform cannot verify it. A named data-protection owner signs that no live personal, commercial or privileged value remains, including inside attachments, file names and free-text fields, before the first load.
2. The installation runs in **Test** mode, with the acknowledgement ticked on stage 1; never Production mode (it cannot finish setup) and never Demo mode with seeded data.
3. Real SQL Server, a real backup folder and a restore rehearsal into a new database are included (T10, I4): this is where masked volume pays off.
4. Governance is rehearsed **outside** the product's settings: because the second-person rule is off in Test (G-31), run the published-change steps under a written procedure with two people, and rely on the automated rehearsal for proof of the rule.
5. Attachments pass through the mock scanner, which is no protection. That is acceptable only because the data are masked.
6. Use a real identity provider only if its directory is also non-production; the Test-mode behaviour with a real provider is Not Verified.
7. Do not extrapolate volume results beyond 10,000 contracts without running the performance baseline at the customer's scale.

*What it will not prove.* Production governance, live mail, a real scanner, the Production wizard, proxy and TLS behaviour, least-privilege database rights.

### 6.3 Can a new customer be implemented in Production today?

**No.**

*Evidence.*

| # | Evidence | Grade |
|---|---|---|
| 1 | A fresh Production installation answers **503 Unhealthy** on `/health/ready`, even with the database current, the packs signed and trusted, and the data folder writable. The identical database in Test mode answers 200 Healthy with every check Healthy. The Production-only difference is R-DOC-01 | LIVE |
| 2 | R-DOC-01 is Unhealthy in Production when no scanner is connected, when only the demonstration scanner is connected, and when skipped scans are allowed | AUTO (`Production_is_not_ready_without_a_real_connected_scanner…`) |
| 3 | No real scanner adapter exists. In Production the mock is not registered and the module falls back to `DisabledMalwareScanner`, which reports "not connected" | READ (`PlatformComposition.cs`; `DocumentsApi.cs`) |
| 4 | The setup wizard's readiness stage treats an Unhealthy platform check as **blocking**, and a blocking check cannot be acknowledged; so Finish setup is expected to be refused | READ (`PlatformCheck`); the stage itself was **not reached** LIVE |
| 5 | Without signed packs the Production wizard stops at stage 5 with an empty list. No signed Production pack and no real publisher key exist in the repository | LIVE |
| 6 | The Production wizard cannot pass stage 11 without a real SMTP server that accepts a test message; Mock and Disabled are not offered | LIVE |
| 7 | Stage 13's R-ID-02 (a successful test sign-in) is blocking in Production, and no real identity provider has ever been used with this product | READ; NOT VERIFIED |
| 8 | Attachments in Production are held and cannot be used (migration MIG-F014; document gate) | AUTO / READ |
| 9 | Live SMTP, live Entra, least-privilege database login, the Production composition file, a reverse proxy, IIS and Azure are all **Not Verified** | NOT VERIFIED |
| 10 | No unaided non-developer has followed any guide; no assistive-technology test | NOT VERIFIED |

*What would turn this into a "yes, supervised".*

| Priority | Item | Owner |
|---|---|---|
| 1 | A real scanner adapter, connected and tested (C1), **or** a documented, audited, owner-accepted position on attachments that changes R-DOC-01 deliberately (decision D-17; a product-owner and security decision, not an installation setting) | Product owner, security, developer |
| 2 | A Production-mode rehearsal in a non-production environment with the customer's real identity provider and mail relay, taken through stage 14 and one full day of use | System Administrator |
| 3 | Signed packs and a custodied key (T8, M18, G-34) | Pack Publisher |
| 4 | A Test/rehearsal switch for the second-person rule (G-31) so that people can practise governance before Production | Developer |
| 5 | A least-privilege database login script and a restore-in-place rehearsal | DBA, System Administrator |
| 6 | One unaided run by a trained non-developer using only the nine guides, with every hesitation logged | Customer, Project Manager |

Production approval remains external governance. A "yes" at that point would be a recommendation to the customer's governance body, not an approval.

---

## 7. Evidence index

| Evidence | Where |
|---|---|
| The 23-step automated rehearsal ledger | `docs/NO_CODE_REHEARSAL.md`; the two ledger files named in that document, written beside the API test binaries |
| Full local test totals | `deploy/STATUS.md` (row for the readiness slice), `TASKS.md` |
| CI run 69 | `deploy/STATUS.md`; <https://github.com/Jeffa90/ContractMGMT/actions/runs/39137676309> |
| Gap register | `docs/IMPLEMENTATION_READINESS.md` (G-01 to G-30, and G-31 to G-37 from this document) |
| Production gate code | `src/Ecp.Host/DocumentScanReadinessCheck.cs`, `src/Modules/Ecp.Setup/Stages/SetupChecks.cs` (`PlatformCheck`), `src/Ecp.Host/PlatformComposition.cs` |
| Production gate test | `tests/Ecp.Api.Tests/DocumentScanningUiTests.cs` |
| Second-person rule setting | `src/Ecp.Host/PlatformComposition.cs`, `src/Modules/Ecp.Configuration/ConfigurationApi.cs` (`ConfigurationOptions`) |

**How the LIVE observations were made (repeatable, no secrets).** A throwaway database `ecp_rehearsal_prod` was created on a SQL Server 2022 container; the connection string was written to a file with mode 0400 outside the repository and referenced as `file:`; the settings in `INSTALLATION_GUIDE.md` section 5 were exported (Mode Production, DemoSeed false, DemoTenantResolution false, SqlServer, AutoMigrate false, data folder outside any temporary location, SMTP provider and base URL set); `Ecp.Host.dll migrate` was run; the host was started on a loopback port with `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true`; `setup-token` supplied the token; a small script posted the wizard forms with the anti-forgery token and a cookie jar and an `X-Forwarded-Proto: https` header. Packs were built and signed with `Ecp.PackTool` and an ephemeral key that exists only outside the repository, and the public key was trusted with `Ecp__Packs__TrustFile`. The same database was then started in Test mode on a second port to read the per-check list. The throwaway files and the database are not part of the repository; nothing in the repository was changed to make these observations.

## 8. Reading this document honestly

- A **LIVE** observation is an HTTP-level fact about one build on one machine. It is stronger than a statement read from a guide and weaker than a person succeeding in a browser.
- The thirteen steps are not timed; the complexity words are the authors' judgement, to be replaced by what a real rehearsal measures.
- The verdicts do not depend on anything marked NOT VERIFIED being true. Where a verdict is "No" or "Yes, with conditions", the Not Verified items are named as the conditions or the blockers.
- Production approval is external governance. Neither this document nor the platform declares it.

---

## 9. Re-run after the Company Rehearsal Enablement slice (2026-10-19)

The slice (ADR-0038, CONFIRMED_DECISIONS D14.1 to D14.15) closed G-31 to G-37 and added email, scanner, masked-data, network and evidence capabilities. The rehearsal was re-run with the corrected rules.

### 9.1 Gap status

| ID | Resolution | Status | Evidence |
|---|---|---|---|
| **G-31** | `Ecp:Governance:SecondPerson` (`Default` or `Enforced`). Production always enforces and cannot lower it. Test can enforce exactly the Production rule; Demo ignores the setting. The effective mode (`Production`, `TestEnforced`, `TestSimplified`, `DemoSimplified`) is shown on the setup report, `/Admin/Health` and the operations dashboard, and recorded on configuration audit events and releases | **Implemented and Verified** (SQLite and SQL Server) | `GovernanceModeTests`, `GovernanceModeUiTests`, the 23-step rehearsal |
| **G-32** | Titles and messages are expanded at their origin; a response guard replaces any remaining token with a neutral marker and logs a stable code; a render sweep over the Blank, JBS Procurement and Construction Subcontracts packs asserts no `[[` token reaches a reader | **Implemented and Verified** | `TerminologyTests`, `RenderedPageSweepTests`, `SetupHostTests` |
| **G-33** | A Production request that arrives as plain HTTP is answered with a short safe page and the code `ECP-NET-001` (no scheme forwarded) or `ECP-NET-002` (forwarded headers not trusted); readiness check `R-NET-01` and a setup check report the posture; `docs/REVERSE_PROXY.md` explains the proxy and Docker settings | **Implemented and Verified** (five test cases); a real reverse proxy is Not Verified | `NetworkDiagnosticsTests` |
| **G-34** | `ecp-pack` is a standalone .NET tool package (`Ecp.PackTool`) that needs no source checkout: build, validate, report, hash, inspect, sign, verify, compare, keygen; the private key is referenced by `env:` or `file:`, never typed; exit codes 0, 1, 2 and 3. CI packs it, installs that artefact alone and exercises it | **Implemented and Verified** locally and in CI (run 74) | `PackToolArtefactTests`; `.github/workflows/ci.yml` |
| **G-35** | Masked-data declaration (owner, source, method, fields, attachments, personal and confidential data, approval reference, approver, expiry, permitted environment and users) gates commit of a batch marked Masked Company Data in Test mode. The platform never states the data is safe | **Implemented and Verified** | `MaskedDataDeclarationTests`, rehearsal step 16 |
| **G-36** | Guides reconciled; `tools/check-docs.py` fails the build when a guide names a page or a permission that does not exist, repeats a retired statement or tells a business role to run a command | **Implemented and Verified** | `python3 -I tools/check-docs.py .`, `tools/tests/test_check_docs.py` |
| **G-37** | Evidence semantics in one place (`EvidenceSemantics`, codes EVD-nnn); the ledger and the pre-flight pages keep the operational outcome beside the verdict; warnings are accepted on the record (`AcceptedWarningTests`) | **Implemented and Verified** | `EvidenceSemanticsTests`, `RehearsalLedgerSemanticsTests`, rehearsal steps 21 to 23 |
| G-12 | Email is one installation configuration for setup and administration; readiness is proved by a connectivity probe and a synthetic test message; unreachable is never Healthy; results expire; SMTP and Graph stay Not Verified against real services | **Implemented**; SMTP **Verified only against a loopback test relay**; Graph **Scaffolded, Not Verified** | `EmailRulesTests`, `EmailEnablementTests`, rehearsal step 23 |
| G-25 | Scanner contract, configuration, provider-neutral readiness test, labelled mock only in Demo/Test by explicit configuration, mock acceptance recorded for masked-data rehearsals (who, reason, scope, expiry), alerts on quarantine, infection and exhausted retries | **Implemented**; no real scanner exists (**Blocked** for Production) | `ScannerReadinessTests`, `DocumentScanningUiTests`, rehearsal step 17 |

### 9.2 What the re-run showed

* Steps 1 to 23 pass on SQLite and on real SQL Server; four steps need a technical role (17, 21, 22, 23). The upgrade pre-flight (step 22) is **Ready with warnings** with each warning accepted on the record, so it counts as Passed (EVD-010); it is no longer possible to record a Blocked pre-flight as a pass. Ledger: `docs/NO_CODE_REHEARSAL.md`.
* A Production installation still answers Unhealthy without a connected scanner (R-DOC-01). The slice does not weaken this.
* The email readiness page reports Healthy only after a real interaction with a mail server; in the test that server is a loopback relay.

### 9.3 Final verdicts, with evidence and conditions

**1. Synthetic company in Demo: Supported with conditions.**
Evidence: the business journey runs over real HTTP in Demo-style hosts (AUTO). Conditions: governance is simplified (the second-person setting is ignored in Demo), email is simulated, the scanner is the demonstration scanner, only synthetic data is used. Do not read a Demo pass of a second-person step as proof of Production governance.

**2. Synthetic company in Test: Supported with conditions.**
Evidence: 23 of 23 steps Passed on real SQL Server with `Ecp:Governance:SecondPerson=Enforced` (AUTO); the dashboard and setup report show `TestEnforced`. Conditions: a technical lead installs and sets the Test-mode settings (including the governance setting); a supervised session records every hesitation, because no unaided person has followed the guides (Not Verified); a real mail service, real identity provider and real scanner are Not Verified; synthetic data only.

**3. Masked-data company in Test: Supported with conditions.**
Evidence: steps 14 to 19 (declaration gate, scanner acceptance, second-person confirmation, reconciliation). Conditions, all required: (a) the customer masks the data and a named owner signs the declaration, because the platform cannot verify masking and does no automatic de-identification; (b) the declaration is approved, current and permits the environment and the users; (c) the Test mode installation enforces second-person governance; (d) the demonstration scanner is accepted on the record for the scope and period, because it is no protection; (e) real SQL Server, a verified backup and a restore rehearsal are included; (f) volume beyond 10,000 contracts is unmeasured; (g) any real identity provider uses a non-production directory.

**4. Production-mode setup: Blocked.**
Evidence: no real scanner adapter exists, the mock is refused in Production and R-DOC-01 is Unhealthy (AUTO, `DocumentScanningUiTests`); a Production email test requires a real mail server (Not Verified); a real identity provider, signed packs (the tool now exists; no publisher key exists) and the Production-mode rehearsal in a non-production environment are outstanding.

**5. Production use with confidential contracts: Blocked.**
Same blockers as 4, and attachments cannot be used in Production without a scanner. Production approval is external governance and is not declared by this document or the platform.

### 9.4 What would change the Production verdicts

A real scanner adapter connected and healthy; a Production-mode rehearsal in a non-production environment with the customer's identity provider and mail relay taken through setup and a day of use; signed packs with a custodied key; a least-privilege database login and a restore-in-place rehearsal; one unaided run by a trained non-developer. Any "yes" would be a recommendation to the customer's governance body, not an approval.
