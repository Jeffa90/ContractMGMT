# Confirmed Decisions

- JBS Procurement is the first reference solution pack, not hard-coded universal behaviour.
- Business configuration requires no code.
- Docker is the primary portable/demo deployment; IIS and Azure are also supported.
- Category owners read all and edit active assigned categories.
- Legal reads all and collaborates through structured multi-round review.
- Group Head or backup performs final register authorisation after DocuSign execution.
- Approval stamps Registered Date separately and locks Effective records.
- Effective changes use Variations.
- Keep indefinitely pending formal retention confirmation; archive-before-delete.
- AI cannot alter core Terms and Conditions, approve, sign or register.
- Full analytics are externally accessible, including Qlik for the JBS pack.
- Bulk migration of current agreements and attachments is Phase 1 mandatory.

---

# Decision Record DR-2026-10-15

Recorded on the owner's instruction after the pre-implementation review (`docs/REPOSITORY_REVIEW.md`). Everything above this line is unchanged. Where text elsewhere (including `MASTER_BUILD_INSTRUCTIONS.md`) conflicts with this record, this record wins; conflicts are recorded in `docs/REPOSITORY_REVIEW.md`.

## D1 Roles, permissions and scopes

Roles (permissions per `requirements/02-reference/role-permission-matrix.csv`):

- **System Administrator:** technical and installation administration; manages tenants, identity providers, system configuration and integrations; does not automatically reveal stored secrets; emergency access is reason-based, time-limited and audited.
- **Tenant Administrator:** administers organisations within the tenant; manages tenant-level role mappings, solution packs and tenant settings; cannot bypass contract lifecycle controls except through explicitly granted, audited permissions.
- **Organisation Administrator:** manages organisation structure, users, delegated administrators and organisation-level configuration; cannot approve contracts or dispositions unless separately assigned the applicable approval role.
- **Business Configuration Administrator:** manages fields, forms, workflows, rules, approval matrices, lookups, terminology, branding, notification templates and solution-pack configuration; cannot alter live contract records by virtue of configuring the system.
- **Procurement Administrator:** reads and operationally administers all contracts in the authorised organisation; reassigns category ownership; manages templates and operational settings; performs audited force-unlock where explicitly authorised; cannot approve their own contract submission unless separately assigned an approval role and the approval policy permits it.
- **Category Owner / Contract Manager:** reads all contracts in the authorised organisation (subject to classification); creates contracts; edits contracts in currently assigned categories; uploads and manages documents for editable contracts; submits for Legal review and register authorisation; creates Variations and renewal requests; initiates termination, end and disposition requests; cannot directly edit an Effective locked contract; cannot finally approve disposition or permanently delete records.
- **Legal:** reads all contracts in the authorised organisation (subject to classification); creates and edits structured Legal-review rounds, comments, outcomes and Legal mark-ups; marks review cleared or returns for rework; cannot perform final register authorisation unless separately assigned an approver role; cannot alter locked commercial metadata outside an authorised workflow.
- **Approver:** reads what is required for assigned approvals; approves or rejects register-authorisation requests and, where the approval matrix assigns the role, disposition requests; cannot silently edit the submitted record while approving.
- **Backup Approver:** same authority as the primary approver for approvals assigned to the backup role; the system records the actual individual who acted.
- **Auditor:** reads records, configuration history and audit evidence within scope; exports approved audit evidence; no operational editing.
- **Viewer:** read-only, subject to organisation and classification restrictions. Viewer is not a global read role.
- **Division/Site Administrator:** scoped delegated administration only; manages permitted users, taxonomies and configuration within assigned divisions or sites; no implicit access outside scope; no implicit approval or force-unlock rights.

Scope model: every role assignment carries **Role, Organisational Scope, Category Scope and Classification Ceiling**. A user may be restricted to specific organisations, business units, sites, contract categories and classifications. **Access is the intersection of all assigned scopes.** No role grants access outside its assigned scope. Viewer assignments are configurable as All Contracts Viewer, Category Viewer, Business Unit Viewer, Site Viewer or Restricted Viewer. Category Owner remains the exception: read all (within organisation and classification scope), edit assigned categories only.

Closure: Category Owners may initiate termination, end or disposition requests; Procurement Administrators may administer the process; final disposition requires the configured disposition approver; hard deletion is unavailable to normal business users; archive-before-delete is the default.

## D2 DocuSign and platform approvals (JBS Procurement pack)

- DocuSign handles Delegation of Authority, routing for execution and signatures. The platform does not replace or duplicate DocuSign Delegation of Authority.
- After execution the signed agreement is uploaded or retrieved through the future DocuSign adapter; the platform then performs a separate register-authorisation step.
- The register-authorisation approver is the Group Head of Procurement or the configured backup approver. Approval stamps Registered Date, changes the contract to Effective and locks the record. The acting approver and decision are retained.
- The generic value-based approval matrix is a demonstration of the portable approval engine and is not part of the default JBS approval model unless subsequently configured. No JBS financial approval roles are inferred.

## D3 Agreement Reference uniqueness

- Unique constraint `(TenantId, AgreementReference)`: unambiguous anywhere within a tenant. Each organisation may configure a reference prefix and numbering rule to prevent collisions. Organisation remains a mandatory policy and reporting scope.
- The original source agreement reference is preserved separately during migration. Internal entities use immutable GUID identifiers. Configuration-package imports detect reference conflicts before committing.
- The JBS reference configuration retains the JBSAUS-style reference pattern.

## D4 Development and SQL Server testing

- .NET 10 LTS. Fast provider-independent unit and domain tests need no SQL Server. A lightweight test provider may be used for limited application tests but is never presented as proof of SQL Server behaviour.
- Real SQL Server integration tests cover migrations, constraints, Row-Level Security and provider-specific behaviour. They may be skipped only when no SQL Server connection is available (`ECP_TEST_SQL`), and then are reported **NOT VERIFIED**, never Passed.
- Docker, IIS and Azure artefacts that cannot run in the current workspace are labelled **Scaffolded / Not Verified**. Production architecture is not weakened to suit the sandbox. A CI workflow runs the SQL Server suite on a disposable SQL Server where the runner and repository policy allow.

## D5 Claude Code skills

Project skills live in `.claude/skills/` (one canonical copy) with project-specific instructions.

## Approved amendments

1. Add exit transitions from Legal Rework Required (resubmit to Legal; withdraw).
2. Add completion/cancellation transitions for Variation in Progress (apply, cancel, reject).
3. Tenant is the hard isolation boundary; organisation is the operating and policy scope.
4. "Read all" is bounded by tenant, organisation scope and information-classification policy.
5. The attachment manifest gains a stable document source identifier.
6. Migration provenance and idempotency fields are added.
7. Rollback never deletes shared or pre-existing suppliers, categories or other reference data.
8. Setup is reordered so the minimum secure persistence mechanism exists before durable organisation configuration is written.
9. Secret references are separated from ordinary configuration values.
10. The permissions, roles, data dictionaries, integration specifications, configuration semantics, setup acceptance criteria, audit, retention and classification requirements added by the review are approved as the requirements baseline. Items still awaiting an answer are tagged `[OPEN]` with a question ID.

## Registered Date migration exception

- In ordinary operation Registered Date is system-stamped by successful register authorisation.
- During an authorised legacy migration the original Registered Date may be imported. It retains source-system provenance; the migration batch, source record ID, source value and acting migration administrator are audited.
- After migration acceptance users cannot ordinarily edit Registered Date.

## D6 Approval matrix permissions (DR-2026-10-16, owner instruction)

The least-privilege allocation for the four approval matrix permissions is confirmed. It replaces the provisional allocation made by the WFE-02 implementation, and closes the open item "permissions awaiting owner confirmation".

| Permission | Granted to | Notes |
|---|---|---|
| `approvals.matrix.read` | System Administrator, Tenant Administrator, Organisation Administrator, Business Configuration Administrator, Procurement Administrator, Auditor | Approver holds it as a scoped (`S`) right: only the matrix release and the explanation of a request assigned to that approver. Backup Approver is not granted it. |
| `approvals.simulate` | System Administrator, Tenant Administrator, Organisation Administrator, Business Configuration Administrator, Procurement Administrator, Auditor | Not Category Owners, Viewers or Approvers by default. Every run is audited because it reveals approver identities. |
| `approvals.delegate` | Approver, Backup Approver | Own authority only, within the existing delegation limits (90 days, group members only, no loops, not transitive). |
| `approvals.delegate.administer` | System Administrator, Tenant Administrator (tenant scope), Organisation Administrator (organisation scope) | Procurement Administrator holds it only by explicit assignment; no default role grants it. Every administrative delegation action requires a reason and writes an audit event. |

## D7 External solution packs (S7, owner instruction 2026-10-17)

Recorded in `decisions/ADR-0026-external-solution-packs.md`. The owner confirmed:

- The Enterprise Platform path: the product is the Enterprise Contract Platform; JBS Procurement is one optional solution pack, never a compiled assumption, installation default or privileged special case.
- The target in `docs/PACK_ARCHITECTURE.md` is approved. Signing uses ECDSA P-256 (`ES256`) in format version 1, the algorithm is stored in the package, and no external cryptographic dependency is added only for Ed25519.
- Upgrade conflicts are handled per document in version 1; there is no per-definition or per-item merge.
- `Organisation.SolutionPackKey` behaviour is preserved; the Blank and JBS organisations, existing published releases and pinned contracts stay valid.
- A pack is data and configuration only. It never executes customer code, SQL, JavaScript, scripts or unsafe templates.
- Business administrators can upload, validate, preview and install an approved pack without a rebuild or redeploy.
- Production refuses unsigned, invalidly signed or untrusted packs. Demo and Test may permit explicitly marked unsigned synthetic packs only when a safe environment setting is enabled.
- No signing private key is committed. Production key custody is an external operational responsibility.
- The Pack Manager is initially form-based and accessible: no marketplace and no graphical designer.

### D7a Pack permissions (proposal in the S7 brief, implemented as below)

| Permission | Granted to |
|---|---|
| `packs.read` | Tenant Administrator, Organisation Administrator, Business Configuration Administrator, Auditor |
| `packs.upload` | Tenant Administrator, Business Configuration Administrator |
| `packs.validate` | Tenant Administrator, Organisation Administrator, Business Configuration Administrator |
| `packs.install` | Tenant Administrator, Business Configuration Administrator |
| `packs.assign` | Tenant Administrator, Organisation Administrator, Business Configuration Administrator (organisation scope) |
| `packs.retire` | Tenant Administrator |
| `packs.trust.manage` | Tenant Administrator |
| `packs.audit.read` | Tenant Administrator, Auditor |

### D7b Terminology permissions (S4)

Terminology (labels for business concepts) has its own least-privilege permissions so it can be delegated without the general configuration permissions. A label never changes what anyone may do.

| Permission | Granted to |
|---|---|
| `terminology.read` | Tenant Administrator, Organisation Administrator, Business Configuration Administrator, Auditor |
| `terminology.draft` | Tenant Administrator (any organisation), Organisation Administrator (own organisation), Business Configuration Administrator |
| `terminology.publish` | Tenant Administrator (any organisation), Organisation Administrator (own organisation), Business Configuration Administrator; step-up authentication and second-person publication apply |
| `terminology.rollback` | Same as `terminology.publish` |
| `terminology.audit.read` | Tenant Administrator, Organisation Administrator, Business Configuration Administrator, Auditor |

Persisted role codes (`ProcurementAdministrator`, `CategoryOwner`, `Legal`, `Approver`, `BackupApprover`), permission codes, workflow state codes, audit action codes, configuration-kind keys and pack identifiers are stable machine identifiers and are not renamed. Terminology supplies what users read. The owner's brief named `GroupHeadApprover` among the stable identifiers; no identifier of that name exists in the repository (the persisted names are the role `Approver` and the approver group key `GroupHeadProcurement`), so both are kept unchanged. This is recorded as a contradiction in the brief, resolved by keeping what exists.

Trust-key administration is deliberately not held by ordinary business configuration administrators. Install, retire, assign and trust changes also need a recent sign-in. (`requirements/02-reference/role-permission-matrix.csv` is the machine-readable source and the tests read it.)

### D7c Stable role, permission and approver-group identifiers (S5)

Owner decision of 2026-10-17 (Option A+; `decisions/ADR-0029-stable-role-and-permission-identifiers.md`, `docs/ROLE_CATALOGUE_ADR.md`): role codes, permission codes and approver-group keys are permanent identifiers and are never renamed, aliased or reused. What people read comes from terminology and packs. Approver groups may carry display names and roles may be hidden from assignment pickers; both are presentation only and change no authority. The administrative roles System, Tenant and Organisation Administrator can never be hidden. The plan's slice S8 (rename with aliases) is retired, superseded by S4 and this decision.

### D7d Optional steps are configurable capabilities (S6)

Owner instruction of 2026-10-17 (slice S6; `decisions/ADR-0030-configurable-capabilities.md`, `docs/CAPABILITIES.md`): Legal Review, Register Authorisation, Variations, Renewals and Signature Requests are platform capabilities that a business can switch off with no code change. The order is the organisation's published choice, then its solution pack's defaults, then the platform default (all on). A capability is operating-model configuration, never authority: it grants, widens and removes no permission. Switching one off stops new starts only; work already under way can be finished. Incoherent combinations (a capability on without one it needs, a published workflow using one that is off, turning one off from under work in flight) are refused. Allocation of the five new permissions: `capabilities.read` and `capabilities.audit.read` to the Tenant Administrator, Organisation Administrator, Business Configuration Administrator and Auditor; `capabilities.draft` to the first three; `capabilities.publish` and `capabilities.rollback` to the Tenant and Organisation Administrators only. The Business Configuration Administrator can prepare a capability change but not publish it. The permission codes are permanent identifiers (D7c).

## D8 Decisions before the second business proof (S9 readiness, owner instruction 2026-10-17)

Source: `docs/S9_READINESS.md`, reviewed and approved by the owner. These bind the S9 build; they do not start it.

| ID | Decision |
|---|---|
| D8.1 (D1) | An **Administrative Change Order** records practical completion and similar completion facts on a locked contract. No role-less Defects Liability state is introduced as part of S9. |
| D8.2 (D2) | The pack-validation limitation for organisation-dependent documents (G11) is fixed **generically before S9**. The proof does not design around it. A valid pack must not be rejected at upload because an organisation-dependent artefact cannot be judged without an organisation. |
| D8.3 (D3) | **G1b is pre-S9 scope.** PRT-004 is to be satisfied in full, not in part: core field labels are terminology-driven. |
| D8.4 (D4) | **G2 is deferred to S10.** Non-Australian tax identifier formats are out of scope until S10. A pack may rename the identifier (G1a) but the eleven-digit format check remains. |
| D8.5 | Slice **S8 stays retired**. |
| D8.6 | Role codes, permission codes and approver-group identifiers are **permanent** (D7c). |
| D8.7 | **"Approval Authority"** is the neutral display label of the `Approver` role (the code `Approver` is unchanged). |
| D8.8 | The onboarding warning that printed a raw role code prints the role's label. |
| D8.9 | S9 uses: business **Harborline Construction**, pack key **construction-subcontracts**, locale **en-US**, currency **USD**, time zone **America/Chicago**. |
| D8.10 | The Construction Subcontracts pack ships as a **first-class pack** in `templates/packs`, alongside Blank and JBS Procurement. |
| D8.11 | **Sell-side Professional Services** remains the preferred candidate for the next proof business after S9. |

### How the D8 decisions were applied in S9 (2026-10-17; a record, not a new decision)

* D8.1 (no Defects Liability state): the lifecycle has ten states; practical completion is recorded by an Administrative Change Order (type `ADMINISTRATIVE`, Practical Completion Date field), with a single Commercial Manager approval step.
* D8.2, D8.3 (G11 and G1b done first): all eleven pack documents uploaded with only context-dependent warnings and publish in dependency order; every field label the pack renames is a terminology key.
* D8.4 (G2 deferred): no Harborline scenario enters a tax identifier; the pack renames the label (Tax Identifier) and the eleven-digit check is unchanged until S10.
* D8.9, D8.10: pack key `construction-subcontracts`, en-US, USD, America/Chicago; shipped in `templates/packs` as a first-class pack.
* D8.11: Professional Services was not started.
* S9 owner rules (kept): no construction-specific platform code; one generic fix was needed and is recorded as "S9-discovered generic fix F1" (`docs/PORTABILITY_AUDIT.md` section 0.8, ADR-0032); Legal Review and Renewals are disabled in the pack, Register Authorisation, Variations (presented as Change Orders) and Signature Requests are enabled; the notification policy states its content levels explicitly.

## D9 Regional defaults, locale neutrality and counterparty tax identifiers (S10, owner instruction 2026-10-17)

Source: owner instruction for slice S10; architecture in `decisions/ADR-0033-regional-settings-and-counterparty-identifiers.md`; behaviour in `docs/REGIONAL_SETTINGS.md` and `docs/TAX_IDENTIFIERS.md`. D8.4 (G2 deferred to S10) is delivered by these decisions.

| ID | Decision |
|---|---|
| D9.1 | **Organisation settings are the authority** for locale, currency and time zone. |
| D9.2 | A pack may **propose** defaults; the organisation may **override** them. |
| D9.3 | The generic platform must not default to en-AU, AUD, Australia/Brisbane, ABN or Australian address formats. |
| D9.4 | Blank-pack defaults are **neutral**; where no safe universal default exists the organisation must choose explicitly. |
| D9.5 | JBS may configure en-AU, AUD, Australia/Brisbane and ABN terminology and validation. |
| D9.6 | Harborline may configure en-US, USD, America/Chicago and US tax-identifier terminology and validation. |
| D9.7 | Tax identifiers are **configurable organisation and country identities**, not universally ABNs. |
| D9.8 | Existing ABN and Vendor Number values remain **readable and searchable**. |
| D9.9 | Existing database columns may remain where removal is disproportionately risky; generic UI, validation and services use neutral abstractions. |
| D9.10 | Existing **audit records and migration provenance are not rewritten**. |
| D9.11 | "Evergreen" is a stable contract-extension concept; its **label is terminology-driven**. |
| D9.12 | Notification dates use the organisation's locale and time zone for user-facing output; timestamps remain UTC. |
| D9.13 | API and machine exports may use ISO 8601 where documented. |
| D9.14 | Business-facing CSV and XLSX exports use the requesting organisation's configured formats unless a machine-readable format is explicitly selected. |
| D9.15 | The platform **does not claim legal validation** of tax identifiers; it validates configured structure and format only. |
| D9.16 | **No live government registry lookups** in S10. |

## D10 Operational resilience: backup, restore, validation, maintenance and leases (OPS-01 / OPS-02 foundation, owner instruction 2026-10-17)

Source: owner instruction for the Operational Resilience vertical slice; architecture in `decisions/ADR-0034-operational-resilience-backup-restore-maintenance-and-leases.md`; behaviour in `docs/BACKUP_AND_RESTORE.md`, `docs/OPERATIONS_RUNBOOK.md`, `docs/DISASTER_RECOVERY.md`, `docs/MAINTENANCE_MODE.md`, `docs/OPERATION_LEASES.md` and `docs/SUPPORT_BUNDLE.md`.

| ID | Decision |
|---|---|
| D10.1 | **SQL Server is authoritative** for operational backup and restore. |
| D10.2 | A backup includes every module schema and the migration history; tenant and organisation data; configuration drafts and releases; the audit chain; contracts and counterparties; document metadata; document content or an independently verified document-store backup; migration projects and provenance; installed solution packs; pack trust-store public keys and metadata; notifications and outbox data; and regional settings and identifier data. |
| D10.3 | **No plaintext secrets or private signing keys** are stored in backups. |
| D10.4 | Secret **references** may be included; secrets stay in the external secret provider. |
| D10.5 | Restore is **installation-level** in this slice. |
| D10.6 | Per-tenant backup and restore is **Future**. |
| D10.7 | Point-in-time recovery is **documented, not implemented** by application code. |
| D10.8 | **Docker plus external SQL Server plus persistent document storage** is the primary verified operational model. |
| D10.9 | IIS and Azure remain **Scaffolded or Not Verified** unless executed evidence exists. |
| D10.10 | A backup is **not successful until its integrity is checked**. |
| D10.11 | A restore is **not successful until the restored system passes validation**. |
| D10.12 | A restore is **never tested against the active production database**. |
| D10.13 | Backup files must be **encrypted or protected by the operator's approved storage process**. |
| D10.14 | The platform **never claims production readiness merely because backup tooling exists**. |
| D10.15 | Audit evidence **records backup and restore operations without storing sensitive backup contents**. |
| D10.16 | Existing Blank, JBS Procurement and Harborline Construction data **must survive a complete backup and restore cycle**. |

## D11 Upgrade safety, pre-flight validation, N-1 to N testing and rollback evidence (OPS-02, owner instruction 2026-10-18)

Source: owner instruction for OPS-02; architecture in `decisions/ADR-0035-upgrade-safety-preflight-and-rollback-by-restore.md`; behaviour in `docs/UPGRADES.md`, `docs/UPGRADE_RUNBOOK.md`, `docs/ROLLBACK_BY_RESTORE.md`. Production approval remains external governance.

| ID | Decision |
|---|---|
| D11.1 | Upgrades are **installation-level only**. A per-tenant upgrade does not exist. |
| D11.2 | **SQL Server is authoritative** for upgrade safety. SQLite (Demo and Test) is never evidence that an upgrade is safe. |
| D11.3 | Upgrade **testing runs only against a restored copy**, never the active production database. |
| D11.4 | A **verified backup is required** before a Production upgrade. |
| D11.5 | A Production upgrade **requires maintenance mode**. |
| D11.6 | Schema migrations are **forward-only**. |
| D11.7 | **Application downgrade after a schema upgrade is not assumed safe.** |
| D11.8 | **Rollback means restoring the verified pre-upgrade backup together with the matching application version.** The platform offers no downgrade operation. |
| D11.9 | No migration may **silently rewrite audit events, immutable configuration releases, signed pack bytes or pinned workflow identifiers**. |
| D11.10 | **Blank, JBS Procurement and Harborline Construction must survive an upgrade unchanged** (data, releases, packs, documents, wording, capabilities, regional settings, audit heads). |
| D11.11 | **Installed packs are checked for platform-version compatibility**, and **published configuration is checked for schema compatibility**, before an upgrade; the upgrade never mutates signed bytes, republishes or moves pins to make them fit. |
| D11.12 | **Upgrade readiness fails closed** on a critical issue. A warning may proceed only with the owner's explicit, recorded acceptance of that warning. |
| D11.13 | **Upgrade tooling is not production approval.** A Ready outcome, a passed rehearsal or retained evidence never declares production readiness. |
| D11.14 | A path the manifest does not list, or a database that is ahead of, partial against or inconsistent with this build, is **never described as safe**. |
| D11.15 | The N-1 fixture is a **frozen-schema fixture populated by the current services**, not a historical binary; this is stated wherever the rehearsal is reported. |

## D12 Operations dashboard and observability foundation (OPS-03, owner instruction 2026-10-18)

Source: owner instruction for OPS-03; architecture in `decisions/ADR-0036-operations-dashboard-and-observability.md`; scope, contradictions and the criteria matrix in `docs/OPS03_REQUIREMENTS_MATRIX.md`; behaviour in `docs/OPERATIONS_DASHBOARD.md`. Production approval remains external governance.

| ID | Decision |
|---|---|
| D12.1 | OPS-03 is implemented **as defined in `TASKS.md` and `requirements/12-operations/`**; no other operational feature is added because it seems useful. |
| D12.2 | The dashboard **reuses existing services** (readiness, history, leases, maintenance, backups, validation, migration inventory) and puts **no operational logic in page models**. |
| D12.3 | Every line is **Verified, Warning, Failed or Not Verified**, shown distinctly. **Not Verified is never presented or counted as Passed.** |
| D12.4 | Installation-level operators see **counts and times only** for tenant data, read per tenant inside that tenant's scope. No cross-tenant record, content or action. |
| D12.5 | Reading the dashboard needs `operations.read` in the installation tenant; technical detail needs `operations.validate`. The specification's `ops.manage` is read as the D10 permission set. |
| D12.6 | Critical failures are **never hidden or downgraded**. A failed restore or upgrade is **never retried automatically**, keeps maintenance mode on, and the dashboard offers a new attempt only where one is valid. |
| D12.7 | The **TLS certificate and secret expiry are Not Verified** (the platform cannot read them). Backup-encryption certificate expiry is Not Verified. Only pack-signing keys and the key ring are judged. |
| D12.8 | Observability is the standard .NET Meter and structured, correlation-carrying log events with a local reader. **OpenTelemetry, syslog and SIEM sinks are not built or verified** (INT-04) and are labelled Not Verified / Future. |
| D12.9 | A support bundle needs a recent sign-in and is audited; it carries a hashed manifest, size and entry limits and safe names; it contains no secret, token, connection string, key, contract or document content, callback payload or unapproved personal data. Completing the SEC-030/SEC-031 catalogue stays with OPS-04. |
| D12.10 | The readiness catalogue's checks that are not built (SET-03) are shown as **Not Verified: not built**. OPS-03 does not claim them. |
| D12.11 | Anonymous `/health/ready` returns the overall status only in Production; Demo and Test list checks (documented difference). |



