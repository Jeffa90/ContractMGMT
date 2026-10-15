# Repository Review: Gaps, Contradictions, Risks and Assumptions

Review date: 2026-10-15. Scope: every file in the repository at commit `8c5fdc9` ("Initial Files uploaded"), read in full. No application code exists yet.

This register is the single source of IDs used by the rest of the documentation:

| Prefix | Meaning |
|---|---|
| `GAP-nn` | Something required or implied by the repository that is missing |
| `CON-nn` | Two sources disagree, or one source is internally inconsistent or ambiguous |
| `RISK-nn` | Architecture, delivery or environment risk |
| `ASM-nn` | Reversible assumption adopted so work can proceed (CLAUDE.md: "Record contradictions and reversible assumptions rather than silently guessing") |
| `Q-nn` | Open question that only the business or governance owner can answer |

**Status convention used by every new specification** (see `requirements/02-reference/README.md`): statements are tagged `[CONFIRMED]` when traceable to an authoritative source, and `[PROPOSED]` when they are an assumption awaiting confirmation. `[PROPOSED]` content never overrides `[CONFIRMED]` content and may be changed without an ADR; `[CONFIRMED]` content may not.

**Handling of authoritative files.** Nothing under `requirements/01-authoritative/` and nothing in `CLAUDE.md` has been edited. Where those files need amending, the change is listed as a *proposed amendment* (section 7) for the owner to approve.

---

## 1. Environment findings (verified by probing the session)

| Finding | Evidence | Consequence |
|---|---|---|
| No .NET SDK installed | `dotnet: command not found` | Must be installed before any build (RISK-01) |
| SDK download hosts blocked by the agent proxy | `builds.dotnet.microsoft.com`, `dot.net` install script, `dotnetcli.*`: 000/403 | `dotnet-install.sh` will not work |
| .NET SDK available via Ubuntu 24.04 apt | `apt-cache policy`: `dotnet-sdk-10.0` 10.0.112, `dotnet-sdk-8.0` 8.0.131 | T0.1 installs via apt (not yet attempted) |
| NuGet reachable | `api.nuget.org` 200 | Package restore should work |
| Docker CLI present, **daemon not running** | `docker ps`: cannot connect to `/var/run/docker.sock`; `dockerd`, `containerd`, `runc` binaries exist | Compose/Testcontainers unverified; T0.1 tests whether `dockerd` can be started |
| SQL Server apt feeds reachable | `packages.microsoft.com/ubuntu/24.04/mssql-server-2025` 200 | A native SQL Server for tests may be installable (unverified) |
| `mcr.microsoft.com` reachable | `/v2/mssql/server/tags/list` 200 | Image pull may work once a daemon runs |
| Node 22, Python 3.13, Chromium (Playwright) present | tool probes, `/opt/pw-browsers` | Accessibility/browser tests feasible |
| Linux only (Ubuntu 24.04), 4 vCPU, 16 GB RAM | `/etc/os-release`, `nproc`, `free` | IIS and Azure artefacts cannot be run here; they must be labelled *scaffold, unverified* (CLAUDE.md labelling rule) |
| No `az`, `bicep`, `sqlcmd` | tool probes | Azure scaffold cannot be validated locally unless bicep is installed from an allowed source |

---

## 2. Missing items (GAP)

### Structure and hygiene

| ID | Missing item | Why it matters | Resolution in this pass |
|---|---|---|---|
| GAP-01 | `requirements/02-reference/`, `requirements/07-integrations/`, `requirements/08-portability/` (numbering jumps 01, 03, 04, 05, 06, 09...) | Master Build Instructions reference glossary-level, integration and portability behaviour that has no specification | Folders and specifications created |
| GAP-02 | Six files listed in `PACKAGE_MANIFEST.md` are absent: `.env.example`, `.gitignore`, `requirements/06-design/.gitkeep`, `src/.gitkeep`, `templates/demo/.gitkeep`, `tests/.gitkeep` (empty directories and dotfiles are dropped by a browser ZIP upload) | A missing `.gitignore` is a secret-leakage risk the moment code exists | Created (placeholders only; `.env.example` contains no secrets) |
| GAP-03 | Review skills live in `skills/`, but Claude Code discovers project skills in `.claude/skills/`. The session's available-skill list confirms none of the seven are loaded. All seven `SKILL.md` bodies are identical boilerplate with no checklist and no file paths. No skills exist for deployment, accessibility or documentation/handover review although `prompts/90-RELEASE-READINESS.md` assesses all three | `prompts/90-RELEASE-READINESS.md` says "use all project review skills"; they currently cannot be invoked | **Not changed.** Decision needed (Q-16). Tracked as T0.5 |
| GAP-04 | No `LICENSE`/licence text although wizard stage 1 must show "privacy/licence notices" | Cannot satisfy stage 1 | Tracked as Q-17 |
| GAP-05 | Runtime baseline not stated ("preferably current supported ASP.NET Core") | Cannot pick SDK or SQL compatibility level | ASM-01 |

### Specification coverage

| ID | Missing specification | Resolution |
|---|---|---|
| GAP-10 | **Permission catalogue and role-permission matrix.** `status-transitions.csv` uses nine permission codes (`contracts.submit_legal`, `legal.review`, ...). `roles-and-permissions.csv` gives prose only. Master names roles that are absent from the CSV: Organisation administrator, scoped Division/Site administrator, Group Head | `requirements/02-reference/permission-catalogue.csv`, `role-permission-matrix.csv` (all `[PROPOSED]` where not derivable) |
| GAP-11 | **Data dictionary covers one entity (Contract, 15 fields).** Missing: Supplier, Category and category assignment, organisation structure, Document and DocumentVersion, LegalReviewRound, ApprovalRecord, Variation, RecordUnlock, AuditEvent, migration entities. `contracts.csv` also omits fields that other files require (see CON-09) | `requirements/03-data-dictionary/` extended files; original `contracts.csv` untouched |
| GAP-12 | **Workflow semantics.** Conditions are prose ("Legal required", "Authorised approver"). No definition of locks per state, Variation lifecycle, multi-round Legal review, Evergreen and Critical Notice reminders, emergency unlock, or terminal/dead-end rules | `requirements/04-workflows/WORKFLOW_SPEC.md`, `guard-vocabulary.csv`, `status-transitions-supplemental.csv` |
| GAP-13 | **Approval matrix semantics.** Sample JSON only: bound inclusivity, currency handling, other dimensions (category, contract type, legal entity, risk), sequential/parallel/first-response, delegation, effective dating, and exceptions are unspecified | `requirements/08-portability/CONFIGURATION_STUDIO_SPEC.md` section 6 |
| GAP-14 | **No-code designer specifications.** Master gives one paragraph per designer. Missing: field-definition schema (sample has 5 of ~12 properties), rule operator and action catalogue, form layout model, workflow validation checks, safe notification-template syntax, terminology, feature flags, branding | `CONFIGURATION_STUDIO_SPEC.md` |
| GAP-15 | **Configuration package and solution-pack format.** `jbs-procurement-solution-pack.json` is a six-key stub (no manifest, checksums, compatibility, dependency graph, signature). `CATALOGUE_SPEC.md` mentions signatures but no trust model | `CONFIGURATION_PACKAGE_SPEC.md`, `CONFIGURATION_LIFECYCLE_SPEC.md` |
| GAP-16 | **Integration specifications**: identity (OIDC/Entra, group mapping), e-signature/DocuSign (including DoA), reporting/Qlik, API/webhooks, notifications/email, adapter catalogue, outbox/inbox | `requirements/07-integrations/` |
| GAP-17 | **Security requirement specs**: access-control rules, audit event model and evidence export, retention/classification/legal-hold | `requirements/05-security/` additions |
| GAP-18 | **Accessibility and localisation specification** (Master states "WCAG 2.2 AA design practices" only) | `requirements/06-design/ACCESSIBILITY_AND_LOCALISATION.md` |
| GAP-19 | **Setup-wizard acceptance criteria and readiness-check catalogue.** Master lists 18 stage titles only. "Production mode refuses..." has no enumerated checks | `requirements/12-operations/SETUP_AND_READINESS_SPEC.md` |
| GAP-20 | **Migration of Legal artefacts, approvals and Variations.** `MIGRATION_REQUIREMENTS.md` purpose says these move too; the lifecycle, mapping profile, samples and Medius profile cover contracts, suppliers, taxonomies and documents only | `requirements/09-migration/MIGRATION_SUPPLEMENT.md` defines a second slice. Q-12 |
| GAP-21 | **Insufficient sample data to run the mandated tests.** No multi-tenant fixture, users/roles, category taxonomy or assignments, organisation structure, status lookup map, negative cases (duplicates, unmatched, traversal ZIP, bad dates, cross-tenant references); mapping profile maps 5 of 18 CSV columns; attachment files themselves are absent (README only); the JBS pack stub has no workflow/rule/form content | New synthetic fixtures under `sample-data/` and `migration-samples/`; negative cases specified in `docs/TESTING.md` |
| GAP-22 | **ADRs are empty.** ADR-0001..0005 are all "Proposed" with identical placeholder consequences ("Document during implementation"). No ADR for identity, configuration versioning, custom-field storage, audit immutability, jobs/outbox, document storage, UI technology, database layout, time handling | ADR-0001..0005 completed; ADR-0006..0014 added (all *Proposed*) |
| GAP-23 | **Documentation deliverables** required by Master (installation guides, no-code guides, runbooks, API docs, accessibility statement, JBS guide) do not exist except three templates; `prompts/02-RESUME-PROMPT.md` expects migration docs | Planned in `TASKS.md` tracks DOCS-*; six design docs created now |
| GAP-24 | **`TASKS.md` has no dependencies, owners, acceptance criteria or status labels**, and its order violates dependencies (CON-17) | Rewritten as dependency-driven roadmap |
| GAP-25 | **Glossary.** "Tenant", "Organisation", "Legal entity", "Business unit", "Division", "Site", "Group Head" are used without definition (CON-07) | `requirements/02-reference/GLOSSARY.md` |
| GAP-26 | **`CLAUDE.md` authority order** omits `requirements/01-authoritative/DEPLOYMENT_REQUIREMENTS.md` and does not place `docs/`, `decisions/`, `prompts/`, `skills/` or the new folders | Proposed amendment PA-1 (not applied) |

---

## 3. Contradictions and ambiguities (CON)

Severity: **H** blocks correct implementation, **M** causes rework if guessed wrong, **L** cosmetic or easily resolved.

| ID | Sev | Finding | Sources | Resolution adopted |
|---|---|---|---|---|
| CON-01 | H | **Registered Date.** Dictionary: "Stamped on authorisation", not editable. Migration: import `RegisteredDate` from source; "Historical Effective contracts may be imported as locked legacy records". Authority order ranks migration (5) above the dictionary (6) | `contracts.csv`; `MEDIUS_IMPORT_PROFILE.md`; `contracts-import.csv`; `CLAUDE.md` | ASM-06: *legacy-load policy*. Imports may set `registeredDate` and land in Effective only under a policy flag recorded on the batch, tagged `IsLegacyLoad`, fully audited, and impossible after go-live unless re-enabled by an audited decision |
| CON-02 | H | **Who approves, and what the matrix is for.** Confirmed decision: DocuSign handles execution and Delegation of Authority; Group Head or backup performs final register authorisation. Workflow has one post-signature step "Approve" by an "Authorised approver". The sample approval matrix routes by value to *Procurement Manager / Group Head Approver / Executive Approver*, none of which exist as roles | `CONFIRMED_DECISIONS.md`; `status-transitions.csv`; `demo-approval-matrix.json`; `roles-and-permissions.csv` | ASM-08: the matrix resolves *which approver group may action a given approval step*; the matrix `role` field names an **approver group** (assignment of the `Approver` role), not a security role. JBS pack configures a single tier: Group Head, with Backup Approver as first-response backup. The three-tier sample is a generic simulator fixture. Q-01 |
| CON-03 | M | `status` is `EditableAfterEffective = No`, yet Effective → Variation in Progress / Ended are required and CLAUDE.md allows audited unlock | `contracts.csv`; `status-transitions.csv`; `CLAUDE.md` | ASM-09: `status` is never directly editable by any user; only the workflow engine changes it |
| CON-04 | H | **Dead-end states.** `Legal Rework Required` has no outgoing transition; `Variation in Progress` has no outgoing transition. No withdraw/cancel from Draft or rework. Master requires "rework" and Variations to be completable | `status-transitions.csv`; Master "Workflow designer" | Proposed transitions in `status-transitions-supplemental.csv` (ASM-09). Q-03 |
| CON-05 | H | **"Read all" vs classification.** Category owners, Legal and Auditor "read all", but Master requires classifications, masking and export restrictions | `CONFIRMED_DECISIONS.md`; Master "Retention/privacy" | ASM-07: "all" means *all within the user's tenant/organisation scope, up to the user's classification clearance*. Q-06 |
| CON-06 | H | **Setup wizard order.** Stage 5 (organisation, terminology, branding) precedes stage 6 (database): resumable state has nowhere to persist. Stage 12 (solution pack) supplies terminology/branding defaults after stage 5 set them. "Production refuses exposed secrets" yet stage 6 captures a DB connection | Master "Installation wizard" | ASM-10/ASM-11: bootstrap store before the DB exists; reorder (infrastructure, then pack, then organisation tailoring); in Production the wizard *validates* secrets supplied by the secret provider and never accepts or displays them. Proposed amendment PA-2 |
| CON-07 | H | **Isolation boundary.** Master requires `TenantId` isolation; the product also has organisations, and category ownership is org-scoped. `agreementReference` is "unique business identifier" with no scope; the migration idempotency key is "source system + source record ID + target tenant" (no organisation) | Master "Product modes"; `contracts.csv`; `MIGRATION_REQUIREMENTS.md` | ASM-04: **Tenant is the hard isolation boundary** (every owned row, RLS, caches, jobs, files). **Organisation is a policy scope inside a tenant.** Uniqueness: `(TenantId, OrganisationId, AgreementReference)` and idempotency `(TenantId, OrganisationId, SourceSystem, SourceRecordId)`. Single-org installs use one fixed tenant and organisation. Q-07 |
| CON-08 | M | **Mapping profile vs dictionary.** Sample target `supplier.name` is not a dictionary field; transformation names (`Trim`, `ParseDate`, `LookupMap`) are not the spec's list; `SourceRecordId` (the idempotency key) and required `categoryId` are unmapped; `LookupMap` for Status has no lookup table | `field-mapping-profile.csv`; `SOURCE_TO_TARGET_MAPPING.md`; `contracts.csv` | Transformation catalogue and complete mapping profile + status lookup map added (`MIGRATION_SUPPLEMENT.md`, `field-mapping-profile-complete.csv`, `status-lookup-map.csv`) |
| CON-09 | M | **Dictionary lacks fields the rest of the pack needs:** `Currency`, `EstimatedValue`, `VendorNumber`, `Subcategory`, `ExtensionType`, `LegalReviewRequired`, `DocumentFolder` (sample import); owner, SAP Outline Agreement Number, business unit, plant, agreement type, notice period (Medius profile); TenantId/OrganisationId, classification, concurrency token, audit stamps, source status, extraction timestamp | `contracts-import.csv`; `MEDIUS_IMPORT_PROFILE.md`; `contracts.csv` | `contracts-extended.csv` |
| CON-10 | M | **Manifest cannot identify documents.** `ParentDocumentSourceId` implies documents have source IDs, but the manifest's `SourceRecordId` is the *contract's* ID (same value repeats per contract) and there is no source-system column. Document idempotency and parent/child versioning are undefined | `ATTACHMENT_INGESTION.md`; `attachment-manifest.csv` | `DocumentSourceId` (optional manifest column; default derived `SourceRecordId + '/' + RelativePath`) and `SourceSystem` defined in `MIGRATION_SUPPLEMENT.md` section 2 |
| CON-11 | M | **"Create new version" duplicate strategy** has no meaning for contracts: contracts change through Variations, documents have versions, and no contract-version concept exists. "Create new record with generated target reference" conflicts with "agreementReference unique" unless a generation rule exists | `MIGRATION_REQUIREMENTS.md`; `contracts.csv` | ASM-16: for contracts "Create new version" is **not offered**; it applies to documents only. Generated-reference rule defined. Q-11 |
| CON-12 | M | **Rollback vs shared reference data.** "Rollback must never affect pre-existing records or another migration batch", but a batch may create suppliers/taxonomy that a later batch or user references | `MIGRATION_REQUIREMENTS.md` | Reference-count rule in `MIGRATION_SUPPLEMENT.md` section 6: shared items created by a batch are removed only if unreferenced, otherwise retained and reported |
| CON-13 | M | **Overloaded terms.** "Register initial" (action) vs "Registered Date" (stamped only on final authorisation) vs "register authorisation". State `Initial Creation` is entered both after Legal clearance and after approval rejection. State name `Disposed-Archived` implies disposal while the decision is keep indefinitely, archive-before-delete | `status-transitions.csv`; `CONFIRMED_DECISIONS.md` | Glossary fixes meanings; state *codes* are stable, display names are terminology aliases; no rename of authoritative states |
| CON-14 | M | **Unlock authority.** Roles CSV: Procurement Administrator performs "audited unlock". CLAUDE.md: "audited emergency unlock". Dictionary: "Admin unlock only". Master: emergency/support access is reason-based and time-limited. Three phrasings, no actor/approval/expiry defined | as listed | ASM-18: permission `contracts.unlock_emergency` held by Procurement Administrator only; mandatory reason; time-boxed `RecordUnlock` that auto-relocks; second-person confirmation configurable (default on in Production). Q-05 |
| CON-15 | L | **Date semantics.** Date-only fields (`startDate`, `endDate`) vs the UTC rule; reminders compute from date-only values and need an organisation time zone. Status `Effective` vs `effectiveDate` in the future | `contracts.csv`; `CLAUDE.md` | ASM-12: instants as UTC `datetime2`; date-only as `date` interpreted in the organisation's time zone; status `Effective` is independent of `effectiveDate` |
| CON-16 | M | **Custom-field storage.** Master: "typed-value or validated JSON model with indexed/reporting projections" (undecided). `CURRENCY` has StorageKind Decimal with no currency code. No-DDL-per-field conflicts with per-field indexing | Master "Field designer"; `custom-field-types.csv` | ADR-0002/ADR-0009: typed value rows with static typed indexes; currency code stored beside the amount; reporting projections via views |
| CON-17 | H | **Dependency inversion in TASKS and prompts.** Old `TASKS.md` placed bulk migration (item 8) before metadata fields (9), workflow designer (12) and the JBS reference configuration (15). Migration maps to custom fields, workflow states and category/supplier lookups, so it depends on their *runtime* (not their designers). `prompts/01-INITIAL-BUILD-PROMPT.md` lists "MigrationBatch core models" in the first slice but defers the migration slice to "after the foundation" | old `TASKS.md`; `prompts/01-INITIAL-BUILD-PROMPT.md`; `SOURCE_TO_TARGET_MAPPING.md` | Runtime engines precede migration; designers follow it (see `TASKS.md`). A minimal pack loader is built early so the JBS reference configuration exists as *data* before migration |
| CON-18 | L | **Skills reference "authoritative requirements" without paths**, and `prompts/00-READ-ME-FIRST.md` says paste prompts into "Claude Code Cloud" while skills are not registered (GAP-03) | `skills/*/SKILL.md` | T0.5 |
| CON-19 | L | **Data-dictionary column semantics.** `EditableAfterEffective` mixes `No`, `Variation only`, `Admin unlock only`; "Core" always `Yes` | `contracts.csv` | Interpreted as: `No` = immutable; `Variation only` = changed only by an applied Variation; `Admin unlock only` = changed only via audited unlock |

---

## 4. Architecture and delivery risks (RISK)

| ID | Sev | Risk | Mitigation (where designed) |
|---|---|---|---|
| RISK-01 | H | Toolchain absent; Docker daemon not running; IIS and Azure unverifiable on Linux. The Definition of Done demands tests "across the implemented deployment artefacts" | T0.1 environment spike; honest status labels (*scaffold, unverified*); SQL Server test strategy with three fallbacks (`docs/TESTING.md` section 3) |
| RISK-02 | H | Tenant breakout via the places filters miss: raw SQL, `IgnoreQueryFilters`, background jobs, caches, search, reports, exports, blob paths | Defence in depth: EF global filters + SQL Server Row-Level Security via `SESSION_CONTEXT` + Roslyn/architecture tests banning unscoped access + per-tenant blob prefixes + tenant in every cache key (`docs/ARCHITECTURE.md` 6; `docs/SECURITY.md` 4) |
| RISK-03 | H | Metadata extensibility degrades performance and complicates migration/reporting | Typed value rows with static typed indexes; flatten for reporting; row budgets and a performance test (`ADR-0009`) |
| RISK-04 | H | "No arbitrary code" erodes: rule expressions, calculated-display fields, notification templates, matching regexes (ReDoS), package contents | Closed-vocabulary AST interpreter; no Roslyn/script engines; regex non-backtracking with timeouts; template syntax with allowlisted variables (`CONFIGURATION_STUDIO_SPEC.md`) |
| RISK-05 | H | Setup wizard is an unauthenticated administrative surface on a fresh install (takeover of an internet-exposed instance) | One-time setup token, localhost-only default, expiry, route removal after completion, re-auth for changes (`docs/SECURITY.md` 6) |
| RISK-06 | H | Workflow/approval versioning: editing a version in use, in-flight records, rollback semantics | Immutable published versions, in-flight instances pin their version, rollback = new version (`docs/DOMAIN_MODEL.md` 5; `CONFIGURATION_LIFECYCLE_SPEC.md`) |
| RISK-07 | M | Audit immutability vs volume vs privacy (old/new values may hold personal or commercial data) | Append-only table, hash chain, field-level redaction by sensitivity, partitioning plan (`ADR-0011`) |
| RISK-08 | H | Migration scale and rollback: large ZIPs, memory, timeouts, recycle during run, before-images for "update" strategy, shared lookups | Streaming, chunked commit with checkpoints, provenance with before-images, reference counting (`MIGRATION_SUPPLEMENT.md`) |
| RISK-09 | H | Unsafe uploads: zip slip, decompression bombs, OOXML/XXE, polyglots, CSV formula injection in exports, absent malware scanner | Allowlist + signature sniffing, size/ratio/entry limits, no XML external entities, formula-neutralising exports, scanner hook that quarantines until a verdict (`docs/THREAT_MODEL.md`) |
| RISK-10 | M | XLSX library licensing (some popular libraries are commercial or non-commercial licensed) | Prefer MIT/permissive: Open XML SDK or ClosedXML; licence check recorded in the SBOM task (ADR-0014) |
| RISK-11 | H | Background jobs under IIS recycling, Azure scale-out and multi-instance Docker: duplicate processing, lost work, ambient tenant context | Transactional outbox, idempotent consumers, `sp_getapplock` singleton leases, explicit tenant context per job, worker role flag (`ADR-0010`) |
| RISK-12 | H | Qlik/external reporting that connects directly to the database bypasses application security and tenant isolation | Curated read-only `reporting` views with RLS-enforced tenant filter and per-tenant database principal, or API export; never base tables (`REPORTING_QLIK_SPEC.md`) |
| RISK-13 | M | Three deployment targets differ: file systems, time-zone IDs (IANA vs Windows), ICU/globalisation in slim images, Data Protection key persistence (cookies and secrets break on restart), SQL Server image licensing and Apple Silicon | Storage and key-ring abstractions, ICU-enabled images, readiness checks per target (`docs/ARCHITECTURE.md` 8) |
| RISK-14 | H | Breadth over depth: 26 task areas, an instruction to "build actual working software", and a hostile Definition of Done | Milestones with measurable exit criteria; no item is ticked without code and tests; each milestone runnable end to end |
| RISK-15 | M | Accessibility of designers (drag/drop) and tables | Keyboard-first designers with ordering controls; axe + manual checks per milestone (`TESTING.md`) |
| RISK-16 | M | AI features: prompt injection from uploaded documents, data egress to a third-party model, hallucinated metadata applied silently | Disabled by default; no network without explicit provider configuration; output is a *proposal* needing human accept; AI service principal has no write permissions (`docs/SECURITY.md` 9) |
| RISK-17 | H | Demo identity or default credentials reachable in Production | Server-side environment guard + readiness check that fails closed; test that Production mode refuses to start with demo identity |
| RISK-18 | M | Supply chain: NuGet packages, pack signing keys, generated SBOM | Lock files, vulnerable-package scan, SBOM, key-management ADR (ADR-0014) |
| RISK-19 | M | Concurrent configuration drafts and long-lived drafts become stale or conflict | Optimistic concurrency on drafts; three-way compare against base release at publish (`CONFIGURATION_LIFECYCLE_SPEC.md`) |
| RISK-20 | L | UTF-8 BOM on every CSV in the pack | Importers must strip BOM and detect encoding (`MIGRATION_SUPPLEMENT.md`) |

---

## 5. Reversible assumptions (ASM)

| ID | Assumption | Reversal cost |
|---|---|---|
| ASM-01 | Runtime: **.NET 10 LTS** (supported until November 2028). .NET 8 LTS leaves support in November 2026, a month from this review. Database: SQL Server 2022+ compatibility level and Azure SQL | Low before first code |
| ASM-02 | UI: server-rendered **ASP.NET Core Razor Pages** with small TypeScript islands for the designers, plus a versioned JSON API with OpenAPI (ADR-0013) | Medium |
| ASM-03 | One database, **schema per module, DbContext per module**, one ordered migration runner (ADR-0006) | Medium |
| ASM-04 | Tenant = hard isolation boundary; Organisation = policy scope within a tenant (CON-07) | High: decide now |
| ASM-05 | Single-organisation installs still carry a fixed `TenantId` and `OrganisationId` | Low |
| ASM-06 | Legacy-load policy for Effective and Registered Date imports (CON-01) | Low |
| ASM-07 | "Read all" is bounded by tenant/organisation scope and classification clearance (CON-05) | Low |
| ASM-08 | Approval matrix resolves approver *groups*; JBS pack uses a single Group Head tier (CON-02) | Low |
| ASM-09 | Status changes only through the workflow engine; supplemental transitions are proposals (CON-03, CON-04) | Low |
| ASM-10 | Setup state lives in a protected bootstrap store until the database exists, then mirrors into the DB (CON-06) | Low |
| ASM-11 | In Production the wizard validates but never accepts or displays secrets; secrets arrive via environment, Docker secrets, Key Vault or DPAPI-protected file | Low |
| ASM-12 | Instants UTC `datetime2`; date-only values in organisation time zone (CON-15) | Medium |
| ASM-13 | Regular expressions in configuration use a non-backtracking engine with timeouts | Low |
| ASM-14 | Blob de-duplication ("link existing content") applies **within a tenant only**, to avoid a cross-tenant existence oracle | Low |
| ASM-15 | A contract has at most **one open Variation** at a time | Low |
| ASM-16 | "Create new version" duplicate strategy applies to documents, not contracts (CON-11) | Low |
| ASM-17 | Segregation of duties: the submitter of an approval cannot approve it (configurable per tenant) | Low |
| ASM-18 | Emergency unlock is a time-boxed, reasoned, audited grant that auto-relocks (CON-14) | Low |
| ASM-19 | The reference-pack source of truth is *data* under `templates/packs/`; no JBS name appears in compiled code (portability test) | n/a |

---

## 6. Open questions for the business or governance owner (Q)

| ID | Question | Blocks |
|---|---|---|
| Q-01 | Confirm approval semantics: does the platform approval step replace, supplement or mirror DocuSign Delegation of Authority? Which approver groups exist for JBS? | M6 approvals |
| Q-02 | Confirm the proposed role-permission matrix (`role-permission-matrix.csv`), especially who may create contracts, close contracts and approve disposition | M2 |
| Q-03 | Confirm the proposed missing transitions (rework resubmission, withdraw, cancel variation) and multi-round Legal model | M6 |
| Q-04 | Define Evergreen and Critical Notice reminder offsets, recipients and escalation | M6 |
| Q-05 | Who may perform emergency unlock, and is a second person required? | M6 |
| Q-06 | Classification levels and which roles may read each | M2/M9 |
| Q-07 | Uniqueness scope of Agreement Reference (per organisation, per tenant, per legal entity) | M4 |
| Q-08 | Formal retention periods (currently "keep indefinitely pending confirmation") | M9 |
| Q-09 | DocuSign account model (Connect webhooks vs polling), envelope-to-contract mapping, who sees DoA evidence | M6 |
| Q-10 | Qlik access mode: database views, API export or file drop; refresh latency | M9 |
| Q-11 | Meaning of "Create new version" for contracts in the duplicate-strategy list | M5 |
| Q-12 | Is historic Legal/approval/Variation data to be migrated, and from what source format? | M5b |
| Q-13 | Target volumes (contracts, attachment count/size, users, tenants) to size limits | M1 |
| Q-14 | SQL Server edition or Azure SQL tier, and Windows/IIS version in scope | M3 |
| Q-15 | Languages beyond Australian English? | M10 |
| Q-16 | Move review skills into `.claude/skills/` (copy, symlink or keep and read manually)? | T0.5 |
| Q-17 | Product licence and third-party notice text | M3 |

---

## 7. Proposed amendments to authoritative files (not applied)

| ID | File | Proposed change | Reason |
|---|---|---|---|
| PA-1 | `CLAUDE.md` authority order | Insert `requirements/01-authoritative/DEPLOYMENT_REQUIREMENTS.md` as item 4a; state that `docs/REPOSITORY_REVIEW.md` records assumptions and that `[PROPOSED]` statements never outrank `[CONFIRMED]` ones | GAP-26 |
| PA-2 | `MASTER_BUILD_INSTRUCTIONS.md` installation wizard | Reorder stages: infrastructure and identity (6, 7, 8, 9) and solution pack (12) before organisation tailoring (5, 13-15); add bootstrap store and setup token | CON-06, RISK-05 |
| PA-3 | `MASTER_BUILD_INSTRUCTIONS.md` product modes | Replace "multi-tenant mode" wording with the tenant/organisation definitions in the glossary | CON-07 |
| PA-4 | `CONFIRMED_DECISIONS.md` | Add: "Platform approval resolves approver groups; DocuSign handles Delegation of Authority" once Q-01 is answered | CON-02 |

---

## 8. Cross-reference validation

All relative links and backticked repository paths in the new and updated documents were checked against the working tree after the final edit. Result and method are recorded in `docs/DOCUMENT_INDEX.md` section "Link check".
