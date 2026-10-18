# Security Design

Status: **design baseline.** Normative requirements are in `requirements/05-security/` (`ACCESS_CONTROL_SPEC.md`, `AUDIT_SPEC.md`, `RETENTION_AND_CLASSIFICATION_SPEC.md`), `requirements/07-integrations/` and `requirements/12-operations/SETUP_AND_READINESS_SPEC.md`. This document says how they are met. Threats and test mapping: `docs/THREAT_MODEL.md`, `docs/TESTING.md`. The platform provides controls and evidence for customer assessment; it makes **no claim of legal compliance** and **never declares production approval** (`CLAUDE.md`).

## 1. Principles

1. Server-side enforcement only. The UI hides what a user cannot do; it never decides.
2. Deny by default; one access evaluator; no endpoint-specific shortcuts.
3. Tenant isolation is a hard boundary with several independent layers (section 4).
4. Least privilege everywhere: users, administrators, service principals, the database login, the container, the cloud identity.
5. No arbitrary code from configuration, packages, migration files or AI output (closed vocabularies).
6. Secrets are references; values are never committed, logged, displayed or exported.
7. Everything security-relevant is audited, tamper-evident and exportable as evidence.
8. Fail closed: if a check cannot be evaluated (scanner down, key missing, group overage unresolvable), the action is refused.
9. Synthetic data only in the repository and in sessions (`SECURITY-NOTICE.md`).

## 2. Assets and trust boundaries

| Asset | Sensitivity | Boundary protecting it |
|---|---|---|
| Contract content, documents, pricing | Confidential to Restricted | Tenant, organisation, classification, role, ownership, lock |
| Audit evidence | High | Append-only store, hash chain, `audit.read` / `audit.export` |
| Configuration and packs | High (integrity) | Draft, validate, publish, signatures, step-up auth |
| Identity assignments | Critical | `roles.delegate` limits, last-admin protection, audit |
| Secrets and keys | Critical | Secret provider, write-only entry, no logging |
| Setup surface | Critical until completion | Setup token, loopback default, route removal |
| Backups and exports | High | Encryption, tenant prefix, short-lived authorised links |

Trust boundaries: Internet ↔ reverse proxy/host; host ↔ database; host ↔ document store; host ↔ identity provider; host ↔ external adapters; tenant A ↔ tenant B; unauthenticated ↔ authenticated; authenticated ↔ privileged; configuration author ↔ runtime; uploaded content ↔ platform. Data-flow diagram and per-boundary threats are in `THREAT_MODEL.md`.

## 3. Authentication and sessions

- OpenID Connect Authorization Code + PKCE (`IDENTITY_SPEC.md`). Tokens validated for issuer, audience, signature, lifetime. Accounts linked by `(issuer, subject)`.
- UI sessions: `Secure`, `HttpOnly`, `SameSite=Lax` cookies, idle (30 min) and absolute (8 h) timeouts, rotation on privilege change, server-side revocation list; antiforgery tokens on all state-changing form posts; `SameSite` plus antiforgery for defence in depth.
- API: bearer tokens or client credentials; no cookie authentication on `/api` except same-origin UI calls that carry antiforgery headers.
- Step-up (`prompt=login`, `max_age`, optional MFA claim) for: setup changes, configuration publish, package import, legacy-load commit, emergency unlock, elevation, role delegation above a threshold.
- Demo identity is compiled in but **inert unless mode ≠ Production** (R-ID-01); selecting Production while it is enabled aborts start-up.
- Brute-force protection on any local credential flow (Demo only): rate limit and lockout; Production has no local passwords.
- Open-redirect protection: only local return URLs.

## 4. Authorisation and tenant isolation

### 4.1 Access evaluator

A single `IAccessEvaluator.AuthoriseAsync(principal, permission, resource)` is the only way to obtain a decision. It evaluates the layers in `ACCESS_CONTROL_SPEC.md` section 3 (including scope intersection across organisation, business unit, site, category and classification ceiling, section 2) and returns an explainable result (allowed/denied, layer, reason code). Controllers/pages declare required permission via attributes or call the evaluator; unannotated endpoints fail an architecture test. List queries are shaped by `IAccessScope` (tenant, organisation set, classification ceiling, category set) so that disallowed rows are never read. Relationship facts come from `IRelationshipProvider` implementations (category assignment, approver assignment) so rights follow **current** assignments.

### 4.2 Role model

Roles and permissions come from the catalogue and matrix (`requirements/02-reference/`). The test AUT-001 generates the authorisation matrix from `role-permission-matrix.csv`, so documentation and behaviour cannot drift. Delegation cannot exceed the grantor's rights (INV-60); the last administrator cannot be removed; role changes take effect within 60 seconds at most (cached per tenant and user).

### 4.3 Tenant isolation layers

See `ARCHITECTURE.md` section 6 for mechanisms. Security expectations:

1. Tenant is never read from client input. A request carrying another tenant's identifiers returns 404, indistinguishable from "does not exist".
2. EF global filter, SQL Row-Level Security via `SESSION_CONTEXT`, composite foreign keys with `TenantId`, and tenant-prefixed storage keys must each independently block cross-tenant reads. Tests disable one layer at a time in a harness (a "layer-off" test) to prove the others hold (TEN-030 to TEN-033).
3. `IgnoreQueryFilters`, `FromSqlRaw`/`ExecuteSqlRaw`, `HttpContext.Items` for tenant, static tenant state and unprefixed cache keys are banned by analyzers/architecture tests.
4. Background jobs carry tenant on the message and open an explicit scope.
5. Caches, search indexes, report projections, exports, packages, support bundles and logs are tenant-scoped.
6. The migration framework rejects cross-tenant and cross-organisation references at validation and again at commit (INV-48).

### 4.4 Object-level checks (BOLA/IDOR)

Every route taking an id loads the resource **through the scoped query** and then authorises against the loaded resource. Sequential or guessable identifiers are not relied on for security; GUIDs are used but never as a capability. Download and export links are authorised per request.

### 4.5 Workflow integrity

Transitions are the only way to change status. A transition is authorised, guarded and executed in one transaction with audit and outbox. Client-supplied target states are ignored; the client names a transition code. Approver identity is the authenticated user (never a callback field). Rules cannot override locks.

### 4.6 Lifecycle, classification and notification controls (implemented, lifecycle slice)

* **Classification (ADR-0019).** Five levels; Legal Privileged is a privileged level that needs `records.read_privileged` (Legal) or an explicit assignment flag in addition to the ceiling. Lowering needs `classification.lower` and a reason. Elevations (support, emergency) can never carry the privileged permission or any lifecycle, approval, unlock or publication permission.
* **Transitions.** Visible (404) → permission (403) → idempotency → from-state and expected state (409) → managed-role check → guards. Managed steps (Legal, signature, approval, Variation, renewal) cannot be run through the generic call. The status has no public setter; the client names a transition code and never a target state.
* **Locks.** Effective contracts are locked in the domain layer; the only writers are an approved Variation and an audited emergency unlock (permission, recent sign-in, reason of at least 20 characters, bounded time, only the opener may change fields, classification never offered, automatic re-lock). Every change under an unlock carries the unlock id in audit.
* **Segregation of duties.** The submitter of a register authorisation or disposition cannot decide it; the guard is in the definition and validation refuses a definition that removes it. First response wins; a second gets `already_decided`.
* **Approver reach.** An approver can read a contract only while a request for that approver's group is pending (or the contract is flagged for disposition for that group); access ends with the decision.
* **Approval matrix (ADR-0022).** The matrix is declarative data (strict JSON, closed vocabulary and operators; no SQL, script, expression or template). Publication is refused for overlaps, gaps and ties; resolution fails closed. Approvers are chosen from groups and then filtered by the access evaluator for the specific record (submitter, expired, out of scope, below the classification ceiling and privileged-without-grant are excluded). Delegation is own-authority only, bounded to 90 days, non-transitive, never circular and audited. The simulator needs `approvals.simulate`, stores nothing but an audit entry, and is organisation-scoped. Plans are pinned per request; concurrent final responses produce one decision (SQL Server tested). New threats T-090 to T-094 in `docs/THREAT_MODEL.md`.
* **Notifications (ADR-0021).** Recipients are filtered through the same `contracts.read` evaluation as the record; content is withheld by classification threshold (title from Restricted, everything from Legal Privileged); the inbox re-checks access when it is read; templates are plain text with allow-listed placeholders (no markup, no logic); subjects are stripped of control characters; email addresses are masked in logs; secrets are references. Demonstration and capture providers are refused in Production.
* **Administration.** `notifications.administer` is limited to the administrator's organisations; other organisations' deliveries return not found.
* **Dashboards** are built from the secured reads; there is no separate query path that could skip a limit.
* **External adapters.** The e-signature adapter and Graph email are scaffolds with no credentials and no egress; they fail closed.

### 4.7 Configurable metadata, search, views and exports (implemented, CFG-03/DOM-03/STU-01; ADR-0023)

* **No customer text is code.** Field definitions are strict JSON (unknown members are errors) written by the Studio from a typed model; patterns run on the non-backtracking engine with a time limit; calculations are a closed list; views and filters are typed (field, operator, values); nothing is evaluated as SQL, script, expression or template. Column and sort names reach the database only after being matched against a catalogue.
* **Hidden means absent.** A configured field the caller may not see (role, state, contract type, effective dates, classification, Legal Privileged) is absent from forms, details, the register catalogue, filters, sorts, free-text search, columns, exports, the reporting schema, Variation pages, approval snapshots and change history, and is answered as an unknown field. Values are held at the more sensitive of the field's and the contract's classification; audit events at the most sensitive level changed.
* **Access first.** Register search applies the caller's access in the database before any filter, sort or page; page size is capped (200); a configured-field filter is an uncorrelated subquery (no per-row lookup). Failures are plain-language messages; nothing about hidden data is revealed.
* **Exports** need `records.export`, include only readable and reportable fields, neutralise formula-looking text, are limited (normal and large with a recorded reason), audited without content, and shared the register's own query. Shared views are published through configuration (versioned, audited); a person's own views are private.
* **Studio** needs `config.draft`/`config.publish`/`config.rollback`/`config.history.read`; organisations the person has no configuration right in are not offered and are not-found if named. Antiforgery applies to every POST.
* **Migration** into configured fields is trusted but bounded: values are checked against the published definition, nothing is guessed, retired fields are refused, the source value and batch are kept, and the audit event carries the batch.
* **Known limits:** audit readers (`audit.read`) see change events at their classification level, including role-hidden fields' values; CSV/XLSX cannot be made safe for every spreadsheet product (apostrophe prefix is the mitigation); SQL Server behaviour of the new tables is not yet verified (see TESTING.md).

## 5. Data protection

| Topic | Control |
|---|---|
| In transit | TLS 1.2+ everywhere; HSTS; internal calls to adapters over TLS; database connection encrypted |
| At rest | Database and storage encryption provided by the platform (TDE/disk/Blob) and **attested** in readiness (R-DB-02, R-ST-03); application-level field encryption is not assumed. Per-tenant keys are a future option |
| Secrets | `ISecretProvider` (environment, Docker secrets, Key Vault, DPAPI file). Entry is write-only; stored values are never displayed. Rotation supported (references resolve at use; cache TTL ≤ 5 min) |
| Key ring | ASP.NET Core Data Protection keys persisted outside the container layer, protected (DPAPI/Key Vault/volume permissions); R-SEC-04 |
| Pack signing keys | Private signing keys never reside in the repository or the running app; verification uses public keys only (ADR-0014) |
| Backups | Encrypted at rest; restore tested; contain all tenants' data, so access is `ops.manage` only |
| Data minimisation | Webhook/notification payloads minimal by default; AI prompts exclude sensitive fields by default; audit address capture configurable |
| Classification | Enforced in queries, search, reports, exports (`RETENTION_AND_CLASSIFICATION_SPEC.md`) |

## 6. Setup and bootstrap security

Implements `SETUP_AND_READINESS_SPEC.md` section 1 (RISK-05). Built and tested; details in `docs/SETUP_AND_ONBOARDING.md` and `decisions/ADR-0016-setup-state-and-first-run-protection.md`.

1. The setup token (256 bits, 24 hours, single use) is issued by `Ecp.Host setup-token` and printed to that terminal once; only its hash is stored and it is never logged (ASM-20). It is exchanged for **one** session (60 minutes sliding, hash only stored, HttpOnly, SameSite=Strict, path `/Setup`). Five wrong tokens lock entry for five minutes; the token form is rate limited and antiforgery protected.
2. **Loopback only**: callers that are not on the server get `404` for every `/Setup` path unless `Ecp:Setup:AllowRemote` is set in host configuration; the wizard cannot widen it.
3. State is held in a Data-Protection-protected bootstrap file from the first start, mirrored to the database (RLS, installation scope) when it exists; an unreadable file stops setup rather than restarting it. Every step is audited; secrets are never stored: Production accepts only secret **references**, a literal is refused, and the sanitised report omits even the references.
4. Production start-up refuses default credentials, development identity, Demo data, the lightweight database, inline connection strings and ephemeral storage (R-ID-01, R-SEC-01, R-SEC-02, R-ST-01, R-DEMO-01); the wizard's `mode` step re-applies the guard and the readiness step blocks completion on any violation.
5. On completion every `/Setup` path is `404`, the token and session are cleared, and changes need a signed-in installation administrator (`setup.run`) with a **recent authentication** (10 minutes; `/Reauth` asks the provider to authenticate again with `prompt=login`). Only selected steps can be reopened, and each change is audited.
6. The bootstrap file and report hold state only; neither is returned by any endpoint other than the sanitised report, and neither belongs in support bundles.

## 7. Input handling, uploads and injection

| Area | Controls |
|---|---|
| General | Strong typing and validation at the boundary; DTOs separate from entities (no mass assignment); allowlisted sort/filter fields; parameterised data access only |
| XSS | Razor encoding by default; no raw HTML from data; administrator-provided text is plain or restricted safe markup; strict CSP (nonces, `default-src 'self'`, no `unsafe-inline`/`unsafe-eval`); `X-Content-Type-Options: nosniff`; `frame-ancestors 'none'`; Referrer-Policy; Permissions-Policy |
| Custom-field injection | Field codes are validated patterns; values are typed; labels/help are encoded on output; JSON is parsed with size/depth limits; patterns use a non-backtracking engine with time limits |
| SQL injection | EF parameterisation; raw SQL wrapper requires parameters and a scope |
| CSV/Excel formula injection | Output neutralisation in every export and error workbook (`RETENTION_AND_CLASSIFICATION_SPEC.md` section 6) |
| Uploads | Size and count limits; extension allowlist; content-signature check (declared type is advisory); hash computed; streaming to quarantine; malware scan hook; scan status gates download; filenames sanitised and never used as paths; images (logos) re-encoded |
| Archives | Reject traversal, absolute paths, symlinks, device names; entry, size and ratio limits; no recursive extraction; streaming extraction into quarantine |
| OOXML/XML | XML parsers use `DtdProcessing.Prohibit`, no external resolvers (XXE); macros rejected; embedded objects ignored |
| SSRF | One egress client; https only; resolved IP checked against private, loopback, link-local and metadata ranges; no cross-host redirects; size/time limits |
| Header/email injection | Messages built via provider APIs; control characters rejected in subjects/recipients |
| Log injection | Structured logging; values never concatenated into format strings; newline stripping |
| Open redirects | Local paths only |

### 7.1 Migration inputs (implemented 2026-10-16)

Source files are untrusted. CSV, XLSX and ZIP are read by in-repository code with explicit limits (rows, columns, cell size, entries, total size, compression ratio); XML is read with DTD processing prohibited; `.xlsm` is refused; archive entries with traversal, absolute, drive, UNC, reserved-device or symbolic-link paths, duplicate names, disallowed extensions or mismatching signatures are marked invalid per entry and never extracted to disk; every file goes to the malware scanner hook (mock outside Production, files held in Production because no real adapter exists); the attachment naming rule is the only pattern and runs on the non-backtracking engine with a timeout; exports neutralise formula text. Migration permissions are held by Procurement Administrators only, evaluated against the stored project's organisation; a legacy load needs a second person. See `docs/MIGRATION.md` section 9.

## 8. Audit (ADR-0011)

- Table `audit.Event`: INSERT and SELECT only for the runtime principal; a separate maintenance principal (not used by the app) performs partition archive.
- Per-tenant gap-free sequence and SHA-256 chain: `hash = SHA256(canonicalContent || previousHash)`. The writer takes a per-tenant lock (`sp_getapplock`) for the short append, so the chain cannot fork under concurrency. A nightly verification job and an operator command report breaks.
- Written in the same transaction as the change; failure to audit fails the operation.
- Old/new values only for non-sensitive fields; sensitive values replaced by a redaction marker and keyed hash.
- Evidence export: manifest with per-file hashes and chain anchors, HMAC-signed with a key from the secret provider.
- Access to audit is itself audited; `audit.read` ≠ `audit.export`.
- Privacy: erasure requests tombstone identity references in audit (a mapping table), never edit events.

## 9. AI security (RISK-16)

1. Disabled by default; enabling is a tenant decision that displays what data categories leave the platform (`DescribeAsync`).
2. The AI adapter principal has **no write permissions**. Output is stored as a *proposal* attached to the record; a human with the relevant permission must accept it, and accepted values pass the same validation, rules and lock checks as manual edits. AI never approves, signs, registers, bypasses access, changes locked records or alters controlled T&Cs (`CONFIRMED_DECISIONS.md`).
3. Prompts include only data the requesting user may read, below the classification ceiling, with sensitive fields excluded unless the tenant allows.
4. Treat document content as untrusted input: delimit it, instruct the model to ignore instructions inside it, restrict model tools to none, and never execute output. Output is length-limited, schema-validated and encoded on display.
5. Every AI request and decision is audited (who, what categories of data, provider, accept/reject).
6. Providers are called through the egress guard; no provider is called in Production without a recorded approval (R-AI-01).

## 10. Integrations and webhooks

Signature verification (HMAC, timestamp, replay protection via inbox; implemented provider-neutrally in S3 as `HmacCallbackVerifier` and `ISignatureEventInbox`, ADR-0027; no live provider or callback endpoint yet), per-tenant secrets by reference, SSRF-guarded egress, idempotent handlers, no trust in identity fields from callbacks, dead-letter visibility, minimal payloads (`API_AND_WEBHOOKS_SPEC.md`, `ESIGNATURE_PROVIDER_SPEC.md`). Reporting access via curated views with per-tenant principals and RLS (`REPORTING_QLIK_SPEC.md`).

## 11. Web and API hardening

HTTPS redirect + HSTS (preload not assumed); secure cookies; antiforgery; strict CSP; CORS disabled by default (explicit per-tenant origin allowlist if enabled); rate limiting (stricter on authentication, setup, import, export, webhook endpoints); request size limits per route; generic error responses with correlation id; no server banner; cache-control `no-store` for authenticated and download responses; clickjacking prevention; subresource integrity for any third-party script (none planned; TypeScript islands are bundled and self-hosted).

## 12. Configuration and package security

Closed vocabularies; schema validation; allowlisted asset types; safe image handling (SVG sanitised or rasterised); signature and checksum verification; import-as-draft only; permission and step-up for import/publish; package scanner for secrets; no executable content; `packages.import` cannot grant rights beyond the importer's own (`CONFIGURATION_PACKAGE_SPEC.md` section 3). Second-person publish in Production by default.

### 12.1 Solution packs (implemented in S7; ADR-0026, `docs/PACK_SECURITY.md`)

Packs are external, read-only data. A package is a closed ZIP of JSON with size, entry, ratio, depth and path limits, SHA-256 per document and a canonical, signed manifest (ECDSA P-256). Production refuses unsigned, unknown-key, revoked-key, expired-key, wrong-algorithm and tampered packs; Demo and Test accept an unsigned pack only if it is marked synthetic and `Ecp:Packs:AllowUnsignedSyntheticPacks` is on, with a warning. Verification and trust are repeated at every read. Uploads are quarantined, owned by the uploader, verified again at install and never assign or publish anything; assignment creates drafts only. Eight `packs.*` permissions are checked in the service on every call; install, retire, assign and trust changes also need a recent sign-in; trust-key administration is held by the Tenant Administrator only. Only public keys are accepted, shown or stored; no private key is committed (architecture test and CI check). Pack text is always encoded on output and the validation report CSV defuses formulas. Production key custody is an external operational responsibility.

## 13. Logging, monitoring and incident support

Structured logs with tenant, correlation and actor; redaction filter for known secret shapes and configured sensitive keys; security events (auth failure bursts, denials, elevation, unlock, package import, setup access attempts, audit-chain break) raise alerts via the monitoring sink and SIEM export. Support bundles are redacted automatically and tenant-scoped; a seeded-secret test (SEC-030) gates release. Incident ownership and contact are a production-readiness item (`docs/PRODUCTION_READINESS_CHECKLIST.md`).

## 14. Supply chain (ADR-0014; RISK-10, RISK-18)

NuGet and npm lock files committed; restore with locked mode in CI; vulnerable-package and deprecated-package checks fail the build above a configured severity; CycloneDX SBOM generated per build; licences of dependencies checked against an allowlist (permissive licences; no non-commercial or copyleft-incompatible components); base images pinned by digest and rebuilt on a schedule; GitHub Actions (if added) pinned by SHA with minimal token permissions; no secrets in CI logs.

## 15. Production restrictions (summary)

Production refuses: Demo identity, default credentials, ephemeral storage, exposed secrets, unsigned catalogue packs (without audited override), missing malware scanner (without recorded acceptance), unresolved critical readiness failures, setup routes after completion, demonstration data. See the check catalogue in `SETUP_AND_READINESS_SPEC.md` section 4.

## 16. Security assurance activities

| Activity | When |
|---|---|
| Threat model review | Each milestone (`THREAT_MODEL.md` change log) |
| Security regression suite | Every build (`TESTING.md` section 10) |
| Dependency/SBOM/licence scan | Every build |
| Skill-based reviews (`.claude/skills/security-review`, `tenant-isolation-review`, `portability-review`, `deployment-review`) | Each milestone exit |
| Penetration test, formal security review, accreditation | External governance, before any production use; **not** performed or implied by this project |

## 17. Terminology (S4; ADR-0028, `docs/TERMINOLOGY.md`)

* A label is presentation only. No evaluator, route, query or workflow reads a label; forms post keys and ids. Renaming a label changes no permission, scope, state or audit action.
* Label text is plain text: markup, script, template and expression syntax are refused on write, output is HTML-encoded, and the only expansion is the constrained `[[key|modifier]]` token language (no expressions, members or nesting). Tokens expand before argument formatting, so user data is never interpreted.
* Tenant and organisation isolation: releases, cache keys and drafts carry tenant and organisation; an administrator can name only organisations their grant covers (a plain not-found otherwise). Cross-tenant and cross-organisation tests cover read, draft, publish and cache.
* Administration is least-privilege (`terminology.*`), publish and rollback need recent authentication and follow second-person rules, every step is audited under stable action codes, and diagnostics never log label values.
* Pack terminology is inside the signed manifest: tampering fails verification; the content scan rejects unsafe text; installation creates a draft only.
* The resolver fails safe to the neutral wording; a cache failure falls back to persistence. No secret or live confidential data is part of terminology.

## Capabilities (S6)

A capability switch is operating-model configuration, not authorisation (ADR-0030 D1). It cannot grant, widen or remove a permission, and the refusal it produces (`capability_disabled`) is made after the permission, tenant, organisation, classification and lock checks, never instead of them. The capabilities document is plain data validated like other configuration (no markup, template or expression syntax; bounded size), published only by the Tenant and Organisation Administrators, audited, versioned and rolled back through the ordinary lifecycle. The resolved set is cached by tenant, organisation and version, so one tenant's switches are never served to another. On a read failure the platform fails open (everything on), which can only add steps, never bypass a control. See `docs/CAPABILITIES.md`.

## A third shipped pack (S9)

The Harborline pack adds no code path, permission, route or role. It is data under the same controls as the other packs: safe-ZIP reading, hash and signature verification, per-document validation, installation as drafts, publication by authorised administrators with the usual second-person and recent-authentication rules. Its notification policy withholds titles from `Confidential` and everything from `Restricted` content, stated explicitly (a four-level scale has no privileged level). Tests show tenant and organisation isolation of terminology, capabilities, workflows, matrices, fields, views and notifications between a JBS, a Blank and a Harborline organisation in one tenant, and between two tenants (`SecondBusinessScenarios.Isolation`, SQLite; SQL Server in CI). The pack carries only synthetic data: no real company, licence number, tax identifier or document, and the synthetic currency rate is labelled. A new architecture guard (`SecondBusinessGuardTests`) fails if the pack's vocabulary reaches platform source, a project file or a compiled assembly. Residual: tenant-level classification and branding apply to the whole tenant, so one business's choice affects the others in a shared tenant (recorded as an open finding); the pack format cannot carry executable content by design.

## 18. Regional settings and tax identifiers (S10; `docs/TAX_IDENTIFIERS.md`)

* **Classification gate**: a counterparty identifier is shown only to a reader whose grant reaches its classification. A reader below that level is never told one exists: it is absent from lists, ending it answers "not found", a write that would collide with a hidden value is refused as "not found" before any duplicate check, and the register's projected value, search text and exports are withheld by the same gate. Found and fixed during S10: the write-time read check ignored the classification dimension; it now includes it.
* **No code from configuration**: a regional profile holds codes from closed catalogues; identifier rules are a closed list (no customer regular expression, hence no ReDoS or review surface); labels are plain text with markup and template syntax refused; nothing is executed.
* **Tenant and organisation isolation**: settings are cached per tenant, organisation and published version; a publish or rollback invalidates; a cold cache reads persistence. One organisation's profile cannot be returned for another (tested).
* **Honest claims**: the platform validates structure only. "Format Valid" is never described as registered, active or legally valid; there is no registry lookup and the externally verified statuses are reserved and unused.
* **Audit**: identifier add and end, profile drafts and releases, and the organisation's regional change are audited; audit text and migration provenance are not rewritten.

## 19. Backup, restore and operations (OPS-01 / OPS-02 foundation; ADR-0034; `docs/BACKUP_AND_RESTORE.md`)

* **Who**: six permissions (`operations.read`, `.backup`, `.restore`, `.validate`, `.maintenance`, `.evidence.export`) held only by the System Administrator. Every service method re-checks the permission **and** that the caller is in the installation's own tenant; a tenant, organisation or business administrator is refused whether or not the page is hidden. Creating a backup, restoring, entering or leaving maintenance mode and taking over a lease need a sign-in within the last 10 minutes; restore also needs a reason and a typed phrase naming the backup. Restoring over the active database is not reachable from the web.
* **What leaves the platform**: backups hold references to secrets, never values, and no private key. Manifests, evidence reports and support bundles are built from fields that cannot hold secrets or record or document content, have free text masked, and are scanned and refused if a secret remains. Exception messages from the database server pass through the same redaction.
* **Input**: a backup is untrusted input at restore time. Manifest strictness, path rules, link refusal and per-file hashing are in `docs/BACKUP_AND_RESTORE.md` sections 5 and 7. Nothing is extracted from an archive.
* **Isolation**: operational tables belong to the installation tenant under the same row-level security as every other table; a customer tenant cannot read or write them (tested on SQL Server). A restore preserves tenant and organisation ids and hashes and validates row-level security and `SESSION_CONTEXT` behaviour on the copy.
* **Integrity of history**: audit chains are verified at backup and at restore and are never regenerated; the restore appends its own events.
* **The privileged account**: backup and restore use a separate SQL Server account supplied as a secret reference; the application's day-to-day account does not need those rights. Falling back to the application account is reported by readiness.
* **Residual and operator-owned**: encryption and access control of the backup storage, custody of the SQL Server certificate, secrets and the data-protection key ring, off-site copies, retention and deletion, and rehearsal are the organisation's (`docs/OPERATIONS_RUNBOOK.md` section 9). The platform records the operator's statement and cannot verify it.

