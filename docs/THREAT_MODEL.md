# Threat Model

Status: **initial model, pre-implementation.** Method: asset and data-flow decomposition, STRIDE per component, abuse cases for the threats named in Master ("IDOR/BOLA, tenant breakout, workflow bypass, approval spoofing, unsafe uploads, custom-field injection, malicious package imports and privilege escalation"), and mapping to controls (`docs/SECURITY.md`) and tests (`docs/TESTING.md`). It is reviewed at the end of each milestone; the change log is at the bottom. It is not a penetration test and does not imply that any control is already implemented.

Severity (inherent, before controls): **Critical / High / Medium / Low**. "Residual" is what remains with the planned controls.

## 1. System context and data flows

```mermaid
flowchart LR
  U[Users: browser] -->|HTTPS| P[Reverse proxy / host TLS]
  S[Service principals: Qlik, integrations] -->|HTTPS / SQL views| P
  P --> W[Ecp.Host: UI, API, hosted services]
  W -->|TDS + TLS| DB[(SQL Server / Azure SQL)]
  W -->|streams| ST[(Document store)]
  W -->|OIDC| IDP[Identity provider]
  W -->|egress guard| EXT[Adapters: DocuSign, email, ERP, AI, SIEM, scanner]
  EXT -->|signed callbacks| P
  A[Administrators: Studio, setup, packs] --> W
  PKG[.ecpkg and migration archives] -->|upload| W
  OPS[Operators: host, backup, support] --> DB
  OPS --> ST
```

Trust boundaries: TB1 Internet↔host; TB2 host↔database; TB3 host↔document store; TB4 host↔identity provider; TB5 host↔external adapters (outbound and inbound); TB6 tenant↔tenant; TB7 unauthenticated↔authenticated; TB8 ordinary↔privileged roles; TB9 configuration authoring↔runtime; TB10 uploaded content↔platform; TB11 operator↔application (backup, support).

## 2. Threat register

### 2.1 Access control and tenancy

| ID | Threat | Sev | Boundary | Key mitigations | Tests | Residual |
|---|---|---|---|---|---|---|
| T-001 | **IDOR/BOLA**: user changes an id to read or modify another user's contract, document, comment, export or job | Critical | TB6, TB8 | Scoped queries then authorise on the loaded object; 404 vs 403 policy; per-request download authorisation; GUIDs not capabilities | AUT-014, TEN-001, TEN-002, TEN-003 | Low |
| T-002 | **Tenant breakout** via unfiltered query, raw SQL, ignore-filter, background job, cache, search, export or report | Critical | TB6 | Layered isolation (filter + RLS + composite FKs + storage prefix + cache keys + job scope); analyzers; layer-off tests | TEN-001 to TEN-020, TEN-030 to TEN-033, ARC-003 to ARC-006 | Low |
| T-003 | Tenant spoofing: header, claim or host manipulation selects another tenant | Critical | TB6, TB7 | Tenant derived from verified principal or validated host map; never client supplied | TEN-019 | Low |
| T-004 | Cross-organisation read inside one tenant by a user of another organisation | High | TB8 | Organisation layer in evaluator; org filters; tests with `viewer.a2` | TEN-018, AUT-003 | Low |
| T-005 | **Privilege escalation** via delegated administration (grant self or others higher rights; role template import) | Critical | TB8 | Delegation bounded by grantor rights; `roles.delegate` scope; packages cannot assign people; audit; step-up | AUT-009, AUT-011, PKG-008 | Low |
| T-006 | Stale rights: ended category assignment still edits | High | TB8 | Evaluate active assignment at action time; 60 s role cache bound | AUT-005 | Low |
| T-007 | Classification bypass via search, counts, reports, exports, notifications or AI prompts | High | TB8 | Clearance in `IAccessScope`; omit not mask in lists/exports; notification names only readable records | AUT-006, TEN-007, IMP-002, NTF-006 | Medium |
| T-008 | Support/elevated access abused or not visible | High | TB11 | Reason, expiry, tenant-visible banner, full audit, read-only default | AUT-012, AUD-006 | Medium (operator trust) |
| T-009 | Existence oracle through error messages, timing or counts | Medium | TB6 | Uniform 404/generic errors; no cross-tenant counts; constant-time comparisons for tokens | TEN-017, SEC-021 | Low |
| T-010 | Last administrator removed or locked out; or hidden super-admin | Medium | TB8 | Last-admin protection; all admin grants audited | AUT-011 | Low |

### 2.2 Workflow, approvals and records

| ID | Threat | Sev | Boundary | Key mitigations | Tests | Residual |
|---|---|---|---|---|---|---|
| T-020 | **Workflow bypass**: set status directly via API/mass assignment, or call a later transition skipping guards | Critical | TB8 | `status` has no setter; transitions by code; guards in one transaction; DTOs exclude status | WFL-001, WFL-002, WFL-006 | Low |
| T-021 | **Approval spoofing**: forged callback or edited body attributes approval to someone else; submitter approves own submission | Critical | TB5, TB8 | Approver identity from session; callback HMAC and inbox; SoD default on; matrix pinned | WFL-004, APR-007, ADP-004 | Low |
| T-022 | Locked record modified via a side path (import, API, rule action, AI accept, Variation misuse, unlock reuse) | Critical | TB8 | Domain-layer lock enforcement; migration uses Contracts contract; unlock time-boxed; Variation limited to `Variation only` fields | WFL-004, WFL-009, WFL-011, MIG-015 | Low |
| T-023 | Approval matrix gap/overlap routes a high-value contract to a low-level approver, or silently skips approval | High | TB9 | Publish-time gap/overlap/tie validation; fail closed on no match; simulation | APR-002, APR-003, APR-004 | Low |
| T-024 | In-flight records change behaviour when configuration changes | High | TB9 | Pinned workflow and matrix versions; safe-change rules | WFL-007, NCD-007 | Low |
| T-025 | Concurrent double approval or transition | Medium | TB8 | Row version, idempotency key, unique approval constraint | CNC-001, CNC-002 | Low |
| T-026 | Registered Date or legacy flags forged via import or edit | High | TB8 | Registered Date set only by Approve or authorised legacy load with second-person confirmation | WFL-005, MIG-015 | Low |
| T-027 | Reminder spam, loss or duplication | Low | TB5 | Dedupe keys; idempotent send; dead-letter | NTF-001 to NTF-005, WFL-010 | Low |

### 2.3 Uploads, imports and files

| ID | Threat | Sev | Boundary | Key mitigations | Tests | Residual |
|---|---|---|---|---|---|---|
| T-030 | **Unsafe upload**: malware, polyglot, wrong type, script in document, oversize | High | TB10 | Allowlist, signature sniffing, size limits, quarantine, scanner hook, download gating, `nosniff`, `Content-Disposition` | SEC-011 to SEC-016, ADP-006 | Medium (depends on scanner) |
| T-031 | **Zip slip / path traversal** in archive or filename | High | TB10 | Normalise and reject traversal, absolute, drive, UNC, reserved names, symlinks; keys are hashes not names | MIG-012, SEC-012 | Low |
| T-032 | **Decompression bomb / resource exhaustion** | High | TB10 | Entry, size, ratio, depth limits; streaming; job quotas; timeouts | MIG-013, SEC-013 | Low |
| T-033 | XXE or billion-laughs in OOXML/XML; macro or embedded object execution | High | TB10 | DTD prohibited; no resolver; macros rejected; no Office automation server-side | SEC-014 | Low |
| T-034 | **CSV/Excel formula injection** through exports and error workbooks | Medium | TB10 | Cell prefixing | MIG-021, IMP-003 | Low |
| T-035 | ReDoS through configured naming-rule or field-pattern regexes | Medium | TB9, TB10 | Non-backtracking engine, length cap, timeout | MIG-019, NCD-004 | Low |
| T-036 | Migration cross-tenant reference or file linking to another tenant's contract | Critical | TB6 | Validation and commit-time checks; tenant in all keys | MIG-008, TEN-010 | Low |
| T-037 | Rollback damages unrelated data | High | TB8 | Selection by batch provenance only; preview; reference-count rule | MIG-006, MIG-016 | Low |
| T-038 | Duplicate content hash leaks cross-tenant existence | Medium | TB6 | Dedupe within tenant only | TEN-021 | Low |
| T-039 | Source files containing personal or commercial data retained in staging indefinitely | Medium | TB10 | Staging retention policy and audited discard; restricted access | MIG-005, MIG-022 | Medium |

### 2.4 Configuration and packages

| ID | Threat | Sev | Boundary | Key mitigations | Tests | Residual |
|---|---|---|---|---|---|---|
| T-040 | **Custom-field / configuration injection**: stored XSS via labels, help, terminology, templates; JSON injection; mass assignment | High | TB9 | Output encoding; plain/restricted markup; code patterns; closed schemas | SEC-005, NCD-002, NCD-005 | Low |
| T-041 | **Malicious package import**: traversal, bomb, macros, unsigned, tampered, role grants, secrets, SSRF URLs | Critical | TB10 | Package safety rules; hash and signature verification; import-as-draft; no assignments; step-up; second-person publish | PKG-001 to PKG-008, PRT-008 | Low |
| T-042 | Arbitrary code via expressions, templates or calculated fields | Critical | TB9 | Closed vocabularies; interpreters with limits; banned APIs (Roslyn, Jint, scripting, `Assembly.Load`, `Process`) by architecture test | NCD-003, ARC-007 | Low |
| T-043 | Malicious or buggy release breaks the platform (non-terminating rules, huge definitions) | Medium | TB9 | Limits; pass cap; validation and simulation; instant rollback; last-good retention | NCD-006, NCD-008 | Low |
| T-044 | Branding SVG/CSS used for XSS or UI redress | Medium | TB9 | SVG sanitise or rasterise; no custom CSS/HTML | SEC-017 | Low |
| T-045 | Pack signing key theft or rogue publisher key registration | High | TB9 | Keys offline/HSM; trust registration is a privileged audited act; revocation list | PKG-005 | Medium (operational) |

### 2.5 Setup, identity and secrets

| ID | Threat | Sev | Boundary | Key mitigations | Tests | Residual |
|---|---|---|---|---|---|---|
| T-050 | **Setup takeover** on a fresh internet-exposed install | Critical | TB1 | Setup token, loopback default, 404 without token, one session, route removal | SEC-008, SEC-009 | Low |
| T-051 | **Demo identity or defaults in Production** | Critical | TB7 | Start-up guard and readiness check; Production refuses | SEC-010 | Low |
| T-052 | OIDC attacks: token substitution, nonce/state replay, account linking by email, open redirect, group overage ignored | High | TB4 | PKCE, state/nonce, `(iss, sub)` linking, local return URLs, fail-closed group resolution | SEC-004, AUT-016 | Low |
| T-053 | Session theft or fixation; CSRF | High | TB7 | Secure cookies, rotation, antiforgery, SameSite, short lifetimes | SEC-002, SEC-003 | Low |
| T-054 | Secret exposure in config, logs, errors, exports, support bundles, packages | High | TB11 | Secret references; redaction; package scanner; write-only entry | SEC-030, PRT-007 | Low |
| T-055 | Key ring lost or exposed (cookie forgery, unreadable secrets after restart) | High | TB11 | Persisted protected key ring; readiness R-SEC-04 | DEP-002 | Medium (operator) |
| T-056 | Brute force or credential stuffing | Medium | TB7 | IdP controls; rate limits on sign-in callbacks and Demo login | SEC-020 | Low |

### 2.6 Integrations and egress

| ID | Threat | Sev | Boundary | Key mitigations | Tests | Residual |
|---|---|---|---|---|---|---|
| T-060 | **SSRF** via URL fields, webhooks, website retrieval, catalogue URLs, AI/provider endpoints | High | TB5 | Single egress client: https only, IP range checks after resolution, no redirects across hosts, allowlist | SEC-007, ADP-007 | Low |
| T-061 | Forged or replayed inbound webhook (e-signature) | High | TB5 | HMAC, timestamp window, inbox dedupe, tenant-bound secrets | ADP-004, ADP-005 | Low |
| T-062 | Outbound webhook leaks sensitive data or is directed at an internal target | High | TB5 | Minimal payloads; egress guard; per-tenant allowlist | SEC-007, TEN-013 | Low |
| T-063 | **External reporting bypasses security**: Qlik principal reads base tables or another tenant | Critical | TB2, TB6 | Curated views, RLS, per-tenant principals, no base-table grants | SEC-040 to SEC-045 | Medium (network config) |
| T-064 | **AI prompt injection / data egress / unreviewed change** | High | TB5, TB10 | Disabled default; no-write principal; proposals; delimiting; schema-validated output; sensitive-field exclusion; audit | SEC-050 to SEC-053 | Medium |
| T-065 | Adapter failure causes data loss or blocks core operation | Medium | TB5 | Outbox, retries, dead-letter, manual fallbacks | ADP-001, ADP-002 | Low |

### 2.7 Audit, data and operations

| ID | Threat | Sev | Boundary | Key mitigations | Tests | Residual |
|---|---|---|---|---|---|---|
| T-070 | **Audit tampering** or deletion; silent gaps | High | TB2, TB11 | Append-only grants, hash chain, sequence, same-transaction write, verification job, exported anchors | AUD-001 to AUD-005 | Medium (privileged DBA can still restore/alter; detected by chain) |
| T-071 | Sensitive values leak into audit values | Medium | TB2 | Field sensitivity redaction | AUD-004 | Low |
| T-072 | Backup contains all tenants and leaks | High | TB11 | Encryption, access limited to `ops.manage`, tested restore, retention | BRS-002 | Medium (operator) |
| T-073 | Export/download link guessed or shared | Medium | TB7 | User-bound, short-lived, authorised tokens; expiry | IMP-004 | Low |
| T-074 | Denial of service: expensive search/report/export/import, large uploads, many jobs | Medium | TB1 | Rate limits, quotas, async jobs with caps, query limits, pagination | PER-004, SEC-020 | Medium |
| T-075 | Time manipulation or skew breaks reminders, token validity, hash chain order | Low | TB11 | UTC clock abstraction; skew check R-OPS-04; sequence not time ordered | AUD-003 | Low |
| T-076 | Dependency compromise or licence violation | High | Supply chain | Locked restores, vulnerability scan, SBOM, licence allowlist, pinned images | SEC-060 to SEC-062 | Medium |
| T-077 | Data kept longer or deleted earlier than authorised (retention, legal hold) | High | TB8 | Archive-before-delete; hold checks; second approval; default Indefinite | RET-001 to RET-006 | Low |
| T-078 | Wrong-tenant maintenance (e.g., job or migration running without scope) | High | TB6 | Scope required by data layer; fail closed | TEN-005, ARC-004 | Low |

## 3. Abuse cases (walk-throughs used as test scenarios)

1. **A category owner whose assignment ended opens an old URL** (T-001, T-006): record readable (read all), edit denied, audit entry shows the assignment reason.
2. **A user in organisation A2 guesses a contract id in A1** (T-004): 404.
3. **An administrator imports a package granting `Tenant Administrator` to a user id** (T-005, T-041): rejected at validation; no assignments allowed in packages.
4. **A forged DocuSign callback claims "completed"** (T-021, T-061): rejected, audited; contract remains Out for Signature; even a genuine completion only supplies the executed copy.
5. **A migration ZIP contains `../../etc/x`, a 1 GB zero file, and a macro-enabled workbook** (T-031, T-032, T-033): each rejected with its message code; batch continues for other files; counts reconcile.
6. **A Business Configuration Administrator publishes a rule set that never stabilises** (T-043): validation fails in simulation; if forced via package, runtime pass cap blocks the transition with explanation.
7. **A fresh Docker instance is exposed to the Internet before setup** (T-050): `/setup` returns 404 without the console token; remote requests are refused by default.
8. **Qlik principal for tenant A attempts `SELECT * FROM contracts.Contract`** (T-063): permission denied; the views return only tenant A rows.
9. **A document claims to be a PDF but contains script/HTML** (T-030): signature mismatch rejects it; if allowed type but malicious, scanner verdict gates download.
10. **A support engineer opens elevated access** (T-008): tenant administrators see the banner; every action carries the elevation id; access expires.

## 4. Assumptions and limitations

- The host OS, hypervisor, network and cloud control plane are trusted to the extent the operator secures them (R-DEP checks verify some settings but cannot prove the platform's security).
- A database administrator with unrestricted access can alter data; audit hash chaining detects but does not prevent this. Separation of duties for DBA access is an operational control.
- Malware detection quality depends on the configured scanner.
- Identity-provider compromise compromises the platform for the users it asserts; step-up and short sessions limit blast radius.
- Penetration testing, formal security review and accreditation are external governance and not performed by this project.

## 5. Review process

At every milestone exit: re-run the abuse cases as automated tests; add threats for new features (new adapters, new input formats); record the outcome below; unresolved High/Critical items block the milestone.

## 6. Change log

| Date | Change | By |
|---|---|---|
| 2026-10-15 | Initial model created during pre-implementation repository review | Claude Code session |
| 2026-10-16 | M1 implementation evidence: tenant filters, RLS, composite FKs, append-only audit and Production guard verified on SQL Server 2022 Developer; runtime role needed explicit SELECT on the RLS predicate functions (finding fixed). Setup, upload and OIDC threats remain unmitigated until their milestones | Claude Code session |
| 2026-10-16 | Setup and onboarding implemented and tested: T-050 (token, single session, loopback `404`, closure after completion, restart and CLI tests), T-051 (Production guard in the `mode` step and readiness `R-DEMO-01`), T-054 (secret references only; sanitised-report test). New residual risks: the setup token is a bearer credential on an operator terminal (ASM-20); with `Ecp:Setup:AllowRemote` the token and network controls are the only barrier; onboarding publication can grant viewer and category-owner access, so it is restricted to roles that hold organisation-level configuration rights and cannot map administrator or approver roles (ADR-0017); the live OpenID Connect step-up (`prompt=login`) is verified only against a mock provider | Claude Code session |
| 2026-10-16 | Migration slice implemented and tested (T-030 to T-039 mitigations exercised: unsafe archive entries, bomb limits, XXE refusal, `.xlsm`, formula neutralisation, naming-rule engine, cross-tenant batch access, provenance-driven rollback). Residuals: no real malware scanner adapter (mock outside Production; files held in Production), synchronous commit within the request (resumable), staging retention beyond acceptance is a manual purge (`migration.staging.purged`). | Claude Code session |
