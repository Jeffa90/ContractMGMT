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

## Completion for OPS-03 (what changed, 2026-10-18)

| Item | Status |
|---|---|
| Generation needs `operations.evidence.export` **and a recent sign-in** (previously the permission only). The command line `support-bundle <file>` is unchanged: it runs on the server for an operator who already has access to it | **Implemented**, **Verified locally** |
| One audit event per generation and download, `operations.supportbundle.exported`: size, SHA-256, correlation id. Nothing is stored on the server, so there is nothing to expire or clean up | **Implemented**, **Verified locally** |
| Contents are now four files: bundle.json, summary.csv (formula-safe: cells starting `=`, `+`, `-`, `@`, tab or carriage return are neutralised), README.txt and manifest.json | **Implemented** |
| manifest.json lists every other file with its size and SHA-256, in a fixed order, and its limits; the hashes are checked by a test | **Implemented**, **Verified locally** |
| Entry names are an allow-list (plain file names, no path, no leading dot); each entry is capped at 2 MB and the bundle at 5 MB; archive timestamps are fixed so the archive does not reveal the host's clock | **Implemented**, **Verified locally** (unit tests on the rules) |
| New content: operational facts (outbox counts and times, job counts and times, data-migration batches in progress, releases in force, pack compatibility counts, signing-key expiry counts), the metrics snapshot, the correlation id of the generation, and a statement that no telemetry exporter is configured | **Implemented**, **Verified locally** |
| Canaries: a secret in an operation summary, a token, a connection string, a personal e-mail address, a failed job's own error text with a contract title, and a log line are all absent from every file | **Verified locally** (`OperationsDashboardScenarios`, SQLite and SQL Server) |
| The SEC-030 and SEC-031 catalogue of cases and a fuller redaction rule set belong to **OPS-04** and are **not** claimed here | OPS-04 open |

The bundle is evidence for the person who supports the installation. Look through it before you send it on; where it goes is the organisation's responsibility.
