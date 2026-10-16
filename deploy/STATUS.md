# Deployment verification log

Newest first. A line records what was actually run, where, and the result. Anything not listed is **Not Verified**.

| Date (UTC) | Artefact | Environment | Result |
|---|---|---|---|
| 2026-10-16 | Dockerfile **runtime stage** (`tools/docker-runtime-smoke.sh`) | Cloud workspace, nested Docker, host networking | **Verified**: image built, Demo `/health/ready` Healthy, process runs as non-root (uid 1654), Production with Demo/SQLite settings refuses to start. |
| 2026-10-16 | Dockerfile **SDK build stage** (`deploy/docker/smoke.sh --network host`) | Cloud workspace | **Not Verified**: restore failed inside the build container with `NU1301 UntrustedRoot` (the workspace's intercepting proxy CA is not trusted in the build container). A workspace limitation; the Dockerfile was not weakened. Runs unchanged in CI (`docker-image` job) on a normal runner. |
| 2026-10-16 | `compose.yml`, `compose.production.yml` | not run | Scaffolded / Not Verified |
| 2026-10-16 | IIS `web.config` and guide | no Windows/IIS | Scaffolded / Not Verified |
| 2026-10-16 | Azure `main.bicep` | no Azure subscription/Bicep compiler | Scaffolded / Not Verified |
| 2026-10-16 | `.github/workflows/ci.yml` | not run on a GitHub runner | Scaffolded / Not Verified |
