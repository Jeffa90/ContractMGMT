Treat this repository as the complete requirements and delivery workspace for Enterprise Contract Platform v2.

Read `CLAUDE.md`, `requirements/01-authoritative/MASTER_BUILD_INSTRUCTIONS.md`, all migration requirements, all remaining requirements, data dictionaries, workflows, sample data, decisions, skills and documentation.

JBS Australia Group Procurement is the first solution pack, not hard-coded universal behaviour.

Inspect the cloud development environment and repository. Create and maintain implementation, architecture, domain-model, security, threat-model, testing, migration and handover documents, ADRs and TASKS.md.

Build actual working software. Do not stop at summaries, plans, wireframes, empty solutions, interfaces or placeholder scaffolding.

Implement the first tested vertical slice:
1. Modular-monolith solution
2. SQL Server model and migrations
3. Tenant isolation
4. Docker deployment
5. IIS publish foundation
6. Azure deployment scaffold
7. Resumable first-run setup wizard
8. Organisation onboarding wizard
9. Demo identity plus OIDC/Entra configuration points
10. Server-side roles/policies and delegated administration foundation
11. Supplier, Contract, Document and MigrationBatch core models
12. Basic contract create/view/search/edit
13. Category-ownership security
14. Audit events
15. Health/readiness
16. Automated tests and verified run commands

Migration is Phase 1 mandatory. After the foundation, implement a working migration vertical slice with CSV/XLSX metadata import, attachment manifest and ZIP ingestion, dry run, mapping, validation, deterministic matching, unmatched queue, commit, reconciliation, idempotent resume and safe batch rollback.

No-code administration is mandatory. New fields, forms, rules, workflows, approval matrices, lookups, terminology, branding and notifications must not require source changes or per-field database migrations. Configuration requires draft, validation, preview/simulation, publish, version, rollback and audit. No arbitrary executable code.

Enforce tenant, organisation, role, ownership, classification, lifecycle and record-lock rules server-side and test cross-tenant access across records, documents, jobs, caches, reports, imports and exports.

Update TASKS.md before phases. Implement coherent increments, run build/migrations/tests, fix failures, update docs and continue without waiting when the next task is defined.

Start now. End with working functionality, tests, exact commands, blockers, decisions and the first incomplete task.
