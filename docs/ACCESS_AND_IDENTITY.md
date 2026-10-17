# Identity providers, access scopes and the Viewer model (as implemented)

Status: describes the code in this build (2026-10-16). Requirements: `requirements/05-security/ACCESS_CONTROL_SPEC.md`, `requirements/07-integrations/IDENTITY_SPEC.md`, `requirements/02-reference/role-permission-matrix.csv` (confirmed decisions D1, A3, A4). Design: `docs/SECURITY.md` sections 3 and 4, `decisions/ADR-0007-identity-and-authorisation.md`.

## 1. Identity providers

| Provider | Status | Notes |
|---|---|---|
| Generic OpenID Connect (code flow, PKCE) | Implemented; verified against an in-process mock identity provider (`OidcSignInTests`, 11 tests) | `MapInboundClaims=false`; client secret is a reference resolved at run time; HTTPS authority required (loopback HTTP only outside Production); sign-in handlers are rebuilt when settings change, no restart |
| Microsoft Entra ID adapter | Implemented and validated against the mock with Entra-shaped claims; **live Entra tenant: Not Verified** | Directory (tenant) id must be a GUID (`common`, `organizations`, `consumers` refused); authority derived from it; account key is the object id; token `tid` must equal the configured directory; groups overage detected and fails closed |
| Demo identity | Implemented, Demo and Test only | Pick a synthetic user, no credentials, loopback unless `Identity:Demo:AllowRemote`. Production start-up is refused while it is enabled (R-ID-01); the service itself refuses to sign anyone in outside those modes |
| Production | Refuses Demo identity, Demo data, lightweight database, inline secrets, ephemeral storage (`ProductionGuard`, tests `ProductionGuardTests`, `ProductionRefusalTests`, setup `mode` step) | |

Accounts are **invite-only** and link by `(issuer, subject)`. An email address links an account only when the account is invited and unlinked and the provider asserts the address as trusted. Users are per tenant (a person in two tenants has two users). Identity-group mappings (`rolemapping.*`) grant and end roles at each sign-in. The session is a signed cookie (HttpOnly, SameSite=Lax, Secure in Production, 8 hours sliding) holding the user and tenant chosen from the **verified** identity, never from request input; a disabled account or suspended tenant ends the session on the next request.

## 2. Role assignments and scopes

A role assignment (`identity.RoleAssignment`) is effective-dated (`EffectiveFromUtc` / `EffectiveToUtc`) and carries a **scope**: level (Installation, Tenant, Organisation), organisations, optional business units, divisions, sites, an optional category list (null = all categories; a parent covers its sub-categories), and a **classification ceiling**. An organisation-level assignment without an organisation is refused.

Delegation (`DelegationPolicy`): a grantor can grant only roles and scopes within their own; administrative grantors other than a Tenant Administrator cannot grant the highest classification level (ASM-12); the last administrator cannot be removed (`LastAdministrator`). Trusted internal grants (setup, onboarding, Demo seeding, group mapping) go through `GrantTrustedAsync` with a recorded source; they are not reachable from request input.

## 3. Evaluation (`AccessEvaluator`, pure, 8 layers)

1. account state; 2. tenant (another tenant's record **does not exist**: 404); 3. scope **intersection** (organisation, business unit, division, site, category; a dimension that does not apply to a resource type never narrows it); 4. role permission from the embedded confirmed matrix (`Y` / `S` / blank); 5. relationship for `S` cells (active category ownership, assigned approval, tenant-level definition, policy-authorised); 6. classification ceiling (an out-of-scope **or above-ceiling** record is 404, not 403); 7. lifecycle and lock; 8. segregation of duties. An in-scope permission failure is 403 and audited as a denial.

The same rules produce **list predicates** (`AccessGrant` to a LINQ filter, `ContractAccessFilter`), so lists never show what a direct read would hide; a test (`GrantProjectionTests` and the scenario suite) keeps the two consistent. Server-side resource authorisation is applied in `ISecuredContractService`, `ISecuredSupplierService`, `ISecuredCategoryService`, the configuration service and the audit browser; page attributes only gate navigation.

## 4. Viewer model

A **Viewer is never a global reader.** Viewer kinds are scope assignments, not code paths:

| Kind | Scope the assignment carries |
|---|---|
| All Contracts | whole organisation(s), up to the ceiling |
| Category | category list (a parent covers its descendants) |
| Business Unit | business unit list |
| Site | site list |
| Restricted | the **intersection** of whichever of category, business unit and site are set |

Access is the intersection of tenant, organisation, business unit, division/site, category, classification ceiling and the role's permission. **Classification overrides otherwise valid access.** A **Category Owner** reads every contract in the organisation within the ceiling (read-all does not narrow by category) but edits only contracts whose category tree they currently own (effective-dated ownership; ending ownership removes edit rights at once; locked records are never editable). Tests: `AccessScenarioTests_*` (27 scenarios on each provider: every viewer kind, category owner, legal, procurement administrator, tenant-wide auditor, administrators and approvers without a relationship see no contracts, direct read is 404 outside scope or above ceiling, 403 for an in-scope user without the permission), `ViewerScopeTests`, `ClassificationOverrideTests`, `CategoryOwnerTests`, `RoleBoundaryTests`, `EffectiveDatingAndAccountTests`, `MatrixDrivenAuthorisationTests`.

## 5. Administration roles in the confirmed matrix (a finding)

* **Tenant Administrator** holds `onboarding.run` and `users.manage`, but `config.draft`, `config.publish` and `config.rollback` are `S` with the *tenant-level definition* relationship: they apply to tenant-level definitions only. Organisation-level configuration (including onboarding) is done by an **Organisation Administrator** (`Y` for draft, `S` with no extra relationship for publish and rollback) or a **Business Configuration Administrator**. The wizard explains this instead of failing silently, and setup makes the initial administrator an Organisation Administrator of the first organisation (ASM-21).
* **Installation (System) Administrator** is the only holder of `setup.run`.
* **Auditor** reads and exports audit evidence; reading business values requires `audit.read` for the event's organisation, administrative readers (`audit.read_admin`) see administrative events without business values.

## 6. Pages (all authorised on the server; every state-changing page has antiforgery validation)

Home (role and scope summary, contract counts by status), contract register, details, create, edit; supplier list, details, create; category administration and ownership; user and role administration (invite, grant, revoke, disable, reactivate; identity providers are configured in the setup *identity* step and identity-group mappings in the onboarding *group mappings* step, there is no separate administration page for either yet); access simulation; audit browser and evidence export; health and readiness; sign-in, sign-out, `/Reauth`; installation setup (`/Setup`, `/Admin/Setup`); organisation onboarding (`/Onboarding`). A demonstration banner shows outside Production. Navigation shows only what the person can use (cosmetic: the server decides).

## 7. Terminology and roles (S4)

Role codes are stable machine identifiers and were not renamed (`ProcurementAdministrator`, `CategoryOwner`, `Legal`, `Approver`, `BackupApprover`, …). The **label** shown for a role comes from terminology: for example `ProcurementAdministrator` reads *Contract Administrator* in a neutral organisation and *Procurement Administrator* in the JBS reference organisation. Administrator pages show the code next to the label. Authorisation never reads a label. Five permissions were added (`terminology.read`, `.draft`, `.publish`, `.rollback`, `.audit.read`; 82 in total), granted only to Tenant Administrator, Organisation Administrator, Business Configuration Administrator and (read and audit) Auditor; see decision D7b and `docs/TERMINOLOGY.md` §11. Replacing the role catalogue itself remains S5/S8.
