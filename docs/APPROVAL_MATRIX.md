# Approval matrix (WFE-02)

Status: **Implemented** (module `Ecp.Approvals`, tier 6). Decisions: ADR-0022. This page is for business administrators and implementers. JBS keeps its confirmed model and does **not** use a matrix by default.

## What it is for

A matrix answers, for a submission: which steps are needed, who can perform each, in what order, and why. It is configuration (a document with draft, validation, publication, versions and rollback), never code.

## JBS default (unchanged)

DocuSign handles Delegation of Authority. Register approval is separate: the Group Head of Procurement or a backup decides, first response wins. A workflow that does not set `settings.approval.matrix` runs this model exactly as before. Nothing in the JBS pack names a matrix.

## The document

```json
{ "schemaVersion": 1, "matrices": [ {
  "code": "value-tiers", "name": "Value tiers", "currency": "AUD",
  "rates": [ { "currency": "USD", "rate": 1.5, "effectiveFrom": "2026-01-01" } ],
  "rows": [
    { "code": "small", "kind": "Base", "priority": 100, "flow": "Sequential",
      "when": { "valueMax": 100000 },
      "steps": [ { "key": "manager", "display": "Manager", "approverGroup": "Managers", "backupGroup": "ManagerBackups", "mode": "FirstResponse" } ] },
    { "code": "large", "kind": "Base", "priority": 100, "flow": "Sequential",
      "when": { "valueMin": 100000 },
      "steps": [ { "key": "manager", "display": "Manager", "approverGroup": "Managers", "mode": "FirstResponse" },
                { "key": "director", "display": "Director", "approverGroup": "Directors", "mode": "AllRequired" } ] } ] } ] }
```

Samples: `sample-data/synthetic-generic-value-approval-matrix.json` and `sample-data/synthetic-approval-matrix-advanced.json` (synthetic data only).

* **Conditions** (`when`): `valueMin`/`valueMax` (half-open: min included, max excluded), `categories` (+`includeSubcategories`), `contractTypes`, `legalEntities`, `businessUnits`, `sites`, `classifications`, `riskLevels`, `supplierTypes` and `custom` conditions (`Equals`, `NotEquals`, `In`, `NotIn`, `AtLeast`, `LessThan`).
* **Effective dates**: on the matrix, each row and each rate; inclusive; judged in the organisation's time zone, stored in UTC.
* **Precedence**: the highest priority among matching Base rows wins; equal priority between rows that can match the same record is a publication error and, if it somehow occurs at run time, a block. **Exception** rows add steps after the base or replace them; highest priority first.
* **Flows**: `Sequential` (one stage per step) or `Parallel` (all steps open together). **Modes**: `FirstResponse` (one eligible person decides) or `AllRequired` (every named primary, covered by a delegate where allowed). A rejection anywhere ends the approval.
* **Backups**: `backupGroup` and the Backup Approver role count where `allowBackup` is true (default for FirstResponse; off for AllRequired).
* **Currency**: a different currency needs a dated rate entry; otherwise the submission is blocked. There is no implicit conversion.
* `timeoutDays` and `escalateTo` are recorded data only (no timers or messages yet).

## Who counts as an approver

Group members are checked by the access layer for the record itself. Excluded, with reasons: the submitter, disabled or expired assignments, people outside the organisation, business unit or site scope, below the classification ceiling, and privileged access without a grant. A person with no right to open the record never approves it.

## Delegation

An approver delegates their own authority for a group to another member of the group (or its backup group) for up to 90 days from *Approval matrices → Delegations*. Not to oneself, not transitive, never circular, always with a reason; ended by the delegator or an administrator. Responses show "on behalf of".

## Pages

| Page | Who | What |
|---|---|---|
| `/Admin/Approvals` | `approvals.matrix.read` | Matrices in force, rows and steps in plain language, workflows using them |
| `/Admin/Approvals/Simulate` | `approvals.simulate` | Describe a sample submission, choose in-force matrix, a numbered release or your draft and any date; see steps, people, why each person is in or out, and the resolution trace |
| `/Admin/Approvals/Delegations` | `approvals.delegate` / `approvals.matrix.read` | Create and end delegations; administrators see the organisation's |
| `/Admin/Approvals/History` | `approvals.matrix.read` (+ version history and audit permissions for the lists) | Releases and audit events (publications, simulations, delegations, blocked submissions) |
| `/Approvals/Details/{id}` | approvers; matrix readers see more | The plan: steps, responses, who is outstanding; matrix readers also see the trace and reasons |

Editing uses *Workflow configuration* (draft, validate, publish, rollback). To use a matrix, publish a workflow whose `settings.approval.matrix` names it; the workflow cannot be published if the matrix does not exist, and the matrix cannot be removed while a workflow names it.

## Facts the matrix is judged on

One `ApprovalFactService` builds the facts for a live submission (and a Variation as it would be once applied) and for the simulator, so they cannot drift: value and currency, category path, business unit, site, **legal entity, contract type, risk level, supplier type**, classification, and the additional fields an administrator has marked **usable in approvals**. The simulator's "use the facts of a contract" fills the form from a real contract through the same service (`approvals.simulate` and the right to read the contract). A condition on a fact the platform does not supply, or on an additional field that is not usable in approvals, is refused at publication (`fact_unavailable`). Tests: `ApprovalFactScenarios` (live and simulated plans agree for the same contract; only usable fields feed a matrix).

## Audit

`approval.matrix.published` (with a row-by-row change summary), `approval.simulated`, `approval.resolution_blocked`, `approval.step_approved`, `approval.delegation.created`/`revoked`, and the usual approval decision events.

## Limits (honest)

Escalation timers and messages; configurable segregation of duties beyond the submitter; re-resolving an in-flight plan when a named primary leaves; a visual designer (STU-06). Contract type, risk level, supplier type, legal entity and the additional fields marked usable in approvals now reach the resolver (see above); a multiple-choice, long-text, web-address, file-reference or calculated field cannot be offered to a matrix. The allocation of the four approval permissions is owner-confirmed (CONFIRMED_DECISIONS D6); Procurement Administrators hold `approvals.delegate.administer` only by explicit assignment, and no explicit per-user permission grant mechanism exists yet (Future).
