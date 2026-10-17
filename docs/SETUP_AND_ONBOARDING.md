# First-run setup and organisation onboarding

Status of this document: describes what is **implemented** in this build (2026-10-16) and states plainly what is not. Requirements: `requirements/12-operations/SETUP_AND_READINESS_SPEC.md`. Decisions: `decisions/ADR-0016-setup-state-and-first-run-protection.md`, `decisions/ADR-0017-onboarding-as-a-configuration-kind.md`. Nothing here declares production approval; that is external governance.

| Capability | Label |
|---|---|
| First-run wizard (14 steps), resumable, idempotent | Implemented |
| Setup token, single session, loopback-only by default, closed after completion | Implemented |
| Re-authenticated reopening of selected steps by an installation administrator | Implemented (Demo identity confirmation verified; live OpenID Connect `prompt=login` re-authentication verified only against the in-process mock identity provider) |
| Readiness checks and sanitised report | Implemented (11 setup checks plus the platform's own checks; the full R-* catalogue is **not** built) |
| Organisation onboarding wizard (draft, validate, preview, simulate, publish, roll back) | Implemented |
| Taxonomy and structure import | Implemented as CSV text import (no file upload, no XLSX) |
| Solution pack choice at setup and onboarding from externally loaded, verified packs; Pack Manager for upload, install and assignment | Implemented (S7; `docs/PACK_MANAGER.md`) |
| AI-assisted onboarding, solution-pack marketplace, visual studio | Future (not started by design) |

## 1. How an installation is detected and gated

* Setup state lives in a **protected bootstrap file** (`{data directory}/bootstrap/setup.state`, ASP.NET Data Protection, key ring in the data directory) from the first start, before any database exists. Once the database is migrated the same document is **mirrored** into `setup.SetupRecord`. On load the newer revision wins, so losing either copy loses nothing.
* `{data directory}/bootstrap/setup.complete` is a non-secret marker written on completion. If it is missing but the database says complete, the marker is restored. If the bootstrap file is unreadable (damaged or key ring lost) setup **refuses to continue** and says so; it never silently starts again.
* **Incomplete installation:** browsers are redirected to `/Setup`; the API answers `503` with a problem document (`setup_required`); `/health/*`, static files and the sign-in challenge stay reachable.
* **Seeded Demo** (`Ecp:DemoSeed=true`, the default for `Ecp:Mode=Demo`) is a complete installation from the start, so the Demo still starts without credentials. A host with `Ecp:DemoSeed=false` starts unconfigured.
* **Complete installation:** every `/Setup` path is `404`.
* An existing populated installation that predates setup state has no record and is treated as **incomplete**. There is no "adopt existing installation" command yet (see section 8).

## 2. Opening setup (token, session, network)

1. On the server run `dotnet Ecp.Host.dll setup-token` (in a container: `docker exec <container> dotnet Ecp.Host.dll setup-token`). A 256-bit URL-safe token is printed **once to that terminal**. It is never written to a log or file; only its SHA-256 hash is stored. It expires after 24 hours and replaces any earlier token (and ends any open session).
2. Open `/Setup` from the server and paste the token. The token is **consumed**; a session cookie (`ecp.setup`: HttpOnly, SameSite=Strict, path `/Setup`, Secure in Production or on HTTPS) is issued. Only its hash is stored.
3. **One session at a time**; it ends after 60 minutes without activity (activity slides it) or when a new token is issued. Five wrong tokens lock entry for five minutes.
4. **Loopback only.** A caller that is not on the server sees `404` for every `/Setup` path, unless `Ecp:Setup:AllowRemote=true` is set in host configuration (then the token is still required). A container reached through a published port needs that setting, or run the wizard through `docker exec` / host networking.
5. Every form is antiforgery-protected. The rate-limit policy `signin` applies to the token form.

## 3. The steps

Stages run in this order; each saves its progress, can be repeated safely, and refuses to run before the steps it depends on.

| Step | What it does | Notes |
|---|---|---|
| `mode` | Confirms the host mode (set by `Ecp:Mode`, not changeable here), deployment (Local, IIS, Azure, Container) and tenancy | Demo/Test require acknowledging that the installation is not for real data. Production re-runs the Production guard and **refuses** a lightweight database, an inline connection string, ephemeral storage, Demo identity and Demo data |
| `database` | Checks the connection and that every module's schema is current | Demo/Test apply migrations; Production only verifies and tells you to run `migrate` |
| `storage` | Write, read, existence and delete round trip with the configured document store | |
| `tenant` | Creates the tenant (or adopts one of the same name left by an interrupted run) | Tenancy cannot be changed once the tenant exists |
| `pack` | Lists the packs the **pack loader** finds (shipped files that are compatible and trusted, or explicitly allowed unsigned synthetic packs in Demo and Test); you choose one or **None** | Nothing is chosen for you and no business is the default. It shows each pack's name, version, publisher, capabilities and signature, and says plainly when only Blank is available. Production lists only trusted packs. Data only; nothing in a pack is executed (`docs/PACK_MANAGER.md`) |
| `identity` | Saves the sign-in provider: Microsoft Entra ID, generic OpenID Connect, or Demo identity (Demo/Test only, and only if host configuration enabled it) | The client secret is a **reference** (`env:`, `file:`, `keyvault:`), never a value; a literal is refused. The provider is registered for the tenant **and** for installation administrators (two callback addresses) |
| `admin` | Names the initial administrator | Becomes Tenant Administrator, and (optional, recommended) installation System Administrator in the installation scope; invited until their first verified sign-in (active at once with Demo identity). Accounts link by issuer and subject, not by email alone |
| `organisation` | Creates the organisation (locale, IANA time zone, ISO 4217 currency, reference prefix) and publishes the pack selection | The initial administrator also becomes **Organisation Administrator** of this first organisation (see section 7, assumption ASM-21) |
| `branding` | Product name, accent colour (WCAG contrast checked), footer | Published as a versioned configuration release; amendable later |
| `classification` | Ordered classification levels and default | Published as a versioned configuration release; amendable later |
| `notifications` | Email provider: SMTP (TLS required in Production; credentials by reference), Mock (Demo/Test), Disabled (Demo/Test) | Sends a test message |
| `demo` | Optionally adds the synthetic demonstration data | Demo/Test only; the step does not exist in Production |
| `readiness` | Runs the checks. Blocking failures must be fixed; each failed **warning** needs a recorded reason (who, when, why) | |
| `complete` | Re-runs the checks, audits `setup.completed`, clears the token and session, writes the marker | |

Checks: `R-OPS-01` (database, data directory, ICU/time-zone), `R-ST-01` storage, `R-SEC-01` Production guard, `R-DEMO-01` no Demo identity or data in Production, `R-ID-01` provider valid, `R-ID-02` test sign-in succeeded (blocking in Production), `R-ADM-01` active administrator (invited counts as a warning, blocking in Production), `R-ADM-02` installation administrator named, `R-ORG-01`, `R-CFG-01` pack, branding and classification published, `R-NOT-01` email delivers (Mock/Disabled is blocking in Production), `R-AUD-01` audit chain verifies.

**Test sign-in.** On the `identity`, `admin` and `readiness` steps, *Test sign-in* sends the administrator through the real provider and signs them in; success records `R-ID-02`. This is also the administrator's first sign-in, which links their account.

## 4. After completion

* `/Setup` is `404`. Reopening is for a **signed-in installation administrator** (permission `setup.run`, System Administrator only in the confirmed matrix) at **Administration > Installation setup** (`/Admin/Setup`): the sanitised report (HTML or JSON) and the reopenable steps.
* Reading the report needs only the permission. **Changing** a step needs a **recent authentication** (within 10 minutes). Otherwise the page sends the person to `/Reauth`, which asks a real provider to authenticate again (`prompt=login`, `max_age=0`) or, for the Demo identity, asks for an explicit confirmation.
* Amendable steps: `storage`, `identity`, `branding`, `classification`, `notifications`, `readiness`. The tenant, administrator and organisation steps are closed for good (use user administration and organisation settings).
* Every change is audited (`setup.*`, `config.published`, `identityprovider.*`).

## 5. Sanitised report

Contains: generated time (UTC), mode, deployment, version, step statuses, every check with its result and any acknowledgement (who and why), integration kinds (for example `Sqlite`, `Oidc`, `Mock`), the pack, administrator display names. **Never** contains: tokens or their hashes, session ids, secret references or values, connection strings, authority URLs, client ids or email addresses. A test asserts this against a completed installation.

## 6. Organisation onboarding

`/Onboarding` (permission `onboarding.run`; the configuration module additionally enforces `config.draft`, `config.publish` and `config.rollback` for the organisation).

* The work is a **draft** of an `OrganisationOnboarding` configuration document (schema version 1) that names things by business key and email address: pack, structure (legal entities, divisions, business units, sites), categories, contract types, category owners, viewer scopes and identity-group mappings.
* Steps: *Solution pack and classification*, *Structure*, *Categories*, *Contract types*, *Category owners*, *Viewer scopes*, *Group mappings*, *Review*. Each step shows contextual help, the CSV columns and an example, and accepts CSV text (header optional; `;` separates several values in a cell; at most 5000 lines of 1000 characters). A bad import is rejected **as a whole**, with line numbers, and the draft is unchanged.
* **Validate** checks keys, duplicates, parents and cycles (depth at most six), that every named person already exists in the tenant, that sites, business units and categories exist, viewer-kind rules (All Contracts is not narrowed; Category, Business Unit, Site need their dimension; Restricted needs at least one, and warns on only one), that ceilings are in the classification scale (warning at the highest level), and that group mappings use only roles onboarding may grant. **Administrator roles and approver roles cannot be mapped by onboarding** (they are assigned in user administration under delegation rules).
* **Plan** counts what would be created, what is already present and what the previous release listed that this draft drops.
* **Synthetic preview and access simulation.** The proposed viewer scopes and mappings are evaluated by the **real access evaluator** against synthetic records across the proposed categories, business units, sites and classification levels, for each person or group. Nothing real is read or written.
* **Publish** requires a draft that validated without errors and has not changed since. Publication is a versioned, audited configuration release (second-person publish applies in Production when configured). Applying is idempotent: matching items that already exist are reused; nothing is ever deleted.
* **Roll back** publishes an earlier release's content as a new release, re-validated against today's rules. Only items an onboarding release itself **created** (recorded in `setup.OnboardingItem`) are ended when a later release drops them: structure and categories are deactivated, ownership and viewer roles are ended, mappings deactivated. Things that existed before onboarding, or were created by other means, are never touched. An item that a later release lists again is reactivated.
* Contract types are validated and recorded with the release; contract creation will consume them in a later milestone (**Scaffolded**: recorded, not yet used).
* A **Tenant Administrator** can open onboarding but is told that organisation-level configuration needs an Organisation Administrator or Business Configuration Administrator (the confirmed matrix gives Tenant Administrators configuration rights over tenant-level definitions only). Setup makes the initial administrator an Organisation Administrator of the first organisation so onboarding can start at once.

## 7. Deviations from the specification, assumptions and open points

| Ref | Item |
|---|---|
| ASM-20 | The setup token is issued by a command run on the server and printed to that terminal, not printed to the console log at start-up (`SETUP_AND_READINESS_SPEC.md` section 1 says "console/log"). Not logging a bearer credential is the safer reading; reversible |
| ASM-21 | The initial administrator is also made Organisation Administrator of the first organisation. A Tenant Administrator could grant themselves that role (delegation allows it) and could otherwise not run onboarding; granting it in setup only removes a step. Reversible |
| ASM-22 | Installation (System) administrators sign in through the same provider registered in the installation scope. A person who is both has two independent accounts (one per scope), consistent with "a person in two tenants has two users". The sign-in page lists both providers for the single-tenant case |
| ASM-23 | Stage numbering differs from the specification's S1 to S18: 14 stages are implemented; supplier and taxonomy CSV import beyond onboarding (S13), deployment-specific stages and the full R-* catalogue are not |
| ASM-24 | Onboarding group mappings cannot grant administrator or approver roles (approvers need an approver group, which onboarding does not model) |
| Open | Q-06 (final classification level names): the placeholder scale is editable in setup |
| Not built | Adopting an existing populated installation that has no setup record; file upload and XLSX for onboarding import; supplier CSV import; role templates and delegated-administrator creation inside the wizard; stage S13/S14 dry-run import for suppliers; automated accessibility tests of the wizard (A11Y-009 **Not Verified**; markup follows the accessibility rules but was only reviewed by hand) |

## 8. Running it

```
dotnet Ecp.Host.dll migrate                  # Production only, before setup
dotnet Ecp.Host.dll setup-token              # prints a one-time token
# open http://localhost:<port>/Setup from the server and follow the steps
```

Configuration keys: `Ecp:Mode`, `Ecp:DemoSeed`, `Ecp:DemoTenantResolution`, `Ecp:DataDirectory`, `Ecp:DefaultPackId` (the installation default solution pack, used only for an organisation that has none of its own; unset by default, so such an organisation is refused rather than given some business's pack: ADR-0024), `Ecp:Setup:AllowRemote` (default `false`), `Database:*`. See `docs/ENVIRONMENT.md` and `deploy/README.md`.

## 9. Tests

`tests/Ecp.Integration.Tests/SetupScenarios.cs` (13 scenarios, SQLite and SQL Server), `OnboardingScenarios.cs` (7 scenarios, both providers, plus a service-graph cycle guard), `tests/Ecp.Api.Tests/SetupHostTests.cs` (10 through the real host: gate, token, session cookie flags, antiforgery, end-to-end wizard, closure, restart mid-way and after completion, step-up reopening, the CLI in a separate process, remote callers), `OnboardingHostTests.cs` (4), `tests/Ecp.UnitTests/SetupStageTests.cs`, `OnboardingRulesTests.cs`. See `docs/TESTING.md` section 3 for counts and the Passed / Failed / Skipped / NOT VERIFIED split.

## 9. Solution packs in setup and onboarding (S7)

* **Setup, `pack` step.** The step reads the packs from the same loader the rest of the platform uses. It does not select any pack for you: the first option is empty ("Choose a starting pack") and the step refuses to continue until you choose one, so no business is ever the default. Blank is an ordinary pack in the list. If only one pack is available the step says so and explains how to add others (Administration, Solution packs); if none is available it says why and where to look. Each choice shows the pack name, version, publisher, capabilities and whether it is signed; an unsigned pack carries a warning and appears only in Demo or Test when `Ecp:Packs:AllowUnsignedSyntheticPacks` is on. **In Production only a pack signed by a trusted key is offered**, so a new Production installation must have its shipped packs signed (or its own signed packs mounted) and its public key trusted before this step can be completed (`docs/PACK_SIGNING.md` section 5); until then readiness reports the packs as Degraded and the step explains why none is offered.
* **Onboarding, *Solution pack and classification*.** The select box lists the usable packs (latest usable version of each key) and **None (start from the Blank configuration)**. For each pack the page previews who published it, how it is signed, which kinds of configuration it carries (workflows, notification policy) and how many starting categories and contract types it brings. Ticking "also add the pack's categories and contract types" merges them into the onboarding **draft**; nothing is removed, nothing is published, and the organisation is not assigned the pack's workflow definitions by this step. Assigning a pack's workflow and policy drafts is a separate, explicit action in the Pack Manager (**Organisation packs**).
* **Readiness.** The host adds the check `solution-packs`: a missing pack folder or an unusable installation default pack is *Unhealthy*; no usable shipped pack (for example only unsigned packs in Production) is *Degraded* with what to do.
* **Existing installations.** Nothing is rewritten. Organisations whose pack cannot be resolved are listed (report only) under Administration, Solution packs, Checks.

## 10. Terminology in setup and onboarding (S4)

* **Neutral by default.** Setup and onboarding text is written with terminology tokens and examples are neutral (`Review-Team`, `Finance-Viewers`, `GENERAL`, Europe/London, `USD`, `Example Holdings Ltd`). A Blank organisation sees no JBS or procurement wording anywhere in either wizard (`TerminologyUiTests.Onboarding_pages_for_a_blank_organisation…`).
* **Wording before assignment.** In onboarding, *Solution pack and classification* shows, for the pack you select, the terminology it would introduce and a handful of example labels (what a contract, counterparty, reviewer would be called), before anything is assigned. Choosing the pack prepares an organisation `Terminology` **draft** from the pack's wording; it never publishes, never overwrites a published release or an open draft, and the page says so. Overrides are possible only after a pack is selected, on the Terminology pages the step links to.
* **Plain-language guidance** uses the labels of the organisation being onboarded. The setup report and onboarding review are written with tokens and expanded for the organisation.
* **Not changed:** the CSV column names (`email`, `viewerKind`, …), step identifiers and routes are machine identifiers.

## Capabilities in setup and onboarding (S6)

* **Setup, `pack` step.** The notes under each pack now say which optional steps it switches off (Legal Review, Register Authorisation, Variations, Renewals, Signature Requests), read from the pack's own `Capabilities` document, and where to change them later (Administration, Capabilities). A pack that gives none says all are on. Nothing is chosen for you and nothing is published.
* **Onboarding, pack step.** Each pack shows an "Optional steps" list with on or off and the pack's note.
* **Onboarding, group mapping.** Mapping a group to the Legal role while Legal Review is off gives a warning (`role_unused_capability`), not an error: the capability may be switched on later.
* **Roles.** Which roles a picker offers is still decided by terminology role visibility (ADR-0029 D8); capabilities do not hide or revoke a role.

See `docs/CAPABILITIES.md`.

## Onboarding a second business (S9)

The onboarding wizard runs for a Harborline organisation from pack selection to publish with no platform change. The test drives the same service the pages call (`IConfigurationBootstrap` and the onboarding document) for an organisation with two sites in different time zones, trade packages as the category tree, contract types, category ownership and group mappings, then publishes the organisation-level drafts; the two tenant-level drafts (classification policy, branding) are created for a tenant administrator. The pages for the steps (`/Onboarding`, `pack`, `structure`, `categories`, `types`, `review`) render the pack's words for that organisation: the category step says Trade Packages and the type step Subcontract Types. The "Legal Review is off" note shows for the pack. Limits: the Pack step lists every installed pack by its own name, so it is not part of the neutral-wording scan; tenant-level policy and branding apply to a whole tenant, so two businesses in one tenant must agree on them.

## Regional settings in setup and onboarding (S10; `docs/REGIONAL_SETTINGS.md`)

* **Setup, Organisation step** asks for the organisation name, **country or region**, **locale**, **time zone**, **default currency**, **tax-identifier type** and reference prefix. The selected pack *proposes* values (JBS: Australia, en-AU, Australia/Brisbane, AUD, ABN; Harborline: United States, en-US, America/Chicago, USD, Federal Tax ID); the **Blank pack proposes nothing**, so the fields are empty and required, and the person decides explicitly. A value the person types is never silently replaced by the pack's. Codes are checked against the platform catalogues (an unavailable zone, an unknown currency or a malformed locale is refused with a plain message).
* The step publishes the first `RegionalProfile` release for the organisation through the normal configuration lifecycle and records the effective locale, zone and currency on the organisation. Running setup again does not overwrite an existing profile's later edits.
* **Onboarding, Review** shows whether the locale, default currency and time zone are decided and links to **Regional settings** (`/Admin/Regional`), where the person previews dates (including both daylight-saving offsets), numbers, amounts with their code, address fields and identifier types before publishing. An organisation that has not decided all three is reported as not ready for Production.
* **Pack assignment creates drafts only**: a regional draft that inherits from the pack is prepared; nothing published changes, and a later pack upgrade does not change published settings.
* A non-Australian organisation never sees "ABN" unless its profile (or a pack it chose) configures that type; a Blank organisation sees no identifier field until it configures one.
* **Limit**: the setup form shows help text, not a live preview of formats; the preview lives on the Regional settings page once the organisation exists.
