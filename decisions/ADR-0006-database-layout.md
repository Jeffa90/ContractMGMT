# ADR-0006: Database Layout: Schema and DbContext per Module

Status: Proposed

## Decision
One SQL Server database; one schema per module; one EF Core `DbContext` per module deriving from a shared tenant-aware base; one ordered migration runner and history.

## Context
Modular monolith (ADR-0001), tenant isolation (ADR-0003), three deployment models, Azure SQL and SQL Server 2022+. Master requires EF Core and SQL Server. The assumption set is ASM-03.

## Alternatives considered
- Single DbContext: simpler but couples modules and hides ownership; migrations become one shared history.
- Database per module: breaks transactional audit/outbox atomicity and complicates operations.
- Dapper-only data access: loses change tracking and global filters; raw SQL surface grows.

## Decision details
- Schemas: `tenancy`, `identity`, `audit`, `config`, `contracts`, `documents`, `migration`, `integration`, `notify`, `retention`, `reporting`, `setup`.
- Cross-module reads through public query interfaces or `reporting` views; no cross-module writes.
- The runner applies module migrations in dependency-tier order, records a single version vector, and refuses to start if schema is ahead of code.
- Keys: sequential GUIDs generated in the app; composite `(TenantId, Id)` for foreign keys where tenant-owned.
- Concurrency: `rowversion`. Time: `datetime2(7)` UTC. Money: `decimal(19,4)` + currency code.
- The runtime login has no schema-owner rights; migrations run under a separate privileged login in Production pre-flight (R-DB-03).

## Consequences
- **Security:** least-privilege runtime login; audit table INSERT/SELECT only.
- **Operations:** more migration projects; mitigated by generated runner and ordering tests.
- **Reversal:** merging contexts is mechanical; splitting later is costlier, so start split.

## Verification
ARC-001/002/003; UPG-001; AUD-001; DEP-007 (R-DB-03).
