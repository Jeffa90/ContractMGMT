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

Implements `SETUP_AND_READINESS_SPEC.md` section 1 (RISK-05):

1. First start generates a 256-bit setup token, prints it once to the host console/log, stores its hash in the bootstrap store; `/setup` without it returns 404.
2. Loopback-only by default; a host-level setting can allow a network range; the wizard cannot widen it.
3. One setup session at a time; every stage action is audited under the installation scope.
4. In Production the wizard accepts no secret values; it validates references. Production start-up refuses default credentials, development identity, ephemeral storage and exposed secrets (R-ID-01, R-SEC-01, R-SEC-02, R-ST-01).
5. On completion the setup routes are unregistered, the token is invalidated, and further changes require step-up re-authentication by a holder of `setup.run`.
6. The bootstrap store holds references and state only; its file permissions are restricted to the service account; its contents are never returned by any endpoint or included in support bundles.

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

Signature verification (HMAC, timestamp, replay protection via inbox), per-tenant secrets by reference, SSRF-guarded egress, idempotent handlers, no trust in identity fields from callbacks, dead-letter visibility, minimal payloads (`API_AND_WEBHOOKS_SPEC.md`, `ESIGNATURE_DOCUSIGN_SPEC.md`). Reporting access via curated views with per-tenant principals and RLS (`REPORTING_QLIK_SPEC.md`).

## 11. Web and API hardening

HTTPS redirect + HSTS (preload not assumed); secure cookies; antiforgery; strict CSP; CORS disabled by default (explicit per-tenant origin allowlist if enabled); rate limiting (stricter on authentication, setup, import, export, webhook endpoints); request size limits per route; generic error responses with correlation id; no server banner; cache-control `no-store` for authenticated and download responses; clickjacking prevention; subresource integrity for any third-party script (none planned; TypeScript islands are bundled and self-hosted).

## 12. Configuration and package security

Closed vocabularies; schema validation; allowlisted asset types; safe image handling (SVG sanitised or rasterised); signature and checksum verification; import-as-draft only; permission and step-up for import/publish; package scanner for secrets; no executable content; `packages.import` cannot grant rights beyond the importer's own (`CONFIGURATION_PACKAGE_SPEC.md` section 3). Second-person publish in Production by default.

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
