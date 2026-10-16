# Deployment verification log

Newest first. A line records what was actually run, where, and the result. Anything not listed is **Not Verified**.

| Date (UTC) | Artefact | Environment | Result |
|---|---|---|---|
| 2026-10-16 (after the setup slice) | Dockerfile **runtime stage** (`tools/docker-runtime-smoke.sh`, now running `deploy/docker/verify-image.sh`) | Cloud workspace, nested Docker, host networking | **Verified**: seeded Demo ready and `/Setup` closed (404); a fresh installation (`Ecp__DemoSeed=false`) redirects browsers to `/Setup` and answers 503 on the API; `setup-token` issued inside the container and not present in the container log; ICU and time-zone data present (R-OPS-02); non-root uid 1654; Production with Demo/SQLite settings refuses to start. |
| 2026-10-16 (after the setup slice) | Dockerfile **SDK build stage** (`deploy/docker/smoke.sh --network host`) | Cloud workspace | **Not Verified**: the build reached `dotnet publish` (so the new `COPY` of `templates/packs`, `sample-data` and `requirements/02-reference`, which the projects embed, succeeded) and then failed at restore with `NU1301 ... UntrustedRoot`: the workspace proxy's CA is not trusted inside the build container. Not worked around: no trust or validation was changed. CI's `docker-image` job runs the same script on a normal runner. Note: before this pass the Dockerfile did not copy those embedded files, so the SDK build would have failed on a normal runner too; `tools/check-repo.py` now fails the build if a project embeds a file that the Dockerfile does not copy or `.dockerignore` excludes. |
| 2026-10-16 | Dockerfile **runtime stage** (`tools/docker-runtime-smoke.sh`) | Cloud workspace, nested Docker, host networking | **Verified**: image built, Demo `/health/ready` Healthy, process runs as non-root (uid 1654), Production with Demo/SQLite settings refuses to start. |
| 2026-10-16 | Dockerfile **SDK build stage** (`deploy/docker/smoke.sh --network host`) | Cloud workspace | **Not Verified**: restore failed inside the build container with `NU1301 UntrustedRoot` (the workspace's intercepting proxy CA is not trusted in the build container). A workspace limitation; the Dockerfile was not weakened. Runs unchanged in CI (`docker-image` job) on a normal runner. |
| 2026-10-16 | `compose.yml`, `compose.production.yml` | not run | Scaffolded / Not Verified |
| 2026-10-16 | IIS `web.config` and guide | no Windows/IIS | Scaffolded / Not Verified |
| 2026-10-16 | Azure `main.bicep` | no Azure subscription/Bicep compiler | Scaffolded / Not Verified |
| 2026-10-16 | `.github/workflows/ci.yml` | not run on a GitHub runner | Scaffolded / Not Verified |
