# Organisation onboarding guide: create an organisation and bring it to the point of configuration

**Purpose.** Create a new organisation inside an existing tenant, give it its first administrator and its people, prepare its draft configuration from a solution pack, and describe who owns what and who may see what (the Onboarding wizard). Everything is done through the application's pages. No JSON editing, SQL, source change or Git is needed.

**Who it is for.** A trained, non-developer **Tenant Administrator** (creates the organisation and grants first roles) and the new organisation's **Organisation Administrator** (runs the checklist and the Onboarding wizard). A second person with publishing rights is needed in Production. The day-to-day configuration that follows (terminology, capabilities, regional settings, approver groups, workflow, approval matrix, notification policy, reviews) is in the Business Administrator guide, BUSINESS_ADMIN_GUIDE.md in this folder.

**Prerequisites.**
- The installation is set up, and you can sign in. Installation, identity provider, secrets and backups are technical tasks for the System Administrator and are not covered here.
- You know the facts in the data collection workbook (docs/IMPLEMENTATION_PLAYBOOK.md, section 4): names, key, country, locale, time zone, currency, structure, categories, owners, viewers.
- The people who will administer or own things already have a person record (see Part C). Only a person who has signed in at least once (status Active) can be chosen as the initial administrator.

**Status label.** Implemented. Read from the page source and services on this branch, and the creation, checklist, pack-assignment, reviews and publication steps (not the Onboarding wizard) were also exercised over real HTTP by the no-code rehearsal (docs/NO_CODE_REHEARSAL.md, steps 1 to 4 and 12). **Not Verified:** a run by an unaided non-developer following this guide; a live identity provider. Nothing in this guide approves an installation for production use; that is external governance.

---

## The journey at a glance

| Part | Screen (menu entry, then page) | Who | Permission |
|---|---|---|---|
| A | Organisations, page **/Admin/Organisations** (widen once) | Tenant Administrator | organisations.manage |
| B | Organisations, page **/Admin/Organisations/New** (Check, then Create) | Tenant Administrator | organisations.manage |
| C | Users and roles, page **/Admin/Users** and the person's page | Tenant Administrator (or Organisation Administrator within their organisation) | users.manage |
| D | Set-up checklist, page **/Admin/Organisations/Setup?id=** | Tenant Administrator, or Organisation Administrator | organisations.manage, or onboarding.run for that organisation |
| E | Packs, page **/Admin/Packs/Assign** | Organisation Administrator | packs.assign |
| F | Onboarding, page **/Onboarding** and its steps | Organisation Administrator | onboarding.run, plus organisation-level configuration rights |
| G | Configuration reviews, page **/Admin/Reviews** | A second person with publishing rights | see the Business Administrator guide |

**Design points to know before you start.**
- Creating an organisation **publishes nothing**. Every setting stays a draft until a person other than the author publishes it (in Production, and in Test when governance is Enforced; see the next point).
- **Governance in Test.** In Production the second-person publish rule is always on. In **Test** it is on only when the System Administrator has set the governance mode to `Enforced` (a **Technical task: System Administrator**; the setting is `Ecp:Governance:SecondPerson`, `Default` or `Enforced`). `Enforced` applies exactly the Production rule: the author, last editor and submitter cannot publish, a submission is bound to the content submitted, and a rollback is also submitted for a second person. With `Default`, publishing is immediate and simplified. In **Demo** the setting is ignored and governance is always simplified. Ask the System Administrator which mode applies; it is shown on the setup report and the Health and Operations dashboard pages. To rehearse governance in Test you need at least two Business Configuration or Organisation Administrators. Nothing in Test is approved for real confidential data.
- A Tenant Administrator holds no organisation-level configuration rights by design. After creation, the organisation's own administrator does the configuration.
- Organisations are never deleted. Their audit history stays whole.
- Recent sign-in (step-up): widening, creating and resetting set-up each send you to a **Confirm it is you** page if your sign-in is not recent (the default window is 10 minutes). After you confirm, you return to the page with an empty form and must re-enter and re-Check. See "If something goes wrong".

Use these three example sets wherever a screen takes input. All names are synthetic.

| | **Blank** | **JBS-style** | **Harborline-style** |
|---|---|---|---|
| Pack | none (neutral, no business defaults) | `jbs-procurement` | `construction-subcontracts` |
| Display name | Example Organisation | Example Procurement | Harborline Construction |
| Legal name | Example Organisation Limited | Example Procurement Pty Ltd | Harborline Construction, Inc. |
| Key | EXO | PROC | HBL |
| Country | (not set) | Australia (AU) | United States (US) |
| Locale | en-GB | en-AU | en-US |
| Time zone | Europe/London | Australia/Sydney | America/Chicago |
| Currency | GBP | AUD | USD |

For Blank, locale, time zone and currency are still required fields: enter what your business decides. The page never fills them in for you. Australian English is used in the JBS-style pack; Harborline-style wording comes from its own pack (for example "Subcontract", "Trade Package", "Project Director").

---

## Part A. Allow several organisations (only if the installation was set up for one)

**Navigation.** Menu entry **Organisations**, page **/Admin/Organisations**.
**Purpose.** An installation set up for a single organisation must be widened once before a second organisation can be created. **This cannot be undone**: an installation never goes back to a single organisation.
**Who / permission.** Tenant Administrator; organisations.manage. Needs a recent sign-in.
**Prerequisites.** None. If the page shows a **Create an organisation** button instead of the widen panel, skip this part.

| Field / control | Required | What to enter | Rule | If wrong |
|---|---|---|---|---|
| **Why is this needed?** | Yes | A short reason. Blank: "Second business unit onboarding". JBS-style: "Add the New Zealand procurement organisation". Harborline-style: "Add the Gulf Coast subsidiary" | Up to 500 characters | Rejected with a message; the reason is kept in the audit event |
| Button **Widen to several organisations** | | | Needs a recent sign-in | Takes you to **Confirm it is you** first |

**What the button does.** Changes the tenancy mode permanently and writes an audit event (tenant.tenancy_widened) with your reason. It creates no organisation.
**Success.** Message "This installation now supports several organisations. It cannot be narrowed again." and the **Create an organisation** button appears.
**Common errors.** A blank reason; a forbidden message if you lack organisations.manage.
**Evidence.** The reason text, the date, and the audit entry (Audit events page).

## Part B. Create the organisation (Check, then Create)

**Navigation.** Menu entry **Organisations**, then button **Create an organisation**, page **/Admin/Organisations/New**.
**Purpose.** Records the organisation, its legal entity and optional structure, names its first administrator and records the pack. Publishes nothing.
**Who / permission.** Tenant Administrator; organisations.manage.
**Prerequisites.** Part A done if needed. The initial administrator has signed in once. The chosen pack must be installed and trusted (see the Packs page; pack signing is done outside the application by the Pack Publisher).

### Fields

| Field | Req. | Recommended | Rule | Consequence of a wrong value |
|---|---|---|---|---|
| **Display name** | Yes | The name people see | Up to 200 characters; unique in the tenant | "An organisation with that name already exists in this tenant. Names must be unique." This screen offers no way to rename it afterwards |
| **Registered legal name** | Yes | Full legal name | Up to 200 characters. Becomes the first legal entity, key MAIN | A wrong legal name sits on the first legal entity (key MAIN). Re-check it before Create: this screen cannot change it afterwards |
| **Organisation key** | Yes | 2 to 16 letters or digits, starting with a letter | Begins every reference number; **cannot be changed later**; unique in the tenant | Every contract reference carries it for ever. Example EXO, PROC, HBL |
| **Separator in reference numbers** | Yes | Hyphen | Hyphen, slash, full stop, or none | Cosmetic but permanent in references (HBL-000001 or HBL/000001) |
| **Digits in the number** | Yes | 6 | 3 to 12 | Too few digits runs out of numbers; six allows a million |
| **Country or region** | No | Choose from the list, or leave "(not set)" | Must be on the list | Recorded for the organisation and named in the "what to do next" note (review the regional profile for that country). Wrong country affects only the later regional profile decision |
| **Locale** | Yes | en-GB / en-AU / en-US | A language-and-region tag the platform knows | "That locale is not recognised…". Wrong locale gives the wrong date and number formats |
| **Time zone** | Yes | An IANA name | Must be recognised. Times are stored in UTC and shown in this zone | Wrong zone shifts displayed dates and reminders |
| **Default currency** | Yes | Three letters | An ISO 4217 code the platform offers | Each amount keeps its own currency; the default is only what is offered on entry |
| **Initial administrator** | No, but strongly advised | The person who will configure the organisation | Active people only; shown as "Name (email)". "(none yet)" is allowed | Without one, nobody can configure the organisation until an Organisation Administrator is granted on the Users page |
| **Pack** | No | The matching pack, or "(choose later)" for Blank | Must be installed, trusted and not retired | "That pack is not available here…". The pack supplies drafts only |
| **Divisions, business units, sites and further legal entities** | No | One line per item | Columns: type, key, name, parent key, time zone, registration number. Type is LegalEntity, Division, BusinessUnit or Site. A business unit's parent key must name a division listed in the same box. A first header line is optional. The key MAIN is reserved | Any bad line stops Create and is reported as "Line N: …" |

### Worked examples

**Blank.** Display name Example Organisation; legal name Example Organisation Limited; key EXO; separator Hyphen; digits 6; country (not set); locale en-GB; time zone Europe/London; currency GBP; initial administrator: the named person; pack "(choose later)"; structure box empty.

**JBS-style.** Example Procurement / Example Procurement Pty Ltd / PROC / Hyphen / 6 / Australia (AU) / en-AU / Australia/Sydney / AUD / the named person / pack `jbs-procurement`. Structure:

```
type,key,name,parentKey,timeZone,registrationNumber
Division,CIVIL,Civil Division,,,
BusinessUnit,ROADS,Roads and Bridges,CIVIL,,
Site,SYD,Sydney Office,,Australia/Sydney,
```

**Harborline-style.** Harborline Construction / Harborline Construction, Inc. / HBL / Hyphen / 6 / United States (US) / en-US / America/Chicago / USD / the named person / pack `construction-subcontracts`. Structure (the pack's wording will call divisions "Regions" and sites "Projects"):

```
type,key,name,parentKey,timeZone,registrationNumber
Division,MIDWEST,Midwest Region,,,
BusinessUnit,CIVILWORKS,Civil Works,MIDWEST,,
Site,CHI,Chicago Project Office,,America/Chicago,
```

### Buttons

| Button | What it does | Changes data? |
|---|---|---|
| **Check** | Re-reads every field, lists each problem beside its field and in a panel headed **Fix these first**, or shows **Ready to create** with the numbered actions that Create would take (create the organisation with a sample reference such as HBL-000001; record the legal entity; grant the Organisation Administrator role for this organisation only; assign the pack as drafts; publish nothing). Notes (for example "no initial administrator named") do not block | No |
| **Create the organisation** | Disabled until a Check has passed. Repeats every check, then creates the organisation, records the structure, grants the administrator role, records the pack and prepares what drafts it can, and writes an audit event (organisation.setup.created). Needs a recent sign-in | Yes, no publication |

**Success.** A panel **The organisation was created** lists what was done, then **What to do next** (for example "The organisation's administrator opens Packs, chooses Assign for pack '…' and confirms…"), with a button **Open the set-up checklist**.

**Common errors and recovery.**
| Message (abridged) | Recovery |
|---|---|
| "This installation is set up for a single organisation. Widen it…" | Do Part A |
| "The key 'X' is already used by another organisation…" | Choose another key |
| "Choose an active person from the list. Invite them first under Users…" | The person has not signed in yet. Create with "(none yet)", then grant the role in Part C |
| "Line N: …" in the structure box | Fix that line; remember a business unit's division must be listed in the same box; MAIN is reserved |
| "That pack is not available here…" | Ask the System Administrator or Pack Publisher; or choose "(choose later)" |
| Sent to **Confirm it is you** | Confirm; you return to an empty form. Re-enter, Check, Create |

**Evidence.** The Check panel text (copy it), the "organisation was created" panel, the audit event, and the organisation's row on the Organisations list.

## Part C. Give people their roles (Users and roles)

**Navigation.** Menu entry **Users and roles**, page **/Admin/Users**, then the person's name (page **/Admin/Users/Details/{person}**).
**Purpose.** Invite a person, grant roles scoped to the new organisation, end a role, disable or reactivate an account.
**Who / permission.** Tenant Administrator (may grant every role except System Administrator) or an Organisation Administrator within their own organisation (a narrower list). Permission users.manage; granting also needs roles.delegate.
**Prerequisites.** The person exists (invite them first). Which roles you may offer is limited by your own role, and roles the organisation's terminology hides are not offered in the picker (they remain visible on existing assignments).

### Invite a person
| Field | Req. | Rule | Example |
|---|---|---|---|
| **Name** | Yes | Up to 200 characters | Alex Sample |
| **Email** | Yes | Valid address, up to 254 characters | alex.sample@example.test |
| Button **Send invitation** | | Records the invitation only. **No email is sent**: tell the person yourself. They can sign in once the identity provider vouches for their email address | Success flash: "Invitation recorded…", then the person's page opens |

### Grant a role (on the person's page)
| Field | Req. | Recommended | Notes |
|---|---|---|---|
| **Role** | Yes | The least role that does the job | Shown with its stable code |
| **Viewer kind (Viewer role only)** | For Viewer | All contracts, Category, Business unit, Site or Restricted | A Viewer is never a global reader; match the narrowing below |
| **Applies to** | Yes | **Selected organisations** | "Whole tenant" is for Tenant Administrator and tenant-wide Auditor; "Installation" is for System Administrator |
| **Organisations / Divisions / Business units / Sites / Categories (narrowing)** | Organisation yes; others optional | Choose only the new organisation | Access is the intersection of all of these and the ceiling |
| **Classification ceiling** | Yes | The default level | The highest level the person may see. Only a Tenant Administrator can grant the highest levels |
| **Approver group (Approver and Backup Approver only)** | For approver roles | A registered group | Groups are managed on the Approver groups page (Business Administrator guide) |
| **Reason** | Yes | Why | Up to 1000 characters; kept in the audit trail |
| Button **Grant role** | | | Success flash "Role granted." |

**First roles to grant for a new organisation** (as the rehearsal does): a second Organisation Administrator or Business Configuration Administrator (so there is a person who can publish what the first administrator drafts), the Procurement Administrator(s), and Category Owners, Legal, Auditor and Viewer as needed. Note that publishing rights differ by kind of configuration: capabilities can be published only by a Tenant or Organisation Administrator; workflow, approval matrix, notification policy, regional and onboarding publication also allow a Business Configuration Administrator.

**Ending a role.** On the same page, in the Role assignments table, type a **Reason** and press **End assignment** (success: "Role assignment ended."). **Account:** **Disable account** (reason required) or **Reactivate account**.

**Common errors.** "Your role does not allow you to grant roles." (roles.delegate missing); "The role or scope is above what the caller may grant."; blank reason.
**Evidence.** The role assignments table (screenshot or export) and the audit events.

## Part D. Read the set-up checklist

**Navigation.** From the creation success panel, button **Open the set-up checklist**; or the **Set-up** column link ("N of M steps done") on the Organisations list. Page **/Admin/Organisations/Setup?id=**. The checklist has no menu entry of its own: an Organisation Administrator reaches it from a link or address that the Tenant Administrator passes on.
**Purpose.** A live read of what is actually published and drafted, so it cannot drift from the facts.
**Who / permission.** Tenant Administrator (organisations.manage) or someone with onboarding.run for that organisation. Resetting needs organisations.manage.

The table has the columns **Step, State, Detail, Who, Go to**. States: Done, In progress, Not started, Not visible. Rows: Organisation created; Initial administrator; Structure; Pack; Regional profile; Terminology; Capabilities; Approver groups; Contract workflow; Approval matrix; Notification policy; Submitted changes waiting. A row "In progress" with detail "Submitted: waiting for a second person to publish it" links to the Configuration reviews inbox. Each **Open** link goes to the right screen.

### Reset set-up (only while nothing has been recorded)
Shown as **Start this set-up again** only when the organisation holds no real work (for example no contracts). Otherwise a hint explains why it is closed, listing what the organisation already holds.

| Field | Req. | Rule |
|---|---|---|
| **Reason** | Yes | Up to 500 characters |
| Button **Discard drafts and end structure** | | Needs a recent sign-in. Discards the organisation's open drafts and ends its structure items (including the MAIN legal entity). The organisation stays listed. Published releases, role assignments and the pack choice are untouched |

Success flash: "Set-up reset: N drafts discarded and M structure items ended." Then re-enter structure in Part F, step 2. **Evidence:** the flash text, the audit event (organisation.setup.reset), and the reason.

## Part E. Prepare the drafts from the pack

**Navigation.** Menu entry **Packs**, page **/Admin/Packs/Assign**.
**Who / permission.** The organisation's own administrator (a Tenant Administrator has no organisation-level configuration rights); packs.read to open, packs.assign to assign.
**Purpose.** Completes what Create could not: prepares the remaining drafts (contract workflow, Variation workflow, notification policy, regional profile where applicable) from the pack. Drafts only; safe to repeat; never replaces anything already published.

| Control | What to do |
|---|---|
| **Organisation** | Choose the new organisation |
| **Pack and version** | Choose the pack (JBS-style: `jbs-procurement`; Harborline-style: `construction-subcontracts`; Blank: none, skip this part). An unsigned pack is flagged "not signed" and is accepted only in demonstration or test environments |
| **Preview what would change** | Shows, per kind of configuration, whether something is published or drafted and what would happen. Read it |
| **Assign this pack and prepare drafts** | Prepares drafts where nothing is waiting. Success flash: "The pack was assigned. Drafts were prepared where there was nothing waiting. Review and publish them." The page then shows a **Result** list and **Next steps** |

**Pack signing and trust are not done here.** The application never holds a private key; signing is a **[Technical task: Pack Publisher]** outside the application, and registering the public key under trusted publishers is a **[Technical task: System Administrator]**. A pack that is "not available here" is therefore a technical request, not something to work around.
**Evidence.** The Preview table and the Result list.

## Part F. The Onboarding wizard

**Navigation.** Menu entry **[Organisation] onboarding** (the first word follows the organisation's terminology), page **/Onboarding**. If you may see more than one organisation, a selector at the top switches between them (the address carries ?org=).
**Purpose.** Describe **who owns what and who may see what**, as a shared draft of one document: check it, preview its effect on synthetic data, and publish it as a numbered release that can be rolled back.
**Who / permission.** onboarding.run (Tenant Administrator and Organisation Administrator hold it). Saving and publishing also need organisation-level configuration rights, which in practice only an Organisation Administrator holds. A Tenant Administrator who opens a step sees "You cannot change this organisation's configuration" (the page then answers with an access-denied status). It is blocked while maintenance mode is on.
**Prerequisites.** The organisation exists (Part B). People named as owners and viewers must already exist (invited or active); the wizard does not create or email them.

**How the list steps work (steps 2 to 7).** Type or paste comma-separated text into **List (one item per line)** and press **Save to draft**. A header row is optional; blank lines and lines starting with # are skipped; double quotes are allowed; use ; between several values in one cell; at most 5,000 lines of 1,000 characters. Saving replaces that step's whole list in the draft. **One bad line saves nothing**: the page shows **Nothing was saved. Fix these lines and try again.** with "Line N: …" and keeps your text. There is no file upload or Excel import on these pages. A step's meaning is checked only when you press **Validate** on the review step. The draft starts from the last release, so for a new organisation the box is normally empty (the structure entered at creation is not copied into it).

Each step page opens with the purpose, a **Help for this step** panel with the exact columns and an example, and a link back to the **Checklist**. The checklist (page **/Onboarding**) shows "Current release: N." or "Nothing has been published yet." and a badge per step: Not started, Entered, Optional, Chosen, Not validated, Valid or Needs attention.

### Step 1. Pack and classification (page /Onboarding/Step/pack)
| Field | Req. | Recommended | Rule / consequence |
|---|---|---|---|
| **Pack** (select) | No | Blank: "None (start from the Blank configuration)". JBS-style: `jbs-procurement`. Harborline-style: `construction-subcontracts` | Must be installed, trusted, not retired. Each pack card shows publisher, signature status, what it includes, wording examples, optional steps on or off, and the number of starting categories and contract types. **Choosing here records the choice in this draft only. It does not assign the pack to the organisation** (use Part E for that), and it prepares a terminology draft where you may |
| **Also add the pack's categories and contract types to this draft** | No | Ticked for JBS-style and Harborline-style; unticked for Blank | Merges; nothing existing is removed; duplicates by code are skipped. Wrong tick: you can delete extras by re-saving the categories list |
| Button **Save** | | | Flash "The draft was updated." plus a wording note |

Classification levels are not edited here or on any other onboarding or organisation screen. They are one scale for the whole installation: set in the first-run setup wizard (stage 10, Classification levels), and afterwards changed only by an installation administrator on **Installation setup** (**/Admin/Setup**, permission setup.run).

### Step 2. Organisation structure (page /Onboarding/Step/structure)
Columns: type, key, name, parentKey, timeZone, registrationNumber. Keys are up to 40 letters, digits or hyphens and underscores (avoid a full stop; they are not case sensitive and are stored upper-case).
- Blank: `LegalEntity,MAIN2,Example Organisation Services Limited,,,` and the sites you need.
- JBS-style: `Division,CIVIL,Civil Division,,,` / `BusinessUnit,ROADS,Roads and Bridges,CIVIL,,` / `Site,SYD,Sydney Office,,Australia/Sydney,`
- Harborline-style: `Division,MIDWEST,Midwest Region,,,` / `BusinessUnit,CIVILWORKS,Civil Works,MIDWEST,,` / `Site,CHI,Chicago Project Office,,America/Chicago,`

Validation (on Review): "Key 'x' appears more than once.", a name of 1 to 200 characters, "Division 'x' is not defined here or in the organisation.", "Time zone 'x' was not recognised." Consequence: records are created or updated at publish; nothing is ever deleted; items this wizard created that a later release drops are deactivated; items that existed before (such as those made at creation) are never touched.

### Step 3. Categories (page /Onboarding/Step/categories)
Columns: code, name, parentCode. Code 1 to 40 letters, digits and . _ -; name 1 to 200; no duplicates; the parent must exist; no loops; at most six levels.
- Blank: five to twenty broad categories you choose, for example `GENERAL,General,`
- JBS-style (the pack's five): `PACKAGING,Packaging,` / `PACKAGING-FLEX,Flexible Packaging,PACKAGING` / `ENERGY,Energy,` / `ENERGY-ELEC,Electricity,ENERGY` / `LOGISTICS,Logistics,`
- Harborline-style (the pack's eight trade packages): `EARTHWORKS,Earthworks,` / `EARTHWORKS-EXC,Excavation and backfill,EARTHWORKS` / `CONCRETE,Concrete,` / `CONCRETE-FORM,Formwork,CONCRETE` / `STEEL,Steel,` / `MECHANICAL,Mechanical,` / `ELECTRICAL,Electrical,` / `FINISHES,Finishes,`

Consequence: categories drive ownership, reminders and visibility. Dropped categories are deactivated, not deleted. Later edits and owner changes are on the Categories page.

### Step 4. Contract types (page /Onboarding/Step/types)
Columns: code, name. JBS-style: `SUPPLY,Supply` / `SERVICES,Services` / `LEASE,Lease` / `NDA,Non-disclosure agreement`. Harborline-style: `LUMP_SUM,Lump sum` / `UNIT_RATE,Unit rate` / `COST_PLUS,Cost plus`. Blank: `GENERAL,General agreement`. Label: **Implemented as recorded data; the page states that contract creation uses these in a later milestone, so treat as Scaffolded.**

### Step 5. Owners (page /Onboarding/Step/owners)
Columns: categoryCode, email, primary (blank, yes or no). Example for all three: `GENERAL,sam.owner@example.test,yes` (use your own category code). Rules: the category must exist; the email must belong to an existing person ("No user with the address 'x' exists in this tenant. Invite them in user administration first."); not listed twice. Warning "no_owners" if categories exist but none has an owner. **Consequence:** this creates a category-ownership relationship, **not** the owner role; the person also needs the role (Part C or a group mapping). Reminders go to category owners.

### Step 6. Viewers (page /Onboarding/Step/viewers)
Columns: email, viewerKind, categories, businessUnits, sites, ceiling. Kinds: AllContracts, Category, BusinessUnit, Site, Restricted (Restricted must narrow on at least one dimension). **The ceiling must be filled in and be a level of your scale.** Examples: Blank `vera.viewer@example.test,Category,GENERAL,,,Internal`; JBS-style `vera.viewer@example.test,Category,PACKAGING,,,Internal`; Harborline-style `site.viewer@example.test,Site,,,CHI,Confidential`. Warnings: top_ceiling, restricted_single. **Consequence:** creates a Viewer role assignment. A too-wide viewer sees too much; read the preview. Privileged legal access cannot be granted here.

### Step 7. Identity group mappings (page /Onboarding/Step/mappings)
Columns: providerKey, groupKey, role, viewerKind, categories, businessUnits, sites, ceiling. Role: Viewer, CategoryOwner, Legal, Auditor, ProcurementAdministrator or DivisionSiteAdministrator. Administrator and approver roles cannot be mapped: "Onboarding cannot map the X role. Assign administrator and approver roles in user administration…". A Viewer needs a kind; a Division or Site Administrator must name a site. Example: `corp-sso,Finance-Viewers,Viewer,BusinessUnit,,ROADS,,Internal`. **The provider key and group name come from the identity administrator** (**[Technical task: System Administrator]** to find them). A wrong group name silently does nothing. **Consequence:** the role is applied at every sign-in; leaving the group ends it. Warning privileged_mapping: everyone in that group gains the role, so confirm who controls the group.

### Step 8. Review, simulate and publish (page /Onboarding/Review)
| Section | What to do | Result |
|---|---|---|
| Regional badge | Read **Decided / Not decided**; follow **Review the regional settings** if not decided | Information only. Until locale, currency and time zone are decided the organisation is not ready for Production |
| **1. Validate** | Press **Validate the draft** | Badge Valid or Needs attention and a table of Severity, Where, Message. Errors block publishing; warnings are advice. Any later edit clears the validation |
| **2. What publishing would do** | Read the plan | To create / already present / dropped, per section |
| **3. Who could see what (synthetic preview)** | Read every row for every viewer | Asks the real access rules about invented records across your categories, units and sites. "No viewer scopes or group mappings are in the draft, so there is nothing to simulate." if empty |
| **4. Publish** | Optional **Note for the release history** (up to 500; default "Published from the onboarding wizard"), then the button | The button is disabled until the draft validated cleanly and has not changed since. Where a second person is required it reads **Submit for review**; the second person then follows **Review and publish** (Part G). Success flash: "Release N was published and is now in effect." or "Submitted for review…" |
| **Discard this draft** | Press it | Discards with no confirmation question; a new draft starts from the current release |
| **Release history and rollback** | **Go back to release**, **Reason (required)** up to 500, **Roll back** | Publishes the old content as a **new** release and ends only what a later release introduced and this wizard created. In Production it is submitted for a second person |

**Second-person rule.** In Production, and in Test when governance is `Enforced` (see the design points), the person who created, last edited or submitted the draft cannot publish it; any change after submission voids the submission; the submitted checksum must match at publication. Details are in the Business Administrator guide.

**Evidence.** The Validate result, the plan table, the preview table (screenshots), the release number, and the Configuration reviews history entry.

## Part G. A second person publishes

In Production (and in Test with governance `Enforced`), the Onboarding Review, and every other draft you submit, appears in **Configuration reviews** (menu entry **Configuration reviews**, page **/Admin/Reviews**). The second person opens it, reads the plain-language **What would change**, and presses **Approve and publish**. See the Business Administrator guide, "The review inbox".

---

## What is not on a screen (technical tasks)
| Task | Role |
|---|---|
| Installing the platform, identity provider registration, secrets, mail settings, backups and restore | **[Technical task: System Administrator]** |
| Signing a pack; holding the private key | **[Technical task: Pack Publisher]** and key custody, outside the application |
| Trusting a pack publisher's public key by host configuration (a Tenant Administrator can instead register it on the **Trusted publishers** screen, **/Admin/Packs/TrustKeys**) | **[Technical task: System Administrator]** |

Classification levels are not a technical task and not an onboarding setting: see the note at Part F, step 1.

## Evidence to keep
- The Check panel and the "organisation was created" panel (Part B), with date and your name.
- The reason text given when widening or resetting.
- The role assignments table for the new organisation, and the audit events for organisation.setup.created, role grants and any reset.
- The set-up checklist at three moments: just after creation, after drafts are prepared, and when everything is Done.
- The Onboarding validation result, plan, synthetic preview and the release number, and the review history of each submission.

## If something goes wrong
| Symptom | What to do |
|---|---|
| You are sent to **Confirm it is you** | Confirm (the demonstration provider asks you to press Confirm; a real provider asks you to sign in again). You return to the page with an empty form: re-enter, Check, Create |
| Create button is greyed out | Press Check; fix every item under **Fix these first** |
| The pack is not on the list | A trust or installation matter: raise it with the System Administrator; use "(choose later)" meanwhile |
| Nobody can configure the new organisation | Grant Organisation Administrator on the person's Users page (Part C) |
| Onboarding shows "You cannot change this organisation's configuration" | You are a Tenant Administrator or lack configuration rights. Ask an Organisation Administrator, or have one granted to you (audited) |
| A list step says "Nothing was saved" | Correct the lines shown; your text is kept in the box |
| Publish is disabled | Validate again; any edit clears validation |
| Reset is not offered | The organisation already holds work (the hint lists it). Retire role assignments under Users and publish new releases instead |
| Anything else | Capture the message, the page and the time, and raise it with your System Administrator. Do not edit the database |

Related: docs/IMPLEMENTATION_PLAYBOOK.md (sections 6 and 7, with the earlier detailed playbook), docs/SETUP_AND_ONBOARDING.md, docs/NO_CODE_REHEARSAL.md.
