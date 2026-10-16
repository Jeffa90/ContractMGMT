# Document Index

Start here to find any document. Order of authority for requirements is in `CLAUDE.md`; the tag convention (`[CONFIRMED]` / `[OPEN]`) is in `requirements/README.md`.

## Design documents (how the requirements will be met)

| Document | Purpose |
|---|---|
| `docs/REPOSITORY_REVIEW.md` | Gaps, contradictions, risks, assumptions and open questions found before implementation (IDs `GAP`, `CON`, `RISK`, `ASM`, `Q`, `PA`) |
| `docs/IMPLEMENTATION_PLAN.md` | Sequencing logic, milestones and exit gates |
| `docs/ARCHITECTURE.md` | Modular monolith, configuration engine, tenant isolation, data, deployment, migration |
| `docs/DOMAIN_MODEL.md` | Aggregates, invariants (`INV-nn`), configuration model, events |
| `docs/SECURITY.md` | Security design and controls |
| `docs/THREAT_MODEL.md` | Data flows, threat register (`T-nn`), abuse cases |
| `docs/TESTING.md` | Strategy, environment constraints, test catalogue (IDs) |
| `TASKS.md` | Dependency-driven roadmap and task status |
| `decisions/` (ADR-0001 to ADR-0014) | Architecture decisions (all *Proposed* until T0.3) |

## Operational documents (original package)

`docs/GITHUB_CLOUD_SETUP.md`, `docs/MIGRATION_RUNBOOK_TEMPLATE.md`, `docs/PRODUCTION_READINESS_CHECKLIST.md`, `SECURITY-NOTICE.md`, `README.md`, `prompts/*`, `.claude/skills/*`.

## Requirements map

See `requirements/README.md` for the folder map and topic cross-reference table.

## Added by repository review (2026-10-15)

| Area | Files |
|---|---|
| Repository hygiene | `.gitignore`, `.env.example`, `requirements/06-design/.gitkeep`, `src/.gitkeep`, `tests/.gitkeep`, `templates/demo/.gitkeep`, regenerated `PACKAGE_MANIFEST.md` |
| Requirements index | `requirements/README.md` |
| `02-reference` | `README.md`, `GLOSSARY.md`, `permission-catalogue.csv`, `role-permission-matrix.csv`, `reference-lookups.csv` |
| `03-data-dictionary` | `README.md`, `contracts-extended.csv`, `suppliers.csv`, `categories.csv`, `org-structure.csv`, `documents.csv`, `lifecycle-records.csv`, `audit-event.csv`, `migration-entities.csv` |
| `04-workflows` | `WORKFLOW_SPEC.md`, `guard-vocabulary.csv`, `status-transitions-supplemental.csv` |
| `05-security` | `ACCESS_CONTROL_SPEC.md`, `AUDIT_SPEC.md`, `RETENTION_AND_CLASSIFICATION_SPEC.md` |
| `06-design` | `ACCESSIBILITY_AND_LOCALISATION.md` |
| `07-integrations` | `README.md`, `ADAPTER_CATALOGUE.md`, `IDENTITY_SPEC.md`, `ESIGNATURE_DOCUSIGN_SPEC.md`, `REPORTING_QLIK_SPEC.md`, `API_AND_WEBHOOKS_SPEC.md`, `NOTIFICATIONS_SPEC.md` |
| `08-portability` | `README.md`, `TENANCY_AND_ORGANISATION_SPEC.md`, `CONFIGURATION_LIFECYCLE_SPEC.md`, `CONFIGURATION_STUDIO_SPEC.md`, `CONFIGURATION_PACKAGE_SPEC.md`, `DATA_EXPORT_SPEC.md`, `PORTABILITY_ACCEPTANCE_CRITERIA.md` |
| `09-migration` | `MIGRATION_SUPPLEMENT.md` |
| `12-operations` | `SETUP_AND_READINESS_SPEC.md` |
| Samples | `sample-data/demo-organisation-structure.json`, `demo-categories-and-assignments.json`, `demo-users-and-roles.json`; `migration-samples/field-mapping-profile-complete.csv`, `status-lookup-map.csv` |
| Changed original files | `TASKS.md` (rewritten; original 26 items preserved in a traceability table), `decisions/` ADR-0001 to ADR-0005 (context, alternatives, consequences added; original *Decision* lines kept verbatim) |

**Not changed:** `CLAUDE.md`, everything in `requirements/01-authoritative/`, the original data dictionary, workflow, roles, migration and operations files, original samples, prompts and skills. Proposed amendments to authoritative files are listed in `docs/REPOSITORY_REVIEW.md` section 7.

## Link check

Run after the final edit on 2026-10-15 with a throwaway script (not committed; task T0.5 turns it into a permanent CI check):

| Check | Result |
|---|---|
| Markdown links and backticked repository paths resolve (84 Markdown files); paths to code not yet written (`src/`, `tests/`, `deploy/`, `templates/packs/`, `docs/ENVIRONMENT.md`) are exempt | Pass |
| CSV files well-formed with consistent column counts (22 files); JSON files valid (6 files) | Pass |
| Every referenced ID (`GAP`, `CON`, `RISK`, `ASM`, `Q`, `PA`, `T`, `INV`, and all test families) is defined (465 IDs) | Pass |
| 53 "<file> section N" citations resolve to a numbered heading | Pass |
| Every permission code used by `status-transitions.csv` and the supplemental file exists in `permission-catalogue.csv`; catalogue and matrix list the same permissions | Pass |
| Every role in `roles-and-permissions.csv` has a column in `role-permission-matrix.csv` | Pass |
| Every guard named in the supplemental transitions is in `guard-vocabulary.csv`; every state name is in the `ContractStatus` lookup | Pass |
| All 18 columns of `contracts-import.csv` are mapped in `field-mapping-profile-complete.csv`; targets exist in the dictionary; every sample status, extension, category and subcategory value is in `status-lookup-map.csv` | Pass |
| Sample users, assignments, tenants and organisations are mutually consistent; every user role is a known role | Pass |
| `PACKAGE_MANIFEST.md` lists exactly the files in the tree | Pass |
| 8 Mermaid diagrams render with a real Mermaid parser | Pass |

Not verified (cannot be, before code exists): that the proposed test IDs pass, that the proposed schema is implementable as described, performance budgets, and anything about IIS, Azure or live providers.

## M1 additions (2026-10-16)

`docs/ENVIRONMENT.md` (M0 report), `deploy/README.md` and `deploy/STATUS.md` (artefact verification log), source and test trees as listed in `PACKAGE_MANIFEST.md`. Naming note: the module namespace `Ecp.Migration` (bulk data migration) collides with the EF Core base class `Microsoft.EntityFrameworkCore.Migrations.Migration`; generated EF migrations therefore use the fully qualified base class (`tools/add-migration.sh` applies this). Consider renaming the namespace to `Ecp.DataMigration` before M6 (recorded, reversible).

## Added in the secure application and setup slice (2026-10-16)

`docs/SETUP_AND_ONBOARDING.md` (first-run wizard, setup protection, organisation onboarding; ASM-20 to ASM-24), `docs/ACCESS_AND_IDENTITY.md` (identity providers, scopes, the Viewer model, a finding about Tenant Administrator configuration rights), `docs/SUPPLY_CHAIN.md` (FND-07), `docs/MIGRATION.md` (migration slice: workflow, catalogue, evidence), `decisions/ADR-0018-migration-slice-design.md`, `tools/migration-demo.py`, `tools/generate-migration-sample-attachments.py`, `decisions/ADR-0015-datamigration-module-name.md`, `decisions/ADR-0016-setup-state-and-first-run-protection.md`, `decisions/ADR-0017-onboarding-as-a-configuration-kind.md`, `deploy/docker/verify-image.sh`, `templates/packs/` (Blank and reference pack as embedded data).
