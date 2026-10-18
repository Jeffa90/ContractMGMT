# Pack implementation guide: choosing, receiving, checking, installing and assigning a solution pack

**Purpose.** Take a business administrator from "which pack suits us?" to "the organisation has its starting drafts", using only the application's screens. No JSON editing, SQL, source change or Git is needed for any step in sections 3 to 10. The few steps that cannot be done on a screen (building and signing a pack, keeping the private key, installing the platform) are labelled as technical tasks for a named technical role and are not described here.

**Who it is for.** A trained non-developer: the Business Configuration Administrator, Tenant Administrator or Organisation Administrator who will run the Pack Manager, and the people who sit beside them (System Administrator, Pack Publisher, security/key custody, a second person who publishes).

**Prerequisites.**

* You can sign in and see **Solution packs** in the Administration menu (this needs the "packs.read" permission).
* The installation is running and an organisation exists (see docs/SETUP_AND_ONBOARDING.md for the first-run wizard and the organisation set-up checklist).
* You have a pack file from a publisher you trust, or you will use one of the packs shipped with the installation.
* A second person with the right to publish configuration exists, so the drafts the pack prepares can be published (section 9).

**Status label.** Pack Manager screens: **Implemented** as server-rendered forms (read from the page code; not driven by a person in a browser for this guide). Signing and key custody: **external, by design** (the application holds no private key and cannot sign). Upgrade across pack versions, pack export, marketplace or online catalogue: **Future**. No real publisher key or signed Production pack is supplied with this repository: **Not Verified** end to end for Production. Nothing here declares production approval; that is external governance.

How this journey was proved without code: docs/NO_CODE_REHEARSAL.md (steps 4 to 12 cover assigning a pack and publishing the drafts; run over real HTTP on SQLite and on SQL Server by automated tests, not by an unaided non-developer). Background: docs/PACK_MANAGER.md, docs/PACK_SIGNING.md, docs/PACK_AUTHORING.md and docs/PACK_SECURITY.md.

---

## 1. Who does what: the four-way split

A solution pack is data only. It never runs code. The application deliberately separates four kinds of person so that nobody can sign, trust and install their own pack alone.

| Who | Does what | Where | Cannot do |
|---|---|---|---|
| **Pack Publisher** (external to the application) | Builds the pack and signs it with a private key using the pack command-line tool. Hands over the signed file, the key identifier and the public key. | Outside the application. **[Technical task: Pack Publisher]**, see docs/PACK_AUTHORING.md and docs/PACK_SIGNING.md | Cannot install or assign; the application never sees the private key |
| **Key custody** (your security team, or the publisher) | Generates and guards the private key (ideally in a hardware module or key vault), rotates it, backs it up, decides who may sign, revokes it if lost. | Outside the application. **[Technical task: key custody]** | The application cannot hold, show or accept a private key. If anything asks you to paste one, stop and tell your security team |
| **System Administrator** (technical role) | Decides which publisher keys the installation trusts. Can set trusted public keys in the host configuration (a technical task). Runs the installation, backups and upgrades under Operations. | Host configuration, **[Technical task: System Administrator]**; Operations pages | In the role-permission matrix the System Administrator role holds none of the pack permissions (see the note after this table) |
| **Business Administrator** (Tenant Administrator, Business Configuration Administrator, Organisation Administrator) | Chooses the pack, uploads the signed file, reads the checks, installs it, assigns it to an organisation, reviews the drafts and (with a second person) publishes them. | The Pack Manager pages in section 2 | Cannot sign a pack. Cannot publish their own draft |

**Note on the Trusted publishers page (a recorded contradiction).** The panel on the pack catalogue says a system administrator registers the public key under Trusted publishers. The page itself is protected by the permission "packs.trust.manage", and the role-permission matrix (requirements/02-reference/role-permission-matrix.csv) grants it to the **Tenant Administrator** role only, not to the System Administrator role. In practice: the System Administrator or security team decides which key to trust; a person who holds the Tenant Administrator role enters its public key on the page, or the System Administrator sets the key in host configuration (a technical task). Confirm which route your installation uses before the go-live date.

### Permissions used on these screens

| Permission | What it allows | Tenant Admin | Org Admin | Business Config Admin | Auditor |
|---|---|---|---|---|---|
| packs.read | Open the catalogue, details, Organisation packs, Checks | Yes | Yes | Yes | Yes |
| packs.upload | Upload a file to the holding area | Yes | No | Yes | No |
| packs.validate | Read the checks and preview | Yes | Yes | Yes | No |
| packs.install | Install a checked pack | Yes | No | Yes | No |
| packs.assign | Give an organisation a pack (prepares drafts) | Yes | Yes (own organisation) | Yes (own organisation) | No |
| packs.retire | Retire or reinstate an installed version | Yes | No | No | No |
| packs.trust.manage | Register or revoke a trusted publisher key | Yes | No | No | No |
| packs.audit.read | Read the pack history | Yes | No | No | Yes |

### Governance that applies

* **Recent sign-in (step-up).** Installing, retiring, reinstating, assigning, registering a key and revoking a key each need a sign-in within the last 10 minutes. If yours is older you are sent to confirm it is you and returned to the same page. Uploading and reading do not need it.
* **Second-person publication.** A pack only ever creates **drafts**. The person who last edited or submitted a draft cannot publish it; a change after submission voids the submission; publishing itself needs a recent sign-in. See section 9.
* **Audit.** Every upload, check, install, retirement, reinstatement, assignment and key change is recorded with who, when and the outcome (section 11).
* **Only the uploader can install.** An upload sits in a holding area for 30 minutes and belongs to the person who made it. Another person cannot install it.

---

## 2. Choosing a pack

Three packs ship with the installation (versions as in the pack sources; check the catalogue for what your installation lists). All three are **synthetic examples, not advice**. The shipped files are unsigned, so **Production refuses them** until a trusted publisher signs equivalents (see section 4).

| | **Blank** (neutral) | **JBS-style**: pack key jbs-procurement | **Harborline-style**: pack key construction-subcontracts |
|---|---|---|---|
| Version in source | 1.6.0 | 2.6.0 | 1.1.0 |
| For | Any business that wants neutral wording and few steps | A procurement register with Legal review and a Group Head approval | A subcontract register with value-tier approvals |
| Contract workflow | 5 states: Open, Active, Amending, Closed, Cancelled | 17 states, Draft to disposition | 11 states, Draft to Completed |
| Capabilities (Legal review, Register authorisation, Variations, Renewals, Signature requests) | off, on, on, off, off | all on | off, on, on, off, on |
| Approval | One approver group, no value tiers | One approver group (Group Head or backup) | Value-tier approval matrices |
| Regional proposal | None (you decide) | Australia, en-AU, AUD, ABN | United States, en-US, USD, America/Chicago |
| Starting data | None | Categories, contract types, reference prefix JBSAUS | Trade packages, types, custom fields, views, prefix HBL, branding |

Decision guide:

1. Need Legal review as a formal step? Yes: JBS-style. No: Blank or Harborline-style.
2. Approve by value tiers with several people? Yes: Harborline-style.
3. Vocabulary closer to procurement or to subcontracts? Choose the nearer; terminology (docs/TERMINOLOGY.md) fixes the rest.
4. Nothing fits closely: start from Blank and change wording, capabilities and regional settings through the forms. A materially different workflow, approval or reminder rule needs a pack author (a **[Technical task: Pack Publisher]**).

Staying on Blank is always allowed. **Consequence of a wrong choice:** the drafts you receive have the wrong states, words or approvals. Nothing is lost, because nothing is published until you and a second person publish it, and a published release can be rolled back (new release, never editing history).

**Evidence to keep:** the decision note (pack key, version, who decided, why) and the Details page for that version.

---

## 3. Receiving the pack file and its trust details

This step happens outside the application. Ask the Pack Publisher for, through **separate channels**:

1. The signed pack file (ends in .ecpkg). Example names: jbs-procurement-2.6.0.signed.ecpkg, construction-subcontracts-1.1.0.signed.ecpkg, blank-1.6.0.signed.ecpkg.
2. The **key identifier** the pack was signed with (for example publisher-2026).
3. The **public key** in text form (begins "-----BEGIN PUBLIC KEY-----"). Public keys only.
4. The publisher's statement of the **package checksum (SHA-256)** and the public key's fingerprint, sent by a different route from the file (a telephone call, a signed message, the publisher's own site). You will compare them on screen in sections 7 and 8.

Do not accept a private key from anyone. A key file or text that says "PRIVATE" is refused by the application, and you should tell your security team.

**[Technical task: Pack Publisher]** builds, validates and signs the pack. **[Technical task: key custody]** guards the private key. Neither is a screen in this application.

**Evidence to keep:** where and when each item arrived and by which channel; the checksum and fingerprint as the publisher stated them.

---

## 4. Trusted publishers

**Navigate:** Administration menu, **Solution packs**, then the **Trusted publishers** button. Page: **/Admin/Packs/TrustKeys**.

**Purpose.** Tell the installation which publisher keys it will accept. Production accepts a pack only if it is signed by a trusted, active, in-date key listed here or set by the host configuration.

**Who.** The holder of the Tenant Administrator role, on the instruction of the System Administrator or security team (see the note in section 1). **Permission:** packs.trust.manage. **Step-up:** yes (recent sign-in).

**Prerequisites.** You hold the public key and key identifier from section 3 and have the decision to trust this publisher in writing.

The table at the top lists every key: Key, Label, Algorithm (ES256), Fingerprint (SHA-256), Set by ("tenant trust store" or the host configuration), Status, Valid from and until (UTC). Keys set in host configuration are read-only here and cannot be revoked here.

### Form: Trust a new publisher key

| Field | Required | What to enter | Blank example | JBS-style example | Harborline-style example | Rule and consequence of a wrong value |
|---|---|---|---|---|---|---|
| Key identifier | Yes | The identifier the publisher signed with, exactly | publisher-2026 | jbs-pack-publisher-2026 | harborline-publisher-2026 | 2 to 80 letters, digits, dot, underscore, colon or dash, starting with a letter or digit (the box allows 64). A typo means the publisher's packs show "signing key unknown" (PACK-S003). A key id cannot be reused or replaced: register a new key under a new id |
| Who it belongs to | Yes | The publisher's name, up to 120 characters | Example Publisher (synthetic) | JBS Pack Publisher (synthetic) | Harborline Pack Publisher (synthetic) | Shown to everyone who reads the list; use a name your auditors will recognise |
| Public key | Yes | Paste the whole public key including the BEGIN and END lines | -----BEGIN PUBLIC KEY----- (publisher's text) -----END PUBLIC KEY----- | same | same | Must be an ECDSA P-256 public key. A private key is refused and not echoed. A wrong key makes every genuine pack from the publisher fail (PACK-S002) |
| Valid from (UTC, optional) | No | Start of validity | leave blank | leave blank | leave blank | Before this moment the key is "not yet valid" (PACK-S006) |
| Valid until (UTC, optional) | No | End of validity, for example the planned rotation date | 2028-01-01 00:00 | 2028-01-01 00:00 | 2028-01-01 00:00 | Must be after the start. After it the key is expired (PACK-S005); set a reminder to rotate before then |

**Buttons.**

* **Register this key.** Saves the public key. Result: the flash message "The publisher key was registered. Packs signed with it are now trusted here." and the key appears in the table as Active with its fingerprint.
* **Revoke** (on a tenant-registered, active key; with the optional **Reason**, up to 500 characters). Packs signed only with that key stop being trusted immediately for new installs and assignments. Nothing is deleted; published configuration is unaffected. Result: "The publisher key was revoked." and the row shows Revoked with the time and reason.

**After registering.** Read the fingerprint on the page to the publisher (or security team) over a separate channel and have them confirm it matches the key they hold. If it does not, revoke it at once.

**Common errors.**

| Message (plain meaning) | Recover |
|---|---|
| "That looks like a private key. Paste the PUBLIC key only" | Do not paste it anywhere. Tell your security team the private key was exposed; the publisher should replace the key |
| "That is not a valid ECDSA P-256 public key in PEM form" | Copy the whole block, including both marker lines, with no extra characters |
| "A key with that id is already registered. Keys are revoked, never replaced" | Register the new key under a new identifier |
| "That key id is already used by a platform-configured key" | Choose another identifier |
| "The key's end date must be after its start date" | Correct the two dates |
| Redirect to sign-in | Your sign-in is older than 10 minutes; confirm it and try again |

**Evidence to keep:** a screenshot of the table row (fingerprint visible), the written decision to trust, the publisher's fingerprint confirmation, and the matching pack history entry (pack.trust.registered).

---

## 5. Open the catalogue

**Navigate:** Administration menu, **Solution packs**. Page: **/Admin/Packs**. **Permission:** packs.read. **Prerequisites:** none. **Step-up:** no.

**Purpose.** See every pack this installation can offer and whether it is usable.

Buttons across the top: **Upload a pack**, **Organisation packs**, **Trusted publishers**, **History**, **Checks**. A collapsed panel, "Who does what when a pack comes to this installation", repeats the split in section 1. Filter links: All packs, Shipped with this installation, Installed by administrators.

Reading the table:

| Column | Meaning | What to do |
|---|---|---|
| Source | Shipped (read-only files next to the application) or Installed (added by an administrator) | Shipped packs cannot be retired or replaced |
| Signature | "Signed, trusted" (green); "NOT SIGNED (demonstration or test only)" (amber); "Not trusted" (red) | Use only "Signed, trusted" for real contracts. Production refuses the others |
| Compatible | Whether this platform version can use it | "No" means ask the publisher for a compatible version |
| Status | "Ready to use", "Retired", "Cannot be used here", "Unavailable" | Only "Ready to use" can be assigned |
| Organisations using it | Count | Retiring a version does not change these organisations |

Empty table: "No solution packs are available. Upload one, or continue with the Blank configuration."

---

## 6. Upload (step 1 of 3)

**Navigate:** **Solution packs**, then **Upload a pack**. Page: **/Admin/Packs/Upload**.

**Purpose.** Send the signed file to a safe holding area where it is checked. Nothing is installed and nothing in any organisation changes.

**Who.** Tenant Administrator or Business Configuration Administrator. **Permission:** packs.upload. **Step-up:** not required for upload. **Prerequisites:** the file from section 3; for Production, the publisher's key already trusted (section 4), otherwise the result will be "Untrusted".

| Field | Required | Enter | Blank example | JBS-style example | Harborline-style example | Rule and consequence |
|---|---|---|---|---|---|---|
| Solution pack file (.ecpkg) | Yes | Choose the file | blank-1.6.0.signed.ecpkg | jbs-procurement-2.6.0.signed.ecpkg | construction-subcontracts-1.1.0.signed.ecpkg | Up to 8 MB; only a pack archive. A wrong or damaged file is reported by the checks, not installed. The exact bytes of a shipped pack cannot be uploaded again (outcome "Duplicate") |

**Buttons.**

* **Upload and check.** Sends the file, runs the checks (safe archive, nothing changed since it was made, who signed it and whether you trust them, platform compatibility, and that each document is valid configuration by the same rules as anything you configure) and takes you to the Review page. Checking takes a few seconds; wait.
* **Cancel.** Returns to the catalogue.

**Success.** The Review page opens (section 7). **Common errors.** "Choose a pack file (.ecpkg) to check." (no file chosen). A file over the limit or not a pack archive is shown as an error on the Review page with a code (the full list is in docs/PACK_SECURITY.md, section 5). **Evidence to keep:** the file name and checksum shown on Review.

---

## 7. Review: read the checks, then install or discard (steps 2 and 3 of 3)

**Navigate:** you arrive here automatically after **Upload and check**. Page: **/Admin/Packs/Review** (the address carries a one-time token; it expires after 30 minutes and works only for the uploader).

**Purpose.** Read the result of the checks and the preview, then install or discard. **Who.** Reading: packs.validate (Tenant Administrator, Organisation Administrator, Business Configuration Administrator). Installing: packs.install (Tenant Administrator, Business Configuration Administrator) **and the same person who uploaded**. **Step-up:** yes, for Install.

What is on the screen, top to bottom:

1. **A statement** in a coloured box saying whether the pack can be installed (outcomes: Valid, Valid with warnings, Invalid, Unsupported, Untrusted, Duplicate, Conflict). Only Valid and Valid with warnings can be installed.
2. **A red warning** if the pack is not signed by a trusted publisher.
3. **The summary:** pack name and version, Pack key, Publisher, Signature (with key and algorithm), Capabilities, Needs platform version, and **Package checksum (SHA-256)**. **Compare the checksum with the one the publisher gave you separately (section 3). If it differs, discard the upload.**
4. **Result of the checks:** a table with Level (Error or Warning), Code, Where, What was found, How to fix it. **Download this report (CSV)** saves it to send to the publisher (cells that look like formulas are defused).
5. **What installing will do** (only when installable): the kinds of configuration the pack carries, new capabilities, organisations already using that pack key, any terminology introduced, and "Next steps". Installing publishes nothing, changes no organisation and no contract.

**Buttons and the confirmation.**

| Control | What it does |
|---|---|
| Checkbox "I have read the result and want to install this pack. I understand it changes no organisation until I assign it." | Required. Without it the page asks you to tick the box |
| **Install this pack** | Re-checks the held copy, stores the pack and makes it available to assign. Disabled when the pack needs other packs that are not installed. The identical file again changes nothing; a different file with the same pack key and version is refused (a published version never changes: the publisher must issue a new version) |
| **Discard without installing** (or **Discard this upload** when it cannot be installed) | Deletes the held copy; nothing is installed |

**Success.** "The pack was installed. It is available to assign, and nothing in any organisation has changed." You land on the pack's Details page. If it was already there: "That exact pack was already installed. Nothing was changed."

**Common problems and recovery.**

| You see | Meaning | Recover |
|---|---|---|
| Untrusted, code PACK-S003 | Signed by a key nobody here trusts | Section 4: have the key registered (after the publisher confirms), then upload again |
| Signature missing (PACK-S010) or "NOT SIGNED" | Unsigned packs are refused in Production | Ask the Pack Publisher to sign it |
| Signature invalid (PACK-S002) | Altered file or wrong key registered | Ask for a fresh copy by a different route; check the fingerprint you registered |
| Key revoked or expired (PACK-S004, S005) | The signing key is no longer valid | Ask the publisher to re-sign with a current key |
| Same key and version, different content (PACK-D002) | A published version never changes | Ask the publisher for a new version number |
| Needs a newer platform version (PACK-C001) | Pack is newer than this installation | Ask the System Administrator about upgrading; see docs/UPGRADE_RUNBOOK.md |
| "This upload is no longer available" | It expired or was discarded | Upload again |
| Forbidden | Someone other than the uploader, or without install permission | The uploader repeats it, or ask a person who holds packs.install to upload and install |

The full code list is in docs/PACK_SECURITY.md (section 5).

**Evidence to keep:** the downloaded CSV report, the checksum, and a screenshot of the statement and summary.

---

## 8. Details, retire and reinstate

**Navigate:** from the catalogue, the **Details** link on a row (or you land here after installing). Page: **/Admin/Packs/Details**. **Permission:** packs.read to open.

**Purpose.** See what a version is, what it contains (kinds, scope, whether required), which organisations use it, other versions, and, for an installed (not shipped) pack, retire or reinstate it. Facts shown include Source, Signature, Compatible, Capabilities, Package identifier and the checksum.

Buttons: **Use for an organisation** (only when the pack is usable; opens the Assign page with the pack chosen) and **Back to solution packs**.

**Retire this version** (installed packs only). Field **Reason (kept in the history)**, optional, up to 500 characters; example "Superseded by 2.7.0". Button **Retire this version**. Effect: the version cannot be given to more organisations; organisations already using it keep it. Needs packs.retire (Tenant Administrator) and a recent sign-in. A retired version shows **Make this version available again**. The form appears for anyone who can open the page, but only a holder of the retire permission succeeds; others see an error.

**Evidence to keep:** the retire reason and the history entry (pack.retired or pack.reinstated).

---

## 9. Assign the pack to an organisation, then publish the drafts

### 9.1 Assign

**Navigate:** Administration menu, **Solution packs**, then **Organisation packs** (or **Use for an organisation** on a pack's Details page). Page: **/Admin/Packs/Assign**.

**Purpose.** Give one organisation a pack. This prepares **drafts** of its contract lifecycle, Variation workflow, notification policy and (where the pack carries them) terminology, regional settings, fields, matrices, views and similar. It never replaces anything published and never overwrites a draft that is already open.

**Who.** The organisation's own administrator: after an organisation is created, the Tenant Administrator who created it has no organisation-level configuration rights by design, so the **Organisation Administrator** (or a Business Configuration Administrator for that organisation) does this step. **Permission:** to open, packs.read; to assign, packs.assign for that organisation. **Step-up:** yes. **Prerequisites:** the pack shows "Ready to use"; the organisation exists (its set-up checklist is at /Admin/Organisations/Setup).

The top table lists each organisation you may see with its Pack, Version, Where it came from, Assigned (UTC) and Status (Blank, Pack available, Pack not available here).

**Form: Give an organisation a pack** (choose, then preview).

| Field | Required | Enter | Blank | JBS-style | Harborline-style | Consequence of a wrong choice |
|---|---|---|---|---|---|---|
| Organisation | Yes | The organisation to start | Your new organisation, for example "Example Co" | The procurement organisation, for example "Example Foods Pty Ltd" | The contractor, for example "Harborline Construction" | Drafts land in the wrong organisation; they are drafts, so discard them and assign to the right one |
| Pack and version | Yes | One entry from the list; "(not signed)" is shown for unsigned packs | Blank v1.6.0 | JBS Procurement v2.6.0 | Construction Subcontracts v1.1.0 | A pack that is retired, untrusted, incompatible or missing a dependency is not offered |

**Preview what would change** (a read-only step) shows, for each kind of configuration: Kind, Published already (Yes/No), Draft waiting (Yes/No) and What would happen. Warnings (for example an unsigned pack) appear above. If the pack's capability defaults would switch a capability off from under work in flight, or while published workflows use it, assignment is refused and the reason is shown here first.

**Assign this pack and prepare drafts** creates a draft for each kind where none is waiting. An open draft is kept and reported ("An open draft already exists and was kept"). Capability defaults are applied as defaults (no draft); the organisation's own published choice still wins. Terminology is never published or overwritten. Nothing is published.

**Success.** The flash "The pack was assigned. Drafts were prepared where there was nothing waiting. Review and publish them." and a Result list per kind with Next steps. **Common errors:** capability_conflict (the pack's defaults do not fit the organisation as it is now: read the stated reason, adjust under Capabilities, or choose another pack); pack_not_available/not usable (check section 4 and the Checks page); "configuration pending publication" (drafts from an earlier assignment are waiting: review and publish those first).

**If the pack was chosen when the organisation was created** (the Pack field of the New organisation form), the choice is recorded, but you still open this page, preview and press the assign button so the organisation's own administrator prepares the drafts. Drafts already waiting are kept.

**Evidence to keep:** the Result list, and the history entry (pack.assigned).

### 9.2 Review and publish the drafts (second person)

1. Open each draft in its own place and read it: Workflow configuration (/Admin/Configuration), Configuration Studio (/Admin/Studio), Terminology (/Admin/Terminology), Capabilities (/Admin/Capabilities), Regional settings (/Admin/Regional) and the form editors (/Admin/Forms/Edit). Check them (**Save and check**), preview the impact, and press **Submit for review** (where a second person is required) or **Publish**.
2. A **different person** opens **Configuration reviews** (/Admin/Reviews). The tabs are "Waiting for my decision", "Put forward by me", "Sent back or rejected" and "Everything I can see". Open the item (**Review**), read the plain-language "What would change", then choose **Approve and publish**, **Send back for changes** (comment required) or **Reject** (comment required). Approving needs a recent sign-in. The author never sees the approve button, and any edit after submission voids the submission.
3. **Order matters for rich packs.** For the Harborline-style pack publish in this order: lookups and custom fields, then approval matrices (a workflow that names a matrix is refused until it is published), then workflows, terminology, capabilities, notification policy and views. Some documents upload with warnings ("depends on the organisation") and are only checked for real at publication, so a green upload does not mean a publishable draft (docs/PACK_AUTHORING.md section 6).
4. Until publication the organisation runs on what was in force before.

For the full configuration walk-through see docs/IMPLEMENTATION_PLAYBOOK.md (sections 8 to 13) and docs/NO_CODE_REHEARSAL.md.

---

## 10. Checks

**Navigate:** **Solution packs**, then **Checks**. Page: **/Admin/Packs/Checks**. **Permission:** packs.read. It only reports; it changes nothing.

It shows: the shipped-packs folder (and whether it was found), Pack files found, Usable in this environment, whether unsigned demonstration packs are Refused (Production) or Allowed (demonstration or test), files that were not accepted and why, and **organisations whose pack cannot be used** (pack_unresolved: the assigned pack is not installed, trusted, current or compatible).

Recover from "No shipped pack is trusted in Production": have the Pack Publisher supply signed packs, register their key (section 4) and install (sections 6 and 7). A missing shipped folder is a **[Technical task: System Administrator]** (check the deployment). Nothing is changed automatically; options are listed in docs/PACK_MANAGER.md section 3.

---

## 11. History (audit)

**Navigate:** **Solution packs**, then **History**. Page: **/Admin/Packs/Audit**. **Permission:** packs.audit.read (Tenant Administrator, Auditor).

A read-only list, newest first, 50 per page: When, What, Who, Outcome, Subject, Reason. Entries cannot be edited. Events: pack.upload.verified, pack.upload.rejected, pack.installed, pack.retired, pack.reinstated, pack.assigned, pack.start.recorded, pack.trust.registered, pack.trust.revoked. Details never include key material or signature bytes.

---

## 12. Evidence to keep

For each pack taken into use, keep together (for the project file and any audit):

* The decision note: pack key, version, reasons, approver.
* The publisher's checksum and key fingerprint statements, and how you received them.
* The downloaded validation report (CSV) and the Review statement screenshot.
* The Trusted publishers row (fingerprint) and its history entry.
* The Details page of the installed version and the Result of the assignment.
* The Configuration reviews records showing who submitted and who published each draft.
* The pack history entries for the whole sequence.

Never keep or send a private key, a password or real customer data as evidence.

## 13. If something goes wrong

| Situation | What to do |
|---|---|
| You pasted or were given a private key | Stop. Do not register it. Tell the security team; the publisher issues a new key pair and you register only the new public key |
| You trusted the wrong key | Revoke it on Trusted publishers with a reason. Revoking is immediate and deletes nothing |
| You installed the wrong pack | Retire that version on its Details page. Organisations already using it are unchanged; assign the right pack instead |
| You assigned to the wrong organisation | Nothing is published. Discard the unwanted drafts in their editors and assign correctly |
| A published change is wrong | Use release history (/Admin/Configuration) to publish an earlier release again with a reason; it needs a recent sign-in, creates a new release and, in Production, goes to a second person |
| The page sends you to sign in again | Your sign-in is older than 10 minutes; confirm it and repeat the action |
| "Forbidden" on a button | Your role lacks that permission (section 1); ask a person who holds it |
| Production refuses every pack | Expected until a trusted, signed pack is installed (Checks page); this is by design |
| Anything unclear | Raise it with the System Administrator; see docs/OPERATIONS_RUNBOOK.md |

## 14. What was verified and what was not

* **Verified by reading the code:** every screen, field, button, permission and message described above was checked against the page and handler code under src/Ecp.Host/Pages/Admin/Packs, the role-permission matrix, and docs/PACK_MANAGER.md, docs/PACK_SIGNING.md, docs/PACK_AUTHORING.md and docs/PACK_SECURITY.md.
* **Not verified:** no one has followed this guide unaided in a browser; no assistive-technology test; no signed Production pack exists in the repository, so the Production path (a real trusted key, a real signed file) is **Not Verified** end to end; how a signed copy of a shipped pack at the same version behaves on upload is not verified here (ask the Pack Publisher to issue it under a new version, as docs/PACK_AUTHORING.md section 4 advises); the Review page's statement wording is generated by the service and not quoted here.
