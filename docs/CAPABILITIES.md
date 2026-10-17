# Capabilities: optional steps as configuration (slice S6)

Status: Implemented on 2026-10-17. Decision record: `decisions/ADR-0030-configurable-capabilities.md`. Closes finding P-04 and the on/off part of R-05 in `docs/PORTABILITY_AUDIT.md`. Production approval remains external governance.

A **capability** is one optional step of the operating model that the platform can carry out: Legal Review, Register Authorisation, Variations, Renewals and Signature Requests. Before S6 all five were always on, whatever a pack said. Now an organisation can run with any or all of them off, with no code change, through ordinary configuration.

> A capability is **not authority.** Switching one off removes a step from the business's process. It never grants, widens or removes anyone's permission, role or scope, and it never weakens a server-side rule (tenant, organisation, classification, lock, lifecycle). Every permission check that guards a step is made exactly as before.

## 1. How a business uses it

1. The business's solution pack ships **defaults** (`templates/packs/blank/capabilities/capabilities.json` and `templates/packs/jbs-procurement/capabilities/capabilities.json`). The Blank pack turns Legal Review, Renewals and Signature Requests off. The reference pack leaves everything on.
2. An organisation administrator opens **Administration, Capabilities**, sees what is in force and where each answer comes from, and chooses *inherit*, *on* or *off* per capability. Saving only drafts. **Validate** checks the choice against the organisation's published workflows and work in flight. **Preview** shows before and after. **Publish** makes it so (second-person rules and step-up apply as for any configuration). **History** lists releases and **rolls back** to an earlier one as a new release. **Audit** records every change.
3. Pages, navigation, dashboards, built-in lists and register columns stop offering what is off. The lifecycle engine refuses to start new work in it. Work already under way can still be finished or cancelled.

To run with all five off, the organisation first publishes a lifecycle that does not use Variations (the Blank lifecycle minus its amendment steps; the proof test does exactly this), then publishes the capability choice. The validator refuses the choice while a published workflow still uses a capability that would be off, and refuses it while work is in flight in that capability.

## 2. The capability matrix

Bindings are to the platform's **closed vocabularies**, so a workflow written by any business is recognised without the platform knowing a state name. Source of truth: `src/Ecp.SharedKernel/Capabilities/CapabilityCatalogue.cs`.

| Capability | Key (permanent) | Needs | Transition roles | State roles | Guards | Register fields | Built-in lists and dashboard cards |
|---|---|---|---|---|---|---|---|
| Legal Review | `legal.review` | none | `submitLegal`, `recallLegal`, `beginLegal`, `returnLegal`, `clearLegal`, `resubmitLegal`, `returnToLegal` | `legalQueue`, `legalReview`, `legalRework`, `legalCleared` | `LegalRequired`, `LegalNotRequired`, `ControlledFieldChanged` | `legalReviewStatus`, `legalReviewRequired` | `awaiting-legal` |
| Register Authorisation | `register.authorisation` | none | `submitRegister`, `approve`, `reject` | `approval` | `AuthorisedApprover`, `SubmitterNotApprover` | `registeredDate` | `pending-approval` |
| Variations | `variations` | `register.authorisation` | `openVariation`, `applyVariation`, `rejectVariation`, `cancelVariation` | `variation` | `ParentRemainsLocked`, `NoOtherOpenVariation`, `VariationOutcomeApplied`, `VariationOutcomeNotApplied` | none | `variations-in-progress` |
| Renewals | `renewals` | none | `startRenewal`, `completeRenewal`, `cancelRenewal` | `renewal` | `NoOpenRenewal`, `RenewalSuccessorEffective` | none | `renewals-in-progress` |
| Signature Requests | `signature.requests` | none | `sendSignature` | `signature` | `SignatureVoidedOrDeclined` | none | none |

The one managed step that is deliberately not bound is `cancel` (an organisation can always cancel its own draft).

### What "off" does, per surface

| Surface | When the capability is off | Where it is enforced |
|---|---|---|
| Lifecycle engine, starting a step | The step is refused with the stable code `capability_disabled`. | `LifecycleEngine.ExecuteAsync` through `CapabilityGate` (server side; every service that moves a managed step goes through it) |
| Lifecycle engine, listing steps | The step is not offered. | `LifecycleEngine.AvailableAsync` through `CapabilityGate` |
| Work under way | May be finished, cancelled or returned. The gate refuses a step only when the record is **not already inside** the capability (its current state role is not bound to the same capability). | `CapabilityGate.BlockedByAsync` |
| Navigation | `Legal`, `Approvals` (and the approval matrix pages), `Variations`, `Renewals` entries are not shown unless the capability is on in at least one organisation the person can reach. | `_Layout.cshtml` via `TermContext.UsesAnywhere` |
| Contract page and forms | Legal review fields, the Variations section and the "needs legal review" checkbox are hidden for a record of an organisation that does not use them (shown if the record already has such data). | `TermContext.Uses` (presentation only) |
| Register | Fields bound to the capability are not offered as columns, filters or exports. | `RegisterQueryEngine.CatalogueAsync` |
| Built-in lists | A list that exists only for the capability is not listed and cannot be opened (`not found`). | `ContractViewService` |
| Dashboards | The card is not shown and cannot be opened (`not found`). | `DashboardService` |
| Workflow publishing | A workflow that uses a capability that is off cannot be published (`capability_disabled_in_workflow`). The Variation workflow matters only while Variations are on. | `WorkflowDefinitionKind.ValidateAsync` |
| Capability publishing | See section 4 (compatibility). | `CapabilityKind.ValidateAsync` |
| Pack assignment | See section 4. | `PackAssignments` |
| Onboarding | The pack step shows, per pack, which optional steps it switches off. A group mapping to the Legal role gets a **note** (not an error) when Legal Review is off. | `src/Ecp.Host/Pages/Onboarding/Step.cshtml`, `src/Modules/Ecp.Setup/Onboarding/OnboardingRules.cs` |
| Setup | The pack stage notes state each pack's optional steps and where to change them. | `SetupStages.PackStage` |

Pages for an area stay reachable by direct address so that work in flight can be finished; they offer no way to start new work, and the server refuses it regardless.

## 3. Architecture

```
organisation's published Capabilities document   (mode "overrides", wins)
        |  then
solution pack's capabilities document            (mode "defaults")
        |  then
platform default                                 (everything on: how the platform behaved before S6)
                         |
                   CapabilitySet  (immutable, one per organisation)
                         |
        ICapabilityResolver  ----  the one place anything asks "is it on?"
            |        |        |          |           |            |
        engine     nav &     register   dashboards   workflow    pack assign /
        gate       pages     & lists    & views      publish     readiness / admin
```

* **Catalogue** (`Ecp.SharedKernel`, tier 0): `CapabilityKeys`, `CapabilityDefinition`, `CapabilityCatalogue`, `CapabilitySet`. Adding a capability is a code change with a catalogue entry; turning one on or off is configuration. Code names a key only through `CapabilityKeys` (guard test).
* **Document** (`Ecp.Configuration`): `CapabilityDocument`, the content of the **`Capabilities`** configuration kind and of a pack's capabilities document. Plain data: a mode and a list of `{key, enabled, note}`. No conditions, expressions, SQL, scripts or templates. A note is plain text (no markup, template or expression syntax), at most 300 characters. Unknown keys are warnings (a newer pack on an older platform), never effects.
* **Kind**: `CapabilityKind` gives the document the ordinary lifecycle: draft, validate, preview, publish, version history, rollback, audit. Permissions are `capabilities.read`, `capabilities.draft`, `capabilities.publish`, `capabilities.rollback` and `capabilities.audit.read` (section 6).
* **Resolver**: `ICapabilityResolver` (organisation release, then pack layer, then default). Fingerprint carries the release number and pack identity.
* **Cache**: `ICapabilityCache`, keyed by **tenant, organisation and version** (release id and pack identity), ten-minute time to live, bounded. A publish or rollback changes the key, so it is seen at once; `ApplyAsync` also invalidates the organisation. One organisation's set can never be served to another. A cache failure is counted and falls back to persistence. The resolver also remembers each organisation's answer for the life of a request (and drops it when any publish invalidates the cache), and a pack version's defaults are cached by pack key and version, so a page costs a bounded number of reads (release, organisation and pack assignment once per organisation in reach) and never one per row.
* **Fail open**: if configuration cannot be read, every capability is treated as on and a warning is logged, so a store failure can never silently remove a step from a business's process. The failed result is not cached.
* **Gate** (`Ecp.Lifecycle`): `CapabilityGate` is called from `LifecycleEngine.ExecuteAsync` (refuse) and `AvailableAsync` (do not offer). It reads the transition's role from the closed vocabulary and the source state's role, so it knows no state, step or pack name.
* **Compatibility** (`ICapabilityCompatibility`, implemented in `Ecp.Lifecycle`): found through the vocabularies, never by name (section 4).
* **UI**: `Terms.Uses(key)` (the record's organisation, once a page has called `UseOrganisationAsync`) and `Terms.UsesAnywhere(key)` (any organisation the person reaches) on `TermContext`. Presentation only: no permission depends on it, and the same rule is enforced server side.
* **Tier rules**: the catalogue sits in SharedKernel; the document, kind, resolver, cache and administration sit in Configuration (tier 5); the gate and compatibility sit in Lifecycle (tier 7), reached from Configuration only through `ICapabilityCompatibility`, resolved lazily from the service provider so no module depends upward.

## 4. Compatibility checks

| Check | Severity | Where |
|---|---|---|
| A capability is on whose required capability is off (Variations without Register Authorisation) | **Error** (`capability_requires`) | capability validate, pack assign, readiness |
| A published workflow (lifecycle, or the Variation workflow while Variations are on) uses a capability that would be off | **Error** (`workflow_uses_disabled_capability`) | capability validate, pack assign preview and assign |
| A capability that is on now is being turned off while work is in flight in it (open legal reviews, pending approvals, open Variations, renewals in progress, signature requests sent or delivered) | **Error** (`capability_in_use`) | capability validate, pack assign |
| A capability that is already off has work in flight (for example after a pack change made it off) | Warning (`capability_off_work_in_flight`); the work can be finished | overview, preview |
| A workflow is published that uses a capability that is off | **Error** (`capability_disabled_in_workflow`) | workflow validate |
| A pack's own capability defaults leave a capability on without what it needs, or the pack's starting workflows use a capability the pack turns off | Degraded; **Unhealthy** if it is the installation default pack | readiness check `capabilities` (`/health/ready`) |
| A mapping gives people the Legal role while Legal Review is off | Warning (`role_unused_capability`) | onboarding validation |

A pack assignment whose capability defaults would be incoherent for the organisation as it is now is **refused** with `capability_conflict`; the preview shows the same findings first.

## 5. Packs

The pack format has the content kind `Capabilities` (stored in the package under content/capabilities.json; `PackKinds.Packable`; validated with no organisation, which also checks coherence on its own). It is read by the resolver as the pack layer. Assigning a pack does **not** create a draft for it; its defaults apply from the assignment, and an organisation's own published choice still wins.

| Pack (version) | Legal Review | Register Authorisation | Variations | Renewals | Signature Requests |
|---|---|---|---|---|---|
| Blank (1.5.0) | off | on | on | off | off |
| Reference (2.5.0) | on | on | on | on | on |
| No pack document, or an organisation with no pack and no installation default | on | on | on | on | on |

The Blank defaults match the Blank lifecycle, which has amendments but no Legal, signature or renewal step. They are not a statement about any business; they describe what that starting lifecycle uses.

## 6. Permissions

Five new permission codes (all permanent identifiers under ADR-0029; the role-permission matrix has 87 permissions, 1,044 cells):

| Permission | Granted to |
|---|---|
| `capabilities.read` | Tenant administrator, organisation administrator, business configuration administrator, auditor |
| `capabilities.draft` | Tenant administrator, organisation administrator, business configuration administrator |
| `capabilities.publish` | Tenant administrator, organisation administrator |
| `capabilities.rollback` | Tenant administrator, organisation administrator |
| `capabilities.audit.read` | Tenant administrator, organisation administrator, business configuration administrator, auditor |

The matrix rows carry the basis "S6 owner decision" and the allocation is recorded as decision D7d in `requirements/01-authoritative/CONFIRMED_DECISIONS.md`. Hiding a role in pickers (ADR-0029 D8) uses the same decision point and is unaffected.

## 7. Verification

| Evidence | Where |
|---|---|
| Catalogue, set, document and workflow-use rules | `tests/Ecp.UnitTests/CapabilityTests.cs` |
| Layers, overrides, publish and rollback, permissions, compatibility errors, work in flight, pack assignment refusal, engine gate for all five (refusal, hiding, finishing in-flight work), dashboards, lists and register fields, cache, fail-open, and **a business operating with all five off** on SQLite and SQL Server | `tests/Ecp.Integration.Tests/CapabilityScenarios.cs` |
| Pages, navigation, form, validation errors, publish and rollback through the host, onboarding pack step | `tests/Ecp.Api.Tests/CapabilityUiTests.cs` |
| Portability guard: keys only through `CapabilityKeys`, engine gate present and before managed steps, navigation consults the resolver, only the resolver reads the kind, packs state every capability, documents are data | `tests/Ecp.Architecture.Tests/CapabilityGuardTests.cs` |
| Permission matrix and frozen identifiers | `tests/Ecp.UnitTests/AccessEvaluatorTests.cs`, `tests/Ecp.UnitTests/FrozenIdentifierTests.cs` |

SQL Server runs of the scenarios are skipped locally when no SQL Server is available and are the CI evidence (see `docs/TESTING.md`).

## 8. Status of functionality

| Item | Status |
|---|---|
| Catalogue, document, kind, resolver, cache, administration service | **Implemented** |
| Engine gate and step hiding | **Implemented** |
| Navigation, contract page, forms, register, lists, dashboards | **Implemented** |
| Admin pages (overview, edit, preview, history, audit) | **Implemented** |
| Pack defaults (Blank, reference), pack assignment refusal, readiness check | **Implemented** |
| Onboarding and setup awareness | **Implemented** (notes and a warning; nothing is blocked by them) |
| Hiding the Legal and signature **pages** by direct address | **Not done by design** (work in flight must stay reachable); creation is refused server side |
| Capabilities beyond these five (for example category ownership, critical notice, external reporting) | **Future**; they are not capabilities until a named business needs the switch |
| Reporting, DocuSign, operations, API, AI, marketplace, role renaming | **Not part of S6** |

## 9. Assumptions and contradictions recorded

* **Assumption (reversible):** "off" means "no new starts", not "delete". Existing records, history and documents are untouched; turning a capability back on restores the step.
* **Assumption (reversible):** Variations require Register Authorisation because a Variation is approved the same way as a registration. A business that approves amendments some other way would need a catalogue change.
* **Assumption (reversible):** the defaults for a pack that gives no capabilities document are "all on", so a pack written before S6 behaves exactly as before.
* **Contradiction noted:** `docs/PORTABILITY_AUDIT.md` P-04 describes pack metadata `features` (`LegalReview`, `Variations`, `QlikExternalReporting`, `CriticalNotice`). Those metadata fields remain informational. Capabilities are a separate, validated, versioned document, because metadata cannot be versioned, previewed or audited per organisation. `QlikExternalReporting` and `CriticalNotice` are not capabilities (Future).
