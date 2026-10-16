# ADR-0024: A solution pack is resolved by the organisation, never by a code default

Status: Accepted (portability remediation slice S1, 2026-10-16; `docs/PORTABILITY_REMEDIATION_PLAN.md`, finding P-01 of `docs/PORTABILITY_AUDIT.md`)

## Context

The audit found that the workflow and notification-policy stores installed the reference definitions of a pack named by a process-wide option whose code default was the JBS reference pack. The organisation's own `SolutionPackKey` (set at setup or onboarding and held on the organisation) was never read. An organisation on the Blank pack, or on any future pack, silently received the 17-state JBS lifecycle, the JBS Variation workflow, the approver group `GroupHeadProcurement` and the JBS notification policy the first time it needed one. A pack carries one business's rules, so the platform must not pick one for an organisation that did not choose it (PRT-001, PRT-002).

## Decision

1. **The organisation decides.** When an organisation has published no workflow or notification policy, the reference definitions installed are those of **its own** pack: `Organisation.SolutionPackKey`.
2. **An installation default exists for the organisation without a pack, and it is configuration.** `Ecp:DefaultPackId` in host configuration (environment variable `Ecp__DefaultPackId`) names the pack used only for an organisation whose `SolutionPackKey` is empty. It ships **unset**. No pack identifier remains in compiled code.
3. **No silent choice.** If the organisation has no pack and no installation default is set, the request is refused with the stable code `no_solution_pack` and a message that names the organisation and the setting. Nothing is installed.
4. **One shared rule.** `PackSelection.ResolveAsync` in `Ecp.Tenancy` is the only place the rule lives; the Lifecycle and Notifications stores both call it.
5. **The Blank pack becomes usable.** It ships a neutral contract lifecycle (Open, Active, Being amended, Closed, Cancelled), a neutral Variation workflow (Open, Submitted, Applied, Rejected, Cancelled) and a neutral notification policy (the reminders and their acknowledgement), as data under `templates/packs/blank/`. They use only the existing closed vocabularies, name no business, and pass the same validators as the JBS definitions. The neutral approver group key is `Approvers`. State codes deliberately do not coincide with the JBS lifecycle so that the two are distinguishable in tests.
6. **Test hosts name the pack they rely on.** The test composition sets the installation default to the reference pack (`TestPacks.Reference`), because its worlds create organisations without a pack. That is the one place outside a pack where the JBS pack is named, and it is test code.
7. **The architecture guard (ARC-010) sees every spelling.** It now matches `JBS`, `Medius`, `Qlik` and `DocuSign` case-insensitively and inside identifiers in all `src` C# files (stage A of R4).

## Consequences

* **No change for JBS organisations or for anything already published.** The fall-back applies only where nothing is published; organisations with releases are untouched. Pinned contracts keep their workflow release.
* **Organisations that already received JBS definitions through the old fall-back keep them.** They are that organisation's own published configuration; replacing them is a deliberate piece of work because the platform cannot yet move pinned contracts to another workflow release (plan section 7.1). S1 does not touch them.
* **The seeded Demonstration organisation on the Blank pack (`org-a2`) now has the neutral lifecycle.** The JBS demonstration organisation (`org-a1`) is unchanged.
* **A deployment that created organisations without a pack must set `Ecp:DefaultPackId`** (or choose a pack for the organisation) if those organisations still need their first definitions installed. Setup always records a pack, so a standard installation is not affected.
* **Still open (other slices):** the pack files are still embedded in module assemblies (R7), the role catalogue (R8), the initial status for migrated contracts (R2), the reminder fall-back recipient (R3), vocabulary on pages and dashboards (R5), feature flags (R9). PRT-002 is **not** passed by this slice; its first step is.

## Alternatives considered

* **Keep a code default and let the organisation override it.** Rejected: any default in code is a business's pack chosen for everyone else.
* **Fail at startup when no installation default is set.** Rejected: setup always records a pack, and a standard installation needs no default; refusing only the request that needs it is more precise.
* **Install the pack's definitions eagerly when the pack is chosen.** Attractive and likely right once the pack loader exists (R7); today the lazy install keeps the change small and leaves the current publication path untouched.
