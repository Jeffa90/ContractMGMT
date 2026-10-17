# ADR-0032: A second business is configuration only; generic defects are documented before they are fixed

Status: Recorded by slice S9 (2026-10-17) to give the owner's S9 instructions a durable home. It adds no mechanism. Proposed until the owner reviews it; production approval remains external governance.
Builds on ADR-0024 (pack resolved by organisation), ADR-0026 (external packs), ADR-0028 (terminology), ADR-0029 (stable identifiers), ADR-0030 (capabilities) and ADR-0031 (pack validation).

## Context

The portability claim (PRT-002) is only credible if a second, different business runs on the platform without a source change. S9 built that business (Harborline Construction, pack key `construction-subcontracts`, synthetic) and the owner set the rules for the attempt: no construction-specific branches, types, columns, permissions, routes or platform behaviour; nothing business-specific except in the pack, samples, fixtures and documents; do not extend the platform merely to ease the pack; any generic defect found must be documented before it is fixed.

## Decision

1. **The business is a pack.** Everything specific to Harborline comes from the external `.ecpkg` (lifecycle, change-order workflow, terminology, capabilities, fields, matrices, views, notification policy, classification, branding, onboarding defaults). It ships in `templates/packs` beside Blank and the reference pack and is built by the same tool; no project file names it.
2. **A generic defect is fixed generically or not at all.** A defect the proof exposes is first written down (`docs/PORTABILITY_AUDIT.md`), shown to be generic (it would affect any pack), fixed in the smallest platform-neutral way, given a regression test that benefits every pack, and recorded as "S9-discovered generic fix". S9 found one (F1: contract-field labels typed into a Razor tuple literal) and fixed it by turning the labels into terminology tokens.
3. **A limit is recorded, not worked around in code.** Where the platform cannot do what the business would want (a closed notification event list, tenant-wide classification and branding, `requiredFields` semantics, ISO dates in messages), the pack uses what exists and the limit is recorded as a finding with a disposition (G12 to G16).
4. **The boundary is a test.** `SecondBusinessGuardTests` fails if a construction word appears in any shipped platform file or built `Ecp.*` assembly, if a project file names the pack, or if the S9 commits changed any platform path that is not on a short written list of generic fixes (a list capped at four entries). It reads git history against a recorded baseline commit, so CI checks out full history.
5. **Synthetic and not advice.** The pack, its fictional company and its figures are examples. They are not legal, contractual, tax, safety or industry advice, and nothing here claims commercial readiness or fitness for a real construction business.

## Consequences

* PRT-002, PRT-004 and PRT-013 can be re-labelled on evidence (`docs/PORTABILITY_AUDIT.md` section 0.8). PRT-005, PRT-006, PRT-009 and PRT-011 stay NOT VERIFIED.
* The next business (preferred: sell-side Professional Services) follows the same rules; the diff guard's baseline moves forward with each proof slice.
* Alternatives rejected: extending the notification vocabulary or tenant-level documents to suit the pack (platform change for the pack's convenience); a construction product module (out of scope).
