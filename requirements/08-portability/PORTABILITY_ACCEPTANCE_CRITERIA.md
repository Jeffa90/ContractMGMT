# Portability and No-Code Acceptance Criteria

Turns `requirements/01-authoritative/PORTABILITY_AND_NO_CODE.md` into measurable checks (GAP-14). Each criterion has an automated test (IDs in `docs/TESTING.md` section 9) and is a release gate for the portability review (`.claude/skills/portability-review/SKILL.md`). `[CONFIRMED]`

| ID | Criterion | How it is checked |
|---|---|---|
| PRT-001 | No business-specific names or logic in compiled code: the strings `JBS`, `Medius`, `Qlik`, `DocuSign` (outside adapter namespaces), the JBS status names and role names appear only under `templates/`, `sample-data/`, `migration-samples/`, test fixtures and adapter projects | Static scan in CI with an allowlist; failure blocks merge |
| PRT-002 | **Second business**: a non-JBS pack (for example a Construction demo pack with different states, terms, fields and approval tiers) installs and runs the whole lifecycle with **no source change** | End-to-end test installs the pack and walks a contract through its workflow |
| PRT-003 | Creating 50 custom fields, 5 forms, 10 rules, a workflow and an approval matrix changes **no database schema** | Compare schema hash before and after; migrations table unchanged |
| PRT-004 | Terminology aliases apply to every UI label, notification, export header and help page | Render test with a renamed vocabulary; no default term remains |
| PRT-005 | The same behaviour test suite passes under each supported deployment model's configuration (Docker; IIS and Azure to the extent verifiable) | Shared contract/behaviour suite parameterised by environment |
| PRT-006 | Configuration export then import into an empty tenant yields an identical release hash | Round-trip test |
| PRT-007 | Packages contain no secrets, live users or production assignments | Package scanner with seeded canary secrets; none found |
| PRT-008 | A malicious package (traversal, bomb, macro file, unsigned in Production, oversize, unknown definition kind, hash mismatch) is rejected without side effects | Negative package test set |
| PRT-009 | Upgrading a pack preserves local modifications and reports conflicts | Three-way upgrade test |
| PRT-010 | Branding, terminology, workflow, rules and approval changes can be completed by a Business Configuration Administrator using only the UI | Browser test with that role only; no SQL or file access |
| PRT-011 | Full tenant export imports into an empty tenant with matching counts and hashes | Round-trip test (`DATA_EXPORT_SPEC.md` section 3) |
| PRT-012 | Different culture, time zone and currency per organisation behave correctly (display, reminders due times, approvals) | Parameterised tests across en-AU, en-NZ, a UTC+10 and a UTC-8 zone |
| PRT-013 | Role names, state names and permission codes are never hard-coded in domain logic; they come from definitions and the catalogue | Static scan plus test with renamed roles and states |
| PRT-014 | Tenant isolation holds for every package, export and import path | Tenant-isolation suite includes package/export/import (TEN-0xx) |
| PRT-015 | Feature flags only enable existing capabilities and are tenant/organisation scoped | Flag tests |
