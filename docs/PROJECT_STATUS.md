# Project Status

Status date: **2026-10-16**. Development branch: claude/stoic-volta-dvszm5. This page summarises; it builds, changes and approves nothing. The sources of truth are `TASKS.md` (task state), `deploy/STATUS.md` (what was run where), `docs/TESTING.md` (test results) and the per-slice documents named below. Where this page and those disagree, they win.

**Production approval is external governance and is not declared anywhere in this repository or on this page.** Sections 8 and 9 are an engineering assessment of evidence, not an approval.

## 1. How to read this page

Labels follow `CLAUDE.md`: **Implemented** (works and has tests), **Mocked** (a stand-in that behaves, for tests and demonstration), **Disabled** (present, off by default), **Scaffolded** (files exist, cannot be run here), **Future** (not built). **Verified** always names where: *workspace* (this cloud workspace, SQLite and the in-process host), *CI* (GitHub-hosted runner with a real SQL Server 2022 container and a container image build). SQLite results are lightweight checks and are never presented as SQL Server proof.

`TASKS.md` ticks a task only when every acceptance test in its row exists and passes. A row can therefore be unticked while most of it works; section 4 separates **built but not ticked** from **not started** so that unticked does not read as missing.

## 2. Completed modules

All modules are .NET 10 projects in a modular monolith whose dependency tiers are enforced by architecture tests (`tests/Ecp.Architecture.Tests/ArchitectureTests.cs`). "Verified" columns refer to the slice tests, not to every line.

| Module (tier role) | What it is | Label | Verified |
|---|---|---|---|
| `Ecp.SharedKernel` (0) | Guards, ids, errors, clock, localisation (`en-AU` reference locale, IANA zones, UTC storage) | Implemented | workspace, CI |
| `Ecp.Formats` (0) | CSV and XLSX reading and writing, safe ZIP intake | Implemented | workspace, CI |
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
| Portability remediation S1 | Solution pack resolved by organisation; neutral Blank lifecycle, Variation workflow and notification policy; no compiled default pack (installation default is host configuration, unset); stronger ARC-010; first PRT-002 proof test. Role identifiers untouched | ADR-0024, `docs/PORTABILITY_REMEDIATION_PLAN.md` section 8.6 |

## 4. Open tasks

`TASKS.md` holds 65 unticked rows against 34 ticked. They fall into three groups.

### 4.1 Built but not ticked (acceptance incomplete)

| ID | What works | What keeps it open |
|---|---|---|
| T0.3, T0.5 | ADRs exist; the repository checker runs in CI | ADR acceptance pass not recorded; checker row not ticked |
| TNC-02, SCR-01 | Row-Level Security, composite keys, layer-off tests (SQL Server); CSRF, cookies, CSP, Production guard | Full TEN and SEC catalogues; XSS/SQLi corpora |
| SET-03, SET-04, SET-05, SET-06 | Readiness checks, wizard, onboarding | Full readiness catalogue; automated accessibility tests; delegated-administrator journey; pack selection and taxonomy import with dry run |
| CFG-01, CFG-04 | Configuration kernel, pack installation of the two shipped packs (resolved per organisation since S1) | Schema-validation framework; a pack loader (audit P-03: packs are still embedded in assemblies by the project files) |
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

The first incomplete **product** task is **INT-03: the live e-signature adapter (signed callbacks, inbox)**, the next buildable integration item. CFG-03 was ticked once the PER-002 plan check was done (section 3). The first incomplete **portability remediation** task is **S2** (`docs/PORTABILITY_REMEDIATION_PLAN.md` section 5: migrated contracts take the workflow's initial status, and the unowned-category reminder fall-back comes from policy rather than `RoleCodes.ProcurementAdministrator`). P-01 is fixed by S1; P-03 (packs embedded by project files) and P-02a/b (reminder fall-back and compiled role catalogue) remain open and block onboarding a second business without a rebuild.

## 5. Deferred tasks

Deferred means decided not to build yet, recorded in `TASKS.md` and the ADRs; they are not forgotten work.

| Deferred | Why |
|---|---|
| Visual designers (forms, rules, workflow, approval matrix: STU-03 to STU-06) and drag-and-drop | Form-based Studio first; designers depend on RUL-01, WFE-01 and CFG-02 |
| AI assistance (AIX-01) and supplier intelligence (SUP-01) | Interfaces and mocks only, disabled by default; no model or vendor call is made |
| Solution-pack marketplace and catalogue | Depends on PKM-01 to PKM-04 |
| Qlik or other BI integration beyond the neutral projection | No integration built; the `cf_<key>` projection is the extension point |
| Keyset paging, full-text or search service | Not justified by the 10,000-contract baseline; offset paging only |
| Second-person confirmation of an emergency unlock; escalation messages; migrating pinned contracts to a newer workflow release; daylight-saving tests for reminders | Recorded as Future in the lifecycle slice |
| Person and file-reference fields in migration | Cannot be migrated yet |

## 6. Verified capabilities

Test totals at the end of this pass (details and history in `docs/TESTING.md`; CI figures are from run 32, commit `a0293ee`, the final code; the documentation commit after it changes no code):

| Where | Passed | Failed | Skipped | NOT VERIFIED |
|---|---:|---:|---:|---:|
| Workspace, no SQL Server: unit 451, architecture 20, API 72, integration on SQLite 187 | 730 | 0 | 0 | 189 (the SQL Server wrappers) |
| CI, GitHub-hosted, SQL Server 2022 container (the 189 `SqlServer` tests; the performance baseline is a separate step) | 189 | 0 | 0 | 0 |

Verified on CI (runs 31 and 32; all three jobs succeeded in both): locked restore; Release build with warnings as errors and the NuGet audit as a gate; all non-SQL tests; the repository, tooling and supply-chain checks (SBOM, vulnerability and licence scan); the SQL Server suite (migrations, composite keys, Row-Level Security, audit chain, every slice's scenarios run on both providers); the Dockerfile SDK build stage, image verification and smoke test with host networking.

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
| PRT-002 as a whole (a second business end to end), terminology aliases (PRT-004), configuration and tenant export round trips (PRT-006, PRT-011), renamed roles and states (PRT-013) | Only the first PRT-002 step is proved (S1: a Blank organisation resolves its own pack and opens a Variation); the rest of the features or tests do not exist yet (`docs/PORTABILITY_AUDIT.md` section 4) |
| Performance at 100,000 contracts, selective multi-word configured-field text search | Not measured; the second word of a text search still costs about 0.8 s when it is selective (`docs/PERFORMANCE_REVIEW.md` section 10) |
| Mermaid diagram rendering | Structure is checked; rendering needs `mmdc` |

## 8. Pilot readiness assessment

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
| Business configuration without code | **Partly** | Fields, shared views, approval matrices, workflows and notification policy are configuration with draft, publish and rollback; workflows and policies are edited as JSON, not in a designer; terminology, branding and feature flags are not built |
| A second, non-JBS business | **Partly possible; not ready** | S1 fixed P-01 (pack per organisation, neutral Blank pack). P-03 (packs embedded by project files, no loader) and P-02a/b (reminder fall-back and role catalogue) are open |

Conditions a pilot should set: an external SQL Server with backups owned by the pilot organisation; a real identity provider configured and Demo sign-in disabled; Production guard on; a named support owner; and the open items above accepted in writing. `docs/PRODUCTION_READINESS_CHECKLIST.md` lists the formal items.

## 9. Commercial SaaS readiness assessment

**Not ready.** The platform has the right foundations for multi-tenancy, but SaaS needs a layer that does not exist yet.

| Area | State |
|---|---|
| Tenant isolation | Strong foundation: four layers, tested on SQL Server |
| Tenant provisioning and lifecycle (sign-up, suspend, offboard, delete) | Not built; a tenant is created by first-run setup |
| Operator control plane (fleet health, per-tenant limits, support tooling beyond audited elevation) | Not built |
| Billing, metering, plans, quotas | Not built |
| Business portability (a second business without source change) | P-01 fixed; blocked by P-03 and P-02; no pack loader, no package import and export (PKM-01), no tenant export (EXP-01) |
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
