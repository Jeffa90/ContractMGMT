# 02 Reference

Added by repository review. This folder holds reference material that every other specification relies on.

| File | Content |
|---|---|
| `GLOSSARY.md` | Definitions of terms and scopes (tenant, organisation, legal entity, category owner, Registered Date, Variation, ...) |
| `permission-catalogue.csv` | Every permission code used by workflows, APIs and policies, with scope and sensitivity |
| `role-permission-matrix.csv` | Which role holds which permission, and whether the grant is global or scoped |
| `reference-lookups.csv` | Code lists used by the platform and the JBS reference pack (statuses, document types, duplicate strategies, match methods, ...) |

## Status convention

Statements in files added by repository review are tagged `[CONFIRMED]` or `[OPEN]`; in CSV files a `Basis` or `Status` column plays the same role (`Confirmed`/`Open`). See `requirements/README.md`. Open items are tracked in `docs/REPOSITORY_REVIEW.md` (`Q-nn`).

## Reading the role-permission matrix

- `Y` = granted across the user's tenant/organisation scope. `S` = granted only where the catalogue's `ScopeRule` holds (for example an active category assignment). Blank = not granted.
- The 12 columns are the 12 roles confirmed in DR-2026-10-15 D1 (`requirements/05-security/roles-and-permissions.csv`).
- The `Basis` column cites where each grant comes from: `D1` (the role definitions in the decision record), `A10 baseline` (approved review baseline where D1 is silent), `Master`, or a note that the mode or period is `[OPEN]`.
- A Viewer's `S` grant means the grant is limited to the assignment's scopes (Viewer kind, organisation, business unit, site, category and classification ceiling). See `ACCESS_CONTROL_SPEC.md` section 2.
- Administrative roles deliberately do **not** read contract content (least privilege). System Administrator never reads business data; reaching it requires `support.elevate`.

## Rules for the matrix and catalogue

1. A permission code in `status-transitions.csv`, an API operation, a policy or a UI action must exist in `permission-catalogue.csv`. A test enforces this (see `docs/TESTING.md`, test AUT-010).
2. A role in `roles-and-permissions.csv` must have a column in `role-permission-matrix.csv`.
3. A grant of `S` (scoped) must name its scope rule in the `ScopeRule` column of the catalogue.
4. Roles are *security* roles. Approver groups (for example "Group Head") are *assignments* of the `Approver` role used by approval matrices; they are not roles.
5. Permissions are checked server-side only (CLAUDE.md). UI visibility is a convenience, never a control.
