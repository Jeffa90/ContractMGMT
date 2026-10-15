---
name: security-review
description: Project-specific security review of authentication, authorisation (role, organisational, category and classification scopes), secrets, uploads, packages, migration staging, audit and Production restrictions, against the platform threat model and security design. Use after changes to identity, access, uploads, integrations, setup, configuration import or audit. Takes precedence over the generic security-review for this repository.
---

# Security Review (project-specific)

## Read first
1. `docs/SECURITY.md`, `docs/THREAT_MODEL.md` (T-nn register and abuse cases)
2. `requirements/05-security/ACCESS_CONTROL_SPEC.md`, `AUDIT_SPEC.md`, `RETENTION_AND_CLASSIFICATION_SPEC.md`
3. `requirements/02-reference/permission-catalogue.csv`, `role-permission-matrix.csv`
4. `requirements/07-integrations/*.md`, `requirements/12-operations/SETUP_AND_READINESS_SPEC.md`
5. `docs/TESTING.md` sections 5, 6, 10

## Procedure
1. Enumerate entry points (pages, endpoints, webhooks, jobs, setup routes, file intake). For each, find the authorisation requirement and the tests. Unannotated entry points are findings (ARC-008, AUT-015).
2. Walk the threat-model abuse cases as tests; mark each Pass / Fail / Not verified.
3. Run the checklist.

## Checklist
- **Scope model:** access is the intersection of role permission, organisation scope, business unit and site scope, category scope and classification ceiling; no role grants access outside its assigned scopes; Viewer is never a global read role; Category Owner reads all within organisation and classification scope but edits assigned categories only (AUT-001 to AUT-009).
- **IDOR/BOLA:** every id route loads through the scoped query then authorises (AUT-014); foreign ids return 404.
- **Tenant isolation:** EF filter, Row-Level Security, composite foreign keys, storage prefix, tenant-keyed caches, scoped jobs; layer-off tests (TEN-030 to TEN-033). RLS and SQL behaviour claims need real SQL Server; otherwise NOT VERIFIED.
- **Authentication:** OIDC with PKCE, `(issuer, subject)` linking, local return URLs only, step-up for sensitive actions; Demo identity inert in Production (SEC-010).
- **Setup:** one-time token, loopback default, 404 without token, routes removed after completion (SEC-008/009).
- **Secrets:** only references in configuration; never in logs, exports, packages, support bundles or error messages; write-only entry; System Administrator does not automatically reveal secrets (SEC-030, PRT-007).
- **Uploads and archives:** allowlist, signature sniffing, size and ratio limits, no traversal, XXE disabled, macros rejected, scan status gates download (SEC-011 to SEC-016, MIG-012/013).
- **Packages:** hash and signature verification, import as draft only, no assignments or privilege grants, step-up (PKG-001 to PKG-011).
- **Workflow and approvals:** approver identity from session; callbacks cannot attribute approvals; segregation of duties; locks enforced in domain (T-020 to T-026).
- **Egress and SSRF:** single guarded client (SEC-007).
- **Audit:** append-only, hash chain, same-transaction, redaction, evidence export signed (AUD-001 to AUD-007).
- **Injection:** XSS, SQLi, formula injection, header injection, log injection corpora (SEC-005, SEC-006, MIG-021, IMP-003).
- **Web hardening:** headers, CSP, antiforgery, cookies, rate limits, generic errors (SEC-001 to SEC-003, SEC-019, SEC-020).
- **AI:** disabled by default; no write permission; proposals only; sensitive fields excluded (SEC-050 to SEC-053).
- **Supply chain:** locked restore, vulnerable-package gate, SBOM, licence allowlist (SEC-060 to SEC-062).
- **Production restrictions:** Production refuses Demo identity, default credentials, ephemeral storage, exposed secrets, unsigned catalogue packs and demonstration data (R-* checks).

## Severity
Critical: tenant breakout, authentication bypass, secret exposure, privilege escalation. High: missing authorisation on an endpoint, audit gaps, unsafe upload path. Medium: missing header or limit. Low: hardening gaps.

## Output
Findings table with threat IDs, fixes made with regression tests, residual risks, and items Not verified. Never claim legal compliance or declare production approval.
