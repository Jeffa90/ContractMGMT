# ADR-0029: Role codes, permission codes and approver-group keys are permanent identifiers

Status: Accepted by the owner (portability slice S5, 2026-10-17). Production approval remains external governance.
Closes finding P-02b of `docs/PORTABILITY_AUDIT.md`. Supersedes the plan's slice S8 (rename with aliases). Builds on ADR-0028 (terminology). The analysis, options and impacts are in `docs/ROLE_CATALOGUE_ADR.md`.

## Context

The role catalogue (twelve codes) is compiled; two codes carry a procurement flavour (`ProcurementAdministrator`, `CategoryOwner`) and a JBS pack names an approver group `GroupHeadProcurement`. Renaming them was proposed as S8. After S4, what users read for every role comes from terminology. The codes are persisted in role assignments and identity-group mappings, recorded in append-only audit, written into immutable published configuration (field visibility lists, onboarding documents) and named in customers' identity-provider files and scripts. A rename would add a permanent second spelling and retire nothing.

## Decisions

| # | Decision |
|---|---|
| D1 | Role codes are permanent identifiers. They are never renamed, aliased or reused. A new need gets a new code. |
| D2 | Permission codes are permanent identifiers under the same rule. |
| D3 | Approver-group keys are permanent identifiers. They are free text chosen by a pack or an administrator (the platform gives no key a meaning); a pack may keep its key for ever. |
| D4 | PRT-013 is satisfied by terminology and display-layer abstraction: names, labels and display names come from the catalogue, terminology and packs; identifiers are stable and are not business wording. The requirement text is amended accordingly. |
| D5 | S8 (rename with aliases) is retired and recorded as superseded by ADR-0028 and this decision. |
| D6 | The identifiers are frozen by tests (`tests/Ecp.UnitTests/FrozenIdentifierTests.cs`). |
| D7 | Approver groups may have display names (organisation over pack over the key). Administrator pages show the name with the key beside it. |
| D8 | Roles may be hidden from assignment pickers by an organisation or its pack. This is presentation only: it revokes nothing, blocks no other grant path, and never applies to the system, tenant and organisation administrator roles. Capability-driven hiding (S6) will use the same decision point. |
| D9 | No persisted identifier is renamed and no alias migration is created. |
| D10 | Not decided here, and still open for the owner: the neutral label of the `Approver` role, and a data-driven role catalogue (a different role set or authority model), which stays Future until a named business needs it. |

## Consequences

* P-02b is closed by policy, not by rename. A reader who finds `ProcurementAdministrator` in source finds a documented stable identifier with a terminology label.
* Customer identity-provider mappings, import files, audit exports and published configuration stay valid for ever.
* If a different authority model is ever needed, it is built additively on a closed relationship vocabulary, never by renaming the twelve.
