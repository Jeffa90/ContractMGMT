# Build Tasks

Dependency-driven roadmap. Narrative, sequencing logic and milestone gates: `docs/IMPLEMENTATION_PLAN.md`. Findings behind the order: `docs/REPOSITORY_REVIEW.md`. Tests: `docs/TESTING.md`.

## Rules for this file

1. A task is ticked `[x]` only when **functional code and its listed tests exist and pass** (`prompts/03-CONTINUE-IMPLEMENTATION.md`). Documentation-only tasks are ticked when the document exists and was link-checked.
2. Before starting a task, all tasks in *Depends on* must be ticked. Do not start a milestone before the previous gate in `docs/IMPLEMENTATION_PLAN.md` section 4 passes.
3. *Target label* is what the capability will be labelled when done (`CLAUDE.md`): **I** implemented, **M** mocked, **D** disabled by default, **F** future (interface only or not built), **S** scaffold, unverified (cannot be run in the current environment). Update the label to reality when ticking.
4. Update this file **before** starting a phase and after each increment. Record blockers and decisions here or in an ADR.
5. Test IDs refer to `docs/TESTING.md`. Questions/assumptions (`Q-nn`, `ASM-nn`) refer to `docs/REPOSITORY_REVIEW.md`.

## Current state (2026-10-16, end of the secure application and setup slice)

M0 done. M1 foundation complete except container verification: localisation baseline (en-AU reference locale, ICU and time-zone checks, UTC storage), supply-chain gates (SBOM, vulnerability and licence policy), architecture rules. M2/M3 vertical slice implemented on .NET 10.0.112: **identity and access** (Demo identity, generic OpenID Connect and Microsoft Entra adapters verified against a mock provider, invite-only accounts, effective-dated scoped role assignments, scope-intersection evaluator, Viewer kinds as scopes, classification ceiling, delegation and last-administrator protection, group-to-role mappings), **configuration lifecycle** (drafts, validation, versioned releases, rollback, second-person publish option, audit), **first-run setup wizard** (resumable, protected state, token and single session, 14 idempotent steps, readiness, sanitised report, closure, step-up reopening), **organisation onboarding wizard** (versioned document, CSV import, validation, synthetic access preview, provenance-based apply and rollback) and a **minimal secured application** (home, register, contract and supplier pages, category, user and role administration, audit browser, access simulation, health, setup and onboarding pages, antiforgery on every state-changing page). Documentation: `docs/SETUP_AND_ONBOARDING.md`, `docs/ACCESS_AND_IDENTITY.md`, ADR-0015 to ADR-0017.

Tests (see `docs/TESTING.md` section 3): with a real SQL Server container 453 Passed, 0 Failed, 0 Skipped, 0 NOT VERIFIED; without `ECP_TEST_SQL` 389 Passed, 64 NOT VERIFIED. **CI is Verified on a GitHub-hosted runner** (commit 975c5d3, run 6: build, non-SQL tests, repository checks, supply chain, SQL Server job and the container job all green); the Docker image's full SDK build and behavioural checks therefore passed there, while the SDK stage still cannot run in this workspace (proxy CA, not worked around). IIS, Azure and the compose files remain Scaffolded / Not Verified (`deploy/STATUS.md`).

**First incomplete M1 task: FND-06** (only the compose files remain Not Verified; the image itself is verified in CI). **First incomplete buildable task: IDN-03** (support and emergency-unlock elevation are not built; everything else in IDN-03 is). Not started by design: visual No-Code Studio, AI assistant, full workflow designer, full approval-matrix designer, solution-pack marketplace, full Legal-review lifecycle.

## M0 Decisions and environment

| Done | ID | Task | Depends on | Acceptance / tests | Label |
|---|---|---|---|---|---|
| [x] | T0.0 | Repository review; `docs/REPOSITORY_REVIEW.md`, IMPLEMENTATION_PLAN, ARCHITECTURE, DOMAIN_MODEL, SECURITY, THREAT_MODEL, TESTING; new requirement folders `02-reference`, `07-integrations`, `08-portability` and supplements; ADR-0001 to 0014; sample fixtures; repo hygiene files | none | Files exist; link check passed (`docs/DOCUMENT_INDEX.md`) | n/a |
| [x] | T0.1 | Environment spike (done 2026-10-16, see `docs/ENVIRONMENT.md`; .NET 10.0.112, container engine with host networking, disposable SQL Server 2022 Developer verified): install `dotnet-sdk-10.0` via apt; restore from NuGet; try starting `dockerd`; try native SQL Server (`mssql-server` from the Microsoft feed); confirm Playwright with `/opt/pw-browsers`; write `docs/ENVIRONMENT.md` with what ran and what did not | none | Report states run / not-run facts; SQL Server option A, B or C chosen and recorded in `docs/TESTING.md` section 3 and ADR-0005 | n/a |
| [x] | T0.2 | Owner decisions: DR-2026-10-15 recorded (D1 to D5, approved amendments, Registered Date exception); Q-01, Q-02, Q-07, Q-16 resolved, Q-03, Q-05, Q-09, Q-14 resolved in part; Q-18 to Q-20 added | none | `docs/REPOSITORY_REVIEW.md` section 9 | n/a |
| [ ] | T0.3 | ADR acceptance pass: review ADR-0001 to ADR-0014, mark Accepted/Amended/Rejected | T0.1, T0.2 | Each ADR has status and consequences | n/a |
| [x] | T0.4 | Skills decision (Q-16): moved to `.claude/skills/` (single canonical copy), seven rewritten with project-specific checklists, three added | T0.2 | Skills present with substantive bodies; consistency check validates frontmatter | n/a |
| [ ] | T0.5 | Docs link/consistency checker (script run in CI): relative links, backticked repo paths, CSV well-formedness, permission/role consistency (AUT-010 precursor) | T0.1 | Zero broken links; CSV column counts valid | I |

## M1 Walking skeleton

Gate: ARC-001/002/007/008/009; AUD-001 to AUD-005; SEC-001, SEC-019, SEC-060 to SEC-062; DEP-001/002/005/006 or recorded not verifiable.

| Done | ID | Task | Depends on | Acceptance / tests | Label |
|---|---|---|---|---|---|
| [x] | FND-01 | Solution scaffold: `src/Ecp.sln`, SharedKernel, Host, module projects (empty public contracts), `global.json`, central package management, lock files, analyzers, warnings as errors | T0.1 | `dotnet build -warnaserror` passes | I **Status:** Done; central package management, lock files, NuGet audit as gate, warnings as errors; Release build clean. |
| [x] | FND-02 | Persistence infrastructure: `ITenantOwned`, `TenantDbContext` with global filter, tenant scope abstraction (fails closed), `SESSION_CONTEXT` connection interceptor, schema-per-module convention, ordered migration runner | FND-01 | ARC-003/004/005; integration test that unscoped access throws; TEN-030/031/032 once entities exist | I **Status:** Done; EF filters, SESSION_CONTEXT interceptor, RLS, composite FKs, shared connection/unit of work. TEN-030 to TEN-033 style layer-off tests pass on SQL Server (SQLite cannot prove RLS). |
| [x] | FND-03 | Audit core: append-only `audit.Event`, per-tenant sequence, hash chain, same-transaction writer, verification command | FND-02 | AUD-001 to AUD-005 | I **Status:** Done; gap-free per-tenant sequence, hash chain, `verify-audit` command, append-only trigger and role denies (SQL Server verified). |
| [x] | FND-04 | Web host baseline: problem-details errors, correlation id, security headers/CSP, antiforgery, rate limiting, structured logging with redaction, `/health/*`, OpenAPI generation | FND-01 | SEC-001, SEC-019, SEC-031; OpenAPI diff check | I **Status:** Done; antiforgery now enforced on every state-changing Razor page (auto-validation, tests `AntiforgeryTests`); the API has no state-changing endpoints yet; log redaction still limited to secret references until request logging exists. |
| [x] | FND-05 | Clock and localisation baseline: `IClock`/`FakeClock`, resource infrastructure, `en-AU`, ICU/time-zone data check | FND-01 | Unit tests; L-01/L-06 smoke | I **Status:** Done; `SharedResource` en-AU resources, `LocaleFormatter`, `TimeZones` (IANA with Windows fallback), UTC value converters, ICU and time-zone readiness check R-OPS-02; Brisbane/Australia cases and en-US contrast tested (`LocaleFormatterTests`, `TimeZoneTests`, `LocalisationApiTests`). |
| [ ] | FND-06 | Docker foundation: Dockerfile (non-root, ICU, pinned digest), `deploy/docker/compose.yml` (web, SQL Server, named volumes), smoke script | FND-04 | DEP-001/002/005/006 (if a container engine is available, otherwise recorded) | I or S **Status:** Partial: Dockerfile (now copying the embedded `templates/packs`, `sample-data` and `requirements/02-reference`, guarded by `tools/check-repo.py`), compose files and smoke scripts present. Runtime stage **Verified** with `deploy/docker/verify-image.sh` (seeded Demo, gated fresh install, non-root, ICU, Production refusal). SDK build stage **Not Verified in the workspace** (proxy CA; not worked around) but **Verified on a GitHub runner** (CI run 6, full build plus `verify-image.sh`); compose files Not Verified; compose has no SQL Server service by design. First incomplete M1 task. |
| [x] | FND-07 | Test infrastructure: SQL Server fixture per chosen option, traits/filters, builders, vulnerable-package, SBOM and licence scans, CI script | T0.1, FND-01 | SEC-060 to SEC-062; pipeline script runs | I **Status:** Done; SQL Server fixture and NOT VERIFIED reporting, locked restore, NuGet audit as a gate, CycloneDX SBOM, licence policy (allowed, restricted, prohibited; no automatic legal approval) and checker with tests in `tools/tests`, CI steps added. CI **Verified on a GitHub runner** (run 6, commit 975c5d3). |
| [x] | FND-08 | Architecture tests: module tiers, public-contract boundary, banned APIs, endpoint authorisation requirement, no public setters on workflow-controlled properties | FND-01 | ARC-001/002/007/008/009 | I **Status:** Done; 13 architecture tests. |

## M2 Secure multi-tenant core

Gate: AUT-001 to AUT-016 (applicable); TEN-001 to TEN-021 (existing entities), TEN-030 to TEN-033, TEN-040; ARC-003 to ARC-006; SEC-002 to SEC-006, SEC-010; threat-model review. No domain feature starts before this gate.

| Done | ID | Task | Depends on | Acceptance / tests | Label |
|---|---|---|---|---|---|
| [x] | TNC-01 | Tenancy module: tenant, organisation, legal entity, division, business unit, site; tenant resolution; suspension; single-organisation default creation | FND-02 | TEN-018/019/020 | I **Status:** Done; tenants, organisations, legal entities, divisions, business units, sites with idempotent upserts and deactivation, suspension honoured at every request, single-organisation creation; tests `TenancyDomainTests`, access scenarios. |
| [x] | IDN-01 | Identity: users, memberships, role assignments, sessions; Demo identity (inert in Production); generic OIDC and Entra configuration with mock-IdP tests; invite-only default | TNC-01 | SEC-003/004/010; AUT-016 | I (Demo, OIDC); D (live Entra unverified) **Status:** Done; Demo identity (inert in Production), generic OpenID Connect and Entra adapters verified against a mock IdP (`OidcSignInTests`), invite-only linking by issuer and subject, group mappings. **Live Entra: Not Verified (D).** Memberships are per-tenant users by design (ASM-22). |
| [x] | IDN-02 | Permission catalogue, role matrix and role templates loaded from `requirements/02-reference/*.csv` as versioned seed data | IDN-01 | AUT-001, AUT-010 | I **Status:** Done; the confirmed matrix and permission catalogue are embedded and checked for consistency (`PermissionMatrixTests`, `MatrixDrivenAuthorisationTests`). |
| [ ] | IDN-03 | Access evaluator (layers 1 to 8), `IAccessScope`, `IRelationshipProvider`, delegation, elevation, last-admin protection | IDN-02 | AUT-002 to AUT-009, AUT-011, AUT-012, AUT-014, AUT-015 | I **Status:** Partial: evaluator layers 1 to 8, scope intersection, delegation, last-administrator protection and server-side resource authorisation done and tested; **elevation (support access, emergency unlock) is not built**. First incomplete buildable task. |
| [x] | IDN-04 | Minimal Category, CategoryAssignment (effective-dated) and relationship provider for ownership | IDN-03 | AUT-005 | I **Status:** Done; Category, effective-dated CategoryAssignment, ownership relationship provider, read-all/edit-assigned (`CategoryOwnerTests`, scenarios). |
| [ ] | TNC-02 | Row-Level Security policies generated from the model; composite tenant foreign keys; storage-prefix guard stub; isolation suite for existing entities | IDN-04 | TEN-001 to TEN-021 (existing), TEN-030 to TEN-033, TEN-040 | I **Status:** Partial: Row-Level Security, composite tenant foreign keys and layer-off isolation tests exist for all tables including `setup.*` (verified on SQL Server); the storage-prefix guard exists in the document store; the full TEN-nnn catalogue is not implemented. |
| [ ] | SCR-01 | Security regression baseline: CSRF, cookie flags, open redirect, XSS/SQLi corpora, Production guard | IDN-01 | SEC-002 to SEC-006, SEC-010 | I **Status:** Partial: antiforgery, cookie flags, open-redirect handling, CSP and Production guard are tested; XSS and SQL-injection corpora are not. |

## M3 Setup and deployment awareness

Gate: SEC-008/009/010; DEP-007; A11Y-009; IIS/Azure artefacts statically validated as scaffolds.

| Done | ID | Task | Depends on | Acceptance / tests | Label |
|---|---|---|---|---|---|
| [x] | SET-01 | Bootstrap store, setup token, loopback default, single session, route removal after completion, step-up for later changes | M2 gate | SEC-008, SEC-009 | I **Status:** Done; protected bootstrap file mirrored to the database, one-time token (command-issued, hash only, never logged), single 60-minute session, loopback-only by default (`404` otherwise), route closure after completion, step-up (`/Reauth`) for later changes. Tests: `SetupScenarios`, `SetupHostTests`. |
| [x] | SET-02 | Resumable idempotent wizard stages S1–S4, S6–S11, S17–S18 (Blank pack) per `SETUP_AND_READINESS_SPEC.md`; execution order per section 2 | SET-01 | Stage idempotency tests; restart mid-wizard resumes | I **Status:** Done; 14 resumable idempotent stages (mode, database, storage, tenant, pack, identity, admin, organisation, branding, classification, notifications, demo data, readiness, complete) with dependency checks; restart mid-way and after completion tested. Numbering differs from the specification's S1 to S18 (ASM-23). |
| [ ] | SET-03 | Readiness framework and catalogue (R-*), sanitised report, Production fail-closed at start-up | SET-02 | DEP-007; SEC-010 | I **Status:** Partial: 11 setup checks plus the platform checks, blocking vs warning with recorded acknowledgement, sanitised report (tested for leaks), Production guard at start-up and in the wizard. The full R-* catalogue is not built. |
| [ ] | SET-04 | Accessible wizard UI with text progress | SET-02 | A11Y-009, A11Y-002 (wizard) | I **Status:** Partial: wizard UI with text progress, labelled fields, error summaries and keyboard-operable forms; automated accessibility tests (A11Y-009, A11Y-002 for the wizard) are not written (**Not Verified**). |
| [ ] | SET-05 | Organisation onboarding part 1: structure, invite/map users, role templates, delegated administrators | SET-02, IDN-03 | Journey test; AUT-009 | I **Status:** Partial: onboarding structure, category owners, viewer scopes, identity-group mappings, plan, synthetic access preview, publish and rollback done and tested on both providers; role templates, delegated-administrator creation and the journey test are not built. |
| [ ] | DPL-01 | IIS publish foundation: framework-dependent publish, `web.config`, app pool guidance, install guide | FND-06 | DEP-003 | S |
| [ ] | DPL-02 | Azure scaffold: Bicep (App Service, Azure SQL, Storage, Key Vault, Application Insights, managed identities), install guide | FND-06 | DEP-004 | S |
| [ ] | DOCS-INSTALL | Installation guides: Docker (M1), IIS and Azure (M3) | FND-06, DPL-01, DPL-02 | Docs link-checked; steps exercised where possible | n/a |

## M4a Configuration and runtime engines

Gate: INV-20 to INV-28; NCD-001/003/004/005/006/010/012/013/014; WFL-001 to WFL-003, WFL-006, WFL-007, WFL-013; APR-001 to APR-010; PKG-010; PRT-001, PRT-003; ARC-010.

| Done | ID | Task | Depends on | Acceptance / tests | Label |
|---|---|---|---|---|---|
| [ ] | CFG-01 | Configuration kernel: definitions, versions, releases, canonical JSON and hashing, schema validation framework, release snapshot cache keyed by tenant and release, audit | M2 gate | INV-20 to INV-23, INV-26; NCD-008 (backend), NCD-009 | I **Status:** Partial: drafts, validation, versioned releases with content hash, rollback, audit and second-person publish exist for three kinds plus onboarding; definitions framework, schema validation framework and the release snapshot cache are not built. |
| [ ] | CFG-02 | Lookups, terminology, feature flags, branding: data model, runtime read side, taxonomy import hooks | CFG-01 | Unit/integration; PRT-004 precursor | I |
| [ ] | CFG-03 | Field definitions; `CustomFieldValue` typed storage with static indexes; validation for all 17 types; search and reporting-projection stub (ADR-0009) | CFG-01 | NCD-001, NCD-012; INV-27; PER-002 plan check | I |
| [ ] | RUL-01 | Rules engine: condition AST, operator and action catalogues, pass cap, trace, non-backtracking pattern engine | CFG-01 | NCD-003, NCD-004, NCD-006; property tests | I |
| [ ] | RUL-02 | Calculated-display evaluator and notification/document template placeholder engine with limits | RUL-01 | NCD-005, NCD-010 | I |
| [ ] | INT-01 | Outbox/inbox tables and transactional writer; dedupe keys | FND-03 | INV-53, INV-54; AUD-005 pattern | I |
| [ ] | WFE-01 | Workflow engine runtime: definitions from data, transitions, guards, permissions, atomic audit+outbox, idempotency, pinned versions, publish-time validator (dead ends, reachability, lock rules) | CFG-01, RUL-01, INT-01, IDN-03 | WFL-001 to WFL-003, WFL-006, WFL-007, WFL-013; INV-24 | I |
| [ ] | WFE-02 | Approval matrix resolver, submission and records, simulator core | WFE-01 | APR-001 to APR-010 | I |
| [ ] | CFG-04 | Minimal pack loader (verify, validate, apply as draft, publish via same validators) with Blank and JBS packs as data under `templates/packs/` (fields, forms, workflow from CSV plus accepted supplemental, matrix, lookups, terminology, role templates) | CFG-02, CFG-03, WFE-02 | PKG-010; PRT-001; ARC-010 | I **Status:** Partial: Blank and reference packs exist as embedded data with a catalogue; the loader (verify, validate, apply as draft) is not built. |

## M4b Core domain: contracts, suppliers, documents

Gate: INV-01 to INV-16 (subset), INV-30 to INV-36; AUT-005, AUT-007; SEC-011 to SEC-016; TEN suite for all new entities; WFL-004 to WFL-006; A11Y-001/002 for contract screens. **Demonstrable slice.**

| Done | ID | Task | Depends on | Acceptance / tests | Label |
|---|---|---|---|---|---|
| [ ] | DOM-01 | Supplier and Category/subcategory management (UI, API), organisation scope | CFG-04, IDN-04 | TEN-001/002 for new entities; AUT-001 subset | I **Status:** Partial: supplier and category pages with ownership exist and are access-tested; API surface and custom-field support are not built. |
| [ ] | DCM-01 | Documents: Document/Version model, local storage provider with tenant prefix, streaming upload limits, signature sniffing, SHA-256, quarantine, scan-hook interface with mock, scan-status gating, authorised download, content-addressed dedupe within tenant | M2 gate, FND-02 | INV-30 to INV-36; SEC-011 to SEC-016; TEN-003/004/021/033 | I (scanner M) |
| [ ] | DOM-02 | Contract aggregate: core and extended fields, custom values, status via engine, lock enforcement, legacy-load flags, Registered Date stamping at Approve, ownership by active assignment, rowVersion | DOM-01, WFE-02, DCM-01 | INV-01 to INV-16 (subset); WFL-004 to WFL-006; AUT-005, AUT-007; CNC-001 | I |
| [ ] | DOM-03 | Contract create/view/search/edit UI and API; scoped, typed search and list views | DOM-02 | A11Y-001/002; PER-001 baseline; IDOR sweep AUT-014 | I **Status:** Partial: contract register, details, create and edit pages with scoped lists exist and are access-tested; typed search and the API are not built. |
| [ ] | SET-06 | Wizard S12 pack selection (Blank, JBS), S5/S13/S14/S15 basics; onboarding part 2 (taxonomy and supplier CSV import with dry run) | DOM-01, CFG-04, SET-02 | Journey: install with JBS pack → onboard → create contract | I **Status:** Partial: pack selection and basics of S5/S15 are in setup, taxonomy import is CSV text in onboarding; supplier CSV import with dry run and the install-to-first-contract journey test are not built. |

## M5 Migration vertical slice (Phase 1 mandatory)

Gate: MIG-001 to MIG-026; TEN-010; SEC-012 to SEC-015; documented exact commands and results from a synthetic end-to-end run (`prompts/10-MIGRATION-VERTICAL-SLICE.md`).

| Done | ID | Task | Depends on | Acceptance / tests | Label |
|---|---|---|---|---|---|
| [ ] | MGR-01 | Migration domain: project, mapping profile and versions, batch state machine, staging schema, configuration-release capture, legacy-load policy model | DOM-02, DCM-01, CFG-04 | MIG-023; INV-47 | I |
| [ ] | MGR-02 | Intake and profiling: CSV (BOM/encodings), XLSX, JSON, ZIP; limits; streaming; file hashes; header/type/duplicate/missing-value profile | MGR-01 | MIG-010, MIG-011, MIG-013 | I |
| [ ] | MGR-03 | Mapping engine: target syntax, transformation catalogue, lookup maps, preview with affected-row counts, no truncation | MGR-02 | MIG-014, MIG-020 | I |
| [ ] | MGR-04 | Validation and dry run (no target writes), message catalogue, error report export (CSV/XLSX, formula-safe) | MGR-03 | MIG-001, MIG-021 | I |
| [ ] | MGR-05 | Attachment ingestion: ZIP/folder/multi-file, safety checks, manifest parsing, deterministic matching with reasons, unmatched queue with manual resolution, duplicate options, scan hook | MGR-04, DCM-01 | MIG-003, MIG-005, MIG-012, MIG-019, MIG-025; TEN-010 | I |
| [ ] | MGR-06 | Commit engine: chunked with checkpoints, idempotency keys, duplicate strategies, legacy-load commit (second-person), provenance with before-images, notification/reminder suppression | MGR-04, MGR-05 | MIG-002, MIG-004, MIG-007, MIG-015, MIG-017, MIG-024, MIG-008 | I |
| [ ] | MGR-07 | Reconciliation, business acceptance, freeze window and target freeze | MGR-06 | MIG-009, MIG-026 | I |
| [ ] | MGR-08 | Rollback preview and execution; compensating archival; reference-count rule | MGR-06 | MIG-006, MIG-016, MIG-022 | I |
| [ ] | MGR-09 | Migration wizard UI and API, accessible | MGR-07 | A11Y-002 (migration), A11Y-006 | I |
| [ ] | MGR-10 | Medius starter profile and generic Ariba/Icertis-style flat profiles as pack data | CFG-04, MGR-03 | Profile import tests | I |
| [ ] | MGR-11 | End-to-end sample (2 contracts, 3 manifest rows via fixture builder) with recorded commands and results; performance baseline | MGR-08, MGR-09 | MIG-018; PER-003 baseline | I |
| [ ] | DOCS-MIG | Migration runbook (from template), attachment naming and manifest guide, cutover and reconciliation guide | MGR-11 | Docs link-checked | n/a |
| [ ] | MGR-12 | M5b: historical Legal artefacts, approvals and Variations (read-only), in-flight items reported | MGR-11, LIF-03, Q-12 | MIG-027 | I |

## M6 Lifecycle: Legal, approvals, Variations, notifications

Gate: WFL-004, WFL-008 to WFL-012; APR-005 to APR-008; AUT-008, AUT-012, AUT-013; NTF-001 to NTF-007; CNC-001 to CNC-004; ADP-001 to ADP-008; SEC-007.

| Done | ID | Task | Depends on | Acceptance / tests | Label |
|---|---|---|---|---|---|
| [ ] | INT-02 | Job framework (leases, tenant scope), outbox dispatcher with retry/dead-letter, adapter framework, egress guard, contract-test harness | INT-01, FND-04 | ADP-001, ADP-002, ADP-003, ADP-007, ADP-008; SEC-007; CNC-004 | I |
| [ ] | LIF-01 | Legal review rounds and comments (multi-round) | DOM-03, WFE-01 | WFL-008; INV-11 | I |
| [ ] | LIF-02 | Platform approval flows: submit, approve/reject, backup, delegation, segregation of duties | WFE-02, DOM-03 | WFL-004, APR-005 to APR-008; AUT-008; CNC-002 | I |
| [ ] | LIF-03 | Variations: create, restricted changes, own workflow, apply/cancel/reject | LIF-02 | WFL-009; INV-07/08 | I |
| [ ] | LIF-04 | Emergency unlock: reasoned, time-boxed, second-person, auto-relock | DOM-02, INT-02 | WFL-011; AUT-013 | I |
| [ ] | INT-03 | E-signature interface; Mock provider; DocuSign adapter with HMAC inbox callbacks; executed-copy attach | INT-02, DCM-01 | ADP-003 to ADP-005; WFL-001 (signature path) | M (Mock), I but unverified against live account (DocuSign) |
| [ ] | NTC-01 | Notification service: templates, in-app inbox, email provider (capture + SMTP), dedupe, recipient resolution | INT-02, RUL-02 | NTF-001 to NTF-007 | I |
| [ ] | NTC-02 | Reminders (Evergreen, Critical Notice, review, end) and escalations; time-zone aware | NTC-01, DOM-02 | WFL-010 | I |

## M7 Configuration Studio

Gate: NCD-001 to NCD-015; A11Y-008; PRT-003, PRT-004, PRT-010.

| Done | ID | Task | Depends on | Acceptance / tests | Label |
|---|---|---|---|---|---|
| [ ] | STU-01 | Draft → validate → preview → simulate → publish → version history/diff → rollback, API and UI; second-person publish; draft concurrency | CFG-01, M6 gate | NCD-008, NCD-009, NCD-013, NCD-014 | I |
| [ ] | STU-02 | Field designer | STU-01, CFG-03 | NCD-002, NCD-012; A11Y-008 | I |
| [ ] | STU-03 | Forms/pages designer with accessible ordering controls | STU-02 | NCD-011; A11Y-008 | I |
| [ ] | STU-04 | Rules designer (condition builder, actions, trace) | STU-01, RUL-01 | NCD-003, NCD-006 | I |
| [ ] | STU-05 | Workflow designer and simulator | STU-01, WFE-01 | WFL-003; NCD-007 | I |
| [ ] | STU-06 | Approval matrix designer and simulator | STU-01, WFE-02 | APR-002 to APR-004, APR-009 | I |
| [ ] | STU-07 | Lookups/taxonomy, terminology, branding, feature flags, notification templates, document types administration | STU-01, CFG-02, NTC-01 | PRT-004; A11Y-011 (branding) | I |
| [ ] | STU-08 | Onboarding part 3 and generated business-admin and end-user quick guides | STU-02..07 | Journey by Business Configuration Administrator only (PRT-010) | I |
| [ ] | DOCS-NOCODE | No-code administration guides | STU-08 | Docs link-checked | n/a |

## M8 Portability

Gate: PKG-001 to PKG-011; IMP-001 to IMP-006; PRT-001 to PRT-015.

| Done | ID | Task | Depends on | Acceptance / tests | Label |
|---|---|---|---|---|---|
| [ ] | PKM-01 | Package format: export, import with quarantine, verification, dependency analysis, remapping, dry run, conflicts, apply-as-draft, rollback | STU-01, CFG-04 | PKG-001 to PKG-004, PKG-006 to PKG-008, PKG-010, PKG-011 | I |
| [ ] | PKM-02 | Signing, trust anchors, revocation, Production signature policy, catalogue | PKM-01 | PKG-005 | I |
| [ ] | PKM-03 | Pack upgrade with three-way comparison | PKM-01 | PKG-009 | I |
| [ ] | PKM-04 | Second non-JBS pack (for example Construction demo) proving portability | PKM-01, STU-08 | PRT-002 | I |
| [ ] | EXP-01 | Records, documents (manifest re-ingestable), audit evidence and full-tenant export with expiring authorised links | MGR-11, PKM-01 | IMP-001 to IMP-006; PRT-011 | I |
| [ ] | DOCS-JBS | JBS solution-pack guide | PKM-04 | Docs link-checked | n/a |

## M9 Governance, reporting, API, AI interfaces

Gate: RET-001 to RET-006; AUD-006, AUD-007; SEC-040 to SEC-045; SEC-050 to SEC-053; AUT-006; TEN-007 to TEN-009 for projections and views.

| Done | ID | Task | Depends on | Acceptance / tests | Label |
|---|---|---|---|---|---|
| [ ] | GOV-01 | Classification end to end (lists, search, exports, documents, reports) and clearance model | M6 gate | AUT-006; TEN-007; IMP-002 | I |
| [ ] | GOV-02 | Retention policies, legal hold, disposition, archive-before-delete, tombstones | GOV-01 | RET-001 to RET-006 | I |
| [ ] | GOV-03 | Audit Explorer and evidence export (signed manifest) | FND-03, GOV-01 | AUD-006, AUD-007 | I |
| [ ] | RPT-01 | Reporting projections, curated per-tenant views with RLS, reporting principals, export API/file drop; Qlik guide | GOV-01 | SEC-040 to SEC-045; TEN-008 | I (views, API); F (Qlik live) |
| [ ] | API-01 | Full `/api/v1` surface, ETag/If-Match, idempotency keys, pagination; outbound webhooks with signing | M6 gate | Contract and IDOR tests; SEC-007 | I |
| [ ] | AIX-01 | AI assistance interfaces (onboarding proposals, extraction, summary, schedule drafting, supplier classification): disabled by default, mock provider, proposals only | GOV-01, INT-02 | SEC-050 to SEC-053 | D, M |
| [ ] | SUP-01 | Supplier intelligence adapters (ABN, ERP sync, website description, classification, risk): interfaces and mocks; manual path unaffected | INT-02 | ADP-003 | M, F |
| [ ] | INT-04 | Monitoring and SIEM sinks (OpenTelemetry/syslog); ERP adapter interface | INT-02 | ADP-008 | I (sinks), F (ERP live) |
| [ ] | DOCS-API | API and integration documentation | API-01, RPT-01 | Docs link-checked | n/a |

## M10 Operations and release readiness

Gate: BRS-001 to BRS-004; UPG-001 to UPG-004; SEC-030, SEC-031; PER-001 to PER-006; A11Y-001 to A11Y-020; DEP-008; DOCS track complete; `E2E-DOD-001`; unresolved findings recorded by severity. **Production approval remains external governance.**

| Done | ID | Task | Depends on | Acceptance / tests | Label |
|---|---|---|---|---|---|
| [ ] | OPS-01 | Backup/restore procedures and restore validation script; evidence on dashboard | M9 gate | BRS-001 to BRS-003 | I |
| [ ] | OPS-02 | Upgrade pre-flight, forward-only migrations in Production, rollback by restore | OPS-01 | UPG-001 to UPG-004 | I |
| [ ] | OPS-03 | Maintenance mode; operations dashboard (health, readiness, jobs, outbox, certificates) | SET-03 | BRS-004 | I |
| [ ] | OPS-04 | Support bundle with automatic redaction; DR runbook with RPO/RTO placeholders | OPS-03 | SEC-030, SEC-031 | I |
| [ ] | DPL-03 | IIS and Azure hardening, parity run of shared behaviour suite where platform available | DPL-01, DPL-02 | DEP-008; PRT-005 | S until run on platform |
| [ ] | ACC-01 | Accessibility and localisation pass across all page types; accessibility statement template | M9 gate | A11Y-001 to A11Y-020 | I |
| [ ] | PRF-01 | Performance and scale validation | M9 gate | PER-001 to PER-006 | I |
| [ ] | DOCS-OPS | Operations, backup/restore, upgrade, DR documentation | OPS-04 | Docs link-checked | n/a |
| [ ] | DOCS-A11Y | Accessibility statement template | ACC-01 | Docs link-checked | n/a |
| [ ] | DOCS-DICT | Published data dictionary refreshed from the model | M9 gate | Dictionary matches schema (generated check) | n/a |
| [ ] | REL-01 | Release-readiness review using all skills; smoke tests; assess demo, migration, no-code, Docker, IIS, Azure, handover, security and production readiness separately | all above | `prompts/90-RELEASE-READINESS.md`; `E2E-DOD-001` passes | n/a |

## Continuous tracks (every milestone)

| ID | Task |
|---|---|
| DOCS-SEC | Keep `docs/SECURITY.md` and `docs/THREAT_MODEL.md` current; add change-log entry at each gate |
| DOCS-ARCH | Keep `docs/ARCHITECTURE.md`, `docs/DOMAIN_MODEL.md`, ADRs current; every architecture change gets an ADR |
| DOCS-TEST | Keep `docs/TESTING.md` and the test report in step with reality |
| REV-01 | Run the skill reviews at each milestone exit and record findings by severity |

## Traceability to the original task list

The previous `TASKS.md` had 26 flat items. Each is covered:

| Original item | Now covered by |
|---|---|
| Architecture, domain model and ADRs | T0.0 (done), DOCS-ARCH |
| Tenant-safe modular foundation | FND-01..08, TNC-01, TNC-02 |
| Docker, IIS and Azure deployment foundations | FND-06, DPL-01, DPL-02, DPL-03 |
| First-run installation wizard | SET-01..04, SET-06 |
| Organisation onboarding wizard | SET-05, SET-06, STU-08 |
| Identity, roles, delegated administration and server-side policies | IDN-01..04 |
| Core contract, supplier and document entities | DOM-01..03, DCM-01 |
| Bulk migration framework and attachment ingestion | MGR-01..12 |
| Metadata-driven custom fields | CFG-03 |
| No-code forms/pages designer | STU-03 |
| Safe business-rules designer | RUL-01, RUL-02, STU-04 |
| Versioned workflow designer and simulator | WFE-01, STU-05 |
| Approval-matrix designer and simulator | WFE-02, STU-06 |
| Taxonomy, feature flags, terminology and branding | CFG-02, STU-07 |
| JBS procurement reference configuration | CFG-04, DOCS-JBS |
| Legal review, approvals, locking, Variations and renewals | LIF-01..04, NTC-02 |
| Notifications and escalations | NTC-01, NTC-02 |
| Import/export and configuration packages | PKM-01..04, EXP-01 |
| Industry solution packs and catalogue | PKM-02, PKM-04 |
| AI-assisted onboarding and document-assistance interfaces | AIX-01 |
| Supplier intelligence interfaces | SUP-01 |
| APIs, webhooks and integration adapters | API-01, INT-01..04 |
| Retention, classification, legal hold and disposition | GOV-01, GOV-02 |
| Audit Explorer and evidence export | FND-03, GOV-03 |
| Operations, backup/restore, upgrade and support bundles | OPS-01..04 |
| Accessibility, localisation, security and portability tests | ACC-01, SCR-01, TESTING suites, PKM-04 |
| Business/admin/technical documentation | DOCS-* tracks |
