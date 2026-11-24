---
name: jbs-iis-unattended-install
description: Make a JBS internal web app deploy to an on-prem IIS server (QA or Production) with no manual steps on the server - database schema created by the pipeline, first administrator from configuration (FailsafeAdmins), no setup wizard, hosts file or setup token. Use when setting up or fixing a GitHub Actions -> IIS deployment, when a deploy still needs someone to log on to the server to run migrations or a setup/install wizard, or when deciding how the first administrator gets access to a new app.
---

# Unattended IIS deployment for JBS internal apps

**How to install this on your machine:** save this file as `%USERPROFILE%\.claude\skills\jbs-iis-unattended-install\SKILL.md` and restart Claude Code. It then applies to every project. It works alongside the team's `jbs-dev-standards:jbs-internal-web-app` skill (install that plugin too; it has the deploy templates). Where the two disagree, the team skill wins.

## The goal

After the first push to the deploy branch, nobody logs on to the web server. In particular, nobody should have to:

- copy web.config settings into a PowerShell window,
- run migrations by hand,
- open a loopback-only setup page (hosts-file tricks, `127.0.0.1` entries),
- type a one-time setup token,
- type their own `DOMAIN\name` into a wizard.

If an app needs any of these, treat it as a gap to close in the pipeline. Don't write it up as an instruction for a person.

## The pattern (what our pipelines do)

1. **Self-hosted runner on the web server.** It has the label `iis-qa` or `iis-prod` and runs as a service account. That account is the **migration identity**, with `db_ddladmin`, `db_datareader` and `db_datawriter`. The app pool account gets `db_datareader` and `db_datawriter` only.
2. **Settings come from GitHub environment variables.** Variables prefixed `APPENV_` are stamped into `web.config` as environment variables, for example `APPENV_ConnectionStrings__Default`. Never stamp secrets. Use Windows integrated authentication for SQL, and keep secrets as `env:`/`file:` references resolved on the server.
3. **The schema is created by the pipeline, before the files are copied.** The deploy step runs a migration command from the freshly built package. It sets the same `APPENV_` values as process environment variables first, which is why nobody needs to copy them from web.config. If migration fails, nothing is copied and the old site keeps running.
   - For plain SQL scripts, use `Invoke-SqlMigrations.ps1` from the team templates.
   - For EF Core, use a CLI verb in the app, for example `dotnet MyApp.dll migrate`.
   - If the app deliberately protects upgrades (backup first, maintenance mode), automate **only** the fresh install and the "nothing pending" case. Make the pipeline **stop before copying** with a clear message when an upgrade is pending. Don't bypass the app's upgrade procedure.
4. **The first administrator comes from configuration: `FailsafeAdmins`.**
   - Configure a list of Windows accounts (`AMH\username`) that are always administrators, so a mistake in in-app role setup can't lock everyone out.
   - Set it as GitHub variables, never in the repo: `APPENV_Security__FailsafeAdmins__0` for a simple app, or `APPENV_<Section>__FailsafeAdmins__0__Account`, `__DisplayName` and `__Email` when the app needs a name and email to create the user.
   - The Windows account is `AMH\` plus the person's **Windows logon name**, which isn't always the part of their email before the `@`. Ask, or check with `whoami` on their PC.
5. **If the app has a setup or install wizard, drive the same steps headlessly.** Add a CLI verb (for example `setup-apply --answers <file>`) that calls the **same service** the wizard calls, with the same validation and audit trail. Don't write a second, weaker path.
   - The answers file is in the repo, holds no personal data and no secrets, and is named by a GitHub variable (for example `SETUP_ANSWERS_FILE`).
   - The administrator's identity comes from `FailsafeAdmins`, not from the answers file.
   - Make the verb idempotent and run it on every deploy. Once setup is complete it only re-checks that the failsafe admins still hold their role.
   - Exit codes: 0 means done, 3 means waiting on a person (the pipeline shows a warning, not a failure), anything else fails the deploy.
6. **If the app insists that a human proves sign-in works** (a "test sign-in" readiness check), keep it. Make the proof a normal sign-in from the admin's own PC: a URL the setup gate already allows. Then re-run the workflow.
7. **If the app verifies signed configuration packages at runtime** (packs, plugins, templates that admins can also upload), sign the ones the build ships **in the build**. Keep the private key as an environment secret and pass it as `env:` to the signing tool, never as a file in the repo. Set the public key and key id as `APPENV_` variables so the app trusts that key. Verify each package the way production will before deploying. The shipped packages then carry the same trust as the code. Don't turn off the app's signature check.
8. **Smoke test** an anonymous `/health/live` (or `/warmup`) URL after the deploy.

## Checklist when you pick up an app

- [ ] Read the deploy workflow (`.github/workflows/*deploy*.yml`) and the deploy docs. Look for comments like "migrations are NOT run by this pipeline" and lists of manual first-install steps.
- [ ] Find the app's CLI verbs (search `Program.cs` for `args[0]` / `case "migrate"`) and any setup service or wizard.
- [ ] Check the readiness and production guards that block an automated install. Typical ones: SMTP must be real and tested, packages must be signed, an administrator must have signed in. Don't weaken them. List each one for the owner with what's needed to satisfy it.
- [ ] Add the migrate step before the copy and the setup step after it, plus the `FailsafeAdmins` and answers-file variables.
- [ ] If the repo has a vocabulary or portability guard (no business names or regional defaults in `src/`, `deploy/`, `.github/`), put JBS-specific values in the JBS docs/config area (for example `docs/jbs/`) and point to them with a variable.
- [ ] Write an end-to-end test that runs the headless setup against the real host (SQLite or test mode is fine). It should prove: the first run waits for sign-in, a failsafe admin signs in, the second run completes, and a second failsafe admin can sign in.
- [ ] Record the decision in an ADR if the repo keeps them, update `TASKS.md` and the deploy docs, and label what's verified and what isn't. "Not verified on IIS" until a real deploy has run.
- [ ] Keep `scripts/ci/*.ps1` ASCII-only (they run on Windows PowerShell 5.1).

## GitHub environment variables (QA example)

```
IIS_SITE_NAME, IIS_APP_POOL, IIS_SITE_PATH, IIS_BINDING, IIS_CERT_THUMBPRINT, IIS_APP_POOL_USER, SMOKE_TEST_URL
APPENV_...                                   app settings (no secrets)
APPENV_<Section>__FailsafeAdmins__0__Account   AMH\username
APPENV_<Section>__FailsafeAdmins__0__DisplayName
APPENV_<Section>__FailsafeAdmins__0__Email
SETUP_ANSWERS_FILE                           only if the app has a setup wizard
Secret: IIS_APP_POOL_PASSWORD                only if the pool runs as a domain account
Secret: PACK_SIGNING_KEY (+ public key/key id variables)   only if the app verifies signed packages
```

Set them with `gh variable set NAME --env QA --body "value"`. Get the repo owner's agreement before changing environments or secrets on someone else's repository.

## Server prerequisites (ask IT once)

The runner is installed as a service, with a label. The runner account is local admin for the first deploy only, and has Modify on the site folder and the data folder. You also need DNS, a certificate, a Kerberos SPN for the pool account, and these IIS features: the Hosting Bundle, Windows Authentication and Application Initialization. The DBA creates an empty database with the role grants above.
