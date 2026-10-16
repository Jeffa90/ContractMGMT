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
