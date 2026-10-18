# Operations dashboard (OPS-03)

Status: **Implemented** (service `IOperationsDashboard`, page **Administration, Operations, Operations dashboard** at `/Admin/Operations/Dashboard`, JSON form at `?handler=Json`). **Verified locally** on SQL Server and SQLite; CI result in `deploy/STATUS.md`. Owner decisions: `requirements/01-authoritative/CONFIRMED_DECISIONS.md`, section D12. Architecture: `decisions/ADR-0036-operations-dashboard-and-observability.md`. Scope and criteria: `docs/OPS03_REQUIREMENTS_MATRIX.md`.

> **The dashboard is evidence for the operator, not an approval.** A page full of Verified lines does not mean the installation is ready for production, and nothing here declares production readiness. Not Verified means nobody has looked, or it cannot be looked at here; it is never a pass.

## 1. Labels used in this document

| Label | Meaning here |
|---|---|
| **Implemented** | Built, tested, and used from the operator interface |
| **Verified locally / in CI** | Exercised by an executed test; the result is recorded in `deploy/STATUS.md` |
| **Scaffolded** | Files or hooks exist but cannot be run here |
| **Not Verified** | No executed evidence, or the platform cannot read it |
| **Future** | Not built |
| **External organisational responsibility** | Decided and done by the organisation, not by the platform |

## 2. Who may see it, and what they see

| Reader | Sees |
|---|---|
| Anonymous | Nothing. `/health/live`, `/health/startup`, `/health/ready` answer minimally; in Production `/health/ready` is the overall status only (`HealthBodies.Ready`). Demo and Test also list the per-check status, because the container smoke test relies on it; they are not internet-facing by design |
| Anyone in a customer tenant, including a tenant administrator, an auditor and the installation administrator acting from inside a customer tenant | Refused (403). The service checks the permission **and** that the caller is in the installation's own tenant |
| Holder of `operations.read` in the installation tenant (System Administrator) | The whole dashboard without technical detail columns |
| Also holder of `operations.validate` | Technical detail (identifiers, counts behind a line, redacted messages) |

Reading needs no recent sign-in because nothing is changed. The support bundle download is the one sensitive action reachable from here and needs a recent sign-in (section 8). The dashboard works during maintenance mode (GET).

## 3. What is on it

Each line has a stable code, one of four states, a plain explanation, the time of the last result and its correlation id where one exists, and what to do next with a link. A panel's state is its worst line (Failed, then Not Verified, then Warning, then Verified); so is the dashboard's.

| Panel (key) | Lines (codes) | Source (reused service) |
|---|---|---|
| Installation health (`health`) | `HLT-001` readiness overall and every check that runs on each start and on request (`R-DB-01`, `R-ST-02`, `R-OPS-02`, backup, capability, pack and regional checks); `HLT-002` maintenance mode, its cause and since when | `ReadinessService`, `IMaintenanceService` |
| Readiness catalogue (`readiness`) | One line per catalogue check, `R-ID-01` to `R-MIG-01` (30), from the specification | see section 4 |
| Platform and schema versions (`versions`) | `VER-001` platform version, mode, engine; `VER-002` schema version per module and pending migrations | migration inventory, `SchemaInspector` |
| Upgrade readiness and migrations (`upgrade`) | `UPR-001` upgrade state; `UPR-002` last upgrade, `UPR-003` last rehearsal, `UPR-004` last rollback evidence; `UPR-005` failed upgrade; `UPR-006` data-migration batches in progress | operation history, `MigrationOperationalProbe` |
| Backup and restore (`recovery`) | `REC-001` last verified backup and its age against `StaleBackupDays`; `REC-002` last failed backup; `REC-003` last restore; `REC-004` last validation | backup catalogue, history, `IOperationsAdministration.LastValidationAsync` |
| Background jobs and leases (`jobs`) | `JOB-001` jobs across all tenants (failures in 24 h, stale runs, paused by maintenance); `JOB-LEASE` each live lease; `JOB-STALE` a lease that expired without release; `JOB-STUCK` an operation recorded as running with no live lease | `NotificationsOperationalProbe`, `IOperationLeases`, history |
| Notification outbox (`outbox`) | `OUT-001` waiting, failed (will retry), dead-lettered, sent in 24 h, oldest waiting age | `NotificationsOperationalProbe` |
| Packs and configuration (`configuration`) | `CFG-001` releases in force and open drafts; `CFG-002` pack compatibility (in use, idle, untrusted, unreadable) | `ConfigurationOperationalProbe` (reuses the upgrade pack probe) |
| Document store and audit chain (`integrity`) | `INT-001` storage round trip now; `INT-002` document content and metadata, `INT-003` audit chain, both from the last validation with its time | `R-ST-02`, last validation (`VAL-009`, `VAL-010`, `VAL-015`) |
| Certificates and secret expiry (`certificates`) | `CRT-001` pack-signing keys (expired, expiring in 30 days); `CRT-002` data-protection key ring; `CRT-003` backup-encryption certificate; `CRT-004` TLS certificate; `CRT-005` secret expiry | `ConfigurationOperationalProbe`, `IKeyRingInspector` |

Also on the page: **Recent operational failures**, the operation leases, schema versions by module, and the local metrics. Download as JSON for monitoring that polls it. Links lead to the existing pages (Backups, Restore, Validation, Maintenance, Leases, Evidence, Upgrade pages).

## 4. Readiness catalogue coverage (SET-03 remains open)

`requirements/12-operations/SETUP_AND_READINESS_SPEC.md` section 4 lists 30 checks. The dashboard reports each, honestly:

* **Runs here** (result shown): the platform's own readiness checks (`R-DB-01`, `R-ST-02`, `R-OPS-02`, `R-DB-04` as the backup check), plus three the dashboard derives from facts it already has: `R-OPS-03` (jobs and outbox), `R-OPS-04` (maintenance mode; clock skew is not measured, so it is Not Verified while maintenance is off), `R-SEC-04` (key ring has a usable key).
* **Exists elsewhere, not run here** (Not Verified, with that reason): checks that run in the setup wizard or as the start-up guard (`R-ID-01`, `R-ID-02`, `R-ST-01`, `R-SEC-01`, `R-SEC-02`, `R-CFG-01`, `R-NOT-01`, `R-DEMO-01`, `R-OPS-01`). They need the wizard's context to run.
* **Not built** (Not Verified, "not built"): the rest, including every deployment check (`R-DEP-D1`, `R-DEP-I1`, `R-DEP-A1`).

The coverage line on the page states the three numbers. Building the remaining checks is SET-03, not OPS-03.

## 5. How a line is judged (rules in one place: `OperationsDashboardService`)

| Fact | Failed | Warning | Verified | Not Verified |
|---|---|---|---|---|
| Readiness check | Unhealthy | Degraded | Healthy | could not run |
| Maintenance mode | cause *Upgrade failed* or *Restore failed* | on for any other cause | off | |
| Schema | migrations pending | | all applied | not readable (non-SQL Server) |
| Last backup | | none verified; older than `StaleBackupDays`; verified with warnings | newest verified, recent | not supported / SQLite |
| Last restore, validation, rehearsal, rollback evidence | last run failed or incomplete | completed with warnings; running | completed | none recorded |
| Jobs | a run recorded as running for over an hour | failures in 24 h; none completed in 24 h; paused by maintenance | completed recently | no job has ever run; facts unreadable |
| Outbox | | failed, dead-lettered, or waiting over an hour (not in maintenance) | draining | facts unreadable |
| Pack in use | incompatible, untrusted or unreadable | an idle pack that would be refused | all acceptable | facts unreadable |
| Pack-signing keys | an active key past its end date | expiring within 30 days | none expiring | |
| Key ring | unreadable or no usable key | newest key expires within 7 days | usable key | host does not expose it |
| Integrity from last validation | the check failed | passed with warnings | passed | never validated |
| TLS certificate, secret expiry, backup-encryption certificate | | | | always, with the reason |

## 6. Failure management

The failure register lists failed or incomplete operations (newest first, up to 15) with the last result, time, correlation id and the next step. A line, a log event and an audit event share the correlation id.

| Failure | The dashboard says | A new attempt offered? |
|---|---|---|
| Backup | The backup is not a recovery point; fix the cause, then start a new backup | **Yes**, a fresh run (never a resume) |
| Validation | Read the failing checks, fix, run again | **Yes** |
| Upgrade rehearsal | The copy was used; the installation is untouched; rehearse again on a new copy | **Yes** |
| Rollback evidence | Rollback is not yet proved for this backup | **Yes** |
| **Restore** | Never retried automatically; maintenance mode stays on; follow the restore failure steps | **No** |
| **Upgrade** | Never retried; maintenance mode stays on; roll back by restoring the verified pre-upgrade backup | **No** |
| Stuck: an operation recorded as running with no live lease | The process stopped part-way; its result is unknown and must not be assumed good | **No**: read its evidence |
| Lease expired without release | The holder stopped; nothing is blocked (an expired lease can be taken); check the history for an interrupted run | takeover exists on the Leases page (re-authentication, reason, audited) |
| Job runs failing | The next cycle repeats them (every job is safe to repeat); read the tenant's own job page | automatic by cycle; no manual trigger |
| Dead-lettered messages | A tenant administrator retries them in that tenant's Notifications page | **Not from here**: an operator cannot act on another tenant's messages |
| Pack or configuration incompatibility | Run the upgrade pre-flight for the exact packs | No change is made by the dashboard |
| Audit-chain or document mismatch | Read the failing validation check | No change is made by the dashboard |

Critical failures are never hidden or downgraded: a failed upgrade is Failed on the dashboard, in the failure register and in the health panel until it is dealt with. Operator interventions that exist (maintenance, lease takeover, backup, restore, upgrade, validation, evidence, support bundle) are audited by their own services; the dashboard adds none because it changes nothing, and no action here can bypass tenant isolation or a record lock.

## 7. Observability (foundation)

| Item | Status |
|---|---|
| Correlation id on every operation, dashboard build, log event and audit event | **Implemented**, **Verified locally** |
| Structured events (`ecp.operation.finished`, `ecp.jobs.cycle`, `ecp.dashboard.built`, `ecp.supportbundle.generated`), failures as warnings, parameters limited to fixed words, counts and ids | **Implemented**, **Verified locally** |
| .NET `Meter` `Ecp.Operations`: counters `ecp.operations.finished` (kind, status), `ecp.jobs.cycles` (outcome), `ecp.dashboard.builds` (overall); gauges `ecp.outbox.items`, `ecp.jobs.runs`, `ecp.leases`, `ecp.health.panels`, set when the dashboard is built | **Implemented**, **Verified locally** (a `MeterListener` reads them) |
| Local reader (the dashboard's metrics block, the support bundle's `metrics`) | **Implemented** |
| OpenTelemetry exporter, syslog, SIEM export, Application Insights | **Not built** (INT-04) and **Not Verified**. The Meter is what such an exporter would subscribe to |
| Redaction before any log or evidence output | **Implemented** (`Redaction.Clean`, secret scan on bundle and evidence) |

Gauges hold the values of the most recent dashboard build in this process; they are not a time series. A real monitoring system needs an exporter and scrape interval (external organisational responsibility).

## 8. Support bundle (completion for OPS-03)

See `docs/SUPPORT_BUNDLE.md`. From the dashboard: **Evidence report and support bundle**. Generation needs `operations.evidence.export` **and a recent sign-in**, is audited (`operations.supportbundle.exported`: size, SHA-256, correlation id), and nothing is stored on the server, so there is no expiry to manage.

## 9. Limitations (stated honestly)

* The page reads on request; it is not a monitoring system and does not alert. Poll the JSON form, or use an exporter once INT-04 exists.
* Counts are summed across tenants by probes; on an installation with very many tenants the build time grows with the number of tenants.
* Job and outbox facts exist only for modules that contribute a probe (Notifications, Data Migration, Configuration). Other modules' background work is not shown.
* Integrity lines come from the last validation; run validation to refresh them. A full document hash on every page load would be too costly.
* TLS, secrets, backup-encryption certificate, clock skew, deployment checks and the unbuilt catalogue checks are Not Verified (section 4 and 5).
* IIS and Azure deployments are **Scaffolded / Not Verified** for everything on this page.
* Retention of operation history, alert thresholds, on-call and incident process are the organisation's (`docs/OPERATIONS_RUNBOOK.md`, `docs/PRODUCTION_READINESS_CHECKLIST.md`).
