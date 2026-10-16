# Environment Report (M0)

Task T0.1. Facts verified in the Claude Code cloud workspace on 2026-10-15. Nothing here is assumed; each line says what was run. The workspace is ephemeral: `tools/dev-sandbox.sh` recreates it.

## Summary

| Capability | Result | Evidence |
|---|---|---|
| .NET 10 SDK | **Installed and verified** | `apt-get install dotnet-sdk-10.0`; `dotnet --version` = **10.0.112**; runtimes `Microsoft.NETCore.App 10.0.12`, `Microsoft.AspNetCore.App 10.0.12` |
| NuGet restore and build | **Verified** | A throwaway console project restored EF Core packages from `api.nuget.org` and built (6 s) |
| `dotnet-ef` migrations tool | **Installed** | `dotnet tool install --global dotnet-ef --version 10.0.12` |
| Container engine | **Works with limits** | `dockerd --iptables=false --bridge=none` starts; there is no bridge network, so containers must use `--network host`; `docker pull` of the SQL Server image through the proxy succeeded |
| SQL Server | **Verified, disposable** | `mcr.microsoft.com/mssql/server:2022-latest` (digest `sha256:cf8f0ab2ebe61feec2eed2629061a07d247f791c2a8ddda74a4ac9f06a291718`) running as `Microsoft SQL Server 2022 (RTM-CU27-GDR) 16.0.4305.5`, Developer Edition (64-bit); query and `SESSION_CONTEXT` verified |
| Playwright / Chromium | Present, not used yet | `/opt/pw-browsers/chromium-1194` |
| IIS | **Not available** (Linux only) | Artefacts are *Scaffolded / Not Verified* |
| Azure | **Not available** (no `az`, no `bicep`, no subscription) | Artefacts are *Scaffolded / Not Verified* |
| Docker Compose plugin | `docker compose` availability is checked by the smoke script; `docker-compose` standalone is absent | Compose file is validated with `docker compose config` where the plugin exists |

Blocked by the proxy (not needed): `dot.net`, `builds.dotnet.microsoft.com`, `github.com` web pages. The SDK therefore comes from the Ubuntu archive; the pinned SDK is `global.json`.

## Decision outcome (D4)

Real SQL Server integration tests **can** run in this workspace through a disposable container. The testing policy still applies: those tests are tagged `SqlServer`, run only when `ECP_TEST_SQL` is set, and are reported **NOT VERIFIED** (never Passed) when it is not. Docker, IIS and Azure deployment artefacts keep their own labels (see `deploy/README.md`).

SQL Server licence: the container is run with `ACCEPT_EULA=Y` and `MSSQL_PID=Developer`. Developer edition is licensed for development and testing only. Operators who run `tools/sqlserver-up.sh` accept the licence terms themselves; it is used here only for disposable test data.

## Recreating the environment

```
tools/dev-sandbox.sh                       # SDK, dotnet-ef, container daemon, disposable SQL Server
set -a; . .ecp-dev/test-sql.env; set +a    # exports ECP_TEST_SQL (git-ignored file; contains a generated password)
dotnet test src/Ecp.sln                    # provider-independent + SQL Server suites
```

Without a SQL Server connection, `dotnet test --filter "Category!=SqlServer"` runs the provider-independent suites; `tools/test-report.sh` prints the four counters (Passed, Failed, Skipped, NOT VERIFIED).

## Known limitations

- No bridge networking in the nested daemon: use `--network host` and distinct ports.
- The daemon and SQL Server container do not survive a new session; run the script again.
- Cloud-workspace-specific choices (host networking, no iptables) are confined to `tools/`; they do not affect the product's Docker deployment artefacts, which target a normal Docker engine.
