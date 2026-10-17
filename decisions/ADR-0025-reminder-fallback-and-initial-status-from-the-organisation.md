# ADR-0025: The reminder fall-back and the initial status of migrated contracts come from the organisation's configuration

Status: Accepted (portability remediation slice S2, 2026-10-17; `docs/PORTABILITY_REMEDIATION_PLAN.md` actions R2 and R3, findings P-02a and P-08 of `docs/PORTABILITY_AUDIT.md`)

## Context

Two behaviours still named one business in compiled code or in host configuration after S1:

* **P-02a.** When nobody currently owns a contract's category, the reminder scan told "the Procurement Administrators": the code held the role `ProcurementAdministrator` and used it for every organisation, whatever its pack. A Blank organisation (or any business without that role) would have had its reminders routed to people chosen by the JBS business, or to nobody.
* **P-08.** A migrated, non-legacy contract was created in the state named by host setting `Contracts:InitialStatusCode`, shipped as `"Draft"`: a JBS state. A pack whose first state is not `Draft` would have received a status its workflow does not contain.

## Decision

1. **The fall-back recipient is a notification-policy rule.** The reminder block of the policy gains `unownedFallback`: a list of recipient rules from the existing closed vocabulary. The vocabulary gains one rule, `OrganisationAdministrators` (the holders of the organisation-level administrator role). The scan resolves the policy's list, filtered to people who can open the record, exactly as it filtered the old role holders. An empty list means nobody: the reminder is counted as skipped for lack of a recipient and tried again at the next scan, no reminder window is used up.
2. **Backward compatible read.** A policy published before the field existed has no `unownedFallback`; it reads as `["OrganisationAdministrators"]`, which is what the platform did then, so every release already published behaves as before. A pack that wants another behaviour states it. The validator refuses an unknown or repeated rule when the policy is published.
3. **Packs state it.** The JBS notification policy states `["OrganisationAdministrators"]` (byte-for-byte today's recipient). The Blank pack states `[]`: its reminders go to category owners only.
4. **The initial status comes from the organisation's lifecycle.** `IContractMigrationPort.InitialStatusAsync(organisationId)` replaces the host-wide `InitialStatusCode`. It asks the same source the contract pages use (`IContractCreationPolicy`, the initial state of the organisation's lifecycle definition) and so inherits S1's pack resolution. `Contracts:InitialStatusCode` is removed from the shipped `appsettings.json`; it remains only as the value for a host with no lifecycle module (the test hosts that compose Contracts alone).
5. **The dry run reports the blocker.** An organisation with no lifecycle (no pack and no installation default) stops the dry run with `MIG-E026`, before any row is read, instead of failing at commit.

## Consequences

* **JBS behaviour is unchanged.** Its policy states today's recipient; its initial state is still `Draft`. Releases published before this slice read as before.
* **A Blank organisation never resolves JBS reminder recipients** in a release installed from the pack (tests on both providers prove an administrator is not told while the owner is). **Reversible assumption:** a Blank-pack policy release published in the short time between S1 and S2 (no deployment had one) has no field and would read as the legacy fall-back; republishing it from the pack corrects it.
* **The role identifier is still compiled.** `OrganisationAdministrators` resolves to the role the role catalogue calls `ProcurementAdministrator`; renaming or replacing the catalogue is portability finding P-02b (slices S5 and S8). S2 moves the *decision* into policy; it does not touch role identifiers.
* **Out of scope, deliberately:** the other recipient rules (`LegalPool`, the Legal counterpart) also resolve a compiled role (P-02b), the migrated-contract path still does not pin a workflow (unchanged), no new capability was added.

## Alternatives considered

* **Treat an absent field as "nobody".** Cleaner, but it silently changes who receives reminders for every JBS release already published, which the plan forbids (section 7).
* **A per-organisation setting outside the policy.** Rejected: the policy already is the versioned, audited, validated place for recipients, and a setting would bypass draft, publish and rollback.
* **Make the host setting per organisation.** Rejected: the lifecycle definition already defines the initial state; a second source can only disagree with it.
