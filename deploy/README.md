# Deployment artefacts and verification status

Labels follow `CLAUDE.md`: **Implemented**, **Scaffolded / Not Verified**, **Verified (evidence)**. A scaffold is never "Passed". Cloud-workspace limits (no Windows, no Azure) are *not* reasons to weaken the product; they only limit what can be verified here.

| Artefact | Path | Status | Evidence / gap |
|---|---|---|---|
| Container image | `deploy/docker/Dockerfile` | Runtime stage Verified (re-run 2026-10-16 with the setup behaviours); SDK build stage Not Verified in the workspace (proxy CA) | `deploy/STATUS.md` |
| Local compose (Demo) | `deploy/docker/compose.yml` | Scaffolded / Not Verified | Not run with the compose plugin in the workspace |
| Production-shaped compose | `deploy/docker/compose.production.yml` | Scaffolded / Not Verified | Needs a SQL Server and secret file |
| IIS / Windows Server | `deploy/iis/` | Scaffolded / Not Verified | No Windows/IIS available |
| Azure (Bicep) | `deploy/azure/` | Scaffolded / Not Verified | Not compiled or deployed |
| CI workflow | `.github/workflows/ci.yml` | Scaffolded / Not Verified | Has not run on a GitHub runner yet (depends on runner availability and repository policy) |
| Host health/readiness | `src/Ecp.Host` | Implemented, tested | `Ecp.Api.Tests` |

Production approval is external governance and is not declared by any artefact here.
