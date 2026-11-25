# JBS Contracts: product spec

Status: agreed with Jeff in planning, 2026-11-25. Nothing is built yet. Decisions behind this spec are in decisions.md (numbered D-xx). Look and feel is in design-system.md.

## 1. What it's for

JBS Procurement raises supplier contracts, works them through Legal, gets them signed in DocuSign, and has the Group Head (or backup) approve them into the register. Today that happens across email, spreadsheets and folders. What hurts most:

1. **No personal view.** Contract owners have no one place showing what's theirs and what needs them.
2. **Not knowing where Legal is up to.** Who in Legal has it, what they're doing, whose turn it is.
3. **Missed renewal and notice dates.** A missed notice deadline is the costly mistake.
4. **Can't find contracts quickly.**
5. **Approvals stuck or invisible.**

Every screen and tile in this spec traces back to one of these.

## 2. Glossary (one word per concept)

| Say | Meaning | Don't say |
|---|---|---|
| Contract | An agreement with a supplier | Agreement record, entity |
| Owner | The one person responsible for a contract (with an optional backup) | Category owner, contract manager |
| Contract owner (role) | Someone allowed to raise and edit contracts | Requester |
| Stage | Where a contract is: Draft, Legal review, Signing, Approval, Effective, Ended, Archived, Withdrawn | Status, lifecycle state |
| Whose turn | During Legal review: Legal's turn or the owner's turn | Ball in court, assignee |
| Signed copy | The executed contract uploaded after DocuSign | Executed copy |
| Approve | The Group Head's (or backup's) decision that registers a contract and makes it Effective | Authorise, register authorisation |
| Registered date | When the contract was approved into the register (set by the app, never typed) | |
| Locked | An Effective contract that can only change through a Variation | Immutable |
| Variation | An approved change to an Effective contract | Amendment, change request |
| Notice deadline | The last day to give notice: end date minus notice period | Critical notice date |
| Renewal decision | Renew, Let it end, or Renegotiate | |
| Field | A piece of information on a contract (built-in or added by an admin) | Attribute, custom field, metadata |
| Section | A titled group of fields on the contract page and the Add contract form | Tab, panel, form group |

## 3. People and access (D-03 to D-06)

An admin adds each person (Windows logon, name, email) and ticks their roles. A person can hold several. Anyone not added sees "You don't have access to JBS Contracts yet. Ask your admin to add you." with the admin's name.

| Role (tick) | Can |
|---|---|
| *(no ticks)* | Look at contracts and documents (not Confidential ones) |
| Contract owner | Raise contracts; edit any contract that isn't locked; send to Legal; upload signed copies; send for approval; record renewal decisions; start Variations and renewals; end contracts |
| Legal | Everything a person with no ticks can see, plus Confidential contracts; take Legal reviews, send them back and forth, clear them |
| Approver | Approve or reject contracts and Variations. One approver is marked **Group Head**; the others are backups. Both see and can act on every approval; the record shows who acted and "as backup" where so |
| Admin | People, fields and sections, lists, approvers, reminders, email wording, import, emergency unlock, archive |
| Auditor | Read everything including Confidential, all history and audit; export the register and audit. Changes nothing |

**Confidential** contracts (a tick on the contract) are visible only to their owner and backup, Legal, approvers, admins and auditors. Everyone else doesn't see them in lists, search, tiles or reports, and a direct link says the contract can't be found. Limiting by business unit or site is a later step (D-05).

Rules that are always enforced on the server:
- You can't approve a contract or Variation you raised or own. ("You can't approve this one because you raised it. Ask another approver.")
- Only the actions shown in section 4 change a contract's stage.
- Locked contracts reject every edit except those allowed in section 7.

## 4. The contract journey (D-07)

```
Draft ──► Legal review ──► Signing ──► Approval ──► Effective ──► Ended ──► Archived
  │        (if ticked)        ▲            │            │
  │                           └── Rejected ┘            └── Variation (stays locked)
  └── Withdrawn (from Draft, Legal review or Signing, with a reason)
```

| Stage | Who acts | What they can do | Moves on when |
|---|---|---|---|
| Draft | Any contract owner | Fill in fields, add documents. Drafts save as you type and can be incomplete | Owner clicks **Send to Legal** (if "Needs Legal review" is ticked) or **Ready for signing** (if not). Required fields are checked here |
| Legal review | Legal and the owner, taking turns | See section 5 | Legal clicks **Legal cleared** |
| Signing | Owner | Sign in DocuSign (outside the app), then upload the **signed copy** and confirm the effective date. Details are frozen; **Send back to Legal** (with a reason) if something must change | Owner clicks **Send for approval** (needs a signed copy) |
| Approval | Group Head or a backup | Read a one-page summary, the signed copy and the Legal outcome | **Approve and register**: stage Effective, registered date and approver stamped, contract locked, reminders scheduled. **Reject** (reason required): back to Signing with the reason shown at the top |
| Effective | Owner | Record a renewal decision, start a Variation, renew, end | Ends on its end date (if "Let it end"), when its renewal becomes Effective, or when the owner ends it early (reason required) |
| Ended | Admin | Archive | Admin clicks **Archive** |
| Archived | Nobody | Read only (D-14) | Never deleted |
| Withdrawn | Nobody | Read only | |

"Needs Legal review" is ticked by default; the owner can untick it (D-08).

## 5. Legal review: a back-and-forth (D-09)

Legal doesn't always negotiate with the supplier; the owner often does. So a Legal review is a conversation that passes between Legal and the owner as many times as needed.

- The contract always shows **whose turn it is**, **who has it** (the Legal person, or the owner), **since when** and a **status**: Not started, Reviewing, Waiting on supplier, Waiting on owner, plus an optional short note.
- **Send to Legal** (owner): note and documents optional. New reviews appear for every Legal person under "New to pick up" until someone clicks **I'll take it** (or a Legal person assigns it).
- **Send to owner** (Legal): a note is required ("Supplier won't accept clause 12, please negotiate"); mark-ups can be attached.
- **Send to Legal** again (owner): note and the supplier's new version attached.
- On the owner's turn the owner can edit the contract's fields; on Legal's turn fields are frozen and both sides can still add documents and notes.
- **Legal cleared** (Legal): closes the review; the contract moves to Signing.
- Every hand-over is kept as a numbered turn in the **Legal conversation** on the contract, with who, when, status, note and the documents linked as they were then. Turns can't be edited afterwards.
- Each hand-over emails the person whose turn it now is.

## 6. Signing (D-10)

Version 1: **upload the signed copy**. DocuSign is used outside the app. The data and screens are shaped so a DocuSign connection (send for signing from the app, signed copy returned automatically) can be added later as its own slice, behind an admin setting "How contracts are signed". Until that slice is built, the setting is not shown.

## 7. Locked contracts, Variations and emergency unlock (D-11)

An Effective contract is locked. What can still change, and how:

| What | How |
|---|---|
| Owner and backup | Any contract owner, any time (one-click hand-over), audited |
| Confidential tick | Admin only, with a reason |
| Renewal decision | Owner, any time |
| End date, evergreen, notice period, review date, value and currency, business unit, site | Through a **Variation** |
| Added fields | Per field: "through a Variation" (default) or "any time" (D-17) |
| Title, supplier, category, agreement type, reference, registered date | Never (start a new contract instead); an admin can correct through emergency unlock |

**Variation**: the owner clicks **Start a Variation**, gives a reason, changes the allowed fields (shown as old and new side by side), attaches the signed variation document, optionally sends it to Legal (same back-and-forth), then sends it for approval. When the Group Head or backup approves, the changes apply in one step and are audited; the registered date doesn't change. If rejected or cancelled, nothing changes. One open Variation per contract at a time.

**Emergency unlock** (admin): reason required, expires after 4 hours at most, a banner shows on the contract while it's open, and every edit carries the unlock in its audit record. It never changes the stage or the registered date.

## 8. Renewals, ending and archiving (D-12, D-14)

- **Renewal decision** on any Effective contract: **Renew**, **Let it end** or **Renegotiate**. Recording a decision stops the reminders for that cycle.
- **Renew** copies the contract into a new Draft linked to the old one ("Renews JBSAUS-0142"). It goes through the normal journey. When it becomes Effective, the old contract moves to Ended ("Ended, renewed by JBSAUS-0198").
- **Renegotiate** starts a Variation.
- **Let it end**: the contract moves to Ended on its end date.
- **End early**: the owner ends an Effective contract with a reason and date.
- **Archive**: an admin archives Ended contracts. Nothing is ever deleted (archive-before-delete; there is no delete in version 1).

## 9. Reminders (D-13)

A daily job at 7:00am Brisbane time sends reminder emails and shows them on Home.

| Reminder | When | To |
|---|---|---|
| Notice deadline (end date minus notice period; for evergreen contracts, review date minus notice period) | 90, 60, 30, 14 and 7 days before | Owner and backup |
| Escalation | 7 days before the notice deadline with no renewal decision | Group Head (copy owner and backup) |
| End date | 180, 90 and 30 days before | Owner and backup |

- Reminders stop once a renewal decision is recorded.
- A reminder is never sent twice (each has a unique key), even after a restart or a missed day; a missed day catches up the next morning.
- Changing a date through a Variation recalculates unsent reminders.
- Admins can change the day offsets and the email wording; every change keeps history.

## 10. Contract fields: built-in and added (D-15 to D-18)

Admins add, move, rename, hide and require fields without code, in **Admin > Contract fields**. No database change is needed for a new field.

**Built-in fields** (the starting layout):

| Section | Fields (* = rule field: can be renamed and moved, never hidden) |
|---|---|
| Basics | Title*, Reference* (JBSAUS-0001, set by the app), Supplier*, Category and subcategory*, Agreement type, Description |
| Owner and access | Owner*, Backup owner, Confidential*, Business unit, Site |
| Value | Value and currency (AUD by default), SAP outline agreement number |
| Dates and notice | Start date, Effective date*, End date*, Evergreen (no end date)*, Notice period in days*, Review date |
| Legal | Needs Legal review* |

**Added fields**: the admin picks a kind:

| Kind | Notes |
|---|---|
| Short text, long text | Optional maximum length |
| Whole number, decimal number, money (AUD) | Optional minimum and maximum |
| Date | |
| Yes/No | |
| Pick from a list | One choice or several; the admin types the choices, and can hide a choice later |
| Person | Someone added to the app |
| Supplier | A supplier in the app |
| Link, email | Checked for a valid web link or email address |

For every field (built-in or added) the admin sets: label, help text, section and position, required (checked when the contract leaves Draft, so drafts can stay incomplete), shown as a column option in the Contracts list, filterable, included in exports. For added fields also: **after Effective**, "change only through a Variation" (default) or "can be updated any time".

Sections: admins can add, rename, reorder and hide sections. The Add contract form shows one step per section.

How changes are made safely:
- The admin edits the layout with a **live preview** of the Add contract form and the contract page, then clicks **Save changes**.
- Every saved version is kept in **History** (who, when, what changed) and any earlier version can be put back.
- **Removing** a field hides it. Values already entered are kept and stay in history and audit; the field can be brought back.
- A field's kind can't be changed once any contract has a value in it (add a new field instead), so data is never mangled.
- Added fields appear in contract pages and forms, the Contracts list (column, filter, CSV), reports and the register export, and import mapping.

## 11. Screens

The sidebar shows Home, Contracts, Renewals, Suppliers, Reports (Auditors and admins) and Admin (admins). Approvals open from Home. `Ctrl/Cmd + K` jumps to any contract, supplier or person and offers "Add contract". Every page has a "?" help button.

### Home: a to-do list, not a chart wall
```
┌──────────┬─────────────────────────────────────────────────────────────┐
│ (JBS)    │ Good afternoon, Jeff                Wednesday 25 November    │
│ Home     │ [ Find a contract or supplier…                        Ctrl K ] │
│ Contracts│                                                              │
│ Renewals │ Your turn                                                    │
│ Suppliers│ ┌────────┐ ┌────────┐ ┌────────┐                             │
│ Reports  │ │   3    │ │   2    │ │   1    │                             │
│          │ │Back from│ │To      │ │Signed  │                             │
│          │ │Legal   │ │approve │ │copy due│                             │
│          │ └────────┘ └────────┘ └────────┘                             │
│          │ Coming up                                                    │
│          │ ┌────────┐ ┌────────┐ ┌────────┐                             │
│          │ │   4    │ │   6    │ │   2    │                             │
│          │ │Notice  │ │Ending  │ │With    │                             │
│          │ │in 30d  │ │in 90d  │ │Legal   │                             │
│          │ └────────┘ └────────┘ └────────┘        Choose what I see    │
│          │ Urgent                                                       │
│          │ ⚠ Overdue  Freight – Linfox    Notice deadline was 2 days ago│
│          │                                     [Record decision] [Open] │
│ (JJ) Jeff│ ⏱ This week  Packaging – Amcor   Legal sent it back Mon  [Open]│
│ Admin    │                                              Show all (7)    │
└──────────┴─────────────────────────────────────────────────────────────┘
```

### Contracts list
```
Contracts                                                [+ Add contract]
Every contract at JBS. Click one to open it.
[Search…]  My contracts | All | Waiting on Legal | Ending soon   [Table|Board] Export CSV
 Contract                 Supplier        Stage            With       Key date
 Freight services 2026    Linfox          ● Legal review   Legal 6d   Notice 12 Jan
 Cartons supply           Amcor           ✓ Effective      –          Ends 30 Jun 27
 128 contracts
```
Board view: one column per stage, each card showing how long it has waited. "Columns" lets a person add any field (including added fields) as a column. Lists keep their filters, search and scroll when you come back.

### Contract page
```
< Contracts
Freight services 2026  · JBSAUS-0142                      [Send to owner]
● Legal review – Legal's turn (Sam Lee, since Mon 17 Nov, 8 days)
  "Waiting on supplier": insurance certificate requested
 Draft ──● Legal review ──○ Signing ──○ Approval ──○ Effective
 Next: owner uploads the signed copy, then the Group Head approves.
┌ Essentials ───────────────────────────────────────────────────┐
│ Supplier Linfox · Owner Jeff Judd (backup Ann Ho) · $1.2m AUD │
│ Starts 1 Jan 26 · Ends 31 Dec 27 · Notice 90 days → 2 Oct 27  │
└───────────────────────────────────────────────────────────────┘
▸ Legal conversation (4)   ▸ Documents (3)   ▸ Reminders
▸ Variations   ▸ Activity (newest first)
```
Then the admin-arranged sections. Alert banners at the top for anything urgent ("This contract's notice deadline is in 12 days and has no renewal decision").

### Add contract
One short step per section (Basics, Owner and access, Value, Dates and notice, Legal), autosaved as a Draft, with "+ Add new supplier" inside the supplier picker. A sticky bar keeps Back / Next / Save visible.

### Approve (works on a phone)
A one-page summary: what, who, how much, dates, the signed copy, and the Legal outcome. Two buttons: **Approve and register** and **Reject** (asks for a reason).

### Renewals
```
Renewals                        Next 30 | 60 | 90 | 180 days
Notice deadlines  ●●  ●   ●●●      ●    ●       ●
                 |----30----|----60----|----90----|
 Overdue (1)  Freight – Linfox    notice was 23 Nov  [Record decision]
 This month (3) …
```

### Other screens
- **Suppliers**: list; supplier page with all its contracts, total value and upcoming dates.
- **Variation**: old and new values side by side, reason, document, send.
- **Reports** (slice 8): register (CSV and PDF), pipeline, approvals ("who approved what, when"), audit export.
- **Admin**: People; Contract fields and sections; Lists (categories with subcategories, business units, sites, agreement types, currencies); Approvers (who is Group Head); Reminders; Email wording; Import; and on a contract, Emergency unlock and Archive.
- **No access** page and **not found** page in plain words.

## 12. Home tiles (shown only while something is waiting)

| Role | Tiles |
|---|---|
| Contract owner (your contracts as owner or backup) | Back from Legal (your turn), Signed copy due, Approval rejected, Notice deadline in 30 days, Ending in 90 days, No renewal decision, Drafts |
| Legal | Your turn, New to pick up, Waiting on owner |
| Approver | Waiting for my approval, Variations to approve |
| Admin | Contracts without an owner, Reminders that failed to send, People waiting for access |
| Auditor | No tiles; Reports and Activity |

Each person can choose which tiles they see and which group comes first ("Choose what I see"). Under the tiles, **Urgent** lists overdue, due this week and coming up, each with its one main action.

## 13. Visualisations and tracking (first three, then later)

1. **Progress tracker on every contract**: the stepper plus who has it, since when and what happens next. Fixes "where is Legal at".
2. **Renewal radar**: notice deadlines (not just end dates) over the next 30/60/90/180 days, overdue first.
3. **Pipeline**: board view plus counts and average days per stage, so stuck contracts and approvals show up.

Later, if wanted: approval turnaround by approver, value by category and supplier, a per-contract health indicator. Charts only where they help someone decide or act.

## 14. Emails

Sent to Legal (Legal team, or the Legal person whose turn it is), Back to owner, Legal cleared, Waiting for approval (approvers), Approved, Rejected, Variation approved or rejected, Reminder, Escalation, You've been given access. Each links straight to the contract. Wording is editable by admins with a fixed list of placeholders such as {Contract title}, {Reference}, {Owner}, {Due date}, {Link}; history is kept.

## 15. Data model

All tables live in the `contracts` schema of the `JBSContracts` database. Every table has created and updated times (UTC) and who, plus a row version for safe concurrent editing.

| Table | Holds |
|---|---|
| Person | Windows account, name, email, the five role ticks, is Group Head, active |
| Supplier | Name, ABN, vendor number, notes, active |
| Category | Name, parent (for subcategories, one level), active |
| ListItem | Business units, sites, agreement types, currencies: list name, value, order, active |
| Contract | Reference (unique), built-in fields (section 10), stage and since when, whose turn, Legal person, Legal status and note, renewal decision, renews contract, registered date and by, locked, import source and batch |
| FieldDefinition | Key (stable, never reused), built-in or added, kind, label, help, section, position, required, list/filter/export flags, after-Effective rule, choices, hidden |
| FormSection | Title, position, hidden |
| LayoutVersion | Each saved field and section layout (who, when, a copy of the layout), for History and "put back" |
| ContractFieldValue | Contract, field, value (text, number, date, yes/no, person, supplier or list choices) |
| LegalTurn | Contract or Variation, turn number, from, to, who, when, status, note, linked document versions |
| Document | Contract or Variation, kind (draft, signed copy, Legal mark-up, variation, other), version, file name, size, SHA-256, who and when; the file is stored in the server's data folder |
| Approval | Contract or Variation, decision, who acted, as backup for whom, when, reason |
| Variation | Contract, number, reason, changes (field, old, new), stage, outcome |
| Reminder | Contract, kind, due date, sent, acknowledged, unique key |
| Unlock | Contract, admin, reason, expires, closed |
| AuditEvent | Append-only: when, who, contract, action, plain sentence, details (old and new values), previous hash and hash |
| Setting and EmailTemplate | Current value plus a history table |
| HomePreference | Each person's tile choices |
| ImportBatch and ImportRow | Dry-run and load results, row source, matched files (slice 9) |

## 16. Deployment and switching over from the old app (D-19 to D-22)

Facts (server, site, certificate, database, accounts, email relay, runner, variables) are in `docs/jbs/QA_CONFIGURATION_RECORD.md`. Reuse them.

The new app replaces the old one on QA from slice 0. QA has no real data (confirmed by Jeff), so the database is wiped.

**Slice 0 steps:**
1. **Jeff:** in GitHub > Settings > Environments > QA > Deployment branches, add `redesign`.
2. Tag `main` as `legacy-ecp-final` (the old app stays in git history).
3. On the `redesign` branch only, point `ci.yml` and `deploy.yml` at this folder, reusing `iis-deploy.yml`'s shape and the scripts in `scripts/ci/` (site and pool set-up, certificate, Always Running, the data-protection key permission fix, and the failure-log printing).
4. A one-off step in the first deploy removes the old app's tables and old data sub-folders, and only runs when a QA variable `RESET_DATABASE_CONFIRM` equals `JBSContracts`. Jeff deletes the variable afterwards.
5. New QA variables for the new app (first admins, email relay and sender, data folder, logs). The connection string, paths, binding and certificate stay the same. The old `APPENV_Ecp__` variables are removed at the end.
6. **While building, `main` is frozen for code.** A code push to `main` would redeploy the old app over the new one. Docs-only pushes don't deploy.

**When the new app is accepted:** merge `redesign` into `main`; set the QA deployment branch back to `main` only; remove the old app's code (still in history under the tag); set up Production as in section 7 of the configuration record. Production still deploys **only** from a manual run on `main` with promote-to-production ticked and the Production reviewer's approval.

## 17. Build order (each slice deploys to QA for Jeff to try)

| # | Slice | Jeff tries | Status |
|---|---|---|---|
| 0 | Switch-over; app shell (sidebar, JBS brand, light and dark); Windows sign-in; People admin; no-access page; audit table | Opens QA, is greeted by name, adds a person and ticks roles | Not started |
| 1 | Suppliers; lists; add and edit contract with built-in fields; documents; Contracts list and search; contract page; Confidential | Raises a contract with documents and finds it again | Not started |
| 2 | Contract fields and sections (add, move, rename, hide, require, preview, history, put back, after-Effective rule); added fields in forms, list columns, filters and CSV | Adds "Insurance expiry" and "Site contact" fields and sees them on contracts | Not started |
| 3 | Legal back-and-forth; progress tracker; Activity; hand-over emails | Sends to Legal, gets it back, always sees whose turn it is | Not started |
| 4 | Signing; approval and register; lock; phone approve page | Approves from a phone; the contract locks | Not started |
| 5 | Home tiles; Urgent; Choose what I see; Ctrl+K | Their own to-do list | Not started |
| 6 | Reminders job and emails; escalation; renewal radar; renewal decisions; Renew; End; Archive | Gets a reminder email and records a decision | Not started |
| 7 | Variations; emergency unlock | Changes an end date the proper way | Not started |
| 8 | Reports: register CSV and PDF, pipeline, approvals, audit export | Exports the register | Not started |
| 9 | Import: spreadsheet (for example a Medius export) with column mapping, dry run, error report; ZIP of files matched by agreement reference; legacy Effective contracts loaded locked with their original registered date | Loads a fictional sample | Not started |

Help ("?") ships with each screen, not as a slice of its own.

## 18. Not in version 1

DocuSign connection (designed for, D-10); SAP or other system links; limits by business unit or site (D-05); value and turnaround charts; deleting anything; multiple companies, packs or terminology layers.

## 19. Open questions

1. Is there a reversed (white) JBS logo and an SVG version for dark mode and crisp display? Until then, the logo sits on a white tile in dark mode.
2. Does JBS have a brand typeface? Until then, Inter.
3. Should review dates also send reminders (for example 30 days before)? Not included until confirmed.
4. PDF register export: the PDF library must have a licence that suits JBS (some are free only for small companies). Decide in slice 8.
