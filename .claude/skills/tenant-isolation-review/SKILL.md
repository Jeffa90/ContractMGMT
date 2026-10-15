---
name: tenant-isolation-review
description: Test and review tenant and organisation isolation across records, documents, jobs, caches, search, reports, imports, exports, packages, notifications, audit and administration, including layer-off tests for query filters and SQL Row-Level Security. Use after any new entity, query, cache, job, export or integration.
---

# Tenant Isolation Review

Tenant is the hard boundary; organisation is a policy scope within a tenant; Agreement Reference is unique per tenant.

## Read first
1. `decisions/ADR-0003-tenant-isolation.md`, `docs/ARCHITECTURE.md` section 6
2. `requirements/08-portability/TENANCY_AND_ORGANISATION_SPEC.md`, `requirements/05-security/ACCESS_CONTROL_SPEC.md`
3. `docs/TESTING.md` section 5 (TEN-001 to TEN-040) and ARC-003 to ARC-006
4. Fixtures: `sample-data/demo-organisation-structure.json`, `demo-users-and-roles.json`, `demo-categories-and-assignments.json`

## Procedure
1. Enumerate every tenant-owned entity from the model. Each must implement `ITenantOwned`, have the global query filter, have a Row-Level Security policy, and use composite `(TenantId, ...)` keys for foreign keys. A missing item fails ARC-003.
2. Search the code for `IgnoreQueryFilters`, raw SQL, static or ambient tenant state, cache access, `HttpContext.Items`, background job entry points and file path construction. Each use must be on the allowlist or is a finding (ARC-004 to ARC-006).
3. Using the two-tenant fixtures (overlapping category code `PACKAGING` in both), exercise every surface in section 5 of `docs/TESTING.md`. Include `viewer.a2` (same tenant, other organisation).
4. Run **layer-off** tests: with the EF filter disabled, RLS must still block; with RLS disabled, the filter must still block (TEN-030, TEN-031). These need real SQL Server. If no SQL Server connection is available (`ECP_TEST_SQL`), report them **NOT VERIFIED**; a provider-independent filter test may still run but must be labelled "not SQL Server proof".

## Checklist
- Reads, writes, deletes and foreign-key ids embedded in payloads across tenants (TEN-001/002); organisation isolation within a tenant (TEN-018).
- Documents: download, upload, storage keys, manifests, de-duplication only within a tenant (TEN-003/004/021/033).
- Jobs and outbox carry tenant and open an explicit scope; a job without scope fails closed (TEN-005).
- Caches always keyed by tenant (TEN-006); search, autocomplete and counts (TEN-007); reports and reporting views per tenant principal (TEN-008, SEC-040 to SEC-045).
- Exports, imports, migration references, packages, notifications, webhooks, audit queries, logs, support bundles, configuration (TEN-009 to TEN-016).
- Tenant resolution cannot be tampered with via header, claim, host, route or body (TEN-019); suspended tenants denied (TEN-020).
- Errors and timing reveal nothing about other tenants (TEN-017).
- `AgreementReference` uniqueness is `(TenantId, AgreementReference)` and an import never reveals that a colliding reference exists in another tenant.

## Severity
Any confirmed cross-tenant read or write is Critical. Missing layer or missing test on a tenant-owned surface is High.

## Output
Entity-by-surface matrix (Pass / Fail / NOT VERIFIED), findings with fixes and regression tests, and the SQL Server availability used for the run.
