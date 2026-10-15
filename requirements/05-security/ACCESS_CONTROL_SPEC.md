# Access Control Specification

Normative: what must be true. The design that meets it is in `docs/SECURITY.md`. Approved by decision record DR-2026-10-15 (`requirements/01-authoritative/CONFIRMED_DECISIONS.md`): role model D1, scope intersection, read-all bound, tenant boundary. Tags: `[CONFIRMED]` / `[OPEN]` (awaiting a question answer) as in `requirements/README.md`.

## 1. Principles

1. Every access decision is made server-side. UI hiding is cosmetic. `[CONFIRMED]`
2. Default deny. A request is allowed only if every layer in section 3 allows it. `[CONFIRMED]`
3. Least privilege, including for administrators: System Administrator manages the installation but never reads business data and never automatically reveals secrets; reaching tenant data needs a reason-based elevation. Administrators who configure do not read or alter live contract records by virtue of configuring. `[CONFIRMED]`
4. A caller can never raise their own rights, nor grant a role or scope above their own. `[CONFIRMED]`
5. Authorisation outcomes, including denials, are audited. `[CONFIRMED]`

## 2. Scope model `[CONFIRMED]`

Every role assignment carries four elements:

| Element | Meaning | Values |
|---|---|---|
| **Role** | The permission bundle (`requirements/02-reference/role-permission-matrix.csv`) | The 12 roles |
| **Organisational scope** | Where the assignment applies | One or more organisations, optionally narrowed to business units and/or sites; installation or tenant scope for administrators |
| **Category scope** | Which contract categories the assignment covers | All categories, or a set of categories (a parent category covers its subcategories) |
| **Classification ceiling** | Highest classification the assignment may see | One classification level from the tenant's classification lookup |

A user may be restricted to specific organisations, business units, sites, contract categories and classifications. **Access to a record is the intersection of all of a user's applicable assigned scopes**: the record's organisation, business unit, site, category and classification must each fall inside the assignment that grants the permission. A user holding several assignments is evaluated per assignment; the permission is granted if at least one assignment grants it, and scopes of different assignments are never merged. **No role automatically grants access outside its assigned scope.**

### 2.1 Viewer kinds

Viewer is **not** a global read role. A Viewer assignment is created as one of:

| Kind | Scope required at creation |
|---|---|
| All Contracts Viewer | Organisation(s); all categories, business units and sites within them |
| Category Viewer | Organisation(s) and one or more categories |
| Business Unit Viewer | Organisation(s) and one or more business units |
| Site Viewer | Organisation(s) and one or more sites |
| Restricted Viewer | Any combination, plus a classification ceiling no higher than Confidential unless explicitly set (default ceiling: lowest non-public level) |

An assignment with no explicit organisational scope is invalid and is refused. `[CONFIRMED]`

### 2.2 Category Owner exception `[CONFIRMED]`

Category Owner reads **all** contracts in the organisational scope and classification ceiling of the assignment (category scope does not narrow *read*), and edits only contracts whose category currently has an active assignment to that user.

### 2.3 Other roles and scope

Legal, Auditor and Procurement Administrator are organisation-scoped and may be narrowed by the same elements. Approver and Backup Approver permissions are scoped to the assigned approvals plus the information needed to decide them. Division/Site Administrator is limited to its assigned divisions or sites for users, taxonomies and configuration. Tenant Administrator and System Administrator scopes are tenant and installation respectively and grant no contract-data access by themselves.

## 3. Evaluation layers (all must pass, in this order) `[CONFIRMED]`

| # | Layer | Rule |
|---|---|---|
| 1 | Authentication | Valid session or token from an enabled identity provider; demo identity rejected in Production |
| 2 | Tenant | Resource `tenantId` equals the caller's tenant; tenant derived from the authenticated principal or validated host mapping, never from client input |
| 3 | Scope intersection | The record's organisation, business unit, site and category (as relevant to the assignment) fall inside the assignment's scopes |
| 4 | Permission | The role-permission matrix grants the permission for that assignment, including delegated and scoped (`S`) grants |
| 5 | Relationship | For `S` grants, the scope rule holds: active category assignment, assigned approver or active backup, Division/Site scope, document parent visibility |
| 6 | Classification | The assignment's classification ceiling is at least the record's classification |
| 7 | Lifecycle and lock | The workflow state permits the action; the record is not locked, or an authorised Variation or open unlock covers the field |
| 8 | Segregation of duties | Configurable rules, default on: the submitter cannot approve their own submission; a Procurement Administrator cannot approve their own contract submission unless separately assigned an approval role and the approval policy permits |

Failures return 404 for object reads outside the caller's tenant or scope (no existence oracle) and 403 for permission failures inside scope.

## 4. "Read all" `[CONFIRMED]`

Category Owner, Legal, Auditor and Procurement Administrator "read all": every record in the caller's tenant, organisational scope and category scope **up to the classification ceiling**. Records above the ceiling are invisible in lists, counts, search, reports and exports, not merely masked.

## 5. Category ownership `[CONFIRMED]`

- Ownership follows the **active** assignment at the moment of the action; ending an assignment removes edit rights immediately; reassigning (by a Procurement Administrator, or a Division/Site Administrator within scope) grants them to the new owner. History is retained in `CategoryAssignment` and past audit events are not rewritten.
- A Category Owner edits only unlocked records in states that permit owner edits (`WORKFLOW_SPEC.md` section 1). A Category Owner cannot directly edit an Effective locked contract; changes go through a Variation.

## 6. Delegated administration `[CONFIRMED]`

- Levels: System, Tenant, Organisation, Business Configuration, and scoped Division/Site administrators.
- A delegated administrator can grant only roles, scopes and permissions that they hold, within their own scope.
- Every delegation is effective-dated, reasoned and audited. No inheritance from administrative roles to business data, approvals, dispositions or force-unlock.
- Last-administrator protection: the platform refuses to remove the last holder of `setup.run`/`system.configure` or the last Tenant Administrator of a tenant.

## 7. Elevated, emergency and support access `[CONFIRMED]`

- Requires a reason, a maximum duration (default 4 hours, configurable down), and is visible to tenant administrators while active and afterwards.
- Support access to tenant data is per-tenant, read-only by default, and never reveals secrets.
- Every action performed under elevation carries the elevation id in its audit event. Elevation ends automatically; extension requires a new reasoned grant.
- Force-unlock of a locked contract (`contracts.unlock_emergency`) is available to a Procurement Administrator only where the organisation has explicitly authorised it; it requires a reason and expiry; second-person confirmation is a policy setting (default on in Production) `[OPEN]` Q-05.

## 8. Record locks `[CONFIRMED]`

See `requirements/04-workflows/WORKFLOW_SPEC.md` sections 1, 5 and 7. The lock is enforced in the domain layer and by database constraints where practical.

## 9. Contract closure and disposition `[CONFIRMED]`

Category Owners initiate termination/end (`contracts.close`) and disposition requests (`disposition.request`); Procurement Administrators administer the process (`disposition.administer`); final disposition requires the configured disposition approver group (`disposition.approve`, held by Approver/Backup Approver where the approval matrix assigns the role). No ordinary business user can hard-delete records.

## 10. Service principals and external reporting

- Integration and reporting principals are non-human, tenant-bound, least-privilege, read-only where possible, and have no interactive login. `[CONFIRMED]`
- A reporting principal sees only curated views for its tenant, limited by the same scope elements (`requirements/07-integrations/REPORTING_QLIK_SPEC.md`).
- AI providers act through a principal that has no write permissions; its output is a proposal that a human must accept. `[CONFIRMED]`

## 11. Required tests (see `docs/TESTING.md`)

Authorisation matrix tests are generated from `role-permission-matrix.csv` (AUT-001). Negative tests exist for each layer (AUT-002 to AUT-009), including scope-intersection cases: a Category Viewer cannot read another category, a Site Viewer cannot read another site, an assignment with no organisational scope is refused, a Restricted Viewer cannot exceed its classification ceiling, and assignments are never merged.
