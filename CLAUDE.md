# Claude Code Repository Instructions

## Authority order
1. This file's security and working rules
2. `requirements/01-authoritative/MASTER_BUILD_INSTRUCTIONS.md`
3. `requirements/01-authoritative/CONFIRMED_DECISIONS.md`
4. `requirements/01-authoritative/PORTABILITY_AND_NO_CODE.md`
5. `requirements/09-migration/`
6. Remaining requirements, data dictionaries and workflow matrices
7. Reference summaries, samples and visual references

## Mandatory principles
- JBS is a shipped reference configuration, not universal hard-coded logic.
- Business administrators must configure fields, forms, rules, workflows, approvals, lookups, branding, terminology, notifications and templates without code.
- Configuration requires draft, validation, preview/simulation, publish, version history, rollback and audit.
- No customer configuration may execute arbitrary SQL, code, JavaScript or unsafe templates.
- Enforce tenant, organisation, role, ownership, classification, lifecycle and record-lock rules server-side.
- Effective contracts are immutable except through authorised Variations or audited emergency unlock.
- Store timestamps in UTC; display configured locale/time zone.
- Use Australian English for the JBS reference pack.
- Never commit or log secrets or live confidential data.
- Never declare production approval; that is external governance.

## Working discipline
- Inspect requirements before changing architecture.
- Maintain `TASKS.md`, implementation documentation and ADRs.
- Build tested vertical slices; do not stop at scaffolding.
- Run restore/build/migrations/tests and fix failures before claiming completion.
- Label functionality as implemented, mocked, disabled or future.
- Record contradictions and reversible assumptions rather than silently guessing.
