# Support bundle (foundation)

Status: **Implemented** (`ISupportBundle`; command `support-bundle <file>`; **Operations, Evidence, Download the support bundle**, permission `operations.evidence.export`). Verified by `OperationsFoundationScenarios.The_support_bundle_and_evidence_hold_no_secret...`, `OperationsUiTests` and `RecoveryTests`.

A support bundle is a small zip (a JSON file of facts and a README) that an operator can send to whoever supports the installation, so that a problem can be diagnosed from facts rather than from guesswork. It is built from fields that never hold secrets or content, and is scanned again before it is returned; if the scan finds anything that looks like a secret, no bundle is produced.

## Contains

| Section | Content |
|---|---|
| Identity of the build | Environment mode; database provider; the version of every platform assembly |
| Migrations | Per module: how many are applied and the latest |
| Readiness | Each readiness check's id, status and (redacted) message |
| Counts | Number of tenants and organisations |
| Tenants | Per tenant: organisation count; installed packs (key, version, package SHA-256, signature status, status); configuration kinds with release counts and highest release number (no content) |
| Notification queue | Deliveries by status |
| Backup and restore | Whether backup is configured and encryption requested, maintenance state, the last backups (state, outcome, warnings count, verified time), the last restores, current leases |
| Recent operational failures | Failed or incomplete runs (kind, status, reference, redacted summary, time, correlation id) |
| Recent correlation ids | So support can find the matching log lines |
| Deployment models | The status of each deployment model (Verified, Scaffolded / Not Verified, Not implemented) |
| Recent log lines | The last warnings and errors the process logged: time, level, category, **redacted** message and the exception **type** only (never an exception message or stack trace) |
| Exclusions list | A statement of what is deliberately absent |

## Never contains

Secrets, tokens, passwords, connection strings, private keys, contract content, document content, personal data (names and email addresses are masked if they appear in a log line), full provider callback payloads, raw configuration content, backup contents. Automatic redaction masks connection-string fragments, key/value secrets, bearer tokens, PEM private-key blocks, credentials in URLs and email addresses, and flattens line breaks so a value cannot forge a log line.

## Responsibility

Look at the bundle before you send it. The platform checks it; you remain responsible for where it goes.
