# ADR-0049: Files scanned before upload: the platform's scanner can be replaced by an external-scanning statement

Status: Implemented; tests pass (`DocumentScanningUiTests` and `ScannerPageTests` ADR_0049 cases); **Not Verified on IIS**. Owner decision 2026-11-25 (Jeff): JBS scans files automatically before they can reach the system, so this deployment does not run its own scanner. Builds on ADR-0038 (scanner integration boundary). Production approval remains external governance and is not declared.

## Context

In Production an uploaded file is released only when a connected, real scanner that passed its self-test calls it Clean (G-25, R-DOC-01). No real scanner adapter is supplied, so a Production-mode installation held every attachment and its setup readiness review (R-OPS-01, which includes R-DOC-01) could not finish. JBS's own controls scan files before a user can upload them.

## Decision

1. Host configuration `Documents:Scanner:Provider = external` (on IIS, the GitHub environment variable `APPENV_Documents__Scanner__Provider`) registers `ExternalScanningAttestation`. It scans nothing and returns Clean, so files are released. Only the host configuration selects it; nothing in the application, setup or an imported package can.
2. It is honest everywhere it shows: its name, recorded in every scan attempt and audit entry, is "scanned before upload (external, not by this platform)"; scanner readiness is the new state **External** (code SCN-030), never Healthy and never Verified; the self-test reports that there is nothing to test.
3. R-DOC-01 reports External as **Degraded** in every mode: a warning that setup readiness R-OPS-01 makes someone acknowledge with a reason, instead of the Unhealthy result that blocked. Allowing skipped scans in Production stays Unhealthy.
4. Unchanged: type allowlist, signature sniffing, size limits, `nosniff` and `Content-Disposition` on download, quarantine of anything a future real scanner calls Infected.

## Consequences

* Protection against malicious uploads rests on JBS's controls before upload (threat T-030). Files that arrive by another path (bulk migration import, e-signature returns) are released the same way; they must be covered by the same external controls.
* Reversal: connect a real scanner adapter (ADR-0038 checklist) and change the provider; files already released stay released, so a re-scan of stored files would be a separate step.
