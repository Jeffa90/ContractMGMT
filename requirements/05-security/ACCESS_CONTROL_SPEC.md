# Access Control Specification

Added by repository review (GAP-10, GAP-17, CON-05, CON-14). Normative: what must be true. The design that meets it is in `docs/SECURITY.md`. Tags: `[CONFIRMED]` / `[PROPOSED]` as in `requirements/README.md`.

## 1. Principles

1. Every access decision is made server-side. UI hiding is cosmetic. `[CONFIRMED]`
2. Default deny. A request is allowed only if every layer in section 2 allows it. `[PROPOSED]`
3. Least privilege, including for administrators: System Administrator manages the installation but never reads business data without a reason-based elevation; administrators who configure do not automatically read contracts. `[CONFIRMED]` (roles file: no automatic secret reveal; Master: least privilege)
4. A caller can never raise their own rights, nor grant a role above their own. `[CONFIRMED]` (Master: privilege escalation)
5. Authorisation outcomes, including denials, are audited. `[CONFIRMED]`

## 2. Evaluation layers (all must pass, in this order) `[PROPOSED]`

| # | Layer | Rule |
|---|---|---|
| 1 | Authentication | Valid session or token from an enabled identity provider; demo identity rejected in Production |
| 2 | Tenant | Resource `tenantId` equals the caller's tenant. Tenant is derived from the authenticated principal or a validated host mapping, never from a client-supplied header or body |
| 3 | Organisation scope | Caller has membership in the resource's organisation (or a tenant-level role that permits cross-organisation access for the permission) |
| 4 | Permission | The role-permission matrix (`requirements/02-reference/role-permission-matrix.csv`) grants the permission, including delegated and scoped grants |
| 5 | Relationship | For `S` grants, the scope rule holds: active category assignment, assigned approver or active backup, Division/Site scope, document parent visibility |
| 6 | Classification | Caller's clearance is at least the record's classification. Documents inherit the contract's classification unless raised |
| 7 | Lifecycle and lock | The workflow state permits the action; the record is not locked, or an authorised Variation or an open unlock covers the field |
| 8 | Segregation of duties | Configurable rules, for example the submitter cannot approve (ASM-17) |

Failures return 404 for object reads outside the caller's tenant or organisation (no existence oracle) and 403 for permission failures inside scope. `[PROPOSED]`

## 3. "Read all" `[CONFIRMED]` principle, `[PROPOSED]` bound

Category owners, Legal, Auditor and Viewer "read all". "All" means every record in the caller's tenant and organisation scope **up to the caller's classification clearance** (ASM-07). Records above clearance are invisible in lists, counts, search, reports and exports, not merely masked.

## 4. Category ownership `[CONFIRMED]`

- Category owners read all and edit contracts only in categories they actively own.
- Ownership follows the **active** assignment at the moment of the action: ending an assignment immediately removes edit rights; a new assignment grants them. History is retained in `CategoryAssignment`.
- A Category Owner edits only unlocked records in states that permit owner edits (`WORKFLOW_SPEC.md` section 1).
- Reassigning a category does not rewrite past audit events.

## 5. Delegated administration `[CONFIRMED]`

- Levels: System, Tenant, Organisation, Business Configuration, and scoped Division/Site administrators.
- A delegated administrator can grant only roles, scopes and permissions that they themselves hold, within their own scope.
- Every delegation is effective-dated, reasoned and audited. There is no implicit inheritance from Tenant Administrator to business data.
- Last-administrator protection: the platform refuses to remove the last holder of `setup.run`/`system.configure` or the last Tenant Administrator of a tenant. `[PROPOSED]`

## 6. Elevated, emergency and support access `[CONFIRMED]` principle, `[PROPOSED]` mechanics

- Requires a reason, a maximum duration (default 4 hours, configurable down), and is visible to tenant administrators while active and afterwards.
- Support access to tenant data is per-tenant, read-only by default, and never reveals secrets.
- Every action performed under elevation carries the elevation id in its audit event.
- Elevation ends automatically; extension requires a new reasoned grant.

## 7. Record locks `[CONFIRMED]`

See `requirements/04-workflows/WORKFLOW_SPEC.md` sections 1, 5 and 7. The lock is enforced in the domain layer and by database constraints where practical, so a code path that bypasses an endpoint cannot bypass the lock.

## 8. Service principals and external reporting

- Integration and reporting principals are non-human, tenant-bound, least-privilege, read-only where possible, and have no interactive login. `[PROPOSED]`
- A reporting principal sees only curated views for its tenant (`requirements/07-integrations/REPORTING_QLIK_SPEC.md`).
- AI providers act through a principal that has no write permissions; its output is a proposal that a human must accept. `[CONFIRMED]` (AI never approves, signs, registers, bypasses access)

## 9. Required tests (see `docs/TESTING.md`)

Authorisation matrix tests are generated from `role-permission-matrix.csv` so that the file and the behaviour cannot drift (test AUT-001). Negative tests exist for each layer in section 2 (AUT-002 to AUT-009).
