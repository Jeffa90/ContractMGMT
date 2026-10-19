# First-run setup wizard guide

**Purpose.** Take a freshly installed, empty installation to a working one: a database that is current, document storage that works, a tenant, a starting pack, a way to sign in, a first administrator, a first organisation, branding, classification levels, email, and a recorded readiness review. This guide describes every stage and every field of the wizard, in the order the application presents them.

**Who it is for.** Two people sit at the same screen. The **System Administrator** opens the wizard and handles the technical stages (2, 3, 6, 11 and any blocking check). A **Business Administrator or the Business Lead's delegate** supplies the business answers (stages 1, 4, 5, 7 to 10). Neither needs to edit JSON, run SQL, change source code or use Git. The only commands are the two the System Administrator runs to get the setup token (in the installation guide, INSTALLATION_GUIDE.md in the same folder).

**Permission needed.** None of the usual roles. The wizard is open to whoever holds the **one-time setup token** and the single open **setup session**. The token is printed on the server's terminal by the System Administrator, valid for 24 hours and single use. There is no sign-in on these pages and no role check; the control is possession of the token plus, by default, being on the server itself.

**Prerequisites.**
- The installation guide is complete up to "Open the setup wizard" (database set up, application started, reverse proxy, settings, signed pack in Production).
- The System Administrator has the setup token and the identity provider administrator is available.
- Production only: a pack is available that your installation trusts (otherwise stage 5 cannot continue), and the identity provider application is registered (installation guide, section 8.2).
- The business answers are decided and written down before you start (see "Gather your answers" below).

**Status label.** The wizard is **Implemented**. It is Verified end to end over HTTP in **Test mode** with the demonstration sign-in, the mock email provider and a SQLite file database (host tests). It is **Not Verified** in Production mode with a real identity provider, real mail server and SQL Server together; and a Production-mode run is expected to be stopped at the end by the malware-scanner check (stage 13, "Production readiness note"). The email stage sends a real test through the provider: SMTP is verified only against a loopback test relay in the automated tests and is **Not Verified** against a customer's mail service; Microsoft Graph is Scaffolded. It has not been followed by a non-developer on a clean machine.

**Governance in the wizard.**
- The wizard publishes its settings (the first regional settings, the pack choice, branding, classification levels) through a trusted installation path: validated, versioned and audited, but with **no signed-in user**. The second-person rule for publication and the recent sign-in (step-up) rule therefore do **not** apply to what the wizard publishes. They apply from the first sign-in onward, to every business configuration screen.
- After setup, six stages can be reopened by an installation administrator at **Installation setup** (**/Admin/Setup**), which needs a recent sign-in (within 10 minutes) and records the change in the audit trail under the signed-in person. That path uses the same trusted publication, so it does not ask for a second person; agree a procedure for that.
- **Governance mode.** The setup report and the notes on stage 1 show the effective governance mode: Production, TestEnforced, TestSimplified or DemoSimplified. Production always enforces the second-person publish rule. In Test, `Ecp:Governance:SecondPerson` set to `Enforced` applies exactly the Production rule from the first sign-in onward; `Default` keeps the simplified behaviour. Demo ignores the setting and is always simplified. The wizard's own publication is a trusted installation path and does not use a second person in any mode. The setting is a host setting changed by the System Administrator (a technical task; installation guide, section 5.1).
- Setup actions are audited (for example setup.tenant.created, setup.organisation.created, setup.completed, and the configuration releases and identity provider changes the stages make). Nothing here approves the installation for production use; that is external governance.

---

## A. Words and behaviour common to every stage

| Item | What it does |
|---|---|
| Stage list (**/Setup**) | After the token is accepted, shows a progress bar ("N of M steps complete") and a numbered list. Each line has a status badge (NotStarted, InProgress, Complete, Skipped) and a link. A stage whose earlier stages are not done is shown as plain text with "Complete first: …" |
| Stage page (**/Setup/Step/** followed by the stage id) | Title, summary, status, hints, the form, and one button. A red error box headed "This step was not completed" lists what to fix; nothing is saved by a failed attempt except what the message says |
| The button | "Save and continue" on most stages; the stages with a different button say so below. It runs the stage. A green message confirms success and you are taken to the next stage that is ready. The readiness stage stays on its page after success |
| Required fields | Marked with an asterisk. A blank required field gives "<Field name> is required." |
| Repeating a stage | Safe. Running the same input again changes nothing ("Nothing changed", "unchanged") |
| Progress | Saved after every stage, even through a restart or a closed browser |
| End this setup session | A secondary button on the stage list. It closes your session without finishing. To come back, the System Administrator issues a new token |
| Wording | A few labels use the platform's terminology (for example "Organisation" and "Solution pack"). Before any organisation exists they show the platform's neutral words; the spelling can differ if your locale or pack changes them |
| Setup report | Link "View the setup report" on the stage list. Evidence for the owner, **not** an approval |
| Wording tokens | No raw token of the form `[[...]]` should ever be visible on a setup page. If you see one, it is a defect: a neutral marker appears instead and a stable code is logged. Report the page and the time to the supplier; do not type it into a field |

### Gather your answers (business) before you start

| Answer | Needed at stage | Blank example | JBS-style example | Harborline-style example |
|---|---|---|---|---|
| Tenant name | 4 | Example Holdings | Example Procurement Group | Harborline Construction Group |
| Starting pack | 5 | Blank | JBS Procurement Reference | Construction Subcontracts (synthetic example) |
| First administrator and the email the identity provider asserts | 7 | First Administrator, first.admin@example.test | Alex Example, alex.example@example.test | Sam Example, sam.example@example.test |
| Organisation name, locale, time zone, currency, reference prefix | 8 | Decide: for example Example Organisation, en-GB, Europe/London, GBP, CON | Procurement, en-AU, Australia/Sydney, AUD, JBSAUS | Harborline Construction, en-US, America/Chicago, USD, HBL |
| Product name, accent colour, footer | 9 | Contract Register, #1f3a8a | Procurement Contract Register, #1f3a8a | Harborline Subcontract Hub, #0b5d4b |
| Classification levels | 10 | The pack's five levels | The pack's five levels | The pack's four levels |
| Sender address and test recipient for email | 11 | contracts@example.test | procurement-register@example.test | subcontracts@example.test |

The three profiles are the same everywhere in this guide:

- **Blank**: the neutral pack, no business defaults. Locale, time zone and currency start empty; you decide.
- **JBS-style**: the reference procurement pack `jbs-procurement`; Australian English, Australian dollars, an Australian time zone.
- **Harborline-style**: a synthetic general contractor on the pack `construction-subcontracts`; United States, US dollars, America/Chicago, en-US.

All names, addresses and values in this guide are synthetic.

---

## B. Opening the wizard

| | |
|---|---|
| Navigation | Browse to **/Setup** on the installation's address. A fresh installation sends every browser page here by itself |
| Who | The System Administrator, with the setup token |
| Prerequisite | The token from the command in the installation guide (section 10). In the default configuration the page answers "not found" unless you are on the server itself; the System Administrator sets the setting that allows remote setup only while setup runs |
| Purpose | Prove that the person at the keyboard controls the server |

| Field | Required | Value | Rule | If wrong |
|---|---|---|---|---|
| Setup token | Yes | Paste the token. It is hidden as you type | Single use, 24 hours. Leading and trailing spaces are ignored. Five wrong tokens lock entry for five minutes | A wrong or used token is refused; a new one must be issued on the server |

**Button "Open setup".** Checks the token, uses it up, and starts your session: one session at a time, ended after 60 minutes without activity, or when a new token is issued. Success: the stage list appears with a progress bar.

| Message | Meaning and recovery |
|---|---|
| "The setup token is not valid, has expired, or was already used. Issue a new token from the command line." | System Administrator issues a new token and you paste it |
| "A setup session is already open. It ends after 60 minutes without activity, or issue a new token from the command line." | Someone (possibly you, in another browser) is in a session. Issue a new token, which ends the old session |
| "Too many incorrect tokens. Wait a few minutes, or issue a new token from the command line." | Wait five minutes or issue a new token |
| "Setup is already complete." | Nothing to do here; setup has been closed (see "After setup") |
| The page is simply not found | You are not on the server and remote setup is not allowed, or setup is already complete |
| A short page with `ECP-NET-001` ("not an HTTPS request") | In Production the wizard needs HTTPS. Open the https:// address; if you already did, the proxy ends TLS but does not tell the application (it must send `X-Forwarded-Proto`). Technical task for the System Administrator: `docs/REVERSE_PROXY.md` |
| A short page with `ECP-NET-002` ("forwarded headers not trusted") | The proxy did tell the application, but the application does not yet trust that proxy. Technical task for the System Administrator: enable forwarded headers and name the proxy (`docs/REVERSE_PROXY.md`). The page shows a reference to quote and never shows an address, a header or a secret |

The token form is rate-limited. If the setup page asks for the token again after a long pause, the session expired: ask for a new token. Your stage progress is not lost.

**Evidence to keep.** None of the token. Note the date and time the wizard was opened and by whom.

---

## Stage 1: Installation mode

| | |
|---|---|
| Navigation | Stage list, "Installation mode"; **/Setup/Step/mode** |
| Who | Business Administrator with the System Administrator |
| Purpose | Record where and how this installation runs, and (in Production) confirm that the host's Production rules are satisfied |
| Prerequisite | None |
| Button | **Save and continue** |

The page tells you the mode the host is running in (Demo, Test or Production). **The wizard cannot change the mode**; it is set in host configuration.

| Field | Required | Blank | JBS-style | Harborline-style | Rule | If wrong |
|---|---|---|---|---|---|---|
| Deployment (list: Local development or evaluation; Windows Server with IIS; Azure App Service; Container) | Yes | Container | Container | Container | One of the listed values. "Choose one of the listed deployments." | Recorded in the setup report only; it changes no behaviour. A wrong label misleads the audit trail |
| Tenancy (list: One organisation; Several organisations in one tenant; Several tenants) | Yes | One organisation | One organisation | One organisation (choose Several organisations if two businesses will share this tenant) | One of the listed values. "Choose one of the listed tenancy modes." Once the tenant exists, a different value is refused ("The tenant already exists; tenancy can no longer be changed here.") | See below |
| "I understand this installation is for demonstration or testing only and must not hold real confidential data" | Demo and Test only (not shown in Production) | Tick | Tick | Tick | "Acknowledge that this installation is not for real data." | You cannot continue |

**About the Tenancy field.** The help text says tenancy "can only be widened later by an installation administrator". That text is out of date: after setup, a Tenant Administrator (permission organisations.manage) can widen a one-organisation tenant to several organisations once, on **Organisations** (**/Admin/Organisations**), with a reason and a recent sign-in. It is one-way and audited; an installation never goes back to a single organisation. Choose carefully, but a wrong choice of "one organisation" is recoverable by widening.

The page also shows a **Governance** line naming the effective governance mode and what it means (see "Governance in the wizard"). It is information; the wizard cannot change it.

**Production only.** The stage also runs the Production guard. If something is wrong the page says "Production configuration is not acceptable: [rule id] message…". The rules: demonstration sign-in or tenant resolution must be off (R-ID-01), the database must be SQL Server (R-DB-01), the connection string must be a secret reference (R-SEC-02), demonstration data must be off (R-DEMO-01), and the data folder must be persistent (R-ST-01). Fix these in host configuration (System Administrator) and press the button again.

**Success.** "Production mode confirmed (Container)." or the same for Demo or Test. **Evidence.** Screenshot of the success message.

---

## Stage 2: Database

| | |
|---|---|
| Navigation | "Database"; **/Setup/Step/database** |
| Who | System Administrator |
| Purpose | Prove the database is reachable and its schema is current |
| Prerequisite | Stage 1 complete |
| Fields | None. The page shows the provider and says whether the connection string comes from a secret reference. It never shows the connection string |
| Button | **Check database** |

What the button does: in **Demo and Test** it applies any pending database changes for you. In **Production** it only checks; the database setup must already have been run (installation guide, section 7.3).

| Result | Message | Recovery |
|---|---|---|
| Success | "The database is reachable and all N modules are current." | Continue |
| Production, not current | "The database schema is not current. Run the migrate command, then check again." with "Pending: …" | System Administrator runs the database setup, then press the button again |
| Demo/Test, still not current | "Migrations were applied but the schema is still not current." | System Administrator reads the application log |

**Evidence.** Screenshot of the success message (N modules).

---

## Stage 3: Document storage

| | |
|---|---|
| Navigation | "Document storage"; **/Setup/Step/storage** |
| Who | System Administrator |
| Purpose | Prove documents can be written, read back, found and removed |
| Prerequisite | Stage 2 complete |
| Fields | None |
| Button | **Run storage check** |

The platform writes a small test object, reads it back, checks it exists, deletes it and checks it is gone. Success: "Write, read, existence and delete succeeded with the '<provider>' provider."

| Failure | Meaning |
|---|---|
| "The stored object did not read back identically." / "The stored object could not be found again." / "The test object could not be removed." | The data folder is not working. Check the volume and its permissions |

**Consequence of a wrong setup that this check cannot see:** if the data folder is on a volume that disappears, every uploaded document is lost later without warning. Production start-up already refuses a temporary location. **Amendable later:** yes (Installation setup).

---

## Stage 4: Tenant

| | |
|---|---|
| Navigation | "Tenant"; **/Setup/Step/tenant** |
| Who | Business Administrator |
| Purpose | Create the tenant: the customer account that holds your organisations |
| Prerequisite | Stages 1 and 2 complete |
| Button | **Save and continue** |

| Field | Required | Blank | JBS-style | Harborline-style | Rule | If wrong |
|---|---|---|---|---|---|---|
| Tenant name | Yes | Example Holdings | Example Procurement Group | Harborline Construction Group | At most 120 characters ("The tenant name must be at most 120 characters."). Repeating the stage with the same name adopts the tenant already created | **Cannot be changed later through any screen.** It is internal and not shown to suppliers |

Success: "Tenant 'Example Holdings' is ready." (or "Tenant '…' already exists. Nothing changed." on a repeat). Audited as setup.tenant.created.

---

## Stage 5: Solution pack

| | |
|---|---|
| Navigation | "Solution pack"; **/Setup/Step/pack** |
| Who | Business Administrator with the Business Lead |
| Purpose | Choose the starting configuration: wording, workflows, approval rules, reminder rules, regional proposals. A pack is data only; it publishes nothing by itself |
| Prerequisite | Stage 4 complete |
| Button | **Save and continue** |

| Field | Required | Blank | JBS-style | Harborline-style | Rule | If wrong |
|---|---|---|---|---|---|---|
| Solution pack (list) | Yes | "Blank (no business defaults)" | "JBS Procurement Reference" | "Construction Subcontracts (synthetic example)" | Nothing is preselected: the first option is "Choose a starting pack". Each option reads "Name (version X) from Publisher" followed by ", signed" or ", UNSIGNED (Demo and Test only)". Blank choice: "Solution pack is required." A pack not in the catalogue: "That solution pack is not in the catalogue." | Wrong wording, workflow, capabilities and reminder rules as the starting point. You can change everything later, but once the organisation exists (stage 8) this stage refuses a different pack ("The organisation already exists; change its configuration through onboarding instead."). In practice choose or change the pack afterwards on **Assign** (**/Admin/Packs/Assign**) |

Below the form the page describes each pack: its description, capabilities, example wording (for example what it calls a contract, a supplier, a category), and which optional steps it switches off (Legal review, Register authorisation, Variations, Renewals, Signature requests). In **Production** only packs signed by a trusted publisher are offered, so **the list can be empty** until signed packs are installed. If it is empty the page says no pack is available; the System Administrator fixes that (installation guide, section 6); you cannot continue without a pack.

Success: "<Pack name> selected." Nothing is published until stage 8.

---

## Stage 6: Identity provider

| | |
|---|---|
| Navigation | "Identity provider"; **/Setup/Step/identity** |
| Who | System Administrator with the identity provider administrator |
| Purpose | Decide how people sign in |
| Prerequisite | Stage 4 complete |
| Button | **Save and continue** |

Client secrets are **referenced, never typed**. A pasted secret is refused.

| Field | Required | Blank / JBS-style / Harborline-style | Rule | If wrong |
|---|---|---|---|---|
| Provider type (list: Microsoft Entra ID; Other OpenID Connect provider; Demonstration sign-in only in Demo and Test when enabled) | Yes | Microsoft Entra ID in all three (or Other OpenID Connect provider) | Demonstration sign-in is refused in Production | Nobody can sign in |
| Provider key | Yes | entra | "The provider key must be 2 to 40 lower-case letters, digits or hyphens." | It becomes part of the sign-in redirect address and of group mappings. Changing it later breaks both |
| Name shown on the sign-in page | Yes | Company sign-in | "A display name is required." | Cosmetic. The sign-in page shows "Sign in with" followed by this name |
| Entra directory (tenant) id | Entra only | A GUID such as 11111111-2222-3333-4444-555555555555 (synthetic) | Must be a GUID. 'common', 'organizations' and 'consumers' are refused because they allow other directories to sign in. A custom authority is refused for Entra | A wrong id makes every sign-in fail |
| Authority (issuer URL) | Other OpenID Connect only | https://login.example.test/realm | Absolute, HTTPS, no fragment or credentials (plain HTTP only for loopback outside Production) | Sign-in fails |
| Client id | Yes | The application (client) id from the provider | "The client id is required." | Sign-in fails |
| Client secret reference | No, but strongly recommended | env:ECP_OIDC_SECRET or a file: reference to a secret file | "The client secret must be a secret reference (env:NAME, file:/path or keyvault:name), never the secret itself." In Production an unresolved reference **blocks**; the keyvault: form never resolves today | A pasted secret is refused (good). An unresolved reference blocks readiness |
| Groups claim | Yes | groups | "A groups claim name is required for identity-group mapping (use 'groups')." | Group-to-role mapping never fires |

**What Save does.** Checks the settings, saves the provider, and registers it **twice**: once for your tenant and once for installation administrators. Blocking problems appear under "The provider settings are not valid." Non-blocking notes (for example "No client secret reference is set; only a public client using PKCE without a secret will work.") appear in the success box.

**Redirect addresses.** The page says the callback addresses are "shown after saving". **They are not.** The System Administrator builds them as the installation guide explains (section 8.2): after saving, a button **Test sign-in with <name>** appears below the form; hover over it and read the text after scheme=. Register that address, the installation equivalent, and their -signout forms with the identity provider.

**Test sign-in.** The button sends the administrator through the real provider and back. Accounts are invite-only, so run the test **after stage 7** (where the first administrator is named); the button is also on the stage 7 and stage 13 pages. A successful test is recorded as check R-ID-02 (blocking in Production). Testing signs you in as that administrator (this is also their first sign-in, which links their account).

**Demo/Test only:** "Demonstration sign-in" records a successful test at once.

**Evidence.** The provider key and display name; the registered redirect addresses (technical sheet). Never the client secret.

---

## Stage 7: Initial administrator

| | |
|---|---|
| Navigation | "Initial administrator"; **/Setup/Step/admin** |
| Who | Business Administrator |
| Purpose | Name the person who will administer the tenant. They sign in through the provider from stage 6 |
| Prerequisite | Stage 6 complete |
| Button | **Save and continue** |

| Field | Required | Blank | JBS-style | Harborline-style | Rule | If wrong |
|---|---|---|---|---|---|---|
| Full name | Yes | First Administrator | Alex Example | Sam Example | Any text | Cosmetic |
| Email address | Yes | first.admin@example.test | alex.example@example.test | sam.example@example.test | "Enter a valid email address." Must match the address the identity provider asserts for this person (accounts link by provider and subject, not by email alone) | If it does not match, the person cannot sign in and setup cannot finish (check R-ADM-01 in Production) |
| Also make this person an installation administrator | No (ticked by default) | Tick | Tick | Tick | Creates a second, separate account in the installation scope with the System Administrator role. Only installation administrators can reopen setup later | If unticked and nobody else is named, **nobody can ever reopen setup** (warning R-ADM-02). A System Administrator should be a dedicated person |

What Save does: creates the user (invited until their first verified sign-in; active at once with the demonstration sign-in), grants **Tenant Administrator**, and, if ticked, creates the installation-scope account. Success: "<Name> was set up as tenant administrator." (or "…already existed; the administrator role is in place."). **Not changeable afterwards through the wizard**; use user administration. The email address is personal data and is held by the identity module, not repeated in the setup state.

**Now run Test sign-in** (button below the form). Evidence: R-ID-02 shows Pass later in stage 13.

---

## Stage 8: Organisation

| | |
|---|---|
| Navigation | "Organisation"; **/Setup/Step/organisation** |
| Who | Business Administrator |
| Purpose | Create the first organisation and publish its first regional settings |
| Prerequisite | Stages 5 and 7 complete |
| Button | **Save and continue** |

The chosen pack proposes defaults; **the Blank pack proposes none for locale, time zone and currency**, so those fields start empty and you decide.

| Field | Required | Blank | JBS-style | Harborline-style | Rule | If wrong |
|---|---|---|---|---|---|---|
| Organisation name | Yes (pre-filled with the tenant name) | Example Organisation | Procurement | Harborline Construction | Unique in the tenant: "An organisation with that name or reference prefix already exists in this tenant." | Not changeable here later |
| Country or Region (list) | No | (not set) | Australia (AU) | United States (US) | From the list. A label for people and for the identifier and address profiles; it decides no format by itself | Wrong profile suggestions only |
| Locale | Yes | en-GB | en-AU | en-US | A language-and-region tag. "That locale is not recognised." Controls date, number and currency layout, **not** the language of the pages | Dates and amounts appear in an unexpected order (for example 03/04) |
| Time Zone | Yes | Europe/London | Australia/Sydney (the pack pre-fills Australia/Brisbane; change it if your organisation observes Sydney time) | America/Chicago | A place-based identifier, Area/Location. "That time zone is not recognised." Times are stored in UTC and shown in this zone | Wrong deadlines and reminders: "today" is judged in this zone |
| Default currency | Yes | GBP | AUD | USD | A three-letter ISO code the platform offers. "That currency is not an ISO 4217 code the platform offers." | Wrong default on new amounts. Existing amounts are never converted |
| Tax Identifier type (list) | No | (none for now) | ABN (AU) | Federal Tax ID (US) | From the identifier profiles in the installed packs | Wrong field shape on supplier forms |
| Contract reference prefix | Yes | CON (the Blank pack proposes CON) | JBSAUS | HBL | 2 to 16 letters or digits, starting with a letter; upper-cased: "Reference prefix must be 2 to 16 letters or digits starting with a letter." | **Cannot be changed through any screen**; every future reference carries it |

**The reference format.** The help text says references look like PREFIX-0001. That is out of date: this stage uses the standard format of a hyphen and **six digits**, for example JBSAUS-000001 or HBL-000001. The wizard has no field for the separator or the number of digits. Another organisation created later on **Organisations** (**/Admin/Organisations/New**) can choose both.

What Save does: creates the organisation; publishes its first regional settings as release 1 (only if none exists), recording the choices and which pack version proposed them; makes the initial administrator an **Organisation Administrator** of this organisation (so they can run organisation onboarding); and publishes a record of the pack choice. Success: "Organisation '<name>' is ready." Audited as setup.organisation.created. Not changeable afterwards through the wizard.

---

## Stage 9: Branding

| | |
|---|---|
| Navigation | "Branding"; **/Setup/Step/branding** |
| Who | Business Administrator |
| Purpose | The product name, accent colour and footer shown in the application |
| Prerequisite | Stage 8 complete |
| Button | **Save and continue** |

| Field | Required | Blank | JBS-style | Harborline-style | Rule | If wrong |
|---|---|---|---|---|---|---|
| Product name | Yes (pre-filled "Enterprise Contract Platform") | Contract Register | Procurement Contract Register | Harborline Subcontract Hub | 1 to 60 characters: "The product name must be 1 to 60 characters." | Cosmetic |
| Accent colour | Yes (pre-filled #1f3a8a) | #1f3a8a | #1f3a8a | #0b5d4b | Six-digit hex such as #1f3a8a: "The accent colour must be a six-digit hex colour…". Must have a contrast ratio of at least 4.5:1 against white text: "The accent colour needs a contrast ratio of at least 4.5:1 against white text (WCAG 2.2 AA)." | Pale colours are refused |
| Footer text | No | (blank) | Procurement contract register. Contact procurement-register@example.test | Synthetic example business. Not legal advice. | At most 200 characters | Cosmetic |

Success: "Branding published." (or "Branding unchanged."). **Amendable later** (Installation setup).

---

## Stage 10: Classification levels

| | |
|---|---|
| Navigation | "Classification levels"; **/Setup/Step/classification** |
| Who | Business Administrator with the Legal lead |
| Purpose | The ordered sensitivity scale. Access never exceeds a person's classification ceiling |
| Prerequisite | Stage 8 complete |
| Button | **Save and continue** |

The fields are pre-filled from the chosen pack (or from the platform's standard five levels if the pack gives none). The page notes that the final level names are an open decision for the owner (decision Q-06) and can be replaced here at any time.

| Field | Required | Blank | JBS-style | Harborline-style | Rule | If wrong |
|---|---|---|---|---|---|---|
| Levels, lowest first (text area) | Yes | Public, Internal, Confidential, Restricted, Legal Privileged (one per line) | Same five | Public, Internal, Confidential, Restricted (four) | One per line (commas also split); each up to 50 characters; distinct, not case-sensitive: "Classification levels must be a non-empty list of distinct names." | **Every access rule, approval condition and reminder privacy rule uses this scale.** Renaming or removing a level later affects who can see records. Decide with Legal |
| Default level for new records | Yes | Internal | Internal | Internal | Must be one of the levels: "The default classification must be one of the levels." | New records over- or under-protected |
| Privileged levels (text area) | No | Legal Privileged | Legal Privileged | (empty: no privileged level) | Each must be one of the levels: "The privileged level 'X' is not one of the levels." | Privileged records need an extra explicit grant; leaving it empty removes that protection |

Success: "N classification levels published." (or "unchanged"). **Amendable later.**

---

## Stage 11: Email notifications

| | |
|---|---|
| Navigation | "Email notifications"; **/Setup/Step/notifications** |
| Who | System Administrator |
| Purpose | Save the email configuration and prove the provider works with an actual test through it |
| Prerequisite | Stage 4 complete |
| Button | **Save and continue** (it saves the settings and runs the test every time it is pressed) |

**One configuration, not two.** This stage reads and writes the **same** installation-level email configuration that Administration uses (**/Admin/Operations/Email**; anyone with `operations.read` can see it, and changing it needs the System Administrator, `operations.maintenance`, with a recent sign-in). Live delivery reads it. There is no second set of host settings to keep in step; host settings are only a fallback used when nothing has been stored. Only non-secret settings and secret references are stored. A secret typed into a field is refused and never shown back.

| Field | Required | Blank / JBS-style / Harborline-style | Rule | If wrong |
|---|---|---|---|---|
| Provider (list: SMTP server; in Demo and Test also "Development capture (simulated; delivers nothing)" and "Disabled (no email is sent)") | Yes | SMTP server | Production refuses the simulated providers ("The demonstration and development-capture providers deliver nothing and are refused in Production.") and Disabled ("Production requires a delivering email provider.") | No email |
| From address | For SMTP | contracts@example.test (Blank), procurement-register@example.test (JBS-style), subcontracts@example.test (Harborline-style) | "A valid From address is required." | Replies are lost; mail may be rejected as spoofed |
| SMTP host | SMTP | smtp.example.test | Host name only: "Enter the SMTP host name (no scheme or path)." | Test fails |
| SMTP port | SMTP | 587 | 1 to 65535. **A blank or non-numeric value is silently turned into 587** | Test fails or hits the wrong port |
| Use TLS | | Ticked | Production requires TLS. Port 587 with STARTTLS is what is expected to work; port 465 (implicit TLS) is probably unsupported | Mail refused |
| SMTP user reference, SMTP password reference | Production: both | env:ECP_SMTP_USER and a file: reference | Must be references, never values. Production requires both; an unresolved reference fails the test | A pasted secret is refused |
| Send a test message to | Yes (except Disabled) | first.admin@example.test | "Enter a valid test recipient address." | Without a passing test email is not ready |

**The test is real and required.** Saving settings proves nothing. The button connects to the provider (a connectivity probe, including TLS) and then sends a synthetic test message whose text is fixed and contains no contract information. Results:

| Message | Meaning |
|---|---|
| "The mail server was reached and accepted the test message." | A real provider interaction passed. Shown with a test reference |
| "The simulated provider accepted the test message. Nothing was delivered to a real mailbox, so email is Not Verified until a real provider is configured and tested." | Demo and Test only. A simulated provider **never** reads Healthy; Administration shows "Simulated: nothing delivered" |
| "The email test did not pass, so email is not ready. …" with a diagnostic code (for example EML-CONN-001 cannot connect, EML-TLS-002 certificate not trusted) and a reference | The settings are saved but email is **not** ready. A configured provider that cannot be reached is never Healthy. Correct the settings and press the button again |
| "Email notifications are disabled." | Disabled provider: nothing is sent. Not allowed in Production |

A passing result is tied to the settings (a changed setting makes it **stale**) and expires (168 hours by default, `Ecp:Email:ReadinessMaxAgeHours`), after which email reads as not ready until it is tested again. Check R-NOT-01 in stage 13 reads this result: in **Production, Finish setup cannot complete without a passing test**; outside Production a missing, simulated, stale or failed test is a warning that must be acknowledged. SMTP is Implemented and **Not Verified** against a customer's mail service; Graph is Scaffolded and is not connected. **Amendable later.**

---

## Stage 12: Demonstration data (Demo and Test only)

| | |
|---|---|
| Navigation | "Demonstration data"; **/Setup/Step/demo** |
| Who | Business Administrator |
| Purpose | Optionally add synthetic example organisations, users and contracts |
| Prerequisite | Stage 8 complete |
| Availability | **Does not exist in Production** ("Not applicable in this mode.") |
| Buttons | **Add demonstration data** and **Skip this step** |

It adds synthetic tenants **alongside** yours. Nothing real is included. Never use it in an installation that will hold real data (in Production it is refused: "Demonstration data is refused in Production."). Skipping marks the stage Skipped, and is the right choice for any installation you intend to keep.

---

## Stage 13: Readiness review

| | |
|---|---|
| Navigation | "Readiness review"; **/Setup/Step/readiness** |
| Who | Business Administrator with the System Administrator for any blocker |
| Purpose | Run the checks, fix blockers and record a reason for each warning |
| Prerequisite | Stages 3, 8, 9, 10 and 11 complete |
| Button | **Run checks** (the page stays on this stage after success) |

The page lists the checks in a table (Check, Result, Detail). Results are Pass, Warning or Blocking. The 12 checks:

| Check | Passes when | If not | Who fixes |
|---|---|---|---|
| R-OPS-01 | Database, data folder, globalisation and the platform's other readiness checks are all Healthy. These include the malware-scanner check R-DOC-01 and the transport check R-NET-01 | Blocking if any is Unhealthy; a warning if only Degraded (for example no backup configured, no trusted pack, no scanner or only the demonstration scanner in Test, or a recent HTTPS or proxy refusal) | System Administrator (installation guide, section 9) |
| R-ST-01 | Stage 3 was done | Blocking | Run stage 3 |
| R-SEC-01 | The Production guard finds nothing wrong (always passes outside Production) | Blocking | System Administrator, host settings |
| R-DEMO-01 | No demonstration sign-in, seeding or data in Production | Blocking | System Administrator |
| R-ID-01 | An enabled identity provider is valid | Blocking | Stage 6 |
| R-ID-02 | A test sign-in succeeded | Blocking in Production, warning elsewhere | Press Test sign-in (after stage 7) |
| R-ADM-01 | An active tenant administrator exists | Blocking if none; if the administrator is still only invited: blocking in Production, warning elsewhere | The administrator signs in once (Test sign-in) |
| R-ADM-02 | An installation administrator was named | Warning | Tick the box in stage 7, or accept with a reason |
| R-ORG-01 | The organisation exists | Blocking | Stage 8 |
| R-CFG-01 | The pack choice, branding and classification are published | Blocking | Stages 8 to 10 |
| R-NOT-01 | The email provider was reached by a real, recent test through the saved settings | Blocking in Production, a warning elsewhere, if email was never set up or the test is missing, failed, stale or from a simulated provider | Stage 11 |
| R-AUD-01 | The audit chain verifies | Blocking | Contact the supplier with the report |

**Warnings need a reason.** After a run that has warnings, one text box appears for each, named by check id: "Acknowledge R-xx-nn: description" (for example R-OPS-01, R-ADM-02 or R-NOT-01). Type a reason (at most 300 characters; longer is cut) and press **Run checks** again. Finish setup refuses while any warning has no acknowledgement. Who, when and why are recorded and appear in the report.

**The scanner.** In Test with no scanner connected, or with the labelled demonstration scanner (only when `Documents:Scanner:Provider` is `mock`), R-DOC-01 is Degraded, so it arrives as a warning inside **R-OPS-01** and you must acknowledge R-OPS-01 with a reason that names the scanner, for example "No real malware scanner exists; the demonstration scanner is no protection; synthetic data only". That acknowledgement is not the on-the-record acceptance of the mock that masked-data rehearsals need (**/Admin/Operations/Scanner**, System Administrator). In Production, R-DOC-01 is Unhealthy without a connected, healthy scanner and is blocking. Messages: "N blocking check(s) failed." and "Acknowledge each warning with a reason, then run the checks again." **A blocking check cannot be acknowledged**; it must be fixed. Do not acknowledge a warning you do not understand.

Examples of reasons (synthetic): for R-OPS-01 when only the backup check is degraded, "Backup folder is being provisioned; verified backup due before go-live (owner: System Administrator, 2026-11-01)"; for R-ADM-02 in a rehearsal, "A separate System Administrator will be added by the System Administrator after setup."

**Production readiness note (from reading the code; not run in Production).** The platform's own checks include R-DOC-01, which is **Unhealthy** in Production while no real malware scanner is connected, and **no real scanner adapter exists** (not built; Not Verified). R-OPS-01 treats an Unhealthy platform check as **blocking**, and R-NOT-01 is blocking without a passing email test. So a Production run is expected to stop at this stage until a scanner adapter is built and connected. See the installation guide, section 9.3. Raise it with the owner; do not try to bypass it. Production-mode setup is therefore **Blocked**; a Test-mode rehearsal with acknowledged warnings is the supported use.

**Evidence.** Screenshot of the table with all rows Pass or acknowledged, and the reasons you typed.

---

## Stage 14: Finish setup

| | |
|---|---|
| Navigation | "Finish setup"; **/Setup/Step/complete** |
| Who | Business Administrator |
| Purpose | Close setup |
| Prerequisite | Stage 13 complete |
| Button | **Finish setup** |

It runs every check again. If any blocking check fails, or any warning has no acknowledgement, it refuses: "Setup cannot finish while checks are unresolved. Return to the readiness review." with the check ids.

On success: setup is audited (setup.completed), the token and session are cleared, **/Setup** answers "not found" from then on, and you land on the sign-in page with the message "Setup is complete. Sign in with the administrator you created."

**Evidence.** Before you press the button, save the setup report (section C). After it, the report is available from Installation setup.

---

## C. The setup report

| | |
|---|---|
| Navigation | During setup: **/Setup/Report** ("View the setup report" on the stage list). After setup: **Installation setup** (**/Admin/Setup**) |
| Who | System Administrator (after setup: permission setup.run) |
| Contents | Generated time (UTC), mode, deployment, version, whether setup is complete, the pack, administrator names, a table of stages with status and time, every readiness check with its result and any acknowledgement (who and why), integration kinds (database provider, identity kind, email kind, storage provider) |
| Never contains | Secrets, secret references, connection strings, tokens, hashes, client ids, authority addresses or email addresses |
| Download | **Download as JSON** at the foot of the page. A hint repeats that it contains no secrets |

Keep a copy with the date. It is evidence for the owner; it is **not** an approval.

---

## D. After setup

1. **Sign in.** The sign-in page shows "Sign in with" and your provider's display name. The first administrator signs in and becomes active. They hold Tenant Administrator and, for the first organisation, Organisation Administrator.
2. **Installation setup (/Admin/Setup).** Main menu entry **Installation setup**, visible only to installation administrators (permission setup.run). It shows "Steps you can reopen" and the report. Reopening needs a recent sign-in (within 10 minutes; otherwise you are sent to confirm who you are again). **Amendable stages:** document storage, identity provider, branding, classification levels, email notifications, readiness review. The tenant, administrator and organisation stages are closed for good; use user administration and the organisation screens. Each change is audited.
3. **Second administrator.** Create another administrator so that a configuration change can be published by a second person. In Production, and in Test when `Ecp:Governance:SecondPerson` is `Enforced`, the person who last edited or submitted a draft cannot publish it, and a change after submission voids the submission.
4. **Live email.** Nothing to copy: live delivery reads the configuration saved in stage 11. Ask the System Administrator to re-run the test on **/Admin/Operations/Email** before go-live and whenever it reads stale.
5. **Next guides.** Organisation onboarding (ORGANISATION_ONBOARDING_GUIDE.md), the business administrator guide (BUSINESS_ADMIN_GUIDE.md), pack implementation (PACK_IMPLEMENTATION_GUIDE.md), and the proof that the business journey works without code, `docs/NO_CODE_REHEARSAL.md`.

---

## Evidence to keep

| Evidence | Source |
|---|---|
| Date, time and operator of the session; nothing of the token | You |
| A screenshot of each stage's success message, or the saved setup report | Each stage; section C |
| The setup report (JSON) before and after Finish setup | Section C |
| The reasons typed for acknowledged warnings, by check id (including the scanner) | Stage 13 and the report |
| The governance mode line and the email test reference | Stage 1 and stage 11, and the setup report |
| The registered redirect addresses and the provider key | Technical sheet (installation guide) |
| The names of the first administrator and the installation administrator | Stage 7 |

---

## If something goes wrong

| Symptom | Cause | What to do |
|---|---|---|
| A stage says "Complete first: …" | An earlier stage is not done | Do the named stage |
| "This step was not completed" and a message | Validation or a technical problem | Read the message; correct the field or ask the System Administrator; press the button again. Nothing is half-saved except what the message says |
| The pack list is empty | Production with no trusted signed pack | Installation guide, section 6 |
| The provider save fails with "The provider settings are not valid." | Fields or an unresolved secret reference | Fix the field named; in Production the secret file or variable must exist inside the container |
| Test sign-in fails | Redirect address mismatch, or the administrator's email does not match, or stage 7 not done | Installation guide, section 8.2; compare the email to what the provider asserts |
| Readiness shows a blocking row | A real problem | Fix it; blocking rows cannot be acknowledged |
| Email stage says "The email test did not pass, so email is not ready" | The provider could not be reached, TLS failed, a secret reference did not resolve, or delivery was refused | Read the diagnostic code and reference; the System Administrator corrects the settings (also on **/Admin/Operations/Email**) and the test is run again |
| A page shows `ECP-NET-001` or `ECP-NET-002` | HTTPS or proxy settings (installation guide, section 8.1) | Technical task for the System Administrator: `docs/REVERSE_PROXY.md`. Quote the reference |
| A `[[...]]` token is visible | A wording defect | Report the page and time; it should never be shown |
| Finish setup refused | Unresolved checks | Return to stage 13 |
| The session ended | 60 minutes idle, or a new token was issued | The System Administrator issues a new token; your progress is kept |
| You made a wrong choice that is closed for good (tenant name, reference prefix, provider key) | By design | For tenancy, widen on **Organisations**. For the others there is no screen; ask the supplier before you continue. For a rehearsal, start again with an empty database |
| Every page except the live health check says "The installation state could not be read" | The setup state file is damaged or its key ring is gone | Restore the data folder from backup; there is no other documented recovery |

Escalate with: the stage, the exact message, the date and time (UTC) and the setup report. **Never send tokens, secrets, connection strings or live confidential data.**

*Written 2026-10-18 by reading the stage definitions, the setup pages, the readiness checks and the pack data in the repository. Verified over HTTP only in Test mode (see the status label); not followed by a non-developer on a clean machine. This guide does not approve any installation for production use.*
