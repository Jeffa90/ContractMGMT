# 02 Reference

Added by repository review. This folder holds reference material that every other specification relies on.

| File | Content |
|---|---|
| `GLOSSARY.md` | Definitions of terms and scopes (tenant, organisation, legal entity, category owner, Registered Date, Variation, ...) |
| `permission-catalogue.csv` | Every permission code used by workflows, APIs and policies, with scope and sensitivity |
| `role-permission-matrix.csv` | Which role holds which permission, and whether the grant is global or scoped |
| `reference-lookups.csv` | Code lists used by the platform and the JBS reference pack (statuses, document types, duplicate strategies, match methods, ...) |

## Status convention

Every statement in files added by repository review is tagged `[CONFIRMED]` or `[PROPOSED]`; in CSV files a `Basis` or `Status` column plays the same role. See `requirements/README.md` for what the tags mean. Proposals are tracked in `docs/REPOSITORY_REVIEW.md` (`ASM-nn`, `Q-nn`) and become `[CONFIRMED]` only when the business owner confirms them.

## Reading the role-permission matrix

- `Y` = granted across the user's tenant/organisation scope. `S` = granted only where the catalogue's `ScopeRule` holds (for example an active category assignment). Blank = not granted.
- Columns `SystemAdministrator` to `Viewer` that appear in `roles-and-permissions.csv` are `[CONFIRMED]` roles. `OrganisationAdministrator` and `DivisionSiteAdministrator` are `[PROPOSED]` columns because Master names these delegated-administration levels but the roles file omits them (GAP-10).
- The `Basis` column says whether a grant is derivable from a confirmed source or is `PROPOSED` pending Q-02.
- Administrative roles deliberately do **not** read contract content (least privilege). System Administrator never reads business data; reaching it requires `support.elevate`.

## Rules for the matrix and catalogue

1. A permission code in `status-transitions.csv`, an API operation, a policy or a UI action must exist in `permission-catalogue.csv`. A test enforces this (see `docs/TESTING.md`, test AUT-010).
2. A role in `roles-and-permissions.csv` must have a column in `role-permission-matrix.csv`.
3. A grant of `S` (scoped) must name its scope rule in the `ScopeRule` column of the catalogue.
4. Roles are *security* roles. Approver groups (for example "Group Head") are *assignments* of the `Approver` role used by approval matrices; they are not roles.
5. Permissions are checked server-side only (CLAUDE.md). UI visibility is a convenience, never a control.
