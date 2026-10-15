---
name: contract-domain-review
description: Review contract lifecycle behaviour against the confirmed domain rules: category ownership, Legal read-all and multi-round review, register authorisation by Group Head or backup, Registered Date separation, Effective locking, Variations, renewals, closure and disposition, reminders and archive-before-delete. Use after any change to contracts, workflow, approvals, Variations, locks, reminders or retention.
---

# Contract Domain Review

Verify that the implementation does what the **confirmed** requirements say, not what the code happens to do. Never change a requirement to match defective code; record a finding instead.

## Read first
1. `requirements/01-authoritative/CONFIRMED_DECISIONS.md` (including the 2026-10-15 decision record)
2. `requirements/04-workflows/status-transitions.csv`, `status-transitions-supplemental.csv`, `guard-vocabulary.csv`, `WORKFLOW_SPEC.md`
3. `requirements/03-data-dictionary/contracts.csv`, `contracts-extended.csv`, `lifecycle-records.csv`
4. `requirements/02-reference/role-permission-matrix.csv`, `permission-catalogue.csv`
5. `docs/DOMAIN_MODEL.md` (invariants `INV-nn`), `docs/TESTING.md` sections 6 and 8

## Procedure
1. List the code paths that change `status`, `recordLocked`, `registeredDate`, `registeredBy`, Variations, unlocks, reminders and disposition. For each, find the test IDs that cover it. A path with no test is a finding.
2. Walk every row of the transition CSVs through the engine (allowed actor, denied actor, failed guard). Confirm the engine is data-driven: no state or role names in domain code.
3. Run the checklist below. Record each result as Pass / Fail / Not verified (with reason).
4. Fix safe, confirmed defects and add regression tests. Leave design questions as findings.

## Checklist
- **Status integrity (INV-02, WFL-006):** `status` has no public setter; API, import, rule actions and mass assignment cannot set it.
- **Ownership (INV-09, AUT-005):** edit requires an *active* category assignment at the time of the action; an ended assignment grants nothing; Category Owners read all (within organisation and classification scope) but edit assigned categories only.
- **Legal (WFL-008):** Legal reads all, creates and edits rounds, comments, outcomes and mark-ups; closes a round as Cleared or Returned for rework; closed rounds are immutable; Legal cannot perform register authorisation unless also assigned the Approver role; Legal cannot change locked commercial metadata outside an authorised workflow.
- **Register authorisation (WFL-004, INV-10):** performed after execution by the configured approver (Group Head of Procurement or backup in the JBS pack); requires an executed copy; the actual acting individual and decision are stored (`onBehalfOf` when a backup acts); the submitter cannot approve their own submission unless policy and role allow it; approver cannot silently edit the submitted record.
- **Registered Date (INV-05, WFL-005):** stamped by successful register authorisation, independent of Effective Date, never user-editable; the only exception is an authorised legacy migration that retains source provenance (see `migration-review`).
- **Locking (INV-03, INV-04):** Effective, Variation in Progress, Ended and Disposed-Archived are locked at the domain layer for every role; only an applied Variation or an audited, reasoned, time-boxed unlock changes locked fields; auto-relock works.
- **Variations (WFL-009, INV-07/08):** at most one open per contract; only `Variation only` fields change; apply, cancel and reject all return the parent to Effective; Registered Date is not re-stamped.
- **Renewals, termination, disposition:** Category Owners can *initiate* renewal, termination/end and disposition requests; Procurement Administrators administer the process; only the configured disposition approver finally approves; no user-facing hard delete exists (RET-005); archive precedes any deletion (RET-002).
- **Reminders (WFL-010, NTF-004):** Evergreen and Critical Notice offsets are configuration; due times use organisation time zone; dedupe keys prevent duplicates; a Variation recomputes unsent reminders.
- **Agreement Reference (INV-01):** unique per tenant; organisation numbering rule applied; original source reference preserved separately for migrated records.
- **Audit (INV-16):** every transition writes audit and outbox atomically.

## Severity
Critical: lock/ownership/approval bypass or tenant data exposure. High: wrong actor can transition, missing audit, Registered Date misuse. Medium: wrong guard message, reminder mistakes. Low: naming or documentation.

## Output
A table of findings (ID, severity, requirement reference, evidence, test ID, status), the tests added, and residual limitations. Do not declare production readiness; that is external governance.
