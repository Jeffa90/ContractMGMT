# 08 Portability

Added by repository review (GAP-01, GAP-13, GAP-14, GAP-15, CON-07). `requirements/01-authoritative/PORTABILITY_AND_NO_CODE.md` states the obligation in one paragraph: the platform moves between businesses through configuration, not source forks, and business administrators configure without code. These files make it testable. Tags: `[CONFIRMED]` / `[PROPOSED]` as in `requirements/README.md`.

| File | Content |
|---|---|
| `TENANCY_AND_ORGANISATION_SPEC.md` | Product modes, the tenant/organisation model, tenant resolution, isolation surfaces |
| `CONFIGURATION_LIFECYCLE_SPEC.md` | Draft, validate, preview, simulate, publish, version, rollback and audit; releases; pinning; concurrency |
| `CONFIGURATION_STUDIO_SPEC.md` | Field, form, rule, workflow, approval-matrix, lookup, terminology, branding, feature-flag and template semantics, and publish-time validation |
| `CONFIGURATION_PACKAGE_SPEC.md` | Package and solution-pack format, signing, import with dry run, conflict resolution, export, catalogue trust |
| `DATA_EXPORT_SPEC.md` | Records, documents, audit and full-tenant export; round-trip portability |
| `PORTABILITY_ACCEPTANCE_CRITERIA.md` | Measurable portability and no-code acceptance tests |

Design counterparts: `docs/ARCHITECTURE.md` (sections 4 and 5), `docs/DOMAIN_MODEL.md` (section 5).
