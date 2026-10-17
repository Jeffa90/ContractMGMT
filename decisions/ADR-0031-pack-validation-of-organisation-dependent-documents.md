# ADR-0031: Pack validation of organisation-dependent documents

Status: Accepted by the owner's decision D2 (S9 readiness, 2026-10-17; `requirements/01-authoritative/CONFIRMED_DECISIONS.md` D8). Production approval remains external governance.
Fixes finding G11 of `docs/S9_READINESS.md`. Builds on ADR-0026 (external solution packs) and ADR-0030 (capabilities).

## Context

A pack is validated at upload with no organisation (`Guid.Empty`) and again, per document, when it is assigned to a real organisation. Some documents can only be judged against an organisation's published configuration: an approval-matrix condition on a custom field, a list view over a custom field, a workflow that names a matrix, a classification level that belongs to the tenant's scale. Judged with no organisation, those were refused at upload, so a valid pack could not be installed, and the first assignment then reported every such draft as a problem.

## Decision

1. **Context-dependent codes.** A closed list (`ContextDependentCodes` in `Ecp.Configuration`) names the findings that mean "this refers to something the organisation has not published yet": an unavailable fact, column, filter, matrix or custom-field-shaped key. At upload these are **warnings**. A mistyped core column, a wrong type or a bad date stays an error and is refused at upload.
2. **Pack-own classification scale.** While a pack's documents are validated, the pack's own classification levels are the scale (`ClassificationScaleOverride`, scoped to the validation), so a pack with a four-level scale and no privileged level is judged against that scale and not the tenant's.
3. **Prerequisites at assignment.** A closed table (`PackKinds.Prerequisites`) says which kinds a kind depends on (matrix and views on custom fields; workflows on the matrix and custom fields). A draft whose only findings concern something a prerequisite *still-open draft* will provide is reported as "waits for something it depends on to be published first" and does not need attention. Any other finding is a problem.
4. **Publication stays the judge.** Publishing a draft validates it for the real organisation with the real, published prerequisites. Nothing is weakened: the order in which a business publishes (classification, fields, terminology, matrix, workflows, policy, views, branding) is enforced by validation, not by convention, and is covered by the smoke test.
5. **No code, no new vocabulary for businesses.** Both tables are platform vocabulary. A pack remains data.

## Consequences

* A valid pack is never refused only because the organisation-dependent artefacts cannot be judged without an organisation.
* The assignment result tells an administrator which drafts wait for which, instead of calling them broken.
* Cost: two closed lists to maintain when a new packable kind is added; the smoke test (`PackKindSmokeScenarios`) is where a new kind is exercised end to end, and a reviewer adding a kind should extend it.

## Alternatives considered

* **Design the second business's pack around the limitation** (rejected by the owner, D2).
* **Validate packs only at assignment** (rejected: an upload would accept anything, including malformed documents; narrowness at upload is a security property of the package pipeline).
* **Give the pack validator a placeholder organisation** (rejected: it would invent configuration the pack does not own).
