# Implementation playbook

For: Project Managers, Business Leads, System Administrators and Business Administrators. **No programming knowledge is assumed.**

Status: written 2026-10-18 from the platform as built at that date (platform version 1.1.0); reconciled on 2026-10-19 with the Implementation Readiness Gap Closure and Company Rehearsal Enablement slices (see the two update notes below and section 1.4). Every page, field, rule and message in this playbook was read from the product's source code and documentation by people (and research assistants) who did not run a live installation for this document; where something is a reading of the code rather than something observed, it says so. Production approval is **external governance**: nothing in this playbook, and nothing the platform shows, approves an installation for production use.

> **Update (Implementation Readiness Gap Closure, 2026-10-18).** After this playbook was written, the slice described in `docs/IMPLEMENTATION_READINESS.md` fixed gaps G-01, G-02, G-03, G-04, G-19, G-20 and G-21, added the Configuration reviews inbox, the organisation wizard, the approver-group pages, the workflow / approval matrix / notification policy form editors, rollback governance and the document-scanning gate, and wrote eight click-by-click guides (`docs/INSTALLATION_GUIDE.md`, `docs/SETUP_WIZARD_GUIDE.md`, `docs/ORGANISATION_ONBOARDING_GUIDE.md`, `docs/BUSINESS_ADMIN_GUIDE.md`, `docs/PACK_IMPLEMENTATION_GUIDE.md`, `docs/MIGRATION_EXECUTION_GUIDE.md`, `docs/UAT_AND_CUTOVER_GUIDE.md`, `docs/OPERATIONS_QUICK_GUIDE.md`). **Where this playbook says a screen does not exist or that JSON must be edited for a workflow, matrix or notification policy, the gap register and the guides are now authoritative**; the rest of the playbook (decisions, workshops, data collection, sequencing) is unchanged. The journey is proved by the 23-step ledger in `docs/NO_CODE_REHEARSAL.md` (originally 20 steps). Appendix D below keeps the original findings as written; each row that has since been closed carries a status note, and the full status column is in the register.

> **Update 2 (Company Rehearsal Enablement, 2026-10-19; ADR-0038).** A second slice closed G-31 to G-37 and added a governance setting for Test mode, evidence semantics, a real email readiness test, a scanner boundary, a masked-data declaration, a standalone pack tool, proxy/HTTPS diagnostics and an evidence pack. Section 1.4 states each in a few lines. The verdicts for each kind of rehearsal are in `docs/IMPLEMENTATION_REHEARSAL.md` section 9 (see 1.4). Statements below that contradict section 1.4 are history. Nothing here, and nothing the platform shows, is a production approval.

## How to use this playbook

1. The **Project Manager** reads sections 1 to 3 first, then uses sections 14 to 20 as the plan.
2. The **Business Lead** owns the decisions in section 3 and the decision register (Appendix C) and signs off UAT (section 16) and cutover (section 17).
3. The **System Administrator** does the technical steps: sections 1, 5 and the parts of 6, 14, 19 and 20 marked "System Administrator".
4. The **Business Administrator** does the configuration steps: sections 7 to 15, except where a row says it needs someone else.
5. Appendix B is the list of everything a Business Administrator **cannot** yet do alone. Read it before you promise anyone a date. Appendix D is the gap list: what stops this from being a true no-code experience today.

### Ratings used throughout ("Can a Business Administrator do this unaided?")

| Rating | Meaning |
|---|---|
| **A: Unaided** | A trained Business Administrator can finish it alone with the inputs from the workshop |
| **B: Unaided with preparation** | Needs decisions or data prepared in advance (a workbook, a mapping file, a list of people), but no technical skill |
| **C: Needs a System Administrator** | Needs server access, a command line, environment settings, secrets or the identity-provider portal |
| **D: Needs a developer, pack author or product owner** | Cannot be finished without a person outside the business team; see Appendix B |

"Trained" means the person has completed the supplier's administrator training and has read this playbook. Ratings are the authors' judgement from the product as built; test them in your own rehearsal (section 16).

### Words used

| Word | Plain meaning |
|---|---|
| **Installation** | One running copy of the platform, with its database |
| **Tenant** | The customer account inside an installation. Usually one per company or group |
| **Organisation** | A business inside a tenant (for example "Procurement" or "Harborline"). Most settings are per organisation |
| **Solution pack** (pack) | A bundle of starting settings for a kind of business: wording, workflows, approval rules, reminder rules. It is data only. Choosing a pack never changes anything by itself |
| **Draft, validate, preview, publish** | The safe way to change any setting: you work on a draft, the platform checks it, you preview the effect, then you publish. Each publish makes a numbered, permanent release. You can go back to an earlier release (it is published again as a new release) |
| **Capability** | A process step that can be switched on or off for an organisation (for example Legal review) |
| **Terminology** | The words people see ("Supplier", "Subcontractor"). Changing a word never changes permissions or records |
| **Maintenance mode** | A switch that stops all changes while keeping reading, sign-in and health checks working |
| **Not Verified** | Nobody has checked it, or it cannot be checked here. It is **never** a pass |

---

## 1. Pre-requisites

### 1.1 People and time (the project cannot start without these)

| Item | Why | Who provides it |
|---|---|---|
| A named **Project Manager** and **Business Lead** | Decisions and dates | Sponsor |
| At least **two** Business Administrators trained on the platform | In Production, a configuration change must be published by a **second person**. The form pages submit a draft for review and a second person approves and publishes it at `/Admin/Reviews` (G-01 is fixed; Appendix D keeps the original finding as history). Rehearse this in Test with governance set to Enforced (section 1.4) | Business Lead |
| A **System Administrator** with access to the server, database and identity provider | Installation, secrets, backups, upgrades | IT |
| An **identity provider administrator** (for example Microsoft Entra ID) | To register the platform, create groups, read the group names | IT |
| A **data owner** for the old system | To extract and clean the contract data | Business Lead |
| A **Legal / compliance lead** | To decide classification levels (entered at setup stage 10; see 1.4) and review rules | Business Lead |
| Access to a **Pack Publisher** (technical; the supplier or a trained person) | Only to build and sign a whole new or customer-specific pack for Production. Changes to workflows, approval matrices and notification policies are made by Business Administrators on forms at `/Admin/Forms/Edit` (G-03 is fixed; Appendix B) | Supplier |

### 1.2 Technology (System Administrator)

| Item | Requirement | Status |
|---|---|---|
| Hosting model | **Container with an external SQL Server and persistent document storage.** This is the only model with executed test evidence | **Verified** |
| IIS on Windows Server | Guidance exists | **Scaffolded / Not Verified**: nobody has installed it on IIS for this product |
| Azure App Service with Azure SQL | A deployment template exists | **Scaffolded / Not Verified**; Azure SQL is not supported by the backup tooling |
| Container engine | Docker Engine with the Compose plugin | Verified (Compose 5.x) |
| .NET | The container image carries it (.NET 10). You do not install .NET for the container model | |
| Database | **SQL Server 2022** (tested edition: Developer, in a container). A **licensed** edition is needed for real use. The lightweight SQLite database is for demonstration only and is refused in Production | Verified on 2022 only |
| Database accounts | One account for the application (it needs to create schema on first run and for upgrades) and a **separate backup and restore account** (needs BACKUP DATABASE, RESTORE, CREATE DATABASE, and permission to connect to the master database). The documents do not explain how to create a least-privilege runtime login (Appendix D, G-16); the tested setup used a full administrator account | **Not Verified** for least privilege |
| Web address and TLS | A real HTTPS address and a reverse proxy or ingress that ends TLS. The application itself listens on plain HTTP port 8080. In Production the site only works over HTTPS | |
| Storage | A persistent volume for the data directory (documents, packs, key ring). A **shared backup folder** that both the database server and the application can read and write (needs file-permission work on Linux), and a second folder with room for restore rehearsals | |
| Identity | A Microsoft Entra ID tenant or another standard OpenID Connect provider. The live Entra sign-in has **not** been tested against a real directory | **Not Verified** (tested against a built-in stand-in only) |
| Email | An SMTP server with TLS (port 587). Microsoft Graph email is a scaffold, not connected. Readiness is proved by a real provider interaction (section 1.4) | SMTP **Implemented**, verified only against a loopback test relay in the automated tests; **Not Verified** against a customer's mail service. Graph **Scaffolded, Not Verified** |
| Secrets | Somewhere to keep the database connection string, the identity client secret and the mail password as files or environment variables. The names `keyvault:` and `dpapi:` are accepted as notations but **do not work** today (Appendix D, G-14) | |
| Signed packs | In Production only **signed** packs are accepted. The three shipped packs are unsigned samples. Someone must sign them (or the supplier's own signed packs must be installed) using the standalone `ecp-pack` tool (a **Technical task** for the Pack Publisher; section 1.4), and the public key must be registered or trusted | **Technical task** (Appendix B); signing and key custody are **Not Verified** in any customer environment |

### 1.3 Decisions to have made before installation

These are in the decision register (Appendix C). The blocking ones are: which pack to start from, the mode (Production or not), the identity provider, the email provider, the list of classification level names (open decision Q-06), who the first administrator is, and who the second publisher is.

### 1.4 What the Company Rehearsal Enablement slice changed (2026-10-19)

Concise statements; the guides give the click-by-click detail. Anything marked **Technical task** is for the System Administrator or Pack Publisher, never a Business Administrator.

- **Test-mode governance (`Ecp:Governance:SecondPerson`).** The setting is `Default` or `Enforced`. Production **always** enforces the second-person publish rule and the setting cannot lower it. In Test, `Enforced` applies exactly the Production rule: the author, last editor and submitter cannot publish, a submission is bound to the content hash, and a rollback is also submitted for a second person. `Default` keeps the simplified behaviour (publish immediately). Demo ignores the setting and always keeps simplified governance. The effective mode (Production, TestEnforced, TestSimplified or DemoSimplified) is shown on the setup report, `/Admin/Health` and `/Admin/Operations/Dashboard`, and recorded on configuration audit events and releases. **Technical task:** the System Administrator sets `Enforced` for a governance rehearsal; at least two Business Configuration or Organisation Administrators are needed.
- **Evidence semantics (every report, rehearsal and pre-flight).** Ready is a pass. Ready with warnings is a pass **only** when every warning was accepted on the record (who, reason, expiry of at most 90 days; by a System Administrator with a recent sign-in, permission `operations.maintenance`; for pre-flight warnings the page is `/Admin/Operations/UpgradeReadiness`). Blocked and Failed are a fail. Not Verified is Not Verified, never a pass. The operational outcome is always shown unchanged beside the verdict. Codes are EVD-nnn. A pre-flight that is Blocked is never counted as a pass.
- **Email readiness (`/Admin/Operations/Email`).** One installation-level configuration is used by both the setup wizard's email stage and Administration. Reading needs `operations.read`; changing needs the System Administrator (`operations.maintenance`) with a recent sign-in. Providers: `smtp`; `graph` (scaffold, Not Verified, not connected); `dev-capture` and `demo-log` (Demo and Test only, deliver nothing, refused in Production); `disabled` (not allowed in Production). Only non-secret settings and secret references (`env:`, `file:`, `keyvault:`) are stored; a secret typed into the form is refused and never echoed. Readiness is proved by an **actual provider interaction**: a connectivity probe plus a synthetic test message with fixed text (no contract information). A result is tied to the settings (a change makes it stale) and expires after 168 hours by default (`Ecp:Email:ReadinessMaxAgeHours`). A configured but unreachable provider is never Healthy; simulated providers read "Simulated: nothing delivered". Production setup cannot finish the email stage without a passing test. Email is therefore **not** "ready once configured".
- **Malware scanner (`/Admin/Operations/Scanner`).** The platform defines the integration boundary (`IMalwareScanner`) and a provider-neutral readiness self-test. **No real scanner adapter exists** (not built, Not Verified). Configuration is `Documents:Scanner:*` (Provider none, mock or a provider key; Endpoint; SecretRef as a reference only; TimeoutSeconds 30; MaxFileSizeBytes 100 MB; RetryBackoffSeconds 60; SelfTestMaxAgeHours 24) and `Documents:MaxScanAttempts`. The labelled mock ("demonstration scanner") is registered in Demo by default, in Test **only** when Provider is set to mock on purpose, and never in Production; it protects nothing and the page carries a TEST MODE warning. Using the mock for a masked-data rehearsal must also be **accepted on the record** (kind `scanner.mock`: who, reason, scope, expiry of at most 90 days; System Administrator with a recent sign-in). Production is Unhealthy on R-DOC-01 and cannot finish setup without a connected, healthy scanner; attachments are held. Quarantine, infection, provider unavailability and exhausted retries raise operational notifications to recipients already defined in notification configuration. The connect-a-provider checklist is `docs/DOCUMENT_SCANNING.md`.
- **Masked-data declaration (`/Migration/MaskedData`; Test mode only).** The customer masks the data; the platform does not mask, detect or de-identify, and never states the data is safe. A Procurement Administrator (migration permission) records a declaration: dataset owner, source system, masking method, fields masked, attachments included, personal data included, confidential data included, approval reference, approved by, approved on, review-by date (expiry) and permitted users. On the batch page ("Data handling") the batch is marked Masked Company Data (or Synthetic) and linked to a declaration. In Test mode a batch marked Masked Company Data cannot be committed without a current, approved, unrevoked declaration that permits the environment and the user, and not while the mock scanner is unaccepted; each refusal gives a code and a sentence. A declaration can be revoked (reason required), which blocks dependent batches. Second-person confirmation of a legacy load still applies. The Test-mode acknowledgement tick on setup stage 1 is **not** the masked-data control. Production data follows separate governance.
- **Standalone pack tool (`ecp-pack`; Technical task, Pack Publisher).** `Ecp.PackTool` is a .NET tool package installed with `dotnet tool install Ecp.PackTool --tool-path <dir> --add-source <folder holding the .nupkg>`. It needs the .NET runtime, not a source checkout or the repository's SDK. Commands: version, build, build-all, validate, report, hash, inspect, sign, verify, compare, keygen. Sign takes `--key-ref env:VARIABLE` or `--key-ref file:/path/to/private.pem`; the key is never typed, printed, stored or logged, the application never holds a private key, and there is no browser signing. Builds are deterministic. `report` writes a sanitised JSON report (identity, hashes, findings; no content, key, signature bytes or local paths). Exit codes: 0 success, 1 package or request not acceptable (including failed verification), 2 usage error, 3 a file or key could not be read or written, or a key reference could not be used. The Pack Publisher hands over signed packs and the **public** key only; the Tenant Administrator (holds `packs.trust.manage`) registers the public key at `/Admin/Packs/TrustKeys`, or the platform operator trusts a key by host configuration (`Ecp:Packs:TrustFile`). Verified locally; the CI step that installs and exercises the packaged tool is recorded in `docs/IMPLEMENTATION_REHEARSAL.md` section 9.1 with its own label.
- **Proxy and HTTPS diagnostics.** Production needs HTTPS. A plain-HTTP request in Production gets a short safe page with `ECP-NET-001` (no HTTPS scheme was forwarded: use the https address or make the proxy forward `X-Forwarded-Proto`) or `ECP-NET-002` (forwarded headers arrived but were not trusted: set `Ecp:Network:ForwardedHeaders:Enabled` and the KnownProxies or KnownNetworks). Readiness check R-NET-01 reports the posture and the setup wizard shows plain-language TLS and proxy failures. Detail: `docs/REVERSE_PROXY.md`. A real reverse proxy remains **Not Verified** end to end (**Technical task**: System Administrator).
- **Evidence pack (`/Admin/Operations/EvidencePack`, permission `operations.evidence.export`).** Exports a sanitised company implementation evidence pack covering environment, version, deployment, pack, organisation setup, governance, identity, email, scanner, masked-data status, configuration publication, migration and reconciliation, UAT, backup, upgrade pre-flight, operational readiness, open risks and accepted exceptions, with sign-off placeholders. No secrets and no contract or document content; formula-safe CSV; stable codes, hashes and correlation ids. The sign-off lines are placeholders for people to complete; the platform declares no approval.
- **Terminology tokens.** No raw `[[...]]` token should ever be visible on Setup, Onboarding, Pack Manager or Administration pages. If one is, it is a defect (a neutral marker appears and a stable code is logged).
- **Who sets classification levels (one answer; the setup and onboarding guides had disagreed).** The Legal / compliance lead and Business Lead decide the names (open decision Q-06). They are entered **once, at setup stage 10** (`/Setup/Step/classification`), by a Business Administrator while the System Administrator holds the setup session (no one is signed in during setup; the stage publishes through the trusted setup path). They are **not** edited in the onboarding wizard or on any business configuration page. After setup they can be amended **only** at `/Admin/Setup` (stage "Classification levels") by an installation administrator, that is the System Administrator holding `setup.run`, with a recent sign-in. A Business Administrator therefore cannot change them alone after setup. (Read from `ClassificationStage` in `src/Modules/Ecp.Setup` and the `/Admin/Setup` pages; not run in a browser for this document.)
- **Rehearsal verdicts.** The current verdicts are in `docs/IMPLEMENTATION_REHEARSAL.md` section 9 (the ledger is `docs/NO_CODE_REHEARSAL.md`, 23 steps, Test mode, Enforced governance): a synthetic company in Demo, **Supported with conditions**; in Test, **Supported with conditions**; a masked-data company in Test, **Supported with conditions**; Production-mode setup, **Blocked**; Production use with confidential contracts, **Blocked**. Production approval is external governance.

---

## 2. Roles and responsibilities

### 2.1 Project roles

| Role | Does | Must not |
|---|---|---|
| **Project Manager** | Plan, risks, workshop logistics, workbook collection, UAT and cutover schedule, hypercare rota | Make configuration changes |
| **Business Lead** | Owns decisions (wording, classification, approvals, workflow), signs off UAT and cutover, accepts migration exceptions | Share administrator sign-ins |
| **System Administrator** | Server, container, database, secrets, backups, upgrades, identity-provider setup, setup token, maintenance mode, monitoring | Decide business rules |
| **Business Administrator** | Setup wizard (with the System Administrator), onboarding wizard, terminology, regional settings, capabilities, packs, migration, UAT scripts | Skip the validate and preview steps |
| **Second publisher** | Publishes what another administrator drafted (required in Production) | Publish their own draft |
| **Data owner** | Extracts, cleans and certifies the legacy data; approves reconciliation | |
| **Legal / compliance lead** | Classification levels, legal review rules, retention decisions | |
| **Identity provider administrator** | Registers the application, creates groups, supplies group names | |
| **Pack Publisher** (technical; formerly "pack author") | Builds and signs packs with `ecp-pack` and hands over signed packs and the public key only (see 1.4 and Appendix B). Workflows, approval matrices and notification policies are edited by Business Administrators on forms | |

### 2.2 Product roles (what the people are called inside the platform)

The platform has twelve built-in roles. Their codes never change; their labels follow your terminology. The ones an implementation team touches:

| Role (neutral label) | What it allows | Who should hold it during the project |
|---|---|---|
| System Administrator | Installation-level operations: backup, restore, upgrade, maintenance, the operations dashboard | System Administrator only. Holds nothing inside a customer tenant |
| Tenant Administrator | Users and roles for the whole tenant, packs, trust keys | Business Lead's delegate; **cannot run the onboarding wizard** (see 7.2) |
| Organisation Administrator | The onboarding wizard, configuration, users within the organisation | The Business Administrators |
| Business Configuration Administrator | Draft and publish workflow, notification and approval settings; terminology; regional settings | Business Administrators (cannot publish capability changes) |
| Contract Administrator (labelled "Procurement Administrator" in the JBS pack) | Runs the migration wizard, notifications administration | Migration team |
| Contract Owner, Reviewer, Approver, Backup Approver, Auditor, Viewer, Division/Site Administrator | Day-to-day roles | End users, set during onboarding or user administration |

Administrator and approver roles can only be granted on the **Users** page, not in the wizards. Nobody can grant a role to themselves, and the last administrator cannot be removed.

### 2.3 What each project role does at each stage (summary)

| Stage | PM | BL | SA | BA |
|---|---|---|---|---|
| Discovery workshop | Organise | Decide | Attend (technology) | Attend, take notes |
| Workbook | Chase | Approve | Technical sheets | Fill and check |
| Installation | Track | Sign-off | Do | Observe |
| Setup wizard | Track | Decide inputs | Run commands, give secrets | Enter fields |
| Onboarding wizard | Track | Approve lists | Identity-provider groups | Do |
| Terminology, regional, approvals, workflow, notifications | Track | Decide | Email/secret settings | Do (see ratings) |
| Migration | Plan | Accept | Environment, scanner decision | Do |
| UAT and cutover | Run | Sign off | Backup, maintenance, monitoring | Support |
| Hypercare | Rota | Triage | Watch the dashboard | Fix configuration |

---

## 3. Discovery workshop checklist

Hold one workshop per organisation (or one combined session if the businesses are similar). Allow a full day, plus a half-day follow-up for the approval and workflow detail. Bring the Business Lead, Legal lead, two or three real contract owners, someone from finance or procurement who knows the approval limits, the System Administrator, and the data owner of the old system.

### 3.1 Questions to answer (tick when answered and written down)

**The business**
- [ ] What do we call the things we manage: contract, agreement, subcontract? What do we call the other party: supplier, counterparty, subcontractor, vendor? (Terminology, section 9.)
- [ ] How many separate businesses (organisations) will use the platform? Do they share people, categories or approval rules? (If more than one: a Tenant Administrator adds further organisations at `/Admin/Organisations/New`; G-02 is fixed.)
- [ ] Which country or region, language-and-region tag (for example en-AU, en-US), currency, time zone? One per organisation. (Section 10.)
- [ ] Which tax or company identifier do you record for the other party, and in what shape (length, mask, checksum)?
- [ ] What address fields do you need, and which are required?

**Structure and people**
- [ ] Legal entities, divisions, business units and sites (with short codes). (Onboarding step 2.)
- [ ] The category list (and sub-categories, up to six levels). The contract types list. (Steps 3 and 4. Contract types are recorded but do **not** yet change behaviour: Appendix D, G-22.)
- [ ] Who owns each category? (Names and email addresses, who is primary.)
- [ ] Who may see what? For each group of readers: all contracts, or limited by category, business unit or site; and the highest classification level they may open.
- [ ] How will people sign in, and which identity groups exist that should grant a role automatically? (Group names exactly as the provider sends them.)

**Rules**
- [ ] The classification levels (names, order, default for new records, which levels need special permission). *This is an open owner decision (Q-06); the placeholder scale is Public, Internal, Confidential, Restricted, Legal Privileged.*
- [ ] Which process steps are used: Legal review? Registration approval? Variations? Renewals? Signature requests? (Capabilities, section 8.3.)
- [ ] Approval: who approves what, by value and by risk? Is one approver group enough, or are there value tiers with several people? (Section 11.)
- [ ] Workflow: the states a contract passes through and the allowed moves; which states lock the record; what must exist before each move (final document, executed copy, comment). (Section 12.)
- [ ] Notifications: who is told about what; reminder lead times (for example 30/60/90/120 days); critical-notice offsets; what to do when a category has no owner. (Section 13.)
- [ ] Retention and legal hold: the platform currently keeps records indefinitely pending a decision; are there any rules you must record now? (The retention and legal-hold blocking on archive is **Scaffolded**.)

**Data**
- [ ] Which old systems hold contracts? How many contracts, how many documents, in what formats? Who extracts them? Are dates in one format? Are statuses consistent? (Sections 14 and 15.)
- [ ] Which contracts are live now (effective), and so need a **legacy load** rather than an ordinary load?
- [ ] Is the source "registered date" trustworthy? Do you want it kept?

**Technology**
- [ ] Hosting model, web address, database server, backup folder, storage encryption, identity provider, email server, secret storage, signing keys for packs. (Section 5.)
- [ ] Recovery targets: how much data may be lost (RPO) and how long may recovery take (RTO)? These are **placeholders you must fill**; the platform records them, it does not decide them.
- [ ] Who is on the hypercare rota and who is the second publisher?

### 3.2 Workshop outputs
A signed-off copy of the data collection workbook (section 4), the decision register (Appendix C) with owners and dates, the list of people and groups, and the migration inventory.

---

## 4. Data collection workbook

Use one spreadsheet file with one sheet per table below. **Save the list sheets as CSV (comma separated, UTF-8)** because the onboarding wizard takes CSV text. Keep a header row; lines starting with `#` are ignored by the wizard. Use `;` between several values in one cell.

### Sheet 1: Organisation

| Column | Example (Blank) | Example (JBS-style) | Example (Harborline-style) | Rules |
|---|---|---|---|---|
| Tenant name | Example Holdings | JBS Group (example) | Harborline Group | Max 120 characters |
| Organisation name | Example Holdings Pty Ltd | Procurement | Harborline Construction | Max 200; must be unique in the tenant |
| Reference prefix | CON | JBSAUS | HBL | 2 to 16 letters or digits, starting with a letter; made upper case |
| Country or region | (decide) | Australia | United States | From the offered list |
| Locale | (decide) | en-AU | en-US | A language-and-region tag; decides date and number layout |
| Currency | (decide) | AUD | USD | Three-letter ISO code |
| Time zone | (decide) | Australia/Brisbane | America/Chicago | A place-based name such as Area/City |
| Tax identifier type | (none for now) | ABN (11 digits) | Federal Tax ID (##-#######) | See section 10 |

### Sheet 2: Structure (onboarding step 2, CSV columns `type,key,name,parentKey,timeZone,registrationNumber`)

| type | key | name | parentKey | timeZone | registrationNumber |
|---|---|---|---|---|---|
| LegalEntity | LE-1 | Example Holdings Pty Ltd | | | (company number) |
| Division | OPS | Operations | | | |
| BusinessUnit | FIN | Finance | OPS | | |
| Site | HQ | Head office | | Australia/Brisbane | |

Rules: type is one of LegalEntity, Division, BusinessUnit, Site. Key: 1 to 40 letters, digits, `_` or `-`, starting with a letter or digit (**do not use a full stop in a key**: the check now rejects it, as publishing does; G-21 is fixed). Name 1 to 200 characters. parentKey is used only by a BusinessUnit (its Division). Time zone is used only by a Site. Registration number is used only by a LegalEntity.

### Sheet 3: Categories (step 3, `code,name,parentCode`)
Code 1 to 40 characters; name 1 to 200; parent optional; no cycles; no more than six levels.

### Sheet 4: Contract types (step 4, `code,name`)
Recorded only (Scaffolded).

### Sheet 5: Category owners (step 5, `categoryCode,email,primary`)
The person **must already exist** as a user (invited or active). `primary` is yes or no (default yes).

### Sheet 6: Viewers (step 6, `email,viewerKind,categories,businessUnits,sites,ceiling`)
viewerKind is one of AllContracts, Category, BusinessUnit, Site, Restricted. Ceiling is **required in practice** and must be one of your classification levels. A Restricted viewer's ceiling cannot be above Confidential through the wizard.

### Sheet 7: Group mappings (step 7, `providerKey,groupKey,role,viewerKind,categories,businessUnits,sites,ceiling`)
Roles the wizard accepts: Viewer, CategoryOwner, Legal, Auditor, ProcurementAdministrator, DivisionSiteAdministrator. The group name must be exactly what the identity provider sends.

### Sheet 8: Terminology (section 9)
Stable key, neutral word, your singular, your plural. Start from the pack's list (86 keys exist; 32 are required).

### Sheet 9: Capabilities (section 8.3)
Five rows: Legal review, Registration approval, Variations, Renewals, Signature requests: on or off, and why.

### Sheet 10: Regional (section 10)
Locale, supported locales, currency, supported currencies, time zone, time display (locale / 24-hour / 12-hour), phone region, identifier types (key, label, country, rule, length or mask, checksum, required, unique, classification), address fields used and required.

### Sheet 11: Approval design (section 11)
For each approval: name, value from, value to, currency, category / risk / other conditions, flow (one after another or together), steps (approver group, backup group, how many must respond, time limit).

### Sheet 12: Workflow design (section 12)
States (name, locked?, who may add documents), moves (from, to, who may do it, what must be true).

### Sheet 13: Notification design (section 13)
Event, who is told, in-app or email, mandatory?, wording, plus reminder lead times and offsets.

### Sheet 14: People and groups
Name, email, role wanted, scope, ceiling, identity group, approver group key (Approver and Backup Approver only).

### Sheet 15: Migration source inventory (section 14)
System, owner, extract date, contracts, documents, file types, date format, status values, category values, supplier naming quality, known problems.

### Sheet 16: Mapping workbook (section 14)
Source column, target field, transformation, default, required, lookup key, validation parameters, notes (this is the field-mapping CSV format).

### Sheet 17: Lookups
Lookup key, source value, target code, notes (status, category, document type, extension type).

### Sheet 18: UAT scripts and results (section 16)

### Sheet 19: Cutover checklist and hypercare log (sections 17 and 18)

### Sheet 20: Technical sheet (System Administrator only; **never put a secret in this file**)
Hosting, addresses, database server and name, account names, backup folder paths, secret *references* (names, not values), identity provider details, mail server, recovery targets, who holds each key.

---

## 5. Installation (System Administrator)

Rating for the whole section: **C**. A Business Administrator cannot do any of it. There is **no installer program and no installation guide** in the product; these steps are assembled from the deployment files and the host's behaviour (Appendix D, G-13). Treat the first run as a rehearsal on a non-production machine.

### 5.1 Steps (container model, the only one with test evidence)

1. **Prepare the database.** Create an empty database on SQL Server 2022. Create the application login and the separate backup/restore login (permissions in 1.2). Record the names in the technical sheet.
2. **Create the secret files.** One file holds the full database connection string; another the backup account's connection string. Keep them outside the code folder, readable only by the account the container runs as (user id 1654, undocumented, Appendix D, G-15). Never email or paste a secret.
3. **Build or obtain the image.** The production Compose file expects an image named `ecp:local` and has no build instruction; build it first with the supplier's instructions.
4. **Edit the Compose file.** The supplied production file needs you to switch on the volumes (data, backups, restore) and add the settings in 5.2. Do not remove the read-only, no-new-privileges and non-root settings.
5. **Run the database setup once**, as a deliberate step (`migrate`). In Production, this refuses to run again on a database that already holds an installation; after that, schema changes only come through the upgrade procedure (section 20).
6. **Start the application.** Check health in a browser or with a monitoring tool: `/health/live` should say Live; `/health/startup` answers 503 until the database is current; `/health/ready` answers 200 for Healthy or Degraded and 503 for Unhealthy. In Production the ready answer shows only the overall word, no detail.
7. **Open the setup wizard** (section 6). A fresh installation redirects every browser page to `/Setup` and answers 503 to API calls until setup is complete.

### 5.2 Settings the System Administrator must set (names use double underscores for environment variables)

| Setting | Purpose and example | Secret? |
|---|---|---|
| `Ecp__Mode` = `Production` | The mode. **A missing or unrecognised value silently becomes Production.** The shipped default file says Demo, so always set it explicitly | No |
| `Ecp__DemoSeed` = `false` | Must be false. With true (the shipped default) the installation fills itself with fake data and **skips the setup wizard entirely** | No |
| `Ecp__DemoTenantResolution` = `false` | Must be false in Production (default true) | No |
| `Database__Provider` = `SqlServer` | Production refuses anything else (default is SQLite) | No |
| `Database__ConnectionStringRef` = `file:/run/secrets/ecp-sql` | A reference to the secret file. Only `env:NAME` and `file:/path` work. A pasted connection string is refused in Production | The file's content |
| `Database__AutoMigrate` = `false` | Production never migrates by itself | No |
| `Ecp__DataDirectory` = `/var/ecp` | Documents, packs, key ring, setup state. **Must be a persistent volume**, not a temporary folder; losing the key ring loses sessions and protected setup state | No |
| `Ecp__Setup__AllowRemote` = `true` | Only while running setup through a published port (setup is otherwise reachable only from the server itself). Remove afterwards | No |
| `Notifications__EmailProvider` = `smtp` | Host-level starting value. Email is now one installation configuration at `/Admin/Operations/Email`, shared by the setup wizard and Administration (section 1.4); confirm on that page which settings are in force, and run its test. The default `dev-capture` is refused in Production | No |
| `Notifications__Smtp__From`, `__Host`, `__Port` (587), `__UseTls` (true), `__UserRef`, `__PasswordRef` | Mail server settings; user and password are secret references (`env:` or `file:`) | References only |
| `Notifications__BaseUrl` | Web address used in links inside emails | No |
| `Notifications__JobIntervalSeconds` | How often background work runs (default 60; 0 turns it off) | No |
| `Ecp__Recovery__BackupDirectory`, `__SqlServerBackupDirectory`, `__RestoreTargetDirectory` | The shared backup folder as the application sees it and as the database server sees it, and the folder for restore rehearsals | No |
| `Ecp__Recovery__AdminConnectionStringRef` | The backup/restore account (reference). If missing, readiness reports a Degraded backup check | The file's content |
| `Ecp__Recovery__StorageProtection` | One sentence describing how the backup storage is protected. Recorded, not checked | No |
| `Ecp__Recovery__EncryptionCertificate` | Optional name of a SQL Server certificate for encrypted backups; the certificate must itself be backed up elsewhere | Certificate is sensitive |
| `Ecp__Packs__TrustFile` or `Ecp__Packs__TrustedKeys__0__KeyId` and `__PublicKeyPem` | The **public** key that signed your packs | No (public) |
| `Ecp__Packs__ShippedDirectory` | Where signed pack files are mounted (default under the application folder) | No |
| `Documents__AllowSkippedScan` | There is **no real malware scanner**. With false (default) a document that has not been scanned cannot be downloaded in Production; true accepts that risk. This is a product-owner and security decision (Appendix C, D-17) | No |

Consequences of getting these wrong: a wrong mode can start the system in the wrong posture; leaving the demo settings on prevents start-up in Production (the start-up guard refuses) or hides the wizard; a non-persistent data directory loses documents or the key ring; a missing `Notifications__EmailProvider` means no email in Production.

### 5.3 Reverse proxy and identity-provider registration
The proxy must end TLS and forward the original scheme and client address. In the identity provider, register the redirect addresses the setup wizard will use (the wizard does **not** display them; build them as `https://<site>/auth/callback/oidc-<tenant id without hyphens>-<provider key>` and the installation equivalent; ask the supplier, Appendix D, G-10).

### 5.4 Installation checkpoint
- [ ] Health addresses answer as described.
- [ ] No secret appears in any file under the code folder or in the project documents.
- [ ] A backup folder test file written by the database server can be read by the application.
- [ ] The technical sheet is complete, and the person who holds each secret and key is named.

---

## 6. Setup wizard: every page and field

**What it is.** The one-time wizard for a fresh installation. It runs at `/Setup`, in 14 stages, in a fixed order. Each stage saves when you press its button and can be repeated safely (running the same input again changes nothing). The wizard survives a restart or a closed browser.

**Who.** A System Administrator opens it (needs a command line on the server); a Business Administrator enters the business fields with them. Rating for opening the wizard: **C**. Rating for the business fields: **B**.

**Opening it.**
1. The System Administrator runs the command `setup-token` on the server (for a container: run it inside the container). It prints a 256-bit token **once**, to that terminal only. It is valid for 24 hours and is single use. Running the command again replaces it. There is no way to get a token from a web page.
2. Browse to `/Setup` **from the server itself** (or set `Ecp__Setup__AllowRemote=true` temporarily). Paste the token into **"Setup token"** and press **"Open setup"**.
3. A setup session starts: one at a time, 60 minutes of inactivity ends it. Five wrong tokens lock entry for five minutes. Messages: "The setup token is not valid, has expired, or was already used. Issue a new token from the command line." / "A setup session is already open…" / "Too many incorrect tokens…" / "Setup is already complete."

**If the wizard never appears:** the installation was started with demonstration data switched on (`Ecp__DemoSeed` true). Demo mode seeds fake data, marks setup complete and closes `/Setup`.

**Setup in the three modes.**

| Item | Demo / Test | Production |
|---|---|---|
| Acknowledgement tick on stage 1 | Required | Not shown |
| Database stage | Applies migrations itself | Only checks; you must have run the database setup |
| Demonstration sign-in | Offered if enabled | Never |
| Mock and Disabled email | Offered | Refused |
| Demonstration data stage | Exists (optional) | Does not exist |
| Unsigned packs | Only if the host allows unsigned samples | Only signed, trusted packs |
| Unresolved secret reference | Warning | Blocks |

The wizard cannot change the mode.

### Stage 1: Installation mode (`/Setup/Step/mode`)
**Purpose:** record how and where this installation runs. **Button:** "Save and continue". **Rating:** A (with the System Administrator's answers).

| Field | Required | Recommended | Rules | If wrong |
|---|---|---|---|---|
| "Deployment" | Yes | Container (all three profiles) | One of: Local development or evaluation / Windows Server with IIS / Azure App Service / Container. "Choose one of the listed deployments." | Recorded in the setup report only; it does not change behaviour. A wrong label misleads the audit trail |
| "Tenancy" | Yes | One organisation (Blank, JBS-style, Harborline-style when separate installations) | One organisation / Several organisations in one tenant / Several tenants. "Choose one of the listed tenancy modes." | Cannot be widened later through any screen (help text promises it; the code has no path: Appendix D, G-02). Choose **several organisations** now if two businesses will share one tenant |
| "I understand this installation is for demonstration or testing only…" | Demo/Test only | Tick | "Acknowledge that this installation is not for real data." | Cannot continue |

In Production this stage also runs the start-up guard and refuses with "Production configuration is not acceptable: [R-xx-nn] message…" if demo settings, an inline connection string, a non-SQL Server database or a temporary data folder are in use.

### Stage 2: Database (`/Setup/Step/database`)
**Purpose:** prove the database is reachable and current. **Button:** "Check database". No fields. **Rating:** C.
- Demo/Test: applies pending migrations automatically. Production: "The database schema is not current. Run the `migrate` command, then check again." plus "Pending: …".
- Success: "The database is reachable and all N modules are current."
- **If wrong:** nothing further can proceed. A database that is reachable but out of date is a System Administrator problem.

### Stage 3: Document storage (`/Setup/Step/storage`)
**Purpose:** prove documents can be stored and read back. **Button:** "Run storage check". No fields. **Rating:** C.
- The platform writes a small test object, reads it back, checks it exists, deletes it and checks it is gone. Failures: "The stored object did not read back identically." / "…could not be found again." / "…could not be removed."
- **If wrong:** if the data folder is temporary (Production), startup already refused it. If it is on a volume that disappears, you will lose every uploaded document later without any warning here.

### Stage 4: Tenant (`/Setup/Step/tenant`)
**Purpose:** create the tenant. **Button:** "Save and continue". **Rating:** A.

| Field | Required | Recommended | Rules | If wrong |
|---|---|---|---|---|
| "Tenant name" | Yes | Your company or group name | At most 120 characters. Repeating the stage adopts an existing tenant of that name | **Not changeable later**. The name is internal and not shown to suppliers |

### Stage 5: Solution pack (`/Setup/Step/pack`)
**Purpose:** choose the starting configuration. **Button:** "Save and continue". **Rating:** B.

| Field | Required | Recommended | Rules | If wrong |
|---|---|---|---|---|
| Starting pack (select) | Yes | **Blank** if you do not resemble the other two; **JBS Procurement Reference** for a procurement contract register; **Harborline / Construction Subcontracts** for a subcontract register | Must be in the catalogue. Nothing is preselected. Options show name, version, publisher and "signed" or "UNSIGNED (Demo and Test only)". In Production only packs signed by a trusted key are offered, so the list can be empty until signed packs are installed | Choosing the wrong pack gives you the wrong wording, workflow, capabilities, regional defaults and reminder rules as the starting point. You can change everything later, but the onboarding wizard does **not** re-assign the pack (use Administration, Packs, Organisation packs) (Appendix D, G-08) |

The page shows each pack's description, capabilities, example wording, and which optional steps the pack switches off (Legal Review, Register Authorisation, Variations, Renewals, Signature Requests). Nothing is published until stage 8.

### Stage 6: Identity provider (`/Setup/Step/identity`)
**Purpose:** decide how people sign in. **Button:** "Save and continue". **Rating:** C (needs the identity provider administrator and a secret file).

| Field | Required | Recommended | Rules | If wrong |
|---|---|---|---|---|
| "Provider type" | Yes | Microsoft Entra ID (or "Other OpenID Connect provider"). Demonstration sign-in only in Demo/Test | Demo sign-in is refused in Production | Nobody can sign in |
| "Provider key" | Yes | `entra` | "The provider key must be 2 to 40 lower-case letters, digits or hyphens." | Part of the callback address and of group mappings: changing later breaks both |
| "Name shown on the sign-in page" | Yes | "Company sign-in" | "A display name is required." | Cosmetic |
| "Entra directory (tenant) id" | Entra only | The GUID of your directory | Must be a GUID. `common`, `organizations` and `consumers` are refused because they allow other directories to sign in. A custom authority is refused for Entra | A wrong id fails every sign-in |
| "Authority (issuer URL)" | Other OIDC only | Your provider's address | Absolute, HTTPS, no fragment or credentials | Sign-in fails |
| "Client id" | Yes | From the provider's app registration | "The client id is required." | Sign-in fails |
| "Client secret reference" | No (strongly recommended) | `env:ECP_OIDC_SECRET` or `file:/run/secrets/oidc` | "The client secret must be a secret reference (env:NAME, file:/path or keyvault:name), never the secret itself." In Production an unresolved reference **blocks**; the `keyvault:` form does not resolve today (Appendix D, G-14) | A pasted secret is refused (good). A reference that does not resolve blocks readiness |
| "Groups claim" | Yes | `groups` | "A groups claim name is required for identity-group mapping (use 'groups')." | Group-to-role mapping never fires |

On saving, the platform registers the provider twice (tenant and installation administrators). **Register both redirect addresses in the provider yourself**; the page says it shows them after saving but it does not (G-10). Use the **"Test sign-in with <name>"** button on this page and later stages; a successful test records check R-ID-02, which blocks completion in Production.

### Stage 7: Initial administrator (`/Setup/Step/admin`)
**Purpose:** create the first administrator. **Button:** "Save and continue". **Rating:** A.

| Field | Required | Recommended | Rules | If wrong |
|---|---|---|---|---|
| "Full name" | Yes | The Business Lead's delegate | | Cosmetic |
| "Email address" | Yes | The address the identity provider asserts for this person | "Enter a valid email address." Accounts link by provider and subject, not by email alone | If it does not match, the person cannot sign in and setup cannot finish (check R-ADM-01) |
| "Also make this person an installation administrator" | No | Ticked, **but use a different, dedicated account in practice** (a separate account is created for the installation role) | Only installation administrators can reopen setup later | If unticked and nobody else is named, **nobody can ever reopen setup** (warning R-ADM-02) |

Creates the user, grants Tenant Administrator, and optionally a separate System Administrator account. Not changeable afterwards.

### Stage 8: Organisation (`/Setup/Step/organisation`)
**Purpose:** create the first organisation and publish its first regional settings. **Button:** "Save and continue". **Rating:** B. The chosen pack proposes defaults; the Blank pack proposes none (fields start empty).

| Field | Required | Recommended (Blank / JBS-style / Harborline-style) | Rules | If wrong |
|---|---|---|---|---|
| "Organisation name" | Yes | Your business name / Procurement / Harborline Construction | At most 200. Unique within the tenant: "…with that name or reference prefix already exists in this tenant." | Not changeable here later (use regional and terminology pages for related items) |
| "Country / region" | No | Decide / Australia / United States | From the list; "a label for people and for the identifier and address profiles; it decides no format by itself" | Wrong profile suggestions only |
| "Locale" | Yes | Decide (for example en-AU) / en-AU / en-US | "That locale is not recognised." Controls date, number and currency layout, **not** the page language | Dates and amounts are shown in unexpected order (for example 03/04) |
| "Time zone" | Yes | Decide / Australia/Brisbane / America/Chicago | Must be a recognised place-based time zone. Times are stored in UTC and shown in this zone | Wrong deadlines shown. Reminder "today" is judged in this zone |
| "Default currency" | Yes | Decide / AUD / USD | A three-letter ISO code the platform offers | Wrong default on new amounts. Existing amounts are never converted |
| "Tax identifier type" | No | (none for now) / ABN / Federal Tax ID | From the pack's list | Wrong field shape on supplier forms |
| "Contract reference prefix" | Yes | CON / JBSAUS / HBL | 2 to 16 letters or digits starting with a letter; upper-cased | Cannot be changed through any screen; every future reference carries it. **The help text says PREFIX-0001 but the platform allocates six digits (for example CON followed by a hyphen and six digits)** and the wizard has no field for digits or separator (G-20) |

Creates the organisation, publishes the first regional settings as release 1 (only if none exists), makes the initial administrator an Organisation Administrator of it, and publishes a record of the pack choice. Not changeable afterwards.

### Stage 9: Branding (`/Setup/Step/branding`)
**Purpose:** product name and accent colour. **Button:** "Save and continue". **Rating:** A. Amendable later.

| Field | Required | Recommended | Rules | If wrong |
|---|---|---|---|---|
| "Product name" | Yes | Your own, for example "Harborline Subcontract Hub" | 1 to 60 characters | Cosmetic |
| "Accent colour" | Yes | A dark brand colour, for example #1f3a8a | Six-digit hex; must have contrast ratio at least 4.5:1 against white text (accessibility). "The accent colour needs a contrast ratio of at least 4.5:1 against white text (WCAG 2.2 AA)." | Pale colours are refused |
| "Footer text" | No | Short legal or contact line | At most 200 characters | Cosmetic |

### Stage 10: Classification levels (`/Setup/Step/classification`)
**Purpose:** the sensitivity scale. **Button:** "Save and continue". **Rating:** B (the names are an owner decision, Q-06). Amendable later **only** at `/Admin/Setup` by the System Administrator (permission `setup.run`, recent sign-in); this is the only place the levels are entered or changed, not the onboarding wizard (section 1.4).

| Field | Required | Recommended | Rules | If wrong |
|---|---|---|---|---|
| "Levels, lowest first" | Yes | The pack's scale (Blank and JBS-style: Public, Internal, Confidential, Restricted, Legal Privileged; Harborline-style: four levels, no privileged level) | One per line (commas also split); each up to 50 characters; distinct (not case-sensitive) | **The scale is used by every access rule, approval condition and reminder privacy rule.** Renaming or removing a level later affects who can see records; do this with the Legal lead |
| "Default level for new records" | Yes | Internal | Must be one of the levels | New records are over- or under-protected |
| "Privileged levels" | No | Legal Privileged (if used) | Must be among the levels | Privileged records need an extra explicit grant; leaving it empty removes that protection |

The page itself says the final names are an open owner decision (Q-06). Decide them before UAT.

### Stage 11: Email notifications (`/Setup/Step/notifications`)
**Purpose:** prove an email provider works by sending a real test message. **Button:** "Save and continue". **Rating:** C. Amendable later.

| Field | Required | Recommended | Rules | If wrong |
|---|---|---|---|---|
| "Provider" | Yes | SMTP server (Production). Mock and Disabled only in Demo/Test | Production refuses Mock and Disabled | No email |
| "From address" | For SMTP and Mock | A monitored shared mailbox | "A valid From address is required." | Replies lost; mail may be rejected as spoofed |
| "SMTP host" | SMTP | Host name only | "Enter the SMTP host name (no scheme or path)." | Test fails |
| "SMTP port" | SMTP | 587 | 1 to 65535. **A blank or non-numeric value is silently turned into 587** | Test fails or hits the wrong port |
| "Use TLS" | | Ticked | Production requires TLS. Only STARTTLS on 587 is expected to work; port 465 (implicit TLS) is probably unsupported (G-12) | Mail refused |
| "SMTP user reference" / "SMTP password reference" | Both or neither | `env:ECP_SMTP_USER`, `file:/run/secrets/smtp-pass` | Must be a reference; "Set both the SMTP user and password references, or neither." In Production an unresolved reference blocks | A pasted secret is refused |
| "Send a test message to" | Yes (except Disabled) | Your own address | "Enter a valid test recipient address." The test is sent on **every** save | If you skip it, you do not know mail works |

**Important (updated 2026-10-19):** the stage now uses the same installation email configuration as `/Admin/Operations/Email`, and readiness is proved by a connectivity probe plus a synthetic test message, tied to the settings and expiring (section 1.4). Production setup cannot finish this stage without a passing test. The original finding (the stage did not change live delivery; G-12) is history; SMTP stays Not Verified against a customer's mail service.

### Stage 12: Demonstration data (`/Setup/Step/demo`)
Demo/Test only; absent in Production. Buttons "Add demonstration data" and "Skip this step". Adds synthetic tenants alongside yours. **Never** use in an installation that will hold real data.

### Stage 13: Readiness review (`/Setup/Step/readiness`)
**Purpose:** run the checks and acknowledge warnings. **Button:** "Run checks". **Rating:** B (needs the System Administrator for any blocker).

The 12 checks (blocking unless noted): R-OPS-01 platform health; R-ST-01 storage stage done; R-SEC-01 Production guard clean; R-DEMO-01 no demo in Production; R-ID-01 an identity provider is valid; R-ID-02 a test sign-in succeeded (blocking in Production, warning elsewhere); R-ADM-01 a tenant administrator exists and has signed in (blocking in Production when only invited); R-ADM-02 an installation administrator named (warning); R-ORG-01 an organisation exists; R-CFG-01 pack, branding and classification published; R-NOT-01 email is a real working provider (Mock or Disabled blocks in Production); R-AUD-01 the audit chain verifies.

After a run, each warning shows a text box "Acknowledge <check id>: <description>": enter a reason (up to 300 characters); who, when and why are recorded. Messages: "N blocking check(s) failed." / "Acknowledge each warning with a reason, then run the checks again."
**If wrong:** you cannot finish. Do not acknowledge a warning you do not understand.

### Stage 14: Finish setup (`/Setup/Step/complete`)
**Purpose:** close setup. **Button:** "Finish setup". Re-runs every check. On success, setup is audited, the token and session are cleared, `/Setup` returns 404 forever, and you land on the sign-in page: "Setup is complete. Sign in with the administrator you created."

### 6.1 After setup
- The administrator page is `/Admin/Setup`, for the System Administrator only, and needs a sign-in within the last 10 minutes. Only these stages can be amended: storage, identity, branding, classification, notifications, readiness. Changes are audited.
- The **setup report** (`/Setup/Report`, JSON with `?format=json`) lists mode, versions, stages, checks with acknowledgements, integration kinds, the pack and administrator names. It contains no secrets, connection strings, addresses or tokens. Keep a copy as evidence. It is evidence for the owner, **not** approval.
- If the state file becomes unreadable (damaged or key ring lost) everything except the live health check returns 503 and there is no documented recovery (G-11).

### 6.2 Setup checkpoint
- [ ] The report was saved. [ ] A second administrator exists (a Production configuration change needs two people). [ ] Email readiness passed on `/Admin/Operations/Email` (a real provider test that is not stale or expired). [ ] The Business Lead has seen the classification levels and reference prefix.

---

## 7. Onboarding wizard: every page and field

### 7.1 What it is
The organisation onboarding wizard describes **who owns what and who may see what**. It lives at `/Onboarding`. You work on a shared draft of one document, check it, preview its effect on synthetic data, and publish it as a numbered release that can be rolled back. Steps are not enforced in order. Saving a step replaces that step's whole list in the draft.

### 7.2 Who can use it (read this first)
- Needs the permission `onboarding.run`: held by **Tenant Administrator** and **Organisation Administrator** only.
- To save and publish it also needs configuration permissions that, in practice, only an **Organisation Administrator** holds fully. A Tenant Administrator can open the pages but is shown "You cannot change this organisation's configuration". A Business Configuration Administrator is refused (the blocked panel wrongly names them as able to use it).
- **Production publishing needs a second person**: the person who last saved the draft cannot publish it. Plan two Organisation Administrators.
- It is blocked while maintenance mode is on.
- People named in owners and viewers **must already exist** (invited or active). The wizard will not create or email them.

### 7.3 How the list steps work (applies to steps 2 to 7)
Type or paste CSV text into the box labelled **"List (one item per line)"**; press **"Save to draft"**. Rules: comma separated; double quotes allowed; a header row is optional (detected if the first cell equals the first column name); blank lines and lines starting with `#` are skipped; at most 5,000 lines of 1,000 characters; `;` separates several values in one cell. **One bad line rejects the whole save** and shows "Nothing was saved. Fix these lines and try again." with "Line N: …". There is no file upload (despite one help text saying so), no Excel import and no supplier import. Field-level meaning is only checked when you press **Validate** on the Review page.

Messages: "Too many lines: at most 5000 are accepted." / "The line is longer than 1000 characters." / "A quoted value is not closed." / "Expected <columns>." / "'x' is not a viewer kind (AllContracts, Category, BusinessUnit, Site, Restricted)." / "'x' is not yes or no." / "'x' is not a structure type (LegalEntity, Division, BusinessUnit, Site)."

### 7.4 Checklist page (`/Onboarding`)
Shows "Current release: N." or "Nothing has been published yet.", an organisation selector if there is more than one, and status badges per step (Not started, Entered, Optional, Chosen, Not validated, Valid, Needs attention). **Rating: A.**

### Step 1: Solution pack and classification (`/Onboarding/Step/pack`)
**Purpose:** choose the starting pack for the draft and optionally pull its categories and contract types in. **Rating:** B.

| Field | Required | Recommended | Rules | If wrong |
|---|---|---|---|---|
| Pack (select) | No | "None (start from the Blank configuration)" for Blank; the matching pack for the other two | Must be installed, trusted and not retired: "That solution pack is not available here…" | **Does not re-point the organisation** (G-08). It records the choice and prepares a terminology draft only; assigning a pack to an organisation is done in Administration, Packs |
| "Also add the pack's categories and contract types to this draft" | No | Ticked for JBS-style and Harborline-style; unticked for Blank | Merges; nothing existing is removed; duplicates by code are skipped | You get categories you then delete by re-saving the list |

Classification levels are **not** edited here (they were set in setup stage 10 and can be amended only at `/Admin/Setup` by the System Administrator; section 1.4).

### Step 2: Organisation structure (`/Onboarding/Step/structure`)
Columns `type,key,name,parentKey,timeZone,registrationNumber` (see workbook sheet 2). **Rating:** B.
- **Validation (on Review):** "A key is 1 to 40 letters, digits, '.', '_' or '-'." (but publish accepts only letters, digits, `-` and `_`, so avoid `.`), "Key 'x' appears more than once.", "A name is 1 to 200 characters.", "Division 'x' is not defined here or in the organisation.", "Time zone 'x' was not recognised."
- **Recommended:** Blank: one legal entity and the sites you need. JBS-style: legal entity, divisions, business units, sites. Harborline-style: Company (legal entity), Regions (divisions), Projects (sites) — the pack's wording calls divisions "Region" and sites "Project".
- **Consequences:** records are created or updated at publish (keys upper-cased). Nothing is ever deleted; items **this wizard created** that a later release drops are deactivated; items that existed before are never touched. There is no other screen for editing structure.

### Step 3: Categories (`/Onboarding/Step/categories`)
Columns `code,name,parentCode`. **Rating:** B.
- Rules: code 1 to 40 letters, digits, `.`, `_`, `-`; name 1 to 200; no duplicates; parent must exist; no cycles; "is nested more than 6 levels deep."
- Recommended: Blank: 5 to 20 broad categories; JBS-style: the pack's five; Harborline-style: the pack's eight trade packages.
- Consequences: categories drive ownership, reminders and visibility. Dropped categories are deactivated, not deleted; later edits and owner changes are on `/Admin/Categories`.

### Step 4: Contract types (`/Onboarding/Step/types`)
Columns `code,name`. **Rating:** A, but **Scaffolded**: recorded in the release and **used by nothing yet** (G-22). Keep it short.

### Step 5: Owners (`/Onboarding/Step/owners`)
Columns `categoryCode,email,primary`. **Rating:** B.
- Rules: category must exist; valid email; "No user with the address 'x' exists in this tenant. Invite them in user administration first."; not listed twice; `primary` blank/yes/y/true/1 or no/n/false/0. Warning `no_owners` if categories exist but none has an owner.
- **Consequence:** this creates a category-ownership relationship, **not** the Contract Owner role; the person also needs that role (via a group mapping or the Users page). Reminders go to category owners; with no owner the policy's fallback applies.

### Step 6: Viewers (`/Onboarding/Step/viewers`)
Columns `email,viewerKind,categories,businessUnits,sites,ceiling`. **Rating:** B.
- Kinds: AllContracts (cannot be narrowed), Category, BusinessUnit, Site, Restricted (must narrow by at least one dimension).
- `ceiling` is effectively **required** and must be a level of your scale; a blank gives "Classification ceiling '' is not one of: …". A Restricted viewer cannot have a ceiling above Confidential through the wizard (`ceiling_too_high`).
- Warnings: `top_ceiling` at Restricted or above; `restricted_single` when only one dimension is used.
- **Consequences:** creates a Viewer role assignment for that person. A too-wide viewer sees too much; the Review preview shows it on synthetic data. Legal Privileged access cannot be given here.

### Step 7: Identity group mappings (`/Onboarding/Step/mappings`)
Columns `providerKey,groupKey,role,viewerKind,categories,businessUnits,sites,ceiling`. **Rating:** C for the group names (identity administrator), then B.
- Roles accepted: Viewer, CategoryOwner, Legal, Auditor, ProcurementAdministrator, DivisionSiteAdministrator. Any other (administrators, Approver, Backup Approver): "Onboarding cannot map the X role. Assign administrator and approver roles in user administration…".
- Rules: provider key required (an unknown key is only a warning: "Identity provider 'x' is not configured yet…"); group required, up to 200 characters; a Viewer needs a kind; a kind on a non-Viewer is refused; a Division/Site Administrator must name a site.
- Warnings: `privileged_mapping` (everyone in that group becomes an administrator-type role; confirm who controls the group), `role_unused_capability` (Legal mapped while Legal review is off), `no_access`.
- **Consequences:** applied at **every sign-in**: group membership grants the role, leaving the group ends it. A wrong group name silently does nothing. Wizard-made mappings cannot be viewed or edited anywhere else.

### Step 8: Review, simulate and publish (`/Onboarding/Review`)
**Rating:** A for pressing the buttons; the content reading is B.
1. **Regional settings badge** (Decided / Not decided) with a link to `/Admin/Regional`. Information only.
2. **"1. Validate"** → button "Validate the draft". Errors block publishing; warnings are advice.
3. **"2. What publishing would do"**: a table of what will be created, already present, and dropped.
4. **"3. Who could see what (synthetic preview)"**: asks the real access rules about invented contracts across your proposed categories, units and sites. Read every row for each viewer. "No viewer scopes or group mappings are in the draft, so there is nothing to simulate."
5. **"4. Publish"**: optional **"Note for the release history"** (max 500; default "Published from the onboarding wizard"). The button **"Publish"** is disabled until the draft validated cleanly and has not changed since. Success: "Release N was published and is now in effect." A second person must publish in Production.
6. **"Discard this draft"**: no confirmation question.
7. **Release history and rollback:** "Go back to release" (choose), **"Reason (required)"** (up to 1,000), **"Roll back"**. Rolling back publishes the old content as a new release; it ends only what a later release introduced and this wizard created.

### 7.5 Onboarding items outside the wizard
- **People:** `/Admin/Users` ("Invite a person": Name, Email; "Send invitation"). **No email is sent** — someone must tell the person. In demonstration mode an invited person cannot sign in (only pre-made active users can).
- **Roles:** the same page, "Grant a role" (role, scope, ceiling, approver group for Approver roles, reason). This is the only place for administrator and approver roles. Approver group keys are free text and **permanent**.
- **Second organisation:** `/Admin/Organisations` and `/Admin/Organisations/New` (Tenant Administrator; Check, then Create; set-up checklist at `/Admin/Organisations/Setup`). G-02 is fixed.
- **Delegated administrators and role templates:** not in the wizard (not built).

### 7.6 Onboarding checkpoint
- [ ] Every list reviewed by its owner. [ ] Validate shows no errors. [ ] The preview was read for each viewer. [ ] A second person published. [ ] The first sign-in test per role was done (section 16).


---

## 8. Solution pack selection

### 8.1 What a pack decides, side by side

| | **Blank** (1.6.0) | **JBS-style: JBS Procurement Reference** (2.6.0) | **Harborline-style: Construction Subcontracts** (1.1.0) |
|---|---|---|---|
| For | Any business that wants neutral wording and few steps | A procurement contract register with Legal review and a Group Head approval | A subcontract register with value-tier approvals |
| Wording | Neutral: Contract, Counterparty, Category, Approval Authority | Procurement: Supplier, Category Owner, Procurement Administrator, Group Head of Procurement, ABN | Subcontract, Subcontractor, Trade Package, Package Manager, Commercial Administrator, Project Director, Change Order, Project (site), Region (division) |
| Capabilities (Legal review / Registration approval / Variations / Renewals / Signature requests) | off / on / on / off / off | on / on / on / on / on | off / on / on / off / on |
| Contract workflow | 5 states: Open, Active, Amending, Closed, Cancelled | 17 states (Draft through legal review, signature, approval, Effective, variation, renewal, suspension, termination, end, disposition, withdrawn) | 11 states (Draft, Internal review, Ready for signature, Sent for signature, Awaiting activation, Active, Change order open, Stop work, Completed, Terminated, Withdrawn) |
| Approval | One approver group, no value tiers | One approver group (Group Head of Procurement, or backup) | Value-tier **matrices** for activation and change orders (three value tiers plus exceptions for high risk and bonds) |
| Reminders and notifications | 4 events, lead time 90 days (30/60/90/120), critical-notice offsets 30/14/7/1; no fallback if a category has no owner | 28 events, same reminder values; fallback to the organisation's administrators | 16 events; lead time 30 days (14/30/60/90), offsets 14/7/3/1; fallback to administrators |
| Regional proposal | None (you decide) | Australia, en-AU, AUD (also USD/NZD), Australia/Brisbane, ABN (11 digits) | United States, en-US, USD (also CAD), America/Chicago, Federal Tax ID (##-#######, unique) |
| Classification | 5 levels, privileged: Legal Privileged | same | 4 levels, no privileged level |
| Starting data | none | 5 categories, 4 types, prefix JBSAUS | 8 trade packages, 5 types, 25 custom fields, 11 shared views, prefix HBL, branding |
| Signed? | **Unsigned sample (synthetic)** | **Unsigned sample (synthetic)** | **Unsigned sample (synthetic)** |

All three are **synthetic examples**, not advice. Harborline's value tiers are invented numbers. In Production none can be used until it is **signed with your key and the key is trusted** (or the supplier's signed packs are installed). Until then readiness shows a Degraded solution-pack check.

### 8.2 Pack Manager pages (`/Admin/Packs`)
Rating: **B** for choosing, assigning and retiring; **C** for building or signing a pack (a Pack Publisher's **Technical task** with `ecp-pack`; section 1.4).

| Page | Purpose | Fields and buttons | Rules and messages | If wrong |
|---|---|---|---|---|
| Catalogue (`/Admin/Packs`) | See every pack and its state | Filters "All packs", "Shipped with this installation", "Installed by administrators"; columns Pack, Version, Publisher, Source, Signature ("Signed, trusted" / "NOT SIGNED (demonstration or test only)" / "Not trusted"), Compatible, Status ("Ready to use" / "Retired" / "Cannot be used here" / "Unavailable"), Organisations using it | Empty: "No solution packs are available. Upload one, or continue with the Blank configuration." | Reading only |
| Upload (`/Admin/Packs/Upload`) | Step 1 of 3 | "Solution pack file (.ecpkg)" (required); buttons "Upload and check", "Cancel" | File up to 8 MB; "Choose a pack file (.ecpkg) to check."; the file waits 30 minutes in a holding area and only the uploader can continue | Wrong file type is refused |
| Review (`/Admin/Packs/Review`) | Steps 2 and 3: read the checks, then install | Checkbox "I have read the result and want to install this pack. I understand it changes no organisation until I assign it."; buttons "Install this pack", "Discard…" | Needs permission to install and a sign-in within 10 minutes. The same file again: "That exact pack was already installed. Nothing was changed." The same key and version with different content is refused ("Pack versions never change; publish the changes under a new version."). The report lists each issue with Level, Code, Where, What was found, How to fix it, and downloads as a spreadsheet | Installing makes the pack available; it changes no organisation |
| Details | About, contents, who uses it, other versions | Retire form: "Reason (kept in the history)" (max 500), "Retire this version" / "Make this version available again" | Shipped packs are read-only. A retired version cannot be given to more organisations; existing users keep it | Retiring does not remove it from anyone |
| Organisation packs (`/Admin/Packs/Assign`) | Give an organisation a pack | "Organisation", "Pack and version" (both required), "Preview what would change", then "Assign this pack and prepare drafts" | Blocked: retired, untrusted or incompatible packs, missing dependencies, capability conflicts. Existing terminology is kept; drafts are created for other kinds unless a draft is already open; **nothing is published**. Needs a sign-in within 10 minutes | Assigning prepares drafts only. A later pack version never changes published settings; you must assign again and review |
| Trusted publishers (`/Admin/Packs/TrustKeys`) | Trust the public key that signed your packs | "Key identifier" (2 to 80 characters), "Who it belongs to" (max 120), "Public key" (PEM), optional "Valid from/until (UTC)", "Register this key"; revoke with a reason | "That looks like a private key. Paste the PUBLIC key only…". Keys are revoked, never replaced; register a new key under a new id | Tenant Administrator only. A pasted private key is refused |
| Checks and History | Report-only pages | | | |

### 8.3 Capabilities (`/Admin/Capabilities`)
**Purpose:** switch optional process steps on or off per organisation. **Rating: A for the form; B for the decision.** A capability is a process switch, not a permission: it never grants or removes authority.

| Capability | If switched off |
|---|---|
| Legal review | No new reviews; review queues and status fields hidden |
| Registration approval | Nothing can be submitted for registration approval; the approvals queue is hidden; Variations cannot be approved |
| Variations (needs Registration approval) | No Variation can be raised |
| Renewals | No Renewal can be started |
| Signature requests | No signature request can be made; an executed copy is still attached as a document |

Pages: overview "Capabilities in force" (what is on, and whether it comes from your choice, the pack default, or the platform default); "Edit" (per capability a select "Choice for…": Inherit / On / Off, a "Reason (optional)", and a "Note for the history"); "Preview"; "History" (restore an earlier release with a required reason); "Audit". Buttons: "Save the draft", "Save and validate", "Publish" (Tenant and Organisation Administrators only), "Preview", "Discard the draft".
Rules: turning on Variations needs Registration approval on (`capability_requires`); you cannot switch off a step a published workflow uses (`workflow_uses_disabled_capability`) or that has work in flight (`capability_in_use`); notes are plain text up to 300 characters.
**Consequence of a wrong choice:** staff lose a step they need, or are asked for a review that is not wanted. The engine enforces it on the server; hiding a menu is not the only effect. Work already inside a step can still be finished or cancelled.
**Recommended:** Blank: use the pack's defaults. JBS-style: all on. Harborline-style: Legal review off, Renewals off, Signature requests on.

### 8.4 Choosing a pack: decision guide
1. Do you need Legal review as a formal step? Yes → JBS-style. No → Blank or Harborline-style.
2. Do you approve by value tiers with several people? Yes → Harborline-style (matrices).
3. Is your vocabulary closer to procurement or to subcontracts? Choose the nearer; terminology (section 9) fixes the rest.
4. None fits closely: start from Blank and change wording, capabilities and regional settings through forms. Workflow, approval matrix and notification policy are changed on forms at `/Admin/Forms/Edit` (sections 11 to 13); a Pack Publisher is needed only to build and sign a new pack.

---

## 9. Terminology configuration

**Rule.** A stable internal key (for example `supplier`) is never shown to ordinary users; the label is. Changing a label never changes permissions, workflows, records, role codes, web addresses, audit codes or database columns. Wording order of precedence: your organisation's published terminology → the assigned pack's wording → the platform's neutral wording.

### 9.1 Pages (`/Admin/Terminology`), per organisation
Rating: **A** for entering wording, **B** for deciding it. Permissions: Tenant, Organisation and Business Configuration Administrators draft, publish and roll back; Auditors read.

| Page | Fields and buttons | Rules and messages | If wrong |
|---|---|---|---|
| Overview | "Organisation", "Locale" (hint: a language such as `en`, or language and region such as `fr-CA`, max 20), "Show". Facts: Locale, Solution pack, "Published terminology" (Release N, or "None yet: the pack's and the platform's wording apply"), Draft. Table "Wording in force": stable key, label, plural, source ("Organisation override" / "Copied from pack" / "Pack default" / "Platform neutral"). Also approver group names and roles offered | | |
| Edit | Per term: checkbox "Use my own wording for {key}"; "Singular", "Plural" (max 80 each); optional "Short label" (30), "Description" (300), "Help text" (600). Approver group names: "Stable group key" (new rows only), "Name shown" (80), "Description (optional)" (300). "Roles offered when assigning": Inherit / Offered / Not offered (administrative roles cannot be hidden). "Note for the history" (500). Buttons: "Save the draft", "Save and validate", "Publish" (needs a sign-in within 10 minutes; in Production, and in Test with governance Enforced, you submit the draft and a second person approves and publishes it at `/Admin/Reviews`; G-01 is fixed), "Preview", "Discard the draft" | Blocking errors: singular and plural both required; length limits; no leading or trailing space; no line breaks; **no markup, script or template syntax** (`<`, `>`, `{{ }}`, `${ }`, `javascript:` …); key shaped like `contract.type`; no duplicates; not more than 600 terms. An unknown key is only a warning and has no effect | A wrong word is visible to everyone immediately after publish; permissions are not affected. Acronyms (ABN) are kept as typed |
| Preview | Tables of representative text now and after publishing; role and group names | Shows that only wording changes | Read it with a real contract owner |
| History | Releases with changes; "Release to publish again" and "Reason (recorded)" (required, max 1,000), "Publish it again" | "Give a reason for restoring the earlier release." | Rollback publishes the old wording as a new release |
| Audit | Who changed what | | |

### 9.2 The term list
86 keys exist in the product; 32 are **required** in any pack (contract, register, reference, type, document, owner, administrator, reviewer, approver and backup approver, counterparty, supplier, category, sub-category, legal review, approval, registration approval, external execution, variation, renewal, notification, organisation, legal entity, business unit, division, site, solution pack, migration project, migration batch, application name, portfolio, executed document, contract owner). A new key cannot be added by an administrator (G-18). Role labels come from fixed mappings: Contract Administrator ← role Procurement Administrator; Contract Owner ← Category Owner; Reviewer ← Legal; Approver ← Approver; Backup Approver ← Backup Approver.

### 9.3 Where wording appears
Pages, notification templates (use tokens like `[[contract|lower]]`; unknown tokens fail validation), migration summaries and exports. The page language stays English; the platform has no translated page sets (Future).

### 9.4 Recommended wording by profile
- **Blank:** keep neutral wording; change "Counterparty" if your people say "Vendor".
- **JBS-style:** the pack gives "Supplier", "Category Owner", "Procurement Administrator", "Group Head of Procurement", "Executed Copy", "Vendor Number", "ABN", "Estimated Agreement Value".
- **Harborline-style:** the pack gives "Subcontract", "Subcontractor", "Trade Package", "Work Section", "Package Manager", "Commercial Administrator", "Formal Reviewer", "Project Director", "Change Order", "Activation Approval", "Company/Region/Project".
Do the wording workshop with real contract owners; wording is the most visible part of acceptance.

---

## 10. Regional configuration

**Principle.** The platform assumes no country, language, currency, time zone, identifier format or address layout; each is a per-organisation setting. The pack proposes; you decide. Precedence: your published regional settings → the pinned pack version → stored organisation values → the installation fallback (only when no organisation exists) → machine-safe invariant output.

### 10.1 Page `/Admin/Regional`
Rating: **B** (the decisions are the work; the form is simple). Permissions: configuration draft/publish/rollback (Organisation and Business Configuration Administrators). Publish and rollback need a recent sign-in (G-19 is fixed).

| Field | Required | Recommended (Blank / JBS / Harborline) | Rules | If wrong |
|---|---|---|---|---|
| Country or Region | No | Decide / Australia / United States | From the closed ISO list | Only changes suggestions |
| Locale | No (but Production readiness reports until set) | Decide / en-AU / en-US | A language-and-region tag, max 35. Decides date order, number and currency layout, **not** the page language | Dates shown in the wrong order |
| "Supported locales" | No | | At most 40; must include the primary locale | |
| "Default currency" | No (but see above) | Decide / AUD / USD | Closed ISO 4217 list. "Every amount keeps its own currency… Changing it never converts or rewrites an amount." | Wrong default offered on new amounts |
| "Supported currencies" | No | JBS: AUD, USD, NZD; Harborline: USD, CAD | At most 60; must include the default | Foreign-currency approvals fail without a dated rate |
| Time Zone | No (but see above) | Decide / Australia/Brisbane / America/Chicago | A known place-based zone; an unavailable zone blocks publish. Daylight saving follows the zone | Wrong deadlines and reminder days |
| Time Display | No | Locale / 24-hour / 12-hour | | Cosmetic |
| "Phone region" | No | AU / US | Two letters; no phone format is checked | |
| Tax identifier types | No | Blank: none; JBS: ABN (digits, 11); Harborline: Federal Tax ID (mask ##-#######, unique) | Up to 12 types. Per type: key (lower-case with dots, for example `us.ein`), label (max 60), optional country, rule (`free`, `digits`, `alphanumeric`, `mask`), length (digits/alphanumeric) or mask (`#` digit, `A` letter, `*` either), checksum (`none`, `luhn`, `mod89-weighted`), required, "No two suppliers in the organisation share a value", classification. Only the **shape** of a value is checked; nothing is looked up in a registry | A wrong rule rejects good data or accepts bad data. Existing values are not rewritten when a rule changes |
| Address fields | No | Use all six for Blank; decide the rest | Six keys (line 1, line 2, locality, region, postal, country): "used" and "required"; a required field must be used | Address text is free text with no postal check |
| "Note for the history" | No | | Max 200 | |

Buttons: "Save draft", "Save and validate", "Validate and publish", "Discard the draft". The **Preview** shows sample date, date-and-time (January and July, to show daylight saving), number, amount and address fields. History: "Publish the content of release" + "Reason" → "Roll back". A draft that leaves locale, currency or zone undecided produces a warning, not an error, but Production readiness stays flagged.

**Consequences:** time zone drives which calendar day a reminder is "today"; currency matters to approval tiers (a foreign-currency submission with no dated rate is blocked, never converted silently); identifier rules gate data entry and migration (`MIG-E027`, `MIG-E028`).

---

## 11. Approval design

### 11.1 Two models
- **Single approver group** (Blank and JBS-style): the workflow names one approver group key; the first member to respond decides; the submitter cannot approve their own submission. No form is needed.
- **Value-tier matrix** (Harborline-style): a document of rows that choose the approvers by value, category, risk and other conditions.

### 11.2 Design steps (Business Lead + Business Administrator)
1. List what needs approval (activation, change order, registration).
2. For each, write the value tiers (from–to, half-open: lower bound included, upper excluded) in **one currency**; list exchange rates only if other currencies will be submitted (a dated rate is required; nothing is converted implicitly).
3. For each tier, name the **approver groups** (and a different backup group), whether steps run one after another or together, and whether one or all must respond.
4. List exceptions (for example high risk adds a Safety and Risk step; a bond adds a bond check).
5. Make sure every value is covered (no gaps) and no two rows can apply at the same priority (no ties).
6. Create the approver groups at `/Admin/ApproverGroups` (key, description, organisation scope, primary and backup members with dates, preview of who can approve, in-use and last-member protections; G-04 is fixed). Group keys are permanent. Approver and Backup Approver roles are still granted on `/Admin/Users/Details/…`.

### 11.3 Where it is entered
- **Single group:** part of the workflow document (`settings.approval.approverGroupKey`).
- **Matrix:** the form editor at `/Admin/Forms/Edit` (approval matrix kind), with closed vocabularies, add / remove / move buttons, validation on the field, preview and impact analysis, then the ordinary draft, second-person publication, history and rollback (G-03 is fixed). Raw JSON at `/Admin/Configuration?kind=ApprovalMatrix` is a read-only diagnostic in Production. Rating: **B** (the design is the work).
- The matrix pages `/Admin/Approvals` (read-only plain-language view), `/Admin/Approvals/Simulate`, `/Admin/Approvals/Delegations`, `/Admin/Approvals/History` are forms.

### 11.4 Validation you will meet (matrix)
Blocking: a matrix needs at least one Base row; codes are lower-case letters, digits and hyphens (up to 50); `range_gap` (values between two ranges match nothing); `range_overlap`; `row_tie`; `currency` must be a three-letter code; rate must be above zero and not overlap; `timeout` 0 to 365; `backup_group` cannot equal the approver group; `classification_unknown`; `fact_unavailable` (a condition on something the platform does not supply, or a custom field not marked usable in approvals); `matrix_in_use` (a workflow still names it).
Warnings: `range_lower` / `range_upper` (values below the first or above the last range are **blocked at run time**), `reference_unknown`, `group_empty` ("Nobody currently holds the approver group…").
Run-time blocks when someone submits (each audited): `matrix_not_found`, `matrix_not_effective`, `currency_missing`, `no_rate`, `no_match`, `priority_tie`.
Time limits and escalation per step (`timeoutDays`, `escalateTo`) are **recorded only; nothing happens** (Scaffolded).

### 11.5 The simulator (`/Admin/Approvals/Simulate`) — rating **A**
Fields: "Matrix code" (required), "Version to try" (the release in force / a numbered earlier release / my open draft), "Release number", "Date to test"; the submission: "Value", "Currency", Category, Business unit, Site, Legal entity, "Classification", "Contract type", "Risk level", "Supplier type", "Custom fields (one per line, code=value)", "Submitted by". Button "Run the simulation". Output: Resolved/Blocked, rows that applied, resolved steps with the people who can respond, why each person is in or out, and a trace. Use it with real examples from the workshop at each tier boundary (below, at, above) and for each exception.

### 11.6 Delegations — rating **A**
`/Admin/Approvals/Delegations`: "My approver group", "Delegate to" (a member of the same group or its backup), "From", "Until" (at most 90 days), "Reason" (max 500), "Delegate". No circles or duplicates; not transitive. Ending: a "Why" and "End it".

### 11.7 Consequences
A gap or tie stops a real submission, not just a test. A wrong approver key means nobody can approve. A matrix change affects the **next** submission that resolves it (matrix text is not pinned to a contract's workflow release; the resulting plan is fixed at submission). In Production (and in Test with governance Enforced) a matrix needs a second person to publish, through `/Admin/Reviews` (G-01 is fixed).

### 11.8 Recommended by profile
- **Blank:** one approver group (for example Approvers) with two or more members.
- **JBS-style:** one approver group (Group Head of Procurement and a backup).
- **Harborline-style:** start from the shipped matrices as a model only; replace the invented limits (250,000 / 2,000,000 activation; 1,000,000 / 5,000,000 change orders) with your delegation-of-authority limits, then simulate every boundary.

---

## 12. Workflow design

### 12.1 What you can and cannot do alone
The contract workflow (states, allowed moves, locks, what must exist before each move) and the Variation workflow are edited on the form editor at `/Admin/Forms/Edit` (workflow kinds), with closed vocabularies, validation on the field, preview and impact analysis, then the ordinary draft, second-person publication, history and rollback (G-03 is fixed). Raw JSON at `/Admin/Configuration` is a read-only diagnostic in Production. There is still no diagram. Rating: **B** (the design is the work; a Business Administrator can do it with the design sheet).

### 12.2 Design steps
1. Draw the states on paper from the workshop (sheet 12). Mark which are **locked** (no edits while Effective, in Variation, Suspended, Terminated, Ended and so on) and what documents may be added in each (Open / LegalOnly / Closed).
2. Draw the moves: from, to, who may do it (a permission from the catalogue), what must be true (the 23 conditions such as "legal review required", "final document exists", "executed copy exists", "comment required", "authorised approver", "submitter is not the approver", "classification clearance").
3. Decide which moves need Legal review and whether the approval is a single group or a matrix.
4. Enter the design on the workflow form editor from the design sheet; validate, preview, test it on a non-production installation with real roles, submit, and have a second person publish.
5. Agree what happens to contracts already in flight: they stay on the workflow release they **started under**; moving them to a newer release is not built (Future).

### 12.3 What the validator rejects (plain words)
Only schema version 1; exactly one initial state and it cannot be locked; unique state and move codes; every move names defined states and a known permission, guard and role; a move cannot unlock a locked record ("A transition cannot unlock a record."); the approving move must land in a locked state and carry the authorised-approver and submitter-is-not-approver conditions; submitting and approving require an executed copy; rejection and return-for-rework need a comment; no dead ends and no exits from a terminal state; document types for the executed copy and final documents must be named; an approver group key is required; a named matrix must be published first; emergency-unlock settings are bounded (default at least 5 and not above the maximum, maximum 5 to 1,440 minutes; second-person confirmation must stay off — not supported). Warnings: states that cannot be reached; missing optional move roles.

### 12.4 Locks and emergency unlock
Locked states cannot be edited. An emergency unlock needs a special permission (Procurement Administrator, scoped), a sign-in within 10 minutes, a reason of at least 20 characters and a duration within the limit (60 minutes by default, 240 at most). Support access can never publish configuration.

### 12.5 Recommended by profile
- **Blank:** use the five-state workflow as shipped. 
- **JBS-style:** use the shipped 17-state workflow unchanged for the first release; change only after UAT.
- **Harborline-style:** use the shipped 11-state workflow; confirm "Internal review", "Stop work" and "Change order open" match practice.

---

## 13. Notification design

### 13.1 What you can do
- **Wording and who is told** are in the **notification policy**, a document (edited on the form editor at `/Admin/Forms/Edit`, notification policy kind; G-03 is fixed): rating **B**.
- **Reminder lead time per contract** is a form on the contract page ("Remind this many days ahead", then "Save the lead time"): rating **A** (needs `reminders.configure`).
- **Delivery monitoring** (`/Admin/Notifications`): summary (provider, waiting, failed, dead-lettered, sent in 24 hours), deliveries filter, "Retry", background jobs table, "Run the background work now": rating **A**.
- **Email provider, server, secrets, link address and schedule** are server settings (section 5.2): rating **C**. The admin page only shows them.
- **Users' own pages:** `/Notifications` (inbox: unread, waiting to be acknowledged, mark all as read, acknowledge) and `/Dashboards` (ten fixed cards).

### 13.2 Design steps
1. List the events you want (28 exist, in groups: legal, approval, registration, variation, renewal, disposition, unlock, ownership, reminders).
2. For each, choose recipients from nine rules: category owners, submitter, legal pool, assigned legal, approver group, counterpart, affected user, actor, organisation administrators (note: this last one is the holders of the Procurement Administrator role). People who cannot read the record are dropped automatically.
3. Choose channels (in-app, email). Mandatory messages must include in-app.
4. Write each template: subject up to 300 characters, body up to 3,000, using only the 14 placeholders (contract reference, title, actor, link, due date, days remaining, window days, step name, ISO due date, today's date, date-time, UTC time, value, currency) and terminology tokens. No expressions, conditions, loops or markup.
5. Reminders: allowed lead times, default, review-date and end-date reminders, critical-notice offsets, evergreen handling, and the **fallback recipients** when a category has no owner (Blank: none, so reminders wait; JBS-style: organisation administrators).
6. Privacy: the level from which the title is withheld, and the level from which everything is withheld.
7. Delivery: attempts (default 5) and back-off (1, 5, 30, 120, 720 minutes).

### 13.3 What the validator rejects
Allowed lead times 1 to 3,650 and the default among them; offsets 0 to 3,650; unknown or repeated events; an event with no recipients or channels; a mandatory message without in-app; a missing template or subject/body; an unknown placeholder or bad terminology token; "A template holds plain text and {{placeholders}} only…"; control characters in a subject; withhold-everything not at or above withhold-title; attempts 1 to 20. Warning: no rule for a reminder event, which then produces nothing.

### 13.4 Behaviour to know
- The policy is **not pinned**: the latest release applies when a message is created.
- Reminders fire once at the most urgent window reached; "today" is the organisation's calendar day in its time zone; past dates produce nothing; evergreen contracts get no end-date reminder.
- Escalation of an unacknowledged reminder is **recorded only** (Future).
- Messages are only sent to people with active accounts.
- While maintenance mode is on, background work pauses and queued mail is sent after release.

### 13.5 Test before go-live
Run the email readiness test at `/Admin/Operations/Email` (a **Technical task** for the System Administrator); trigger one event of each kind with a test contract on a non-production installation; confirm the recipient list, wording and link; confirm a dead-lettered delivery shows on `/Admin/Notifications` and "Retry" works after fixing the provider.


---

## 14. Migration preparation

Moving contracts from an old register (a spreadsheet, a database export) into the platform uses the **Data migration** pages (menu: Data migration, `/Migration`). Everything is done with files and forms. The platform loads a contracts file (CSV or Excel), an optional attachment list (the "manifest") and an optional ZIP of attachment files. **Nothing is guessed**: a value it cannot map is reported, not invented.

**Who:** the Procurement Administrator role holds every migration permission (stage, commit, legacy load, accept, roll back). The **data owner** (a named business person, not necessarily a user) signs the acceptance. The System Administrator is needed only for the backup around cutover (sections 17 and 19).

### 14.1 What can and cannot be migrated today

| Item | Status |
|---|---|
| Contract core fields (title, reference, supplier or counterpart, dates, value and currency, category, type, risk level, function code, notice period, critical notice) | Supported |
| Additional (custom) fields defined for the organisation | Supported, except calculated, person and file-reference fields |
| Suppliers or counterparts, categories | Created or linked from the file (categories only if the profile option "create missing categories" is ticked) |
| Attachments | Supported, matched by fixed rules (section 14.5) |
| Legacy contracts that are already Effective or later (locked) | Supported only as a **legacy load** with a policy and a second person's confirmation |
| Legal entity, business unit and site of a contract | **Not supported as mapping targets**; set by hand afterwards (Appendix D, G-23) |
| Contract owners, users | **Not supported** (Appendix D, G-23) |
| Documents' own versions history, comments, approvals history | Not supported (Future). Keep the old system read-only for history |
| Fields the profile points at `staging.` targets | Retained but **not loaded** |

### 14.2 Steps for the Project Manager and Business Lead
1. Name the **source system**, its version, the **data owner** and the **extraction date**.
2. Decide the **scope**: which contracts (all, active only, last N years). Record what is deliberately left behind.
3. Decide the **cutover freeze**: dates between which people may not create contracts by hand in the target organisation (the migration itself is not blocked).
4. Decide for each source status which **platform state** it becomes. This is the single most important mapping (section 14.4).
5. Decide the **duplicate rule** (section 14.6) and the **reference rule** (keep the old reference or generate a new one from the numbering rule; the old reference is always kept separately).
6. Plan at least **two rehearsals** (dry run, then mock load that writes and can be rolled back) before the final load.

### 14.3 Preparing the source file
- One row per contract. A stable, unique **source record id** per row is essential: it is the identity that makes a re-run safe.
- CSV in UTF-8 is best. UTF-16 is detected. Windows-1252 must be chosen explicitly when uploading. Excel (`.xlsx`) works; `.xlsm` (macros) is refused; formulas are read as their last saved values; hidden rows are read but flagged.
- Limits (defaults): 250,000 rows, 500 columns, 32,768 characters in a cell, 500 MB per file. A value longer than its field allows is **rejected, never shortened** (MIG-E009).
- Dates: decide one date format per column and write it down; the mapping needs the format (for example day, month, four-digit year).
- Clean obvious problems first: duplicate rows, blank required values, mixed date formats, free-text statuses that differ only by spelling.
- Use only **synthetic or approved data** in rehearsals on non-production installations. Never put live confidential data into a test environment.

### 14.4 Preparing the mapping profile (a Business Administrator task, rating B)
A **mapping profile** is a versioned recipe: for each source column, which platform field it fills and how it is cleaned. Menu: Data migration, Mapping profiles (`/Migration/Profiles`).

| Page / field | Purpose | Required | Recommended | Validation | If wrong |
|---|---|---|---|---|---|
| Create a profile: Organisation | Which organisation the profile is for | Yes | The organisation you are loading | Only organisations you may stage for are offered | A profile for the wrong organisation cannot be used for the project |
| Name | Label | Yes | "<Source system> to <platform> v1" | Text | None, but unclear names cause mistakes |
| Source system, version | Records where the data came from | Source system yes | Same wording as the project | Text | Provenance reads wrongly |
| Load the field mappings (CSV file) | The recipe: one line per source column | Yes | Start from the sample in the supplier's migration samples folder and change it | Columns must be exactly `SourceColumn, TargetField, Transformation, DefaultValue, Required, LookupMapKey, Validation, Notes`; every target field must exist; every transformation must be on the closed list | Publishing is blocked until errors clear |
| Load the lookups (CSV file) | Translates old values into platform codes (status, category, type, risk, custom-field choices) | Yes when any mapping uses a lookup | One entry per distinct old value | Columns `LookupMapKey, SourceValue, TargetCode`; target code must exist | A value with no entry is a row error MIG-E004 when the lookup is required, or passed through when soft |
| Options: Create missing categories | Lets the load add categories it does not find | No | Off in the final load; on only in early rehearsals | Tick box | On: typos become new categories |
| Options: Open-ended types | Contract types for which a blank end date is normal (evergreen) | No | List the evergreen types | Text | Blank end dates become warnings instead of expected |
| Options: Naming rule | A pattern that finds the contract id inside attachment file names or folders | No | Leave empty unless file names carry the id | Must name the group `sourceRecordId` or `agreementReference`; times out after 250 ms | Wrong pattern: files stay unmatched |
| Publish this version (with note) | Freezes the version | Yes before use | Publish only when the checks list shows no errors | Published versions never change; a change is a new version | A wrong published version is simply replaced by a new one, but batches pinned to it keep it |

**Transformations available (closed list):** Trim, NormaliseWhitespace, Upper, Lower, TitleCase, ParseDate (explicit format), ParseDateTime (explicit format and time zone), ParseDecimal, ParseInteger, MapBoolean, Concat, Split, FixedDefault, LookupMap. Chain with `+`. There are no formulas, expressions or scripts and there never will be (decision of the owner). **Consequence for a Business Administrator:** a source clean-up that needs logic (for example "if column A is blank use column B, else...") cannot be expressed; do it in the spreadsheet before upload.

**Contract status mapping:** the status lookup must map every old status to a state that exists in the organisation's workflow, and (for a legacy load) that the project's legacy policy allows. Without a lookup, the organisation's initial state is used for every record (an ordinary load never changes workflow state). If the organisation has no workflow at all the dry run stops (MIG-E026).

**Rating:** B. A Business Administrator who has never edited a mapping CSV will need a coached first attempt (Appendix D, G-24: no mapping designer, no column auto-detect, no sample-row preview before the dry run).

### 14.5 Preparing attachments
- Give every file a row in the **manifest** (columns `SourceRecordId, AgreementReference, RelativePath, FileName, DocumentType, DocumentDate, VersionLabel, IsExecutedCopy, ParentDocumentSourceId, Sha256, Notes`). Files with no manifest row are matched by folder or file name only when the profile has a naming rule; otherwise they wait in the unmatched queue.
- Matching order is fixed: manifest SourceRecordId, manifest AgreementReference, naming rule, exact parent-folder name. If more than one contract fits, the file is **ambiguous** and a person decides; the platform never breaks a tie.
- Allowed file types are a fixed list; the file content must match its type; traversal paths, encrypted or corrupt archives and decompression bombs are refused.
- **Malware scanning:** there is **no real scanner** behind the platform today (a labelled mock exists for Demo, and for Test only when chosen on purpose and, for masked data, accepted on the record; section 1.4). In Production the setting that requires a scan is forced on, so **files that cannot be scanned are held** and not imported. Final migration of attachments in Production therefore needs a decision (Appendix C, D-17) and a scanner adapter from the supplier (Appendix D, G-25).

### 14.6 Choosing the duplicate rules
| Rule | When to use | Consequence |
|---|---|---|
| Skip existing | Normal for re-running the same file | Nothing changes for records already loaded |
| Update mapped fields | A corrected file for records already loaded | Only mapped fields change; before-images are kept; locked records are refused (MIG-E012) unless the legacy policy allows overwriting and a reason is recorded |
| Reject | You expect no overlap | Every overlapping row is an error |
| Create with a new reference | Old references collide with ones already in use | The numbering rule allocates new references; old kept separately |
| Manual review | Small, risky data sets | Every duplicate waits for a person; the batch cannot commit until decided |

Document duplicates (same file content already in the platform): add a version, link the existing content, reject, keep separate with a warning, or hold for a person. Recommended: link the existing content for a first load; add a version only if the old file is a genuinely newer version.

### 14.7 Legacy-load policy (only for contracts that are already effective)
| Field | Purpose | Recommended | Consequence of a wrong value |
|---|---|---|---|
| Target states a legacy load may create | The only states a legacy record may take | The smallest list (for example Effective, Expired) | A record with another state is rejected (MIG-E008) |
| Expires (UTC date) | Closes the policy automatically | A few days after the planned final load | An open-ended policy is a standing way to create locked records |
| Keep the source registered date | Imports the old registration date only if the source has one | Tick only if the old date is authoritative | Not ticked: the platform's own time is used and a warning is recorded |
| Allow overwriting locked records (with reason) | Lets an update change locked records already in the platform | **Leave off** | On: locked contracts can be changed by a load; this is an audited exception, not a routine |

### 14.8 Checkpoint before section 15
Signed-off source extract with its file hash; published mapping profile version; manifest and ZIP prepared; data owner named; freeze window decided; duplicate and reference rules decided; legacy policy decided; two rehearsal dates in the plan; a scanning decision for attachments.

---

## 15. Migration execution

Run each cycle in the same order. A **dry run** writes nothing to the platform. A **mock load** (rehearsal) writes and can be rolled back. A **final load** is the cutover.

### 15.1 Create the migration project (page: Data migration, `/Migration`)
| Field | Purpose | Required | Recommended | Validation | If wrong |
|---|---|---|---|---|---|
| Organisation | Which organisation receives the records | Yes | The target organisation | Only organisations you may stage for | Records land in the wrong organisation. Stop and roll back |
| Project name | Label | Yes | "<Source system> cutover <year>" | Text | None |
| Source system | The old system's name | Yes | Plain name | Text | Provenance reads wrongly; also part of the identity of every record (changing it later makes the same rows look new) |
| Source system version | Optional | No | If known | Text | None |
| Data owner | The person accountable for the data | Yes | A named business person | Text | Acceptance has no clear owner |
| Date extracted (UTC) | When the extract was taken | Yes | The real date | Date | Misleads the freeze and reconciliation story |

On the project page also set the **reference policy** (keep the source reference where free, or generate new), the **legacy-load policy** (section 14.7) and the **cutover freeze** (start and end in **UTC**, and a tick to block contract creation by people during the window). Remember that freeze times are entered in UTC: convert from local time and check the conversion twice (Appendix D, G-26).

### 15.2 Create a batch
| Field | Purpose | Recommended | Consequence |
|---|---|---|---|
| Batch name | Label | "Rehearsal 1", "Rehearsal 2", "Final" | None |
| Kind | Dry run (validate only), Mock load (rehearsal, writes, reversible), Final load | Dry runs until clean, then mock, then final | A final load is the cutover; it is only reversible until accepted |
| Mapping profile version | Pins the recipe | A published version | Pinned: later changes do not alter the batch |
| Duplicate rule / document option | Section 14.6 | As decided | See 14.6 |
| Legacy load tick | Only for effective-or-later records | Tick only with a policy | Needs a second person's confirmation |

### 15.3 The seven steps on a batch (page `/Migration/Batches/<id>`)
1. **Source files.** Upload the contracts file (choose delimiter, encoding, sheet if asked), then the manifest, then the ZIP. Uploading the same file twice changes nothing; a different file replaces the earlier one and withdraws any confirmation.
2. **Mapping profile and duplicate options.** Pick the profile version and the rules.
3. **Dry run.** Press "Run the dry run". Read the summary, the "What each mapping does" table and the sample of original versus transformed values. Fix problems at their source (the file or the profile), then repeat. Typical codes: MIG-E001 missing value, MIG-E002 date format, MIG-E004 unmapped lookup, MIG-E006 duplicate reference, MIG-E008 status not allowed. The list of all codes is in the migration documentation.
4. **Review.** Handle rows sent to manual review (Rows page) and files that are unmatched, ambiguous, rejected or held (Files page: pair by hand, discard with a reason, release). Each decision changes the plan, so run the dry run again.
5. **Confirm and commit.** For a legacy load a **second person** (with the legacy-load permission) presses "Confirm this legacy load"; the person who commits cannot confirm. Then "Commit this batch". It runs in chunks (500) with checkpoints; if it stops, use "Resume the commit". **Commit is refused while maintenance mode is on** (section 17.3).
6. **Reconcile.** "Save the reconciliation". Source rows must equal accepted plus rejected plus skipped; accepted equals created plus updated; manifest rows and archive files each account for every item. If not, either fix and re-run or **approve exceptions** with a note.
7. **Business acceptance.** The data owner records a sample check (a note saying which records were compared with the source) and acknowledges outstanding items. **Acceptance is final for rollback**; after it only "compensating archival" is possible, and that archives, never deletes.

**Rollback (before acceptance):** read "What a rollback would do" first. Untouched records the batch created are removed; contracts or documents that have moved on are archived with the reason; shared suppliers and categories are kept.

**Evidence:** Provenance page (what each batch created or changed, with before-images), Reconciliation export, error report (Excel and CSV, safe against spreadsheet formulas). Keep them with the runbook (the template is in the migration runbook template).

### 15.4 Can a Business Administrator do it unaided?
| Step | Rating | Why |
|---|---|---|
| Create project, batch; upload files; run dry runs | A | Forms, with hints |
| Author the first mapping profile | B | CSV with a closed catalogue; coaching on the first attempt (G-24) |
| Resolve rows and files | A | Forms |
| Legacy-load policy and confirmation | B | Needs a second authorised person (Appendix C, D-11) |
| Attachments in Production | D | No real malware scanner adapter exists (G-25); Production is Blocked until one is connected |
| Owners, business units, sites, users | D | Not migration targets (G-23) |
| Reconcile and accept | A | Needs the data owner |

---

## 16. User acceptance testing (UAT)

**Purpose:** prove the configured installation matches how the business works before anyone depends on it. The Business Lead signs; the platform never declares readiness.

### 16.1 Prepare
- A non-production installation (or Demo mode) that was set up by the **same steps** as production (sections 5 to 13), with synthetic or approved data. Where possible, a restored copy of a rehearsal backup (section 19).
- A named tester per role: contract owner, legal reviewer, approver (each approver group), registrar, Procurement Administrator, System Administrator, an auditor, and at least one person who should see **nothing** (to prove access limits).
- The approval-design workbook, workflow list, notification list, terminology list and regional settings as the **expected results**.

### 16.2 Test packs (run every one; record who, when, result, evidence)
| Pack | Scenarios |
|---|---|
| Sign-in and access | Each role signs in; sees only its menu items; a person without permission sees "forbidden" or "not found"; classification limits hide restricted contracts; a user from a different organisation sees nothing |
| Contract lifecycle | Create, edit, submit, every state change the workflow allows, every state change it should refuse; locked records cannot be edited; emergency unlock needs reason and recent sign-in |
| Approvals | For each matrix row: a contract that meets the conditions routes to the right approvers in the right order; the **approval simulator** shows the same plan; delegations work; a submitter cannot approve their own |
| Variations and renewals | Create a variation on an effective contract; renewals; the reminders |
| Legal review (if on) | Request, assign, outcome, execution; if off: the step is absent everywhere |
| Notifications | One event of each kind; recipient, wording, link, language and date format; a failed delivery appears in Notifications; mandatory messages arrive in-app |
| Reminders | A contract whose end date is 30, 7 and 1 day away; a contract with no category owner (does the fallback apply?) |
| Terminology | Every page, e-mail, export and help message uses your words, not the pack's (spot check ten screens and two exports) |
| Regional | Dates, numbers, currency, time zone and tax identifier on screens, exports and e-mails for each organisation |
| Search, views and exports | Saved views; CSV and Excel export; formula-safe cells |
| Migration | Mock load of representative data; reconcile; sample check against the source by the data owner; rollback tested once |
| Audit | Every key action is in the audit page with the right actor; configuration changes show before and after |
| Operations | The System Administrator creates a backup, verifies it, restores it to a new database, validates it (section 19); maintenance mode on and off; the operations dashboard (section 18) |

### 16.3 Defect handling
Class each defect: **Configuration** (a Business Administrator fixes it by publishing a new release), **Data** (fix the source and re-run), **Product** (raise with the supplier). Agree a rule for go/no-go: no open defects of the top two severities, every approval and access scenario passed.

### 16.4 Exit
A signed UAT report, the evidence folder, the list of accepted deviations with owners, and the date of the final configuration release (note its **release numbers** and hashes). Freeze configuration changes until cutover unless the Business Lead approves a change.

### 16.5 Unaided?
Executing scenarios: **A**. Designing them: **B**. Fixing a defect found: **A** if it is wording, a choice, a threshold or a recipient; **B** if it is a workflow, approval or notification change on the form editors (G-03 is fixed); **C** if it needs a new or re-signed pack (Pack Publisher).

---

## 17. Cutover

**Purpose:** move from the old register to the platform on a planned date with a way back.

### 17.1 Two weeks before
- UAT signed (section 16). Configuration frozen. Final mapping profile version published and used in a successful rehearsal.
- Production installation in **Production mode** with: single sign-on working for every group, mail provider working, backup folder and admin connection configured (section 5), signed pack (or an approved decision about the pack, D-04), a verified **restore rehearsal** (section 19) and an **upgrade rehearsal** if a newer build is expected (section 20).
- Organisation onboarding **published by the second person** (a second person publishes through `/Admin/Reviews` or the wizard's Publish step; G-01 is fixed).
- Communication sent to users: freeze window, training, who to call.
- A written back-out decision tree and the "point of no return" (usually: business acceptance of the final batch).

### 17.2 Cutover runbook (typical order)
| # | Who | Action |
|---|---|---|
| 1 | Business Lead | Confirm go (all gates in 17.1 are green) |
| 2 | Source owner | Stop changes in the old system; take the final extract; record its hash |
| 3 | Business Administrator | Cutover freeze starts (already scheduled, times in UTC) |
| 4 | System Administrator | **Create a verified backup** (Administration, Operations, Backups: reason, optionally "hold maintenance" while it runs); confirm "Passed". This is the way back |
| 5 | System Administrator | Make sure maintenance mode is **off** (migration commit is refused while it is on) |
| 6 | Business Administrator | Final batch: upload, dry run, review, (legacy: second person confirms), commit |
| 7 | Business Administrator + Data owner | Reconcile; sample check against the source; accept (or roll back) |
| 8 | System Administrator | Run **validation** on the running installation (Operations, Post-restore validation); read the operations dashboard (section 18) |
| 9 | System Administrator | Create a second verified backup (the post-load recovery point) |
| 10 | Business Lead | Decide: open to users. Freeze ends; users sign in |

### 17.3 Things people get wrong
- Maintenance mode blocks the migration commit, configuration publication and configuration rollback. Do not leave it on by accident; do not turn it off in a failed restore state without reading why (it requires an explicit acknowledgement).
- Do not "roll back" after business acceptance; use compensating archival, which keeps records.
- If the platform is rolled back by **restore**, everything done since the backup is discarded; tell users the time window.
- The old system stays read-only for the agreed retention period. The platform does not import the old system's history of comments and approvals.

### 17.4 Unaided?
Mostly **C** for the System Administrator steps (4, 5, 8, 9) and **A/B** for the business steps. The Project Manager runs the sequence; the Business Administrator cannot do the whole cutover alone because the backup and validation permissions belong to the System Administrator role by design.

---

## 18. Hypercare

**Purpose:** the first weeks after go-live, when problems appear that tests did not find.

### 18.1 Duration and roster
Normally two to four weeks. Name a daily owner (Business Administrator), a technical owner (System Administrator) and a decision-maker (Business Lead). Agree response times for the support route.

### 18.2 Daily checks for the System Administrator
Open **Administration, Operations, Dashboard** (the operations dashboard). It shows each area as **Verified, Warning, Failed or Not Verified** (the worst one wins; an empty area is Not Verified, never green) and **never shows business content**. Look at:
| Area | What you want | If not |
|---|---|---|
| Health and readiness | Verified | Open the linked check; fix the setting; a Failed readiness check in Production blocks nothing by itself but must be explained |
| Background jobs | Recent successful runs, none overdue | Run the background work from Notifications; read the failure register |
| Outbox and e-mail | Nothing waiting for hours; no dead-lettered messages | Fix the provider, then "Retry" on the delivery |
| Backups | A recent verified backup within your agreed frequency | Create one now. There is no built-in schedule: the organisation runs the backup command from its own scheduler (G-27) |
| Certificates and keys | No near expiry; key ring healthy | Plan renewal; a lost key ring means signed-in sessions and protected values cannot be read (G-11) |
| Migrations / upgrades | Nothing pending unless planned | Do not run anything unplanned |
| Maintenance mode | Off | Find out who turned it on and why |

Not Verified is not a pass: some checks are not built or cannot run on this installation; the dashboard says which.

### 18.3 Daily checks for the Business Administrator
- **Notifications** page: failures; **Users** page: new users and role changes; **Audit** page: configuration changes in the last day; the number of questions from users about wording (terminology gaps) and about who approves what (approval gaps).
- Keep a **change log**: every configuration change in hypercare is a published release with a note; the release number is the evidence.

### 18.4 Changing configuration during hypercare
The safe loop is always draft, validate, preview or simulate, publish (second person in Production), observe. Roll back is available for every kind (a rollback publishes an earlier release as a new release; it needs a recent sign-in and a reason, and in Production it is submitted for a second person like any other publication: G-01 is fixed). Do not batch up changes: one change per release makes rollback meaningful.

### 18.5 Exit criteria
No open top-severity issues; two consecutive weeks of green dashboards; a verified backup and a restored-and-validated copy within the agreed period; a lessons-learned list; handover to business-as-usual support; the Business Lead signs.

### 18.6 Unaided?
Business monitoring: **A**. Dashboard and failures: **C** (the dashboard needs `operations.read`, held by the System Administrator). A Business Administrator cannot see technical health.

---

## 19. Backup and restore rehearsal

**Purpose:** prove, before you need it, that a complete backup can be created, checked and restored into a **new, separate database**, and that the restored installation is correct. A backup nobody has restored is only a hope.

**Who:** System Administrator (permissions `operations.backup` and `operations.restore`; recent sign-in; a reason). The Project Manager schedules and records. The Business Lead decides the **targets** (how much data loss, how long a rebuild may take, how long backups are kept, where the copies live, who may read them). The platform does not decide these (they are the organisation's, Appendix C D-14 to D-16).

### 19.1 Preconditions
- SQL Server (the only supported backup engine; SQLite is refused). A backup folder that the database server can write and the application can read, on separate storage from the database. An admin connection reference with permission to back up and restore. A restore target folder. (Section 5.2 settings; Appendix D, G-16.)
- Backups hold **everything of the installation**: database, documents, packs. Protect the folder like the database. Encryption of backups is the organisation's choice; the platform records whether the file is encrypted.

### 19.2 Steps
| # | Page | Action | Pass looks like |
|---|---|---|---|
| 1 | Operations, Backups | Create a backup. Fields: **Reason** (free text, secrets are masked); tick **hold maintenance** if you need a quiet moment (stops all changes while it runs) | Outcome Completed or Completed with warnings; warnings are read and understood (no encryption, damaged document, broken audit chain) |
| 2 | Backups | "Verify again" on that backup | Passed (or Passed with warnings, explained). Record the **manifest hash** shown |
| 3 | Operations, Restore | Step 1: choose the backup and a **new** database name (never the live one) | Preview lists no blockers |
| 4 | Restore | Step 2: read the sanitised preview and warnings | Warnings accepted by the owner |
| 5 | Restore | Step 3: Reason; type the exact phrase `RESTORE <backup id>`; optionally paste the expected manifest hash; confirm sign-in | The restore runs through all steps |
| 6 | Restore result / Validation | Read the post-restore validation outcome (checks VAL-001 and following: data counts, audit chains, documents, packs, configuration, pinned workflows) | Passed; no more damage than the backup recorded |
| 7 | Restore history, evidence | Keep the evidence file and the audit events | Evidence stored with the runbook |
| 8 | (outside the platform) | Start the application against the restored database in a **separate** environment and let the Business Administrator and one tester sign in and look at three contracts, the configuration releases and a document | Business confirms |

Disaster recovery over the live database (stop the application, restore in place) exists **only on the command line** and is for a real incident. Rehearse it once in a non-production environment with the System Administrator; it is not a Business Administrator task.

### 19.3 Record
Date; backup id and manifest hash; time taken to back up; time taken to restore and validate (this is your measured recovery time); size; warnings; who restored; who verified as business; whether the objectives agreed by the Business Lead are met. Repeat at the cadence the organisation decides (suggestion: after every release, and at least quarterly).

### 19.4 What to know
- Restore is **installation-level** (per-tenant restore is Future). A restore never changes the live database; it writes a new one. The restored copy is a copy: repoint the application deliberately, with the Project Manager's approval.
- A failed restore leaves the live installation in maintenance mode on purpose; a person must acknowledge the state before it is released.
- A backup is not production approval, and having this feature is not a recovery plan.

### 19.5 Unaided?
**C** (System Administrator only, by design). A Business Administrator can read the evidence but cannot run, and should not.

---

## 20. Upgrade rehearsal

**Purpose:** before any new platform version is applied to production, prove on a restored copy that it upgrades safely, and prove the way back.

**Model:** upgrades go **forward only**; the only way back is **restore** of the backup taken before the upgrade, with the previous application version, and work done after that backup is lost. There is no downgrade.

**Who:** System Administrator (`operations.upgrade` to apply; `operations.restore` for rehearsal and rollback evidence; `operations.read` to view). Business Lead accepts warnings. The supplier supplies the release notes and the upgrade manifest inside the new build.

### 20.1 Steps (pages under Administration, Operations)
| # | Page | Action | What it checks |
|---|---|---|---|
| 1 | Backups | Create and verify a backup (section 19) | The way back |
| 2 | Upgrade Readiness | Pick the backup; run the **pre-flight** | Reports **Ready**, **Ready with warnings**, **Blocked** or **Not Verified**. Blocking examples: database newer than the build, schema drift, a pack or workflow in use that would fail, a backup that is too old or from another installation |
| 3 | Upgrade Preview | Step 1 pick the backup; Step 2 read breaking changes, migrations to apply, blockers and warnings that need the owner's acceptance | Release notes and compatibility warnings, read by the Business Lead |
| 4 | Upgrade Preview | Step 3: **rehearse** on a restored copy (target database name, reason, typed confirmation, accept listed warnings) | The restore, pre-flight on the copy, upgrade of the copy, validation (data identical, packs, configuration, audit chains, documents) and sample journeys |
| 5 | Rollback Evidence | Prove the rollback (backup, target database name, reason, typed confirmation) | The backup restores and validates, and states the data-loss window |
| 6 | Post-Upgrade Validation / Upgrade History / Migration Report | Read results; keep evidence | Each migration recorded with its checksum |
| 7 | (production day) Upgrade Preview Step 4 | Apply: reason; type `UPGRADE <from> TO <to>`; paste the expected manifest hash; accept listed warnings | Two exclusive leases; jobs drain; migrations apply in order; validation; evidence. On failure maintenance mode **stays on** and nothing is retried or rolled back automatically |

### 20.2 Business tasks around an upgrade
Read the release notes for **configuration changes and compatibility warnings** (new capabilities, new terminology keys, changed pack ranges). Re-run the UAT packs most likely to be affected (approvals, notifications, terminology). Choose a window outside business hours (an upgrade needs a maintenance window; zero downtime is not offered). Tell users in advance. After the upgrade, run the daily dashboard checks (section 18) for the first day.

### 20.3 Limits to understand
- The "previous version" fixture used by the supplier's tests is the frozen previous **schema** filled by the current services. It proves this build upgrades that schema with data; it does not prove the previous application wrote exactly your data. **Your own rehearsal on a restored copy of your own backup is the proof that matters.**
- Checksum and drift checks run on SQL Server only.
- Packs shown as unsigned or outside the platform version range will block or warn; ask the supplier before the window.
- Bare `migrate` is refused in Production on an existing installation by design; use the upgrade procedure.

### 20.4 Unaided?
**C**. The Business Lead and Business Administrator contribute the review of notes, warnings and UAT, which is **B**.

---

## Appendix A. Three worked implementations

These are **plans**, not scripts: they say which choice to make at each step and which section tells you how. Durations are planning figures from the authors' judgement for a small team with decisions made promptly; they are not measurements. Re-estimate after your first rehearsal.

### A.1 Blank implementation (neutral wording, few steps)

**Use when:** the business has no strong industry vocabulary, wants a simple register with one approval step, and is happy to adjust wording and switches through forms.

| Phase | What you do | Sections |
|---|---|---|
| Decide | One organisation; Blank pack; Production mode; your identity provider; e-mail provider; reference prefix (for example CON); regional settings (the Blank pack proposes none, so you decide country, locale, currency, time zone, tax identifier type) | 3, 10, Appendix C |
| Install | Standard container deployment with the settings table; keep the first installation as a rehearsal | 5 |
| Setup wizard | Mode Production; one organisation; Blank pack; your single sign-on; one Tenant Administrator and a separate System Administrator; real mail provider; pass the 12 readiness checks | 6 |
| Onboarding wizard | Organisation (name, prefix 3 letters), structure (a few business units), categories (top level only), contract types (up to ten), approver groups assigned to named people, regional, branding. Second person publishes | 7 |
| Terminology | Keep neutral words; change at most the handful your staff will not accept (for example Counterparty to Vendor) | 9 |
| Capabilities | Pack defaults: Registration approval and Variations on; Legal review, Renewals, Signature requests off. Turn on only what you will use | 8.3 |
| Workflow | The five-state workflow as shipped (Open, Active, Amending, Closed, Cancelled) | 12 |
| Approvals | The one approver group; no value tiers. If you need tiers later this becomes a **D** task | 11 |
| Notifications | Four events with lead times 30/60/90/120 days and a 90-day default. **There is no fallback if a category has no owner**: either give every category an owner or add a fallback in the notification policy form editor | 13 |
| Migration | Profile with the status lookup to Open or Active; usually no legacy load because the workflow has no Effective lock state | 14, 15 |
| UAT, cutover, hypercare, backups, upgrade rehearsal | As sections 16 to 20 | 16 to 20 |

**Typical shape of effort:** the smallest implementation. Almost everything is **A** or **B** for a trained Business Administrator once the system is installed and signed in. The blockers are the **C** items (installation, single sign-on, mail, backups) and the Production pack decision (D-04). Planning figure: 4 to 6 weeks elapsed, of which about 2 are installation and identity work.

### A.2 JBS-style implementation (procurement register with Legal review)

**Use when:** a procurement function manages supplier contracts with Legal review, a Group Head approval and a long lifecycle (variations, renewals, suspension, disposition).

| Phase | What you do | Sections |
|---|---|---|
| Decide | One organisation named for the procurement function; JBS-style pack; prefix JBSAUS (or your own); Australia, en-AU, AUD, Australia/Brisbane (or your zone), ABN; whether Legal review stays on (it should); who is the Group Head and the backup | 3, 8, 10 |
| Install and setup | As A.1 but add the Legal pool and the approver group members before UAT; use a classification model with a privileged level | 5, 6 |
| Onboarding | Organisation, structure (legal entities, divisions, business units, sites), five starting categories (add your own), four contract types (recorded only: G-22), approver groups, regional (ABN shown as 11 digits), branding | 7 |
| Terminology | Keep Supplier, Category Owner, Procurement Administrator and Group Head of Procurement; adjust role display names and approver group names; check that every user-facing screen uses them | 9 |
| Capabilities | All five on | 8.3 |
| Workflow | The shipped 17-state workflow **unchanged for first release**. Any change is made on the workflow form editor and is tested in UAT | 12 |
| Approvals | One approver group (Group Head, or backup) at the approval step; a delegation form for absences | 11 |
| Notifications | 28 events; fallback to organisation administrators when a category has no owner; look at the wording of the legal and approval events with Legal | 13 |
| Migration | Legacy load likely, because the old register holds effective contracts. Needs the legacy-load policy, second-person confirmation, the status lookup to Effective, Expired, and so on; attachments are held in Production until a scanner decision (D-17) | 14, 15 |
| UAT | Add scenarios for legal review (request, assign, outcome), execution, Group Head approval, variation, renewal, suspension, disposition and emergency unlock | 16 |

**Typical shape of effort:** the largest decision load (Legal and approvals) but the least authoring, because the pack carries it. Planning figure: 8 to 12 weeks elapsed, driven by legal sign-off, data clean-up and attachments.

### A.3 Harborline-style implementation (subcontract register with value tiers)

**Use when:** a construction or project business approves by value tiers, uses change orders and works in US formats.

| Phase | What you do | Sections |
|---|---|---|
| Decide | Harborline-style pack; prefix HBL (or yours); United States, en-US, USD, America/Chicago (or yours), Federal Tax ID format; the **value tiers and approvers**, which in the pack are **invented example numbers** that your finance and commercial owners must replace; whether bonds and high-risk subcontracts need extra approvers | 3, 8, 10, 11 |
| Install and setup | As A.1. Make sure the e-mail and time zone choices match US business hours | 5, 6 |
| Onboarding | Organisation, structure (projects as sites, regions as divisions), eight trade packages (replace with your own), contract types, approver groups for Package Manager, Commercial Administrator, Project Director (roles as pack terminology), regional, branding | 7 |
| Terminology | Keep Subcontract, Subcontractor, Trade Package, Change Order, Project, Region; review that e-mails and exports use them | 9 |
| Capabilities | Legal review off, Renewals off, Signature requests on, Registration approval on, Variations on | 8.3 |
| Workflow | The 11-state workflow unchanged; confirm Internal review, Stop work and Change order open match practice | 12 |
| Approvals | **The core of this implementation.** Two matrices (activation, change orders): fill in the real value tiers and approver groups; run the **approval simulator** for a case in each tier, a high-risk case and a bond case; remember a gap or tie stops a real submission. Entering the matrix is a **B** task on the form editor; simulating it is **A** | 11 |
| Notifications | 16 events; lead time 30 days (14/30/60/90) and offsets 14/7/3/1; fallback to administrators | 13 |
| Migration | Usually a legacy load of active subcontracts, then change orders created in the platform. 25 custom fields in the pack: map them with `custom.<key>` targets and lookups for choices | 14, 15 |
| UAT | Add scenarios for each value tier, a change order, stop work, completion and termination | 16 |

**Typical shape of effort:** the approval matrix and custom-field mapping dominate. Planning figure: 8 to 12 weeks elapsed.

### A.4 What is different across the three

| | Blank | JBS-style | Harborline-style |
|---|---|---|---|
| People who must decide | 2 to 3 | 6 to 8 (Legal, Procurement, Finance, IT) | 6 to 8 (Commercial, Project, Finance, IT) |
| Developer or pack-author work if you keep the shipped configuration | None beyond signing a pack for Production | None beyond signing | Value tiers are entered by a Business Administrator on the matrix form editor; the shipped numbers are invented and must be replaced |
| Biggest risk | Under-configured (no tiers, no fallback owner) | Legal scope and data volume | Wrong tiers and approvers |
| First thing to rehearse | Installation and sign-in | Legacy load | Approval simulator |

---

## Appendix B. What a Business Administrator cannot do alone today

Each row is a point where implementation still needs a **System Administrator (C)**, or a **developer, pack author or product owner (D)**. "Why" says what is missing. Gap numbers refer to Appendix D.

### B.1 Needs a System Administrator (C)

| # | Task | Why | Section |
|---|---|---|---|
| B-01 | Install the platform; database; storage; reverse proxy and TLS | Server work and environment settings | 5 |
| B-02 | Run the setup wizard's first step: obtain the one-time token and use the loopback address (or allow remote setup deliberately) | The token is issued by a server command (G-09) | 6 |
| B-03 | Set the mode (Demonstration, Test, Production), the test-mode governance setting, the e-mail provider settings, the link address, secrets | Environment settings and `/Admin/Operations/Email` are System Administrator tasks (G-09); email readiness needs a real provider test (1.4) | 1.4, 5.2, 6 |
| B-04 | Register redirect addresses in the identity provider | The page does not show them (G-10) | 6 |
| B-05 | Create the least-privilege database login | Not documented (G-16) | 5 |
| B-06 | Back up, verify, restore, upgrade, maintenance mode, operations dashboard | Operations permissions belong to the System Administrator role by design | 17 to 20 |
| B-07 | Scheduling of backups | No built-in scheduler (G-27) | 19 |
| B-08 | Key ring and secret storage; certificates | Server work (G-11, G-14, G-15) | 5 |

### B.2 Needs a developer, pack author or product owner (D)

> **Status 2026-10-19.** Rows B-20 to B-23, B-26 and B-31 were closed by the Implementation Readiness Gap Closure slice and are kept as history with their current position. B-25 and B-30 changed as described in section 1.4.

| # | Task | Why | Section |
|---|---|---|---|
| B-20 | Write or change a workflow (states, moves, guards) | **Closed (rating now B).** A Business Administrator uses the form editor at `/Admin/Forms/Edit` with preview and impact analysis; second person publishes (G-03 fixed) | 12 |
| B-21 | Write or change a notification policy (events, recipients, templates) | **Closed (rating now B).** Form editor at `/Admin/Forms/Edit` (G-03 fixed) | 13 |
| B-22 | Write or change an approval matrix | **Closed (rating now B).** Form editor at `/Admin/Forms/Edit`, plus the simulator (G-03 fixed) | 11 |
| B-23 | Create approver groups beyond those in the pack | **Closed (rating now B).** `/Admin/ApproverGroups` registry (G-04 fixed) | 11 |
| B-24 | Add a terminology key that does not exist | Keys come from the catalogue (G-18) | 9 |
| B-25 | Create, edit or sign a solution pack; trust a new signing key and apply it in Production | **Technical task for the Pack Publisher** using the standalone `ecp-pack` tool (no source checkout needed); the Tenant Administrator registers the public key. Signing is deliberately outside the application (G-07 by design); key custody Not Verified | 1.4, 8 |
| B-26 | Add a second organisation, or widen a single-organisation setup to several | **Closed (rating now B).** Tenant Administrator at `/Admin/Organisations/New`; widening is done once, with a reason, and never narrowed (G-02 fixed) | 6, 7 |
| B-27 | Re-point an organisation to another pack in onboarding | The wizard records the choice but does not assign it (G-08); use Pack Manager | 7, 8 |
| B-28 | Set the reference number format (digits, separator) | A field for **new** organisations at `/Admin/Organisations/New` (G-20 fixed); no screen changes an existing organisation's format | 7 |
| B-29 | Load contract owners, users, business units and sites with a migration | Not mapping targets (G-23) | 14 |
| B-30 | Scan attachments for malware in Production | Still open: the scanner boundary, gate and readiness self-test exist, but **no real scanner adapter** (G-25; Production Blocked) | 1.4, 14 |
| B-31 | Publish configuration in Production through the form pages | **Closed.** Form pages submit for review; a second person publishes at `/Admin/Reviews` (G-01 fixed) | 6 to 13 |

### B.3 Product-owner decisions
Decisions the supplier's product owner or your governance must make, not a developer: see Appendix C, rows marked **Owner**.

---

## Appendix C. Decision register

Fill this in during discovery (section 3). "Owner" is who decides. "If undecided" is the platform's behaviour or the safe default. **Developer** means a developer or pack author must carry the decision out; **Product owner** means the supplier decides what the product will do.

| ID | Decision | Owner | Needed by | If undecided / default | Section |
|---|---|---|---|---|---|
| D-01 | Which deployment model (container with external SQL Server is the verified one; IIS and Azure are documented but not verified) | Business Lead + System Administrator | Before install | Container with SQL Server | 1, 5 |
| D-02 | Mode for this installation (Demonstration, Test, Production) and which environments exist (rehearsal, UAT, production) | Business Lead | Before install | Production for the live one; rehearse on a separate installation | 5 |
| D-03 | Identity provider and how users are created and removed | System Administrator + Business Lead | Before setup | Sign-in is invite-only; no sign-in without a provider | 6 |
| D-04 | Which pack to start from, and, for Production, who signs it and which key is trusted (or whether the supplier's signed pack is used) | Business Lead + **Developer** | Before setup | Unsigned packs cannot be used in Production | 8 |
| D-05 | One organisation or several (more can be added at `/Admin/Organisations/New`; G-02 fixed) | Business Lead | Setup, stage on tenancy | One organisation | 6 |
| D-06 | Reference prefix and numbering | Business Lead | Onboarding | Prefix required; digits and separator are fields for new organisations (G-20 fixed) | 7 |
| D-07 | Regional profile: country, locale, currency, time zone, tax identifier type | Business Lead | Onboarding | The Blank pack proposes nothing | 10 |
| D-08 | Terminology: which words change | Business Lead | Before UAT | Pack words | 9 |
| D-09 | Capabilities: which steps are on | Business Lead | Before UAT | Pack defaults | 8.3 |
| D-10 | Approval design: approver groups, members, backups, tiers, conditions (a gap or tie stops a submission) | Business Lead + Finance | Before UAT | One approver group | 11 |
| D-11 | Legacy load: whether to do one, the allowed states, expiry, who is the second person | Business Lead + Data owner | Before migration | No legacy load | 14.7 |
| D-12 | Workflow: keep the shipped workflow or change it (then **Developer**) | Business Lead | Before UAT | Shipped workflow | 12 |
| D-13 | Notification policy: events, recipients, wording, reminders, fallback recipients, privacy levels (then **Developer** to write) | Business Lead + Legal | Before UAT | Pack policy | 13 |
| D-14 | Recovery objectives: how much data loss and how long a rebuild may take (RPO and RTO) | Business Lead | Before cutover | The platform does not decide these | 19 |
| D-15 | Backup retention, storage location, encryption and who may read backups | Business Lead + System Administrator | Before cutover | Not enforced by the platform | 19 |
| D-16 | Backup frequency and rehearsal cadence | Business Lead | Before cutover | Operator's scheduler; rehearse after every release | 19 |
| D-17 | Malware scanning of migrated attachments: accept the risk of holding files, or obtain a scanner (**Product owner** and security) | Security + **Product owner** | Before the attachment load | In Production unscanned files are held and not imported | 14.5 |
| D-18 | Sign-in re-check, session and password-less policy beyond defaults | Security | Before go-live | Defaults | 5 |
| D-19 | Data to migrate: scope, cut-off, what stays behind, retention of the old system | Data owner + Business Lead | Before migration | Not decided by the platform | 14 |
| D-20 | Duplicate rules and reference policy for the load | Data owner | Before migration | Skip existing; keep source reference where free | 14.6 |
| D-21 | Attachments: naming rule, duplicate option | Data owner | Before migration | Manifest only; link existing content | 14.5 |
| D-22 | Cutover freeze window (UTC) and the point of no return | Business Lead | Before cutover | None | 17 |
| D-23 | UAT exit rule: severity threshold, accepted deviations | Business Lead | Before UAT | None | 16 |
| D-24 | Hypercare length, roster, support route | Project Manager | Before cutover | None | 18 |
| D-25 | Upgrade policy: how soon after a release, which window, who accepts warnings | Business Lead + System Administrator | Before first upgrade | Not decided by the platform | 20 |
| D-26 | Who are the two (or more) people who can publish in Production | Business Lead | Before onboarding | At least two Organisation Administrators | 1, 7 |
| D-27 | Who is the installation (System) administrator, and is it a separate person from the Tenant Administrator | Business Lead | Setup | Separate account recommended | 6 |
| D-28 | Support access: whether the supplier may be given time-limited support access | Business Lead + Security | Before go-live | Off | 2 |
| D-29 | Classification levels in use and who may see privileged material | Legal + Business Lead | Setup stage 10 (amended later only by the System Administrator at `/Admin/Setup`; 1.4) | Pack levels | 6, 7 |
| D-30 | Production approval and go-live authority | **External governance** | Before go-live | The platform never declares it | all |

---

## Appendix D. Gap list: what still prevents a true no-code implementation

**Status note (2026-10-19).** This appendix is the original gap list as written on 2026-10-18. Rows closed since carry a **Status** note at the start of the Shortcoming cell; the register in `docs/IMPLEMENTATION_READINESS.md` and `docs/IMPLEMENTATION_REHEARSAL.md` (section 9.1) is authoritative, and "Impact" and "Suggested remedy" are history for those rows.

**Basis:** **C** = confirmed by reading the source code in preparing this playbook; **R** = reported by the research reading of the wizards and documents and **not yet exercised in a live installation**: confirm each during your rehearsal. **Severity:** High = stops or blocks a Business Administrator from finishing the step in Production; Medium = needs a workaround or a developer; Low = confusing or missing guidance. "Fix by" is who would resolve it (supplier product team unless stated).

| ID | Area | Shortcoming | Impact | Sev. | Basis | Suggested remedy |
|---|---|---|---|---|---|---|
| G-01 | Publishing | **Status: Fixed (2026-10-18); no longer a blocker.** The form pages now save a content-hashed draft, submit it for review, and a second person publishes at `/Admin/Reviews`; rollback follows the same rule; refusals are audited (`docs/IMPLEMENTATION_READINESS.md` 2.1). *Original finding:* In Production the platform requires a **second person** to publish. The form pages (Configuration, Terminology, Capabilities, Regional and the Studio pages that save before publishing) save the draft under the clicking user's name and then publish, so the clicker is always the last editor and the rule always refuses. The onboarding wizard's Publish step does not re-save and can be completed by a second person; the rollback route does not check the rule at all | Form-page configuration cannot be published in Production; the second-person control is inconsistent (blocks one route, absent on another) | High | C | Publish without re-saving when the draft is unchanged; apply the rule uniformly, including rollback; test in Production mode |
| G-02 | Organisations | **Status: Fixed (2026-10-18)** at `/Admin/Organisations`. *Original finding:* No screen adds an organisation or widens single-organisation tenancy to several; help text promises this but there is no path | Growth into a second business unit needs a developer or a rebuild | High | R | Add "New organisation" in administration; add a tenancy-widening operation |
| G-03 | Authoring | **Status: Fixed (2026-10-18)** by the form editor at `/Admin/Forms/Edit` with preview and impact analysis. *Original finding:* Workflow, notification policy and approval matrix are edited only as JSON in a text box; no form editor, diagram, preview or workflow simulator (the approval simulator tests, but does not author) | The three most business-specific parts need a pack author | High | R | Form-based editors with the validator inline, preview and simulation for each |
| G-04 | Approvers | **Status: Fixed (2026-10-18)** at `/Admin/ApproverGroups`. *Original finding:* Approver groups are typed keys on a user page; no list of groups, no create page, no check that every key in a matrix has a member | A matrix can name a group nobody belongs to; a wrong key blocks approval | High | R | Groups page with members, usage and an "empty group" check |
| G-05 | Onboarding access | A Tenant Administrator holds the permission to start the onboarding wizard but cannot save or publish it; the blocked panel names a role that is also refused | A business lead's delegate is told to proceed and then blocked | Medium | R | Align permissions and wording |
| G-06 | Users | "Send invitation" records the invitation only; no message is sent (the platform's own confirmation says "Invitation recorded") | An administrator must tell each person separately; bulk onboarding of users is manual | Medium | C | Send an invitation message through the mail provider; bulk invite from a list |
| G-07 | Packs | **Status: By design; journey documented and guarded.** The pack tool is now a standalone `ecp-pack` package (G-34 closed). *Original finding:* Creating, validating, signing and installing a signed pack needs the pack tool, key handling and a trusted key; Production refuses unsigned packs | Every customer-specific change to workflows, approvals or reminders in Production is a developer-assisted release | High | R | A supplier-operated signing service or a pack builder in the product |
| G-08 | Onboarding and packs | The onboarding "pack" step records the choice but does not assign the pack to the organisation | The wizard seems to finish a step it has not finished | Medium | R | Assign the pack from the wizard, or relabel the step |
| G-09 | Setup | The setup wizard cannot set the mode, bind to a non-loopback address or create the token; those are environment settings and a command | Cannot start the wizard without a System Administrator | Medium | R | Document in the page; offer a guided pre-check |
| G-10 | Single sign-on | The page says it shows the redirect addresses after saving; it does not | Admin must derive the address; sign-in test fails confusingly | Medium | R | Show the exact addresses on the page |
| G-11 | Key ring | If the data-protection key ring is lost, everything but the live health check fails (503) and no recovery is documented | Loss of sessions and protected values with no procedure | Medium | R | Document back-up and recovery of the key ring; add to backup |
| G-12 | E-mail | **Status: Implemented (2026-10-19)**: one email configuration, real provider test, expiry; SMTP verified only against a loopback relay, Not Verified against a customer's service; Graph Scaffolded. *Original finding:* The setup wizard's e-mail page does not set the delivery provider (an environment setting does); implicit-TLS port 465 is probably unsupported | A tested page that does not change delivery | Medium | R | Make the page authoritative or explain loudly; support 465 |
| G-13 | Installation | No installation guide in the product; steps assembled from deployment files | Unaided install by a non-developer is not realistic | Medium | R | Publish a step-by-step installation guide with screenshots |
| G-14 | Secrets | The notations for Key Vault and DPAPI are accepted but do not work | A System Administrator may believe secrets are stored safely when they are not | Medium | R | Reject unsupported notations or implement them |
| G-15 | Container | The container runs as a fixed user id that is not documented | Volume permission failures | Low | R | Document it |
| G-16 | Database | No instructions for a least-privilege runtime login; tested setup used an administrator account | Over-privileged production database account | Medium | R | Provide the exact grants and a script for the database owner to run |
| G-17 | Sign-in re-check | The configurable minutes for a recent sign-in are not read from settings in every place; behaviour is a fixed ten minutes | An organisation cannot tighten or relax it as the documentation implies | Low | R | Wire the setting or correct the documentation |
| G-18 | Terminology | An administrator cannot add a new term key; role labels come from fixed mappings | New vocabulary (a new object name) needs a developer | Low | R | Allow custom keys that only pages built by the supplier use, or document the closed catalogue |
| G-19 | Regional | **Status: Fixed (2026-10-18).** *Original finding:* The regional page has no sign-in re-check, unlike terminology and capabilities | Inconsistent safety for a setting that changes how every date and amount is shown | Low | R | Add the check |
| G-20 | Numbering | **Status: Fixed for new organisations (2026-10-18); existing organisations keep their format.** *Original finding:* Reference format (digits, separator) is not a field; the wizard asks only for a prefix | Cannot match an existing numbering scheme | Medium | R | Expose pattern, digits and separator, validated |
| G-21 | Structure | **Status: Fixed (2026-10-18).** *Original finding:* A code with a full stop is accepted by the wizard's check but publishing then fails | A whole onboarding publish fails at the end | Medium | R | Reject at entry |
| G-22 | Contract types | Types entered in onboarding are recorded and used by nothing yet | Staff think the types drive behaviour | Low | R | Use or hide |
| G-23 | Migration | Migration cannot set contract owners, legal entity, business unit or site; user accounts cannot be migrated | Registers lose ownership; owners are set by hand after load | Medium | C | Add as mapping targets with a lookup to existing people and units |
| G-24 | Migration | No mapping designer: the profile is built from CSV files; no automatic column matching, no preview of sample rows before the first dry run | The first profile needs coaching | Medium | R | A guided mapping screen with suggestions and sample rows |
| G-25 | Migration | **Status: Gate, readiness test and labelled mock implemented; no real scanner adapter (Production Blocked).** *Original finding:* No real malware scanner exists; in Production unscanned attachments are held and not imported | Attachment migration cannot complete in Production | High | C | Provide a scanner adapter, or a documented owner-accepted bypass (D-17) |
| G-26 | Migration | Freeze window times are entered in UTC rather than the organisation's time zone | Freeze starts or ends at the wrong local time | Low | C | Accept local time with the zone shown |
| G-27 | Operations | No built-in schedule for backups or reminders to rehearse restores; relies on the organisation's scheduler | Backups may simply not happen | Medium | C | A simple schedule and "last verified backup" warning on the dashboard |
| G-28 | Documentation | Counts and statements differ between some documents and the product (for example numbers of readiness checks or permissions in different places) | Reviewers lose trust and miss real differences | Low | R | One generated reference for pages, fields, rules and messages |
| G-29 | Notifications | Escalation of an unacknowledged reminder is recorded but does nothing (Future); no test-send of a single event from the policy page | Cannot prove a policy before go-live without a test contract | Low | R | Add "send a test of this event to me" |
| G-30 | Access to technical health | A Business Administrator cannot see operations or health at all, by design | Business cannot check the daily dashboard | Low | R | A read-only business summary of delivery and backup freshness |

### D.1 What a true no-code experience would need (in priority order)
1. Fix **G-01** so a second person can publish from the page they are on, and rollback follows the same rule. *(Done 2026-10-18; items 2 and 3 were also done except sent invitations (G-06); item 4 is addressed by the standalone pack tool; the real malware scanner in item 5 is still open.)*
2. Form-based editors with preview and simulation for workflows, approvals and notifications (**G-03**), with an approver-group page (**G-04**).
3. A way to add organisations and widen tenancy (**G-02**) and to create users by invitation that is sent (**G-06**).
4. A way to author and install a customer's own pack in Production without a developer (**G-07**).
5. A guided installation and a guided mapping screen (**G-13**, **G-24**), with a real malware scanner (**G-25**).

### D.2 Product-owner questions raised by this review
Whether Production should allow a documented, audited bypass for unscanned attachments (D-17); whether the second-person rule should apply to rollback; whether a single-administrator mode is acceptable for small organisations; whether packs should be signed by the supplier on a customer's behalf.

---

## Appendix E. Can a Business Administrator complete each step unaided? (summary)

Ratings: **A** unaided; **B** unaided with preparation; **C** needs a System Administrator; **D** needs a developer, pack author or product owner. Where a step has parts with different ratings, the lowest governs the whole step.

| Section | Step | Rating | Main reason |
|---|---|---|---|
| 1 | Prerequisites | B | Project Manager and Business Lead work; some technology decisions are C |
| 3 | Discovery workshop | A | Facilitated by the Project Manager |
| 4 | Data collection workbook | B | Needs source data owners |
| 5 | Installation | C | Server, database, secrets, TLS |
| 6 | Setup wizard | C | Token, mode, identity provider portal, email provider settings and test (G-09, G-10; email readiness is a real provider test, 1.4) |
| 7 | Onboarding wizard | B (second organisation: B, Tenant Administrator; Production publish: a second person at `/Admin/Reviews`) | CSV lists; G-05, G-08 remain; G-01 and G-02 fixed |
| 8 | Pack selection and assignment | B (C to build or sign: a Pack Publisher's technical task with `ecp-pack`) | G-07 by design |
| 8.3 | Capabilities | A | Form, with a decision |
| 9 | Terminology | A (D for a new key) | Form with limits; G-18 |
| 10 | Regional | A | Form; G-19 fixed |
| 11 | Approval design | B to enter a matrix on the form editor; A to simulate and delegate | G-03, G-04 fixed |
| 12 | Workflow design | B on the form editor (design is the work); a second person publishes | G-03 fixed |
| 13 | Notification design | B to write on the form editor; A to monitor and retry | G-03 fixed |
| 14 | Migration preparation | B | G-23, G-24 |
| 15 | Migration execution | B (D for Production attachments) | G-25 |
| 16 | UAT | A to run; B to design | None |
| 17 | Cutover | C for the technical steps; B for the business | Backup and validation belong to the System Administrator role |
| 18 | Hypercare | A (business); C (dashboard) | G-30 |
| 19 | Backup and restore rehearsal | C | By design |
| 20 | Upgrade rehearsal | C | By design |

**Overall reading.** For a business that keeps the shipped configuration of a pack and has a System Administrator for installation and operations, a trained Business Administrator can complete roughly three-quarters of the configuration steps unaided (ratings A and B). *Update 2026-10-19:* G-01 to G-04 are fixed, so changing a workflow, an approval matrix or a notification policy, and adding an organisation, are now form tasks (rating B). What still needs another person is building and signing a customer-specific pack (a Pack Publisher's technical task), the installation and operations steps (System Administrator), and a real malware scanner, which does not exist (Production is Blocked until one is connected). These are the authors' ratings, not measurements: confirm them in your rehearsal, and see `docs/IMPLEMENTATION_REHEARSAL.md` section 9 for the verdicts.

---

## Document control

| | |
|---|---|
| Document | Implementation playbook |
| Based on | Platform version 1.1.0, documents and source as of 2026-10-18 |
| Status of statements | Read from source and documents; not run on a live installation for this document. Appendix D marks which gaps were confirmed in code |
| Owner | Supplier documentation lead (to be assigned) |
| Review | Re-read after the first customer rehearsal and after every release that changes a wizard, a page or a permission |
| Production approval | External governance; not given or implied by this document |
