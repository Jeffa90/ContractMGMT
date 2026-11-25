# JBS Contracts: go-live and handover note for IT

This note is for JBS IT and the DBA. It covers what Production needs, what to back up and how to look after the app once it's live. It sits alongside `docs/jbs/QA_CONFIGURATION_RECORD.md` (the QA set-up that works, written for the old app but the same server shape) and `jbs-contracts/docs/product-spec.md` (section 16, switching over).

**Status (26 Nov 2026):** QA runs the new app from the `redesign` branch. **Production hasn't been set up and hasn't been deployed to.** Nothing in this note has been tried on a Production server yet; treat every Production step as Not verified until it's done. Approving go-live is JBS's decision (governance), not part of this note.

## 1. What it is, in one paragraph

JBS Contracts is one ASP.NET Core (.NET 10) website on IIS that serves both the screens and the API. People sign in with Windows sign-in (no passwords in the app). It keeps its data in one SQL Server database (tables in the `contracts` schema, Windows integrated authentication) and its documents in a folder on the web server. It sends email through the JBS anonymous relay. It calls nothing outside JBS: no cloud services, CDNs, analytics or web fonts. A background job inside the app sends reminders every morning, so the app pool must stay running.

## 2. Production: what to set up first (IT and DBA)

The same as QA (configuration record, sections 1 and 2), with these differences.

| Item | Production | Notes |
|---|---|---|
| Web server | A Windows server with IIS, the .NET 10 Hosting Bundle and IIS Windows Authentication | Same prerequisites as QA |
| Site address and certificate | A Production name (for example `jbs-contracts.jbssa.com.au`), a DNS record and a certificate for it | The deploy binds the certificate by its thumbprint |
| Folders | App `C:\inetpub\JbsContracts\app`, data `C:\inetpub\JbsContracts\Data`, logs `C:\inetpub\JbsContracts\logs` | The deploy grants the app pool Read on the app folder and Modify on data and logs |
| App pool identity | A Production service account, set in IIS Manager | Runs the website |
| GitHub runner | A self-hosted runner on the Production web server, label `iis-prod`, as **a different account from the app pool** (decision 25 Nov), local administrator for IIS changes | Runs the deploy and the database scripts |
| Database | An empty database `JBSContracts` on the Production SQL Server | The first deploy creates every table |
| Database roles | Runner account: `db_ddladmin`, `db_datareader`, `db_datawriter`. App pool account: `db_datareader`, `db_datawriter` | No `db_owner`. In QA one account has all three; in Production they're split. Not verified: confirm on the first deploy |
| SQL certificate | A trusted certificate on the SQL Server, so the connection string can drop `TrustServerCertificate=True` | Still open for QA too |
| HSTS | Turn on HSTS for the site in IIS (Site, HSTS: enabled, max-age one year), with HTTP redirected to HTTPS | The app sets its other security headers itself; HSTS is best set in IIS |
| Virus scanning | The server's antivirus scans `C:\inetpub\JbsContracts\Data` | The app doesn't scan uploads; JBS scans files before people upload them (decision recorded for the old app, ADR-0049) |

## 3. GitHub: the Production environment

In GitHub, Settings, Environments, create **Production**:

- **Deployment branches:** `main` only.
- **Required reviewer:** the person JBS names to approve Production deploys.
- **Secrets:** none. Only set `IIS_APP_POOL_PASSWORD` if the deploy must create the app pool itself (normally IT sets the identity in IIS Manager instead).

**Variables** (values are examples; use the real ones):

| Variable | Value |
|---|---|
| `IIS_SITE_NAME` / `IIS_APP_POOL` | `JbsContracts` |
| `IIS_SITE_PATH` | `C:\inetpub\JbsContracts\app` |
| `IIS_BINDING` | `https:*:443:jbs-contracts.jbssa.com.au` |
| `IIS_CERT_THUMBPRINT` | the Production certificate's thumbprint |
| `IIS_APP_POOL_USER` | the Production app pool account, `AMH\...` |
| `SMOKE_TEST_URL` | `https://jbs-contracts.jbssa.com.au/health/live` |
| `APPENV_Database__ConnectionString` | `Server=<Production SQL Server>;Database=JBSContracts;Integrated Security=True;Encrypt=True` (no password, ever; the deploy refuses one) |
| `APPENV_Contracts__DataDirectory` | `C:\inetpub\JbsContracts\Data` |
| `APPENV_Contracts__Logging__Directory` | `C:\inetpub\JbsContracts\logs` |
| `APPENV_Contracts__Logging__FileName` / `__RetainedFiles` | `log-.txt` / `31` |
| `APPENV_Contracts__SiteUrl` | `https://jbs-contracts.jbssa.com.au` (used for links in emails) |
| `APPENV_Contracts__Email__Host` | `email.jbssa.com.au` |
| `APPENV_Contracts__Email__Port` | `25` |
| `APPENV_Contracts__Email__From` / `__FromName` | `no-reply@jbssa.com.au` / `JBS Contracts` |
| `APPENV_Contracts__FirstAdmins__0__Account` / `__DisplayName` / `__Email` | the first admin's `AMH\` logon, name and work email (add `__1__`, `__2__` for more) |

**Never set on Production:** `APPENV_Contracts__AuthMode` or `APPENV_Contracts__ApplyDatabaseScripts` (the deploy refuses them; they're for a developer's computer), `APPENV_Contracts__AllowDemoData` (ignored in Production anyway: demo data and Try as are always off there), or `RESET_DATABASE_CONFIRM` (it was a one-off for QA).

The app also reads the old app's `APPENV_Ecp__...` names (QA still uses some of them). For Production, use only the `Contracts__` names above.

## 4. Fonts

JBS's own fonts aren't in the repository (they're licensed and the repository is public). Copy `JBSVariableText.woff2` and `JBSVariableDisplay.woff2` (from the JBS Webfonts pack) into `C:\inetpub\JbsContracts\Data\fonts` on each server. The deploy creates the folder and warns while it's empty; until then the app shows Inter. No restart is needed.

## 5. The first Production deploy

1. **Switch-over (developer and Jeff):** merge `redesign` into `main`; set QA's deployment branches back to `main` only; remove the old app's code from `main` (it stays in history under the tag `legacy-ecp-final`).
2. **DBA:** take a backup of the empty database (a known starting point).
3. **Jeff:** in GitHub, Actions, **Deploy**, Run workflow on `main` with **promote-to-production** ticked. It deploys to QA first; only if QA succeeds does it wait for the Production reviewer.
4. **Reviewer:** approve. The deploy runs the database scripts (as the runner account), copies the site, configures IIS and checks `/health/live`, which also checks the database. If the check fails, the log prints the IIS and .NET errors.
5. **Each first admin:** open the site from their own PC. They're signed in by name.
6. **Admins:** add people (Admin, People, or import them from Excel), fill the lists (categories, agreement types, business units, sites), then load the old contracts (section 9).

## 6. Backups (DBA and IT)

Two things together make up JBS Contracts' data. **Back them up on the same schedule and restore them to the same point**, because the database refers to the files by name:

| What | Where | Backup |
|---|---|---|
| The database | `JBSContracts` on the Production SQL Server | JBS's standard SQL Server backups (full plus transaction log, as the DBA sets). The app has no backup of its own |
| Documents | `C:\inetpub\JbsContracts\Data\documents` (files saved under year folders with random names) | JBS's standard file backup of the server, at least daily |
| Fonts | `C:\inetpub\JbsContracts\Data\fonts` | Part of the Data folder backup (or re-copy from the Webfonts pack) |

The app folder (`...\app`) doesn't need backing up: every deploy replaces it from GitHub. The logs folder is optional.

**Before each Production deploy:** the DBA takes a database backup. Database scripts only ever go forward, so going back to an earlier version means restoring that backup (and the Data folder from the same time), then deploying the earlier commit.

**Nothing is ever deleted by the app** (contracts are withdrawn or archived, people are switched off), so a restore is only for a disaster, not for an "undo".

## 7. Day to day: health, logs and the morning job

- **Health check:** `https://<site>/health/live` answers `Healthy`, or `Unhealthy` with a reason when it can't reach the database. Point JBS's monitoring at it.
- **Logs:** one file a day in `C:\inetpub\JbsContracts\logs` (`log-YYYYMMDD.txt`, kept 31 days), one JSON line per event. Errors carry the request path; people see a plain message with nothing technical in it.
- **The morning job:** at about 7:00am Brisbane time the app sends reminders, ends contracts whose end date has come, and writes two lines to the log:
  - `Daily reminders for <date>: <n> reminders, <n> contracts ended.`
  - `Audit trail on <date>: latest event <id>, hash <hash>.` This is a copy of the audit trail's latest link, kept outside the database. If anyone ever changed the audit history in the database, the hashes would no longer match these lines (the Audit trail tab under Reports also checks the chain inside the database each time it opens, and says whether it's intact).
  If a morning is missed (server down), the job catches up when the app is back.
- **Email:** emails are queued in the database and sent by the app in the background, with retries. If the relay is down, they go out when it's back. If `Contracts__Email__Host` isn't set, emails are kept and logged but not sent.
- **"Busy, try again":** if one change (for example a very large import) holds the database for over a minute, other saves are refused with that message and nothing is half-saved. It should be rare; if it isn't, tell the developer.
- **Upload limits:** documents up to 25 MB (PDF, Word, Excel, Outlook email, text, CSV, PNG, JPG); ZIPs for the import up to 25 MB; spreadsheets up to 5 MB. IIS's default request limit (about 28 MB) already allows these.

## 8. Security, in short

- Windows sign-in only (Anonymous off, except `/warmup` for the app pool's start-up). Only people an admin has added get in; everyone else sees "You don't have access yet" with their logon to pass on.
- Every rule (role, Confidential, stage, whose turn, locks) is checked on the server. Every change writes a hash-chained audit event that can't be edited or deleted (a database trigger blocks it).
- Every change needs a header a browser only sends from the app itself, and any request another website triggers is refused, so a malicious page can't act with someone's Windows sign-in.
- Strict security headers (content security policy, no framing, no sniffing). Add HSTS in IIS (section 2).
- No secrets are stored anywhere: no passwords in connection strings, no keys in GitHub, nothing in the repository.
- A security and code review was done before go-live and its findings fixed (decision D-93 in `jbs-contracts/docs/decisions.md`). It was a review of the code, not a penetration test of a running server.

## 9. Loading the old contracts (Medius)

Admin, Import old contracts. **Always run it on QA first** with the real export, check the dry run and the imported contracts, then repeat on Production. Imported contracts get a new JBSAUS number and keep the Medius number alongside as the old reference (D-94); running the same file again skips what's already in. Files come as ZIPs named or foldered by the old reference. Every row is audited with the file it came from.

## 10. Help videos (developers)

The help centre's short videos are recorded from a **local** copy of the app with only fictional demo data, never QA or Production. The steps are at the top of `jbs-contracts/web/help-videos/record.cjs`. When a screen changes, re-record its video and commit the new files in `jbs-contracts/web/public/help-videos`.

## 11. Checks the developer runs on every change

CI runs these on every push, and Deploy only deploys a commit whose CI is green:

- API tests against a real SQL Server.
- Front-end tests, lint and build.
- Browser tests of every main screen on a phone and a desktop, light and dark, with accessibility checks.
- Repository checks: no secrets, no font files, ASCII-only deploy scripts.

## 12. Still open before go-live

| Item | Who |
|---|---|
| Production server, site name, certificate, DNS, folders, app pool account | IT |
| Production runner (`iis-prod`) under its own account | IT |
| Production database and the roles in section 2 | DBA |
| Trusted SQL Server certificate (QA and Production) | IT / DBA |
| HSTS on the Production site | IT |
| GitHub Production environment, variables and reviewer (section 3) | Jeff |
| Font files copied to each server's `Data\fonts` | Jeff or IT |
| Backups of the database and Data folder on one schedule (section 6) | DBA and IT |
| Health check added to monitoring | IT |
| User testing and sign-off on QA; the Medius export tried on QA | Jeff and the business |
| Go-live approval | JBS governance |
