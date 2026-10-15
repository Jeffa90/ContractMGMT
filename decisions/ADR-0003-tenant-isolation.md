# ADR-0003: Tenant Isolation

Status: Proposed

## Decision
Enforce tenant isolation in database access, service policies and tests.

## Context
Master: "Tenant isolation must use TenantId on owned entities, query filters, service policies, database constraints and cross-tenant tests. UI filtering alone is prohibited." Three product modes exist (single organisation, multiple organisations, multi-tenant hosting). The review found the boundary undefined (CON-07).

## Decision details
1. **Tenant is the hard boundary; organisation is a policy scope inside a tenant** (ASM-04).
2. Layers, each independently tested (`docs/ARCHITECTURE.md` section 6): authenticated tenant resolution, explicit scope object, EF global query filters, SQL Server Row-Level Security via `SESSION_CONTEXT`, composite foreign keys including `TenantId`, tenant-prefixed storage keys, tenant-keyed caches, scoped jobs and outbox messages, per-tenant reporting principals.
3. Single-organisation installs use one fixed tenant and organisation (ASM-05) so no code path differs by mode.
4. Banned by analyzer/architecture test: `IgnoreQueryFilters`, unwrapped raw SQL, ambient tenant state, unprefixed cache keys.

## Alternatives considered
- Database-per-tenant: strongest isolation but heavy operations, three deployment models, wizard complexity; kept as a future hosting option via the same abstractions.
- Schema-per-tenant: complicates EF models and migrations at scale.
- Filter-only: rejected as a single point of failure.

## Consequences
- **Security:** a single missed filter cannot expose data (layer-off tests TEN-030/031).
- **Performance:** RLS predicate overhead measured (PER-005); all indexes lead with `TenantId`.
- **Operations:** connection interceptor must reset session context when returning connections to the pool; covered by tests.
- **Migration:** idempotency keys include tenant and organisation; cross-tenant references rejected twice (MIG-008).
- **Reversal:** hard to reverse after data exists; hence decided now.

## Verification
TEN-001 to TEN-040, ARC-003 to ARC-006, SEC-040 to SEC-045.
