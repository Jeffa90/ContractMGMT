# QA deployment: what's left for you to do

Branch: `Deployment-Pipeline-Change`. This builds on ADR-0045 (unattended first install, failsafe administrators) and ADR-0046 (pack signing).

## What changed

You no longer need to log on to the web server. Each deploy now does the following:

1. **Builds the app and signs the shipped solution packs** with the QA key, then checks them the way Production mode will.
2. **Creates the database tables** on an empty database (`deploy-migrate`). If a later release changes an existing database, the deploy stops before copying anything and the old site keeps running. Follow `docs/UPGRADE_RUNBOOK.md`, then re-run the deploy.
3. **Copies the site.**
4. **Runs first-run setup** from `docs/jbs/setup-answers.qa.json` (`setup-apply`), using the same steps as the wizard. It also makes sure everyone in the failsafe admin list has administrator access, and it repeats that check on every deploy.

There's no more hosts-file entry, setup token, loopback-only `/Setup` page, or copying web.config settings into PowerShell.

## Your to-do list

### 1. Check your Windows logon name
Run `whoami` on your PC. The answer looks like `amh\jeff.judd`. That exact name (with the `AMH\` part) is what goes in the failsafe admin variable below. It isn't always the same as the first part of your email address.

### 2. Set the GitHub QA environment variables
Run these from any folder, replacing the values in angle brackets:

```
gh variable set SETUP_ANSWERS_FILE --env QA --repo Jeffa90/ContractMGMT --body "docs/jbs/setup-answers.qa.json"
gh variable set APPENV_Ecp__Security__FailsafeAdmins__0__Account     --env QA --repo Jeffa90/ContractMGMT --body "AMH\<your logon>"
gh variable set APPENV_Ecp__Security__FailsafeAdmins__0__DisplayName --env QA --repo Jeffa90/ContractMGMT --body "<your full name>"
gh variable set APPENV_Ecp__Security__FailsafeAdmins__0__Email       --env QA --repo Jeffa90/ContractMGMT --body "<your work email>"
```

To add more admins later, use `__1__`, `__2__` and so on. To remove an admin, delete their variables **and** end their role in People & Access. If you only end the role, the next deploy gives it back.

### 3. Create the pack signing key (once)
Run this on your PC. The private key **must not** be saved in the repository folder.

```powershell
dotnet src\Ecp.PackTool\bin\Release\net10.0\Ecp.PackTool.dll keygen --out-private C:\keys\jbs-packs-qa.key --out-public C:\keys\jbs-packs-qa.pub.pem
gh secret   set PACK_SIGNING_KEY --env QA --repo Jeffa90/ContractMGMT < C:\keys\jbs-packs-qa.key
gh variable set APPENV_Ecp__Packs__TrustedKeys__0__KeyId        --env QA --repo Jeffa90/ContractMGMT --body "jbs-packs-qa"
gh variable set APPENV_Ecp__Packs__TrustedKeys__0__PublicKeyPem --env QA --repo Jeffa90/ContractMGMT < C:\keys\jbs-packs-qa.pub.pem
```

(Build the solution in Release first so the tool exists.) Keep an offline copy of the private key, then delete it from your PC. Production will need its own key pair later.

### 4. Confirm the values in `docs/jbs/setup-answers.qa.json`
- **Tenant name:** currently "JBS Australia" (an assumption). Change it now if it's wrong. It's hard to change once setup has created the tenant.
- **Organisation:** the name, time zone (`Australia/Brisbane`) and currency (`AUD`).
- **Email (SMTP):** this is the main open item, because Production mode won't finish setup without a working, tested mail server. Get these from IT for `email.jbssa.com.au`:
  - the sender address (fill in `from`),
  - the port and whether TLS is used (`port`, `useTls`),
  - the SMTP username and password. Ask IT to set them as **system environment variables on the web server**, named `ECP_SMTP_USER` and `ECP_SMTP_PASSWORD`. The answers file only refers to those names and never holds the values.
- The setup test email goes to the first failsafe admin's email address.

### 5. Ask IT to check the server
- The runner's service account needs **Modify** on `C:\inetpub\JbsContracts\Data`, because setup state is stored there. It already needs the database roles `db_ddladmin`, `db_datareader` and `db_datawriter`.
- The DBA has created the `JBSContracts` database (still marked "DBA to confirm" in `docs/jbs/jbs-github-environments.env`).

### 6. First deploy
1. Merge this branch into the QA branch and push, or run the Deploy workflow by hand.
2. The deploy finishes with a **warning** saying setup isn't complete. That's expected: the app requires an administrator to have actually signed in.
3. From **your own PC**, open `https://jbs-contracts-qa.jbssa.com.au/Account/WindowsSignIn?testProvider=windows` once. You may land on a "not found" page afterwards. That's fine, because the sign-in has been recorded.
4. **Re-run the Deploy workflow.** Setup completes and the site opens normally.

If the second run still warns, the step list in the deploy log names the step that's stuck and why. Readiness warnings can be accepted by adding `"ack.<check id>": "<reason>"` under `readiness` in the answers file.

## Not verified yet
Everything above has passed tests, but none of it has run on the real QA server yet. The first deploy is that test. If something fails, the deploy log shows which step and why.

## Background reading
- `decisions/ADR-0045-unattended-first-install-and-failsafe-administrators.md`
- `decisions/ADR-0046-shipped-packs-signed-by-the-deploy-pipeline.md`
- `docs/DEPLOYMENT.md`, "First-run setup from the pipeline"
- `docs/jbs/CLAUDE_SKILL_IIS_UNATTENDED_INSTALL.md`: copy it to `%USERPROFILE%\.claude\skills\jbs-iis-unattended-install\SKILL.md` so Claude Code on your machine knows this pattern for future projects.
