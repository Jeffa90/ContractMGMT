---
name: portability-review
description: Find business-specific hard-coding (JBS, Medius, DocuSign, Qlik, role and state names) in compiled code and verify that the platform moves between businesses and deployment models by configuration, packs and exports rather than source changes. Use at each milestone exit and after any change to packs, packages, terminology, deployment artefacts or host abstractions.
---

# Portability Review

## Read first
1. `requirements/01-authoritative/PORTABILITY_AND_NO_CODE.md`, `DEPLOYMENT_REQUIREMENTS.md`
2. `requirements/08-portability/PORTABILITY_ACCEPTANCE_CRITERIA.md` (PRT-001 to PRT-015), `CONFIGURATION_PACKAGE_SPEC.md`, `DATA_EXPORT_SPEC.md`, `TENANCY_AND_ORGANISATION_SPEC.md`
3. `docs/ARCHITECTURE.md` sections 5 and 8, ADR-0005 (in `decisions/`)

## Procedure
1. **Static scan (PRT-001, ARC-010):** search `src/` for `JBS`, `JBSAUS`, `Medius`, `Qlik`, `DocuSign` (outside adapter projects), JBS status names, role names, permission strings and approver-group names. Allowed locations: `templates/`, `sample-data/`, `migration-samples/`, test fixtures, adapter projects, documentation. Every other hit is a finding.
2. **Second business (PRT-002):** install the non-JBS pack (different states, terms, fields, approval tiers) and walk a contract through its lifecycle with no source change.
3. **Round trips (PRT-006, PRT-011):** export configuration and a full tenant, import into an empty tenant, compare release hashes, counts and document hashes.
4. **Deployment parity (PRT-005, DEP-008):** the shared behaviour suite must pass for each deployment model that can run here. Models that cannot run are **Scaffolded / Not Verified**; never report them as passed.
5. Run the checklist.

## Checklist
- No signature-provider vocabulary (envelope, recipient tabs, signing ceremony, a vendor name) in `src/` outside a provider adapter (ADR-0027); the architecture test `The_signature_boundary_uses_no_signature_vendor_vocabulary_in_any_source_file` enforces it.
- No state, role, permission, approver-group or terminology literals in domain code; they come from definitions and the catalogue (PRT-013).
- Terminology aliases apply to labels, notifications, exports and help (PRT-004).
- Creating fields, forms, rules and workflows changes no schema (PRT-003).
- Packages and exports contain no secrets, live users or production assignments; malicious packages are rejected (PRT-007, PRT-008).
- Pack upgrade preserves local changes (PRT-009); catalogue installs nothing automatically.
- Host differences are confined to the `HostEnvironment` abstraction (storage, secrets, key ring, scheduler, paths); no `if (Docker)` branches in domain code.
- Culture, time zone and currency are per organisation (PRT-012); date-only values are not shifted by time zone.
- Agreement Reference numbering is an organisation rule (prefix, pattern), not hard-coded; the JBS pack supplies the JBSAUS-style rule.
- Container images contain ICU and time-zone data; IIS and Azure guides exist and are labelled by verification status.

## Severity
High: business logic hard-coded in compiled code, schema churn per field. Medium: deployment-model-specific domain branch. Low: documentation drift.

## Output
Findings table, scan output summary, and list of portability checks Not Verified with reason.
