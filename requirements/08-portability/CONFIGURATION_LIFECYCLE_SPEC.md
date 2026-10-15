# Configuration Lifecycle Specification

Master: "All configuration is versioned, effective-dated where relevant, audited and supports draft, validation, preview, simulation, publish and rollback." CLAUDE.md: "Configuration requires draft, validation, preview/simulation, publish, version history, rollback and audit." This file defines the mechanics (GAP-14, RISK-06, RISK-19). `[CONFIRMED]` lifecycle, `[CONFIRMED]` mechanics.

## 1. Configuration definitions

A *definition* is one versioned unit of configuration, identified by `(scope, kind, code)`. Kinds: `Field`, `Form`, `RuleSet`, `Workflow`, `ApprovalMatrix`, `Lookup`, `Terminology`, `Branding`, `FeatureFlags`, `NotificationTemplate`, `DocumentType`, `DocumentTemplate`, `RetentionPolicy`, `ReminderPolicy`, `RoleTemplate`, `IntegrationSettings`. `code` is stable and never reused. `scope` is tenant or organisation.

## 2. States

`Draft` → `Validated` → `Published` → `Superseded`. A draft that fails validation stays `Draft`. Published versions are **immutable**; superseded versions remain readable forever. `Discarded` drafts are retained for audit.

## 3. Operations

| Operation | Rules |
|---|---|
| Create draft | From the current published version or blank; records the **base release id**; permission `config.draft` |
| Edit | Autosave with optimistic concurrency; two administrators editing the same draft see a conflict, not a silent overwrite |
| Validate | Structural (schema), semantic (the checks in `CONFIGURATION_STUDIO_SPEC.md` section 10), cross-definition (references resolve), safety (closed vocabularies only, size limits), compatibility (no change that invalidates stored data without a migration plan). Results are listed, each with severity, location and plain-language fix |
| Preview | Renders forms, pages, notifications and branding with **synthetic** records. Never reads production values and never writes |
| Simulate | Evaluates access, rules, workflow transitions and approval resolution against synthetic or hand-built records and users, and shows the decision trace. Never writes. Simulation of "access" uses the same evaluator as production |
| Review (optional) | Tenant setting "require second approver to publish" (default on in Production) `[CONFIRMED]` |
| Publish | Atomically creates a **Configuration Release**: a set of definition versions that validate together. Optionally effective-dated. Permission `config.publish`. Fails if the base release has changed (three-way comparison; the user must rebase) |
| Version history | Each release lists author, publisher, time, reason, diff (human-readable) and validation/simulation evidence |
| Rollback | Creates a **new** release whose definitions equal an earlier release. History is never rewritten. Blocked, with explanation, where stored data would become invalid (for example removing a field type that has values) |
| Audit | Every operation emits an audit event carrying the release id and definition codes |

## 4. Runtime resolution (which version applies when)

| Definition | Resolved at | Why |
|---|---|---|
| Field, Form, Terminology, Branding, Lookup values, FeatureFlags, NotificationTemplate | The current release at the time of use | Users see current layout and labels |
| Workflow, ApprovalMatrix | **Pinned** to the version in force when the record (or Variation) started the workflow | In-flight records must not change rules mid-flight |
| RuleSet | The current release at evaluation time, recorded in the audit event | Rules guide, they do not change history |
| MigrationBatch | The release at batch creation, captured on the batch | Reproducible mock cycles |
| RetentionPolicy, ReminderPolicy | The current release when computed; computed schedules are recomputed on relevant changes | |

Pinned versions are retained as long as any instance references them. Changing the pinned version of an in-flight record requires an approved *migration plan* (state mapping) and is audited.

## 5. Safe-change rules for in-use definitions `[CONFIRMED]`

- Workflow: cannot remove a state or transition that in-flight instances occupy or can take; can add states; can change guards and notifications for new instances.
- Fields: cannot change a type once values exist (create a new field and migrate); cannot delete a field with values (deprecate: hide and stop collection); can add choices; removing a choice in use needs a remap.
- Lookups: values are deactivated, never deleted.
- Approval matrices: new versions apply to submissions after the effective time; in-flight submissions keep their pinned version.

## 6. Scope inheritance

Resolution order: organisation release, then tenant release, then pack defaults. A release can mark a definition `inherit` (use the parent) or provide an override.

## 7. Limits and robustness `[CONFIRMED]`

Maximum definition size, definitions per kind, fields per entity (default 200), rules per rule set, workflow states, and package size are configuration with safe defaults. A broken release can be disabled quickly by a feature flag or reverted by rollback; the platform always retains the last release that passed validation.
