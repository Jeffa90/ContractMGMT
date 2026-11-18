using Microsoft.EntityFrameworkCore.Migrations;

namespace Ecp.Persistence;

/// <summary>
/// Retired SQL Server Row-Level Security helpers (ADR-0003 superseded by ADR-0044 for the single-tenant IIS deployment).
/// <para>
/// Earlier migrations call these helpers to create a <c>security</c> schema, tenant predicate functions, one security policy per tenant table and an
/// application database role (<c>ecp_runtime</c>). They now emit nothing, so a fresh installation needs only db_ddladmin, db_datareader and db_datawriter
/// for its migration account. A database created before the change has those objects removed, where present, by the forward migration
/// <c>Audit/20261118000000_RemoveRowLevelSecurity</c>. The changed checksums of the historical migrations are recorded, with the owner decision, in
/// <c>upgrades/migration-reviews.json</c> (amendments).
/// </para>
/// <para>
/// Tenant isolation is unchanged in the application: TenantId on every owned entity, EF global query filters that fail closed, refusal of writes to
/// another tenant, composite foreign keys that include TenantId, and explicit TenantId predicates in every reviewed raw SQL statement.
/// </para>
/// </summary>
public static class RowLevelSecurity
{
    /// <summary>The database role earlier versions created. Named only so the removal migration, readiness and tests can find and remove it.</summary>
    public const string LegacyRuntimeRole = "ecp_runtime";

    /// <summary>Retired: created the security schema, predicate functions and the runtime role. Emits nothing.</summary>
    public static void CreateInfrastructure(this MigrationBuilder migrationBuilder) { _ = migrationBuilder; }

    /// <summary>Retired: dropped the predicate functions. Emits nothing (removal is the forward migration's job).</summary>
    public static void DropInfrastructure(this MigrationBuilder migrationBuilder) { _ = migrationBuilder; }

    /// <summary>Retired: created a tenant security policy. Emits nothing.</summary>
    public static void AddTenantPolicy(this MigrationBuilder migrationBuilder, string schema, string table, string tenantColumn = "TenantId", bool registry = false)
    { _ = (migrationBuilder, schema, table, tenantColumn, registry); }

    /// <summary>Retired: dropped a tenant security policy. Emits nothing.</summary>
    public static void DropTenantPolicy(this MigrationBuilder migrationBuilder, string schema, string table) { _ = (migrationBuilder, schema, table); }

    /// <summary>Retired: granted the runtime role data access on a module schema. Emits nothing; the runtime account holds db_datareader and db_datawriter.</summary>
    public static void GrantRuntimeAccess(this MigrationBuilder migrationBuilder, string schema) { _ = (migrationBuilder, schema); }

    /// <summary>
    /// Removes every object the retired helpers created, each only where it exists, in dependency order: security policies, predicate functions, the
    /// <c>security</c> schema (only when empty), then the members and the role itself (dropping the role also removes its grants and the DENY on the audit
    /// table). On a database that never had them nothing runs, so no permission beyond the migration account's db_ddladmin is needed.
    /// </summary>
    public const string RemovalSql = """
        DECLARE @sql nvarchar(max);
        DECLARE policies CURSOR LOCAL FAST_FORWARD FOR
            SELECT N'DROP SECURITY POLICY ' + QUOTENAME(SCHEMA_NAME(schema_id)) + N'.' + QUOTENAME(name) + N';'
            FROM sys.security_policies WHERE name LIKE N'TenantPolicy[_]%';
        OPEN policies; FETCH NEXT FROM policies INTO @sql;
        WHILE @@FETCH_STATUS = 0 BEGIN EXEC sys.sp_executesql @sql; FETCH NEXT FROM policies INTO @sql; END;
        CLOSE policies; DEALLOCATE policies;
        IF OBJECT_ID(N'security.fn_TenantRegistryPredicate', N'IF') IS NOT NULL EXEC(N'DROP FUNCTION security.fn_TenantRegistryPredicate;');
        IF OBJECT_ID(N'security.fn_TenantPredicate', N'IF') IS NOT NULL EXEC(N'DROP FUNCTION security.fn_TenantPredicate;');
        IF SCHEMA_ID(N'security') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.objects WHERE schema_id = SCHEMA_ID(N'security'))
            EXEC(N'DROP SCHEMA security;');
        IF DATABASE_PRINCIPAL_ID(N'ecp_runtime') IS NOT NULL
        BEGIN
            DECLARE members CURSOR LOCAL FAST_FORWARD FOR
                SELECT N'ALTER ROLE [ecp_runtime] DROP MEMBER ' + QUOTENAME(USER_NAME(member_principal_id)) + N';'
                FROM sys.database_role_members WHERE role_principal_id = DATABASE_PRINCIPAL_ID(N'ecp_runtime');
            OPEN members; FETCH NEXT FROM members INTO @sql;
            WHILE @@FETCH_STATUS = 0 BEGIN EXEC sys.sp_executesql @sql; FETCH NEXT FROM members INTO @sql; END;
            CLOSE members; DEALLOCATE members;
            EXEC(N'DROP ROLE [ecp_runtime];');
        END;
        """;
}
