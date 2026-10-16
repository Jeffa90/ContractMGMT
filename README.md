# Enterprise Contract Platform v2

A complete requirements, sample-data, migration and prompt pack for Claude Code to build a portable, no-code enterprise contract-management platform. JBS Australia Group Procurement is the reference configuration, not hard-coded product behaviour.

## Upload to GitHub

1. Download and extract this ZIP.
2. Open the empty private GitHub repository.
3. Choose **Add file > Upload files**.
4. Drag the **contents inside** `enterprise-contract-platform-v2` into the upload area.
5. Commit to `main`.
6. Confirm that `CLAUDE.md`, `requirements/`, `prompts/`, `sample-data/`, `migration-samples/`, `.claude/skills/` and `docs/` appear at the repository root.

Do not upload the ZIP as the only repository file. Claude needs the extracted files.

## Start Claude Code Cloud

Open the repository in a Claude Code cloud session and paste the contents of `prompts/01-INITIAL-BUILD-PROMPT.md`.

## Data safety

Everything supplied here is synthetic. Never commit production credentials, signed contracts, supplier pricing, personal information, legal advice or live integration secrets.

## Run it (development)

```
dotnet run --project src/Ecp.Host                 # seeded synthetic Demo (SQLite); sign in as a demo user; not for real data
Ecp__DemoSeed=false dotnet run --project src/Ecp.Host   # unconfigured installation: run `dotnet src/Ecp.Host/bin/.../Ecp.Host.dll setup-token`, then open /Setup
```

Data migration (sign in as `procadmin@a.example.test` and open *Data migration*; the synthetic samples are in `migration-samples/`, a scripted end-to-end run is `python3 -I tools/migration-demo.py`): `docs/MIGRATION.md`. First-run setup and organisation onboarding: `docs/SETUP_AND_ONBOARDING.md`. Identity and access: `docs/ACCESS_AND_IDENTITY.md`. Build status, test counts and what is and is not verified: `TASKS.md`, `docs/TESTING.md`, `deploy/STATUS.md`. Nothing here declares production approval.
