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
