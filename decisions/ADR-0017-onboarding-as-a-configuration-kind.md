# ADR-0017: Organisation onboarding as a configuration kind with a provenance ledger

Status: Proposed (accepted with the T0.3 pass)

## Context

Onboarding must support draft, validation, preview, publish, version history and rollback with audit, without code, and must not delete or damage anything that existed before it ran (`CONFIGURATION_LIFECYCLE_SPEC.md`, the user's onboarding requirements).

## Decision

1. Onboarding is the configuration kind `OrganisationOnboarding`. The Configuration module owns the lifecycle; the kind (in the Setup assembly) supplies validation and `ApplyAsync(organisation, content, previousContent, release)`. `IConfigurationKind.ApplyAsync` gained the previous release's content for this purpose.
2. Content is a small JSON document naming things by business key and email address; CSV is an import/export form of the same lists.
3. Apply is idempotent upserts through the owning modules' trusted services. It records in `setup.OnboardingItem` every item it **created** (not items it merely matched). When a later release drops an item, only items with an open ledger row are ended (deactivated or revoked); a ledger row is reopened if the item returns.
4. Onboarding cannot map administrator or approver roles. Roles it grants are ended only through `RevokeTrustedAsync(source = Onboarding)`, which refuses administrator roles.
5. The preview runs the real `AccessEvaluator` against synthetic records and stand-in identifiers; it needs no database access.
6. The kind must not depend on the configuration service (the service enumerates all kinds); the applier resolves it lazily. An integration test fails fast on construction cycles.

## Consequences

* Rollback is "publish earlier content as a new release" and is safe for pre-existing data.
* A person who left the tenant between validation and publication makes apply fail and the whole release roll back (single unit of work).
* Contract types are recorded but not yet consumed (Scaffolded).
* Residual: ledger rows are per organisation and key; renaming a key looks like drop-and-add.
