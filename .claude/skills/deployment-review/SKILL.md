---
name: deployment-review
description: Review Docker, Windows Server/IIS and Azure deployment artefacts for correctness, hardening, persistence and honest verification labelling (Implemented / Scaffolded / Not Verified). Use after any change under deploy/, Dockerfile, CI workflows, host configuration or readiness checks.
---

# Deployment Review

## Read first
`requirements/01-authoritative/DEPLOYMENT_REQUIREMENTS.md`, `requirements/12-operations/SETUP_AND_READINESS_SPEC.md` (R-DEP-D1, R-DEP-I1, R-DEP-A1, R-SEC-04, R-ST-01), ADR-0005 (in `decisions/`), `docs/ARCHITECTURE.md` section 8, `docs/ENVIRONMENT.md`, `docs/TESTING.md` section 13.

## Labelling rule
Every artefact carries one label: **Verified** (actually run and tested here), **Scaffolded / Not Verified** (written and statically checked but not run on the target), or **Future**. Never promote a label without evidence of a run. Do not weaken the production design to fit the current sandbox.

## Checklist
- **Docker:** non-root user; ICU and time-zone data present; base image pinned by digest; no secrets in image or Compose file; named volumes for database, documents and key ring; health checks; read-only root filesystem where possible; SQL Server licensing note; Compose does not publish the database port by default.
- **IIS:** framework-dependent publish; ASP.NET Core Module in-process; application pool without managed code, always running or preload so background jobs survive idle; request limits; least-privilege folder ACLs; key ring in a protected directory; `web.config` validated; no setup exposure.
- **Azure:** Bicep reviewed for private Blob storage, managed identities for SQL/storage/Key Vault, Key Vault references not values, TLS minimum, no public database access by default, worker role with job lease for scale-out.
- **Shared:** same schema and migrations everywhere; migration pre-flight; health endpoints reveal no internals unauthenticated; Production fails closed on readiness failures.
- **CI:** minimal token permissions; no secrets committed; SQL Server integration job uses a disposable container with a generated password; actions pinned (tag now, commit SHA target); test report shows Passed / Failed / Skipped / NOT VERIFIED.
- **Backup and restore:** documented per model; restore validation script exists (BRS-002).

## Output
Per-model table (Docker, IIS, Azure): artefacts, label, evidence, gaps. Findings by severity.
