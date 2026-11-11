# JBS IIS integration and deployment: decision plan

Status: **Review and plan only. No application code, workflow, script or configuration was changed.** Reviewed on 2026-11-11 against branch ux/guided-administration (commit 5063d7e before this document) and pull request 1 (head commit e2c88e0). Production approval is external governance and nothing here declares it.

Owner decision applied throughout: **invite-only application access.** Windows Authentication proves the JBS identity. Windows Authentication alone never grants application access. A person must also exist (be invited) in the application and hold explicit effective access. Directory-group-driven onboarding is deferred.

Classification used for every item: **READY TO IMPLEMENT**, **NEEDS JBS INFRASTRUCTURE ANSWER**, **NEEDS DBA ANSWER**, **ALREADY COMPLIANT**, **JUSTIFIED DEVIATION**, **NOT VERIFIED**.

Files that exist only on the pull request branch are named in plain text, not as links: the pull request's review, the deployment guide, the workflows and the scripts. They are not on the product branch yet.

---

## 1. Current branch situation

Checked against the live remote on 2026-11-11, not against older documentation.

| Remote branch | Commit | What it is |
|---|---|---|
| main | 5063d7e | **The current verified product.** It was fast-forwarded from the original single "Initial Files uploaded" commit on 2026-11-11, on the owner's instruction. The brief says main is "historically only the baseline": that was true until then and is superseded |
| ux/guided-administration | 5063d7e | Identical to main |
| claude/stoic-volta-dvszm5 | 5cdf9f7 | The original functional baseline. Untouched; an ancestor of everything else |
| setup-github-deployment | e2c88e0 | Pull request 1's head. **Eight commits ahead of main, none behind**, so its merge base is main itself |

Consequences:

* Pull request 1 is a **clean descendant of the current product**. Product plus IIS work is simply that branch's head; no merge of two diverged lines is needed.
* Pull request 1 targets **main**, and main does contain the current application, so its assumption "main contains the current application" is now true.
* Nothing needs to be done to main or the baseline branch for the integration. Neither was touched by this review.

**Classification: ALREADY COMPLIANT** (ancestry is clean) with the caution in section 2.

## 2. Pull request 1 assessment

| Question | Answer |
|---|---|
| Source and target | setup-github-deployment into main. Open, not draft, mergeable state "unstable" |
| Author and commits | One author (the IT contact), eight commits on 2026-11-11 (deploy pipeline, review document, sign-in decision, upgrade design, DBA-backup alignment, shared service account) |
| Size | 14 files, 1,663 added lines, 0 deleted |
| Application code changed | **No.** No file under src or tests |
| Other files outside deployment | CLAUDE.md gains three lines; a .gitattributes is added (shell scripts LF, PowerShell scripts CRLF) |
| Workflows added | deploy.yml (trigger: push to main, code changes only, or manual), dotnet-spa-build.yml (reusable build), iis-deploy.yml (reusable deploy) |
| Scripts added | Deploy-IisSite, Initialize-IisSite, Invoke-SqlMigrations (unused by this app), Set-IisAlwaysOn, Set-IisCertificate, Set-WebConfigEnvironment (all PowerShell under the scripts/ci folder), and setup-github-environments.sh under scripts/setup |
| Documents added | The deployment guide and the IIS deployment review |
| Existing CI modified | **No.** ci.yml is unchanged and still runs on every push and pull request |
| Environments expected | GitHub environments QA and Production |
| Variables expected | IIS_SITE_NAME, IIS_APP_POOL, IIS_SITE_PATH, IIS_BINDING, IIS_CERT_THUMBPRINT, IIS_APP_POOL_USER, SMOKE_TEST_URL, and a set of APPENV_ variables stamped into web.config (mode, demo switches, data directory, provider, connection-string reference, the connection string itself, recovery mode, auto-migrate) |
| Secrets expected | IIS_APP_POOL_PASSWORD per environment (also used to run migrations). No other secret is set by the script |
| Approvals expected | Production's "required reviewers" on the GitHub environment (a single named reviewer, only if the script is given one) |
| Runners expected | A self-hosted Windows runner labelled iis-qa on the QA app server, and one labelled iis-prod on the Production app server. The guide documents only the QA runner |

### Can it be merged as is? **No.**

| # | Finding | Evidence | Severity |
|---|---|---|---|
| 1 | **Product CI is red on the pull request.** The architecture test "generic platform files carry no business vocabulary" fails: a vendor name appears in comments of iis-deploy.yml (lines 10 and 12). The failing step stopped the job, so the repository checks, documentation checks, tooling tests and supply-chain steps were **skipped and unproven** | CI runs 96 and 97 on the pull request head; the test step ended "Failed 1, Passed 1702" | Blocking |
| 2 | **The repository consistency check would also fail.** Twelve new files are not in the package manifest, and eleven path references in the documents do not resolve (two of those are artefacts of a clean checkout; the rest name things that do not exist, such as a not-yet-written upgrade script, an Admin/Simulation path, a mappings file path and "appsettings.Demo.json") | Run locally on the pull request's tree | Blocking |
| 3 | **Merging to main starts the deploy workflow.** Its first job is a Windows build with the unit tests, which fail on Windows (section 7). If that were fixed, QA would deploy an application nobody can sign into, still defaulting to Demo mode if one variable is missing, with request limits that break uploads | deploy.yml trigger; sections 5, 8, 10 | Blocking until fixed |
| 4 | **The Production gate is not guaranteed.** The environment script adds reviewers only when PROD_REVIEWER is set; without it Production deploys automatically after QA. It also does not stop a person approving their own deployment, and does not restrict which branches may deploy (a manual run can deploy any branch) | setup-github-environments.sh; deploy.yml has workflow_dispatch | High |
| 5 | **One setting does nothing yet.** The script sets a recovery-mode variable the application does not read (the setting does not exist in the product) | RecoveryOptions has no mode | Medium (harmless until built; misleading in review) |
| 6 | **No database migration step exists**, so a deployed application against an older or empty database starts "Starting" and never becomes ready | deploy.yml header and iis-deploy.yml | Expected: designed in the review, not built |

What is good and should be kept: the build-once, promote-same-artifact shape; per-environment settings kept out of the repository; app_offline during the swap; a previous-version snapshot; AlwaysRunning, preload and no idle time-out; Windows Authentication on with Anonymous off and only the health path anonymous; reuse of the existing ci.yml unchanged.

### Recommended branch strategy

Create **jbs/iis-integration** at the current product commit and make pull request 1 land **there**, not on main. All remaining IIS work lands on it. Only after a verified QA run does main move, by explicit approval.

**Classification: READY TO IMPLEMENT** (Git operations in section 3), subject to the owner's go-ahead.

## 3. Branch integration plan

Preferred result: jbs/iis-integration containing the whole current product (Guided Administration, guided custom fields, every migration, every test), the IIS pipeline and the IIS documentation.

Because main equals ux/guided-administration and the pull request branch is main plus eight commits, there are two equivalent routes. Route A keeps IT's review trail and is recommended.

**Route A (recommended): create the integration branch at the product commit, retarget pull request 1, merge it there.**

1. Create the branch on the remote at the product commit: push origin/main to a new branch named jbs/iis-integration.
2. Change pull request 1's base from main to jbs/iis-integration (a base change on the open pull request; no commits move).
3. Fix the two blocking findings (CI vocabulary guard, manifest and path checks) with a small commit on setup-github-deployment, so the pull request is green. These are documentation and comment edits only.
4. Merge pull request 1 into jbs/iis-integration (a merge commit or a fast-forward; both preserve IT's eight commits).
5. All further work (sections 5 to 11) is committed to jbs/iis-integration.
6. After QA verification and explicit approval: fast-forward main to jbs/iis-integration, and only then does deploy.yml's main trigger become live.

**Route B (equivalent, no review trail): fast-forward.** Create jbs/iis-integration directly at the pull request head (origin/setup-github-deployment, which is the product plus eight commits). Close pull request 1 unmerged with a note. This is simpler but loses the pull request as the review record.

Rules that apply to both:

* main and claude/stoic-volta-dvszm5 are **not modified** until approved.
* jbs/iis-integration should be added to ci.yml's coverage (it already runs on every branch).
* deploy.yml must **not** deploy from jbs/iis-integration to Production. Recommended trigger change: QA deploys from jbs/iis-integration and from main; the Production job runs only when the ref is main (or a release tag).
* This document is committed to ux/guided-administration, as instructed; jbs/iis-integration inherits it when created, because it is built from that commit.

**Classification: READY TO IMPLEMENT** (needs only the owner's approval to create the branch and retarget the pull request). Neither was done in this review.

## 4. Reconciliation of the JBS material

Read in full: the standards guide, the IT review, the deployment guide, the gap plan, the compliance review, the upgrade runbook and the IIS README. Where the IT review supersedes an older assumption:

| Topic | Older assumption | Superseded by | Which wins |
|---|---|---|---|
| Sign-in | Gap plan P0-1: Windows sign-in through the Negotiate package, effort L; I-12 asked whether Entra OIDC would do | IT review section 1a: IIS Windows Authentication as in JBS AI Initiatives; Entra not used; effort M | **IT review.** One correction, to be confirmed in the Windows test: for in-process IIS the scheme constant is the server one (IISServerDefaults), not the out-of-process IISDefaults the review names |
| Anonymous access | Compliance doc I-05 left open | Anonymous off site-wide; only the warm-up path /health/live anonymous | IT review |
| Pipeline | Gap plan P1-2: Azure DevOps YAML | GitHub Actions reusable workflows on self-hosted IIS runners | IT review |
| Migrations | Gap plan P1-5: scripted SQL for the DBA | Pipeline drives the application's own upgrade procedure | IT review (SQL scripts remain available by the application's script command if the DBA insists) |
| Backups | Runbook and IIS README: the application takes and verifies its own backup; a backup share is configured | DBA and Infrastructure own backups; the application only records an external recovery point | **IT review**; needs code (section 12) |
| Migration identity | IIS README: runtime and migration identities separate | QA: one account (the shared service account) runs and migrates | IT review for QA; **Production still to be decided** (DBA question) |
| Connection string | IIS README: a secret file outside the web root | An environment variable referenced as env:ECP_SQL_CONNECTION, no password (integrated authentication) | IT review; compliant with the application's secret-reference rule |
| Branch | Brief: main is the baseline only | main already holds the product | Current remote state |
| Request limits | Gap plan P0-2: 100 MB limit "mirrors the settings" | IT review G-4: the 30 MB in-process default blocks document uploads and the 100 MB limit blocks the 2 GB migration upload | **IT review, and verified** (section 8D) |
| App pool recycle | Gap plan P0-3 | AlwaysRunning, preload, idle off by script | IT review; periodic recycle still to be set |
| Test status | Compliance doc: 979 unit tests pass | 977 of 979 on Windows | Both true: Linux 979, Windows 977 |

Remaining contradictions to carry: the IIS README still says "web.config in the repository is the one to use" while publish generates its own (section 8C and 10); the deployment guide says deploy triggers on main, which this plan changes.

## 5. Authentication design (invite-only Windows sign-in)

**Can the current Identity module support it cleanly?** Yes, with **one new provider adapter** and one small additive change. The module already has everything else: invite-only accounts that link by (issuer, subject), an account lifecycle (invited, active, disabled), signed session issuance, antiforgery on every sign-in post, audit, a rate limit on sign-in, and a refusal path that renders a plain page. The adapter replaces "read an OpenID token" with "read the Windows identity IIS already verified".

**Classification: READY TO IMPLEMENT** except the items marked as needing a JBS answer.

### The flow

1. IIS authenticates the request (Kerberos, with NTLM fallback). Anonymous is off except /health/live.
2. A person with no application session is redirected by the existing fallback policy to the sign-in page.
3. The sign-in page shows a Windows panel: "Continue as AMH\name" (an explicit button, a normal post with the antiforgery token; automatic continuation can be added later with an external script file, because the content-security policy forbids inline script).
4. The post handler asks IIS's authentication scheme for the Windows identity (in-process: IISServerDefaults). It requires an authenticated, non-anonymous, non-guest domain identity. It reads: the user's SID, the account name, the UPN claim if present.
5. It builds the existing login record: provider key "windows", issuer = the account-domain SID prefix (stable across renames and server moves), subject = the user's SID, **groups = none** (so the existing group-mapping engine cannot grant anything), email trusted only if section 5.2 option 1 is chosen.
6. The existing resolution runs: link by (issuer, subject) if known; otherwise match an **invited, not-yet-linked** account (section 5.2); otherwise refuse.
7. Active status check: invited becomes active on first link; disabled is refused.
8. **Effective access check (new, explicit):** after the account resolves, the existing effective-access report is read for the person. If they hold **no active role assignment that grants any permission**, they are refused with the "set up, but no access granted yet" page. This implements "must also have explicit effective access" and prevents an invited-but-unassigned person from reaching an empty application.
9. Success issues the existing signed session. Every later request is decided by the unchanged evaluator (role, scope, category, classification, ownership, record locks).

### Rules the design guarantees

| Requirement | How it is met | Classification |
|---|---|---|
| Windows-authenticated but uninvited person is refused | Step 6 returns the existing not_invited denial | READY TO IMPLEMENT |
| Refusal is plain language, never a 500 | A 403 page rendered by the application, naming the Windows account shown, a contact line from configuration, and the correlation reference. No cookie is issued. Tested against a forced exception path as well | READY TO IMPLEMENT |
| No permission comes from Windows Authentication alone | Windows sign-in only proves identity; roles come only from assignments (invitation, setup, People and Access). Group list passed empty in this phase | READY TO IMPLEMENT |
| Existing role, scope, category, classification security stays authoritative | Untouched: the evaluator and every service check are unchanged | ALREADY COMPLIANT |
| No automatic Tenant or Organisation Administrator | No code path grants a role at sign-in; the first administrator comes only from the set-up wizard (below) | READY TO IMPLEMENT |
| Demo and Test identity remains for non-Production | Unchanged. Production still refuses it at start-up (R-ID-01) and in the service | ALREADY COMPLIANT |
| Audit successful linking | Existing user.activated and user.signed_in events gain the provider and authentication type | READY TO IMPLEMENT |
| Audit refused identities | **New** audit event for each refusal with a reason code (not invited, disabled, no access, not a domain identity), the provider, the correlation id, and the Windows account name and SID. Today a refused sign-in is only a log warning, never an audit event | READY TO IMPLEMENT |
| Preserve antiforgery and secure cookies | Sign-in is a post with the existing token; the session cookie stays HttpOnly, SameSite Lax, Secure in Production (the host requires HTTPS in Production, which an IIS TLS binding satisfies) | READY TO IMPLEMENT |

### 5.1 First administrator and the set-up wizard

The wizard's identity stage offers only Entra and generic OpenID Connect in Production, and its administrator stage invites by **email** and expects the provider to assert that email. A Windows identity asserts no verified email, so the first administrator **could not link and nobody could sign in**. Changes needed:

* Add a Windows provider kind to the identity stage (no secrets or authority; just a display name and a contact line).
* In the administrator stage, when the provider is Windows, capture the person's **Windows account** (and offer "link me": the wizard runs on the server and the person completing it is already Windows-authenticated, so their own SID can be linked directly through the existing trusted-creation path).
* Keep the existing grants (tenant administrator, optionally installation administrator). No other role is added.

**READY TO IMPLEMENT.**

### 5.2 How an invited person is recognised (one decision, one small change)

The invitation today carries a name and an email. Windows gives a SID, an account name and (usually) a UPN. Two options:

| Option | Behaviour | Cost | Needs |
|---|---|---|---|
| 1. Invitation email must equal the UPN | The UPN claim is trusted because the domain asserted it; no schema change | None | **NEEDS JBS INFRASTRUCTURE ANSWER:** is every user's UPN identical to their email address? |
| 2. Invitation names the Windows account | One additive column (the expected Windows account, domain\name or UPN) on the user record, filled in by the People and Access wizard; matched at first sign-in, then replaced by the SID link | One additive migration in the Identity module (manifest regenerated); wizard field | None (works whatever the UPN is) |

**Recommendation: option 2**, because it does not depend on JBS's email convention. Either way the match only ever **links an existing invited account**; it never creates one.

### 5.3 Controls added because Windows has no sign-out or re-prompt

| Control | Why | Classification |
|---|---|---|
| On every request, compare the IIS-authenticated SID with the session's linked subject; a mismatch ends the session | A browser profile shared between two people, or a cookie moved between machines, must not carry one person's session into another's identity | READY TO IMPLEMENT |
| Sign-out ends the cookie and shows a "signed out" page with an explicit "Sign in again" button, not an automatic re-sign-in | IIS will re-authenticate silently, so an automatic loop would make sign-out meaningless | READY TO IMPLEMENT |
| Step-up ("recent sign-in" for publishing configuration, within 10 minutes) cannot force a new password prompt under IIS Windows Authentication | Becomes a "confirm it is you" page that re-checks the Windows identity equals the session user and requires an explicit action. This is a weaker proof of presence than a password re-prompt | **NEEDS JBS INFRASTRUCTURE ANSWER** (accept as a justified deviation, or require another control) |
| Readiness check: if the Windows provider is registered but IIS reports Windows Authentication is not enabled on the site, readiness is Unhealthy | Catches a misconfigured site before anyone tries to sign in | READY TO IMPLEMENT (the exact IIS signal is to be confirmed in the Windows test) |
| Production guard rule: the Windows provider requires that the Demo identity is off (already refused) | Keeps the Production refusals complete | ALREADY COMPLIANT |
| Reject a request whose Windows identity is anonymous, guest, a local machine account, or the application pool's own account | Prevents the pool identity or a local account being mistaken for a person | READY TO IMPLEMENT |

### 5.4 Deferred (by decision)

Directory-group onboarding: creating accounts from group membership, mapping directory group SIDs to roles. The group list is passed empty so the existing mapping engine cannot fire, and a test pins that. **NOT BUILT (deferred)**.

## 6. IIS deployment requirements

| Requirement | Required setting | Source | Classification |
|---|---|---|---|
| ASP.NET Core Module V2 | Installed with the .NET 10 Hosting Bundle; checked by Initialize-IisSite | Pipeline script | READY (checked); version on server: NEEDS JBS INFRASTRUCTURE ANSWER |
| Hosting model | In-process (the published web.config says so) | Verified by publishing | ALREADY COMPLIANT |
| .NET 10 Hosting Bundle | Installed on QA (reported by IT) and Production | IT review | NEEDS JBS INFRASTRUCTURE ANSWER (Production) |
| App pool | No Managed Code, integrated pipeline | Initialize-IisSite | ALREADY COMPLIANT in the script |
| Identity | A domain service account. **The same account as JBS AI Initiatives in QA** (a decision), Production TBD | IT review | JUSTIFIED DEVIATION for QA (shared identity widens the blast radius and shares the per-user data-protection scope); NEEDS JBS INFRASTRUCTURE ANSWER for Production |
| Start mode | AlwaysRunning, idle time-out 0 | Set-IisAlwaysOn | ALREADY COMPLIANT in the script |
| Preload and warm-up | Preload enabled, Application Initialization warming /health/live; requires the Application Initialization feature | Set-IisAlwaysOn | NOT VERIFIED (the feature must be present; the script only warns) |
| Recycle | Default periodic restart (29 hours) is not changed by any script. Recommended: a fixed off-hours schedule, a shutdown time limit longer than a job cycle, and recycle only when idle | Not in scripts | READY TO IMPLEMENT (add to Set-IisAlwaysOn); the schedule NEEDS JBS INFRASTRUCTURE ANSWER |
| Windows Authentication | On for the site; Anonymous off; Anonymous on only for /health/live | Initialize-IisSite and Set-IisAlwaysOn | ALREADY COMPLIANT in the scripts; applied only when the site is first created, not corrected later |
| SPN | HTTP/host registered to the pool account for Kerberos | Deployment guide | NEEDS JBS INFRASTRUCTURE ANSWER |
| TLS | Certificate bound by thumbprint (IT installs); the application itself requires HTTPS in Production | Set-IisCertificate | NEEDS JBS INFRASTRUCTURE ANSWER (thumbprints are placeholders) |
| Forwarded headers | **Not needed**: IIS terminates TLS itself, in-process. Needed only if a load balancer or reverse proxy is added in front | Application guards | ALREADY COMPLIANT (and a check, R-NET-01, exists) |
| Environment variables | APPENV_ variables stamped into the published web.config (QA and Production, no secret except IIS_APP_POOL_PASSWORD) | Pipeline | ALREADY COMPLIANT; the connection string contains no password |
| web.config | **Not what the repository's IIS file says.** Publishing generates its own web.config (verified) with no environment variables, no request limits and no location blocks. The pipeline adds only the environment variables | Verified by publishing | READY TO IMPLEMENT: add a web.config to the Host project so publish uses it (limits, temp path, documented), and keep the pipeline stamping on top |
| Filesystem permissions | Pool account: Read on the site folder; Modify on the data root (below); nothing else writable. The script grants Modify only to a "logs" folder inside the site | Pipeline | READY TO IMPLEMENT (extend; data root is outside the site) |

### Folders (data root example: the drive path IT chose for each environment)

| Folder | Holds | Permission for the pool account | Backed up by Infrastructure | Notes |
|---|---|---|---|---|
| Site folder | The deployed application | Read | Not needed (rebuilt) | Replaced by every deploy; nothing durable may live here |
| Data root, Data\keyring | Authentication and antiforgery keys | Modify; **no one else** | **Yes** | Outside the site, so it survives deploys. See 8C |
| Data root, Data\documents | Contract documents | Modify | **Yes (required)** | Must be backed up in step with the database; a restore of one without the other loses documents |
| Data root, Data\packs and Data\bootstrap | Installed solution packs, set-up state | Modify | Yes | Set-up state is protected by the key ring |
| Data root, Data\outbox | Pending outbound messages | Modify | Yes | |
| Logs folder (section 9) | Rolling application log | Modify | Infrastructure's retention | Outside the site, so deploys do not touch it |
| Temp folder for uploads (8D) | Buffered upload bodies | Modify | No | On the data volume, not the system drive |

**Do NOT point the data root inside the site folder.** The deploy mirrors the package over the site (robocopy with mirror), which would delete anything not in the package except the folders it is told to preserve.

The package must also never contain a local data folder: the Host project's own data folder (git-ignored, created by local runs) is copied by publish if it exists. A pipeline built from a clean checkout is safe, but a developer-built package could ship key material. Add a publish exclusion. **READY TO IMPLEMENT.**

## 7. The two Windows test failures (verified)

Both are **test-only defects**, not product defects, apart from one small product hardening noted.

| Test | Root cause | Fix | Classification |
|---|---|---|---|
| Several_problems_are_all_reported_together (MigrationAuditAndGuardTests) | The test passes the data directory "/tmp/z" and expects five Production-guard violations. The guard's "ephemeral storage" rule recognises the operating system's temp folder and the literal prefix "/tmp/". On Windows, "/tmp/z" resolves to the system drive's tmp folder, which is neither, so only four violations are found (demo identity, provider, inline secret, demo data) | In the test, build the path from the operating system's temp folder (so it is ephemeral on every platform). Optional hardening in the product rule: compare case-insensitively on Windows, since Windows paths are case-insensitive | READY TO IMPLEMENT |
| A_symbolic_link_is_refused (PackFormatTests) | The test marks a zip entry as a symbolic link through the Unix mode bits of the entry's external attributes. The reader honours those bits only when the archive says it was made on a Unix host. The Windows zip library writes a different host marker, so the reader (correctly) ignores the bits and the entry is accepted | In the test, patch the central-directory "version made by" of the entry to the Unix host marker after building the archive, exactly as the neighbouring "encrypted entry" test already patches its header bytes, so the test is host-independent. Optional product hardening: also refuse the DOS reparse-point attribute | READY TO IMPLEMENT |

Neither affects the product's behaviour on a Unix-built pack, which is the only place a symlink can exist. After the fix the Windows unit suite should be 979 of 979 and the build job in deploy.yml can proceed.

## 8. Configuration, data protection, upload limits, recovery

### 8A. Safe defaults (verified)

| Check | Result |
|---|---|
| Does the shipped base appsettings still permit Demo? | **Yes**: Mode is Demo, demo tenant resolution and demo seeding are on |
| SQLite? | **Yes**: Provider is Sqlite with a database file under the site |
| Auto-migrate? | **Yes**: auto-migrate is on (the code applies it only outside Production) |
| What stops this on IIS? | The pipeline stamps Production values; if one variable is missing, a Demo/SQLite site runs, with its database inside the site folder that every deploy mirrors over |

Required change: make the base file **safe by default** and put the demonstration values where they belong.

| Setting | Base appsettings (new) | Where demo/dev values go |
|---|---|---|
| Mode | Production | A Development settings file used by the local launch profile; explicit environment settings in the test harness and the Docker demo (both already set them explicitly) |
| Demo tenant resolution, demo seed | off | Same |
| Provider, connection string, auto-migrate | SQL Server, empty, off | Same |
| Every other setting the code reads | present, empty (discoverable) | n/a |

Verified safe: the test harness and the Docker demo set their own values, and the local launch profile uses the Development environment, so moving the demo values into a Development file does not change them. The installation guide and the Windows quick start already tell the operator to set the mode explicitly (the guide even says the shipped default file says Demo), so they need only a wording update. **READY TO IMPLEMENT** (with the test and the compose file left unchanged).

### 8B. Settings the pipeline sets that need correction

* The connection string variable says TrustServerCertificate=True; the DBA policy is unknown and the production guidance in the product says False. **NEEDS DBA ANSWER.**
* The recovery mode variable is ignored until section 12 is built.

### 8C. Data protection (verified)

* The key ring is stored under the data root (Data\keyring), outside the site, and protected with **per-user Windows data protection** (the current-user scope), because the code calls the default form on Windows.
* **Survives:** an IIS worker restart, an application pool recycle, a deploy (the folder is outside the site) and a server restart, **as long as** the pool runs as the same account and that account's user profile is loaded.
* **Does not survive:** a change of the pool account, deletion of the profile, or moving the folder to another server; and with no loaded profile the application can fail to read or create keys. Effect: everyone is signed out and forms in progress fail; stored data is not lost. The set-up state is protected with the same ring.
* IIS app pools normally load the profile for a custom account, but the script does not set it, so this is **NOT VERIFIED** on JBS servers.

Required design:

| Item | Setting | Classification |
|---|---|---|
| Key protection | Machine-scope protection (any account on that server can read), **or** a certificate whose private key is readable by the pool account (preferred if keys must move between servers) | READY TO IMPLEMENT (machine scope); certificate NEEDS JBS INFRASTRUCTURE ANSWER |
| Profile | Set the pool to load the user profile in Initialize-IisSite as a belt-and-braces measure | READY TO IMPLEMENT |
| Location | Data root, Data\keyring, outside the site | ALREADY COMPLIANT |
| Permissions | Modify for the pool account only; inheritance removed; local administrators and the backup operator read-only | READY TO IMPLEMENT |
| Backup | Included in Infrastructure's backup of the data root. A restore onto a different server signs everyone out once; acceptable | NEEDS JBS INFRASTRUCTURE ANSWER (backup scope) |
| Shared account in QA | The QA pool account is shared with JBS AI Initiatives; the two applications use different folders and different application names, so keys do not collide | JUSTIFIED DEVIATION |

### 8D. Upload limits (verified numbers)

Three independent limits apply, from outermost to innermost, and the **smallest wins**:

| Layer | Default today | Notes |
|---|---|---|
| IIS request filtering (maxAllowedContentLength) | 30,000,000 bytes in a generated web.config (the review's "about 28.6 MB"); 104,857,600 (100 MB) in the repository's IIS file | Can be overridden per URL with location blocks; the maximum value is just under 4 GB |
| ASP.NET Core in-process server (MaxRequestBodySize) | 30,000,000 bytes | Raised per endpoint by RequestSizeLimit |
| Multipart form limit (MultipartBodyLengthLimit) | 134,217,728 bytes (128 MB) | Raised per endpoint by RequestFormLimits |
| Application's own limit | Documents 104,857,600 (100 MB, DocumentsOptions); packs 8 MB (PackOptions); migration upload 2,147,483,648 (2 GB, migration options) | Enforced by the service while streaming |

What each upload endpoint actually has:

| Endpoint | Application limit | RequestSizeLimit | RequestFormLimits | Result today |
|---|---|---|---|---|
| Pack upload page | 8 MB | 9 MB | none (128 MB default) | Works |
| Migration batch uploads (contracts, manifest, archive) | 2 GB | 2 GB | 2 GB | Works in the application; **blocked by IIS** at 30 MB or 100 MB |
| Add document (contract) | 100 MB | **none** | **none** | **Fails above about 28.6 MB** on IIS and also on the Docker host (default 30 MB) |
| Legal review attachment | 100 MB | none | none | Same |
| Variation document | 100 MB | none | none | Same |
| Executed-copy upload (shared page base) | 100 MB | none | none | Same |
| Field-map and list import in migration profiles | small files | none | none | Works (small) |

So the **supported maximum is 100 MB for documents and 2 GB for migration archives**, and documents are not actually supported above about 28 MB today on any host. That is a product defect for every deployment, not only IIS.

Required design (endpoint-specific, not a large global limit):

| Layer | Setting | Classification |
|---|---|---|
| Application | A single setting-driven limit for the four document-upload pages: request size and form size equal to the document limit plus 1 MB for multipart overhead (105,906,176 bytes), applied through one page convention so a new upload page cannot forget it. Migration batch keeps its 2 GB value, plus 16 MB overhead (2,164,260,864) so a file exactly at the limit is accepted | READY TO IMPLEMENT |
| IIS global | Leave at the default (30,000,000). Every other page remains small | READY TO IMPLEMENT |
| IIS per path | location blocks in the project's web.config: the document-upload paths at 105,906,176; the migration batch path at 2,164,260,864; the pack upload at the default | READY TO IMPLEMENT (the generated web.config cannot carry them; the project needs its own) |
| Temp folder | Buffered uploads go to the process temp folder. A 2 GB archive needs more than 2 GB free. Set TMP and TEMP in web.config to a folder on the data volume, with Modify for the pool account. The system drive's temp is the wrong place | READY TO IMPLEMENT; the drive NEEDS JBS INFRASTRUCTURE ANSWER |
| Time-outs | A 2 GB upload over the network takes minutes. The IIS connection and the ANCM in-process request time-out behaviour must be tested; the standard's requestTimeout is documented for out-of-process hosting, so whether it applies in-process is to be confirmed | NOT VERIFIED (test on Windows) |
| Test | A document test just below and just above the limit; a migration archive test near 2 GB on a Windows test server | READY TO IMPLEMENT |

### 8E. External recovery point (DBA-managed backups)

Goal: the application's upgrade needs a **recovery point**; at JBS the DBA's normal backups are that recovery point. The application must record what was declared and by whom, **never claim it took or verified the backup**, and refuse to proceed when the declaration is stale or wrong.

How it works today: the upgrade requires the name of a backup the application made and verified itself, plus a configured backup folder; the pre-flight checks UPF-020 (verified backup), UPF-021 (age), UPF-022 (same installation and environment), UPF-023 (changes since the backup) and UPF-024 (backup matches the schema being upgraded from).

Proposed **External mode** (an explicit installation setting; off by default; the application's own backup commands are refused while it is on):

| Field | Meaning | Rule |
|---|---|---|
| Recovery point identifier | A reference that lets the DBA find the restore point: the backup set or job reference, or the pipeline's run identifier and commit | Required, up to 100 characters, no spaces issues; unique per upgrade |
| Source | **ExternalDba** (the DBA team's backups) or ExternalInfrastructure | Required. The application never writes "ApplicationBackup" for an external point |
| Environment | The environment name (QA, Production) and the host mode | Must equal the running installation's; a Production point is refused on QA and vice versa |
| Database identity | Server and database name (no credentials) | Must equal the connection's server and database |
| Declared by | The operator identity (the command line records the operating-system account of the pipeline runner) plus the change reference (run URL and commit) | Required, taken from the environment, not typed |
| Verifier and method | Who or what checked it: **NotChecked (trusted process)**, DbaAttested (a named DBA), or MsdbChecked (the application read the backup history read-only) | Recorded exactly as it happened. "Verified" is shown **only** for DbaAttested or MsdbChecked; the first deployment will say "Declared, not checked by this application", matching IT's decision to trust the DBA backups |
| Restore-to time (UTC) | The moment the DBA can restore to: the declaration time, taken with maintenance mode already on so nothing changes afterwards | Must not be in the future; must be within the allowed age |
| Declared time (UTC) | When the declaration was made | Taken from the clock |
| Schema and platform version | The platform version and migration heads of the database **at declaration** (read by the application itself) | Must still be identical at apply time; a different value means the database moved after the point was declared, and the declaration is invalid. It also tells the restorer which application version to redeploy |
| Manifest hash | The upgrade manifest the point is bound to | Bound: a point cannot be reused for a different upgrade |
| Reason | Free text and change reference | Required, at least 10 characters |

Validity and staleness:

* **Single use**: bound to one upgrade (manifest hash, database identity, schema version); marked Used on apply and Superseded after success. It cannot be reused.
* **Age**: the restore-to time must be within a configured limit (recommended default 2 hours) of the apply; older is **Blocked**, not a warning, because the declaration is made inside the window.
* **Data-loss window** (UPF-023 equivalent): changes recorded after the restore-to time are counted; any count above zero at apply time is a warning that the restore would discard them.
* **Mismatch** of environment, database, schema version or manifest hash: Blocked.
* Optional later: a read-only check of the backup history table that blocks the upgrade if the last full backup (and log backups, for point-in-time restore) is older than a limit. Needs a read right on that table (**NEEDS DBA ANSWER**) and a recovery model that supports point-in-time restore (**NEEDS DBA ANSWER**).

Audit and evidence:

* New audit events for the declaration, for its use in an apply, and for each refusal. Each carries the identifier, source, environment, database identity, verification method, declared-by, schema version, manifest hash and reason.
* The upgrade evidence and the Upgrade History page show an "External recovery point" block and the sentence "This application did not take or verify this backup."
* If the upgrade fails, the message says "Ask the DBA to restore to the stated time; redeploy the previous release folder", not "restore backup X".

**Classification: READY TO IMPLEMENT** (effort small to medium, in the recovery module: pre-flight, upgrade service, command line, evidence, history page, tests). Whether the pipeline later checks the DBA's backups: **NEEDS DBA ANSWER**.

## 9. Logging (minimum compliant change using Serilog)

Compared with the standard (section 5 of the JBS guide, a guide section whose binding status JBS has not confirmed; the logging library choice is in section 1, which is agreed): the application uses the framework logging with its default providers, structured message templates, a correlation id, an in-memory redacted buffer for the support bundle, and no durable sink. Under IIS in-process with console output off, only what the framework's other default providers keep survives, which is expected to be the Windows event log at Warning and above. **Information-level operational logging is lost.**

**Classification: NON-COMPLIANT today; READY TO IMPLEMENT** apart from the items marked as needing a JBS answer.

### Required for JBS deployment (minimum slice)

| Item | Design | Class |
|---|---|---|
| Serilog as the provider | Serilog configured in code at start-up with a bootstrap logger for start-up failures; the existing ILogger<T> calls and message templates do not change | READY TO IMPLEMENT |
| Structured logging only | Keep message templates; add a build check that rejects an interpolated log message | READY TO IMPLEMENT |
| Durable IIS sink | A rolling file under the data root's Logs folder (outside the site), daily files, size cap, 30-day retention default (setting), readable text plus a compact JSON option | READY TO IMPLEMENT (retention NEEDS JBS INFRASTRUCTURE ANSWER) |
| Correlation id | Already created per request and echoed as X-Correlation-Id; add it to every event through the log context | READY TO IMPLEMENT |
| Process and thread | Standard process-id and thread-id enrichers | READY TO IMPLEMENT |
| Application context | Application name (a setting, default the product name), application version (the informational version), environment (ASPNETCORE_ENVIRONMENT) and the platform mode | READY TO IMPLEMENT |
| Site | A setting (default the machine name). "SiteCode" has no meaning for a multi-tenant product; JBS to say what it expects | NEEDS JBS INFRASTRUCTURE ANSWER |
| Database identifier | Server and database name only, computed once from the connection string with the credentials removed | READY TO IMPLEMENT |
| Login | The **application user id** (a GUID) from the existing actor context, never the display name. Adding the Windows account name is personal data and is a JBS privacy choice | READY TO IMPLEMENT (user id); NEEDS JBS INFRASTRUCTURE ANSWER (account name) |
| Tenant | The tenant id (a GUID, not content) | READY TO IMPLEMENT |
| Redaction | Every sink goes through the existing redactor (connection strings, keys, bearer tokens, secrets) applied to rendered messages and exception text; no request or response bodies, no query strings, no document names or contract text; destructuring of domain objects is off | READY TO IMPLEMENT |
| Support bundle | Keep working: the in-memory buffer and bundle read the same log stream | READY TO IMPLEMENT |
| Tests | Canary-secret tests for every sink, as the existing redaction tests do; a test that proves no request body or query string is logged | READY TO IMPLEMENT |

### Requires infrastructure confirmation (second slice)

| Item | Design | Class |
|---|---|---|
| Application Insights | Information and above, connection string from the environment or a secret reference (never committed), with a telemetry processor that drops query strings, bodies and exception messages that carry values; dependency telemetry without SQL text | NEEDS JBS INFRASTRUCTURE ANSWER (resource, residency, what may be sent) |
| Fatal-only email | A sink that emails only Fatal events, batched and rate-limited, using JBS's mail route | NEEDS JBS INFRASTRUCTURE ANSWER (route, recipients) |
| Windows event log | Optional Error-and-above mirror for the server team | NEEDS JBS INFRASTRUCTURE ANSWER |

**Confidential content is never logged**: the platform rule is that live confidential data and secrets stay out of logs, and the redaction, the "no bodies and no query strings" rule and the user-id-not-name choice implement it.

## 10. SQL Server and DBA readiness

### Every SQL Server assumption found in the product

| Assumption | Where | Why it matters |
|---|---|---|
| SQL Server 2016 or later (row-level security, session context); tested on 2022 only | Migrations, connection setup | Compatibility level must support security policies |
| One database per installation; schemas per module (about a dozen), the security schema, per-schema migration history tables | Module contexts | The migration identity must be able to create schemas |
| Row-level security: tenant predicate functions and one security policy per table, driven by session context set before every command | Persistence | A runtime login that can alter or drop policies defeats the isolation; **the tested login was a full administrator and no least-privilege test exists** |
| A database role named ecp_runtime is created by the first migration and granted data access on each schema; nothing adds a login to it | Persistence | The runtime account must be a member of that role for least privilege, or it is given broader rights (the QA decision gives it owner rights) |
| Row version columns for optimistic concurrency, filtered indexes, many text comparisons | Models | Collation and case sensitivity: tested on the default case-insensitive collation only |
| Application locks (an exclusive lock per tenant to serialise the audit chain), the session-context procedure | Audit, persistence | Runtime needs permission to run them |
| Catalogue and server-property reads (version, edition, permissions, free storage) | Upgrade pre-flight, health | Pre-flight reports "not verified" without view-server-state |
| Backup, restore, integrity check, database-level maintenance statements | Recovery module | Needed only if the application's own backup feature is used; **not used at JBS** (decision) |
| Connection string with Encrypt on; certificate trust policy not asserted | Configuration | The pipeline sets TrustServerCertificate=True; the product guidance says False |
| Migrations applied by an explicit command or the upgrade procedure; Production refuses an automatic migration | Host | Who runs it and under which account |
| No SQL scripts for review exist | Upgrade tooling | The application can print the SQL of a migration on request |

### DBA questions still required (send these)

1. Which SQL Server version, edition and **compatibility level** will each environment's database use? (The product is tested on 2022.)
2. What **collation** is the standard for these databases? (Tests ran case-insensitive.)
3. What are the **database names** to use on the QA and Production database servers, and may each hold only this application?
4. Which **runtime service account** will the application use in each environment, and will it be a member of a least-privilege role (the product creates a role named ecp_runtime) or hold owner rights?
5. Which **migration identity** will run schema changes in each environment: the same account as the runtime (accepted for QA), or a separate one for Production? Needs rights to create schemas, tables, functions and **security policies**.
6. Which **backup identity** applies, if any? (Not needed by the application at JBS.)
7. Will the application use **integrated (Windows) authentication** in every environment, and is a SQL login ever acceptable?
8. What are the **Encrypt** and **TrustServerCertificate** policies? May the server certificate be trusted (the pipeline currently trusts it)?
9. **Who executes migrations** in Production, and in what form: the application's own upgrade procedure driven by the pipeline under approval, or reviewed SQL scripts run by the DBA?
10. **Who owns backups** and restores, and what are the retention and **recovery model**? Is point-in-time restore available (full recovery model with log backups)?
11. Will the DBA team provide a **recovery-point reference** for each upgrade (a backup set or job reference), and may the application read the backup history table read-only to warn when the last backup is old?
12. What **maintenance** (index, statistics, integrity checks) runs, and in which window? Any conflict with the application's maintenance mode?
13. Are application locks, the session-context procedure and catalogue reads permitted for the runtime account?
14. Is the database in the same instance family as JBS AI Initiatives, and what isolation is required between the two applications' databases and the shared account?

### Can implement now versus must wait

| Can implement now | Waits for a DBA answer |
|---|---|
| External recovery-point mode (section 8E) | The decision to check the backup history; the recovery model |
| A SQL Server test that runs the application as a login in the least-privilege role only (no schema rights), proving the runtime needs nothing more | Whether Production uses that role (question 4) |
| Documentation of the role and the grants | Final account names |
| Scripted preview of migrations through the existing script command | Whether reviewed scripts are required (question 9) |
| Pipeline setting for Encrypt on | The certificate trust policy (question 8) |
| Collation sensitivity test on a case-sensitive database | The standard collation (question 2) |

## 11. Pipeline review

Reviewed in full: deploy.yml, dotnet-spa-build.yml, iis-deploy.yml and the six scripts. Nothing was run.

| Area | Finding | Class |
|---|---|---|
| QA trigger | Push to main when files other than documents change, or a manual run. A manual run can pick **any branch** | Fix: restrict by environment branch policy and a branch check in the workflow. READY TO IMPLEMENT |
| Production gate | A GitHub environment with required reviewers. Created only if PROD_REVIEWER is set; no prevention of self-approval; no restriction to protected branches; whether the repository's plan allows these rules for a private repository is not known | NEEDS JBS INFRASTRUCTURE ANSWER (plan) and fix in the setup script. NOT VERIFIED |
| Same artifact | **Yes.** The build job uploads one artifact named app; both deploys download it by name from the same run. It is run-scoped and not overwritable. The web.config differs per environment (stamped at deploy) | ALREADY COMPLIANT. Improve: record the artifact's checksum and the commit in each deploy log so QA and Production can be shown identical. READY TO IMPLEMENT |
| Product CI gate | The deploy workflow builds and runs only the unit tests. It does **not** wait for the full CI (architecture, API, integration, SQL Server, upgrade rehearsal). A commit that fails those can still reach QA | Fix: require the full CI on the commit (branch protection requiring its checks) before deploy. READY TO IMPLEMENT |
| Secrets | Only the pool password is expected. The reusable workflow passes **every** secret as a JSON string to the deploy step so APPENV_ ones can be picked out; with "inherit" this includes repository and organisation secrets | JUSTIFIED DEVIATION if kept (reference pattern); safer: pass only the named secrets. READY TO IMPLEMENT |
| Action versions | Third-party actions pinned by tag, at versions newer than the existing ci.yml uses; existence and trust not checked here | NOT VERIFIED |
| Service accounts | QA pool and migration account is the **shared** JBS AI account; the runner service account needs rights on the site folder, local administrator for first creation, and the Secondary Logon service for running migrations as another account | JUSTIFIED DEVIATION (QA); NEEDS JBS INFRASTRUCTURE ANSWER (Production) |
| Deployment paths | Site under the inetpub folder; data root per environment outside it; a ".previous" snapshot beside the site | ALREADY COMPLIANT; but **Production data root and site path are on different drives from QA** (per the guide) so the script must be given both |
| app_offline and maintenance | The site is taken offline with an offline page before files change; the application's own maintenance mode (a database flag) is a separate thing used by the upgrade procedure | ALREADY COMPLIANT. Gap: the offline page is removed in a "finally" block even when the copy failed, bringing a half-updated site back online |
| Failure handling | A failed copy throws but still brings the site online. The smoke test checks only that /health/live answers below 500, so a site with no database passes | Fix: do not remove the offline page on failure; restore the snapshot; smoke-test /health/ready as the runner's identity. READY TO IMPLEMENT |
| Migration ordering | **Not built.** The designed order is right: offline, declare the recovery point, pre-flight, apply from the new build in a staging folder, swap files, smoke test | READY TO IMPLEMENT (after section 8E) |
| Rollback | Manual: robocopy the previous snapshot back (a single snapshot, overwritten each deploy; no keep-last-N). Database: a DBA point-in-time restore plus Infrastructure's restore of the data root. No automatic restore | JUSTIFIED DEVIATION (decided); keep-last-N snapshots READY TO IMPLEMENT |
| Logging | Pipeline output only; settings values never echoed (names only); no application log or pre-flight evidence uploaded | Fix: upload the pre-flight and upgrade evidence. READY TO IMPLEMENT |
| Artefact retention | 30 days for the app and test results | NEEDS JBS INFRASTRUCTURE ANSWER (retention) |
| Runners | One label per environment; the Production runner is not described in the guide | NEEDS JBS INFRASTRUCTURE ANSWER |
| Pack signing | Production requires signed packs and a trusted key; the build produces unsigned packs and the pipeline has no signing or trust step | **Gap.** Decide who signs and how the trust file reaches the server. NEEDS JBS INFRASTRUCTURE ANSWER |
| Windows build | The Windows build job fails today (section 7) | READY TO IMPLEMENT |

### The environment script: setup-github-environments.sh

| Question | Answer |
|---|---|
| What it creates | The QA and Production GitHub environments, and variables on each: the IIS site, pool, path, binding, certificate thumbprint ("UPDATE ME"), pool user, smoke-test URL, and the application settings (mode, demo off, data directory, SQL Server, connection-string reference and the connection string itself, recovery mode, auto-migrate off). It sets **no secrets** (it prints a reminder for the pool password) |
| Permissions needed | A GitHub command-line login with **admin** on the repository. The repository itself is the default target |
| Safe to run as written? | **No.** (1) Re-running overwrites every value, so a thumbprint or account later entered by IT would be replaced by "UPDATE ME". (2) Production gets reviewers only if PROD_REVIEWER is set; no self-review prevention, no branch restriction. (3) The recovery-mode variable is not read by the product yet. (4) The connection string carries TrustServerCertificate=True before the DBA has answered. (5) It would create environments for a Production host IT has not confirmed |
| Safe after changes | Yes, if it: refuses to run without PROD_REVIEWER; sets prevent-self-review and a deployment-branch policy; does not overwrite a variable that already has a real value; leaves the certificate, account and connection string for IT; and defaults to Encrypt on with trust off until the DBA answers. All small and READY TO IMPLEMENT |
| Manual prerequisites | The GitHub command-line tool installed and logged in as admin; the repository's plan supports environment protection; DNS names and certificates from IT; the service accounts and their SPNs; the databases and grants from the DBA; the pool password set as an environment secret per environment; a self-hosted runner registered on each server with the right label; the Application Initialization feature and Windows Authentication enabled on each server |
| Variables and secrets expected | Variables as above; one secret per environment: the pool password |
| Who should run it | **Jeff runs it, because the command needs repository admin, but only after the script is corrected and with values IT has confirmed.** IT should prepare the values and the server side. Jeff should not run it today |

**Classification: READY TO IMPLEMENT** (script corrections); **NEEDS JBS INFRASTRUCTURE ANSWER** (values).

## 12. Regression plan

JBS changes must not break the product. After each implementation slice and again at the end, the **full** suite must pass on the integration branch, on Linux (CI) and on a Windows test host:

| Area | Tests that must run |
|---|---|
| Whole suite | Restore with locked mode, warnings-as-errors build; unit, architecture, API, integration (including every SQL Server wrapper with a real SQL Server); both package checks, documentation checks, tooling tests, supply chain; the **N-1 to N upgrade rehearsal** and the performance baseline in CI; the Windows unit run |
| Guided Administration | Guided administration journey, guided administration accessibility, navigation and home, the manual acceptance scenario, people and access, my access, mappings and checklist |
| People and Access | People access UI tests, access profile scenarios (SQLite and SQL Server), bootstrap and ownership UI, approval authorities UI |
| Synthetic identities in Test | The demonstration sign-in and test-person tests, the production-refusal tests (every Test and Demo only tool refused in Production), set-up host tests |
| Preview as User | The preview tests inside people access and access profile scenarios (read-only, banner, session claim re-validation, write guard) |
| Custom fields | Guided fields UI (SQLite) and guided fields SQL Server, studio UI and scenarios, custom field scenarios and unit tests |
| Contract creation, edit, lock | UI access tests, lifecycle UI, register scenarios, the effective-record lock assertions |
| Lifecycle, Legal review, approvals | Lifecycle scenarios (all four files), approval matrix scenarios and UI, approver group scenarios, the no-code rehearsal |
| Documents | Document and storage unit tests, scanning scenarios and UI, **new** upload-limit tests (section 8D) |
| Migration | Migration UI and scenarios on SQLite and SQL Server, **new** large-archive test |
| Backup and restore | Recovery unit tests, backup and restore SQL Server tests, backup failure tests, operations dashboard tests (External mode must leave the backup tests intact when it is off) |
| Upgrade | Upgrade foundation scenarios, upgrade SQL Server tests, upgrade journeys, upgrade UI, accepted-warning tests, **new** external-recovery-point tests |
| Solution packs | Pack format, signing and terminology unit tests; pack manager scenarios and UI; pack tool artefact test; the pack build and signing script check |
| Tenant isolation | Access scenarios, tenant isolation and row-level security SQL Server tests, classification scenarios, **new** least-privilege-login test |
| Authentication | Existing OpenID Connect tests unchanged; antiforgery tests; **new** Windows sign-in tests (invited, uninvited, disabled, no access, anonymous or guest identity, mismatched SID, no role from sign-in, audit of success and refusal, refusal never a 500) |
| Configuration safety | **New**: a start-up test that proves a host started with no settings refuses to run as Demo; the harness and compose demo still start |
| Logging | **New**: redaction canaries, no body or query string, correlation id present |

## 13. Risks

| # | Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|---|
| 1 | Nobody can sign in after the first deploy because Windows sign-in is not built, or the first administrator cannot link | Certain until built | High | Build section 5 first; the wizard change is part of it |
| 2 | A host runs as a Demo with a SQLite file in the site folder, wiped by the next deploy | Low | High | Safe defaults (8A) |
| 3 | Keys unreadable after a recycle or identity change; everyone signed out | Medium | Medium | Machine-scope keys, profile loaded, folder backed up (8C) |
| 4 | Documents over 28 MB fail; migration upload blocked | Certain today | Medium | Section 8D |
| 5 | A failed deploy leaves a half-updated site online | Low | High | Section 11 fix |
| 6 | An upgrade runs against a stale or wrong recovery point | Low | High | Section 8E rules; Production approval |
| 7 | The running application holds schema-owner rights (QA), weakening the row-level-security safeguard | Certain in QA | Medium | Accepted for QA; separate Production identities; least-privilege test now |
| 8 | A shared service account links two applications' blast radius and key scope | Certain in QA | Medium | Documented deviation; separate Production account |
| 9 | Production deploys without an approver, or an approver approves their own run, or a non-main branch is deployed | Medium | High | Section 11 script and workflow fixes |
| 10 | Unsigned packs block Production set-up | Certain | Medium | Decide signing (section 11) |
| 11 | Application Insights sends contract identifiers off the server | Medium | High | Telemetry processor, no bodies, residency answer (section 9) |
| 12 | In-process timers pause during a recycle or idle | Low once AlwaysRunning | Medium | Settings plus a readiness check |
| 13 | Deploy workflow's unit-test-only gate lets a failing change reach QA | Medium | Medium | Require full CI |
| 14 | Windows-only behaviours (paths, line endings, in-process authentication signals) differ from the Linux tests | Medium | Medium | The first QA deploy and a Windows test run are the verification; recorded as Verified or Failed, never assumed |
| 15 | Step-up re-authentication is weaker under Windows Authentication | Certain | Low to medium | JBS to accept or specify a stronger control |

## 14. Exact implementation order

Nothing here starts until the owner approves the branch step.

| Step | Work | Why this order | Class |
|---|---|---|---|
| 0 | Create jbs/iis-integration at the product commit, retarget pull request 1 to it | Everything below lands on the integration branch | READY TO IMPLEMENT (approval needed) |
| 1 | Make pull request 1 green: reword the two workflow comments so no vendor name remains, list the new files in the manifest, fix the path references in the new documents that do not resolve; merge it into the integration branch | Restores a trustworthy CI baseline | READY TO IMPLEMENT |
| 2 | Fix the two Windows unit tests (section 7) | Unblocks the Windows build job | READY TO IMPLEMENT |
| 3 | Safe base settings (8A) with a start-up test | Stops accidental Demo hosts before anything is deployed | READY TO IMPLEMENT |
| 4 | Upload limits and the project web.config, temp folder, publish exclusion for the local data folder (8D, section 6) | Makes a published package correct and self-contained | READY TO IMPLEMENT |
| 5 | Data protection: machine-scope keys, profile, permissions note (8C) | Sessions must survive recycles before any user signs in | READY TO IMPLEMENT |
| 6 | Pipeline and script corrections: branch restrictions, full-CI gate, failure handling, ready-check smoke test, keep-last-N snapshots, checksum in logs, environment script fixes (section 11) | The deployment path must be safe before it is used | READY TO IMPLEMENT |
| 7 | Serilog minimum slice: durable redacted file log, enrichers (section 9) | An IIS host must have a durable log from the first deploy | READY TO IMPLEMENT |
| 8 | Windows sign-in, invite-only (section 5), including the wizard and the refusal pages, audit, SID check, readiness check | The one change that makes the application usable | READY TO IMPLEMENT (5.2 option 2) |
| 9 | External recovery-point mode (8E) | Needed before any upgrade of an existing database | READY TO IMPLEMENT |
| 10 | Automated upgrade script in the pipeline (designed in the IT review) | Needs steps 8 and 9 and the DBA answers on identities | NEEDS DBA ANSWER (identities), then READY |
| 11 | Least-privilege SQL Server test | Proves what the DBA will be asked to grant | READY TO IMPLEMENT |
| 12 | First QA deploy as the **verification run** on Windows and IIS, recorded in the deployment status document as Verified or Failed | Nothing in the IIS path has ever run | NOT VERIFIED until done; needs the runner, DNS, certificate, database, SPN |
| 13 | Application Insights and Fatal-only email (second logging slice) | Needs JBS answers | NEEDS JBS INFRASTRUCTURE ANSWER |
| 14 | Explicit owner approval, then move main to the integration branch and enable the Production job | Production follows a verified QA | Owner decision |

## 15. What to send

### To JBS Infrastructure

1. Is **Entra ID** out of scope entirely, so IIS Windows Authentication is the only sign-in? (The IT review says yes; please confirm for Production.)
2. Is every user's **UPN identical to their email address**? If not, invitations will name the Windows account.
3. May we register the **Application Initialization** feature requirement, and may the pool run **AlwaysRunning with preload and no idle time-out** in Production? What **recycle schedule** should apply?
4. May **Anonymous** be enabled on /health/live only? Which monitoring identity will call /health/ready?
5. Which **service account** will run the Production pool, and will the QA account stay shared with JBS AI Initiatives? Please register the **SPN** for each host name.
6. **Certificate thumbprints** for the QA and Production bindings, and the DNS names (contracts-qa and contracts).
7. Which **drive and folders** for the data root, the logs folder and the temp folder on each app server? Are all of them in Infrastructure's **backup**, and what is the retention? Please confirm a restore of the data root and the database to the same time is possible.
8. Is the **repository plan** one that supports required reviewers and deployment-branch rules on environments? Who approves **Production** deployments? May the approver also be the person who triggered the deployment?
9. A **self-hosted runner** with the right label on the Production app server (and on QA, registered to this repository).
10. **GitHub Actions** artefact retention, and whether third-party actions must be pinned to commit identifiers.
11. **Application Insights**: the resource, data residency, and what telemetry may contain. The **SMTP route and recipients** for Fatal alerts.
12. What does JBS expect for **"site"** and **"login"** in log entries? Is the Windows account name acceptable in logs?
13. Is **step-up** (recent sign-in) acceptable as a "confirm it is you" click under Windows Authentication?
14. Who **signs solution packs** for Production, and how does the trust file reach the server?
15. Do Sections 5, 9, 10 and 11 of the standards guide **bind**, or are they guidance? Is the **Jbssa.** project prefix required?

### To the DBA team

Questions 1 to 14 in section 10.

## 16. Next action (the answers you asked for)

**A. Can pull request 1 be merged?** Not as it stands. Its CI is red (a vocabulary-guard failure and unproven later steps), its repository checks fail, and merging to main starts a deploy workflow that cannot yet produce a usable system.

**B. Which branch should it merge into?** A new integration branch, jbs/iis-integration, created at the current product commit (main and ux/guided-administration are identical). Retarget the pull request to it. Move main only after a verified QA run and your approval.

**C. Is the environment script safe for you to run?** No, not as written. After the corrections in section 11, yes, and you (the repository admin) should run it, with IT's confirmed values.

**D. Questions for Infrastructure:** section 15, list 1 to 15.

**E. Questions for the DBA:** section 10, list 1 to 14.

**F. Code tasks Claude can safely start immediately (once you approve the branch step):** make pull request 1 green; fix the two Windows tests; safe base settings; upload limits, project web.config and publish exclusion; machine-scope data protection; pipeline and script corrections; the Serilog durable-log slice; invite-only Windows sign-in with the wizard change (using the account-named invitation); external recovery-point mode; the least-privilege SQL Server test.

**G. Tasks that must wait for answers:** the automated upgrade step (DBA identities and whether the pipeline checks backups); Application Insights and Fatal email (Infrastructure); certificate and account values; pack signing; the step-up decision; the first QA verification run (runner, DNS, certificate, database, SPN).

**H. Does the current product architecture remain suitable?** Yes. The modular monolith, the invite-only identity model, the unchanged evaluator, tenant isolation, the upgrade procedure and the secret-reference scheme all fit the JBS IIS route. The work is at the edges: one new identity adapter and an additive invitation field, an external recovery-point mode, safe defaults, upload limits, key protection, Serilog and the pipeline fixes. None touches the domain, the access model or the data model beyond one additive column.

## 17. Limits of this review

* Nothing was run on Windows, IIS, a JBS server, the self-hosted runners, GitHub environments or the deployment pipeline. Windows findings are from reading the code and the scripts, and from publishing the Host project on Linux (which confirmed the generated web.config and the published settings file).
* The expected IIS behaviours noted as "to be confirmed" (the Windows-authentication signal for the readiness check, the in-process request time-out, profile loading for the pool account) come from knowledge of the framework and must be proved in the first QA verification run.
* Action versions used by the new workflows were not checked for existence.
* The repository's plan and visibility, which decide whether environment protection rules are available, were not checked.
* The pull request's own CI result is the evidence for the product-CI failure; the repository-check result was reproduced locally on the pull request's tree.
