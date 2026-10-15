# Requirements Index

This is the map of the `requirements/` tree and the rules for reading it.

## Authority

`CLAUDE.md` defines the authority order. In summary: `CLAUDE.md` rules, then `01-authoritative/`, then `09-migration/`, then everything else in this tree, then samples and visual references. The owner approved the review's proposals in decision record DR-2026-10-15 (`01-authoritative/CONFIRMED_DECISIONS.md`), which is the highest-ranking requirement text after `CLAUDE.md` and supersedes conflicting text elsewhere (including `MASTER_BUILD_INSTRUCTIONS.md`). Within files added by the review, statements carry one of two tags:

| Tag | Meaning | May it be changed? |
|---|---|---|
| `[CONFIRMED]` | Traceable to `CLAUDE.md`, `01-authoritative/` (including DR-2026-10-15), `09-migration/` or a named data file, or approved as part of the review baseline | Only by the owner of the authoritative source |
| `[OPEN]` | Depends on a question that has not been answered (`Q-nn` in `docs/REPOSITORY_REVIEW.md`). The stated default is implemented behind configuration so a different answer changes data, not architecture | Yes, when the question is answered |

The earlier tag `[PROPOSED]` has been retired: items it covered are now `[CONFIRMED]` or `[OPEN]`. Files created before the review carry no tags; treat their content as `[CONFIRMED]` unless a decision record says otherwise.

## Folder map

| Folder | Purpose | State |
|---|---|---|
| `01-authoritative/` | Master build, confirmed decisions, portability and no-code, deployment | Original, untouched |
| `02-reference/` | Status convention, glossary, permission catalogue, role-permission matrix, reference lookups | Added by repository review |
| `03-data-dictionary/` | Contract dictionary (original) plus extended, supplier, category, organisation, document, lifecycle, audit and migration entities | Original plus additions |
| `04-workflows/` | Status transitions (original) plus workflow specification, guard vocabulary and supplemental transitions (five confirmed, four open) | Original plus additions |
| `05-security/` | Roles (original) plus access-control, audit and retention/classification specifications | Original plus additions |
| `06-design/` | Visual references (original) plus accessibility and localisation specification | Original plus addition |
| `07-integrations/` | Provider/adapter catalogue and specifications for identity, e-signature, reporting, API/webhooks, notifications | Added by repository review |
| `08-portability/` | Tenancy and organisation model, configuration lifecycle, package format, Configuration Studio, data export, portability acceptance | Added by repository review |
| `09-migration/` | Migration requirements (original, authoritative level 5) plus a supplement | Original plus addition |
| `10-solution-packs/` | Solution pack specification | Original (format detail in `08-portability/CONFIGURATION_PACKAGE_SPEC.md`) |
| `11-ai/` | AI assistance and supplier intelligence | Original (adapter detail in `07-integrations/ADAPTER_CATALOGUE.md`) |
| `12-operations/` | Operations specification (original) plus setup and readiness specification | Original plus addition |
| `13-marketplace/` | Catalogue specification | Original (trust model in `08-portability/CONFIGURATION_PACKAGE_SPEC.md`) |

## Cross-reference table (topic to file)

| Topic | Read first | Then |
|---|---|---|
| Terms and scopes | `02-reference/GLOSSARY.md` | `08-portability/TENANCY_AND_ORGANISATION_SPEC.md` |
| Who may do what | `05-security/roles-and-permissions.csv` | `02-reference/permission-catalogue.csv`, `02-reference/role-permission-matrix.csv`, `05-security/ACCESS_CONTROL_SPEC.md` |
| Contract lifecycle | `04-workflows/status-transitions.csv` | `04-workflows/WORKFLOW_SPEC.md`, `04-workflows/status-transitions-supplemental.csv`, `04-workflows/guard-vocabulary.csv` |
| Data fields | `03-data-dictionary/contracts.csv` | `03-data-dictionary/README.md` and the other CSVs there |
| No-code administration | `01-authoritative/PORTABILITY_AND_NO_CODE.md` | `08-portability/CONFIGURATION_STUDIO_SPEC.md`, `08-portability/CONFIGURATION_LIFECYCLE_SPEC.md` |
| Packs and packages | `10-solution-packs/SOLUTION_PACK_SPEC.md` | `08-portability/CONFIGURATION_PACKAGE_SPEC.md`, `13-marketplace/CATALOGUE_SPEC.md` |
| Migration | `09-migration/MIGRATION_REQUIREMENTS.md` | `09-migration/MIGRATION_SUPPLEMENT.md`, `ATTACHMENT_INGESTION.md`, `SOURCE_TO_TARGET_MAPPING.md` |
| Installation | `01-authoritative/DEPLOYMENT_REQUIREMENTS.md` | `12-operations/SETUP_AND_READINESS_SPEC.md`, `12-operations/OPERATIONS_SPEC.md` |
| Integrations | `07-integrations/README.md` | the specification for the provider concerned |
| Audit and retention | `05-security/AUDIT_SPEC.md` | `05-security/RETENTION_AND_CLASSIFICATION_SPEC.md` |

Design documents (how the requirements will be met) live in `docs/`; start at `docs/DOCUMENT_INDEX.md`.
