# Operations quick guide for the System Administrator

**Purpose.** Do the routine and the planned operations tasks through the application's own pages: read the Operations dashboard, create and verify backups, rehearse a restore, run validation, use maintenance mode and operation leases, download evidence and the support bundle, and carry out an upgrade (readiness, preview, rehearsal, migration report, history, post-upgrade validation, rollback evidence). It also covers the Health page, the Email provider and Malware scanner pages, the implementation evidence pack, accepted warnings, HTTPS and reverse-proxy diagnostics (R-NET-01), the Document scanning page, and where audit fits.

**Who it is for.** The System Administrator: a trained operator who does not need to write code, SQL or JSON, edit source or use Git. Business roles (Tenant Administrator, Organisation Administrator and the rest) cannot open the Operations pages, by design.

**Prerequisites.**
- You hold the System Administrator role in the **installation's own tenant**. Operations actions are refused from inside any customer tenant, even for an administrator, and the service checks this, not just the menu.
- The installation runs on SQL Server for backup, restore and upgrade. SQLite (Demo and Test) shows the pages but refuses backup and restore with a plain message.
- Installation-level settings are done once, outside the application, **[Technical task: System Administrator]**: backup directory shared with the database server, restore target directory, a dedicated backup account, the encryption or storage-protection statement. See `docs/OPERATIONS_RUNBOOK.md` section 1 and `docs/BACKUP_AND_RESTORE.md` sections 3 and 4.
- The organisation has decided backup frequency, retention, recovery objectives, where copies live and who may read them. The platform does not decide these (`docs/OPERATIONS_RUNBOOK.md` section 9).

**Status label.** Operations pages, backup, verify, restore, validation, maintenance, leases, evidence, support bundle, upgrade pages: **Implemented**, verified locally on SQL Server for the container model (`deploy/STATUS.md`). IIS and Azure: **Scaffolded / Not Verified**. Per-tenant restore and automatic backup scheduling: **Future**. Document scanning gate: **Implemented**; a real scanner: **Future / Not Verified** (`docs/DOCUMENT_SCANNING.md`). Email page (SMTP): **Implemented**, verified only against a loopback test relay inside the automated tests, **Not Verified** against any customer mail service; Microsoft Graph: **Scaffolded / Not Verified**; dev-capture and demo-log: **Mocked** (deliver nothing). Scanner page and self-test: **Implemented**; the demonstration scanner: **Mocked**; no real scanner adapter exists. Evidence pack and accepted warnings: **Implemented**. HTTPS and proxy diagnostics: **Implemented**; a real reverse proxy end to end: **Not Verified** (`docs/REVERSE_PROXY.md`).

> **Tooling is not governance, and nothing here is production approval.** A green dashboard, a verified backup and a passing pre-flight are evidence for your change process. Production approval is external governance.

---

## 1. Permissions and sign-in rules at a glance

The System Administrator holds all of these. A custom operator role would need exactly the permissions it uses.

| Permission | Lets you | Needs a recent sign-in? |
|---|---|---|
| `operations.read` | Open Operations, dashboard, backups list and details, restore history, validation results, maintenance state, leases, upgrade readiness, preview, migration report, history, post-upgrade validation, rollback evidence | No |
| `operations.backup` | Create a backup; **Verify again** | Create: yes. Verify: no |
| `operations.restore` | Preview and run a restore into a new database; run an upgrade rehearsal; prove a rollback | Run: yes. Preview: no |
| `operations.validate` | Run validation on the running installation; see technical detail columns; download pending migration SQL | No |
| `operations.maintenance` | Enter and leave maintenance mode; take over a lease; tick "hold maintenance" on a backup; change the Email provider settings; accept, or revoke the acceptance of, a warning, an exception or the demonstration scanner | Yes (enter, leave, take over, change, accept) |
| `operations.evidence.export` | Download the evidence report (JSON, CSV), upgrade pre-flight reports, and the support bundle; open and download the implementation evidence pack | Support bundle: yes. Others: no |
| `operations.upgrade` | Apply the upgrade | Yes |
| `ops.manage` | Open **Health and readiness** | No |

**Recent sign-in.** When an action needs it you are sent to a page titled **Confirm it is you** (**/Reauth**); click **Confirm** (a real identity provider asks you to sign in again) and you return to the action. The window is 10 minutes by default. Re-enter your reason if the form was cleared.

**Important:** the System Administrator role does **not** hold `notifications.administer` (needed for **Document scanning**) or `audit.read` / `audit.read_admin` (needed for **Audit events**). See sections 8 and 9.

---

## 2. Where things are

Sign in as the System Administrator. The menu shows:

| Menu entry | Page | Permission |
|---|---|---|
| Operations | **/Admin/Operations** | `operations.read` |
| Health and readiness | **/Admin/Health** | `ops.manage` |
| Support access | **/Admin/Support** | `support.elevate` (not covered here) |

The Operations page (**/Admin/Operations**) is the hub. It shows a red notice if maintenance mode is on, a **Recovery readiness** panel, links to every page below, and a **Recent operations** table (Started, Kind, Reference, Outcome, Summary).

### 2.1 Reading "Recovery readiness"

| Line | What it means | If it is not what you expect |
|---|---|---|
| Database engine | The engine; a red "No supported backup" badge means SQLite | Backups are refused. Use SQL Server for anything real |
| Backup directory | Configured or Not configured | **[Technical task: System Administrator]** set it (runbook section 1) |
| Restore target directory | Configured or Not configured | Same |
| Dedicated backup account | Configured, or "Not configured: the application account is used" | Warning only. **[Technical task: System Administrator]** |
| Database backup encryption | Requested or Not requested | Organisation decision; record it |
| Storage protection statement | Recorded or Not recorded | Organisation decision. The platform never verifies the statement |
| Last verified backup | An id and date, or None | Take and verify one (section 4) |

**Evidence to keep:** a screenshot or the dashboard JSON (section 3).

---

## 3. Operations dashboard

| | |
|---|---|
| Navigation | Operations, then "Operations dashboard: health, readiness, jobs, outbox, certificates and failures" |
| Page | **/Admin/Operations/Dashboard** |
| Who | System Administrator |
| Permission | `operations.read` (technical detail columns additionally need `operations.validate`) |
| Prerequisites | None. It reads and changes nothing, works during maintenance mode, and never shows business content |

**What is on it.** Top card: **Overall** status badge, platform version, mode and engine, maintenance mode, time built, a correlation id, and "Readiness catalogue" coverage (how many checks run here, run elsewhere, or are not built). Then: **Recent operational failures** (up to 15, with "What to do" and a link), one panel per area (health, readiness, versions, upgrade, recovery, jobs, outbox, configuration, integrity, certificates), **Operation leases**, **Schema versions by module**, and **Operational metrics**.

**States.** Every line is **Verified**, **Warning**, **Failed** or **Not Verified**. The worst line wins for a panel and for Overall. **Not Verified means nobody has looked, or it cannot be looked at here; it is never a pass.** An empty area is Not Verified, not green.

**Buttons and links.**
- "Download this dashboard as JSON (for monitoring)" saves a snapshot. Keep one before and after any change.
- "Evidence report and support bundle (needs a recent sign-in)" opens section 10.
- Each failure and line has an **Open** link to the page that fixes it.

**Common results and what to do.** `docs/OPERATIONS_DASHBOARD.md` section 5 explains how each line is judged; `docs/OPERATIONS_RUNBOOK.md` section 13 is the failure map. In short: a Failed upgrade or restore keeps maintenance mode on and is **never** retried by the dashboard; a failed backup, validation, rehearsal or rollback proof offers a fresh attempt; dead-lettered e-mails are retried by the owning tenant's administrator, not from here.

**Evidence to keep:** the JSON download, with its correlation id.

---

## 4. Backups

| | |
|---|---|
| Navigation | Operations, then "Backups: create, verify and review" |
| Page | **/Admin/Operations/Backups** |
| Who | System Administrator |
| Permission | Page: `operations.read`. Create and Verify: `operations.backup`. Hold maintenance: also `operations.maintenance`. Create needs a recent sign-in |
| Prerequisites | SQL Server; backup directory configured (otherwise the page says "No backup directory is configured, so backups cannot be taken from here" and shows no form) |

### 4.1 Create a backup

| Field | Required? | Recommended | Worked example (Blank / JBS-style / Harborline-style) | Rule and consequence |
|---|---|---|---|---|
| Reason for this backup | Required, up to 500 characters | Say why and the change or ticket reference | Blank: "Before month-end close" / JBS-style: "Pre-cutover backup, change CHG-2026-114 (synthetic)" / Harborline-style: "Pre-upgrade backup, ticket HBL-OPS-77 (synthetic)" | Stored in the audit trail. Secrets typed here are masked, but never type one |
| Hold maintenance mode during the backup | Optional checkbox | Tick for an exact backup (rehearsal, cutover, pre-upgrade) | Ticked for all three when you need exact counts | Pauses all changes while the backup runs. Unticked gives an online backup whose counts in the manifest are ranges. Needs `operations.maintenance` |

**Create backup** takes the backup, reads it back and checks it. It is not reported as successful until it is checked. Then you land on the backup's details page.

**Success.** A flash message "Backup bk-... was created and its integrity was checked (Completed)". Outcomes: Completed, Completed with warnings (read them, the backup is still valid), Failed ("is not a recovery point").

**Common errors.**

| Message | Recovery |
|---|---|
| "Another operation holds the recovery lease" | A backup, restore or validation is running, or crashed recently. Wait up to the lease time, or see section 9 |
| "The database server could not write the backup" / "not visible to the application" | Directory permissions or mapping. **[Technical task: System Administrator]**, `docs/OPERATIONS_RUNBOOK.md` section 12 |
| Form missing, "No backup directory is configured" | **[Technical task: System Administrator]** configure `Ecp:Recovery:BackupDirectory` |
| Redirected to **Confirm it is you** | Click **Confirm**, repeat |

### 4.2 Backup history and details

The **Backup history** table reads the backup directory itself: Backup (id), State, Created, Size, Warnings, Detail, Actions. States: Verified (green); Verified with warnings and Unverified (amber); Incomplete or Failed (red; a folder without a manifest is never a recovery point).

- **Verify again** (per row, not shown for Incomplete or Failed): re-checks file hashes, document objects and SQL Server's own check. Run after copying a backup, before relying on it, and regularly. Outcome appears as a flash message and on the details page.
- Click a backup id: **/Admin/Operations/Backups/{id}** shows **State**, a sanitised **Manifest** (created, platform version, environment, database engine and size, manifest SHA-256, documents, installed packages, whether it is an exact quiescent backup, storage protection statement) and **Warnings recorded**. **Write the manifest SHA-256 into your own records**: it lets you detect a manifest that someone replaced.
- "Preview a restore of this backup" goes to the Restore page.

**Evidence to keep:** backup id, manifest SHA-256 (in your records, not only on screen), outcome, warnings and what you decided about each.

**Scheduling.** There is no built-in schedule. Run the backup command from your own scheduler and alert on a non-zero exit: **[Technical task: System Administrator]**.

---

## 5. Restore (into a new database)

| | |
|---|---|
| Navigation | Operations, then "Restore: preview, restore and history" |
| Page | **/Admin/Operations/Restore** |
| Who | System Administrator |
| Permission | `operations.restore`. Starting a restore needs a recent sign-in |
| Prerequisites | A Verified (or Verified with warnings, or Unverified) backup; restore target directory configured; room for a full copy of the document store and packages |

A restore always goes into a **new, separate database and file area**. It is checked there and released only when the checks pass. It never replaces the database in use, applies no migrations and rewrites no history. Restoring over the active database (disaster recovery) is **not** available on this page: it is a command-line action with the application stopped, **[Technical task: System Administrator]** (`docs/OPERATIONS_RUNBOOK.md` section 6 and `docs/DISASTER_RECOVERY.md`).

### Step 1: choose a backup and a target

| Field | Required? | Recommended | Examples | Rule and consequence |
|---|---|---|---|---|
| Backup | Required (list) | The backup you verified and recorded | the id from your records | Only Verified, Verified with warnings and Unverified backups are offered |
| New target database name | Required, letters, digits and underscores, up to 64 | Include the purpose and month | Blank: `ecp_rehearsal_2026_10` / JBS-style: `ecp_jbs_rehearsal_2026_10` / Harborline-style: `ecp_hbl_rehearsal_2026_10` | Must be a name that does not exist. The active database is refused (a red "This is the active database" badge). An existing name is a blocker |

**Preview the restore** shows nothing changed, only a sanitised preview.

### Step 2: preview (read it before going on)

Shows the integrity check (Passed or not, files checked), target database, when the backup was made (environment, platform version), contents (tenants, audit events, document objects, package files), manifest SHA-256, then **Blockers** ("The restore cannot start", red) and **Warnings**. Typical blockers: backup fails its integrity check; name taken or active; restore directory not empty; document content not in the backup; a different environment. A backup from a different environment is the one blocker that still lets you proceed, with an explicit tick-box.

### Step 3: confirm (shown only when the restore may start)

| Field | Required? | Recommended | Examples | Rule and consequence |
|---|---|---|---|---|
| Reason | Required, up to 500 | Name the rehearsal or incident | Blank: "Quarterly restore rehearsal" / JBS-style: "Rehearsal for cutover CHG-2026-114 (synthetic)" / Harborline-style: "Incident INC-HBL-12 side-by-side restore (synthetic)" | Audited |
| Type the phrase shown (`RESTORE <backup id>`) | Required | Copy exactly from the label | `RESTORE bk-20261017T031500Z-0a1b2c3d` | Must match exactly or the restore is refused |
| Manifest SHA-256 you expect | Optional | Paste it from your own records | the hash you recorded in section 4 | A mismatch means the manifest changed after you recorded it. Stop and investigate |
| I accept restoring a backup made in a different environment | Optional tick | Leave unticked | | Tick only if you accept that live data will land in this environment |

**Restore into the new database** runs the restore. The installation enters maintenance mode for its length.

**Success.** A **Restore result** card: **Succeeded** or **Succeeded with warnings** (the copy passed validation and was released from maintenance mode; "nothing switched over", you repoint the application deliberately), the **Post-restore validation** counts (passed, warnings, failed, not verified) with a link to open the results, and the **Evidence file** name. **Failed** means the copy is held in maintenance mode and not released.

**If it failed.** The installation shows maintenance mode with cause "Restore failed" on purpose. Read the failure and the validation table, fix the cause (space, permissions, missing certificate, damaged backup), then restore again into a **new** name. To leave maintenance mode afterwards, see section 7 (acknowledgement tick-box). Never retry blindly.

**Restore history** (bottom): Started, Target, Outcome, Summary.

**Evidence to keep:** the evidence file named on the result (in the backup folder under the evidence directory, **[Technical task: System Administrator]** to copy it), the validation outcome, elapsed time (your measured recovery time), who restored and who checked the copy as the business.

After a rehearsal: starting a separate non-production instance against the copy, and dropping the rehearsal database and folder, are **[Technical task: System Administrator]**; the platform never drops databases.

---

## 6. Validation

| | |
|---|---|
| Navigation | Operations, then "Post-restore validation" |
| Page | **/Admin/Operations/Validation** |
| Who | System Administrator |
| Permission | Page: `operations.read`. Run: `operations.validate` |
| Prerequisites | Run it when the installation is quiet: it reads every document and every table |

**Run validation on the running installation** starts the checks: migrations, referential integrity, tenant isolation, audit chain, documents, packages and signatures, configuration hashes, pinned workflows, regional settings and assigned packs. A restore runs the same checks on the restored copy before it is released.

**Success.** An **Outcome** card (Passed, Passed with warnings, Failed or Not Verified), time and correlation id, and a table of checks (code such as VAL-001, result, what it means, technical detail if you hold `operations.validate`, what to do). Without a run yet the page says "No validation has been run yet".

**Common failures.** VAL-009 Failed: an audit chain does not verify, so investigate who changed records. VAL-010 Failed: document content missing or damaged; restore again, do not release the copy. VAL-002 Failed: created by a newer version; run the version that made it. Full list: `docs/OPERATIONS_RUNBOOK.md` section 12.

**Evidence to keep:** outcome, correlation id and the check table (screenshot or copy of the codes).

---

## 7. Maintenance mode

| | |
|---|---|
| Navigation | Operations, then "Maintenance mode" (or the link in the red notice when it is on) |
| Page | **/Admin/Operations/Maintenance** |
| Who | System Administrator |
| Permission | Page: `operations.read`. Enter and Leave: `operations.maintenance` and a recent sign-in |

While on: requests that change data are refused with a clear message, background jobs take no new work, and every page shows a banner ("The system is in maintenance"). Reads, health, sign-in and these Operations pages keep working. It also refuses configuration publication and rollback and data-migration commits. See `docs/MAINTENANCE_MODE.md`.

| Field | Required? | Recommended | Examples | Rule and consequence |
|---|---|---|---|---|
| Reason | Required, up to 500 | Why, and who asked | Blank: "Planned backup window" / JBS-style: "Cutover freeze CHG-2026-114 (synthetic)" / Harborline-style: "Upgrade window HBL-OPS-77 (synthetic)" | Audited on entering and on leaving |
| "I understand a restore is in progress, waiting for validation, or failed..." | Shown only when the mode is restore- or upgrade-related | Tick only after you have read why it is on | | Without it you cannot leave maintenance after a failed restore or upgrade (the refusal code is `restore_state` or `upgrade_state`) |

The button reads **Enter maintenance mode** when it is off and **Leave maintenance mode** when it is on. The **Current state** card shows State, Cause (Manual, restore or upgrade related), Reason and Since.

**Success.** A flash message "Maintenance mode is on. Changes are paused." or "Maintenance mode is off."

**Common errors.** Forgetting to leave it before a migration commit or configuration publication (those are refused while it is on). Leaving a failed-upgrade state "to see if it works": do not; roll back by restore (section 12).

**Evidence to keep:** the audit actions are `operations.maintenance.enabled` and `operations.maintenance.disabled`, with the reason; note the times in your change record.

---

## 8. Operation leases

| | |
|---|---|
| Navigation | Operations, then "Operation leases" |
| Page | **/Admin/Operations/Leases** |
| Who | System Administrator |
| Permission | Page: `operations.read`. Take over: `operations.maintenance` and a recent sign-in |

A lease lets one instance at a time do work that must not run twice (a backup, a restore, a tenant's background jobs). If an instance stops, its lease expires by itself. The table shows Lease, Scope, Owner, Acquired, Last renewed, Expires and State (Held or Free).

**Take over** (only for a Held lease): type a **Reason** (required, up to 500) in the box on its row and click **Take over**. Use only after you have established the owner has really stopped (for example the process crashed and you cannot wait for expiry). The lease is taken and released at once; the previous owner's next renewal fails so it stops.

| Example reason | Blank / JBS-style / Harborline-style |
|---|---|
| | "Backup process crashed, confirmed by process list" / "Recovery lease held by stopped instance, CHG-2026-114 (synthetic)" / "Stale lease, host restarted, HBL-OPS-77 (synthetic)" |

**Success.** "The lease was taken over and released..." **Evidence to keep:** the audit action `operations.lease.taken_over` with your reason. Background-job leases are per tenant; taking one over while a job really runs risks it running twice, which every job tolerates but is better avoided (`docs/OPERATION_LEASES.md`).

---

## 9. Evidence report and support bundle

| | |
|---|---|
| Navigation | Operations, then "Evidence report and support bundle" |
| Page | **/Admin/Operations/Evidence** |
| Who | System Administrator |
| Permission | Page: `operations.read`. Downloads: `operations.evidence.export`. Support bundle also needs a recent sign-in |

The page lists **Deployment models and what is verified** (Verified, or Scaffolded / Not Verified, with notes) and notices. Three links:

| Link | Gives you | Notes |
|---|---|---|
| Download the evidence report (JSON) | What has been backed up, verified and restored, and what is and is not verified | No secrets, connection strings, keys, record content, document content or personal data |
| Download the evidence report (CSV) | The same, safe to open in a spreadsheet | Formula-safe cells |
| Download the support bundle (zip) | Versions, statuses and counts for diagnosis (bundle.json, summary.csv, README.txt, manifest.json listing each file's SHA-256) | Needs a recent sign-in; one audited act (`operations.supportbundle.exported` with size and SHA-256); nothing is stored on the server. Contents: `docs/SUPPORT_BUNDLE.md` |

**Look at the bundle before you send it.** The platform scans it for secrets and produces none if it finds one, but you remain responsible for where it goes. Send it only to whoever supports the installation.

### 9.1 Implementation evidence pack

| | |
|---|---|
| Navigation | Operations, then "Implementation evidence pack (for the sign-off)" |
| Page | **/Admin/Operations/EvidencePack** |
| Who | System Administrator |
| Permission | `operations.evidence.export` |

Shows an **Overall** badge (the worst section), then Generated (UTC), Platform version, Mode, Governance mode and a reference (correlation id), and a table of sections: environment, version, deployment, pack, organisation set-up, governance, identity, e-mail, scanner, masked-data status, configuration publication, migration and reconciliation, UAT, backup, upgrade pre-flight and operational readiness. Each row has a stable code, a status (Verified, Warning, Failed or Not Verified), what it **counts as**, and what the platform found. Below the table are **Accepted exceptions** (code, kind, scope, who accepted, until when, reason), **Open risks** and **Sign-off**.

* **What counts as a pass.** Only Verified counts. A Warning is not a pass until every warning has been accepted on the record. Failed is a failure. Not Verified is never a pass. The operation's own outcome is shown unchanged beside the verdict.
* **Download the pack (ZIP with JSON, CSV, Markdown and file hashes).** It holds no secrets and no contract or document content, uses formula-safe CSV, and carries stable codes, hashes and correlation ids.
* **Sign-off.** The lines (Implementation lead, Business owner, Security reviewer, System Administrator, Data owner) are **blank placeholders** for people to complete outside the platform. The platform never says the environment is ready for Production.

**Evidence to keep:** the downloaded ZIP and its hashes, filed with the change record, with the sign-off lines completed by the named people.

---

## 10. Health and readiness

| | |
|---|---|
| Navigation | Menu "Health and readiness" |
| Page | **/Admin/Health** |
| Who | System Administrator |
| Permission | `ops.manage` |

Shows an **Overall** badge (Healthy, Degraded, Unhealthy), a table of every readiness check (Check id, Description, Status, Detail), and Mode, Culture and time zone, and Version. Detailed checks are shown to administrators only; the public health addresses reveal no dependency detail in Production.

| Check | What Unhealthy or Degraded means | Do |
|---|---|---|
| Backup check (R-BKP-01) | Backup not configured, no verified backup, or the newest is older than the limit | Section 4 |
| R-DOC-01 "Uploaded files are scanned before anyone can use them" | In Production: no real connected scanner, or the demonstration scanner, or skipped scans allowed. Attachments are held | See section 11, the Scanner page (section 11.1) and `docs/DOCUMENT_SCANNING.md`. Not ready until a real scanner is connected, and none is supplied |
| R-EML-01 "Email provider verified" | Healthy only after a test message really reached the provider with the **current** settings. Anything else is Degraded and says why: not configured, not tested, a changed setting, a failed connection or delivery, or a pass older than the limit. Outside Production a simulated or untested provider is not raised as a fault, but the detail text states the state ("Simulated", "Not configured", "Not Verified") and it is never verified email | Section 10.1. Run the test on the Email page |
| R-NET-01 "HTTPS and reverse-proxy configuration" | Degraded for 15 minutes after a request was refused because it did not arrive over HTTPS (the detail names ECP-NET-001 or ECP-NET-002 and how many). Degraded, not Unhealthy, so a wrong proxy setting does not take the health page down | Section 10.2 |
| Database, storage, operations checks | The named dependency is not working | Open the check's detail; fix the setting **[Technical task: System Administrator]** |

Nothing here approves an installation for production.

### 10.1 Email provider

| | |
|---|---|
| Navigation | Operations, then "Email provider: settings, test message and readiness" |
| Page | **/Admin/Operations/Email** |
| Permission | Read: `operations.read`. Saving settings and sending the test message: `operations.maintenance`, with a recent sign-in |

This is the **one** e-mail configuration for the installation. The setup wizard (the notifications stage) and Administration both use it, so there is nothing to keep in step.

| Provider | What it is | Status |
|---|---|---|
| smtp | A real SMTP service | **Implemented.** Verified only against a loopback test relay inside the automated tests. **Not Verified** against your mail service |
| graph | Microsoft Graph | **Scaffolded, Not Verified**, not connected |
| dev-capture, demo-log | Capture or log messages and deliver nothing | **Mocked.** Demonstration and Test only; refused in Production |
| disabled | No e-mail | Not allowed in Production |

**The page shows** a **Readiness** badge (Healthy, Failed, or a warning state such as Simulated, Not configured or Not Verified), the provider and whether its settings are saved in the application or come from the host's starting configuration, what that means with a stable code, what it **counts as** as evidence, last success, last failure and **A pass expires**. Then a **Settings** form (Provider, From address, SMTP host, SMTP port, Require TLS, SMTP user and SMTP password as **secret references**), **Send a test message**, and **Recent tests**.

| Field | Rule |
|---|---|
| SMTP user, SMTP password | A **secret reference** only: `env:NAME`, `file:/path` or `keyvault:name`. A secret typed into the form is refused and never echoed. Only non-secret settings and references are stored |
| Require TLS | Production needs it |
| Test recipient | A mailbox you control. The test connects to the provider (the **connectivity probe**) and sends a **synthetic message with fixed text** that contains no contract information. The Recent tests table shows Connection and Delivery outcomes with codes, the recipient masked, whether the result is for the current settings, and a reference |

**What counts as ready.** Readiness is proved by an actual provider interaction. A pass is tied to the settings: **changing any setting makes it stale** ("No (settings changed)") and it must be tested again. A pass **expires** after 168 hours by default (set by `Ecp:Email:ReadinessMaxAgeHours`, a **[Technical task: System Administrator]**). A configured but unreachable provider is never Healthy. A simulated provider reads "Simulated: nothing delivered" and is never counted as verified e-mail. In Production, setup cannot finish the e-mail stage without a passing test.

**Evidence to keep:** the Readiness panel and the Recent tests row (reference, time), not the mailbox contents.

### 10.2 HTTPS and reverse-proxy diagnostics (R-NET-01, ECP-NET-001, ECP-NET-002)

Production needs HTTPS. A request that arrives over plain HTTP is refused with a short, safe page that gives a stable code and a reference (the page carries no addresses or headers). Give both to the person who runs the installation. The setup wizard also shows TLS and proxy failures in plain language. Full detail: `docs/REVERSE_PROXY.md`.

| Code | Meaning | What to do (**[Technical task: System Administrator]**) |
|---|---|---|
| ECP-NET-001 | The request reached the server over plain HTTP and no HTTPS scheme was forwarded | Use the https address. If a reverse proxy already handles HTTPS, make the proxy forward the scheme (`X-Forwarded-Proto`), and tell the application to trust it |
| ECP-NET-002 | The proxy's forwarded headers arrived but were **not trusted** | Set `Ecp:Network:ForwardedHeaders:Enabled`, and name the proxy in `KnownProxies` (or its network in `KnownNetworks`); then restart |

R-NET-01 on **/Admin/Health** goes Degraded for 15 minutes after a refusal and otherwise states the transport the host expects. **Status:** the diagnostics are Implemented and tested without a server; a real reverse proxy has **Not Verified** end to end.

---

## 11. Document scanning (what you can and cannot see)

| | |
|---|---|
| Navigation | Menu "Document scanning" |
| Page | **/Admin/Documents/Scanning** |
| Who | Tenant Administrator or Procurement Administrator (business roles), within the organisations they administer |
| Permission | `notifications.administer` |

**The System Administrator role does not hold `notifications.administer`**, so the menu entry and page are not available to it unless the installation gives that person a business role as well (an organisational decision, not recommended as a default). The System Administrator's part is technical:

| Task | Where |
|---|---|
| Connect a real scanner provider | **[Technical task: System Administrator]** plus a developer: it needs an adapter implementing the scanner interface, registered in the host. No adapter is supplied; **Future / Not Verified** |
| Keep "Documents__AllowSkippedScan" off in Production | **[Technical task: System Administrator]** host setting |
| Read R-DOC-01 | **/Admin/Health**, section 10 |
| Tune the retry job (maximum attempts, batch size) | **[Technical task: System Administrator]** host settings, see `docs/DOCUMENT_SCANNING.md` |

What the business administrator sees on the page, the fail-closed rule, the retry job, the audit actions and the honest status table are in `docs/DOCUMENT_SCANNING.md`.

### 11.1 Malware scanner page (System Administrator)

| | |
|---|---|
| Navigation | Operations, then "Malware scanner: configuration, self-test and readiness" |
| Page | **/Admin/Operations/Scanner** |
| Permission | Read: `operations.read`. Running the self-test and accepting the demonstration scanner: `operations.maintenance` and a recent sign-in |

**The page shows** a **Readiness** badge, the scanner and its provider key, what that means with a stable code, what it **counts as** as evidence, the last self-test and when a pass expires, and whether skipped scans are released (not allowed in Production). The **Configuration** panel is read-only: it shows `Documents:Scanner:*` as set in the host (Provider: none, mock or a provider key; Endpoint; Credential reference, a secret reference only; Timeout 30 seconds; Largest file scanned 100 MB; Retry every 60 seconds up to `Documents:MaxScanAttempts`; self-test valid for 24 hours). Changing them is a **[Technical task: System Administrator]**.

* **Self-test** (**Run the scanner self-test**): asks whether the scanner is reachable, calls harmless content clean, and detects the standard anti-malware test file (EICAR, harmless). It proves the scanner responds and recognises that file; it does not prove the scanner is good.
* **Status.** No real scanner adapter exists (**Not Verified / not built**). The platform defines the integration boundary and a provider-neutral self-test; the connect-a-provider checklist is in `docs/DOCUMENT_SCANNING.md`.
* **The demonstration scanner (mock)** is registered in Demonstration by default, in Test **only** when Provider is set to mock on purpose, and never in Production. The page shows a prominent **TEST MODE** warning. **The mock is no protection.**
* **Accept the demonstration scanner for a masked-data rehearsal** (Test mode only, and only while the mock is the scanner): enter **Scope**, **Why this is acceptable for the rehearsal** and **Valid for (days, at most 90)**, then **Record the acceptance**. It records who, why, for what scope and until when, and it does not make the scanner real. Masked company data cannot be committed behind the mock without it (refusal MDD-006 in `docs/MIGRATION_EXECUTION_GUIDE.md` section 8.5). The acceptance history shows Active, Expired or Revoked.
* **Production** is Unhealthy on R-DOC-01 and cannot finish setup without a connected, healthy scanner; attachments are held.
* **Notifications.** Quarantine, infection, provider unavailability and exhausted retries raise operational notifications to the recipients already defined in the notification configuration. Nothing is added here.

**Evidence to keep:** the Readiness panel and the last self-test reference; for a masked-data rehearsal, the acceptance row (who, scope, reason, until).

---

## 12. Upgrade: readiness, preview, rehearsal, apply, report, history, validation, rollback evidence

An upgrade moves the installation to the version now running. It needs a verified backup to roll back to, maintenance mode and a passing pre-flight. **It only goes forward; there is no downgrade.** The only way back is to restore the verified pre-upgrade backup and run the matching previous version, which discards everything created after that backup. Rehearse on a restored copy first. Policy: `docs/UPGRADES.md`. Procedure: `docs/UPGRADE_RUNBOOK.md`. Way back: `docs/ROLLBACK_BY_RESTORE.md`.

**Before the window [Technical task: System Administrator]:** deploy the new build next to the old one without admitting users, keep the previous release files or image until the upgrade is accepted, and never run the bare migrate command on an existing Production database (it is refused). The pages below then drive the checks and the apply.

### 12.1 Upgrade readiness (pre-flight)

| | |
|---|---|
| Navigation | Operations, then "Upgrade readiness (pre-flight)" |
| Page | **/Admin/Operations/UpgradeReadiness** |
| Permission | `operations.read`; downloads need `operations.evidence.export` |
| Prerequisite | A Verified backup (section 4); run when quiet (it reads every document and many tables) |

| Field | Required? | Recommended | Rule and consequence |
|---|---|---|---|
| Verified backup to roll back to | Optional list | Choose the backup you took for this change | Left on "(the most recent verified backup)" it uses the newest. A backup that is too old, from another installation or another environment is a finding |

**Run the pre-flight** reads and changes nothing. Result badge: **Ready**, **Ready with warnings**, **Blocked** or **Not Verified**. Facts: installation version and state, version to upgrade to, upgrade manifest SHA-256, migrations pending, correlation id, counts of passed, warnings, blockers and not verified, then the findings table (Code, Result, What it means, Technical detail, What to do). Links: JSON and CSV downloads, and "Preview the upgrade".

| Outcome | Meaning | Do |
|---|---|---|
| Ready | Nothing found | Continue |
| Ready with warnings | Each warning needs your explicit acceptance, by code, at rehearsal and apply. It counts as **Passed** only when every warning has been accepted on the record (see below) | Decide with the owner, accept on the record, continue |
| Blocked (a failure, never a pass) | A critical issue (database newer than the build, schema drift, a changed historical migration, a pack or workflow in use that would fail, a bad backup) | Fix it; never work around; run again |
| Not Verified | A check could not be made (for example SQLite) | Resolve it, or accept the gap in writing. It is not a pass |

At this early stage "not in maintenance mode" is expected. Keep the JSON.

**Accepted warnings (who, why, expiry).** Under a warning in the findings table, **Accept this warning** opens a small form: **Why it is acceptable (kept on the record)** (required, up to 500 characters) and **Valid for (days, at most 90)**, then **Accept the warning**. Only a System Administrator with a recent sign-in (`operations.maintenance`) can do it. The **Accepted warnings** table lists Code, Accepted by, Reason, Until and State (Active, Expired or Revoked), with **Revoke** on an active row. Accepting does not remove the warning: the pre-flight still reports Ready with warnings, and the evidence counts it as Passed only while **every** warning has a current acceptance. Run the pre-flight again to see it counted. A Blocked pre-flight, or a Not Verified one, is never counted as a pass. Accepted exceptions appear in the evidence pack (section 9.1).

### 12.2 Upgrade preview, rehearsal and apply

| | |
|---|---|
| Navigation | Operations, then "Upgrade preview, rehearsal and upgrade" |
| Page | **/Admin/Operations/UpgradePreview** |
| Permission | Page: `operations.read`. Rehearse: `operations.restore` and a recent sign-in. Apply: `operations.upgrade` and a recent sign-in |

**Step 1: choose the backup you can roll back to.** Pick a Verified backup, **Preview the upgrade**.

**Step 2: what would run.** From and To versions, pre-flight badge with blocker and warning counts, manifest SHA-256, **Breaking changes**, **Migrations to apply** (each module and id, kind of change, and "review recorded" or "review missing"), links to the migration report and the pre-flight, **The upgrade cannot start** (blockers) and **Warnings that need the owner's acceptance**. The Business Lead reads the breaking changes and warnings.

**Step 3: rehearse on a restored copy (recommended).** The backup is restored into a new database and the upgrade is run on the copy. Nothing in use changes.

| Field | Required? | Recommended | Examples | Rule and consequence |
|---|---|---|---|---|
| New database name for the copy | Required, letters, digits, underscores, up to 64 | Purpose and month | Blank: `ecp_upgrade_rehearsal_2026_10` / JBS-style: `ecp_jbs_upg_rehearsal_2026_10` / Harborline-style: `ecp_hbl_upg_rehearsal_2026_10` | Must not exist |
| Reason | Required, up to 500 | Change reference | "Rehearsal for CHG-2026-114 (synthetic)" | Audited |
| Type `REHEARSE UPGRADE FROM <backup id>` | Required | Copy exactly | `REHEARSE UPGRADE FROM bk-20261017T031500Z-0a1b2c3d` | Mismatch is refused |
| "I accept" boxes | One per warning | Tick only warnings you have decided to accept | | Unticked warnings block |

**Rehearse the upgrade** shows a **Rehearsal result** (Succeeded, Succeeded with warnings or Failed) and the evidence file. Note the migration durations to plan the window. If the rehearsal fails, the real upgrade must not proceed.

**Step 4: upgrade this installation.** Shown only when the pre-flight is Ready or Ready with warnings. **Maintenance mode must be on** (section 7), and you should have taken a fresh verified backup inside the window.

| Field | Required? | Recommended | Examples | Rule and consequence |
|---|---|---|---|---|
| Reason | Required, up to 500 | Change id and why | "Apply 1.1.0, CHG-2026-114 (synthetic)" | Audited |
| Type `UPGRADE <from> TO <to>` | Required | Copy exactly from the label | `UPGRADE 1.0.0 TO 1.1.0` | Mismatch is refused |
| Manifest SHA-256 you expect | Optional | Paste from the release notes | | Mismatch stops the upgrade |
| Accept boxes | One per warning | As rehearsed | | |

**Upgrade this installation** takes the upgrade and recovery leases, waits up to 60 seconds for background jobs to finish (it stops before any migration if they do not), migrates module by module, records each migration, validates, and **releases maintenance mode only if validation passes**.

**Success.** **Upgrade result** badge Succeeded or Succeeded with warnings, "The upgrade passed validation and maintenance mode was released", a per-module list of migrations and durations, and the evidence file. Open **Post-upgrade validation** next.

**Failure.** The installation is held in maintenance mode with cause "Upgrade failed". **Do not release it by hand and do not run the upgrade again to see if it works.** Roll back by restore (section 12.6). The dashboard will never offer a retry.

### 12.3 Migration report

**/Admin/Operations/MigrationReport** (Operations, "Migration report"; `operations.read`; the SQL download needs `operations.validate`). Shows platform version, installation version, manifest hash, **Schema drift** ("None: the database matches the schema its history says it has", or differences; "Not verified (SQL Server only)" otherwise), breaking changes, **Actions for the operator**, **Compatibility notes**, and per module a table of migrations (applied or pending, kind of change, review, recorded checksum, objects touched). "Download the SQL of the pending migrations (for review)" gives a text file for your DBA; nothing runs from this page. A migration marked "REVIEW MISSING" that changes existing data or structure is a reason to stop.

### 12.4 Upgrade history

**/Admin/Operations/UpgradeHistory** (`operations.read`). Pre-flights, rehearsals, upgrades and rollback proofs, newest first: Started, Kind, Reference, Outcome, Summary, Correlation id. Each is also in the audit trail. Evidence files hold no secrets, record content or personal data and sit in the backup folder under its evidence directory (**[Technical task: System Administrator]** to copy them to your change record).

### 12.5 Post-upgrade validation

**/Admin/Operations/PostUpgradeValidation** (`operations.read`). Shows the last upgrade (badge, summary, time, evidence file) and its checks: schema versions, migration records, tenant isolation policies, data unchanged from before the upgrade, audit chains, documents, packs and signatures, configuration hashes, pinned workflows, regional settings and assigned packs. "No upgrade has been run on this installation yet" until one has. After a successful upgrade: read it, admit users to the new build, watch health and the first approvals, take a new verified backup with the new version, and **keep the pre-upgrade backup and the previous release's files** until the owner accepts the upgrade.

### 12.6 Rollback evidence (prove the way back)

| | |
|---|---|
| Navigation | Operations, then "Rollback evidence" |
| Page | **/Admin/Operations/RollbackEvidence** |
| Permission | Page: `operations.read`. Prove: `operations.restore` and a recent sign-in |

A red notice states: **A rollback discards everything created after the backup it restores.** This page proves that the backup restores into a clean database, is the pre-upgrade state and is intact. It never runs an older version against an upgraded database.

| Field | Required? | Recommended | Examples | Rule and consequence |
|---|---|---|---|---|
| Verified pre-upgrade backup | Required list | The backup taken inside the window | the id from your change record | |
| New database name for the restored copy | Required | Purpose and month | Blank: `ecp_rollback_proof_2026_10` / JBS-style: `ecp_jbs_rollback_2026_10` / Harborline-style: `ecp_hbl_rollback_2026_10` | Must not exist |
| Reason | Required, up to 500 | Change id | "Rollback proof CHG-2026-114 (synthetic)" | Audited |
| Type `PROVE ROLLBACK TO <backup id>` | Required | Replace the words backup id by the id you chose | `PROVE ROLLBACK TO bk-20261017T031500Z-0a1b2c3d` | Mismatch is refused. If the label still shows the words "<backup id>", type the id you selected |

**Prove the rollback** shows a result badge, how many audited events recorded after the backup would be discarded (the data-loss window), the **platform version you must run** against the restored database, a check table and the evidence file. **Most recent proof** appears below.

**To actually roll back** (only after a failed upgrade or a decision by the owner): leave maintenance mode on, stop the new application, restore the verified pre-upgrade backup (in place on the command line, or into a new database and repoint), start the **previous** application version, validate, admit users. Those are **[Technical task: System Administrator]** steps (`docs/ROLLBACK_BY_RESTORE.md`, `docs/OPERATIONS_RUNBOOK.md` sections 5 and 6). Tell the business what window of work is lost.

**Evidence to keep for an upgrade:** pre-flight JSON, dashboard JSON before and after, manifest SHA-256, rehearsal evidence and duration, warnings accepted (codes and who accepted), the pre-upgrade backup id and verification time, post-upgrade validation outcome, rollback proof, who accepted the upgrade (`docs/UPGRADE_RUNBOOK.md` section 10).

---

## 13. Audit

The platform writes an audit event for every operations act. The actions are:

| Area | Audit actions |
|---|---|
| Backup | `operations.backup.started`, `operations.backup.completed`, `operations.backup.failed`, `operations.backup.verified` |
| Restore | `operations.restore.started`, `operations.restore.completed`, `operations.restore.failed`, `operations.restore.lineage` |
| Validation | `operations.validation.run` |
| Maintenance and leases | `operations.maintenance.enabled`, `operations.maintenance.disabled`, `operations.lease.taken_over`, `operations.lease.expired_takeover`, `operations.lease.released_all` |
| Evidence | `operations.evidence.exported`, `operations.supportbundle.exported`, `operations.upgrade.preflight.exported`, `operations.upgrade.evidence.exported`, `operations.upgrade.script.read` |
| Upgrade | `operations.upgrade.started`, `operations.upgrade.completed`, `operations.upgrade.failed`, `operations.upgrade.rehearsal.requested`, `operations.upgrade.rehearsal.started`, `operations.upgrade.rehearsal.completed`, `operations.upgrade.rehearsal.recorded`, `operations.upgrade.rehearsal.failed` |

**Reading them on screen.** **Audit events** (**/Admin/Audit**) is for people with `audit.read` (Auditor) or `audit.read_admin` (Tenant and Organisation Administrators). Filter with **Action starts with** (for example `operations.`) and **Export evidence (JSON with hashes)** needs `audit.export`. The role matrix gives the System Administrator neither permission, and I found no page that uses the system-audit permission it does hold. **Whether and how a System Administrator can browse the installation's own audit events on screen is therefore Not Verified.** Until that is confirmed, rely on the Operations history tables, **Upgrade history**, the evidence report and the evidence files, and ask your platform supplier how installation-tenant audit events are read in your deployment.

---

## 14. Command-line verbs (not on any screen)

Every item here is **[Technical task: System Administrator]**, run on the server as `dotnet Ecp.Host.dll <command>` (inside a container, `docker exec ecp dotnet Ecp.Host.dll <command>`). Exit codes: 0 success, 2 refused or failed, 64 usage. The command line has no signed-in user and relies on access to the server. Exact options: `docs/OPERATIONS_RUNBOOK.md` section 11 and `docs/UPGRADE_RUNBOOK.md`.

| Verb | What it does | Page equivalent |
|---|---|---|
| backup | Create and verify a backup (optionally hold maintenance) | Backups |
| list-backups | List backups and states | Backups |
| verify-backup | Re-check a backup | Verify again |
| restore | Restore beside the running installation, validate, release. With the in-place option: disaster recovery over the active database, application stopped | Restore (new database only) |
| validate | Validation on the running installation | Validation |
| maintenance on or off | Maintenance mode | Maintenance |
| support-bundle, evidence | Write the support bundle or evidence report to a file | Evidence |
| upgrade-manifest-verify | Check the manifest this build carries | none |
| upgrade-preflight, upgrade-plan | Readiness and plan | Upgrade readiness, Upgrade preview |
| migration-report, upgrade-script | Migration inventory; SQL of pending migrations | Migration report |
| upgrade-rehearse, upgrade-apply | Rehearse on a copy; apply | Upgrade preview |
| upgrade-rollback-evidence, upgrade-history | Prove rollback; list history | Rollback evidence, Upgrade history |

Other host tasks that are never on a screen: installing and renewing the identity provider registration, secrets and the data-protection key ring, certificates and TLS, the backup scheduler, the shared backup folder permissions, deploying the previous or new build, dropping rehearsal databases, registering the public key of a pack publisher (the pack is signed outside the application by the Pack Publisher; the application never holds a private key: `docs/PACK_SIGNING.md`).

---

## 15. Suggested rhythm (the organisation decides the real one)

| When | Task |
|---|---|
| Daily during hypercare, then weekly | Open the dashboard; save the JSON; clear anything Failed; explain anything Not Verified |
| Per your backup policy | Verified backup (your scheduler runs it); **Verify again** after copying |
| After every release and at least quarterly | Restore rehearsal into a new database, validation, record time taken |
| Before every upgrade | Pre-flight, preview, rehearsal on a copy, rollback proof; business reads breaking changes and warnings |
| Always | Leave maintenance mode off unless a task needs it |

---

## Evidence to keep

- Backup id, manifest SHA-256 (in your own records), outcome and warnings.
- Restore: result, validation outcome, evidence file, elapsed time against your recovery objective.
- Dashboard JSON before and after significant work; evidence report; support bundle only when support requested it.
- Implementation evidence pack (ZIP) with sign-off lines completed by people; Email and Scanner page readiness (reference and time); any scanner acceptance (who, scope, reason, expiry).
- Upgrade: pre-flight JSON, rehearsal and rollback-proof evidence, accepted warnings with who accepted, why and until when, post-upgrade validation, upgrade history entries.
- Maintenance and lease reasons with times (UTC).
- A signed record by the owner of each decision the platform does not make. This record is evidence of what ran, **not approval for production use**.

## If something goes wrong

| Symptom | What to do |
|---|---|
| Page says forbidden or not found | You are outside the installation tenant, or lack the permission listed for that page. Operations pages are closed to every customer-tenant role by design |
| "Confirm it is you" | Click **Confirm** and repeat the action |
| Create backup missing or "No backup directory is configured" | **[Technical task: System Administrator]** configure the backup directory and account |
| "Another operation holds the lease" | Wait; check **Operation leases**; take over only when the owner has stopped (section 8) |
| Restore blocked or failed | Read blockers or failure and the validation table; restore again into a **new** name; the installation stays in maintenance mode until you acknowledge the state (section 7) |
| Validation Failed | Read the check's "What to do". Do not release a failed copy. VAL-009 and VAL-010 are serious (audit chain, documents) |
| Pre-flight Blocked | Fix the cause named; never work around it |
| Upgrade failed | Maintenance mode stays on. Do not retry. Roll back by restore with the previous version (section 12.6) |
| Dashboard shows Not Verified | Decide: look at it, or accept the gap in writing. It is never a pass |
| Attachments held | See `docs/DOCUMENT_SCANNING.md` and the Scanner page (section 11.1); expected until a real scanner is connected |
| Email says Simulated, Not Verified or Failed | Section 10.1. Only a test that really reached the provider with the current settings is a pass; a changed setting or an expired pass needs a new test |
| A visitor sees ECP-NET-001 or ECP-NET-002 | Section 10.2 and `docs/REVERSE_PROXY.md`. Use the https address, or fix the proxy's forwarded scheme and trust settings |
| Anything unexplained | Note the correlation id on the page, download the support bundle (recent sign-in), review it, send it to whoever supports you |

## What was and was not verified

- **Verified by reading the code:** every page, field, button, permission and recent-sign-in rule above (Operations pages and their handlers, the Operations and Upgrade administration services, the menu, the Health, Audit and Document scanning pages, the role-permission matrix), and the command names.
- **Verified by the project's own tests (as recorded in `deploy/STATUS.md`):** backup, restore, validation, maintenance, leases and upgrade on SQL Server for the container model. I did not run them for this guide.
- **Not run:** no live installation was driven for this guide.
- **Not Verified:** IIS and Azure; real identity provider; real e-mail (SMTP is verified only against a loopback test relay, Graph is not connected); a real malware scanner (none is built); a real reverse proxy end to end; how a System Administrator reads installation-tenant audit events on screen (section 13); the label on the Rollback evidence page before a backup is chosen (section 12.6); whether the evidence files can be downloaded from a page (the Upgrade history page has a handler but no link, so treat the backup folder as the source).
