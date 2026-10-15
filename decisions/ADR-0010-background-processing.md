# ADR-0010: Background Processing, Outbox and Inbox

Status: Proposed

## Decision
Use ASP.NET Core hosted services with a **transactional outbox** and **inbox** in the same database, SQL Server application locks (`sp_getapplock`) for singleton jobs, and an explicit tenant scope per unit of work. Host roles `web`, `worker`, `all` choose which services run.

## Context
Master requires outbox/inbox for reliable integrations and tenant-aware background jobs. Risks: IIS recycling and idle shutdown, Azure scale-out double-processing, Docker multi-instance, ambient tenant leakage (RISK-11).

## Decision details
- Outbox rows written with the change; dispatcher claims batches with `UPDLOCK, READPAST`, retries with jittered back-off, dead-letters after a limit with admin replay.
- Inbox unique `(Provider, ExternalEventId)`; handlers idempotent by `DedupeKey`.
- Scheduled jobs (reminders, unlock expiry, projection refresh, sweeper, audit verification, export expiry) hold a named lease and are resumable.
- Jobs open `ITenantContext` from the message; code without a scope fails closed.
- IIS: app pool "AlwaysRunning" with preload; documentation requires disabling idle timeout; readiness R-DEP-I1.
- Alternatives: Hangfire/Quartz (extra dependency and its own storage and licensing; revisit if needs outgrow), message broker (operational cost, three deployment models).

## Consequences
- **Reliability:** at-least-once delivery with idempotent consumers; no lost messages across restarts.
- **Security:** tenant scope explicit; egress through the single guarded client.
- **Operations:** dead-letter queue and lease status shown on the operations dashboard.
- **Reversal:** moderate; interface `IJobScheduler`/`IOutbox` isolates the mechanism.

## Verification
ADP-001/002, CNC-004, NTF-001 to NTF-005, TEN-005, WFL-010.
