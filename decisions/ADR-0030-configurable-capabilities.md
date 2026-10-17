# ADR-0030: Optional platform steps are configurable capabilities

Status: Accepted by the owner's S6 instruction (portability slice S6, 2026-10-17). Production approval remains external governance.
Closes finding P-04 and the on/off part of R-05 of `docs/PORTABILITY_AUDIT.md`. Builds on ADR-0026 (external solution packs), ADR-0028 (terminology) and ADR-0029 (stable identifiers). The matrix and architecture are in `docs/CAPABILITIES.md`.

## Context

Legal Review, Register Authorisation, Variations, Renewals and Signature Requests are compiled platform capabilities. Their steps were already driven by the workflow definition, but they were always on: the pack metadata `features` was parsed and never read, so a business without Legal Review or a register could not remove them. "JBS is a shipped reference configuration, not universal hard-coded logic" was only partly true.

The question recorded in the remediation plan was whether Legal Review is a flag-gated platform capability or a JBS pack feature that can be removed. The owner's S6 brief fixes the first reading: the data model and pages stay, and the business chooses whether to use them.

## Decisions

| # | Decision |
|---|---|
| D1 | A **capability** is an operating-model switch. It is not authority: it never grants, widens or removes a permission, role or scope, and no server-side rule (tenant, organisation, classification, lock, lifecycle) depends on it. |
| D2 | There are exactly five capabilities, with permanent keys: `legal.review`, `register.authorisation`, `variations`, `renewals`, `signature.requests`. A new capability is a code change with a catalogue entry. A key is never renamed (ADR-0029 rule). |
| D3 | Capabilities are bound only to the platform's **closed vocabularies**: transition roles, state roles, guards, register fields and built-in list or dashboard keys. A workflow written by any business is recognised through these, so the platform never names a state, step or business. |
| D4 | Resolution order is: the organisation's published `Capabilities` document, then the solution pack's defaults, then the platform default (everything on). The platform default keeps every pack written before S6 behaving exactly as before. |
| D5 | A capability document is a new **configuration kind** with the ordinary lifecycle (draft, validate, preview, publish, version history, rollback, audit). It is plain data: statements of `{key, enabled, note}`; no conditions, expressions, SQL, scripts or templates. |
| D6 | A pack carries its defaults as a new packable content kind. Assigning a pack does not create a draft for it; the defaults apply from the assignment and an organisation's own published choice wins. |
| D7 | **Off means no new starts.** Work already inside the capability can be finished, cancelled or returned. This is enforced at the single lifecycle chokepoint (`ExecuteAsync`, with `AvailableAsync` hiding the step), so no page, API or service can bypass it. |
| D8 | Incoherent combinations are **errors**, not warnings: a capability on without one it needs; a published workflow using a capability that would be off; turning a capability off while work is in flight in it; assigning a pack whose defaults would do either. Work in flight in a capability that is already off is a warning. |
| D9 | Resolution **fails open**. If configuration cannot be read, every capability is treated as on and a warning is logged. A store failure must never silently remove a step from a business's process. |
| D10 | The resolved set is cached by **tenant, organisation and version** (release and pack identity); a publish or rollback is seen at once, and one tenant's or organisation's switches can never be served to another. |
| D11 | Five permissions: `capabilities.read`, `.draft`, `.publish`, `.rollback`, `.audit.read`. Draft is delegable to the business configuration administrator; publish and rollback stay with the tenant and organisation administrators. Recorded as D7d in `requirements/01-authoritative/CONFIRMED_DECISIONS.md`. |
| D12 | UI pages for an area stay reachable by direct address so in-flight work can be finished. Navigation, forms, lists, dashboards and register columns hide what is off; the server refuses the start regardless. |
| D13 | `QlikExternalReporting` and `CriticalNotice` in the old pack `features` metadata are **not** capabilities. They stay informational (Future) until a named business needs a switch. |

## Alternatives considered

* **Read the pack's `features` metadata.** Rejected: metadata is not versioned, previewed, audited or overridable per organisation, and a pack is not the right owner of a runtime decision an organisation must be able to change.
* **Remove the compiled steps from the platform and make them pack content.** Rejected: Legal Review, approvals, Variations and renewals have their own data and rules (queues, rounds, plans, before and after values). They stay platform capabilities; the business chooses to use them.
* **Hide by permission.** Rejected: permission is authority. Using it to hide a step would mix two concerns and make an administrator's access depend on the process.
* **Per-feature code flags (`if (legalEnabled)`).** Rejected: it spreads the decision across the code base. One gate at the engine and one resolver for every reader keeps it findable and testable (`tests/Ecp.Architecture.Tests/CapabilityGuardTests.cs`).

## Consequences

* P-04 and R-05 are closed for the five capabilities. The gate, resolver and compatibility rules are covered by unit, SQLite and SQL Server scenario, API and architecture tests, including a business operating with all five off.
* A business that turns Variations off must first publish a lifecycle without amendment steps; the validator says so rather than letting the process stall.
* The Blank pack's defaults (Legal Review, Renewals and Signature Requests off) describe what its starting lifecycle uses. They are not a claim about any business.
* The role-permission matrix grows from 82 to 87 permissions. The codes join the frozen identifiers.
* Reversible assumptions: "off" is never "delete"; Variations require Register Authorisation because they are approved the same way; a pack with no capabilities document means all on.
* Not decided: further capabilities (category ownership, critical notice, external reporting), and any change to the approval model. Future until a named business needs them.
