# Configuration Studio Specification

Master gives one paragraph per designer; this file gives the semantics needed to build and test them (GAP-13, GAP-14, RISK-04). Lifecycle mechanics are in `CONFIGURATION_LIFECYCLE_SPEC.md`. `[CONFIRMED]` capabilities from Master "No-code Configuration Studio"; `[PROPOSED]` detail.

## 1. Principles

1. **Closed vocabularies.** Every configurable behaviour is expressed in a finite catalogue of typed operators, actions and functions. There is no free-text expression, script, SQL, JavaScript, regex-with-backtracking, template logic, macro, or custom CSS. `[CONFIRMED]` (CLAUDE.md)
2. **Data, not schema.** Creating fields, forms, rules, workflows or lookups never creates a database object (no per-field migration). `[CONFIRMED]`
3. **Stable codes.** Every definition and every state, field, choice and action has an immutable code used by APIs, packages, rules and reports. Labels are localised and aliasable. `[PROPOSED]`
4. **Explainable.** Rules, workflows and approvals produce a decision trace that the simulator displays and the audit log can reference.
5. **Accessible.** Each designer has keyboard-only operation and a structured text outline (see `requirements/06-design/ACCESSIBILITY_AND_LOCALISATION.md` A-08).

## 2. Field definitions

Types are those in `requirements/03-data-dictionary/custom-field-types.csv` (17 codes). A field definition has:

| Property | Notes |
|---|---|
| `code` | `^[a-z][A-Za-z0-9]{1,63}$`, immutable, unique per (scope, entity) |
| `appliesTo` | Entity types: Contract, Supplier, Document, Variation |
| `type` | One of the 17 type codes |
| `label`, `help`, `placeholder` | Localised plain text (no markup in labels; help allows a restricted safe markup: paragraphs, emphasis, links to https) |
| `required` | Static, or "required when" a condition tree is true, or required in named states |
| `default` | Typed literal or one of an allowlisted set of functions (`today`, `now`, `currentUser`) |
| `validation` | By type: min/max, min/max length, decimal places, allowed values, date windows (relative days), email/URL syntax, optional pattern (non-backtracking engine, maximum 200 characters, 50 ms timeout), and unique-within-scope flag |
| `choices` | Inline list (code, label, active) or a reference to a Lookup |
| `currencyCode` | For `CURRENCY`: fixed code, or field reference to a currency field |
| `sensitivity` | None, Sensitive (masked below clearance; excluded from audit values and AI) |
| `classification` | Optional default classification for the value |
| `searchable`, `reportable` | Flags that include the field in search and in reporting projections. They do not create indexes per field |
| `visibility`, `editability` | Conditions by role, state, contract type and rule |
| `editableByVariation` | Whether a locked record may change it through a Variation |
| `calculation` | Only for `CALCULATED_DISPLAY` (section 9) |
| `status` | Active or Deprecated (hidden, no new values, existing values retained) |

## 3. Forms and pages

A form is a tree: **page → tab → section → field group → field | related list | action button | static help**. Properties per node: label, help, conditional visibility, conditional required, read-only condition, column span at breakpoints (responsive grid, 1/2/3 columns), and role/state visibility. Related lists show child records (documents, Legal rounds, approvals, Variations) with configurable columns. Action buttons are limited to workflow transitions and a fixed set of platform actions (upload, export view, print view); they never call arbitrary URLs. Form layouts reference fields by code; unknown references fail validation. Different forms may apply by contract type and state.

## 4. Rules designer

**Structure.** A rule set contains rules; each has an id, name, priority (integer), enabled flag, `appliesTo` entity types, `trigger` (`OnLoad`, `OnChange(field)`, `OnSubmit`, `OnTransition(code)`), a **condition tree**, and a list of **actions**.

**Condition tree.** AND/OR groups nested to depth 5, at most 50 leaf comparisons. A comparison is `(left operand, operator, right operand)`. Operands: a field code, a system value (`record.state`, `record.category`, `user.role`, `today`), a literal, or a lookup value. Operators by type:

| Type family | Operators |
|---|---|
| Any | `Equals`, `NotEquals`, `IsEmpty`, `IsNotEmpty`, `In`, `NotIn` |
| Text | `Contains`, `StartsWith`, `EndsWith`, `MatchesPattern` (safe pattern engine, limits as above) |
| Number/currency/percentage | `GreaterThan`, `GreaterOrEqual`, `LessThan`, `LessOrEqual`, `Between` |
| Date/datetime | `Before`, `After`, `OnOrBefore`, `OnOrAfter`, `WithinNextDays`, `WithinLastDays` |
| Multi-choice | `ContainsAny`, `ContainsAll` |

**Actions** `[CONFIRMED]` list: set value; require / show / hide / make read-only a field; assign owner / assign group; require Legal review; select workflow; select approval matrix; create notification; set classification; block transition with an explanation. Each action has typed parameters (for example `SetValue(field, literal | fieldCopy | dateAddDays)`).

**Evaluation.** Rules run in ascending priority then id order, deterministically. Actions that change a value cause a re-evaluation pass, at most 3 passes; a rule set that does not stabilise is a validation error at publish (found by the simulator) and a safe runtime stop (the transition is blocked with an explanation). Two enabled rules that set the same target with the same priority and overlapping conditions are a validation warning. A trace of fired rules is available to the simulator and recorded with the audit event of any resulting change.

## 5. Workflow designer

Elements: **states** (code, label, category `Start|Active|Rework|Terminal`, lock behaviour `Unlocked|ContentFrozen|Locked`), **transitions** (code, from, to, permission, guards, required fields, required documents, comment required, notifications, side-effect actions from a fixed catalogue such as `StampRegisteredDate`, `SetLock`, `StartVariation`, `GenerateReminders`), **steps** (review/approval steps with model `Sequential|Parallel|AllResponses|FirstResponse`, approver group, backup, timeout, escalation), **timeouts and escalations**, **rework loops**, and **delegation/backup** windows.

**Publish-time validation** `[PROPOSED]` (workflow checks):

1. Exactly one `Start` state; at least one `Terminal` state.
2. Every state is reachable from `Start`; every non-terminal state has at least one outgoing transition (no dead ends, the CON-04 defect class).
3. Cycles are allowed only through states categorised `Rework` and must contain an exit path.
4. Every permission code exists in the permission catalogue; every guard code exists in the guard vocabulary; every referenced field, document type, notification template and approver group exists.
5. Locking: a `Locked` state's outgoing transitions are restricted to the lock-safe set (Variation, End, Archive, Apply/Cancel variation); no transition may reach an unlocked state from a locked state except through the Variation return path.
6. Timeouts have escalation targets; parallel steps have a defined completion rule.
7. For an in-use definition, the safe-change rules in `CONFIGURATION_LIFECYCLE_SPEC.md` section 5.

The simulator walks a synthetic record through a path and shows allowed/blocked reasons at each step.

## 6. Approval matrix designer

An approval matrix selects **who must approve** a submission. It does not contain people; it names *approver groups* whose membership is managed in identity (CON-02, ASM-08).

**Definition.**

| Property | Notes |
|---|---|
| `name`, `code`, `version` | Versioned like any definition |
| `currency` | ISO 4217 code; values are compared in this currency |
| `dimensions` | Any of: value range, category, contract type, legal entity, business unit, risk level, classification |
| `rows` | Each row: conditions on dimensions, priority, and an ordered list of **steps** |
| `step` | `{ key, approverGroup, mode: Sequential | Parallel | FirstResponse, backupGroup, timeoutDays, escalateTo }` |
| `effectiveFrom`, `effectiveTo` | A matrix version is applicable to submissions made within its window |
| `exceptions` | Rows with higher priority that add or replace steps (for example, a named category always needs an additional step) |

**Semantics.**

1. **Value ranges are half-open: `[minimum, maximum)`**, with a null maximum meaning unbounded. The sample `demo-approval-matrix.json` uses inclusive upper bounds `99999.99` and a lower bound `100000`; it normalises to `[0, 100000)`, `[100000, 500000)`, `[500000, ∞)`. Validation reports gaps and overlaps among rows that share all other dimension values. `[PROPOSED]`
2. **Currency.** No implicit currency conversion. If the contract currency differs from the matrix currency the matrix may include a versioned, effective-dated rate table; otherwise resolution fails closed with "no matching matrix row" and notifies the Procurement Administrator. A silent default is never used.
3. **Resolution.** Among matching rows the highest priority wins; ties are a validation error at publish. Exceptions apply after the base row. No match means the submission is **blocked with an explanation** (fail closed).
4. **Models.** `Sequential` completes steps in order; `Parallel` requires all; `FirstResponse` is satisfied by the first valid response from the group or its backup (the Confirmed Decision about backup approvers).
5. **Segregation of duties.** The submitter cannot act on their own submission (ASM-17, configurable).
6. **Delegation.** A group member may delegate for a bounded window to another member of the same group or the backup group; delegation is audited and shows on the approval record as "on behalf of".
7. **Pinning.** The resolved steps and the matrix version are stored with the submission (`ApprovalRecord.matrixVersionId`). Later matrix versions do not change in-flight submissions.
8. **JBS reference.** Single tier: approver group `GroupHead`, `FirstResponse` with `BackupApprover` as backup. The three-tier AUD sample is a generic simulator fixture.
9. **Simulation.** Input: sample contract attributes. Output: matching row, resolved steps and current group members, with the decision trace.

## 7. Lookups, taxonomy, terminology, feature flags, branding

- **Lookups and taxonomies:** hierarchical (maximum depth 3), values with immutable codes, effective dating, activation instead of deletion, import from CSV/XLSX with dry run.
- **Terminology:** `(concept code, culture) → singular, plural` aliases. Concept codes are platform-defined (for example `contract`, `supplier`, `category`, `variation`).
- **Feature flags:** key, description, default, scope (tenant or organisation), dependency list, and risk level. Flags enable modules (LegalReview, Variations, QlikExternalReporting, CriticalNotice, AI assistance). Flags can only enable code that exists; they cannot introduce behaviour.
- **Branding:** product name, logo (PNG/JPEG; SVG only after sanitisation to a safe subset or rasterisation), colour tokens (checked for AA contrast against text), email header/footer text. **No custom CSS, JavaScript, or HTML.**

## 8. Notification and document templates

Notification templates: see `requirements/07-integrations/NOTIFICATIONS_SPEC.md` section 4 (allowlisted `{{placeholders}}` only). Document templates (authorised templates, Master): word-processing files with a declared merge-field set from the allowlist, no macros (macro-enabled formats are rejected), merged by the platform into a new document version. Clauses designated *controlled T&Cs* are locked in the template and cannot be altered by users or AI.

## 9. Calculated-display fields

Read-only values derived from other fields of the same record using a closed function set: arithmetic on numbers, `concat`, `ifEmpty`, `dateAddDays`, `dateDiffDays`, `format(culture)`, and simple lookups of the record's own references (for example category name). Maximum 30 nodes. No cross-record queries, no loops, no user-defined functions. The value is computed on read and, when `reportable`, materialised into the reporting projection; it is never the source of truth.

## 10. Publish-time validation catalogue `[PROPOSED]`

| Code | Check |
|---|---|
| V-001 | Schema valid for each definition kind |
| V-002 | All referenced codes resolve (fields, lookups, groups, templates, permissions, guards, document types) |
| V-003 | No use of anything outside the closed vocabularies |
| V-004 | Workflow checks in section 5 |
| V-005 | Approval-matrix gaps, overlaps and ties (section 6) |
| V-006 | Rule priority conflicts and non-terminating rule sets (section 4) |
| V-007 | Field changes that would invalidate stored data (type change, removed choice in use, new required field without default on existing records is a warning with a backfill plan) |
| V-008 | Limits: sizes, counts, depth |
| V-009 | Accessibility checks on branding colours and form structure |
| V-010 | Terminology completeness for the default culture |
| V-011 | Template placeholders belong to the event's allowlist |
| V-012 | Security: pattern engine limits, no executable file types in assets |
