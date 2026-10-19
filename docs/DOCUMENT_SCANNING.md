# Document scanning: the malware gate

**Purpose.** Describe exactly how an uploaded file is held, scanned, released or quarantined, what an administrator sees and can do on the Document scanning page, what is recorded and audited, and what is and is not built. Written from the code, not from intent.

**Who it is for.** Business administrators who use the Document scanning page (Tenant Administrator, Procurement Administrator), the System Administrator who configures the installation, security and risk reviewers, and anyone deciding whether attachments can be used in Production.

**Prerequisites.** Read-only reading of this guide needs none. To use the page you need the permission `notifications.administer` (held by the Tenant Administrator and the Procurement Administrator in the role matrix; **not** by the System Administrator). See section 6.

**Status label.** The gate (hold, retry, quarantine, history, audit, page, readiness check): **Implemented**. The integration boundary (provider configuration, provider-neutral self-test, Scanner page at `/Admin/Operations/Scanner`, recorded acceptance of the demonstration scanner, operational alerts): **Implemented** (ADR-0038). A demonstration scanner: **Mocked** (Demo by default; Test only when asked for by name; never Production). A real scanner adapter: **Future / Not Verified**: none is built, none has been exercised, and no real scanner is claimed anywhere in this product. So in Production every uploaded file stays held and Production cannot report ready.

> Nothing here is a security certification or production approval. Production approval is external governance. A passing readiness check R-DOC-01 means the gate is wired to a real, connected scanner; it does not mean the scanner is good.

Code reviewed: `src/Modules/Ecp.Documents/Scanning.cs`, `src/Modules/Ecp.Documents/MigrationPort.cs` (the scanner interface lives here), `src/Modules/Ecp.Documents/DocumentsApi.cs`, `src/Modules/Ecp.Documents/DocumentQuery.cs` (the mock scanner), `src/Modules/Ecp.Documents/Domain/DocumentScanAttempt.cs`, `src/Ecp.Host/DocumentScanReadinessCheck.cs`, `src/Ecp.Host/BackgroundJobService.cs`, `src/Ecp.Host/PlatformComposition.cs`.

---

## 1. The rule in one paragraph

A file can be downloaded, copied to another contract, or counted as evidence (for example as an executed copy, in a lifecycle rule that needs a document, or in a signature request) only when its latest version has been called **Clean** by a connected scanner. Until then it is **held**. A scan that fails is **never** read as clean. A file found **infected** is **quarantined** and is never released.

---

## 2. The scanner provider interface

A scanner is a component that implements the scanner interface (`IMalwareScanner`, defined in `src/Modules/Ecp.Documents/MigrationPort.cs`):

| Member | Meaning |
|---|---|
| Name | A short name recorded with every scan attempt and shown on the page (for example the provider's name) |
| IsConnected | False when no scanner is connected: nothing is scanned and files stay held. Defaults to true for a real adapter |
| IsDemonstrationOnly | True for the stand-in used in Demo and Test. It proves the hook, not protection. Defaults to false |
| ScanAsync(content) | Reads the stored file and returns a verdict |

Verdicts and the status they set on the file version:

| Scanner verdict | Version status | Meaning |
|---|---|---|
| Clean | Clean | Scanner found nothing. The only status that releases a file by default |
| Infected | Infected | Quarantined; never released |
| Skipped | Skipped | The scanner chose not to scan (or none is connected). Held |
| Error, or any exception while reading or scanning | Error | The scan failed. Held; retried a bounded number of times |
| (not yet scanned) | Pending | The starting state. Held |

Registration (what is actually in the host):

- The Documents module registers a default scanner named **none** that is not connected and answers Skipped (`DisabledMalwareScanner`).
- In **Demo** the host registers the demonstration scanner after it by default (`MockMalwareScanner`, named "mock (demonstration only)"). In **Test** it is registered only when `Documents:Scanner:Provider` is set to `mock` on purpose; otherwise Test reads "No scanner connected" like Production. A Production host refuses to register it, whatever the setting says.
- In Production the demonstration scanner is never registered. A real provider adapter must replace the default registration. **No adapter is supplied.** Writing and registering one is a development task, not a configuration setting: **[Technical task: System Administrator]** with a developer.

The same interface is used in three places: when a file is uploaded to a contract (new document or new version), by the scheduled retry job, and by the migration module when it stages attachments. In migration, with `Migration:RequireMalwareScan` (forced on in Production) a file that could not be scanned is held (`MIG-F014`) and an infected file is rejected (`MIG-F013`); see `docs/MIGRATION.md`.

---

## 3. What happens to a file

### 3.1 On upload

1. The file is stored (content-addressed, size-limited, within the tenant).
2. It is scanned straight away. If the scanner throws or cannot read the file, the result is Error, not an upload failure.
3. In one transaction the document version is saved with its status and a scan-attempt row is written with trigger **upload**.
4. If it was infected, the audit action `document.quarantined` is recorded. The ordinary `document.added` (or `document.version_added`) audit event also carries the scan result for a new document.

### 3.2 Who may use a file, by status

| Status | Download | Copy to another contract | Counts as evidence in rules (guards, signature requests, register authorisation, renewal copy) | Retried by the job? | Can an administrator scan it again? |
|---|---|---|---|---|---|
| Clean | Yes | Yes | Yes | No (settled) | Not needed |
| Pending | No (held) | No | No | Yes | Yes |
| Skipped | No (held), **unless** the skipped-scan option is on | Same as download | Same as download | Yes | Yes |
| Error | No (held) | No | No | Yes, until the attempt limit | Yes (even after the limit) |
| Infected | No (quarantined) | No | No | No | **No: refused** |

**The one exception to "only Clean releases".** The host option `Documents:AllowSkippedScan` (environment variable `Documents__AllowSkippedScan`) defaults to **false**. If an operator sets it true, a Skipped file is treated as released. This is a product-owner and security decision. The Document scanning page shows a red alert when it is on, and the R-DOC-01 check reports Production as not ready (section 7). Leave it off.

### 3.3 The scheduled retry

The background job service runs once per active tenant on the notifications timer (`Notifications:JobIntervalSeconds`, default 60 seconds; **0 turns all background jobs off, including this one**). After the notification jobs, it runs the scan job. The job:

- Does nothing if no scanner is connected (files stay held; the readiness check says so).
- Takes held versions in the states Pending, Error and Skipped, oldest first, and examines at most **ScanBatchSize** per run (`Documents:ScanBatchSize`, default 50).
- For a version in Error, counts the Error attempts already recorded. At **MaxScanAttempts** (`Documents:MaxScanAttempts`, default 5) it gives up on that version: it stays held and is left for an administrator. It is never released.
- Re-reads each version inside its transaction and records nothing if another run (or another instance) already settled it, so running twice is safe.
- Skips infected and clean versions.
- Pauses while maintenance mode is on, and each tenant's cycle holds a lease so two instances never run the same tenant's jobs at once.

Pending and Skipped versions are retried without a limit; only Error attempts are counted toward the maximum.

A one-off administrator retry (**Scan again**, section 5) records trigger **manual**; the job records trigger **rescan**.

---

## 4. The DocumentScanAttempt history

Every scan, whatever triggered it, is a row in the table **DocumentScanAttempt**. It is the evidence that a file was held, retried and released or quarantined. It never holds file content or scanner output beyond a short note.

| Column | Meaning |
|---|---|
| Id, TenantId, OrganisationId | Identity and ownership (tenant-owned, like every record) |
| DocumentId, VersionId | Which document version was scanned |
| AttemptedUtc | When (UTC) |
| Outcome | The status the scan produced: Clean, Infected, Skipped, Error (or Pending) |
| Provider | The scanner name at the time |
| Trigger | **upload**, **rescan** (the scheduled retry) or **manual** (an administrator) |
| Detail | A short bounded note (at most 300 characters), for example "The scanner could not complete the scan." or "The scan could not be completed (ExceptionTypeName)." The exception message and any scanner payload are never stored |

The Document scanning page reads this table to show **Attempts**, **Last note** and whether the job has given up on a version.

---

## 5. The Document scanning page

| | |
|---|---|
| Navigation | Menu "Document scanning" |
| Page | **/Admin/Documents/Scanning** |
| Who | Tenant Administrator or Procurement Administrator |
| Permission | `notifications.administer`, scoped to the organisations the person administers |
| Prerequisites | None. Scanning is only reached from the menu if the permission is held; otherwise the page is forbidden |

**Scope.** The page and the service apply the permission per organisation. A person who administers organisation A sees only A's held files and counts; one who holds it for the whole tenant sees all; one who holds it nowhere is refused ("You cannot view or retry document scanning."). A file in an organisation you do not administer is "not found" even if you know its id. Another tenant sees nothing.

### 5.1 The Scanner panel

| Display | Meaning |
|---|---|
| Badge "No scanner connected" | "Every uploaded file is held. Production needs a scanner provider connected by the system administrator." |
| Badge "Demonstration scanner" | "It checks only for a standard test string. It is for trials and is not protection." |
| Badge "Connected" and the provider name | A real adapter is registered and reports connected |
| Red alert "Files whose scan was skipped are allowed to be used..." | `AllowSkippedScan` is on. Turn it off in Production |
| Counts row | Clean (released), Waiting, Skipped, Failed, Infected (quarantined), Oldest held (UTC) |
| Hint | "A failed scan is tried again automatically up to [MaxScanAttempts] times, then left here for an administrator. A failure is never treated as clean." |

### 5.2 Files that are held

A table of up to 200 held versions, newest first: Document, File, State, Uploaded (UTC), Attempts, Last note, and a button.

| State shown | Means |
|---|---|
| Quarantined (infected) | Found infected. No button. Stays in quarantine |
| Failed: will be retried | Error, attempts below the maximum |
| Failed: needs an administrator | Error, attempts at or above the maximum |
| Skipped | The scanner skipped it |
| Waiting | Pending |

**Scan again.** Shown for every held file except an infected one, and only if a scanner is connected. It needs a recent sign-in (you are sent to **Confirm it is you**, then back). It scans that one file now and records a manual attempt. Success message: "The file was scanned and released." Otherwise: "The file was scanned again. It is [state] and stays held." There is nothing on this page that releases an infected file, and none that overrides a scan result.

**Common errors.**

| Message | Meaning and recovery |
|---|---|
| "No malware scanner is connected, so nothing can be scanned. The file stays held until one is connected." | Nothing to retry until the System Administrator connects a scanner provider (a technical, development task) |
| "That file was found to be infected. It stays in quarantine and is never released by scanning it again." | Expected. Handle the infected file according to your security policy (for example ask the contract owner for a clean replacement) |
| Not found | The file is outside the organisations you administer |
| "Nothing is held. Every uploaded file has been scanned and released." | Good state |

**What the page does not do.** It does not send any e-mail or in-app message when a file is held, fails or is quarantined (no notification is wired to these events), it does not delete a quarantined file, and it does not let you release a file by hand. Administrators must look at the page; schedule that in hypercare and daily checks (`docs/UAT_AND_CUTOVER_GUIDE.md`). Notifying on quarantine is **Future**.

**Worked examples (the gate does not differ by business).** Synthetic file names only.

| Case | Example upload | Demo or Test (demonstration scanner) | Production with no scanner |
|---|---|---|---|
| Blank | "test-agreement-001.pdf" on a contract with the CON prefix | Clean, released | Skipped, held, "No scanner connected" |
| JBS-style | "test-supply-agreement-001.pdf" on JBSAUS | Clean, released | Held |
| Harborline-style | "test-framing-subcontract-001.pdf" on HBL | Clean, released | Held |
| Any case, a harmless text file containing the standard antivirus test text | "scanner-test.txt" | Quarantined; no **Scan again**; audit `document.quarantined` | Not applicable: nothing is scanned |

---

## 6. Permissions and roles

| Who | Can do |
|---|---|
| Tenant Administrator, Procurement Administrator (`notifications.administer`) | Open the page, see counts and held files for their organisations, use **Scan again** (recent sign-in) |
| System Administrator | Does **not** hold `notifications.administer`, so no menu entry or page. Reads R-DOC-01 on **/Admin/Health**; sets host options; connects an adapter (technical) |
| Everyone else | Forbidden. A viewer is refused |

An organisation's administrators see only their own files. The role names in a business's pack may differ; the permission is what counts.

---

## 6A. The Scanner page and the provider contract

The page `/Admin/Operations/Scanner` (permission `operations.read` to look; the System Administrator acts) shows the scanner as a whole: which provider is configured, its endpoint or service name, whether a secret reference is set (never the secret), its limits, the last self-test and when it goes stale, and the state: **Not connected**, **Demonstration** (prominent TEST MODE warning, Not Verified), **Connected, not tested** (Not Verified), **Healthy**, **Failed**, **Stale** or **Refused** (the configuration asks for something this mode refuses, such as the demonstration scanner in Production).

Configuration (`Documents:Scanner`, all **[Technical task: System Administrator]**; secrets only as references):

| Setting | Default | Meaning |
|---|---|---|
| `Provider` | empty (Demo: `mock`; otherwise `none`) | `none`, `mock`, or the key a provider adapter registers under |
| `Endpoint` | none | A host and port or URL for the adapter. Not a secret |
| `SecretRef` | none | A secret reference (`env:`, `file:`, `keyvault:`); the value is never stored or shown |
| `TimeoutSeconds` | 30 | How long one scan may take |
| `MaxFileSizeBytes` | 100 MB | A larger file is held, not released |
| `RetryBackoffSeconds` | 60 | Wait before a failed scan is tried again; the number of tries is `Documents:MaxScanAttempts` |
| `SelfTestMaxAgeHours` | 24 | How long a passed self-test counts before the state reads Stale |

The **self-test** is provider-neutral: it asks the scanner whether it is reachable, expects Clean for harmless content and Infected for the standard anti-malware test file (assembled at run time; harmless). It proves the scanner responds and detects the standard test file; it does not prove the scanner is good. The result is kept in memory, so after a restart the scanner reads Not Verified until it is tested again, which is the honest state. Only a connected, real scanner that passed its self-test with the current configuration counts as Passed in evidence; the demonstration scanner, a missing scanner and an untested one are Not Verified, and a failed or refused one is Failed.

### Accepting the demonstration scanner for a masked-data rehearsal (Test only)

In Test the demonstration scanner is no protection. A migration batch marked as masked company data cannot be committed while the demonstration scanner is in use unless a System Administrator has recorded an **acceptance** on the Scanner page (recent sign-in, `operations.maintenance`): who accepted, the reason, the scope (for example "masked-data rehearsal for organisation X") and an expiry of at most 90 days. The acceptance is audited, can be revoked, expires by itself, and is listed on the evidence pack. Demo cannot record one (nothing there needs it) and Production refuses the demonstration scanner outright. The Scanner page still labels the scanner Not Verified after acceptance: acceptance records a decision, not protection.

### Operational alerts

Quarantine of an infected file, a scan that failed, and a file still unscanned after the allowed number of attempts (`document.quarantined`, `document.scan.failed`, `document.scan.retry_exhausted`) raise in-app and email notifications to the Tenant Administrators and Procurement Administrators of the organisation, linking to the Document scanning page. A scanner outage produces one message per person per day, not one per file. Email delivery follows the email configuration (`docs/OPERATIONS_QUICK_GUIDE.md`); the alert text never names the file's content.

### Checklist: connecting a real provider (a development and security task)

1. Write an adapter implementing `IMalwareScanner` (section 2) in an adapter project, register it in the host under a provider key, and set `Documents:Scanner:Provider` to that key. Nothing in the contract changes.
2. Put the endpoint in `Endpoint` and the credential in a secret provider referenced by `SecretRef`.
3. Make the adapter report `IsConnected` and `IsDemonstrationOnly = false`, and implement a health probe if the provider has one.
4. Run the self-test on the Scanner page: expect Healthy.
5. Upload a harmless file (released) and the standard test string (quarantined) in a non-production environment; keep the evidence.
6. Confirm R-DOC-01 is Healthy, `AllowSkippedScan` is false and the alerts reach the right people.
7. Record the adapter's own test evidence outside this product. It remains **Not Verified** here until that evidence exists.

---

## 7. The readiness check R-DOC-01

Name: "Uploaded files are scanned before anyone can use them". It runs with the other readiness checks and appears on **/Admin/Health** (System Administrator) and in the health panel of the Operations dashboard.

| Mode | Situation | Result |
|---|---|---|
| Production | No scanner connected | **Unhealthy**: every uploaded file is held and cannot be downloaded, copied or counted as evidence until a scanner provider is connected |
| Production | Demonstration scanner configured | **Unhealthy** (Refused): not protection, and it is never registered |
| Production | Skipped scans allowed | **Unhealthy**: turn it off so only Clean files are released |
| Not Production | No scanner connected (or connected but untested, stale or failed) | **Degraded**: expected in a trial |
| Any | Real scanner connected (and the cases above do not apply) | **Healthy**: "Scanner '[name]' is connected." |
| Demo | Demonstration scanner | **Healthy**: "The demonstration scanner is connected (trial only)." |
| Test | Demonstration scanner | **Degraded** until an acceptance is recorded on the Scanner page; with one, the message names who accepted it, until when and for what scope. It is never presented as protection |
| Production | Connected scanner that has not passed its self-test, or whose test is stale or failed | **Unhealthy**; only a Healthy scanner makes the check Healthy |

An Unhealthy check in Production does not stop the application. It stops attachments being usable, and it must be explained in go/no-go evidence.

---

## 8. Audit actions

| Audit action | Recorded when |
|---|---|
| `document.quarantined` | A version is found infected, on upload or by a retry |
| `document.scan.failed` | A retry (scheduled or manual) ends in Error |
| `document.scan.completed` | A retry ends in Clean, or in Skipped |
| `document.added`, `document.version_added` | A document or version is added (the first carries the scan status) |

Each event records the organisation, the classification, and a small JSON of outcome, provider, trigger and document id. **Two honest limits:** a scan made at upload is written as an attempt row and audits only an infection (`document.quarantined`), not a clean result or an Error; and `document.scan.completed` is also written when a retry ends in Skipped, in which case the file is still held. Read the **Outcome** in the event values, not just the action name. Browse them on **Audit events** (**/Admin/Audit**, filter "Action starts with" `document.`) if you hold `audit.read` or `audit.read_admin`.

---

## 9. Settings (all **[Technical task: System Administrator]**)

| Setting | Default | Effect |
|---|---|---|
| `Documents:AllowSkippedScan` (env `Documents__AllowSkippedScan`) | false | If true, Skipped files are released. Keep false in Production |
| `Documents:MaxScanAttempts` (env `Documents__MaxScanAttempts`) | 5 | Error attempts before the job gives up on a version |
| `Documents:ScanBatchSize` (env `Documents__ScanBatchSize`) | 50 | Held versions examined per tenant per job run |
| `Notifications:JobIntervalSeconds` | 60 | Timer for all background jobs; 0 turns them off |
| `Migration:RequireMalwareScan` | forced on in Production | Migration holds files it could not scan (`MIG-F014`) |
| `Documents:Scanner:*` | see section 6A | Provider, endpoint, secret reference, timeout, size limit, retry backoff, self-test age |

The default file `src/Ecp.Host/appsettings.json` sets `AllowSkippedScan` to false. Do not put a provider's credentials in a settings file; use the secret provider (`docs/OPERATIONS_RUNBOOK.md`).

---

## 10. Honest status table

| Capability | Status | Notes |
|---|---|---|
| Scanner provider interface | **Implemented** | `IMalwareScanner`; integration and API tests exist (`tests/Ecp.Integration.Tests/DocumentScanningScenarios.cs`, `tests/Ecp.Api.Tests/DocumentScanningUiTests.cs`); results not re-run for this guide |
| Fail-closed hold (Pending, Skipped, Error held) | **Implemented** | With `AllowSkippedScan` false |
| Quarantine of infected files | **Implemented** | Never downloadable, copyable, or evidence; rescan refused |
| DocumentScanAttempt history | **Implemented** | Section 4 |
| Scheduled bounded retry (MaxScanAttempts, ScanBatchSize) | **Implemented** | Needs a connected scanner; needs background jobs on |
| Document scanning page and organisation scope | **Implemented** | Section 5 |
| Audit actions | **Implemented** | With the limits in section 8 |
| Readiness check R-DOC-01 | **Implemented** | Section 7 |
| Demonstration scanner (standard test string only) | **Mocked** | Demo and Test only; never registered in Production; not protection |
| **A real scanner adapter** | **Future / Not Verified** | None is supplied or tested. Until one is added and connected, no uploaded file can be released in Production |
| Notification to administrators on quarantine, failure or exhausted retries | **Implemented** | Section 6A; recipients are the organisation's Tenant and Procurement Administrators; a real mail service is Not Verified |
| Scanner page, provider configuration, provider-neutral self-test, Stale/Refused states | **Implemented** | Section 6A; tested with a fake provider, not a real scanner |
| Acceptance of the demonstration scanner for a masked-data rehearsal | **Implemented** (Test only) | Who, reason, scope, expiry (90 days at most), audited, revocable |
| Release or delete of a quarantined file from the page | **Not provided** by design | Handle under your security policy |
| Scanning of already-stored files in bulk when a scanner is first connected | **Implemented via the retry job** | Held files are picked up in batches, oldest first; not separately tested at scale (**Not Verified**) |
| Production use of attachments | **Blocked until a real scanner is connected** | Or the organisation decides in writing not to use attachments (see `docs/PRODUCTION_READINESS_CHECKLIST.md`) |

---

## Evidence to keep

- The Document scanning page counts and the Scanner badge on cutover day, and at each hypercare review (screenshot).
- The R-DOC-01 result from **/Admin/Health** (or the Operations dashboard JSON).
- The written decision on attachments: a real scanner connected, or attachments not used.
- Audit export filtered to `document.` for the period, where you hold the permission.
- For any scanner you connect: the adapter's own test evidence, kept outside this product (**Not Verified** here).

## If something goes wrong

| Symptom | What to do |
|---|---|
| Every upload is held in Production | Expected with no real scanner. Check R-DOC-01 and the Scanner page. Connect a provider (technical, section 6A checklist) or do not use attachments |
| A masked-data batch will not commit in Test because of the scanner | The demonstration scanner has not been accepted. A System Administrator records an acceptance on the Scanner page (reason, scope, expiry) |
| "Failed: needs an administrator" | The scanner repeatedly failed on that file. Check the scanner service, then use **Scan again** (recent sign-in) |
| Files stay Waiting and nothing happens | Check: a scanner is connected; background jobs are on (`Notifications:JobIntervalSeconds` greater than 0); maintenance mode is off; the Operations dashboard shows jobs running |
| A file is quarantined | Do not try to release it. Follow your security policy; ask for a clean replacement |
| The page is forbidden | You do not hold `notifications.administer` for any organisation; ask the Tenant Administrator |
| The page shows the red "skipped scans allowed" alert | A System Administrator must turn `AllowSkippedScan` off |
| A Skipped file is somehow downloadable | `AllowSkippedScan` is on. Turn it off and review what was released while it was on |
