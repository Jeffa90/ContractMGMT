# Project Status

Status date: **2026-10-17** (after PER-002 and portability remediation S1 to S7, including S5 stable identifiers and S6 configurable capabilities; CI results are recorded in `deploy/STATUS.md`). Development branch: claude/stoic-volta-dvszm5. This page summarises; it builds, changes and approves nothing. The sources of truth are `TASKS.md` (task state), `deploy/STATUS.md` (what was run where), `docs/TESTING.md` (test results) and the per-slice documents named below. Where this page and those disagree, they win.

**Production approval is external governance and is not declared anywhere in this repository or on this page.** Sections 10 and 11 are an engineering assessment of evidence, not an approval.

## 1. How to read this page

Labels follow `CLAUDE.md`: **Implemented** (works and has tests), **Mocked** (a stand-in that behaves, for tests and demonstration), **Disabled** (present, off by default), **Scaffolded** (files exist, cannot be run here), **Future** (not built). **Verified** always names where: *workspace* (this cloud workspace, SQLite and the in-process host), *CI* (GitHub-hosted runner with a real SQL Server 2022 container and a container image build). SQLite results are lightweight checks and are never presented as SQL Server proof.

`TASKS.md` ticks a task only when every acceptance test in its row exists and passes. A row can therefore be unticked while most of it works; section 4 separates **built but not ticked** from **not started** so that unticked does not read as missing.

## 2. Completed modules

All modules are .NET 10 projects in a modular monolith whose dependency tiers are enforced by architecture tests (`tests/Ecp.Architecture.Tests/ArchitectureTests.cs`). "Verified" columns refer to the slice tests, not to every line.

| Module (tier role) | What it is | Label | Verified |
|---|---|---|---|
| `Ecp.SharedKernel` (0) | Guards, ids, errors, clock, localisation (`en-AU` reference locale, IANA zones, UTC storage) | Implemented | workspace, CI |
| `Ecp.Formats` (0) | CSV and XLSX reading and writing, safe ZIP intake | Implemented | workspace, CI |
| `Ecp.Packs` (0) and `Ecp.PackTool` | The `.ecpkg` solution-pack format: safe archive, canonical manifest, content scan, compatibility, builder, ES256 signing and trust policy, `ecp-pack` command line | Implemented (S7; ADR-0026) | workspace; CI |
| `Ecp.Persistence` | Tenant-scoped `DbContext`, global filters, `SESSION_CONTEXT` interceptor, unit of work, ordered migrations | Implemented | workspace, CI (SQL Server) |
| `Ecp.Audit` | Append-only, gap-free, hash-chained audit written in the business transaction; verify command | Implemented | CI (SQL Server) |
| `Ecp.Tenancy` | Tenants, organisations, legal entities, divisions, business units, sites | Implemented | workspace, CI |
| `Ecp.Identity` | Users, external identities, effective-dated role assignments, scopes, access evaluator, delegation, OpenID Connect and demo sign-in, support access and emergency elevation | Implemented (live Entra not verified) | workspace, CI; OIDC against an in-process mock provider |
| `Ecp.Configuration` | Draft, validate, publish, release, rollback, second-person publish, audit for every configuration kind; classification policy; packs and branding records | Implemented (kernel partial: no schema-validation framework) | workspace, CI |
| `Ecp.Setup` | First-run wizard, setup protection token, readiness checks, organisation onboarding as a configuration kind | Implemented | workspace, CI, container smoke |
| `Ecp.Operations` | Health and readiness endpoints, platform checks | Implemented (no operations dashboard) | workspace, CI |
| `Ecp.Documents` | Document and version model, local storage with tenant prefix, streaming limits, signature sniffing, SHA-256, scan-hook interface with mock | Implemented (real scanner Mocked) | workspace, CI |
| `Ecp.Contracts` | Suppliers, categories and ownership, the contract aggregate and its core fields, secured services, the typed register, list views (11 shipped, personal, shared), CSV and XLSX export | Implemented | workspace, CI |
| `Ecp.Metadata` | The additional-field engine: 17 types, strict validation, typed value rows, visibility, calculated display fields, the form-based Studio services | Implemented | workspace, CI |
| `Ecp.DataMigration` | Staging, mapping profiles, dry run, resumable commit, provenance, reconciliation, rollback, attachments | Implemented | workspace, CI |
| `Ecp.Lifecycle` | Configurable workflow engine, guards, Legal review, execution boundary, register authorisation, Variations, renewals, force-unlock | Implemented (external signature adapter Scaffolded) | workspace, CI |
| `Ecp.Notifications` | Notification policy as configuration, outbox dispatch, providers, reminders, inbox, dashboards | Implemented (SMTP and Graph not verified live) | workspace, CI |
| `Ecp.Approvals` | Approval matrix kind, resolver, flows, backups, delegation, simulator | Implemented | workspace, CI |
| `Ecp.Host` | Razor Pages application, middleware, security headers, background jobs, demo seeding | Implemented | workspace, CI, container smoke |

## 3. Completed vertical slices

Each is a slice that runs end to end from the page through the services to the database, with tests; the document names where it is described.

| Slice | What it delivers | Evidence |
|---|---|---|
| M0 / M1 walking skeleton | Solution, SQL Server integration suite, tenant isolation layers, audit chain, deployment scaffolds | `docs/ENVIRONMENT.md`, CI |
| Foundation | Localisation, supply-chain gates (locked restore, NuGet audit, SBOM, licence policy), architecture rules | `docs/SUPPLY_CHAIN.md` |
| Identity, access and configuration kernel | Roles, scopes, delegation, evaluator, configuration lifecycle | `docs/ACCESS_AND_IDENTITY.md` |
| Secure application and first-run setup | Sessions, antiforgery, permission policies, setup wizard and protection, onboarding | `docs/SETUP_AND_ONBOARDING.md`, `deploy/STATUS.md` |
| Migration | Staging, mapping, dry run, commit, reconciliation, rollback, attachments, wizard pages, scripted run | `docs/MIGRATION.md` |
| Lifecycle | Classification, state machine, Legal review, execution, register authorisation, Variations, renewals, notifications, dashboards | `docs/LIFECYCLE_AND_NOTIFICATIONS.md` |
| Approval matrix (WFE-02) | Resolver, simulator, delegation, plans pinned per request | `docs/APPROVAL_MATRIX.md` |
| Metadata, search and Studio (CFG-03 / DOM-03 / STU-01) | Core fields, the field engine, dynamic forms, typed search, views, exports, Studio, migration into additional fields | `docs/CUSTOM_FIELDS.md`, `docs/SEARCH_AND_VIEWS.md`, `docs/CONFIGURATION_STUDIO.md`, `docs/EXPORTS.md` |
| PER-002 query-plan review | Plans captured on SQL Server; one optimisation retained and one tried and reverted on same-runner evidence; configured-field text search 599 to 417 ms median (target below 500 ms met on the benchmark operation); regression guards | `docs/PERFORMANCE_REVIEW.md`, `docs/PERFORMANCE.md` |
| Portability audit and remediation plan | Where JBS and vendor names appear, classified; ordered remediation actions | `docs/PORTABILITY_AUDIT.md`, `docs/PORTABILITY_REMEDIATION_PLAN.md` |
| Portability remediation S2 | The reminder fall-back for an unowned category is a notification-policy rule (JBS states today's recipient, Blank states none); migrated contracts start in their own organisation's initial state; `Contracts:InitialStatusCode` removed from host configuration. Role identifiers untouched | ADR-0025, `docs/PORTABILITY_REMEDIATION_PLAN.md` sections 8.8 and 8.9 |
| Portability remediation S4: terminology and guard stage B | Business wording is data: a stable key per concept, labels from the organisation's published terminology, then its pack, then neutral platform wording; one resolver with a tenant-keyed cache; a versioned `Terminology` configuration kind with form-based pages and `terminology.*` permissions; an optional signed pack terminology document installed as a draft only; generic pages, messages, notifications, dashboards, built-in views, migration text and exports speak the organisation's words; the guard now scans every shipped text file | `docs/TERMINOLOGY.md`, `decisions/ADR-0028-configurable-terminology.md`, `docs/PORTABILITY_AUDIT.md` section 0.4 |
| Portability remediation S7: external solution packs | The eight embedded pack resources are gone; Blank and JBS are external `.ecpkg` files built from `templates/packs`; one product-neutral loader over shipped and tenant-installed packs; ES256 signing and trust (Production refuses unsigned and untrusted); tenant-scoped pack store, quarantine, drafts-only assignment, trust-key store, audit; form-based Pack Manager (catalogue, details, upload, validation report with CSV, preview, install, organisation packs, history, trusted publishers, checks); `solution-packs` readiness check; report-only organisation diagnostics; setup and onboarding use the loader | Implemented (ADR-0026, `docs/PACK_MANAGER.md`, `docs/PACK_SECURITY.md`) |
| Portability remediation S1 | Solution pack resolved by organisation; neutral Blank lifecycle, Variation workflow and notification policy; no compiled default pack (installation default is host configuration, unset); stronger ARC-010; first PRT-002 proof test. Role identifiers untouched | ADR-0024, `docs/PORTABILITY_REMEDIATION_PLAN.md` section 8.6 |

## 4. Open tasks

`TASKS.md` holds 65 unticked rows against 34 ticked. They fall into three groups.

### 4.1 Built but not ticked (acceptance incomplete)

| ID | What works | What keeps it open |
|---|---|---|
| T0.3, T0.5 | ADRs exist; the repository checker runs in CI | ADR acceptance pass not recorded; checker row not ticked |
| TNC-02, SCR-01 | Row-Level Security, composite keys, layer-off tests (SQL Server); CSRF, cookies, CSP, Production guard | Full TEN and SEC catalogues; XSS/SQLi corpora |
| SET-03, SET-04, SET-05, SET-06 | Readiness checks, wizard, onboarding | Full readiness catalogue; automated accessibility tests; delegated-administrator journey; pack selection and taxonomy import with dry run |
| CFG-01, CFG-04 | Configuration kernel, external pack loader and Pack Manager (S7; both shipped packs are external files) | Schema-validation framework; pack upgrade workflow, per-item merge and pack export (S7d/e, Future) |
| WFE-01 | Lifecycle module delivers the runtime | Rules-engine integration (RUL-01), designer |
| DOM-01, DOM-02, DOM-03, DCM-01, INT-01 | Suppliers, categories, contracts, register, documents, outbox | JSON API for contracts and search; some acceptance tests |
| MGR-02, MGR-05, MGR-11, DOCS-MIG | Intake, attachments, scripted end-to-end run | Runbook from the template; remaining acceptance tests |
| INT-02, INT-03 | Job framework, outbox dispatcher, manual and mock signature | Live e-signature adapter with signed callbacks |
| STU-01, STU-02 | Form-based Studio for fields and shared views | Simulation of a draft against records, diff view, JSON API, designers |
| DPL-01, DPL-02 | `web.config`, Bicep and guides | Cannot run here (Scaffolded) |

### 4.2 Not started, planned

| Area | IDs |
|---|---|
| Rules and terminology | RUL-01, RUL-02, CFG-02 (lookups, terminology, feature flags, branding) |
| Migration extras | MGR-10 (starter profiles as pack data), MGR-12 (historical Legal artefacts) |
| Portability | PKM-01 to PKM-04 (packages, signing, upgrade, second pack), EXP-01 (full tenant export), DOCS-JBS |
| Governance and reporting | GOV-01 (end-to-end classification pass; partly delivered by the lifecycle slice), GOV-02 (retention, legal hold, disposition), GOV-03 (Audit Explorer), RPT-01 (reporting projections and views; a column projection stub exists) |
| API and integrations | API-01 (`/api/v1`), INT-04 (monitoring sinks), DOCS-API |
| Operations and release | OPS-01 to OPS-04, DPL-03, ACC-01, PRF-01 (scale), DOCS-OPS, DOCS-A11Y, DOCS-DICT, REL-01, DOCS-INSTALL |

### 4.3 First incomplete task

The first incomplete **product** task is **INT-03: the live e-signature adapter (signed callbacks, inbox)**, the next buildable integration item. CFG-03 was ticked once the PER-002 plan check was done (section 3). The first incomplete **portability remediation** task is **S4** (`docs/PORTABILITY_REMEDIATION_PLAN.md` section 5: the vocabulary and terminology read side, guard stage B). P-01 is fixed by S1, P-02a and P-08 by S2; P-03 is closed by S7 (packs are external; a second business is a file, not a release). P-02b (compiled role catalogue; Medium) remains open.

## 5. Deferred tasks

Deferred means decided not to build yet, recorded in `TASKS.md` and the ADRs; they are not forgotten work.

| Deferred | Why |
|---|---|
| Visual designers (forms, rules, workflow, approval matrix: STU-03 to STU-06) and drag-and-drop | Form-based Studio first; designers depend on RUL-01, WFE-01 and CFG-02 |
| AI assistance (AIX-01) and supplier intelligence (SUP-01) | Interfaces and mocks only, disabled by default; no model or vendor call is made |
| Solution-pack marketplace and catalogue | Future; the loader, signing and Pack Manager it depends on exist (S7), upgrade and export (PKM-03) do not |
| Qlik or other BI integration beyond the neutral projection | No integration built; the `cf_<key>` projection is the extension point |
| Keyset paging, full-text or search service | Not justified by the 10,000-contract baseline; offset paging only |
| Second-person confirmation of an emergency unlock; escalation messages; migrating pinned contracts to a newer workflow release; daylight-saving tests for reminders | Recorded as Future in the lifecycle slice |
| Person and file-reference fields in migration | Cannot be migrated yet |

## 6. Verified capabilities

Test totals at the end of this pass (details and history in `docs/TESTING.md`; CI figures are from run 39, commit `3c78a29`, the S7 code; the S2 CI row is kept for comparison):

| Where | Passed | Failed | Skipped | NOT VERIFIED |
|---|---:|---:|---:|---:|
| CI after S9 (run 56, commit 626877d), SQL Server 2022 container | 244 | 0 | 0 | 0 |
| Workspace after S9, no SQL Server: unit 713, architecture 80, API 107, integration on SQLite 245 | 1,145 | 0 | 0 | 244 (the SQL Server wrappers, including 11 new Harborline wrappers) |
| CI after the S9 readiness prerequisites (run 53), SQL Server 2022 container | 233 | 0 | 0 | 0 |
| Workspace after the S9 readiness prerequisites, no SQL Server: unit 698, architecture 71, API 103, integration on SQLite 234 | 1,106 | 0 | 0 | 233 (the SQL Server wrappers, including 3 new smoke-test wrappers) |
| Workspace after S6, no SQL Server: unit 695, architecture 71, API 101, integration on SQLite 231 | 1,098 | 0 | 0 | 230 (the SQL Server wrappers, including 11 new capability wrappers) |
| Workspace after S5, no SQL Server: unit 655, architecture 64, API 95, integration on SQLite 220 | 1,034 | 0 | 0 | 219 (the SQL Server wrappers, including 1 new group-and-role wrapper) |
| Workspace after S4, no SQL Server: unit 629, architecture 64, API 93, integration on SQLite 219 | 1,005 | 0 | 0 | 218 (the SQL Server wrappers, including 9 new terminology wrappers) |
| Workspace after S3, no SQL Server: unit 563, architecture 36, API 80, integration on SQLite 207 | 886 | 0 | 0 | 209 (the SQL Server wrappers, including the S3 migration test) |
| Workspace after S7, no SQL Server: unit 545, architecture 27, API 80, integration on SQLite 202 | 854 | 0 | 0 | 203 (the SQL Server wrappers, including 10 new Pack Manager scenarios) |
| CI after S7 (run 39), SQL Server 2022 container (203 `SqlServer` tests including the performance baseline) | 203 | 0 | 0 | 0 |
| Workspace after S2, no SQL Server: unit 455, architecture 20, API 72, integration on SQLite 191 | 738 | 0 | 0 | 193 (the SQL Server wrappers) |
| CI after S2 (run 35), SQL Server 2022 container (192 `SqlServer` tests plus the performance baseline, run as separate steps) | 193 | 0 | 0 | 0 |

Verified on CI (runs 31, 32 and 35; all three jobs succeeded in each): locked restore; Release build with warnings as errors and the NuGet audit as a gate; all non-SQL tests; the repository, tooling and supply-chain checks (SBOM, vulnerability and licence scan); the SQL Server suite (migrations, composite keys, Row-Level Security, audit chain, every slice's scenarios run on both providers); the Dockerfile SDK build stage, image verification and smoke test with host networking.

Verified in the workspace only: `tools/docker-runtime-smoke.sh` and `tools/compose-smoke.sh` for earlier slices (the container daemon could not be started for the latest slices; nothing was worked around).

By capability:

* **Tenant isolation**: four layers (query filters, composite foreign keys, `SESSION_CONTEXT` with Row-Level Security, tenant-keyed storage), with layer-off tests on SQL Server.
* **Audit**: gap-free, hash-chained, same-transaction, verifiable.
* **Access control**: one evaluator driven by the confirmed role matrix; scopes, classification ceilings, delegation, last-administrator protection, support access with elevation; crafted-request tests through the real pages.
* **Configuration lifecycle**: draft, validate, publish, rollback and audit for every configuration kind; no customer configuration runs SQL, code or script (ARC-007 bans expression compilation).
* **Contracts and register**: typed, paginated, database-side search with access applied first; hidden fields are absent and unusable in filters, sorts and text.
* **Lifecycle**: configurable state machine, Legal review rounds, execution boundary, register authorisation without self-approval, Variations, renewals, locks and force-unlock.
* **Approval matrix**: overlap, gap and tie validation; fail-closed resolver; simulator parity with live submission.
* **Migration**: dry run, resumable commit, idempotent reruns, provenance, rollback, additional-field mapping.
* **Performance (register, SQL Server, 10,000 contracts, one CI runner)**: see `docs/PERFORMANCE.md` and `docs/PERFORMANCE_REVIEW.md`. No service-level target exists; against the owner's PER-002 target, configured-field text search measured 599 ms before and 417 ms after on the same runner (410 ms on the final code). A selective second search word (about 0.8 s) is outside it and is recorded as a remaining bottleneck.
* **Reminder fall-back and migrated initial status (portability S2)**: a Blank organisation's reminders never reach the JBS fall-back role (an administrator is not told; the owner is); the JBS policy keeps today's recipient; a migrated contract starts in its own organisation's initial state (`Draft` for JBS, `Open` for Blank).
* **Pack resolution (portability S1)**: a Blank organisation resolves the Blank pack and a JBS organisation the JBS pack; an organisation with no pack is refused unless the host sets `Ecp:DefaultPackId`; no compiled platform code names a default pack; ARC-010 (any spelling of the business and vendor names in `src`) passes; the Blank lifecycle opens a Variation using only Blank states and policy.

## 7. Not verified capabilities

Never reported as passed:

| Not verified | Why |
|---|---|
| IIS hosting (`web.config`, guide) | No Windows or IIS available |
| Azure deployment (`main.bicep`, guide) | No subscription or Bicep compiler |
| `compose.production.yml` | Not run |
| Live Microsoft Entra ID or other identity provider | Only an in-process mock OpenID Connect provider |
| Live SMTP and Microsoft Graph email | Providers are tested against fakes and a capture log |
| Any live e-signature service | Interface, manual and mock providers only |
| Malware scanning | A mock scan hook only |
| Browser accessibility (WCAG 2.2 AA) | ACC-01 not done; no automated audit run |
| Scale: 100,000 contracts, concurrency, cold start, HTTP and rendering time | Only the 10,000-contract in-process baseline exists |
| Backup, restore and disaster recovery | OPS-01 to OPS-04 not built |
| Penetration test and external security review | Not performed |
| Deployment-model parity (PRT-005), configuration and tenant export round trips (PRT-006, PRT-011), pack upgrade preserving local changes (PRT-009) | Not built or not tested; S9 did not touch them. Real-world fitness of the Harborline pack for a construction company is not claimed |
| Performance at 100,000 contracts, selective multi-word configured-field text search | Not measured; the second word of a text search still costs about 0.8 s when it is selective (`docs/PERFORMANCE_REVIEW.md` section 10) |
| Mermaid diagram rendering | Structure is checked; rendering needs `mmdc` |

## 8. Technical debt

Known shortcuts and gaps in what is built, each with where it is tracked. None is hidden in the "Implemented" labels above; they are the reasons some rows are unticked or labelled partial.

| Debt | Effect | Tracked as |
|---|---|---|
| Production key custody for pack signing is external: the shipped packs are built unsigned, so a Production installation needs operator-signed packs and a trusted key before any pack is usable (readiness: Degraded until then) | First Production start needs an operator step | `docs/PACK_SIGNING.md`; by design (ADR-0026 D7) |
| Pack upgrade across versions, per-item merge, pack and tenant export, marketplace | A new pack version is installed and assigned (drafts) manually | S7d/e/f; Future |
| The 12-role catalogue (including `ProcurementAdministrator`) is compiled; the new reminder rule `OrganisationAdministrators` still resolves to it | Role *codes* are permanent identifiers by decision (ADR-0029); their names, approver-group names and role visibility are configurable. A different role set or authority model is Future | P-02b closed by policy; S5 done, S8 retired |
| Legal review and register authorisation (with Registered Date stamping) were compiled capabilities that were always on; pack `features` switches were read and ignored | **Fixed in S6 (ADR-0030)**: Legal Review, Register Authorisation, Variations, Renewals and Signature Requests are configurable capabilities (organisation over pack over default of all on), enforced at the engine, hidden in pages, lists and dashboards, with compatibility checks. The metadata `features` stay informational. Category ownership, critical notice and external reporting are not capabilities (Future) | R-05, P-04 closed; slice S6 |
| Labels, dashboard titles and help text were resource strings that named JBS roles and states | **Fixed in S4 (ADR-0028)**: terminology keys, a resolver, a versioned configuration kind, pack terminology; role *codes* remain compiled (P-02b, S5/S8) | P-05 to P-07, R-06; slice S4 |
| The signature boundary was shaped like one vendor | **Fixed in S3 (ADR-0027)**: provider-neutral vocabulary, interfaces, status mapper, event inbox and a renaming migration. A live adapter and the callback endpoint remain Future | P-10; slice S3 |
| Country and ERP specifics in core fields and validation (ABN, SAP outline agreement number), the compiled classification name `Legal Privileged`, `en-AU` and Brisbane defaults | Another country or ERP needs source changes | R-01 to R-04; slice S10 |
| The architecture guard scanned C# only (stage A) | **Stage B done in S4**: every shipped text file is scanned, with a narrow written allow-list (S10 locale defaults, demo seeder, migration demo script) | P-09; slice S4 |
| Configured-field text search: a selective second word costs about 0.8 s, every statement scans the contract table once, the missing-index suggestion is unapplied, small tenancy reads repeat, offset paging only | Fine at 10,000 contracts, unknown at 100,000 | `docs/PERFORMANCE_REVIEW.md` section 10; PRF-01 |
| `RegisterFilters` keeps two copies of the sort lambda (open and closed access) | Two places to keep in step | PER-002 follow-up, no ID |
| Workflows, notification policies and approval matrices are edited as JSON; no schema-validation framework; no designers | Usable by an administrator who can edit JSON, not by a business user | CFG-01, STU-03 to STU-06, WFE-01 |
| Contracts pinned to a workflow release cannot be moved to a newer one; a migrated, non-legacy contract is not pinned to a workflow | Workflow changes apply to new contracts only | Recorded as Future in the lifecycle slice |
| Emergency unlock changes core fields only; person and file-reference fields cannot be migrated | Narrower than the requirement | Lifecycle slice, migration slice |
| CI actions are tagged, not pinned to commit SHAs | Supply-chain exposure on the build | `TODO(SEC-060)` in `.github/workflows/ci.yml` |
| Performance timings come from shared runners and vary by up to about 2.4 times; no time limit is asserted | Regressions in time are caught by shape guards and logical reads, not by a threshold | `docs/PERFORMANCE_REVIEW.md` sections 1 and 8 |
| Docker, compose, IIS and Azure checks are not runnable in the cloud workspace; only CI runs the container smoke test | Deployment claims rest on CI and on Scaffolded files | `deploy/STATUS.md` |
| Mermaid diagrams are checked for structure, not rendered | Rendering errors would be found late | `tools/check-repo.py` |

## 9. Portability status

Portability means a second business can run on the platform by configuration, packs and exports, with no source change (PRT-001 to PRT-015). Evidence: `docs/PORTABILITY_AUDIT.md` (what was found), `docs/PORTABILITY_REMEDIATION_PLAN.md` (the order of work and the outcome of each slice).

**Position: partly portable, not yet portable to a second business without a rebuild.** The platform no longer chooses a business's pack or defaults; the pack, lifecycle, Variation workflow, notification policy, reminder fall-back and migrated initial state of an organisation now come from that organisation's configuration. Packs themselves are still compiled into the assemblies, and roles are still a compiled catalogue.

### 9.1 Findings

| Finding | Severity | Status |
|---|---|---|
| P-01 a code default selected the JBS pack for every organisation | High | **Fixed (S1, ADR-0024)**: resolved by organisation, installation default is host configuration and ships unset |
| P-02a the reminder fall-back named a JBS role | High | **Fixed (S2, ADR-0025)**: a notification-policy rule; Blank states none |
| P-08 migrated contracts started in a host-configured JBS state | Medium | **Fixed (S2, ADR-0025)**: from the organisation's lifecycle |
| P-09 the architecture guard was too narrow | Medium | **Stage A done (S1)**; stages B and C open |
| P-03 packs embedded by project files, no loader | **High** | **Fixed (S7, ADR-0026)**: external signed-or-allowed packs, one loader, Pack Manager; evidence in `docs/PORTABILITY_AUDIT.md` section 0.2 |
| P-02b compiled role catalogue | Medium | **Closed by policy (S5, ADR-0029)**: permanent identifiers, names from terminology; a data-driven catalogue is Future |
| P-10 the signature boundary shaped like one vendor | Low | **Fixed (S3, ADR-0027)**: provider-neutral vocabulary and interfaces, status mapper, idempotent event inbox, renaming migration, guard over all of `src/` |
| P-04 compiled capabilities, pack feature flags ignored | Medium | **Fixed (S6, ADR-0030)** for the five platform capabilities; evidence in `docs/PORTABILITY_AUDIT.md` section 0.6 |
| P-05 JBS vocabulary in code and pages | Medium | **Fixed (S4, ADR-0028)** |
| R-03 locale fall-backs | Low | **Fixed (S10, ADR-0033)**: installation defaults ship empty; organisation settings are the authority |
| G2 tax-identifier formats; G12 ISO dates in messages | Medium, Low | **Fixed (S10)**: configurable identifier types, organisation formats in messages and exports |
| P-06, P-07, R-04 | Low | **Open** (R-04's "Evergreen" part fixed by S10) |
| R-01, R-02 | Medium | **Open** (R-01 partly addressed by S10: the tax identifier is a generic type; R-02 "Legal Privileged" constant remains a naming debt) |
| R-05 compiled Legal-review and register steps | Medium | **On/off closed (S6)**; the capabilities remain compiled, with a switch |
| R-06 | Info | **Open** |

Remaining high-severity portability violation: **none** from the original audit (P-02b is Medium). S9 recorded findings G12 to G16 and L1 (Low or Medium, none a blocker) and one generic fix, F1 (`docs/PORTABILITY_AUDIT.md` section 0.8).

### 9.2 Acceptance criteria

| Criterion | State |
|---|---|
| PRT-001 static scan | Partly verified: ARC-010 passes with every spelling in C#; no business or vendor name in `src` C#; the only `jbs-procurement` text in `src` is two project-file embedding lines (P-03) |
| PRT-002 a second business | **Met (S9).** Harborline Construction (`construction-subcontracts` 1.0.0, synthetic) installs, assigns, publishes and runs a subcontract and a change order through every state and step with no source change; SQLite locally and SQL Server in CI (run 56). Not a claim of fitness for a real construction business |
| PRT-003 no schema change per field | Verified |
| PRT-004 terminology aliases | **Met for the proof's scope (S9).** Render sweep as four roles, exports, notification inbox and every pack document show only the pack's words where it sets a term, in US formats. Limits: platform validation-message wording is not swept as a class; notification dates now follow the organisation (G12 fixed by S10) |
| PRT-006 and PRT-011 export and import round trips | **NOT VERIFIED** (not built) |
| PRT-012 locale per organisation | **Met (S10).** Locale, currency and time zone are organisation settings (a published, versioned profile; packs propose, the organisation decides); display, reminder day, notifications, exports and approval timestamps follow them; parameterised tests across en-AU, en-NZ, en-US, de-DE and zones at UTC+10, UTC+13, UTC-8 and UTC-5/-6, with daylight saving in both halves of the year; no installation default ships. Limit: page text language is not localised |
| PRT-013 renamed roles and states | **Met (S9)** under the amended criterion (ADR-0029): role, approver-group and state names shown to people come from terminology and definitions; identifiers are frozen by tests |

### 9.3 Slices

| Slice | State |
|---|---|
| S1 pack per organisation, neutral Blank pack, guard stage A, first PRT-002 proof | **Done**, CI-verified |
| S2 reminder fall-back and migrated initial status | **Done**, CI-verified (run 35) |
| S3 neutral signature vocabulary with an EF migration | **Done** (ADR-0027); CI run 41 (commit 3d1597a) green: SQL Server 209 Passed, 0 Failed, 0 Skipped, including the migration test (`deploy/STATUS.md`) |
| S4 vocabulary and terminology read side, guard stage B | **Done** (ADR-0028); local 1,005 Passed, 0 Failed, 0 Skipped, 218 NOT VERIFIED; CI run 44 (commit 6f9d862) green: SQL Server 218 Passed, 0 Failed, 0 Skipped, 0 NOT VERIFIED, container build and smoke succeeded (`deploy/STATUS.md`) |
| S5 role catalogue decision (ADR) | **Done** (ADR-0029, Option A+): identifiers permanent, frozen by tests; approver-group names and role visibility built; CI run 46 (commit f99c464) green: SQL Server 219 Passed, 0 Failed, 0 Skipped, 0 NOT VERIFIED (`deploy/STATUS.md`) |
| S6 feature flags that gate capabilities | **Done** (ADR-0030): catalogue, `Capabilities` kind, pack defaults, organisation overrides, resolver, engine gate, capability-aware navigation, onboarding and setup, compatibility and readiness checks, guard; local 1,098 Passed, 0 Failed, 0 Skipped, 230 NOT VERIFIED; CI run 48 (commit c88295c) green: SQL Server 230 Passed, 0 Failed, 0 Skipped, 0 NOT VERIFIED, container build and smoke succeeded (`deploy/STATUS.md`) |
| S7 pack loader and store; remove embedded packs; Pack Manager | **Done** (S7a to S7c; S7d to S7f Future); CI-verified (run 39; `deploy/STATUS.md`) |
| S8 implement the role decision | **Retired**: superseded by S4 and ADR-0029 |
| S9 second pack end to end (PRT-002, PRT-004, PRT-013) | **Done** (ADR-0032, `docs/SECOND_BUSINESS_PROOF.md` section 16, `docs/CONSTRUCTION_SUBCONTRACTS_PACK.md`); local 1,145 Passed, 0 Failed, 0 Skipped; CI run 56 (commit 626877d) green: SQL Server 244 Passed, 0 Failed, 0 Skipped, 0 NOT VERIFIED, container build and smoke test passed (`deploy/STATUS.md`) |
| S9 readiness prerequisites (G11, content-kind smoke test, G1a, G1b, locale probe; owner decisions D1 to D4) | **Done** (`docs/S9_READINESS.md` section 13); local 1,106 Passed, 0 Failed, 0 Skipped, 233 NOT VERIFIED; CI run 53 (commit c5d9ce2) green: SQL Server 233 Passed, 0 Failed, 0 Skipped, 0 NOT VERIFIED, container build and smoke succeeded (`deploy/STATUS.md`). S9 itself has not started |
| S10 regional defaults, locale neutrality, country-aware organisation settings and configurable counterparty tax identifiers | **Done** (ADR-0033, `docs/REGIONAL_SETTINGS.md`, `docs/TAX_IDENTIFIERS.md`, `docs/PORTABILITY_AUDIT.md` section 0.9). Local 1,298 Passed, 0 Failed, 0 Skipped, 256 NOT VERIFIED; CI run 58 (commit 13e7805) green: SQL Server 256 Passed, 0 Failed, 0 Skipped, 0 NOT VERIFIED, container build and smoke test succeeded (`docs/TESTING.md` section 9.5, `deploy/STATUS.md`) |

## 10. Pilot readiness assessment

A **pilot** here means a time-boxed, supervised use by one organisation, on non-critical or synthetic-then-migrated data, on a Docker host with an external SQL Server and a real OpenID Connect provider, with the owner accepting the limits below. This is an engineering reading of the evidence; the decision belongs to the owner and to external governance.

| Dimension | Reading | Basis |
|---|---|---|
| Core contract management for the JBS reference configuration | **Ready for a supervised pilot** | Contract, supplier, category, document, lifecycle, Legal review, approval, Variation, renewal, notification, register, export and migration slices are built and tested on SQL Server in CI |
| Data migration | **Ready for mock cycles** | Dry run, reconciliation and rollback exist; no real cutover has been rehearsed against production-sized data |
| Security model | **Adequate for a pilot, not assessed externally** | Layered isolation and server-side enforcement are tested; no penetration test, no live identity-provider run, no malware scanner |
| Deployment on Docker | **Ready** | Image builds, passes verification and smoke on CI |
| Deployment on IIS or Azure | **Not ready to rely on** | Scaffolded and unverified |
| Operations | **Not ready without a manual runbook** | No backup and restore tooling, no maintenance mode, no support bundle, no operations dashboard |
| Accessibility | **Unknown** | Pages follow accessible patterns (labels, error summaries, keyboard operation) but no audit was run |
| Performance | **Probably adequate at pilot scale** | 10,000 contracts: register pages 40 to 75 ms; configured-field text search was the slowest operation and now measures about 0.4 s (selective second words about 0.8 s; `docs/PERFORMANCE_REVIEW.md`); larger scale is unmeasured |
| Business configuration without code | **Partly** | Fields, shared views, approval matrices, workflows and notification policy are configuration with draft, publish and rollback; workflows and policies are edited as JSON, not in a designer; terminology (S4) and capabilities (S6, the feature flags) are configuration with the same lifecycle; branding administration is not built |
| A second, non-JBS business | **Partly possible; not ready** | S1 fixed P-01 (pack per organisation, neutral Blank pack), S2 fixed P-02a and P-08 (reminder fall-back, initial status) and S7 fixed P-03 (a pack is installed from a file without a rebuild; proved by a third pack built in a test). Still open: and P-02b (role catalogue) are open |

Conditions a pilot should set: an external SQL Server with backups owned by the pilot organisation; a real identity provider configured and Demo sign-in disabled; Production guard on; a named support owner; and the open items above accepted in writing. `docs/PRODUCTION_READINESS_CHECKLIST.md` lists the formal items.

## 11. Commercial SaaS readiness assessment

**Not ready.** The platform has the right foundations for multi-tenancy, but SaaS needs a layer that does not exist yet.

| Area | State |
|---|---|
| Tenant isolation | Strong foundation: four layers, tested on SQL Server |
| Tenant provisioning and lifecycle (sign-up, suspend, offboard, delete) | Not built; a tenant is created by first-run setup |
| Operator control plane (fleet health, per-tenant limits, support tooling beyond audited elevation) | Not built |
| Billing, metering, plans, quotas | Not built |
| Business portability (a second business without source change) | P-01 and P-03 fixed (packs import as drafts through the Pack Manager); P-02b open; no pack upgrade workflow or pack export (S7d/e), no tenant export (EXP-01) |
| Scale, concurrency and noisy-neighbour protection | Unmeasured beyond 10,000 contracts; per-tenant rate and size limits are not built |
| Availability, backup, restore, disaster recovery, upgrade without downtime | Not built; forward-only migrations with rollback by restore are planned (OPS-02) |
| Observability (metrics, tracing, SIEM sinks) | Structured logging with redaction exists; sinks not built (INT-04) |
| Public API and webhooks | Not built (API-01) |
| Compliance evidence (retention, legal hold, audit explorer, evidence export, data residency) | Audit chain exists; retention, legal hold and export are not built (GOV-02, GOV-03) |
| Security assurance (penetration test, threat-model review, secrets management in a cloud vault, SOC 2 or ISO 27001 controls) | Threat model and design exist; no external assurance |
| Accessibility and localisation | Locale is per organisation; no accessibility audit; one language |
| Cloud hosting (Azure) | Scaffold only |
| Documentation for customers (admin guides, API reference, accessibility statement) | Partly; the no-code administration guides and API documentation are not written |

The shortest honest path from here is: fix the remaining portability findings (P-02, P-03), build the pack loader and packages (PKM-01, PKM-04), the operations track (OPS-01 to OPS-04), the API (API-01), then a scale and security assurance pass; only then is a SaaS offering a reasonable question to ask.
