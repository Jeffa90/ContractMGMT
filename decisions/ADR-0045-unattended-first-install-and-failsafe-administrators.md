# ADR-0045: Unattended first install from the deploy pipeline, and failsafe administrators from host configuration

Status: Implemented (tests pass on SQLite in Test mode); **Not Verified on IIS**. Owner decision 2026-11-24 (Jeff, through Clayton): align with the other JBS internal apps, whose pipeline creates the database and whose administrators come from configuration (`FailsafeAdmins`). Production approval remains external governance and is not declared.

## Context

A first QA install needed a person on the web server: copy the web.config settings into a PowerShell window, run `migrate`, run `setup-token`, add a hosts-file line so the browser reaches the loopback-only `/Setup` wizard, and type the administrator's `DOMAIN\name`. The other JBS apps deployed by the same GitHub Actions to IIS pattern need none of that.

## Decision

1. **`deploy-migrate`** (new CLI command, run by `iis-deploy.yml` as the runner account before any file is copied). Fresh database: applies the migrations (what `migrate` does). Installed database with nothing pending: no-op. Installed Production database that this release would change: exits 2 with the upgrade-runbook instruction, and the deploy stops with the old site still running. The backup-then-`upgrade-apply` rule for an installed Production database is **unchanged**; only the first install and no-change deploys are automated.
2. **`Ecp:Security:FailsafeAdmins`** (host configuration, set as `APPENV_Ecp__Security__FailsafeAdmins__N__Account|DisplayName|Email` variables on the GitHub environment; never in the repository). Each entry is a Windows account (`DOMAIN\name`) and an email address.
3. **`setup-apply --answers <file>`** (new CLI command, run by `iis-deploy.yml` after the copy when the environment sets `SETUP_ANSWERS_FILE`). It runs the **same setup stages** the wizard runs, through `ISetupService.ExecuteAsync`, with the same validation and audit. Values are the stage's own defaults, then the answers file, then (administrator step only) the first failsafe administrator. Once the tenant and identity provider exist, every failsafe administrator is invited by Windows account and holds an active Tenant Administrator role and an installation (System) Administrator account. It runs on every deploy, so a configured administrator whose role was removed gets it back. A **disabled** account is reported and left disabled.
4. **What still needs a person.** Production readiness still requires a real sign-in (R-ID-02, R-ADM-01). The administrator opens `<site>/Account/WindowsSignIn?testProvider=windows` once from their own PC (the setup gate already allows this path), and the next deploy finishes setup. No hosts file, no token, no server logon. Production readiness also still requires a signed pack and a working SMTP provider; this ADR does not relax either.

## What does not change

The wizard, its loopback restriction and the one-time token are unchanged for anyone who uses them. Windows Authentication still establishes identity only. No role is read from Windows. A person who is not a failsafe administrator must still be invited. No secret is in the answers file or in `web.config`.

## Consequences

* The runner's service account needs read and write access to the data directory and its key ring (setup state is protected by Data Protection), as well as the migration database roles of ADR-0044.
* Configuration is now a second way to hold the administrator role. This is a deliberate break-glass. Removing someone means removing their `FailsafeAdmins` entry **and** ending their role in People & Access; doing only the second is undone by the next deploy.
* Reversal: unset `SETUP_ANSWERS_FILE` and the `FailsafeAdmins` variables; the pipeline then skips `setup-apply`, and `deploy-migrate` behaves as `migrate` on a fresh database.

Tests: `tests/Ecp.Api.Tests/DeploymentCliTests.cs`.
