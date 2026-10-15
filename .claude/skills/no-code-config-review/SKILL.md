---
name: no-code-config-review
description: Verify that fields, forms, rules, workflows, approval matrices, lookups, terminology, branding and notification templates are configurable without code, and that configuration has draft, validation, preview, simulation, publish, versioning, rollback and audit with only closed vocabularies. Use after any change to the Configuration, Rules or Workflow modules or the Configuration Studio.
---

# No-Code Configuration Review

## Read first
1. `requirements/01-authoritative/PORTABILITY_AND_NO_CODE.md` and Master "No-code Configuration Studio"
2. `requirements/08-portability/CONFIGURATION_STUDIO_SPEC.md`, `CONFIGURATION_LIFECYCLE_SPEC.md`
3. `docs/DOMAIN_MODEL.md` section 5 (INV-20 to INV-28), ADR-0002 (in `decisions/`), `ADR-0008`, `ADR-0009`
4. `docs/TESTING.md` section 9 (NCD-001 to NCD-015)

## Procedure
1. As a **Business Configuration Administrator only** (no SQL, no file access), complete: add a field, place it on a form, add a rule, change a workflow transition guard, add an approval-matrix row, rename terminology, change branding, edit a notification template, publish, roll back. If any step needs a developer, it is a Fail (PRT-010).
2. Compare the database schema hash before and after creating 50 fields, 5 forms, 10 rules, a workflow and a matrix (NCD-001, PRT-003). Any change is a Fail.
3. Run the checklist.

## Checklist
- **Closed vocabularies (NCD-003, ARC-007, INV-26):** unknown operators, actions, functions and node types are rejected at validation and at runtime; no scripting engines, expression compilation, dynamic SQL, template logic, macros, custom CSS/JS/HTML anywhere in configuration or packages.
- **Limits:** condition depth and node counts, rule pass cap of 3 (NCD-006), calculated-display limits (NCD-010), pattern length and timeout with non-backtracking engine (NCD-004), definition size limits.
- **Lifecycle (NCD-008, NCD-009):** draft → validate → preview → simulate → publish → version history/diff → rollback → audit; published versions immutable (INV-20); a release validates as a whole (INV-21); rollback publishes a new release equal to an earlier one (INV-22); stale drafts require rebase (INV-23); packages create drafts only (INV-28).
- **Preview and simulation (NCD-013/014):** perform no writes; simulation uses the real access evaluator, rules engine, workflow engine and approval resolver and matches production decisions.
- **Pinning (WFL-007, INV-24):** in-flight workflow and approval versions do not change when new releases publish; safe-change rules block unsafe edits (NCD-007, INV-25, INV-27).
- **Workflow validator (WFL-003):** rejects dead ends, unreachable states, unsafe cycles and unknown permission or guard codes. Run it on the original transition CSV alone: it must report `Legal Rework Required` and `Variation in Progress`.
- **Approval matrix (APR-001 to APR-010):** half-open ranges, gap/overlap/tie detection, fail closed on no match, no implicit currency conversion, backup and delegation recorded. Confirm the **JBS pack** uses only register authorisation by the Group Head of Procurement or backup and does not infer financial approval roles; the generic value-based matrix is a labelled synthetic demonstration.
- **Field types (NCD-012):** all 17 types validate, store, search and project; currency stores a currency code; sensitive fields are masked and excluded from audit values and AI.
- **Output safety (NCD-002, SEC-005, SEC-017):** labels, help, terminology and templates are encoded; template placeholders are checked against the event allowlist (NCD-005); SVG branding sanitised.
- **Accessibility of designers (A11Y-008):** keyboard operation, move up/down controls, outline equivalent.
- **Audit:** every configuration action emits an event carrying release id and definition codes.

## Severity
Critical: arbitrary code path, unaudited publish. High: schema change per field, preview writing data, in-flight behaviour change. Medium: missing validation. Low: UX gaps.

## Output
Findings table and a record of the Business-Configuration-Administrator-only walkthrough.
