# ADR-0005: Deployment Models

Status: Proposed

## Decision
Support Docker, IIS and Azure with shared business behaviour.

## Context
`DEPLOYMENT_REQUIREMENTS.md`: Docker and Compose primary (portable/demo), Windows Server/IIS without Docker dependency, Azure with Azure SQL and persistent storage; same wizard, schema, migrations, domain behaviour and configuration packages. The build environment (verified) is Linux with no .NET SDK, no running container daemon and no Windows/IIS/Azure.

## Decision details
- One codebase, one schema. Differences live in a `HostEnvironment` abstraction: secret provider, storage provider, key-ring store, scheduler role, paths (`docs/ARCHITECTURE.md` section 8).
- Docker: non-root image with ICU, pinned digest, named volumes for database, documents and key ring; optional ClamAV and mail-capture services.
- IIS: framework-dependent publish, ASP.NET Core Module in-process, app pool without managed code, always-running/preload, least-privilege ACLs.
- Azure: App Service (or container) with Azure SQL, private Blob storage, Key Vault, managed identities, Application Insights, Bicep; worker role with SQL application-lock lease for scale-out.
- Labelling: Docker artefacts *implemented* only if run; IIS and Azure artefacts are *scaffold, unverified* until run on those platforms (`CLAUDE.md`).
- SQL Server for tests follows `docs/TESTING.md` section 3; the T0.1 spike outcome is recorded here when known: **pending**.

## Alternatives considered
- Docker only: violates requirements.
- Platform-specific forks: violates portability.
- Kubernetes first: not required; Compose and App Service cover the stated models.

## Consequences
- **Security:** readiness checks per model (R-DEP-D1, R-DEP-I1, R-DEP-A1); Production fails closed.
- **Operations:** key ring and secrets are per-model; install guides per model are deliverables (DOCS-INSTALL).
- **Licensing:** SQL Server container licensing (Developer for non-production) must be stated in the Docker guide.
- **Reversal:** low; abstractions are small.

## Verification
DEP-001 to DEP-008, PRT-005.
