# ADR-0039: Guided administration: access profiles as a presentation layer, effective access from the evaluator, and Test/Demo-only preview

Status: In progress on the guided-administration branch (slices UX-1 to UX-8). Proposed until the owner reviews it; production approval remains external governance and is not declared.
Builds on ADR-0005 (portability), the access model, ADR-0038 (company rehearsal enablement) and the approved direction in `docs/UX_REDESIGN.md` and `docs/UX_ACCEPTANCE_CRITERIA.md`.

## Context

The first manual usability test found that a business administrator had to understand role codes, viewer kinds, group claim keys, scope intersection and CSV syntax to give a colleague access. The security engine is sound; the way it is presented is not usable.

## Decision

1. **Access profiles are a UX and configuration layer, not a second security engine.** A profile (for example Contract Manager, Reviewer, Approval Authority, Read Only) is a named recipe over existing stable role codes, plus category ownership where the role is useless without it. Applying one issues ordinary role grants and ownerships through the services that already enforce delegation, scope containment, ceiling rules, self-grant refusal and last-administrator protection. A profile cannot carry a permission, a new role, or System or Tenant Administrator. Authority still derives only from real grants, scopes, ceilings, category ownership, approver-group membership and identity mappings.
2. **A new module, `Ecp.Access` (tier 8, schema `access`)** holds the catalogue, the application service, the effective-access service and one small additive table, `ProfileApplication`, the profile-provenance record. The provenance row records which profile produced which grants and ownerships, by whom and when. It explains and allows undo of exactly what one save created; deleting every row changes nobody's permissions.
3. **A save is atomic.** `IUnitOfWork` already gives one transaction across module contexts and buffers audit events until commit, so a refusal part-way (a delegation rule, a missing ownership, a self-grant) rolls back every grant, ownership and audit event of that save. There is no partial state to report or repair.
4. **Preview is the real apply, rolled back.** The review screen runs the same code inside a transaction and discards it, so every rule of the real services applies and the screen cannot disagree with the save.
5. **Effective access is computed, never stored.** The report calls the public `AccessEvaluator` for every permission, groups the 94 permissions into plain-English abilities (`abilities.csv`; a test fails the build if a permission has none) and explains where and why from the real grants. A person reads their own report; another person's needs authority over them.
6. **Stable identifiers do not change.** Role and permission codes stay as they are; terminology controls what people read. Profile labels and summaries are terminology strings expanded at display.
7. **Changing roles, scope, classification ceiling, ownership or approver membership needs a recent sign-in**; a simple non-security edit does not.
8. **Identity group mappings stay Advanced and restricted** to the currently supported safe role catalogue; privileged administrator and approval roles are not made broadly assignable through identity groups.
9. **Test/Demo-only tools** (slices UX-3 and UX-4, recorded here when built): "Allow demonstration sign-in" for synthetic people, "Preview as this user", and "Give me full Test-mode access". All are refused in Production at the service layer, audited, and never grant System Administrator implicitly. Preview uses the target's real access, never the administrator's, cannot cross tenant or organisation boundaries and is read-only.

## Alternatives considered

* **A second, simplified permission model.** Rejected: two engines drift; the profile layer cannot disagree with the evaluator because it only creates ordinary grants.
* **Storing effective access on the person.** Rejected: it goes stale; computing on read from the evaluator is exact.
* **Applying a profile as several independent calls and reporting partial success.** Rejected once the unit of work was found to provide atomicity (this resolves UXG-01).
* **Putting the module in Setup (tier 9).** Rejected: People & Access must be usable by the administration pages without depending on first-run set-up.

## Consequences

* One additive migration (`Access: AddProfileApplication`) is part of the platform 1.1.0 upgrade manifest, regenerated with the release notes.
* Administrators see names, never codes; the Advanced section keeps raw grants for specialists.
* A person who administers an organisation but is not a Tenant Administrator can assign category ownership; a Tenant Administrator alone cannot (existing rule, unchanged). The guided screens explain this instead of failing silently.
