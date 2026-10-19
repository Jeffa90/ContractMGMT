# Installation guide (System Administrator)

**Purpose.** Install the Enterprise Contract Platform as a container with an external SQL Server, put it behind HTTPS, register it with the identity provider, prove it is healthy, and hand over to the first-run setup wizard.

**Who it is for.** A System Administrator: someone who can use a command line on the server, create a database login, edit a container composition file, manage files that hold secrets and register an application in the identity provider. A business administrator cannot do this guide and does not need to.

**Every command and every file edit in this guide is a [Technical task: System Administrator].** The application has no screen for them. Where the guide uses a screen (the health pages), it says so and names the permission.

**Status label.** Container model: **Implemented**, with executed test evidence (section 2). IIS and Azure: **Scaffolded / Not Verified**. Real malware scanning: the integration boundary is **Implemented** but **no real scanner adapter exists** (not built, Not Verified), so Production is blocked on it (section 9.3). Live Microsoft Entra sign-in and live SMTP: **Implemented, Not Verified** against a real directory or mail server; Microsoft Graph email is **Scaffolded**. Production approval is external governance; nothing in this guide, and nothing the application shows, approves an installation for production use.

**Prerequisites.** Section 3 lists them. In short: Docker Engine with the Compose plugin; a SQL Server 2022 database; a persistent volume; a TLS-terminating reverse proxy; an identity provider administrator; a Pack Publisher who can sign packs (with the standalone pack tool, no source checkout needed); and a few hours for a rehearsal on a non-production machine first.

**Next guide.** When section 10 is done, continue with the setup wizard guide (SETUP_WIZARD_GUIDE.md in the same folder). The earlier, longer playbook (`docs/IMPLEMENTATION_PLAYBOOK.md`, sections 5 and 6) is background; where it differs from this guide, this guide was checked against the code on the date in section 12.

---

## 1. Words used

| Word | Meaning |
|---|---|
| Installation | One running copy of the application with its database and data folder |
| Mode | Demo, Test or Production. Set by the setting `Ecp__Mode`. **A missing or unrecognised value silently becomes Production** (checked in the host code), so always set it explicitly |
| Secret reference | A pointer to a secret, written `env:NAME` (an environment variable) or `file:` followed by a path. Only these two work. `keyvault:` and `dpapi:` are accepted as notation but **do not resolve** (the code returns nothing for them); see section 5 |
| Governance mode | How the second-person publish rule applies: Production, TestEnforced, TestSimplified or DemoSimplified. Production always enforces it. Set in Test by `Ecp__Governance__SecondPerson` (section 5.1) |
| Readiness check | A named self-test (for example R-DB-01). Results are Healthy, Degraded or Unhealthy |
| Not Verified | Nobody has executed a check for it here. It is never a pass |

---

## 2. What is Verified and what is Not Verified

Read this before you promise a date. The source of record is `deploy/STATUS.md` (newest rows first); this table summarises it as at the date in section 12.

### 2.1 Verified (an executed test exists)

| Item | Evidence | Limit |
|---|---|---|
| Container image builds from `deploy/docker/Dockerfile` and starts | The CI job "container image build and smoke test" succeeded on a GitHub-hosted runner (run 63, commit 9c28896, recorded in `deploy/STATUS.md`) | The cloud workspace used for development cannot run the image build (its proxy certificate is not trusted inside the build container); CI runs it |
| What the smoke script checks (`deploy/docker/verify-image.sh`) | A seeded Demo becomes ready and its setup pages answer 404; a fresh installation redirects browsers to the setup page and answers the API with 503; `setup-token` prints a token and the token is **not** in the container log; ICU and time-zone data are present (R-OPS-02 Healthy); shipped packs are present as pack files and ready; the process is not root (user id 1654); **Production refuses Demo and SQLite settings** | It tests Demo and Test settings with SQLite, not a Production composition with SQL Server |
| SQL Server 2022 as the database | The SQL Server integration suite passed against a disposable SQL Server 2022 Developer container in CI and locally (latest local run recorded: 1,723 passed, 0 failed, 0 skipped, 0 Not Verified) | Developer edition, which is not licensed for real use. Tested login was a full administrator (section 4.1) |
| Backup, verify, restore into a new database, 16 validation checks on the restored copy | SQL Server tests with a shared backup folder, in CI | Docker host with an external SQL Server only. A restore rehearsal with your data is yours to do |
| Upgrade from the previous release (N-1 to N rehearsal), upgrade refusal tests | CI step with its evidence artefact | A previous-release binary as the source is Not Verified |
| First-run setup wizard, end to end over HTTP | Host tests in Test mode: Demo sign-in, mock email, SQLite file database. Token, one session, five-wrong-tokens lockout, remote refusal, resume after restart, closed after completion | **Not run in Production mode** with Entra, SMTP and SQL Server together |
| The 20-step no-code business journey | `docs/NO_CODE_REHEARSAL.md`: over real HTTP, on SQLite and on real SQL Server | Business steps only; steps 19 and 20 touch operations pages. Installation itself is outside it |

### 2.2 Not Verified (do not treat as working)

| Item | Why it is Not Verified | What you must do |
|---|---|---|
| `deploy/docker/compose.production.yml` | Configuration validated with `docker compose config` only; never run (it needs a managed SQL Server and a secret file) | Treat as a template. Run a rehearsal first |
| IIS on Windows Server (`deploy/iis`) | No Windows or IIS was available. **Scaffolded** | Not covered by this guide. Follow `deploy/iis/README.md` as a checklist in a Windows test environment |
| Azure App Service with Azure SQL (`deploy/azure`) | Not compiled or deployed. Azure SQL has no backup provider in this product and there is no Blob document provider. **Scaffolded** | Not covered by this guide |
| Live Microsoft Entra ID or other OpenID Connect directory | Tested only against an in-process stand-in that issues Entra-shaped claims | Rehearse sign-in with your directory before go-live (section 8) |
| Live SMTP delivery | Implemented. Verified only against a loopback test relay inside the automated tests, **not** against a customer's mail service. Port 465 (implicit TLS) is probably unsupported; use 587 with STARTTLS | Configure it on **/Admin/Operations/Email** and run the test there (section 11) |
| Microsoft Graph email | **Scaffolded, Not Verified, not connected.** Every send fails closed | Do not use |
| Malware scanning | The integration boundary and a provider-neutral self-test are **Implemented**; **no real scanner adapter exists** (not built, Not Verified). See section 9.3 for what that does to Production | A scanner adapter must be built and connected, and its self-test must pass, before Production use |
| Least-privilege database login | The tested login was a full administrator. A role named ecp_runtime exists in the schema, but no grants script is supplied | Ask your database administrator; test it in your rehearsal |
| Key Vault or DPAPI secret references | Accepted as notation, never resolved | Use `env:` or `file:` |
| A specific reverse proxy product, TLS and HSTS in front of the container | The HTTPS and forwarded-header diagnostics (ECP-NET-001, ECP-NET-002) are tested over real HTTP with simulated proxy behaviour, but no real proxy product (nginx, IIS ARR, Azure Front Door, Traefik) was part of any test; end to end it is Not Verified | Rehearse it (section 8.1, `docs/REVERSE_PROXY.md`) |
| Production mode with the shipped packs | Shipped packs are unsigned; Production refuses them | Sign them with the standalone pack tool (section 6) |
| Production-mode setup | Blocked until a connected, healthy scanner exists (section 9.3); the Production-mode rehearsal in a non-production environment has not passed | Do not plan a Production go-live on the dates of this guide |
| This guide followed by a person who is not a developer on a clean machine | Never done | Your rehearsal is that evidence |

### 2.3 Labels for what you are installing

| Component | Label |
|---|---|
| Application container, setup wizard, health endpoints, operations pages, backup and restore, upgrade procedure | **Implemented** (Verified as in 2.1) |
| Demo sign-in, mock email, development capture mailbox, demo log, the labelled demonstration scanner | **Mocked**: Demo and Test only; deliver nothing or protect nothing; Production refuses them |
| Microsoft Graph email, Key Vault references, DPAPI references | **Disabled** in practice (accepted notation or scaffold that does nothing useful) |
| Real malware scanner adapter, IIS and Azure deployments, backup schedule, Azure SQL backup | **Future** or **Scaffolded** (the scanner boundary and the labelled demonstration scanner are in the rows above and below) |

---

## 3. Before you begin

### 3.1 Decisions to have in writing

| Decision | Why | Example (synthetic) |
|---|---|---|
| Mode | Production for real data; Demo or Test for rehearsal | Rehearsal first in Test, then Production |
| Public web address | Used for sign-in redirects and links in email | https://contracts.example.test |
| Starting pack | Needs a signed pack in Production (section 6) | Blank, the reference procurement pack `jbs-procurement`, or the synthetic subcontract pack `construction-subcontracts` |
| Identity provider and who administers it | You cannot finish setup without a working test sign-in in Production | Microsoft Entra ID, administered by a named person |
| First administrator's email address | Must be the address the identity provider asserts for that person | first.admin@example.test |
| Who the second publisher is | In Production a configuration change is published by a second person | A second Business Configuration Administrator |
| Who keeps each secret and key | Named person, not "IT" | See the technical sheet below |

### 3.2 The technical sheet (keep it, but never put a secret in it)

Record: server name; image tag; database server and database name; login names (not passwords); the path of each secret file and who may read it; the public web address; the identity provider, application (client) id and directory id; the mail host and sender address; the backup folder paths; who holds the pack-signing key; the date of each rehearsal. Do not paste a password, token, client secret or connection string anywhere in it.

### 3.3 Technology

| Item | Requirement |
|---|---|
| Container engine | Docker Engine with the Compose plugin (tested: Compose 5.x) |
| Database | SQL Server 2022. Use a licensed edition for real use; Developer edition is for development and testing only. SQLite is for demonstration only and Production refuses it |
| Database accounts | One for the application (it creates and changes schema on first run and in upgrades) and a separate backup and restore account (BACKUP DATABASE, RESTORE, CREATE DATABASE, and permission to connect to the master database) |
| Web address and TLS | A real HTTPS address and a reverse proxy or ingress that ends TLS. The application listens on plain HTTP port 8080 inside the container |
| Storage | A persistent volume for the data folder (documents, packs, the key ring, setup state). A shared backup folder that both the database server and the application can read and write, and a second folder for restore rehearsals |
| Identity | Microsoft Entra ID, or another standard OpenID Connect provider. HTTPS only |
| Email | An SMTP server that offers STARTTLS on port 587 |
| Malware scanner | Production needs a connected, healthy scanner adapter; none is supplied (section 9.3). A Test rehearsal can use the labelled demonstration scanner on purpose |
| Packs | At least one pack, signed by a Pack Publisher, plus the Publisher's **public** key. The Pack Publisher needs the .NET runtime and the standalone `ecp-pack` tool, not a source checkout (section 6) |

---

## 4. Prepare the database and the secrets

### 4.1 [Technical task: System Administrator] Create the database and logins

1. On SQL Server 2022 create an **empty** database (example name: ecp).
2. Create the application login with enough rights to create schema on first run and for upgrades. The tested setup used a full administrator. A least-privilege login is **Not Verified** (section 2.2); if you try one, rehearse the migration and an upgrade under it.
3. Create the separate backup and restore login (rights in section 3.3).
4. Record the names in the technical sheet. Do not record the passwords.

**Success:** you can connect to the database with each login from the Docker host.
**If wrong:** the migration (section 7) fails with a connection or permission message; fix the login and run it again. It is safe to run again on an empty database.

### 4.2 [Technical task: System Administrator] Create the secret files

The database connection string and the backup account's connection string are kept in files **outside** the code folder. A pasted connection string in the composition file is refused in Production.

1. Create a folder for secrets on the Docker host, outside any Git working copy (the supplied composition expects a folder named secrets next to the compose file; that folder is git-ignored in this repository, but yours should not be inside a repository at all).
2. With a text editor (not with a command that puts the password in your shell history) create one file holding the full connection string on a single line, in the form your SQL Server client library expects. Example shape, with placeholders only:

```text
Server=db.example.test,1433;Database=ecp;User Id=ecp_app;Password=<put the password here>;Encrypt=True;TrustServerCertificate=False
```

3. Create a second file for the backup account in the same way.
4. Make each file readable only by the account the container runs as. The container runs as the fixed non-root user id 1654 (checked by the smoke script). Example, run as an administrator on the host:

```text
chown 1654:1654 ./secrets/ecp-sql.txt ./secrets/ecp-sql-backup.txt
chmod 0400 ./secrets/ecp-sql.txt ./secrets/ecp-sql-backup.txt
```

5. Never email, paste into a ticket, commit or log a secret.

**Success:** only the host administrator and user 1654 can read the files.
**If wrong:** the application starts but cannot read the reference; readiness reports an unreachable database, and in the setup wizard an unresolved reference blocks Production. Correct ownership and permissions. The user id and these permission commands are **Not Verified** on a production-shaped composition.

---

## 5. Settings the System Administrator sets

Environment variable names use double underscores. Put them in the composition file's environment block. **None of these can be set from a screen**, except the email configuration, which a System Administrator sets on **/Admin/Operations/Email** (row below).

| Setting | Required in Production | Purpose and example | Secret? |
|---|---|---|---|
| `Ecp__Mode` | Yes | Value Production. The shipped default file says Demo, so set it explicitly | No |
| `Ecp__DemoSeed` | Yes | Value false. If true (the shipped default) the installation fills itself with fake data and **skips the setup wizard** in Demo and Test; in Production the start-up guard refuses it | No |
| `Ecp__DemoTenantResolution` | Yes | Value false (default is true) | No |
| `Database__Provider` | Yes | Value SqlServer. Production refuses anything else | No |
| `Database__ConnectionStringRef` | Yes | A reference to the secret file (example below). A pasted connection string is refused in Production | The file's content |
| `Database__AutoMigrate` | Yes | Value false. Production never migrates by itself | No |
| `Ecp__DataDirectory` | Yes | The persistent data folder inside the container (the image sets /var/ecp). It must not be under a temporary folder; the start-up guard refuses that. Losing the key ring in it loses sessions and the protected setup state | No |
| `Ecp__Setup__AllowRemote` | Only while running setup | Value true while you run the wizard through a published port or a proxy; remove it afterwards. Without it, setup pages answer 404 to anything that is not the server itself | No |
| Email provider settings | Yes, but **not as host settings** | Email is one installation configuration, set on **/Admin/Operations/Email** (a [Technical task: System Administrator]) and read by both the setup wizard's email stage and Administration. Only non-secret settings and secret references are stored there. The old host settings `Notifications__EmailProvider`, `Notifications__Smtp__Provider`, `Notifications__Smtp__From`, `__Host`, `__Port`, `__UseTls`, `__UserRef` and `__PasswordRef` are now only a **fallback** that live delivery uses when nothing is stored; do not rely on them in Production. Provider `disabled` is not allowed in Production, and `dev-capture` and `demo-log` are refused there | References only |
| `Ecp__Email__ReadinessMaxAgeHours` | No | How long a passing email test stays valid (default 168 hours). After that, email reads as not ready until it is tested again | No |
| `Notifications__BaseUrl` | Yes | The public web address, used to make links in email work | No |
| `Notifications__JobIntervalSeconds` | No | Seconds between background runs (default 60; 0 turns the timer off) | No |
| `Ecp__Recovery__BackupDirectory`, `__SqlServerBackupDirectory`, `__RestoreTargetDirectory` | For backups | The shared backup folder as the application sees it, as the database server sees it, and the folder for restore rehearsals | No |
| `Ecp__Recovery__AdminConnectionStringRef` | For backups | Reference to the backup account's secret file. If missing, readiness R-BKP-01 is Degraded | The file's content |
| `Ecp__Recovery__StorageProtection` | Recommended | One sentence about how backup storage is protected. Recorded, not checked | No |
| `Ecp__Recovery__EncryptionCertificate` | Optional | Name of a SQL Server certificate for encrypted backups. The certificate must be backed up elsewhere | Certificate is sensitive |
| `Ecp__Packs__TrustFile` or `Ecp__Packs__TrustedKeys__0__KeyId` and `__PublicKeyPem` | Yes | The Pack Publisher's **public** key. Public only; a private key is never configured here | No (public) |
| `Ecp__Packs__ShippedDirectory` | If you mount signed packs | Where signed pack files are mounted. Default is the packs folder in the application | No |
| `Documents__AllowSkippedScan` | Leave false | See section 9.3. Setting true does not make Production ready | No |
| `Documents__Scanner__Provider`, `__Endpoint`, `__SecretRef`, `__TimeoutSeconds`, `__MaxFileSizeBytes`, `__RetryBackoffSeconds`, `__SelfTestMaxAgeHours` | For a real scanner | The scanner configuration (section 9.3). Provider is `none`, `mock` or a provider key; Endpoint is the service address; SecretRef is a secret reference only. Defaults: timeout 30 seconds, 100 MB (104857600 bytes), retry back-off 60 seconds, self-test valid 24 hours. Retries are limited by `Documents__MaxScanAttempts` | SecretRef is a reference |
| `Ecp__Network__ForwardedHeaders__Enabled`, `__KnownProxies`, `__KnownNetworks`, `__ForwardLimit` | Behind a proxy | Which proxy the application trusts to say the visitor used HTTPS (section 8.1). Without them a proxied request is refused with ECP-NET-002 | No |
| `Ecp__Governance__SecondPerson` | No (Test only) | `Default` or `Enforced`. Production always enforces the second-person publish rule and ignores a lower value. See section 5.1 | No |

Example values (synthetic; paths in the container). The email provider (SMTP host, sender, user and password references) is set on **/Admin/Operations/Email**, not here:

```text
Ecp__Mode=Production
Ecp__DemoSeed=false
Ecp__DemoTenantResolution=false
Database__Provider=SqlServer
Database__ConnectionStringRef=file:/run/secrets/ecp-sql
Database__AutoMigrate=false
Notifications__BaseUrl=https://contracts.example.test
Ecp__Network__ForwardedHeaders__Enabled=true
Ecp__Network__ForwardedHeaders__KnownProxies=10.0.0.5
```

Consequences of a wrong value: a wrong mode starts the system in the wrong posture; leaving demo settings on makes Production refuse to start (the guard names the rule, such as R-DB-01, R-SEC-02, R-DEMO-01 or R-ST-01); a non-persistent data folder loses documents or the key ring; an email configuration that has not been tested, or whose test has expired or was run against different settings, reads as not ready and Production setup cannot finish the email stage (section 11).

### 5.1 Settings for a Test-mode rehearsal

A supervised company rehearsal runs in **Test** with synthetic or masked data. Test is not approved for real confidential data. These settings are **[Technical task: System Administrator]**:

```text
Ecp__Mode=Test
Ecp__DemoSeed=false
Ecp__Governance__SecondPerson=Enforced
```

- **Governance setting `Ecp:Governance:SecondPerson`** (environment variable `Ecp__Governance__SecondPerson`) is `Default` or `Enforced`. In Test, `Enforced` applies exactly the Production second-person rule: the author, last editor and submitter of a draft cannot publish it, a submission is bound to the content it was made for, and a rollback is also submitted for a second person. `Default` keeps the simplified behaviour (publish immediately). Demo ignores the setting and always keeps simplified governance. Production always enforces the rule and the setting cannot lower it.
- The effective mode (Production, TestEnforced, TestSimplified or DemoSimplified) is shown on the setup report, **/Admin/Health** and **/Admin/Operations/Dashboard**, and is recorded on configuration audit events and releases. To rehearse governance, use `Enforced`, and have at least two Business Configuration or Organisation Administrators.
- **Scanner in Test.** The labelled demonstration scanner is registered in Test **only** when `Documents__Scanner__Provider=mock` is set on purpose. It is no protection (section 9.3). Do not set it unless you intend to rehearse with it.
- Email in Test can use `dev-capture` (delivers nothing). It reads "Simulated: nothing delivered", never Healthy.

---

## 6. Packs: signed and trusted (do this before the wizard)

Production accepts only packs signed by a key you trust. The packs shipped in the image are built **unsigned** (no private key exists in the repository), so in Production the wizard's pack stage offers an empty list and cannot continue until a signed pack is present.

1. **The Pack Publisher** ([Technical task: Pack Publisher], external to the application) builds and signs the pack with the standalone pack tool and hands you the signed pack files and the **public key** only. The application never holds a private key, receives none and has no browser signing. How: `docs/PACK_SIGNING.md` and `docs/PACK_AUTHORING.md`.
   - **The tool is `ecp-pack`**, a standalone .NET tool package (`Ecp.PackTool`). It needs the .NET runtime on the Pack Publisher's machine; it does **not** need a source checkout of this repository or its SDK. Install it from the folder that holds the `.nupkg` (built by CI and published as the build artefact `ecp-pack-tool`): `dotnet tool install Ecp.PackTool --tool-path ./ecp-tools --add-source ./folder-holding-the-nupkg`, then `./ecp-tools/ecp-pack version`.
   - Commands: version, build, build-all, validate, report, hash, inspect, sign, verify, compare, keygen. Builds are deterministic (the same source gives the same bytes).
   - Signing takes a key **reference**, for example `--key-ref env:VARIABLE` or `--key-ref file:/path/to/private.pem`. The key is never typed, printed, stored or logged. `report` writes a sanitised JSON report (identity, hashes, findings; no content, no key, no signature bytes, no local paths).
   - Exit codes: 0 success; 1 package or request not acceptable (including failed verification); 2 usage error; 3 a file or key could not be read or written, or a key reference could not be used.
   - **Not Verified:** a self-contained build for a machine without the .NET runtime, and signing with a key held in a hardware module or Key Vault.
2. **[Technical task: System Administrator]** Mount the signed pack files read-only into the folder named by `Ecp__Packs__ShippedDirectory` (for example over the image's packs folder), and trust the public key in host configuration with `Ecp__Packs__TrustFile` or `Ecp__Packs__TrustedKeys__0__KeyId` and `__PublicKeyPem`.
3. Alternatively a **Tenant Administrator** can register the same public key on the screen **Trusted publishers** (**/Admin/Packs/TrustKeys**, permission packs.trust.manage, needs a recent sign-in). Only the Tenant Administrator role holds that permission in the role matrix, not the System Administrator, and the screen needs a tenant to exist, so it cannot be used before the wizard's tenant stage. For a first installation use host configuration.
4. **Key custody** (who generates, stores, rotates and revokes the private key) is the organisation's own external responsibility.

**Success:** after the application starts, **/Admin/Health** shows solution-packs as Healthy.
**If wrong:** solution-packs is Degraded with the reason ("No shipped solution pack is trusted in Production…" or "N shipped pack file(s) were not accepted…"). The wizard's pack list is empty. Check the mount, the key id and the public key text; see the Checks page in the Pack Manager once a tenant exists.

For Demo and Test only, unsigned **synthetic** packs are allowed when `Ecp__Packs__AllowUnsignedSyntheticPacks` is true; Production ignores that setting.

---

## 7. Build the image, edit the composition, run the database setup, start

### 7.1 [Technical task: System Administrator] Build or obtain the image

The production composition expects an image named ecp:local and has no build instruction. From the repository root (the folder that holds the Dockerfile's context):

```text
docker build -f deploy/docker/Dockerfile -t ecp:local .
```

The build restores packages from the internet. If your network intercepts TLS, the build can fail at restore with a certificate error; fix the trust on your build host, do not weaken package validation.

### 7.2 [Technical task: System Administrator] Edit the composition file

Start from `deploy/docker/compose.production.yml` (**Scaffolded / Not Verified**). Make a copy under your own control and then:

1. Add the settings in section 5 to its environment block.
2. Un-comment and set the backup volumes and the backup settings if you will take backups from the application (you should).
3. Keep the supplied hardening: read-only root file system, all capabilities dropped, no-new-privileges, non-root user, the published port bound to 127.0.0.1 only.
4. Keep the data volume (ecp-data) so that documents, packs, the key ring and setup state survive restarts.
5. Mount the secret files (the supplied file already declares ecp-sql).

### 7.3 [Technical task: System Administrator] Run the database setup once

This is a deliberate step. In Production `migrate` refuses to run on a database that already holds an installation; after the first time, schema changes come only from the upgrade procedure (`docs/UPGRADE_RUNBOOK.md`).

```text
docker compose -f deploy/docker/compose.production.yml run --rm ecp migrate
```

The image's entry point is the application, so the word migrate is passed to it. Expected output: "Migrations applied." and exit status 0. **Not Verified** on this composition; the same command runs under the CI migration tests.

**If wrong:** a message about the connection means section 4; a message that the database already holds an installation means you must use the upgrade commands, not `migrate`.

### 7.4 [Technical task: System Administrator] Start the application

```text
docker compose -f deploy/docker/compose.production.yml up -d
```

Then check the health pages (section 9).

---

## 8. Reverse proxy and identity provider

### 8.1 [Technical task: System Administrator] Reverse proxy and TLS

Requirements (the proxy product is your choice; none is tested):

- It ends TLS for the public address and forwards plain HTTP to the container port 8080 on the Docker host (the published port is bound to 127.0.0.1 so the proxy must be on the same host or use a private network you control).
- It forwards the original **Host** header and the **X-Forwarded-For** and **X-Forwarded-Proto** headers, set by the proxy and never passed through from the visitor. The image sets ASPNETCORE_FORWARDEDHEADERS_ENABLED to true, which trusts **any** sender, so it is only acceptable where the container is reachable from the proxy alone (the published port is bound to 127.0.0.1). For anything wider, set `Ecp__Network__ForwardedHeaders__Enabled=true` and name the proxy in `Ecp__Network__ForwardedHeaders__KnownProxies` or `__KnownNetworks`.
- It redirects HTTP to HTTPS. In Production the application **requires HTTPS**, sends HSTS and marks its cookies Secure, and answers a plain-HTTP request with a short safe page and a code instead of working (below). `/health/*` stays reachable over HTTP for probes.
- It does not cache pages and does not strip the application's security headers.
- It forwards the health paths. Do not expose Administration pages to the internet if you can avoid it; they require sign-in, but a network restriction is a sensible extra.

**Success:** opening https://contracts.example.test/health/live in a browser shows `{"status":"Live"}`, and https://contracts.example.test/SignIn shows a normal sign-in page.
**If wrong:** a certificate warning is the proxy's certificate; a 502 means the proxy cannot reach the container. A page headed with a code is the application telling you which part is wrong:

| Code | Meaning | What to do |
|---|---|---|
| `ECP-NET-001` | The request arrived over plain HTTP and no HTTPS scheme was forwarded: the visitor used http://, or the proxy ends TLS but does not forward `X-Forwarded-Proto` | Use the https:// address; behind a proxy, make it send `X-Forwarded-Proto` and `X-Forwarded-For` |
| `ECP-NET-002` | Forwarded headers arrived but were not trusted: forwarded handling is off, or the sender is not a known proxy | Set `Ecp__Network__ForwardedHeaders__Enabled=true`, name the proxy in `KnownProxies` or `KnownNetworks`, restart |

The page shows a reference (the correlation id, also sent as the `X-Correlation-Id` header) and never a header value, address, secret or stack trace. Readiness check **R-NET-01** reports it and goes Degraded for 15 minutes after such a refusal; it does not take the health endpoint down. Full detail: `docs/REVERSE_PROXY.md`. A real reverse proxy product is **Not Verified** end to end.

### 8.2 [Technical task: System Administrator] Register the application with the identity provider

The wizard's identity stage (see the setup wizard guide, stage 6) stores the provider settings. The provider's own portal is where you create the application registration and the redirect addresses. **The wizard does not display the redirect addresses** even though a note on the page says it does (checked in the code), so build them as follows.

1. In the provider, create an application registration of the web-application kind, with the authorisation-code flow. The application uses PKCE and asks for the scopes openid, profile and email. Note its **application (client) id** and, for Microsoft Entra ID, the **directory (tenant) id**.
2. Create a client secret and store it in a secret file or environment variable as a secret reference (for example env:ECP_OIDC_SECRET). Do not paste it into the wizard; a pasted secret is refused.
3. Make the provider include a **groups** claim in the ID token (in Entra, in the application's token configuration). If a person belongs to too many groups the provider sends an "overage" marker instead; the application detects it and **fails closed**, so keep the groups list short or filter the groups.
4. Redirect addresses. The application registers the provider **twice**: once for the tenant and once for installation administrators. Register both, and the matching sign-out addresses:

   - Tenant: https://contracts.example.test/auth/callback/oidc-**TENANTID**-**KEY** (32 hexadecimal digits, no hyphens, then a hyphen and the provider key from the wizard, for example entra)
   - Tenant sign-out: the same address with -signout added at the end
   - Installation: https://contracts.example.test/auth/callback/oidc-00000000000000000000000000000001-**KEY**
   - Installation sign-out: the same address with -signout added at the end

5. How to learn TENANTID: complete the wizard's tenant stage and save the identity stage. A button **Test sign-in with** your provider's display name then appears on the identity stage page. Hover over it (or copy the link address) and read the part after scheme=. That text is oidc-, then the 32 digits, then your provider key. The address you register is the public address plus /auth/callback/ plus exactly that text. As a fallback, if you press the button before registering, Microsoft Entra ID names the rejected address in its error message (this is provider behaviour, not tested here).
6. Keep the registered addresses in the technical sheet.

**Success:** the **Test sign-in** button takes you to the provider's sign-in page and back to the wizard (see the wizard guide).
**If wrong:** the provider says the redirect address does not match: compare scheme, host and the whole path character by character. A different message that the account is not invited: the first administrator's email in wizard stage 7 must match what the provider asserts, and the test must be run after stage 7.
**Not Verified:** all of this against a real directory.

---

## 9. Health and readiness checks

### 9.1 [Technical task: System Administrator] The three health addresses

Run these from the server or any machine that can reach the address. They need no sign-in.

```text
curl -s -o /dev/null -w "%{http_code}\n" https://contracts.example.test/health/live
curl -s https://contracts.example.test/health/startup
curl -s https://contracts.example.test/health/ready
```

| Address | Meaning | Expected |
|---|---|---|
| **/health/live** | The process is running | 200 and `{"status":"Live"}` |
| **/health/startup** | The database schema is current | 200 `{"status":"Started"}`; 503 `{"status":"Starting"}` until the database setup (section 7.3) has run |
| **/health/ready** | The readiness checks overall | 200 for Healthy or Degraded; **503 for Unhealthy**. In Production the body is only the overall word, for example `{"status":"Degraded"}`. In Demo and Test it also lists each check |

On a fresh, not-yet-set-up installation, browser pages redirect to the setup page and API calls answer 503 with the code setup_required; the three health addresses keep answering.

Use **/health/live** for a container or monitoring liveness probe. Be careful using **/health/ready** as a load-balancer probe in Production until you have read section 9.3.

### 9.2 The Health and readiness screen

| | |
|---|---|
| Navigation | Main menu entry **Health and readiness**, page **/Admin/Health** |
| Who | System Administrator (permission ops.manage; the role matrix gives it to the System Administrator only) |
| Prerequisite | Setup complete and you are signed in as an installation administrator |
| What it shows | Overall status, then one row per check (id, description, status, detail), then mode, the governance mode (section 5.1), culture and time zone, version. It is read-only: there is no button |
| Evidence | Save the page (print to PDF) with the date |

Checks you will see, and how to read them (all read from the code):

| Check | Healthy means | Degraded or Unhealthy usually means |
|---|---|---|
| R-DB-01 | Database reachable and every module's migrations applied | Unhealthy: pending migrations (run section 7.3 or the upgrade procedure) or database unreachable |
| R-ST-02 | The data folder accepts a write, read and delete | Unhealthy: the volume is missing, read-only or full |
| R-OPS-02 | Culture (ICU) and time-zone data are present | Unhealthy: the image lacks globalisation data |
| R-BKP-01 | Backup is configured, protected and a verified backup is recent | Degraded with the reasons (no backup folder, no dedicated account, no encryption or protection statement, no verified backup, older than seven days) |
| solution-packs | At least one shipped pack is usable here | Degraded: no trusted pack in Production, or files rejected; Unhealthy: pack folder not found |
| capabilities | Shipped packs' capability defaults are coherent | Degraded: a pack leaves a capability on without one it needs |
| regional | Shipped packs' regional proposals are valid on this host | Degraded: an invalid locale, currency or time zone in a pack |
| R-NET-01 | The expected transport (HTTPS, forwarded headers) is in place | Degraded for 15 minutes after a request was refused with ECP-NET-001 or ECP-NET-002 (section 8.1) |
| **R-DOC-01** | Uploaded files are scanned before anyone can use them (section 9.3) | See 9.3 |

### 9.3 Check R-DOC-01: the malware gate

R-DOC-01 asks whether the gate that holds uploaded files can do its job. From the code, in Production:

| Situation | Result | Plain meaning |
|---|---|---|
| No scanner connected | **Unhealthy** | Every uploaded file is held and cannot be downloaded, copied or counted as evidence |
| The demonstration scanner is connected | **Unhealthy** | It only looks for a standard test string. It is not protection |
| Skipped scans are allowed (`Documents__AllowSkippedScan` true) | **Unhealthy** | Files nobody scanned could be used |
| A real scanner is connected and skipped scans are off | Healthy | |

Outside Production, with no scanner, it is Degraded ("expected in a trial").

**No real scanner is supplied.** The platform defines the integration boundary (`IMalwareScanner`) and a provider-neutral readiness self-test, but the adapter for a real product is **not built and Not Verified**. In Production the application registers no scanner at all (the demonstration scanner is never registered in Production), so out of the box R-DOC-01 is **Unhealthy** in Production.

**Scanner configuration `Documents:Scanner:*`** (environment variables `Documents__Scanner__...`; a [Technical task: System Administrator]):

| Setting | Meaning | Default |
|---|---|---|
| `Provider` | `none`, `mock` or the key of a provider adapter | none (Demo defaults to mock) |
| `Endpoint` | The scanning service address | none |
| `SecretRef` | A secret **reference** (never the secret) | none |
| `TimeoutSeconds` | Per-scan timeout | 30 |
| `MaxFileSizeBytes` | Largest file sent for scanning | 104857600 (100 MB) |
| `RetryBackoffSeconds` | Wait before a retry | 60 |
| `SelfTestMaxAgeHours` | How long a passing self-test stays valid | 24 |

Retries are limited by `Documents:MaxScanAttempts`. The scanner page **/Admin/Operations/Scanner** (read: `operations.read`; accepting the mock for a rehearsal needs the System Administrator, `operations.maintenance`, and a recent sign-in) shows the state and a prominent TEST MODE warning. The checklist for connecting a provider is in `docs/DOCUMENT_SCANNING.md`.

**Rules for the mock (the labelled "demonstration scanner"):**

- It is registered in **Demo** by default, in **Test only when `Provider` is `mock` on purpose**, and **never in Production**.
- It is no protection: it only looks for a standard test string. R-DOC-01 reports it as a warning in Test.
- For a masked-data rehearsal its use must also be **accepted on the record** (kind scanner.mock: who, reason, scope, and an expiry of at most 90 days). Until it is, a batch marked Masked Company Data cannot be committed.
- Quarantine, infection, provider unavailability and exhausted retries raise operational notifications to the recipients in the existing notification configuration.

What that means for you, from reading the code (**not executed in a Production-mode run**; confirm it in your rehearsal):

- **/health/ready** will answer 503 in Production until a real scanner is connected.
- The setup wizard's readiness stage (R-OPS-01) runs the same platform checks and treats an Unhealthy one as **blocking**, so **Finish setup is expected to be refused in Production** until R-DOC-01 is Healthy. In Test, a missing or demonstration scanner is only Degraded, so R-OPS-01 is a **warning** that must be acknowledged by check id with a reason (wizard guide, stage 13). Do not work around the Production block by acknowledging it (a blocking check cannot be acknowledged) and do not set `Documents__AllowSkippedScan` to true (that is also Unhealthy).
- Raise this with the product owner and security owner now. It is a product-owner and security decision, not an installation setting.

**Production blockers, stated honestly.** (1) No real scanner adapter exists, so R-DOC-01 is Unhealthy and Production setup cannot finish; attachments are held. (2) Production-mode setup has not been run end to end with a real identity provider, mail service and SQL Server together. (3) SMTP is verified only against a loopback relay, Graph is a scaffold, and no reverse proxy product has been exercised. The supported use today is a supervised rehearsal in Demo or Test with synthetic or masked data. Nothing here approves Production; that is external governance.

Business administrators can see the scanner state and held files on the screen **Document scanning** (**/Admin/Documents/Scanning**, permission notifications.administer, held by the Tenant Administrator and Procurement Administrator, not the System Administrator). It shows the scanner state, counts (clean, waiting, skipped, failed, infected, oldest held), held files with a **Scan again** button (only when a scanner is connected), and states that infected files stay in quarantine. See the document scanning note in the docs folder (DOCUMENT_SCANNING.md) for the design.

---

## 10. Open the setup wizard

### 10.1 [Technical task: System Administrator] Issue the setup token

```text
docker compose -f deploy/docker/compose.production.yml exec ecp dotnet Ecp.Host.dll setup-token
```

It prints a token **once** to your terminal. Verified in the smoke test: the token is not written to the container log. It is valid for 24 hours and single use; running the command again replaces it and ends any open session. There is no way to get a token from a web page. Do not paste the token into a ticket or chat.

### 10.2 Open the page

1. Add `Ecp__Setup__AllowRemote=true` to the composition and restart, unless you run a browser on the server itself with a path that reaches the application as a local caller. (Through a published Docker port the application usually sees the Docker network address, not the loopback address, so assume you need it.)
2. In a browser open **/Setup** on your public address (through HTTPS; in Production the setup cookie is marked Secure). Continue with the setup wizard guide. Keep the System Administrator and the identity provider administrator at hand for stages 2, 3 and 6.
3. When the wizard finishes, **remove** `Ecp__Setup__AllowRemote` and restart.

---

## 11. After the wizard

1. **[Technical task: System Administrator] Check the email configuration.** The setup wizard's email stage and **/Admin/Operations/Email** read and write the **same** installation configuration, so there is no second set of settings to copy. Open the page, confirm the provider and secret references, and run the test again: it makes a connectivity probe and sends a synthetic message with fixed text (no contract information). A result is tied to the settings (a changed setting makes it stale) and expires after 168 hours by default. A configured but unreachable provider is never Healthy, and a simulated provider reads "Simulated: nothing delivered", never Healthy. SMTP is **Not Verified** against a real customer mail service; Graph is a scaffold.
2. **Open Health and readiness (9.2)** and record the result. Expect R-DOC-01 to need attention (9.3) and R-BKP-01 to be Degraded until backups are configured.
3. **Configure and rehearse backups** (`docs/BACKUP_AND_RESTORE.md`, `docs/OPERATIONS_RUNBOOK.md`). Back up the data folder's key ring together with the data folder. There is no documented recovery if the key ring is lost and the setup state becomes unreadable (everything except the live health check then answers 503).
4. **Create a second administrator** so that a configuration change can be published by a second person.
5. **Save the setup report** from the wizard (its guide explains how).

---

## 12. Evidence to keep

| Evidence | Where from |
|---|---|
| The technical sheet (no secrets) | Section 3.2 |
| The composition file you ran, with secrets absent | Your copy |
| Output of migrate ("Migrations applied.") | Section 7.3 |
| The three health responses, dated | Section 9.1 |
| A saved copy of Health and readiness | Section 9.2 |
| The registered redirect addresses | Section 8.2 |
| The setup report (JSON), saved | Wizard guide |
| The results of your rehearsal: what worked, what did not, who did it, date | Your own record |
| The version of the product and the commit the image was built from | Health and readiness shows the version |

This guide was written on 2026-10-18 from the platform at that date (version 1.1.0) by reading the code, the deployment files and the verification log, and reconciled on 2026-10-19 with the Company Rehearsal Enablement slice (ADR-0038). No installation was performed to write it. It is **not Verified** by a person who is not a developer on a clean machine. It is not an approval for production use; that is external governance.

---

## 13. If something goes wrong

| Symptom | Likely cause | What to do |
|---|---|---|
| The container exits at start with "Start-up refused" and a rule such as R-DB-01, R-SEC-02, R-DEMO-01 or R-ST-01 | A Production rule is broken (SQLite, an inline connection string, demo seed on, data folder in a temporary location) | Read the rule in the log, fix section 5 settings, start again |
| Every browser page goes to /Setup | The installation is not set up yet (expected on a fresh installation) | Section 10 |
| /Setup answers 404 | You are not on the server and `Ecp__Setup__AllowRemote` is not true; or setup is already complete | Set it temporarily; if complete, use Installation setup (**/Admin/Setup**) after signing in |
| /Setup never appears and the site is full of fake data | The installation started with `Ecp__DemoSeed` true | Stop, correct the setting, start with an empty database |
| Everything except /health/live answers 503 with "The installation state could not be read" | The setup state file is damaged or the key ring is gone | Restore the data folder (including the key ring) from backup. There is no other documented recovery |
| /health/startup answers 503 | The database schema is not current | Section 7.3 |
| /health/ready answers 503 in Production | Some check is Unhealthy; check R-DOC-01 first (9.3), then open Health and readiness | Fix what the row names |
| A browser page or API call shows `ECP-NET-001` (HTTP 400) | The request arrived over plain HTTP and no HTTPS scheme was forwarded: the visitor used http://, or the proxy does not forward `X-Forwarded-Proto` | Use the https:// address; make the proxy send `X-Forwarded-Proto` and `X-Forwarded-For`. Section 8.1 and `docs/REVERSE_PROXY.md` |
| A page shows `ECP-NET-002` (HTTP 400) | Forwarded headers arrived but the application did not trust the sender | Set `Ecp__Network__ForwardedHeaders__Enabled=true` and name the proxy in `KnownProxies` or `KnownNetworks`; restart. Section 8.1 and `docs/REVERSE_PROXY.md` |
| Sign-in fails with a redirect mismatch (the identity provider names an address) | The registered redirect address differs from the one the application builds. This is a registration problem, not the proxy symptom above: a proxy that does not forward the scheme now gives `ECP-NET-001` instead | Section 8.2 step 5: compare scheme, host and the whole path character by character |
| Email shows not ready, stale, failed or "Simulated" | The email test was never run, the settings changed since, the pass expired, the provider is unreachable, or the provider is a simulated one | Open **/Admin/Operations/Email**, correct the settings and run the test again. Production setup cannot finish the email stage without a passing test |
| Attachments stay held; R-DOC-01 is Unhealthy in Production | No real scanner is connected (none exists) | Section 9.3. This is a Production blocker, not a setting to work around |
| A secret reference shows as unresolved | The file is missing in the container, unreadable by user 1654, or the variable is not set; or you used keyvault: or dpapi: | Use `env:` or `file:` and fix permissions |
| You need to go back to an earlier state | Data or schema is wrong | Restore from a verified backup into a new database (`docs/BACKUP_AND_RESTORE.md`). Do not run `migrate` on an existing Production database |

Escalate to the supplier with: the version, the date and time (UTC), the health responses, the Health and readiness page, and the application log lines around the failure. **Never send secrets, connection strings, tokens or live confidential data.**
