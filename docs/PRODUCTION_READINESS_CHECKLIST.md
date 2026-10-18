# Production Readiness Checklist

This checklist is a list of things an organisation must decide, do and evidence. Ticking it is an organisational act. **No tick in this repository means production approval, and the existence of tooling never does.** Production approval is external governance.

- [ ] External identity configured and Demo identity disabled
- [ ] Persistent encrypted database and storage
- [ ] Secrets provider
- [ ] Tenant-isolation tests
- [ ] Backup/restore verified (see "Backup and restore evidence" below)
- [ ] Monitoring and incident ownership
- [ ] Retention, privacy and Legal review
- [ ] Bulk migration mock cycles and reconciliation accepted
- [ ] Malware scanning/file controls
- [ ] Security review and penetration test
- [ ] Operational support owner
- [ ] Formal production approval

## Backup and restore evidence (OPS-01)

The platform provides the tooling (`docs/BACKUP_AND_RESTORE.md`). The organisation supplies the governance. Tooling does not replace organisational backup governance.

- [ ] Backup directory on protected storage owned by the operator; encryption at rest and access control decided and documented (`docs/BACKUP_AND_RESTORE.md` section 13)
- [ ] Backup frequency, retention, recovery point objective (RPO) and recovery time objective (RTO) decided by the organisation and written down (placeholders in `docs/DISASTER_RECOVERY.md`)
- [ ] Off-site or separate-failure-domain copy of backups arranged by the operator (the platform does not copy backups elsewhere)
- [ ] The secret provider's own backup and recovery arranged by the operator (backups hold secret references only, never secrets or private signing keys)
- [ ] At least one backup created and **verified** (Administration, Operations, Backups) with the manifest hash recorded
- [ ] At least one restore rehearsed into a disposable target database and **validated**; the evidence report exported and signed off by a named person (`docs/DISASTER_RECOVERY.md` sign-off template)
- [ ] The operator understands the in-place disaster-recovery procedure and has read the runbook (`docs/OPERATIONS_RUNBOOK.md`)
- [ ] The readiness check for a missing, unverified or stale backup is clear on the installation
- [ ] Rehearsals repeated on the organisation's chosen schedule and after a major upgrade

Not covered by the tooling and therefore not verified here: IIS and Azure deployment models, point-in-time restore, per-tenant restore, remote object-store backup, offline encrypted media, and a rehearsal with the organisation's own data and storage.

## Upgrade evidence (OPS-02)

The platform provides the tooling (`docs/UPGRADES.md`, `docs/UPGRADE_RUNBOOK.md`, `docs/ROLLBACK_BY_RESTORE.md`). Tooling does not replace the organisation's change control, and a Ready pre-flight is not an approval to upgrade or to go live.

- [ ] Upgrade policy understood: installation-level, forward-only, rollback only by restoring the verified pre-upgrade backup with the matching application version; no downgrade exists
- [ ] The previous release's files or image are kept until each upgrade is accepted
- [ ] Pre-flight run against the real installation and every blocker and warning read; warnings accepted by a named person, by code
- [ ] Upgrade rehearsed on a **restored copy** (never the production database) from a verified backup; the rehearsal evidence and duration filed with the change record
- [ ] A fresh backup taken and verified in maintenance mode immediately before the upgrade; its id recorded
- [ ] Maintenance window, communication and change approval arranged by the organisation
- [ ] Post-upgrade validation read and the organisation's own acceptance checks performed
- [ ] Rollback evidence produced for the pre-upgrade backup; data-loss window and recovery time recorded against the organisation's RPO and RTO
- [ ] Sign-off recorded by a named person (`docs/UPGRADE_RUNBOOK.md` section 10)

Not covered by the tooling and therefore not verified here: IIS and Azure deployment models, zero-downtime or mixed-version operation, a previous-release binary as the N-1 source (the N-1 fixture is a frozen schema populated by current services), and an upgrade rehearsal with the organisation's own data and storage.

## Operations dashboard and monitoring (OPS-03)

The platform provides the dashboard (`docs/OPERATIONS_DASHBOARD.md`). Monitoring, alerting and on-call are the organisation's.

- [ ] The people who will operate the installation can open the operations dashboard and have read each panel's explanation; nobody treats **Not Verified** as passed
- [ ] Every line currently Not Verified has been either done or accepted in writing (TLS certificate and secret expiry tracked where they live; clock skew; deployment-model checks; the unbuilt readiness-catalogue checks, SET-03)
- [ ] A named person looks at the dashboard on a schedule the organisation chooses, and before and after every maintenance window
- [ ] Alert thresholds, on-call duty and the incident process are decided and written down; the platform does not alert by itself
- [ ] A telemetry exporter (OpenTelemetry, syslog or SIEM) is chosen and verified if the organisation needs one; none is built or verified in the platform (INT-04)
- [ ] The support-bundle procedure is agreed: who may generate one (recent sign-in required), where it may be sent, and how long the recipient keeps it
- [ ] Retention of operation history and audit data is decided

Not covered here and therefore Not Verified: IIS and Azure deployment models, exporters and alerting, TLS certificate monitoring, clock-skew measurement.
