# Operation leases (OPS-02 foundation)

Status: **Implemented** (SQL Server and SQLite), concurrency verified on SQL Server by `Many_contenders_on_many_instances_at_once_produce_exactly_one_owner` and `Two_backups_at_once_produce_one_backup_and_one_refusal...`. Architecture: ADR-0034.

A lease is a time-limited claim on a piece of work that must not run twice at the same time, across every instance that shares a database: a backup, a restore, a validation, a tenant's background-job cycle. In-process locks do not hold across instances, so the database decides.

## 1. Model (`ops.OperationLease`)

One row per (name, scope): `Name` (lower-case letters, digits and `. _ : -`), `ScopeKey` (`installation` or a tenant id), `OwnerId` and a human `OwnerDescription`, `AcquiredUtc`, `HeartbeatUtc`, `ExpiresUtc`, `ReleasedUtc`, `Generation` (bumped each time the lease changes hands; a fencing token) and the row version. A unique index on (tenant, name, scope) and optimistic concurrency make acquisition atomic: of any number of simultaneous contenders exactly one wins, the rest get "not acquired" (not an error). Rows belong to the installation tenant and are protected by the same row-level security as every other table.

## 2. Behaviour

| Event | Result |
|---|---|
| Acquire a free, released or expired lease | The caller owns it; `Generation` increases when the owner changes |
| Acquire a lease held by another owner | Refused; nothing changes. The message names the holder |
| Ask again as the current owner | Allowed (it renews) |
| Renew | Extends `ExpiresUtc` and `HeartbeatUtc`; **fails** if the lease expired, was released, or was taken over, and the work must then stop (`LeaseHolder.Lost`) |
| Holder crashes | It stops renewing; after `ExpiresUtc` another owner can take the lease. That takeover is audited (`operations.lease.expired_takeover`). A crashed instance never blocks the work for ever |
| Operator takes over | `operations.maintenance`, recent sign-in, reason; audited (`operations.lease.taken_over`); the old owner's next renewal fails. The page releases the lease at once so the work can be started again |
| Release | Only by the owner of that generation |

`LeaseHolder` keeps a lease renewed from a timer (in a scope of its own, because a scope's connection is not safe to share) and releases it on disposal.

## 3. Where leases are used

| Lease | Scope | Held by |
|---|---|---|
| `recovery.exclusive` | `installation` | Backup, restore, validation of the running installation, and an upgrade |
| `upgrade.exclusive` | `installation` | An upgrade (OPS-02), held together with `recovery.exclusive`: names the upgrade in progress and keeps a second upgrade, backup, restore or validation out. A lease lost mid-run aborts the upgrade |
| `background.jobs` | the tenant id | One tenant's background-job cycle (notification generation, delivery, reminders, unlock expiry), so two instances sharing a database never run the same tenant's jobs at once |

Not leased (and documented, not hidden): jobs inside one instance run one tenant at a time already, and every job is safe to repeat, so a lease is a protection against duplicate work, not the only one.

## 4. Operator view

**Administration, Operations, Leases** lists each lease, its owner, when it was acquired and last renewed, when it expires and whether it is held, with the takeover form. A held lease whose owner is gone expires by itself within `LeaseMinutes` (default 10 for recovery work; renewed every third of that).

## 5. Limits

Time is the platform clock of each instance (UTC); instances whose clocks differ by more than the lease term can disagree about expiry, so keep clocks synchronised. SQLite proves provider-independent behaviour only; the SQL Server run is the concurrency evidence.
