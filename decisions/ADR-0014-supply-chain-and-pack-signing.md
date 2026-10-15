# ADR-0014: Supply Chain, Licences, SBOM and Pack Signing

Status: Proposed

## Decision
Lock dependencies; scan for vulnerabilities and licence problems on every build; generate a CycloneDX SBOM; prefer permissive licences for spreadsheet and archive handling; sign shipped and exported packs with Ed25519 using keys held outside the repository and application.

## Context
Master: dependency scanning and SBOM "where practical"; signed/checksummed packs. Review risks: XLSX library licensing (RISK-10), supply chain (RISK-18, T-076), key management (T-045).

## Decision details
- Central package management, lock files, `restore --locked-mode`; vulnerable and deprecated package checks fail the build above a severity threshold (SEC-060).
- Licence allowlist (permissive licences); components with non-commercial, source-available-with-restrictions or incompatible licences are rejected (SEC-062). Spreadsheet handling uses an Open XML based permissively licensed library; the choice is made in MGR-02 with licence evidence recorded here.
- SBOM archived per build (SEC-061); base images pinned by digest and rebuilt on a schedule.
- Pack signing: private keys are generated and kept offline or in an HSM/Key Vault; the application and repository hold public keys only; tenants register trusted publisher keys; revocation list supported (`CONFIGURATION_PACKAGE_SPEC.md` section 4). Rotation procedure documented in DOCS-OPS.
- CI (if introduced) pins third-party actions by commit SHA and uses minimal token permissions.

## Alternatives considered
No signing (rejected: Production catalogue trust), signing with shared secret (rejected: no publisher identity), X.509 PKI (heavier; revisit for enterprise catalogues).

## Consequences
- **Security:** reduced compromise window; auditable inventory.
- **Operations:** key custody is a human process; documented, not automated.
- **Reversal:** algorithm agility via `keyId` and manifest `formatVersion`.

## Verification
SEC-060 to SEC-062, PKG-004/005, PRT-007/008.
