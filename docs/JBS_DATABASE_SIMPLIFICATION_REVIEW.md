# JBS database simplification review

Status: **Review only. Nothing in this document has been implemented.** Prepared 2026-11-18 on branch jbs/iis-integration at commit 4969a79 (CI run 118 green on SQL Server 2022 and 2025).

Question from IT: are the `ecp_runtime` database role, SQL Server Row-Level Security (RLS) and `sp_getapplock` leftovers from the multi-customer design that a JBS-only deployment does not need?

Short answer:

| Item | Recommendation | One-line reason |
|---|---|---|
| `ecp_runtime` role | **REMOVE** | Its only extra over db_datareader + db_datawriter is a DENY on audit UPDATE/DELETE, which the audit trigger already enforces for every login. CI proved db_datareader + db_datawriter run the application. |
| Row-Level Security | **REMOVE** (owner decision: amends ADR-0003) | It enforces only the *tenant* boundary. JBS has exactly one tenant, which cannot be widened to several tenants after setup. Organisation, business unit, category and classification access are enforced by the application, not by RLS. |
| `sp_getapplock` | **KEEP** | It stops concurrent saves from breaking or failing on the audit chain, and EF Core itself uses it for migration locking. It needs no special permission. |

---

## A. `ecp_runtime` database role

1. **Why it exists.** ADR-0003 and `R-DB-03`: give the runtime login data access to the application's schemas only, with no schema-owner rights, and make it the principal that RLS predicates and the audit DENY are written against.
2. **Every place it is used.**
   - `src/Ecp.Persistence/RowLevelSecurity.cs`: `CREATE ROLE`, `GRANT SELECT` on the two predicate functions, and `GrantRuntimeAccess` (`GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA`).
   - Migrations: 31 `GrantRuntimeAccess` calls in 31 migration files; `CreateInfrastructure` in `src/Modules/Ecp.Audit/Persistence/Migrations/20261016000541_Initial.cs`, which also does `DENY UPDATE, DELETE ON audit.AuditEvent TO ecp_runtime`.
   - Readiness: `src/Ecp.Persistence/ConnectionInspection.cs` (`IS_MEMBER('ecp_runtime')`) and `src/Modules/Ecp.Operations/DatabaseAccessReadinessCheck.cs` (R-DB-02 warns when the runtime login is not a member).
   - Tests: `SqlServerBehaviourTests.The_runtime_role_can_read_and_write_business_data_but_not_update_or_delete_audit`, `RuntimePrivilegeSqlServerTests`, `SqlPermissionProbeTests`.
3. **JBS requirement it satisfies.** None specific to JBS. The JBS requirement is "runtime account least privilege, not db_owner, no schema changes". db_datareader + db_datawriter in a database that holds only this application meets that equally well.
4. **Protection lost if removed.**
   - The runtime account would no longer be scoped to the application schemas. In a JBS-only database those are the only schemas, so nothing is lost.
   - The `DENY UPDATE, DELETE` on `audit.AuditEvent` would go. The append-only trigger `audit.TR_AuditEvent_AppendOnly` still blocks UPDATE and DELETE for every login, db_owner included (tested: `The_audit_table_is_append_only_even_for_a_privileged_login`, SQL 51000). db_datawriter cannot disable or alter that trigger, because that needs ALTER on the table.
5. **Equivalent protection already present?** Yes. The trigger protects the audit trail. Readiness already refuses or warns on a db_owner or sysadmin runtime login (R-DB-02), and `R-MIG-01` refuses automatic migration in Production.
6. **DBA and deployment complexity of keeping it.**
   - The migration identity needs `CREATE ROLE`.
   - The DBA must add the runtime account to an application-created role after the first migration.
   - The role is owned by the migration account, which must be moved first if that account is ever replaced.
   - It is a non-standard role for DBA audits.
7. **Impact of removal.**
   - New installs stop creating the role: `CreateInfrastructure` and `GrantRuntimeAccess` become no-ops.
   - A new forward migration drops the grants, the DENY and the role on existing databases.
   - R-DB-02 checks "not db_owner and not sysadmin" instead of role membership.
   - About 4 tests change.
8. **Decision: REMOVE.**
9. **Evidence.** CI runs 115 and 116 on SQL Server 2022 and 2025: the runtime account with db_datareader + db_datawriter provisioned a tenant, added reference data and created a draft contract (`SqlPermissionProbeTests`, "Runtime account, db_datareader + db_datawriter: SUCCEEDED"). The audit trigger test passes in every SQL Server run.

## B. Row-Level Security

1. **Why it exists.** ADR-0003 (status: Proposed) chose layered *tenant* isolation for three product modes, including multi-tenant hosting: EF query filter, RLS through `SESSION_CONTEXT`, composite foreign keys including `TenantId`, and tenant-prefixed storage keys. The aim is that one missed filter cannot expose another tenant's data.
2. **Every place it is used.**
   - **Migrations:** 86 `AddTenantPolicy` calls across 31 of 36 migration files, which leave **84 enabled policies** in an installed database; plus `CreateInfrastructure` (schema `security`, functions `fn_TenantPredicate` and `fn_TenantRegistryPredicate`).
   - **Runtime:** `src/Ecp.Persistence/ScopedConnection.cs` runs `sp_set_session_context` (TenantId, IsInstallationScope) on every connection or scope change, and `src/Ecp.Persistence/RowLevelSecurity.cs` holds the helpers.
   - **Recovery and upgrade:**
     - `src/Ecp.Persistence/MigrationInventory.cs` and `src/Modules/Ecp.Recovery/Schema.cs` list the enabled policies in backup manifests.
     - `src/Modules/Ecp.Recovery/Validation.cs`: VAL-007 fails restore validation when no policies exist; VAL-008 tests session-context isolation.
     - `src/Modules/Ecp.Recovery/Upgrade/Preflight.cs` maps these to UPF-053 and UPF-054.
   - **Tests:**
     - `SqlServerBehaviourTests`: every table has a policy; RLS shows only the current tenant's rows; RLS blocks cross-tenant writes; session context follows scope changes.
     - Policy-count assertions in `UpgradeSqlServerTests`, `SignatureVocabularyMigrationTests`, `BackupFailureSqlServerTests`, `QueryPlanReport` and `tests/Ecp.UnitTests/UpgradeTests.cs`.
     - `SqlPermissionProbeTests`.
3. **JBS requirement it satisfies.** None that the application does not already meet. JBS has **one tenant**:
   - Setup creates exactly one tenant (`SetupStages.cs`, `CreateTenantAsync`).
   - After setup, tenancy can only be widened from single organisation to several organisations *inside* that tenant (`OrganisationCreation.cs` → `WidenTenancyAsync(TenancyMode.MultiOrganisation)`); no path in Production creates a second tenant.
   - With one tenant there is no other tenant's data for RLS to hide.
   - RLS does **not** enforce organisation, business unit, division, site, category, ownership or classification. Those are enforced only in the application: `ContractAccessFilter`, `AccessEvaluator`, `SecuredContractService`, `LevelVisibility` and the Access module. They are unaffected by this review and are **not** to be removed.
4. **Protection lost if removed.**
   - RLS's second line of defence against cross-*tenant* reads and writes. That has no effect with one tenant.
   - The "fail closed with no scope set" behaviour at SQL level. The EF query filter already throws `TenantScopeMissingException` when no tenant is set, so that behaviour remains.
   - Raw-SQL paths (backup inventories, integrity checks) would no longer be scoped by SQL. They are fixed, reviewed and parameter-free (ADR-0034), and an architecture test bans unwrapped raw SQL and `IgnoreQueryFilters`.
5. **Equivalent protection already present?** For a one-tenant JBS database, yes:
   - EF global tenant filter on every `ITenantOwned` entity, plus refusal of writes to another tenant (`TenantDbContext`).
   - Composite foreign keys including `TenantId`. These are the "database constraints" the Master instructions require, and they stay.
   - Tenant-prefixed storage keys.
   - All organisation and classification access checks on the server.
6. **DBA and deployment complexity of keeping it.**
   - The migration identity needs `ALTER ANY SECURITY POLICY`, now and for any release that adds a table.
   - **RLS also filters the DBA.** A DBA querying `contracts.Contract` in SSMS, even as db_owner, sees **zero rows** unless they first call `sp_set_session_context`. The same applies to any future report or extract that reads the database directly. This is the main source of troubleshooting confusion.
   - 84 policies to explain at DBA review and in restore validation.
   - The schema-ownership dependency (predicate functions and tables must keep the same owner).
   - One extra `sp_set_session_context` round trip per connection or scope change.
7. **Impact of removal.** See section E.
8. **Decision: REMOVE** for this deployment. It is an owner decision, because it amends ADR-0003 and the isolation sections of `docs/SECURITY.md` and `docs/THREAT_MODEL.md` (T-002).
   - Master requirement D1 ("TenantId on owned entities, query filters, service policies, database constraints and cross-tenant tests") is still met: composite foreign keys are the database constraint, and the cross-tenant tests stay at the EF layer.
   - CONFIRMED_DECISIONS D4 says SQL Server tests "cover ... Row-Level Security"; that clause becomes not applicable.
   - Consequence to record: the product's **multi-tenant hosting mode** would then rely on application-layer isolation only, and should be marked unsupported or Future until RLS is reinstated for such a hosting option. JBS does not use that mode.
9. **Evidence.**
   - ADR-0003 states RLS's purpose as cross-tenant protection only.
   - The predicate functions in `RowLevelSecurity.cs` test only `TenantId` and `IsInstallationScope`.
   - No policy references organisation, category or classification.
   - The tenancy widening path stops at several organisations in one tenant.
   - Probe (CI runs 115 and 116): RLS is the only reason the migration identity needs `ALTER ANY SECURITY POLICY` (SQL 262 without it).

**Removing RLS eliminates:** `ALTER ANY SECURITY POLICY`; all 84 security policies and the `security` schema with its two functions; the `SESSION_CONTEXT` plumbing in `ScopedConnection` and VAL-008; and the "DBA sees no rows" troubleshooting problem. Together with removing `ecp_runtime`, it also eliminates `CREATE ROLE`.

## C. `sp_getapplock`

Assessed separately from RLS, as asked.

1. **Why it exists.** The audit trail is a per-tenant hash chain: each event stores its sequence number and the previous event's hash.
2. **Every use.**
   - **Application, one place:** `src/Modules/Ecp.Audit/AuditApi.cs`, `AuditFlusher.BeforeCommitAsync`. It takes an exclusive lock on `audit:{tenantId}` that lasts for the transaction, with a 15-second timeout, before reading the last sequence and appending.
   - **Framework:** EF Core 10's SQL Server migrator itself runs `sp_getapplock @Resource = '__EFMigrationsLock'` during `Migrate` (verified in `Microsoft.EntityFrameworkCore.SqlServer.dll` 10.0.12). That use cannot be removed without abandoning EF migrations.
   - **Not used** for background-job exclusivity, backup, restore, validation, upgrade orchestration or notification processing. Those use the SQL-backed operation leases in `ops.OperationLease` (`src/Modules/Ecp.Operations/Leases.cs`, ADR-0034 §9): a unique key plus optimistic concurrency.
3. **JBS requirement it satisfies.** A tamper-evident, gap-free audit trail, plus correct behaviour when several users save at the same time. On one IIS server with one JBS tenant, *every* change contends for the same chain, so this matters more for JBS, not less.
4. **Protection lost if removed.** Two concurrent saves would read the same last sequence. The unique index `(TenantId, Sequence)` would then reject one of them, so the user's change fails and rolls back. Without that index the chain would fork. Either outcome is unacceptable.
5. **Equivalent protection present?** No. Operation leases cover long-running operations, not individual request transactions. Row versions protect each record, not the shared chain.
6. **DBA complexity.** Close to none. `sp_getapplock` needs only membership of `public`; no grant is required. The lock lasts for the transaction and SQL Server releases it automatically.
7. **Impact of removal.** Not proposed.
8. **Decision: KEEP.**
9. **Evidence.**
   - `SqlServerBehaviourTests.Concurrent_writers_in_one_tenant_keep_the_audit_chain_gap_free` passes on SQL Server 2022 and 2025 in every CI run.
   - The unique index on `(TenantId, Sequence)` is defined in `AuditDbContext`.
   - The EF Core string was confirmed in the shipped assembly.

## D. Simplified JBS SQL model (after A and B)

| Identity | Rights | Notes |
|---|---|---|
| Migration: the `iis-qa` runner's service account | **db_ddladmin + db_datareader + db_datawriter**, the grant the DBA has already made | No `CREATE ROLE`, no `ALTER ANY SECURITY POLICY`, no db_owner. **Expected, not yet proven:** the first install was last probed only up to the first refusal (CREATE ROLE). The change must include a CI test, as for the current minimum set, proving a full first install and an upgrade on SQL Server 2022 and 2025 with exactly these three roles. Under the current design the probe already proved db_ddladmin + db_datareader + db_datawriter is refused (runs 115/116), so the simplification is what makes the DBA's current grant sufficient. |
| Runtime: `AMH\svc_jbsai_qa` | **db_datareader + db_datawriter** | Proven sufficient (CI runs 115 and 116). Never db_owner, never the migration account. Audit stays append-only through the trigger. |

`sp_getapplock` needs no grant for either identity.

## E. Impact of removing `ecp_runtime` and RLS

| Area | Impact |
|---|---|
| Existing migrations | 31 of 36 migration files call the RLS or role helpers. **Not edited one by one.** The helpers `CreateInfrastructure`, `AddTenantPolicy` and `GrantRuntimeAccess` become no-ops, so a fresh install creates none of it. The Audit `Initial` migration's `DENY ... TO ecp_runtime` line is removed. |
| New migration | One forward migration, for example `RemoveRowLevelSecurity` (Audit module, applied last). It drops every `TenantPolicy_*` policy that exists, the two predicate functions, the `security` schema, the role grants and the DENY, then the `ecp_runtime` role after removing its members. Every statement is guarded by an existence check, so on a fresh JBS database it does nothing and needs no extra permission (to be proven by test). |
| Policies removed | 84, on databases that have them. A fresh JBS database never has them. |
| Code files (outside migrations) | About 8: `RowLevelSecurity.cs`, `ScopedConnection.cs`, `ConnectionInspection.cs`, `MigrationInventory.cs`, `src/Modules/Ecp.Operations/DatabaseAccessReadinessCheck.cs`, `src/Modules/Ecp.Recovery/Validation.cs` (VAL-007 and VAL-008 become "no RLS expected: Passed if none"), `src/Modules/Ecp.Recovery/Schema.cs`, `src/Modules/Ecp.Recovery/Upgrade/Preflight.cs`. Unchanged: EF filters, `IsInstallationScope`, composite foreign keys, all access-control code, the audit trigger, `sp_getapplock`. |
| Tests | About 9 files: `SqlServerBehaviourTests` (4 RLS tests and 1 role test replaced by "no policies, no role; runtime with db_datareader + db_datawriter cannot change audit"), `RuntimePrivilegeSqlServerTests`, `SqlPermissionProbeTests` (the new minimum becomes ddladmin + r + w), `UpgradeSqlServerTests`, `SignatureVocabularyMigrationTests`, `BackupFailureSqlServerTests`, `QueryPlanReport`, `tests/Ecp.UnitTests/UpgradeTests.cs`, plus the EF-layer cross-tenant tests, which stay as they are. |
| Documentation | ADR-0003 amendment (owner), `docs/SECURITY.md`, `docs/THREAT_MODEL.md` T-002, `docs/ARCHITECTURE.md` §6, `docs/DEPLOYMENT.md`, the JBS status documents. Multi-tenant hosting mode marked Future or unsupported. |
| Upgrade impact | The N-1 to N rehearsal (frozen 1.0.0 schema) must end with 0 policies and no role. Backups taken before the change list policies in their manifests; restoring one and upgrading removes them. |
| Existing-data impact | **None.** Policies, functions and the role are metadata. No row, column, `TenantId`, index or foreign key changes. No JBS database exists yet. |
| Rollback approach | Before the first QA install: revert the commit; the QA database is new and holds no data. After an install: restore the pre-deploy copy-only backup (the ADR-0034 path the upgrade already uses), or apply the forward migration's `Down`, which recreates role, functions and policies through the same helpers. That `Down` would need the old permissions again. |
| Effort | One focused change and one CI cycle on SQL Server 2022 and 2025. No new product functionality. |

## Timing

**Recommended: make the change BEFORE the first QA deployment**, on condition that the owner approves the ADR-0003 amendment now.
- No JBS database exists yet, so there is no data, upgrade or rollback risk for JBS.
- The DBA's **current** grant (db_ddladmin + db_datareader + db_datawriter) becomes sufficient, so the open DBA request for `CREATE ROLE` and `ALTER ANY SECURITY POLICY` disappears.
- QA then tests the design that will go to Production, not one about to be removed.

**If the owner does not want to amend ADR-0003 now:** QA can proceed with the current design without risk. It is fully proven on SQL Server 2022 and 2025 (runs 115, 116 and 118). The simplification would then be a later forward migration, which needs the two extra permissions to remain until it has run.

---

## Recommendation for IT and the DBA

**KEEP:**
- `sp_getapplock`. It serialises the audit chain and EF Core uses it for migration locking; no permission is needed.
- The audit append-only trigger, the `TenantId` composite foreign keys, and all application-level access control (organisation, business unit, category, ownership, classification).

**REMOVE:**
- The `ecp_runtime` database role, with its grants and DENY.
- SQL Server Row-Level Security: 84 security policies, the `security` schema and functions, and `SESSION_CONTEXT`.

**SIMPLIFY:**
- Runtime account to standard database roles.
- Migration account to the roles already granted.

**Required SQL permissions after simplification** (to be proven by CI before QA):
- Migration (runner service account): `db_ddladmin`, `db_datareader`, `db_datawriter`. No `CREATE ROLE`, no `ALTER ANY SECURITY POLICY`, no db_owner.
- Runtime (`AMH\svc_jbsai_qa`): `db_datareader`, `db_datawriter`. Never db_owner.

**Timing:** change before the first QA deployment if the owner approves the ADR-0003 amendment now. Otherwise QA proceeds with the current, proven design (migration account additionally `CREATE ROLE` + `ALTER ANY SECURITY POLICY`), and the simplification follows as an upgrade.
