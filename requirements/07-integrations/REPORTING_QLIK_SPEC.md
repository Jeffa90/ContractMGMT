# External Reporting and Qlik Specification

Confirmed: "Full analytics are externally accessible, including Qlik for the JBS pack." Master: Qlik is the external full-reporting destination. The danger is that direct database access bypasses application authorisation and tenant isolation (RISK-12). This file defines safe access (GAP-16). Open point: Q-10. `[OPEN]` detail.

## 1. Scope split

| In the platform | In the external tool (Qlik) |
|---|---|
| Operational lists, search, saved filters, per-record history, simple counts and due-date views, audit explorer, reconciliation reports | Full analytics, cross-period trending, spend and renewal pipelines, dashboards beyond operational needs |

## 2. Access modes (tenant selects one or more)

| Mode | Mechanism | Isolation |
|---|---|---|
| A. Curated SQL views | Read-only views in the `reporting` schema, exposed to a **per-tenant database principal** | Views filter by tenant using the principal's identity; Row-Level Security predicate is the enforcement, the view filter is belt-and-braces. No access to base tables. Azure and IIS only where the customer's network permits |
| B. REST export API | Paged, incremental JSON/CSV endpoints under `/api/v1/reporting/...` with OAuth client credentials | Same access evaluator as the API; classification clearance of the service principal applies |
| C. File drop | Scheduled CSV/Parquet export to an approved location | Tenant-prefixed path; files signed with a manifest hash |

Mode A is the Qlik default where allowed; Mode B is the default for Azure-hosted multi-tenant installations. A tenant with multiple organisations may restrict a principal to chosen organisations.

## 3. Datasets (stable, versioned column contracts)

`ContractFlat`, `SupplierFlat`, `CategoryFlat`, `CategoryAssignmentHistory`, `DocumentMetadata` (no content), `ApprovalFlat`, `VariationFlat`, `ReminderFlat`, `CustomFieldValueLong` (entity, field code, typed value columns) and `CustomFieldPivot` (generated per published release for fields flagged reportable), and an optional `AuditSummary`.

Rules:

1. Every dataset row carries `TenantId`, `OrganisationId`, `UpdatedUtc` and `RowVersion` to support incremental loads. Deletions and archives appear as tombstone rows, never as silent disappearance.
2. Sensitive and above-clearance data is omitted or masked according to the **principal's** clearance (`RETENTION_AND_CLASSIFICATION_SPEC.md`).
3. Column names are stable codes; terminology aliases are provided in a separate `FieldCatalogue` dataset (code, alias, type, currency code column).
4. Dataset schemas are versioned; breaking changes require a new dataset version and an upgrade note. Views are created by migrations, not by hand.
5. Projections are refreshed by a background job or maintained as indexed views; staleness is reported on the readiness dashboard (target latency is configuration; default 15 minutes) `[CONFIRMED]`.
6. Reporting reads never take locks that block operational writes (snapshot isolation or replica where available).

## 4. Security tests

Test SEC-040 to SEC-045 in `docs/TESTING.md`: a reporting principal for tenant A cannot read tenant B through views, API or file drop; cannot read base tables; cannot see records above clearance; cannot write; secret rotation revokes access.

## 5. Qlik integration guide (documentation deliverable)

Connection string patterns, incremental-load scripts keyed on `RowVersion`/`UpdatedUtc`, handling of tombstones, and a field catalogue load. Delivered under `docs/` in task DOCS-API.
