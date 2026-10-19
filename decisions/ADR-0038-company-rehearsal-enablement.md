# ADR-0038: Company rehearsal enablement: Production-equivalent governance in Test, truthful email and proxy readiness, scanner boundary, masked-data declaration, standalone pack tool and honest evidence

Status: Implemented by the Company Rehearsal Enablement slice (2026-10-19). Proposed until the owner reviews it; production approval remains external governance and is not declared by this slice.
Builds on ADR-0026 (external packs), ADR-0034 and ADR-0035 (operations and upgrade), ADR-0036 (operations dashboard), ADR-0037 (implementation readiness).

## Context

`docs/IMPLEMENTATION_REHEARSAL.md` took a synthetic customer through thirteen steps, drove a Production-mode host over HTTP and found seven gaps (G-31 to G-37) that stand between the platform and a supervised company rehearsal in Demo/Test: second-person governance could not be rehearsed in Test; raw terminology tokens appeared on setup pages; a proxy that does not forward the scheme produced a bare 500; the pack tool needed a source checkout; nothing helped with masked data; several guides had drifted; and the rehearsal recorded a Blocked pre-flight as a pass. The earlier gap G-12 (email setup does not equal live delivery) and the scanner boundary of G-25 were the other two obstacles.

## Decision

1. **Demo/Test is the supported mode for the first supervised company rehearsal.** Production is not declared ready. Production still needs a connected, healthy malware scanner and every Production-mode control it already has.
2. **Governance mode is explicit.** `Ecp:Governance:SecondPerson` is `Default` or `Enforced`. Production always enforces the second-person rule and the setting cannot lower it. In Test, `Enforced` switches on exactly the Production rule (author, last editor and submitter cannot decide; hash-bound submission; rollback follows it); `Default` keeps the simplified behaviour. Demo ignores the setting and keeps simplified governance. The effective mode (`Production`, `TestEnforced`, `TestSimplified`, `DemoSimplified`) is shown on the setup report, Health and the operations dashboard, and is recorded on configuration audit events and releases.
3. **Evidence semantics are fixed in one place.** Ready is Passed. Ready with warnings is Passed only with recorded acceptance of every warning. Blocked and Failed are Failed. Not Verified stays Not Verified. Reports keep the original operational outcome next to the evidence verdict and nothing converts one to another.
4. **Terminology tokens never reach a reader.** Titles and service messages are expanded at their origin; a response guard expands or replaces any remaining token with a controlled diagnostic marker, logs a stable code and, outside Production, sets a header that the render sweep asserts is absent.
5. **Proxy and HTTPS problems get a plain diagnostic.** Secure cookies, antiforgery and HSTS are unchanged. A Production request that arrives as plain HTTP is answered with a short safe page and a stable code (`ECP-NET-001` the request is not HTTPS and carries no forwarded scheme; `ECP-NET-002` forwarded headers arrived but were not trusted), with a correlation id and the operator action, never header values or stack traces. Health paths are exempt. Readiness check `R-NET-01` and a setup check report the forwarded-header posture and any diagnostics seen.
6. **Email configuration is one thing.** Setup and Administration write the same installation-level configuration (non-secret settings and secret references only), and live delivery reads it, falling back to environment settings only when none is stored. Readiness is proved by an actual provider interaction: a connectivity probe and a test message built from fixed synthetic text. A configured but unreachable provider is never Healthy. A result is tied to a fingerprint of the settings and expires by an explicit policy; Production setup cannot complete email without a passing test. SMTP and Graph stay Not Verified until exercised against real services; Graph remains a scaffold.
7. **The scanner boundary is a contract, not a scanner.** Configuration carries provider key, endpoint or service reference, secret reference, timeout, file-size limit, retry policy and health-check behaviour. A provider-neutral readiness test exercises the contract. The labelled mock provider is permitted in Demo and Test only, by explicit configuration, with a prominent warning; for a masked-data rehearsal its use must also be accepted on the record (who, why, scope, expiry). Quarantine, infection, provider unavailability and exhausted retries raise operational notifications to recipients defined by existing configuration. No real scanner is claimed.
8. **Masked data is declared, not detected.** The responsible organisation records a declaration (owner, source, method, fields masked, attachments, personal and confidential data, approval reference, approver, time, expiry, permitted environment and users). A batch marked as Masked Company Data cannot commit in Test mode without a current, approved declaration that permits the environment. The platform never states that the data is safe. Production data follows separate governance.
9. **Pack signing stays technical and external.** The pack tool is shipped as a standalone artefact (self-contained executable and a portable build) that needs no source checkout, SDK or IDE. It builds deterministic packages from a source directory, validates, inspects, signs from an external key reference, verifies and writes a sanitised report, with documented exit codes. The application never holds, receives or stores a private key; there is no browser signing.
10. **A sanitised company evidence pack** assembles environment, governance, identity, email, scanner, declaration, publication, migration, backup, pre-flight and readiness facts with stable codes, hashes and correlation ids, without secrets, contract content or document content, and is formula-safe.
11. **Guides are reconciled against the application** and a consistency check fails the build when a guide names a page route or a permission the application does not have.

## Alternatives considered

* **Making Test always enforce the second-person rule.** Rejected: it would change every existing Test deployment and the simplified path some trials rely on; an explicit setting is safer.
* **Treating a configured email provider as ready.** Rejected: it is exactly the misleading pass the rehearsal found.
* **A browser-based signing experience.** Rejected: it would put private keys inside the web application's trust boundary.
* **Automatic de-identification or masked-data detection.** Rejected: the platform cannot know; a declaration with an approver is honest and auditable.
* **A mock scanner that is on by default in Test.** Rejected: a trial must choose it and, for masked company data, accept it on the record.

## Consequences

* New tables only (email configuration and results, scanner acceptance, masked-data declaration and batch link); the upgrade manifest is regenerated.
* Existing Test harnesses state the mock scanner explicitly.
* Real SMTP, Graph, Entra and a real scanner remain Not Verified; the slice supplies the boundary and the evidence, not the services.
* Production stays blocked until a real scanner is connected and the Production-mode rehearsal in a non-production environment has passed.
