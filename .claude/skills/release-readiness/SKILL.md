---
name: release-readiness
description: Assess demo, migration, no-code configuration, Docker, IIS, Azure, technical handover, security and production readiness separately, with an honest Passed / Failed / Skipped / NOT VERIFIED test report. Production approval is external governance and must never be declared. Use at milestone exit and for the final release review.
---

# Release Readiness

Run the project review skills first (`contract-domain-review`, `migration-review`, `no-code-config-review`, `portability-review`, `security-review`, `tenant-isolation-review`, `deployment-review`, `accessibility-review`, `documentation-review`), then assess each area **separately**.

## Read first
`prompts/90-RELEASE-READINESS.md`, `docs/PRODUCTION_READINESS_CHECKLIST.md`, `requirements/12-operations/SETUP_AND_READINESS_SPEC.md`, `docs/TESTING.md`, `TASKS.md`, `docs/ENVIRONMENT.md`.

## Procedure
1. Build and test from a clean checkout: restore (locked), build with warnings as errors, unit and architecture tests, then integration tests. Record exact commands and outcomes.
2. Produce the test report with four counters: **Passed, Failed, Skipped, NOT VERIFIED**. A test that needs SQL Server, a container engine, IIS, Azure or a live provider and could not run is NOT VERIFIED, never Passed. Skipped means deliberately excluded; list why.
3. Run the end-to-end Definition-of-Done journey (`E2E-DOD-001`) if its dependencies exist; otherwise mark it NOT VERIFIED.
4. Assess the areas below. For each give a status (Ready / Ready with conditions / Not ready / Not verified), the evidence and the blockers.

## Areas (each assessed separately)
1. **Demo:** clean start, Demo identity, synthetic data, no production credentials needed.
2. **Migration:** MIG suite results, sample run, reconciliation evidence.
3. **No-code configuration:** NCD and PRT-010 results.
4. **Docker:** image hardening (non-root, ICU, digest), Compose smoke, persistence across restart. Label by verification status.
5. **IIS:** publish output and `web.config` validated; label **Scaffolded / Not Verified** unless run on Windows/IIS.
6. **Azure:** Bicep validated; label **Scaffolded / Not Verified** unless deployed.
7. **Technical handover:** docs complete and link-checked, runbooks, ADR status, known limitations.
8. **Security:** security regression suite, isolation suite, SBOM, vulnerable-package and licence scans, threat-model currency.
9. **Production readiness:** readiness checks R-*, open critical/high findings, outstanding external approvals.

## Rules
- **Never declare production approval.** State that penetration testing, security review sign-off, accreditation, retention confirmation and formal approval are external governance.
- Fix safe defects, add regression tests, and record unresolved findings by severity (Critical / High / Medium / Low).
- Do not weaken production architecture to suit the current sandbox; record the environment limitation instead.

## Output
A report with the four test counters, the nine area statuses, findings by severity, items NOT VERIFIED with the reason, and the first incomplete task in `TASKS.md`.
