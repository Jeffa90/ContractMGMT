# Business Administrator guide: day-to-day configuration without code

**Purpose.** Change how an organisation works through the application's own pages: who holds which role, which approver groups decide, what things are called, which optional steps are on, how dates and amounts are shown, and the contract workflow, approval matrix and notification policy. Every change follows the same safe path: draft, check, preview, submit, a second person publishes, with history and rollback. No JSON editing, SQL, source change or Git is needed.

**Who it is for.** A trained, non-developer **Organisation Administrator** or **Business Configuration Administrator** (and a **Tenant Administrator** for tenant-wide tasks), plus the second person who reviews and publishes. To create the organisation first, use ORGANISATION_ONBOARDING_GUIDE.md in this folder.

**Prerequisites.**
- The organisation exists and you hold an administrator role for it (see "Roles and permissions at a glance").
- In Production, and in Test when governance is Enforced, at least **two** people who can publish the kind of configuration you change (see "Who can publish what").
- You can sign in recently enough to publish (the default window is 10 minutes).

**Status label.** Implemented. Read from the page source and services on this branch; the draft, submit, review, publish and form-editor path was exercised over real HTTP by the no-code rehearsal (docs/NO_CODE_REHEARSAL.md, steps 5 to 12). **Not Verified:** an unaided non-developer following this guide; a live identity provider; real e-mail delivery. Nothing here approves an installation for production use; that is external governance.

Examples use three synthetic cases: **Blank** (neutral, no pack), **JBS-style** (reference procurement pack `jbs-procurement`; Australian English, AUD, Australia/Sydney) and **Harborline-style** (a synthetic general contractor on pack `construction-subcontracts`; US, USD, America/Chicago, en-US).

---

## 1. Ground rules that apply to every screen

### 1.1 The path every change takes
1. **Start a draft** (or open the form; saving starts one). The published version stays in force until a draft is published.
2. **Edit**, then **Save**. Saving never publishes.
3. **Save and check** (called **Save and validate** on Terminology, Capabilities and Regional). Problems appear beside the field; a summary at the top links to each.
4. **Preview.** Read what it would change.
5. **Submit for review** (or **Publish**, where no second person is required). A different person then reads **What would change** and publishes.
6. Every publish makes a numbered, permanent release. You can go back by publishing an earlier release again (a new release); history is never edited.

### 1.2 What the buttons do
| Button | Does | Changes what is in force? |
|---|---|---|
| **Start a draft** | Opens a draft from the release in force | No |
| **Save** | Stores the draft. Saving unchanged content changes nothing | No |
| **Save and check** / **Save and validate** | Saves, then checks every rule, reporting problems and notes | No |
| **Add / Move up / Move down / Remove** (form editors) | Changes rows on the screen only. **They do not save.** Press Save before leaving | No |
| **Submit for review** | Puts exactly the saved content forward for a second person. Appears where a second person is required and you are not the one to decide | No |
| **Publish** | Appears where no second person is required: checks, then makes a new release in force | Yes |
| **Review and publish** (link) | Appears to a person who may decide; opens the review page | No |
| **Discard the draft** | Throws the draft away. A new draft starts from the release in force | No |

### 1.3 The second-person rule, explained
Where the installation requires it, no one publishes their own work. **Production always requires it.** **Test** requires it when the System Administrator has set the governance setting to **Enforced** (a **[Technical task: System Administrator]**); Test then applies exactly the Production rule, so it is the place to rehearse governance, and at least two Business Configuration or Organisation Administrators are needed. With the setting left at **Default**, Test keeps the simplified behaviour (publish immediately). **Demonstration** ignores the setting and always keeps the simplified behaviour. The setting can never lower Production. The mode in force (Production, Test enforced, Test simplified or Demonstration simplified) is shown on **/Admin/Health** and **/Admin/Operations/Dashboard**, and is recorded on configuration audit events and releases.
- **Who counts as the author.** The person who created the draft, the person who last edited it and the person who submitted it. None of them can approve, send back or reject it. A reviewer who opens or decides on a draft does not become its editor.
- **Submitting.** Your submission is for the exact saved content. **Any edit after submission voids the submission** (the review page then shows "Changed after it was put forward"); submit again so the reviewer sees what will be published. Saving unchanged content changes nothing.
- **Matching content.** At publication the submitted checksum must equal the draft's checksum. If not, publication is refused ("The content is no longer the content that was submitted for review…") and the refusal is audited.
- **Withdrawing.** Only the author or submitter can **Withdraw my submission**. A reviewer who does not want it **rejects** it.
- **Rollback is also a change.** Wherever the second-person rule applies (Production, and Test with **Enforced**) a rollback is submitted and another person publishes it (see section 11).
- **Recent sign-in (step-up).** Publishing, publishing a rollback and a few tenant-level actions need a recent sign-in. If yours is not recent you are sent to **Confirm it is you**; afterwards you return to the page, and **anything you had not saved is lost**. Save first.
- **Audit.** Every step is recorded: who, what, when and the reason or note.

### 1.4 Who can publish what
| Kind of configuration | Can draft | Can publish (approve) |
|---|---|---|
| Contract workflow, Variation workflow, Approval matrices, Notification policy, Regional settings, Organisation onboarding, Additional fields, Saved views | Organisation Administrator, Business Configuration Administrator (a Tenant Administrator only at tenant level) | Business Configuration Administrator; Organisation Administrator (scoped to their organisation) |
| Terminology | Organisation Administrator, Business Configuration Administrator, Tenant Administrator | The same three |
| Capabilities | Organisation Administrator, Business Configuration Administrator, Tenant Administrator | Organisation Administrator and Tenant Administrator only (not the Business Configuration Administrator) |

This is the role-permission matrix as read for this branch. The review inbox shows the permission needed for each item, in the column **Permission needed to publish**.

### 1.5 Roles and permissions at a glance
| Page | Menu entry | Permission to open |
|---|---|---|
| **/Admin/Users** | Users and roles | users.manage (Tenant Administrator, Organisation Administrator; a Division or Site Administrator within scope) |
| **/Admin/ApproverGroups** | Approver groups | users.manage |
| **/Admin/Terminology** | Terminology | terminology.read |
| **/Admin/Capabilities** | Capabilities | capabilities.read |
| **/Admin/Regional** | Regional settings | config.draft or config.history.read |
| **/Admin/Configuration** | Workflow configuration | config.draft or config.history.read |
| **/Admin/Forms/Edit** | reached from the Workflow configuration page, from the set-up checklist, or from Approval matrices | config.draft or config.history.read |
| **/Admin/Reviews** | Configuration reviews | any draft or publish permission above |
| **/Admin/Approvals** | Approval matrices (shown only while the register authorisation capability is on) | approvals.matrix.read |

A menu entry appears only if you hold its permission. Its words follow the organisation's terminology (for example a pack may call approvals "Approvals" and a variation a "Change Order").

---

## 2. Users and roles

**Navigation.** Menu entry **Users and roles**, page **/Admin/Users**; person's page **/Admin/Users/Details/{person}**.
**Purpose.** See the people you may administer, invite a person, grant and end roles, disable or reactivate an account.
**Who / permission.** users.manage; granting also needs roles.delegate. You can grant only roles and scopes within your own.
**Prerequisites.** For an Approver or Backup Approver role, the approver group should already be registered (section 3).

The People table lists **Name, Email, Status, Current roles**; the name opens the person.

| Control | Req. | Recommended | Rule | If wrong |
|---|---|---|---|---|
| Invite: **Name**, **Email**, **Send invitation** | Both | A real work address | Name up to 200, email up to 254. The invitation is only recorded; **no email is sent**, so tell the person | A wrong email means the person cannot be matched when they sign in; disable the record and invite again |
| Grant: **Role** | Yes | The least role that does the job | Only roles you may grant, and not roles the organisation's terminology hides | An over-wide role gives over-wide access: end it at once and record why |
| **Viewer kind** | For Viewer | Narrowest that fits | All contracts, Category, Business unit, Site or Restricted. A Viewer is never a global reader | A kind needs its matching scope below; the service refuses a mismatch |
| **Applies to** | Yes | **Selected organisations** | Tenant and Installation levels are for tenant-wide and System Administrator roles | Scope too wide: end and regrant |
| **Organisations, Divisions, Business units, Sites, Categories** | Organisation yes | Tick only what applies | Narrowing: access is the intersection of all of them | Narrower than intended: the person sees too little |
| **Classification ceiling** | Yes | The default level | Highest level the person may see. Only a Tenant Administrator can grant the highest levels | Too high exposes confidential records |
| **Approver group** | Approver roles | A registered group | Unregistered keys are marked "(not registered)" | The person approves for the wrong group |
| **Reason** | Yes | Why, in a sentence | Up to 1000 characters | Blank is refused |
| **Grant role** | | | Success: "Role granted." | |
| **End assignment** with a **Reason** | Reason required | | Success: "Role assignment ended." | |
| **Disable account** (reason) / **Reactivate account** | | | Success flashes "Account disabled." / "Account reactivated." | |

**Worked examples.**
- Blank: grant Category Owner to Sam Owner for Example Organisation, category General, ceiling Internal; reason "Owns General".
- JBS-style: grant the Approver role to a Group Head of Procurement delegate, approver group `GroupHeadProcurement`, ceiling Confidential; or Backup Approver for the backup.
- Harborline-style: grant Package Manager (the pack's name for the Category Owner role) for the Concrete trade package; grant Approver in group `ProjectDirectors`.

**Common errors.** "Your role does not allow you to grant roles."; "The role or scope is above what the caller may grant."; a blank reason.
**Evidence.** The Role assignments table (role, scope, ceiling, effective dates, granted by) and the audit entry.

## 2A. People & Access (the guided way; UX-3, UX-6, UX-7)

Most administrators never need section 2. **Administration, People & Access, People** (`/Admin/People`) lists everyone you may manage with the kind of access each has.

* **Review before anything is given.** Both **Add a person** and **Change access** end in **Review access**: *This person will be able to* (with where), *This person will not be able to* (for example formal review, approving their own submissions, records above their classification ceiling), and a summary (organisation, profile, ownership, classification ceiling, approval memberships, administration access). Tick **I have read this and I want to give this access.** to save; without the tick nothing is given. Role names and codes appear only if you press **Show advanced details**.
* **Add a person** (`/Admin/People/Access`): name and email, what they will do (Contract Manager, Contract Administrator, Reviewer, Approval Authority, Read Only, Organisation Administrator, Business Configuration Administrator, Auditor; you see only what you may give), where they can work, which categories they own, the highest classification they may see, and for an approval authority the approver group. The last step says in plain English what the person will be able to do and not do, and what saving will add. Saving needs a recent sign-in. Adding the person and giving the access is one step: if the access is refused, the person is not created.
* **Change access** on a person adds to what they have; **End this access** on the person's page ends exactly what a guided save created. Access given any other way is left alone.
* **Allow demonstration sign-in** (Demo and Test only; absent in Production) creates a synthetic test person with no password who can be chosen on the demonstration sign-in page.
* **Preview as this user** (Demo and Test only) lets you see the application as that person sees it, using their real access. It is read-only, lasts 30 minutes, shows a banner "TEST MODE: Previewing as ..." with **Exit user preview**, and is recorded. You can preview only a person whose access you could give yourself.
* **My access** (`/Access`) tells any signed-in person what they can do, where and why, and what they cannot do and what kind of access would give it.
* **Set up my access** (`/Access/Setup`), for an administrator who cannot yet create anything: in Demo and Test, **Give me full Test-mode access** says exactly what it grants, then applies it (and can be removed). In Production nobody can give themselves access; ask another administrator.
* **Categories and owners** (`/Admin/Categories`): a primary and a backup owner per category, chosen from everyone you manage; **Own all current and future categories** shows a preview first and is a standing instruction: a category added later is recognised automatically, in the same moment it is created. Ending it (the *Owns every current and future category* box on the same page) ends only the ownership it created, never ownership given another way. Tick several rows to set them together. A person without the owner role is given it in the same save.
* **Approval authorities** (`/Admin/ApprovalAuthorities`): choose who can approve and who stands in for them, by name. The authorities your workflow already names (for example "Group Head of Procurement") are listed with nobody in them; pick the members and backups, review the plain-English summary, tick to confirm, save. You never type a group key; it is shown only if you press *Show the machine keys* under Advanced. **Approver groups (advanced)** below is still there for registering a group by its key.
* **Identity group mappings** (`/Admin/People/Mappings`, Advanced, tenant administrators) give access automatically to a group from your single sign-on. They are limited to reading, reviewing, auditing, owning and administering contracts and can never carry administrator or approval access.


## 3. Approver groups

**Navigation.** Menu entry **Approver groups**, page **/Admin/ApproverGroups**; one group at **/Admin/ApproverGroups/Details?key=**.
**Purpose.** An approver group is *who decides an approval*. Workflows and approval matrices name a group by its key; the members are the people holding the Approver or Backup Approver role under that key.
**Who / permission.** users.manage (a Tenant Administrator may register groups for all organisations; an Organisation Administrator for theirs).
**Prerequisites.** The people exist and are active.

The list shows **Group, Description, For, Approvers, Backups, Relied on, Status, Open**. Status is Not registered (people hold the key but it was never registered), Retired, No members, or In use.

### Register an approver group
| Field | Req. | Rule / example | If wrong |
|---|---|---|---|
| **Group key** | Yes | Letters, digits, full stops, hyphens and underscores, starting with a letter or digit, up to 100. **Permanent**. Blank: `Approvers`; JBS-style: `GroupHeadProcurement`; Harborline-style: `ProjectDirectors` | A wrong key cannot be edited: register the right one and retire the wrong one if nothing relies on it |
| **What this group is for** | No | Up to 300 characters | Cosmetic |
| **Belongs to** | Yes | One organisation, or "All organisations" if you cover everywhere | A group for one organisation cannot take members of another |
| **Members may delegate their approvals to someone else for a period** | No | Ticked by default | Unticking stops delegation for this group |
| **Register the group** | | Success: "Approver group … is registered. Add its members next." | "An approver group with that key is already registered." |

The name people read comes from **Terminology** (section 4), not from this page.

### Add and end members (on the group's page)
| Step | Fields | Result |
|---|---|---|
| **Add a person** | **Person**, **As** (Approver or Backup approver), **For** (organisation), **Highest classification they may see**, **From** and **Until** (optional, UTC) | Press **Preview the effect** |
| Preview | Shows **Who can approve now** and **Who could approve afterwards**, what relies on the group, and any refusal (for example "cannot sign in, so cannot approve") | Nothing is saved yet |
| Confirm | **Reason (kept in the audit trail)**, then **Add them to the group** | Success: "The person is added to the group." |
| **Preview ending**, then **Reason** and **End the membership** | Same before/after view | Success: "The membership has ended." |
| **Save the settings** | Description and delegation switch | "The group's settings are saved." |
| **Retire the group** (reason) / **Make the group active again** | Retire only when nothing relies on it and nobody is in it | "This group cannot be retired while it is relied on…" or "…still has N active member(s). End their membership first." |

**Protections.** You cannot end the last member of a group that something relies on ("That would leave the group with nobody to approve while it is relied on… Add someone else first."). An unregistered key is shown with a **Register and manage** link.
**Evidence.** The group's Members table, and the preview text before and after each change.

## 4. Terminology

**Navigation.** Menu entry **Terminology**, page **/Admin/Terminology**; draft at **/Admin/Terminology/Edit**; also **Preview**, **Version history**, **Audit history**.
**Purpose.** Change the words people read (what a contract, supplier or review is called). **Labels only**: terminology never changes permissions, workflows or records. Each label has a stable key that never changes.
**Who / permission.** terminology.draft to edit; terminology.publish to publish; terminology.read to read.
**Prerequisites.** None. The order of wording is: this organisation's published terminology, then its pack's wording, then the platform's neutral wording.

The overview shows the organisation, locale, pack, published release, any open draft, and a table of every label with its source (Organisation override, Copied from pack, Pack default, Platform neutral), plus **Approver group names** and **Roles offered when assigning**.

| Field (per row, on **Edit**) | Req. | Rule | If wrong |
|---|---|---|---|
| **Locale being edited** + **Show this locale** | | Save first; switching does not carry unsaved changes | Unsaved edits lost |
| **Use my own wording for {key}** (tick) | | Unticked rows inherit the pack, then the platform | Problems are reported beside the row when you press Save and validate |
| **Singular**, **Plural** | If ticked | Up to 80 characters | Misleading words on every page |
| **Short label** (30), **Description** (300), **Help text** (600) | No | Optional, under a fold-out | Cosmetic |
| **Approver group names**: **Name shown**, **Description** (and, for a group not listed, **Stable group key**, typed exactly as workflows and roles write it) | No | Up to 80 characters; blank inherits the pack's name or shows the key | A mistyped key names a group nobody has |
| **Roles offered when assigning** (per role: Inherit, Offered, Not offered) | No | Only tidies the pickers; administrative roles are always offered | Hiding a role only removes it from pickers; nobody loses access |
| **Note for the history (optional)** | No | Up to 500 | |

**Buttons.** **Save the draft** (flash "The draft is saved. Nothing is published…"), **Save and validate** (flash "The draft is valid. Preview it, then publish when you are satisfied."), **Preview** (representative pages "Now" against "After publishing", role names, group names), **Submit for review** or **Publish** (success "Published. People see the new wording from their next page. Permissions, workflows and records are unchanged." or "Submitted for review…"), **Discard the draft**.

**Examples.** Blank: tick `contract`, Singular "Agreement", Plural "Agreements". JBS-style: keep the pack's "Agreement" and "Group Head of Procurement"; name the group key `GroupHeadProcurement` "Group Head of Procurement". Harborline-style: the pack already says "Subcontract", "Trade Package", "Project Director"; name `ProjectDirectors` "Project Directors".
**Evidence.** The Preview page, the Version history row, the review entry.

## 5. Capabilities

**Navigation.** Menu entry **Capabilities**, page **/Admin/Capabilities**; draft at **/Admin/Capabilities/Edit**; **Preview**, **Version history**, **Audit history**.
**Purpose.** A capability is an optional step of the process. The five are: **Legal review**, **Register authorisation** (approval of an executed contract), **Variations**, **Renewals** and **Signature requests**. Switching one off removes the step from pages, lists and lifecycle for this organisation. **It never changes who may do anything.**
**Who / permission.** capabilities.draft to edit; capabilities.publish to publish (Organisation Administrator or Tenant Administrator only).
**Prerequisites.** A capability can be switched off only when no published workflow uses it and nothing is under way in it. Variations needs Register authorisation.

The overview table shows **Capability, In force, Source, Pack default, Published choice, Draft, Needs**, and a **Compatibility checks** panel (Problem or Note rows).

| Field (per capability) | Req. | Rule | If wrong |
|---|---|---|---|
| **Choice** | Yes | **Inherit** (the pack's default, otherwise on), **On**, **Off** | Off with work in progress, or while a workflow uses it, is refused when checked |
| **Reason (optional)** | No | Up to 300 | |
| **Note for the history (optional)** | No | Up to 500 | |

Buttons as for Terminology (**Save the draft**, **Save and validate**, **Preview**, **Submit for review** or **Publish**, **Discard the draft**). Success text: "Published. The pages, lists and lifecycle follow the new choices from the next request. Permissions and records are unchanged."
**Examples.** Blank: leave all on (inherit). JBS-style: all five on. Harborline-style (the pack's defaults): Legal review off ("Formal Review" is not used), Renewals off, Register authorisation on ("Activation Approval"), Variations on ("Change Orders"), Signature requests on.
**Evidence.** The in-force table and the Compatibility checks panel.

## 6. Regional settings

**Navigation.** Menu entry **Regional settings**, page **/Admin/Regional** (organisation selector when you have more than one).
**Purpose.** The locale, currency and time zone that dates, times, numbers and amounts are shown in; the counterparty tax-identifier types; and the address fields. A pack proposes values; the organisation decides, and its published choice always wins. Times are stored in UTC and shown in the time zone. A locale is a language-and-region format, not the language of the page text. Only the structure of a tax identifier is checked: nothing is looked up in a registry.
**Who / permission.** config.draft to edit, config.history.read to read history; config.publish to publish.
**Prerequisites.** Until locale, currency and time zone are all decided, the page shows "Not complete" and the organisation is reported as not ready for Production.

| Field | Req. | Recommended | Rule | If wrong |
|---|---|---|---|---|
| **Country or region** | No | Choose, or "(inherit)" | List | |
| **Locale** | Decide | en-GB / en-AU / en-US | Up to 35 characters; a language-and-region tag | Wrong date and number formats |
| **Supported locales** | No | Comma separated | Up to 400 | |
| **Default currency** | Decide | GBP / AUD / USD | Offered when an amount is entered. **Changing it never converts or rewrites an amount** | |
| **Supported currencies** | No | JBS-style: AUD, USD, NZD; Harborline-style: USD, CAD | Comma separated | |
| **Time zone** | Decide | Europe/London / Australia/Sydney / America/Chicago | An IANA name; daylight saving follows the zone | Displayed times shift |
| **Time display** | No | (inherit) | Locale's habit, 24-hour or 12-hour | |
| **Phone region** | No | AU / US | Two letters | |
| **Tax identifier types**: tick **Use my own list instead of the pack's**; **Add a profile the installed packs offer**; per type **Type key, Label shown, Country, Rule, Length, Mask, Checksum, Required, No two … share a value, Classification** | No | JBS-style: ABN, rule digits, length 11. Harborline-style: Federal Tax ID, rule mask `##-#######` | Closed list of rules; # digit, A letter, * either | A wrong mask rejects good values |
| **Address fields**: tick **Use my own choice instead of the pack's**; per field **used** and **Required** | No | Pack's choice | Free text; no postal format checked | |
| **Note for the history (optional)** | No | | Up to 200 | |

**Buttons.** **Save draft** (flash "The draft is saved. Nothing is published…"), **Save and validate**, **Validate and publish** / **Submit for review**, **Discard the draft**. A **Preview of the form as it stands** panel shows sample values in the settings the form would give. History and rollback sit at the bottom (section 11).
**Evidence.** The preview panel and the release row.

---

## 7. The form editors: common layout

**Navigation.** Menu entry **Workflow configuration** (page **/Admin/Configuration**), tab for the kind, button **Edit with the form**; or the **Open** link on the set-up checklist; or on the **Approval matrices** page the tab **Edit (draft and publish)**. Page **/Admin/Forms/Edit?kind=…&organisationId=…** where kind is WorkflowContractLifecycle, WorkflowVariation, ApprovalMatrix or NotificationPolicy.
**Purpose.** Edit these documents with typed fields and buttons. You never see or type a document. Every choice comes from a closed list; there are no formulas, code or free templates.
**Who / permission.** config.draft or config.history.read to open (read-only without a draft); config.draft to edit; config.publish to publish.
**Prerequisites.** The set-up for the organisation exists; the pack's drafts were prepared (ORGANISATION_ONBOARDING_GUIDE.md, Part E).

**On the page.**
1. Tabs: **Contract workflow**, **Variation workflow**, **Approval matrices**, **Notification policy**. Organisation selector and **Show**.
2. A line "In force: release N published by …" with **History and rollback**; or "Nothing has been published yet."
3. When no draft is open the form is read-only: "This is what is in force. To change it, start a draft…" with **Start a draft**. With a draft: "Draft last edited by …".
4. **Fix these before you submit: (N)**: a red summary whose items link to the field; **Worth a look:** notes that do not block.
5. The form, in sections. A list of rows (stages, moves, matrices, rows, steps, events, messages) shows each row as a boxed group titled with its name, with **Move up**, **Move down**, **Remove**, and **Add …** below the list. **Remember: these buttons act on the screen only; press Save.**
6. **Save, check and submit**: **Note for the release history** (up to 500; "Say what changed and why. A second person reads it when they review."), then **Save**, **Save and check**, **Submit for review** or **Publish**, **Discard the draft**.
7. **Preview and impact** (below the form; it reads the form as it stands, saved or not): **What it would affect**, and tables such as "What the workflow does", the matrix rows with who approves, or "What would be sent" for a sample contract. For approval matrices there is also a link **Try a sample submission in the simulator**.

**Success texts.** Start: "A draft is open. Change the fields, check it, then submit it." Save: "The draft is saved. Nothing is published. Check it before you submit it." Check: "The draft is valid. Read the preview and the impact below, then submit it." Publish: "Published. Records created from now on follow it; records already in progress keep the release they started under." or "Submitted for review…", after which you land on **Configuration reviews** under "Put forward by me".
**Common errors.** A number field that is not a number is shown beside the field and Save is blocked; problems from the check link to the field. **Recovery:** fix and press **Save and check** again.

### 7.1 Contract workflow and Variation workflow

The two share one form. A Variation is a controlled change to an effective contract. Records in progress keep the release they started under; changes apply to new records.

**Section "About this workflow"**
| Field | Req. | Meaning, example |
|---|---|---|
| **Workflow code** | Yes | Short name, for example contract-lifecycle (the Variation workflow has its own code) |
| **What it describes** | Fixed | Contract or Variation |
| **Description** | No | Free text for your records |

**Section "Stages"** (rows): a state a record can be in. Exactly one is the starting stage; a final stage has no way out; every other stage needs at least one way out.
| Field | Meaning |
|---|---|
| **Stage code** (required) | Letters and digits only. Moves refer to it: do not change once records use it. Example Draft |
| **Name people see** (required) | Shown on screens |
| **What this stage means to the platform** | A choice from a closed list (for example waiting for Legal, awaiting register authorisation, in force). Leave blank for a plain stage |
| **Starting stage**, **Final stage**, **Locked**, **Freeze content** | Ticks. A locked record cannot be edited except by an authorised Variation; the starting stage cannot be locked |
| **Documents in this stage** | Open, LegalOnly or Closed |
| **Reason shown when locked** | Up to 200 characters |

**Section "Moves between stages"** (rows): which stages a move starts from, where it goes, who may do it and what must be true first.
| Field | Meaning |
|---|---|
| **Move code** (required), **Name people see** (required) | Code letters and digits; the name is the button label, for example "Submit for review" |
| **Starts from** | Tick every stage the move can start from |
| **Goes to** (required) | The stage afterwards |
| **Who may do it** (required) | A permission chosen from the catalogue; the roles holding it are those in the permission catalogue |
| **What the move does** | Ordinary (no side effect), or one of the closed list of special moves (send to Legal, send for signature, submit for register authorisation, approve, reject, raise or apply a variation, start or complete a renewal, and so on). Special moves are carried out by the step that owns them |
| **Checks that must pass first** | Ticks from a closed list with plain-language meanings (for example "The approver is not the person who submitted it"). An approving move must have both the authorised-approver and submitter-is-not-approver checks |
| **Ask for a comment**, **Ask for a reason** | Ticks |
| **Fields that must be filled in**, **Document types that must be attached** | Choices or one per line |
| **What this move is for** | Free text, up to 500 |

**Section "Settings"**
| Group | Fields |
|---|---|
| Documents | Signed-copy document type (required); final document types; Legal mark-up type (required); approval evidence type; document types people may upload (blank allows all except the signed copy) |
| Approval | **Approver group** (required; pick from the Approver groups list: Blank `Approvers`, JBS-style `GroupHeadProcurement`, Harborline-style `ProjectDirectors`); **Require a signed copy** (tick); **Approval matrix** (optional; when chosen it decides who approves instead of the single group) |
| Legal review | Ask Legal for a reason when returning for rework |
| Emergency unlock | Allow an authorised unlock (time-limited, needs a reason and a recent sign-in, audited); **Default length** and **Longest length** in minutes (5 to 1440); the second-person unlock option is marked "Not supported yet. Leave this off." |
| Renewals | **Successor starts in** (not a locked stage); fields and document types copied to the successor (dates, status and history never copied); **Successor start date** rule |
| Variations | **Variation workflow code** (must differ from this workflow's own code); **Legal review of a variation** (None, Optional, Required); **Fields a variation may change** (one per line) |

**What the check looks for.** Exactly one starting stage; every non-final stage has a way out; moves refer to real stages; approving moves carry the safeguards; a named matrix exists. **The impact panel** lists new and removed stages and moves, changed permissions, and counts of approvals and Legal reviews now under way.

**Examples.** Blank: change one button label ("Submit for review" to "Send for checking"), Save and check, submit. JBS-style: keep the Legal review stages; set the approver group `GroupHeadProcurement`. Harborline-style: no Legal stages in use (capability off); the approver group `ProjectDirectors` or a matrix named `subcontract-activation`.

### 7.2 Approval matrices

**Purpose.** Who must approve a submission. A matrix has rows; the first row that fits (by value, category and so on) says which approver groups decide, and in what order. **Names groups, never people.** A workflow opts in by naming a matrix code. Overlaps, gaps and ties are found when you check.

**Section "Matrices"** (rows)
| Field | Meaning, example |
|---|---|
| **Matrix code** (required) | Lower-case letters, digits, hyphens, up to 50. Example subcontract-approvals |
| **Name** (required), **Description** | Up to 200 / 1000 characters |
| **Currency** (required) | Three letters. Value ranges are in this currency (AUD, USD) |
| **In force from**, **In force until** | Dates; blank for none |
| **Exchange rates** (rows) | **Foreign currency**, **Rate** (units of the matrix currency per one unit), **From**, **Until**. Without a rate table, a submission in another currency is blocked, never silently converted |
| **Rows** (rows) | see below |

**Each row**
| Field | Meaning |
|---|---|
| **Row code** (required), **What this row covers** | Lower-case letters, digits, hyphens. Example up-to-250k |
| **Kind of row** (required) | Base: says who approves. Exception: adds to, or replaces, the steps of the base row that also matches |
| **What an exception does** | AddSteps or ReplaceSteps; only for exceptions |
| **Priority** | 0 to 10000. The higher number wins when two rows fit. Two rows that can both fit must not share a priority |
| **In force from / until** | Dates |
| **When it applies**: **Value from (included)**, **Value up to (excluded)** (ranges include the lower figure and exclude the upper, so the next row starts where this one ends), **Categories** (with **Include subcategories**), **Contract types**, **Legal entities**, **Business units**, **Sites**, **Classifications**, **Risk levels**, **Counterparty types**, and **Conditions on additional fields** (field key, "Is" equal / not equal / one of / none of / at least / less than, values) | Empty means "any". No formulas |
| **How the steps run** | Sequential (one after another) or Parallel (at the same time) |
| **Steps** (1 to 10 rows) | **Step key**, **Name people see**, **Approver group** (required, pick from the list), **Backup group** (optional, different), **Who must respond** (FirstResponse or AllRequired), **Escalate after (days)** (0 to 365), **Escalate to**, **Backup approvers may respond** (yes, no, follow the mode) |

**Examples.** Blank: one matrix `general-approvals`, one base row (no conditions), one step with group `Approvers`. JBS-style: one step with group `GroupHeadProcurement`. Harborline-style: the pack's value tiers, for example rows `sub-small`, `sub-medium`, `sub-large` with steps for `ProjectDirectors`, `CommercialManagers`, `RegionalDirectors`, and an exception row `sub-high-risk` adding a `SafetyRisk` step.

**Before you submit.** Use **Try a sample submission in the simulator** (page **/Admin/Approvals/Simulate**): choose the matrix, set **Version to try** to your draft, enter a sample value, category and so on, and read which rows matched and who would approve. Nothing is submitted; each run is recorded. The approvals pages also show **Matrices**, **Delegations** and **History and audit**.
**Impact panel.** Lists new and removed matrices, rows added, removed and changed, any workflow that names a matrix this draft removes (submissions would be blocked), and the number of approvals waiting (each keeps the plan it was given).

### 7.3 Notification policy

**Purpose.** Who is told about what, how, in what words, and when reminders are sent. Message wording is plain text with placeholders from a closed list shown beside the box; nothing else.

| Section | Fields |
|---|---|
| **About this policy** | **Description** |
| **Reminders** | **Default warning time (days)** (must be one of the allowed times); **Warning times a contract may choose** (whole numbers 1 to 3650, comma separated, each once, for example 30, 60, 90, 120); **Remind about review dates**; **Remind about end dates**; **Notice deadlines**: **Send notice-deadline reminders**, **Remind this many days before the deadline** (for example 30, 14, 7, 1), **Extra warning time for critical contracts (days)**; **Open-ended contracts**: **Do not remind about an end date**, **Still remind about the review date**; **Keep escalation details for (days)**; **Who is told when nobody owns the category** (ticks; if none is ticked the reminder is not sent and the scan tries again later) |
| **What a message may reveal** | **Hide the title from this level upwards**; **Say nothing from this level upwards** (must be at or above the first) |
| **Delivery** | **Attempts** (1 to 20); **Wait between attempts (minutes)** (for example 1, 5, 30, 120, 720) |
| **Who is told about what** (rows, one per event) | **Event** (required, from a closed list with plain meanings, such as "Legal cleared it", "Approved", "Reminder: end date approaching"); **Who is told** (at least one, from a closed list such as category owners, submitter, assigned Legal, approver group); **How** (InApp is the inbox; Email needs the mail settings to be working; a mandatory message must include InApp); **Wording** (one of the messages below, required); **Mandatory** (people cannot switch it off). An event with no row sends nothing |
| **Wording of messages** (named rows) | **Message name** (letters and digits); **Subject** (up to 300); **Body** (up to 3000). Placeholders only from the list shown beside the box, written like {{contract.reference}} |

**Examples.** Blank: default warning 90 days; allowed 30, 60, 90, 120; review and end-date reminders on. JBS-style: add a row for "Approved" to the submitter by InApp and Email using a message named ApprovedNotice. Harborline-style: reword the "Change order approved" message.
**Impact panel.** Shows "What would be sent" for a sample contract (reference REF-000123, 250,000 AUD, due 31 March 2027), messages no event uses, events with no line (nobody told), and who will start or stop being told. A published policy applies to every message sent afterwards; it is not pinned to contracts.
**Evidence for all three editors.** The check result, the Preview and impact panel (screenshot), the note you wrote, and the review entry.

---

## 8. Other configuration pages not detailed here
The **Configuration Studio** (menu entry **Configuration Studio**, page **/Admin/Studio**) manages additional contract fields, choice lists and saved views, with the same draft, check, submit, review and rollback path. The old raw document page **/Admin/Configuration** is a **read-only diagnostic in Production** (editable only in Demonstration and Test) and still hosts the release history and **Publish again** rollback for the four form kinds. Pack handling is on **/Admin/Packs** (the business administrator uploads, reads the checks, installs and assigns; signing and key registration are technical tasks: **[Technical task: Pack Publisher]**, **[Technical task: System Administrator]**).

## 9. The review inbox

**Navigation.** Menu entry **Configuration reviews**, page **/Admin/Reviews**; one item at **/Admin/Reviews/Details?id=**.
**Purpose.** Changes are put forward by one person and published by another. This list shows what is waiting for you, what you put forward, and what was sent back. You read a plain-language summary; you never read a technical document.
**Who / permission.** Anyone with a draft or publish permission for any kind. The decision buttons appear only if you may publish that kind **and** did not create, last edit or submit it.

**Tabs.** **Waiting for my decision** (default), **Put forward by me**, **Sent back or rejected**, **Everything I can see**.
**Columns.** What (for example Contract workflow, Approval matrices, Terminology; a rollback shows "(puts back release N)"), For, Put forward by (and last edited by), When, Changes, Checks (Valid, or N problems and warnings), Status (Waiting for review, Sent back for changes, Rejected; "yours; another person decides" on your own), the permission needed to publish, and **Review** (if you may decide) or **Open**.

**The review page.** Sections: summary (status, created by, last edited by, put forward by, their note, what it replaces, permission needed); a notice if "A newer release was published after this change was started"; **Checks**; **What would change** (table: Where, Change, Now, Would become); **Your decision**; **History** (a timeline of put forward, withdrawn, rejected, sent back, "Changed after it was put forward", published); and for authorised people a **Technical detail** fold-out showing checksums.

| Control | Shown to | Does |
|---|---|---|
| **Note for the release history (optional)** | A person who may publish | Up to 500 characters |
| **Comment** | A person who may decide, or withdraw | Up to 2000. **Required to reject or send back**; kept with the change for good |
| **Approve and publish** | A person who may publish and did not author it | Publishes exactly what was put forward. Needs a recent sign-in. Success: "Published as release N. It is in force now." |
| **Send back for changes** | Same | The author edits and submits again. Flash: "Sent back for changes. Your comment is kept with the change." |
| **Reject** | Same | Flash: "Rejected. Your comment is kept with the change." |
| **Withdraw my submission** | The author or submitter | Keeps the draft but takes it off review |
| **Open the editor to change it** | Author, after a send-back or rejection | Goes to the right editor |

**The author never sees the approve button.** On their own item they see the explanation instead. **If the draft was changed after submission,** publication is refused until it is put forward again.
**Common errors.** "A second person must review this. The person who created, last edited or submitted it cannot decide on it." / "This draft is not waiting for review, or its content has changed since it was submitted." / "A comment is required." **Recovery:** ask the author to submit again, or find another reviewer with the right permission (the column **Permission needed to publish** names it).
**Evidence.** The review page (summary, What would change, History), the comment text, and the release number.

## 10. Reading the history and audit
Each area has a version history: **Terminology** and **Capabilities** have **Version history** and **Audit history** buttons; the form editors link **History and rollback** to **/Admin/Configuration**; Approval matrices have **History and audit** at **/Admin/Approvals/History**; Studio has **Versions and rollback**; Regional has a history table on its own page. A release row shows its number, when (UTC), who, the note and a short checksum. The **Audit events** page (menu entry **Audit events**, for auditors and administrators) holds every draft, submission, decision, refusal, publication and rollback.

## 11. Rollback (publish an earlier release again)

**Navigation.**
- Workflow, Variation, Approval matrices and Notification policy: **/Admin/Configuration**, choose the tab and organisation, scroll to **Release history**, press **Publish again** on the row you want, with a **Reason** (required, up to 300) typed in the same row.
- Terminology: **Version history** page, section **Restore an earlier release**: **Release to publish again**, **Reason (recorded)** (up to 1000), **Publish it again**.
- Capabilities: **Version history** page, **Reason** and **Restore** on the row.
- Regional: the history table's form: **Publish the content of release**, **Reason**, **Roll back**.
- Onboarding: **Go back to release**, **Reason (required)**, **Roll back** (see ORGANISATION_ONBOARDING_GUIDE.md).
- Configuration Studio: its **Versions and rollback** page.

**Who / permission.** The rollback permission for that kind (config.rollback, terminology.rollback, capabilities.rollback). **Needs a recent sign-in.**
**What it does.** It **creates a new release** with the old content. It never edits or removes history. Records in progress keep the release they started under; terminology rollbacks change no permission or workflow.
**Where the second-person rule applies (Production, and Test with Enforced governance)** the rollback is **submitted for a second person**: the flash reads "Submitted for review. A second person has to review and publish the rollback; nothing changes until they do." It appears in the review inbox as "(puts back release N)". Where it does not apply (Demonstration, and Test with Default governance) it is published at once: "The earlier release is published again as a new release. History is kept."
**Errors.** A blank reason; "release not found"; the sign-in step-up; a pack or capability that the old content needs but is now off (the check reports it).
**Evidence.** The history table showing the new release marked "republishes N", the reason, and the review entry.

---

## Evidence to keep
- For each change: the note you wrote, the **Save and check** result, a screenshot of **Preview and impact** (or the Preview page), the review page (**What would change**, **History**), and the release number.
- For roles and groups: the Role assignments table and the group's Members table before and after, with your reason.
- For rollbacks: the reason and the new release row.
- Keep these with your change record. Do not paste passwords, secrets or real customer data into notes.

## If something goes wrong
| Symptom | What to do |
|---|---|
| The page shows a red **Fix these before you submit** list | Open each link; fix; **Save and check** again |
| **Publish** or **Approve and publish** sends you to **Confirm it is you** | Confirm, then return; if the form is empty or reset, the unsaved changes were lost: re-enter |
| "A second person must publish…" | You authored it. Ask another eligible person; check the review column for the permission needed |
| You edited after submitting and the reviewer cannot publish | Submit again |
| A rollback is waiting | A second person must publish it from **Configuration reviews** |
| A raw terminology marker such as double square brackets around a word is visible on a Setup, Onboarding, Pack Manager or Administration page | That is a defect, not something to fix by editing wording. A neutral marker is shown and a stable code is logged; report the page and the time to the System Administrator |
| A button is missing | You lack the permission, or you are the author. The review page explains |
| Pages say the system is in maintenance | Changes are paused until a System Administrator ends maintenance (**[Technical task: System Administrator]**) |
| A pack, e-mail or sign-in problem | **[Technical task: System Administrator]** / **[Technical task: Pack Publisher]**. Do not work around it |
| You cannot undo something | Roll back (section 11); never edit stored documents or the database |

Related: docs/IMPLEMENTATION_PLAYBOOK.md (sections 8 to 13), docs/TERMINOLOGY.md, docs/CAPABILITIES.md, docs/REGIONAL_SETTINGS.md, docs/APPROVAL_MATRIX.md, docs/CONFIGURATION_STUDIO.md, docs/LIFECYCLE_AND_NOTIFICATIONS.md, docs/NO_CODE_REHEARSAL.md.
