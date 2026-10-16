# Supply Chain: vulnerabilities, SBOM and licences

Task FND-07; requirements SEC-060 to SEC-062 (`docs/TESTING.md`), ADR-0014. **Status: Implemented** (local run verified on 2026-10-17; the CI jobs are Scaffolded / Not Verified until they run on a GitHub runner).

## What runs

| Control | Where | Result type |
|---|---|---|
| NuGet vulnerability audit as a **build gate** | `Directory.Build.props` (`NuGetAudit`, mode `all`, level `low`, warnings as errors) so a vulnerable direct or transitive package fails restore/build; vulnerable transitive versions are pinned in `Directory.Packages.props` | Build fails |
| Locked restore | `packages.lock.json` per project; CI uses `dotnet restore --locked-mode` | Restore fails if the lock file is stale |
| Vulnerability report | `tools/supply-chain.sh` (`dotnet list package --vulnerable --include-transitive`) | Fails on any finding |
| SBOM | `tools/supply-chain.sh` produces `artifacts/sbom/ecp.bom.json` (CycloneDX 1.6, runtime packages; test projects excluded) with the pinned `CycloneDX` tool | Fails if not generated; uploaded by CI |
| Licence scan | `nuget-license` (pinned) lists every package including test-only; `tools/check-licences.py` classifies each against `tools/licence-policy.json` | Fails on prohibited, restricted-without-record, unknown licences or stale exceptions; warns on `PendingOwnerReview` |
| Policy script tests | `tools/tests/test_check_licences.py` (`python3 -I -m unittest discover -s tools/tests`) | CI |

Tools are pinned in `.config/dotnet-tools.json` (`dotnet tool restore`). Run everything with `tools/supply-chain.sh` (needs NuGet access).

## Licence categories

These categories are an **engineering control**. They are not legal advice, and no category, exception or report in this repository constitutes legal approval of a licence. The product owner and counsel decide; Release readiness lists open reviews (`.claude/skills/release-readiness`).

| Category | Meaning | Examples (SPDX) | Handling |
|---|---|---|---|
| **Allowed** | Permissive licences the maintainers accept for use and redistribution with attribution | MIT, Apache-2.0, BSD-2-Clause, BSD-3-Clause, ISC, MS-PL, 0BSD, Zlib, Unlicense, BSL-1.0, CC0-1.0 | Automatic. Attribution notices are collected from the SBOM at release |
| **Restricted** | Weak copyleft or conditional licences that need a recorded review of how the package is used (linking, modification, distribution) | MPL-2.0, LGPL-*, EPL-*, CDDL-1.0, CC-BY-* | Not accepted automatically. A named exception with evidence and status must exist in `tools/licence-policy.json` |
| **Prohibited** | Strong copyleft, network copyleft, source-available or field-of-use restricted licences incompatible with a closed commercial distribution | GPL-*, AGPL-*, SSPL-1.0, BUSL-1.1, Commons-Clause, CC-BY-NC-*, Elastic-2.0 | Build gate fails; cannot be excepted without an ADR |
| **Unknown** | Missing, file-only or unrecognised licence metadata | n/a | Fails until resolved or recorded as an exception |

Exceptions carry a status: `Recorded` (maintainers resolved the licence from evidence; owner to confirm) or `PendingOwnerReview` (not an open-source licence or needs a decision before distribution). Pending reviews do not fail the build but are surfaced in every run and in release readiness.

## Current findings (2026-10-17)

- 98 packages are Allowed (MIT or Apache-2.0).
- 3 packages are `Recorded` exceptions, all **test-only** (`NetArchTest.Rules`, `Mono.Cecil`, `xunit.abstractions`): their metadata lacks an SPDX expression, so the licence was resolved from the metadata URL or the upstream repository.
- 2 packages are **`PendingOwnerReview`**, and they ship with the application: `Microsoft.Data.SqlClient.SNI.runtime` and `Microsoft.Identity.Client.NativeInterop` carry *Microsoft Software License Terms* (not an OSI licence). The terms must be reviewed by the product owner or counsel before a release that includes the Windows native libraries is distributed. Nothing here approves them.
- No known vulnerable package (direct or transitive) at the time of the run.

## Updating

Adding a dependency: add it to `Directory.Packages.props`, regenerate lock files (`dotnet restore --force-evaluate`), run `tools/supply-chain.sh`. A new licence outside Allowed fails the run until it is classified or an exception is recorded with evidence.
