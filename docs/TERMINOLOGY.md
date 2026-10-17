# Terminology

Status: **Implemented** in portability slice S4 (ADR-0028). Everything below is implemented unless it is marked *future*. Production approval remains external governance.

This is the guide for how the platform decides what to call a business concept. It covers the model, the resolver and its cache, how packs and organisations supply wording, the token language used in messages, the administration pages, permissions, compatibility, and what is deliberately left unchanged.

## 1. The rule in one paragraph

A **stable key** (for example `supplier`, `legal.review`, `contract.owner`) names a concept inside the platform. A **label** (for example *Counterparty*, *Supplier*, *Partner*) is only what a person reads. The platform never decides authority, routing, state, storage or an audit action from a label. Changing a label changes what people read and nothing else.

## 2. Stable key versus label

| | Stable key | Label |
|---|---|---|
| What it is | A machine identifier owned by the platform | Text a person reads |
| Examples | `supplier`, `contract.owner`, `ProcurementAdministrator`, `AwaitingLegal`, `suppliers.manage`, `configuration.published` | Counterparty, Supplier, Partner, Contract Administrator, Procurement Administrator |
| Who changes it | Nobody, except through a reviewed migration | An organisation (published terminology) or a solution pack |
| Used by | Code, permissions, workflow definitions, audit, URLs, form fields, APIs, exports' machine columns | Pages, messages, notifications, exports' headings, help, accessible labels |

**Not renamed in S4** (the owner's decision, ADR-0028 D2): the role codes `ProcurementAdministrator`, `CategoryOwner`, `Legal`, `Approver`, `BackupApprover` (and the other seven), every existing permission code, workflow state codes, configuration-kind keys, audit action codes, pack identifiers, URL paths (`/Suppliers`, `/Admin/Categories`), form field names and database columns. Renaming these would be a migration with risk and no benefit, because the stable key is never shown to an ordinary person. Administrator pages show the key beside the label (for example `Contract Administrator` with `ProcurementAdministrator` in a code element) so nobody has to guess.

## 3. Where a label comes from (resolution order)

For an organisation, a locale and a key, the first layer that has an active entry wins:

1. **Organisation-published terminology** (the `Terminology` configuration kind, §6).
2. **The organisation's assigned solution pack** (the pack's `terminology` document, §5).
3. **Neutral platform terminology** (`TermCatalogue`, compiled in; it names no business).
4. **The stable key**, only as a last-resort diagnostic, for a key that is not in the catalogue at all.

Within a layer, the locale chain is the exact locale then its language (`en-AU` then `en`). An entry may mark itself inactive to *remove* an inherited override: the label then falls through to the next layer. Overrides inherit everything they do not mention.

The neutral defaults are: Contract Administrator, Contract Owner, Reviewer, Approval Authority (the neutral label of the `Approver` role, owner decision D8), Backup Approver, Contract Type, Contract Portfolio, Contract Register, External Execution, Review, Approval, Organisation, Business Unit, Site, Counterparty. The **Blank** pack ships exactly these. The **JBS reference** pack ships procurement wording (Procurement Administrator, Category Owner, Legal Review, Group Head of Procurement, Supplier, Category, Subcategory, Procurement Contract Register) *inside the pack and nowhere else*. A synthetic third pack used in the tests (Contract→Agreement, Supplier→Counterparty, Category→Portfolio, Category Owner→Agreement Manager, Legal Review→Formal Review, Registration Approval→Activation Approval) proves that a pack the repository has never seen works with no source change.

## 4. The catalogue

`TermCatalogue` (`src/Ecp.SharedKernel/Terminology/TermCatalogue.cs`) lists 73 keys: the 32 **required** keys that a complete document must carry, and 41 optional keys: ten earlier ones (`agreement`, `registered.date`, `review.team` and the seven `role.*` keys for the administrative roles) and 31 **field-label keys** added in the S9 readiness work (below). Each has a group, neutral singular and plural, and a description used by the administration pages. Required keys: `application.name`, `contract`, `contract.register`, `contract.reference`, `contract.type`, `contract.portfolio`, `executed.document`, `document`, `contract.owner`, `contract.administrator`, `reviewer`, `primary.approver`, `backup.approver`, `counterparty`, `supplier`, `category`, `subcategory`, `legal.review`, `approval`, `registration.approval`, `external.execution`, `variation`, `renewal`, `notification`, `organisation`, `legal.entity`, `business.unit`, `division`, `site`, `solution.pack`, `migration.project`, `migration.batch`.

**Field-label keys (S9 readiness, G1a and G1b; PRT-004).** Every label a person reads for a contract field is a term, so a business can rename any of them without a code change. All 31 are optional: the neutral wording below applies until a pack or an organisation sets its own, and a pack built before this change stays valid.

| Group | Keys (neutral wording) |
|---|---|
| Identifiers (G1a; the reference pack sets today's words) | `supplier.number` (Vendor Number; reference pack: Vendor Number), `supplier.tax.id` (Tax Identifier; reference pack: ABN), `erp.reference` (ERP Reference; reference pack: SAP Outline Agreement Number) |
| Contract fields (G1b) | `field.description`, `field.owner`, `field.status`, `field.classification` (Data Classification), `field.risk.level`, `field.function`, `field.currency`, `field.estimated.value` (reference pack: Estimated Agreement Value), `field.extension.type`, `field.evergreen`, `field.notice.period`, `field.critical.notice`, `field.start.date`, `field.effective.date`, `field.review.date`, `field.end.date`, `field.locked`, `field.created`, `field.updated` |
| Field group headings (G1b) | `field.group.identity`, `.ownership`, `.lifecycle`, `.governance`, `.classification`, `.structure`, `.commercial`, `.dates`, `.audit` |

Where they are used: `CoreFields.All` (register columns, filters, views, exports), the contract and supplier forms and details, the Variation form and comparison, reminder schedule lines, dashboard cards, the supplier and numbering messages, and the migration and onboarding messages that name them. Pages use `[[field.end.date|sentence]]` for sentence case; the three identifier terms are used without a modifier because an acronym (ABN, SAP, EIN) must not be lower-cased. **Boundary:** general interface words (Next, Cancel, Reason, Name, Actions) and administrator-only diagnostics are platform vocabulary, governed by the localiser, not by terminology. The render test `Contract_and_supplier_field_labels_follow_the_organisations_words...` and the unit test `Every_core_field_label_and_group_is_made_of_catalogue_tokens...` protect the boundary. Known limit: the eleven-digit format check on the tax identifier is still the Australian rule (G2, deferred to S10), so a pack may *rename* the identifier but not yet change its format.

A term has a singular, a plural, and optionally a short form, a description and help text. Roles map to keys in one place (`TermCatalogue.KeyForRole`): `ProcurementAdministrator`→`contract.administrator`, `CategoryOwner`→`contract.owner`, `Legal`→`reviewer`, `Approver`→`primary.approver`, `BackupApprover`→`backup.approver`, and the seven administrative roles to their `role.*` keys. Variants: `en-US` spells *Organization*.

## 5. Pack terminology

A solution pack may carry an optional pack document terminology/terminology.json (content type `Terminology`, mode `complete`). Rules:

* **Optional.** A pack without it is still valid; its organisations read the neutral wording. Packs built before S4 (Blank 1.1.0, JBS 2.1.0) load unchanged.
* **Signed and hashed like every other document.** Its SHA-256 is in the manifest, so the pack's content hash and signature cover it; a tampered document fails verification. The content scan rejects markup, script and template syntax.
* **Complete.** It must carry all 32 required keys for each base locale it declares, with a singular and a plural.
* **Installed as a draft only.** Assigning a pack to an organisation seeds an organisation `Terminology` *draft* from the pack's document (every entry stamped with the pack and version it came from). It never publishes, and never overwrites a published release or an open draft. Until the organisation publishes, it reads the pack's wording directly (layer 2).
* **Previewed.** The pack review and assign pages list the terminology the pack introduces and how each label differs from what the organisation reads today. Setup and onboarding show the selected pack's wording and example labels before assignment.

Pack versions: Blank **1.3.0**, JBS reference **2.3.0** (S4 introduced the document at 1.2.0 and 2.2.0; the A+ follow-up of ADR-0029 added approver-group names) (both rebuilt; `docs/PACK_AUTHORING.md` §4 describes the document and how a new pack provides terminology).

## 6. The `Terminology` configuration kind

Organisation terminology uses the existing lifecycle: **draft → validate → preview → publish → version history → rollback → audit**, with second-person publication where `ConfigurationOptions.RequireSecondPersonPublish` is on (the person who drafted cannot publish) and the same recent-sign-in rule for publish and rollback. It is tenant- and organisation-scoped: a release belongs to one organisation, and one organisation can never read or change another's.

Content is an *overrides* document: only what the organisation changes. Validation errors (publication is blocked): unknown mode; more than 600 terms; a key not shaped like `contract.type`; a locale not like `en` or `en-AU`; a duplicate (key, locale); a missing singular or plural; limits (label 80, short 30, description 300, help 600 characters); leading or trailing space; line breaks in a single-line field; control characters; and **any markup, script, template or expression syntax** (`<`, `>`, `&entity;`, `javascript:`, `onclick=`, `{{ }}`, `${ }`, `<% %>`, `@(`, `expr:`). A key this version does not know is a warning (no effect), not an error, so a newer pack stays compatible with an older platform. A *complete* document (a pack's) must additionally carry every required key.

### 6a. Approver-group names and role visibility (ADR-0029)

The same document (a pack's, or an organisation's overrides) may carry two more optional sections. They are written only when present, so documents and packs without them are unchanged.

* **`groups`: approver-group display names.** `{ "key": "GroupHeadProcurement", "locale": "en", "name": "Group Head of Procurement", "description": "..." }`. The key is the stable identifier stored on role assignments and named in workflows (letters, digits, dot, hyphen, underscore; at most 100 characters; free text chosen by a pack or an administrator, which the platform gives no meaning). The name is plain text of at most 80 characters. Resolution: the organisation's name, then the pack's, then the key itself. Ordinary pages show the name (`Terms.Group`); administrator pages show the name with the key beside it (`Terms.GroupLabel`). Used by the approvals queue and detail, the approval matrix and simulator pages, delegations and user administration. Blank names `Approvers` *Registration Approvers*; the JBS pack names `GroupHeadProcurement` *Group Head of Procurement*.
* **`roles`: role visibility.** `{ "code": "Legal", "offered": false }`. A role that is not offered does not appear in the role picker when people assign roles (user administration lists which roles are not offered). It is **presentation only**: no grant is revoked, holders of the role are still listed, the trusted grant path and every permission check are unchanged, and `SystemAdministrator`, `TenantAdministrator` and `OrganisationAdministrator` can never be hidden (a document that tries is refused). The organisation's choice wins over the pack's; with neither, every role is offered. The picker offers a role when at least one organisation the grantor can delegate in offers it. This is the decision point that S6 (capabilities) will feed, for example a pack without Legal review hiding the Legal role.

Validation: a group key that is not shaped like a key, a duplicate (key, locale), a missing or over-long name, markup in a name, more than 200 groups, a role code that is not one of the twelve (a *label* is not a role code), a duplicate role entry, and hiding an administrative role are all errors and block publication. Both sections are edited on the Edit page ("Approver group names", "Roles offered when assigning"), appear on the Overview and Preview pages, and need the same `terminology.*` permissions as everything else on those pages.

## 7. The resolver and its cache

`ITerminologyResolver` (`Ecp.Configuration`) is the single read path: `GetAsync(organisationId, locale)` returns a `TermSet` resolved once for a page; `ResolveManyAsync` looks up many keys in one set (never one query per label); `PreviewAsync` shows a draft without caching; `PackLayerAsync` exposes the inherited layer to the admin pages; `HomeOrganisationAsync` answers "whose words describe things when no record is in view". Singular, plural, short, description and help are all available from the set. **Generic pages never read pack files**; the resolver reads the pack's terminology through the pack loader and nothing else does.

* **Home organisation rule.** The page-level terminology is that of the signed-in person's home organisation: the organisation of their first active organisation-level assignment, otherwise the tenant's first organisation (a tenant-wide role), otherwise the installation default pack. A page about one organisation's record (a contract, variation, legal review, approval, supplier, migration batch) calls `Terms.UseOrganisationAsync(record.OrganisationId)` and describes the record in *its* organisation's words. Anything shown to a person is presentation only.
* **Cache.** The key is (tenant, organisation, locale, **version**), where the version is the published release id plus the pack key and version. A publish or rollback changes the release, so the next lookup misses and rebuilds; `TerminologyKind.ApplyAsync` also invalidates the organisation's entries immediately. Entries expire after ten minutes and the store is bounded (2,000 entries; it clears when full). If the cache throws, the resolver counts a fallback, logs a warning and resolves from persistence. It is a single-process cache: with several instances, the release id in the key makes a publish visible everywhere on the next lookup, and the short expiry bounds the rest. Statistics (hits, misses, fallbacks, entries) are available to diagnostics.
* **Fail safe.** Any failure while reading terminology returns the neutral wording, never an error and never another tenant's labels. Diagnostics log the failure and correlation, never label values.

## 8. The token language

Text that the platform stores or writes in English and that mentions a business concept uses constrained tokens: `[[contract]]`, `[[contract|plural]]`, `[[supplier|lower]]`, `[[category|lower|a]]`. A token is a catalogue key plus optional modifiers `plural`, `short`, `lower`, `sentence`, `a` (adds *a* or *an*) and `cap`. There are no expressions, conditions, members or nesting. Expansion happens **before** `{0}` arguments are formatted, so a value that came from a person can never be read as a token.

* **Pages**: `@L["…[[token]]…"]` and `@Terms.Expand(text)`. `TermLocalizer` replaces the framework localiser so every `L[...]` call expands.
* **Domain messages**: `DomainException` keeps its raw text with tokens (`RawMessage`) and exposes `Message` with the neutral wording, so logs and API responses never show a token; the Host expands the raw text into the organisation's words for people.
* **Notifications, documents, reports**: notification templates may carry tokens. Unknown keys or modifiers, and a stray `[[`, **fail validation** (`term_token`) when the policy is saved, not when it is sent. The renderer expands tokens, then fills `{placeholders}`.
* **Stored text** (migration plan summaries and messages, setup reports): stored with tokens and expanded for display and in downloads, so a report is produced in the words of its organisation. Rows written before S4 contain plain words and are unchanged.
* **Audit**: audit *action keys* are stable and never tokenised. Historical audit text is never rewritten. A token never appears in an audit record's action; descriptions that already exist stay as they were written, so history remains interpretable whatever the current wording.

## 9. What is converted

Navigation and the header; home; the contract register (columns, filters, built-in views, saved-view names for system views, empty states); contract create, edit, details, lifecycle and unlock pages; supplier and category pages; the legal-review queue; approvals; variations; renewals; notifications and the inbox; dashboards; configuration studio, simulation and approvals administration; migration (projects, batches, messages, reconciliation, provenance, downloaded reports); setup and onboarding (including help and examples); pack manager; user and role administration; audit and health pages; validation messages, confirmations, flash messages and empty states; accessible labels and error summaries; CSV and XLSX headings (the worksheet name too, clamped to the spreadsheet's rules); and the notification policy of the JBS pack. Roles are labelled from the catalogue with the stable code shown beside the label on administrator pages.

**Not converted, by design** (machine identifiers, not labels): URL paths, form field names, JSON property names, database tables and columns, the machine column keys of reporting exports (`cf_…`), the fixed CSV header of the legacy contract export (`AgreementReference,ContractTitle,Supplier,…`, a data-interchange format), role codes, permission codes, workflow state codes, audit action codes, pack ids. Known remaining items are listed in §13.

## 10. Administration

Pages are form-based; no one edits JSON. They live under **Admin → Terminology** (`/Admin/Terminology`):

| Page | Purpose |
|---|---|
| Overview | Every key with its stable key, the current label, and **where it comes from** (organisation, pack, or neutral); what is changed in an open draft |
| Edit | One row per catalogue key; fields for override singular, plural, short, description, help; locale selector; a "differs from pack default" marker; save, validate, publish, discard. Entered values are kept when a problem is shown, and each problem is shown beside its row |
| Preview | Before and after for the draft, with a sample sentence per term, and the statement that only wording changes, not permissions or workflows |
| History | Every release; restore (rollback) to an earlier release |
| Audit | Terminology audit events for the organisation |

Accessibility: every input has a visible label and an id; problems are announced in an error summary and tied to the field; the tables have captions and scoped headers; no information is carried by colour alone. Setup and onboarding link to the same pages and, after a pack is selected, store its wording as a terminology *draft* for the organisation (never silently published).

## 11. Permissions

New permissions (`requirements/02-reference/permission-catalogue.csv`, `role-permission-matrix.csv`, decision D7b in `requirements/01-authoritative/CONFIRMED_DECISIONS.md`):

| Permission | Allocation |
|---|---|
| `terminology.read` | Tenant Administrator, Organisation Administrator, Business Configuration Administrator, Auditor |
| `terminology.draft`, `terminology.publish`, `terminology.rollback` | Tenant Administrator, Organisation Administrator, Business Configuration Administrator |
| `terminology.audit.read` | Same as `terminology.read` |

Not granted to: System Administrator (installation-level; no tenant data), Division/Site Administrator, Procurement Administrator (stable code), Category Owner, Legal, Approver, Backup Approver, Viewer. Publishing and rollback require the recent-sign-in policy (`/Reauth`) and second-person rules. Reading is org-scoped: a person can name only organisations their grant covers. Auditor sees the overview and audit but not the draft. The 77 existing permissions and every existing role assignment are unchanged; the catalogue now lists 82 (`AccessEvaluatorTests` asserts the count).

## 12. Why terminology does not alter security

* Authorisation is evaluated from role *codes*, permission *codes*, scopes and record facts. No evaluator, handler, route or query takes a label as input.
* Labels pass through `Terms.Expand` at the edge of a response. They are not read back from a request: a form posts keys and ids, never labels.
* Renaming `Procurement Administrator` to `Contract Administrator` (or the reverse) changes no grant, no scope, no workflow and no audit action. Naming an approver group or hiding a role from the picker changes none either; the role code, the permission code and the group key remain the permanent identifiers (ADR-0029, `tests/Ecp.UnitTests/FrozenIdentifierTests.cs`). A test signs in as the same role in a JBS organisation and a Blank organisation and asserts the label differs while the pages reachable are identical.
* Label text is plain text, HTML-encoded on output, validated for markup on write, and never evaluated as a template other than by the constrained token expander.
* Expansion never crosses tenants: the cache key and the release lookup carry tenant and organisation; the tests prove one organisation's wording never reaches another.

## 13. Historical records, compatibility and the known remaining items

* Contracts, documents, workflow states, approvals, audit entries, saved views, migration profiles, release hashes and pack identities are unchanged. Audit text is not rewritten; audit action codes are unchanged.
* Saved views keep their stored names (a person named them). Built-in views are written with tokens and shown in the organisation's words.
* The legacy contract CSV export keeps its fixed machine header.
* **Locale defaults** (`en-AU`, the Brisbane time zone and `AUD` in `appsettings.json`, `LocaleFormatter`, `OrgLocale`, `MigrationPageModel`, `CustomFields`, `GlobalisationProbe`) are *regional configuration*, not terminology, and are the S10 slice. They are the only fixed regional defaults and are enumerated in the guard's allow-list (§14).
* The owner's brief listed `GroupHeadApprover` among the stable identifiers. No identifier of that name exists; the persisted names are the role `Approver` and the approver group key `GroupHeadProcurement`. Both are kept unchanged and the contradiction is recorded in decision D7b (resolved by keeping what exists). Replacing the role catalogue is S5/S8.
* `DemoSeeder` maps the role names in the shipped sample CSV to stable codes. It is sample-data loading, allowed by name in the guard.

## 14. Portability guard, stage B

`tests/Ecp.Architecture.Tests/VocabularyGuardTests.cs` scans every text file shipped with the platform — C#, Razor, resources, JSON, project and props files, appsettings, deployment files, workflows, scripts, CSS and JavaScript under `src/`, `deploy/`, `.github/` and `tools/`, plus the root props files — case-insensitively for: vendor and business names (`JBS`, `JBSAUS`, `Medius`, `Qlik`, `DocuSign`); fixed business labels (`Group Procurement`, `Group Head of Procurement`, `Procurement Administrator`, `Procurement Contract Register`, `Category Owner`); fixed Australian defaults (`en-AU`, an Australian time-zone identifier, `Brisbane`, `AUD`); and capitalised business words typed into page text or `L["…"]` literals (`Supplier`, `Category`, `Legal Review`, …).

It does **not** scan `templates/` (pack sources), `sample-data/`, `migration-samples/` or `docs/` (documentation, including historical findings, may use any word). It does not flag stable identifiers (`ProcurementAdministrator` has no space) or tokens. The allow-list names single files with a reason; a stale allowance fails the build; the list is capped; and one test proves an exception covers only its own file and rule. It is fourteen entries today: eleven for S10 locale defaults and documentation examples, one for the audit-prefix `AUD`, one for the demo seeder, one for the Medius migration demonstration script.

## 15. How a new pack provides terminology

1. Copy the Blank pack's file templates/packs/blank/terminology/terminology.json, change the labels you need, keep all 32 required keys for each locale you declare.
2. Add the document to the pack source's recipe (`pack-source.json`) as content type `Terminology`.
3. Build, validate and sign with `ecp-pack` (see `docs/PACK_AUTHORING.md`). Install and assign through the Pack Manager. The organisation reads the pack's words immediately and can override any of them in a draft.

No code changes and no recompilation are involved; the `A_pack_the_repository_has_never_seen…` API test and the third-pack scenarios prove it.

## 16. A pack that renames everything (S9)

The Harborline pack (`docs/CONSTRUCTION_SUBCONTRACTS_PACK.md`) sets 52 terms: all 32 required keys and 20 optional ones, among them field-label keys such as "Subcontractor Number", "Job Cost Reference", "Risk Rating", "Mobilization Date" and "Scheduled Completion Date". Findings from the render sweep of four roles over the register, contract, lifecycle, approval, change-order, supplier, category, dashboard, notification, administration and onboarding pages (`tests/Ecp.Api.Tests/SecondBusinessUiTests.cs`):

* **S9-discovered generic fix F1.** `src/Ecp.Host/Pages/Shared/_ContractFields.cshtml` built its labels in a tuple literal ("Contract type", "Agreement type", "Risk level", "Function"), which the page-literal guard could not see, so they never passed through the resolver. They are tokens now (`[[contract.type|sentence]]`, `[[agreement]] type`, `[[field.risk.level|sentence]]`, `[[field.function|sentence]]`). Stage B of the guard has a new rule (`fixed-field-label-literal`) that refuses those four bare quoted labels in Razor, with no new allowance. This benefits every pack.
* **Acronym-capable terms are used unmodified.** The `sentence` modifier lower-cases the rest of the word, so a term that is an acronym or a proper noun is used as written.
* **Dates in notifications and pages are ISO** (G12), not locale-formatted; it is neutral but not yet en-US in appearance. Recorded in `docs/PORTABILITY_AUDIT.md`, not changed.
* **The Pack step of onboarding lists every installed pack by name**, so it is the one page the leak scan does not apply to.
* **The role picker hides a role only when every organisation in the administrator's reach hides it.** A tenant-wide administrator still sees Legal while any organisation offers it.

## 17. Regional, address and identifier wording (S10; `docs/REGIONAL_SETTINGS.md`)

* **New keys** (platform neutral wording in brackets): `supplier.identifier` (Identifier), `address` (Address), `address.line1` (Address Line 1), `address.line2` (Address Line 2), `address.locality` (City or Locality), `address.region` (State, Province or Region), `address.postal` (Postal Code), `address.country` (Country or Region), `regional.country` (Country or Region), `regional.locale` (Locale), `regional.time.zone` (Time Zone), `regional.date.format` (Date Format), `regional.time.display` (Time Display). `supplier.tax.id` stays "Tax Identifier".
* **Per business**: Blank keeps the neutral wording; JBS Procurement says Suburb or City, State or Territory, Postcode and ABN (2.6.0); Harborline says City, State, ZIP Code and Tax Identifier (1.1.0). An organisation can override any of them like any other term.
* **A tax identifier type with no label of its own** (`"label": ""` in a regional profile) is called by the organisation's word for `supplier.tax.id`, so a pack that only renames the term renames the field. A type with a label (such as "ABN" or "Federal Tax ID") shows that label.
* **Evergreen** is the stable *code* of an extension type; the words people read are `field.evergreen` (neutral: Evergreen). Fixed "Evergreen" text is refused by guard stage C. Critical Notice and Notice Period were already keys (`field.critical.notice`, `field.notice.period`).
* Exports and notifications use these words; a machine export keeps stable column keys.
