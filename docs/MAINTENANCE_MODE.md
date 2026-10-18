# Maintenance mode (OPS-02 foundation)

Status: **Implemented**, verified by `OperationsFoundationScenarios`, `OperationsUiTests` and the restore tests. Architecture: ADR-0034. Companions: `docs/BACKUP_AND_RESTORE.md`, `docs/OPERATIONS_RUNBOOK.md`.

Maintenance mode is one installation-wide flag. It exists so that nothing changes data while an operator backs up for an exact copy, restores, validates or upgrades, and so that a restore that failed cannot be forgotten.

## 1. What it does

| Area | Behaviour while on |
|---|---|
| Requests that change state (POST, PUT, PATCH, DELETE, on any route) | Refused by the host with **503** and `Retry-After: 120`. Pages get a short plain-text answer; `/api` callers get problem details with the stable code `maintenance_mode`. The answer never says why or by whom, and never includes internals |
| Reads (GET, HEAD, OPTIONS) | Still served: people can look things up |
| Always open | `/health/*` (liveness, startup, readiness), sign-in and sign-out, re-authentication, the error page, and **Administration, Operations** (how an operator turns it off). The comparison is on the normalised path, case-insensitive, by segment; a path that only resembles an allowed one (a doubled slash, a longer segment) is refused |
| Background jobs | The job cycle takes **no new work** (notification generation, delivery, reminders, emergency-unlock expiry). Work already running finishes its current unit; every job is safe to repeat, so nothing is half-applied. Jobs resume on their next cycle after maintenance mode ends |
| Banner | Every page shows "The system is in maintenance. Changes are paused until an operator ends maintenance." The reason and cause are shown to operators on the Maintenance page only |
| Authorised operators | Can still read and use the Operations pages, including starting a backup, validation or restore |

It is a **server-side** control (middleware plus job gating), not a UI hint. A caller cannot bypass it by calling an endpoint directly. It does not stop an operator's own Operations actions, a process with direct database access, or a read request that has a side effect (the platform has none that change business data).

## 2. Who can change it

`operations.maintenance` (System Administrator), with a recent sign-in and a **mandatory reason**. Both transitions are audited (`operations.maintenance.enabled` / `disabled`) with the reason and who. The command line (`maintenance on|off --reason "..."`) is for an operator with access to the server.

## 3. Causes and the restore rule

| Cause | Set by | Can be left by |
|---|---|---|
| `Manual` | An operator | An operator with a reason |
| `RestoreInProgress` | A restore, on the installation | The restore workflow when it finishes; an operator only with an explicit acknowledgement |
| `RestoreValidationPending` | A restore, on the restored copy | The restore workflow when validation passes |
| `RestoreFailed` | A restore that failed, on the installation (and the copy, if it exists) | **Only an operator who acknowledges the restore state knowingly** (checkbox on the page, `--acknowledge-restore-state` on the command line), with a reason |

So a failed restore leaves the installation protected until a person decides otherwise; "just let me in" is refused with the code `restore_state`.

## 4. Several instances

The flag is in the database. Each instance keeps a copy for at most three seconds (and drops it at once when it makes a change itself), so another instance sees a change within three seconds. If the flag cannot be read (for example the operational tables are not migrated yet), requests are not refused for a state that cannot be known; readiness reports the database problem.

## 5. Not in scope

Scheduled maintenance windows, per-tenant maintenance, a custom maintenance page, and automatic entry on health failure are Future.
